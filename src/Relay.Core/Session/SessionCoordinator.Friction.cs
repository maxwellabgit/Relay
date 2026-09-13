using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Core.Usage;

namespace Relay.Core.Session;

/// <summary>
/// Idle / session-end friction review (Alpha Step 6 / G7): read recent usage lines, and when the same
/// friction repeats, open one Direct Improve task with a Tier B proposal that still needs approval.
/// At most once per session and once per UTC day.
/// </summary>
public sealed partial class SessionCoordinator
{
    private static readonly TimeSpan FrictionIdleDebounce = TimeSpan.FromSeconds(2);

    private bool _frictionReviewedThisSession;
    private DateOnly? _frictionReviewedDay;
    private IDisposable? _frictionIdleTimer;

    /// <summary>
    /// After a task settles, debounce a review while Ready with no live work and no capture.
    /// Shutdown calls <see cref="TryReviewFriction"/> directly.
    /// </summary>
    private void ScheduleFrictionReview()
    {
        if (_services.Usage is null || _frictionReviewedThisSession || _shutDown) return;
        _frictionIdleTimer?.Dispose();
        _frictionIdleTimer = _scheduler.Schedule(FrictionIdleDebounce, () => TryReviewFriction("idle"));
    }

    /// <summary>
    /// Reads recent usage and, when a repeated friction pattern is clear, creates one Improve task
    /// awaiting approval. Returns true when a review ran (whether or not it proposed). Safe to call
    /// again: a second call in the same session or on the same UTC day is a no-op.
    /// </summary>
    public bool TryReviewFriction(string trigger = "idle")
    {
        if (_services.Usage is null) return false;
        if (_frictionReviewedThisSession) return false;
        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        if (_frictionReviewedDay == today) return false;
        if (HasFrictionMarker(today)) { _frictionReviewedDay = today; _frictionReviewedThisSession = true; return false; }

        var shuttingDown = trigger == "shutdown" || _shutDown;
        if (!shuttingDown)
        {
            if (_state != RelayState.Ready || _capture != CapturePhase.None || _tasks.Any(t => t.IsLive)) return false;
        }

        // A prior friction improve still waiting is enough — do not stack another.
        if (_tasks.Any(t => t.Lane == "friction" && t.IsLive))
        {
            RecordFrictionReview(today, trigger, proposed: false, deferred: false, pattern: null, action: null, examples: 0, taskId: null, exampleIds: null);
            return true;
        }

        var suggestion = FrictionReview.Suggest(ReadRecentUsage());
        if (suggestion is null)
        {
            RecordFrictionReview(today, trigger, proposed: false, deferred: false, pattern: null, action: null, examples: 0, taskId: null, exampleIds: null);
            return true;
        }

        // Shutdown cancels live tasks; only record the finding so a later idle can propose (no day marker).
        if (shuttingDown)
        {
            Append(EventTypes.FrictionReviewed, new
            {
                trigger,
                proposed = true,
                deferred = true,
                pattern = suggestion.Pattern,
                action = suggestion.Action,
                examples = suggestion.ExampleTaskIds.Count,
                exampleTaskIds = suggestion.ExampleTaskIds.Take(8).ToList(),
            });
            _frictionReviewedThisSession = true;
            return true;
        }

        // Source event first so the proposal cites a real ledger id.
        var reviewed = Append(EventTypes.FrictionReviewed, new
        {
            trigger,
            proposed = true,
            deferred = false,
            pattern = suggestion.Pattern,
            action = suggestion.Action,
            examples = suggestion.ExampleTaskIds.Count,
            exampleTaskIds = suggestion.ExampleTaskIds.Take(8).ToList(),
        });
        if (reviewed is null) return false;

        var taskId = ProposeFrictionImprovement(suggestion, reviewed.Id);
        WriteFrictionMarker(today);
        _frictionReviewedDay = today;
        _frictionReviewedThisSession = true;
        return taskId is not null;
    }

    private void RecordFrictionReview(DateOnly day, string trigger, bool proposed, bool deferred, string? pattern, string? action, int examples, string? taskId, IReadOnlyList<string>? exampleIds)
    {
        Append(EventTypes.FrictionReviewed, new
        {
            trigger,
            proposed,
            deferred,
            pattern,
            action,
            examples,
            taskId,
            exampleTaskIds = exampleIds,
        });
        WriteFrictionMarker(day);
        _frictionReviewedDay = day;
        _frictionReviewedThisSession = true;
    }

    private string? ProposeFrictionImprovement(FrictionSuggestion suggestion, string sourceEventId)
    {
        var title = "Improvement from repeated friction";
        var task = NewTask(TaskOrigin.Direct, TaskKind.Improve, "friction", "", sourceEventId, suggestion.Reason,
            foreground: false, title: title, why: suggestion.Pattern);
        if (Append(EventTypes.TaskCreated, TaskCreatedPayload(task, chars: suggestion.Reason.Length)) is null)
        {
            _tasks.Remove(task);
            return null;
        }

        var proposal = new Proposal(
            Ulid.NewUlid(_clock.UtcNow),
            suggestion.Action,
            suggestion.Reason,
            new Dictionary<string, string>(suggestion.Target, StringComparer.Ordinal),
            [sourceEventId],
            ["Applies only after you approve; revertible as a change set."],
            Risks.ControlledWrite,
            true,
            Producers.Engine);

        task.Plan = new TurnPlan(
            true,
            title,
            [$"Friction pattern: {suggestion.Pattern}", $"Seen in {suggestion.ExampleTaskIds.Count} recent task(s)"],
            null,
            [],
            [proposal],
            Producers.Engine);

        ReceiveProposal(task, proposal);
        PersistTask(task);
        AdvanceTask(task);
        Notify();
        return task.TaskId;
    }

    private IReadOnlyList<UsageLine> ReadRecentUsage()
    {
        var usage = _services.Usage!;
        var now = _clock.UtcNow;
        // Today and yesterday cover overnight sessions without scanning the whole usage tree.
        var lines = new List<UsageLine>();
        lines.AddRange(usage.Read(now));
        lines.AddRange(usage.Read(now.AddDays(-1)));
        return lines;
    }

    private string FrictionMarkerPath(DateOnly day)
        => Path.Combine(_root.UsageDirectory, "friction-reviewed-" + day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    private bool HasFrictionMarker(DateOnly day) => File.Exists(FrictionMarkerPath(day));

    private void WriteFrictionMarker(DateOnly day)
    {
        try
        {
            Directory.CreateDirectory(_root.UsageDirectory);
            File.WriteAllText(FrictionMarkerPath(day), _ledger.SessionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* review still ran; marker is best-effort */ }
    }
}
