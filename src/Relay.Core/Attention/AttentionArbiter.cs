using Relay.Core.Ids;
using Relay.Core.Preferences;
using Relay.Core.Tasks;

namespace Relay.Core.Attention;

/// <summary>One thing the UI may show. Items with the same key merge: the card refreshes in place and counts occurrences.</summary>
public sealed record AttentionItem(
    string ItemId,
    Presentation Level,
    string Title,
    string Detail,
    string Key,
    string? TaskId,
    TaskOrigin Origin,
    TaskKind Kind,
    DateTimeOffset FirstAt,
    DateTimeOffset LastAt,
    int Occurrences,
    IReadOnlyList<string> TaskIds,
    bool Pinned,
    string Reason)
{
    public bool NeedsAction => Level == Presentation.Proposal;
}

/// <summary>What the arbiter was told about a finished task; everything it needs and nothing it should not see.</summary>
public sealed record AttentionInput(
    string TaskId,
    TaskOrigin Origin,
    TaskKind Kind,
    string Title,
    string Detail,
    string? MergeKey,
    Presentation? Suggested,
    double Confidence,
    bool HasAnswer,
    bool? Consistent,
    int SourcesCited,
    int PendingProposals,
    int ExecutedOperations,
    bool Failed,
    string? WatchedTerm,
    DateTimeOffset At,
    /// <summary>Proposals the user rejected or edited: the card was seen and answered, so the finding must not come back as an alert.</summary>
    int RejectedProposals = 0);

public sealed record AttentionDecision(Presentation Level, string Reason, AttentionItem? Item, bool Merged);

/// <summary>
/// Decides how much attention a finished task gets. Origin sets the floor (a direct ask is always
/// answered); evidence sets the ceiling (an alert needs two sources and confidence); preferences set
/// the budgets and cool-downs; merge keys stop the same finding from carding twice. The model's
/// suggested presentation is one input among these, never the decision.
/// </summary>
public sealed class AttentionArbiter
{
    public const double AlertConfidence = 0.6;
    public const int AlertSources = 2;

    private readonly Func<CompiledPreferences> _preferences;
    private readonly List<AttentionItem> _items = new();
    private readonly List<(DateTimeOffset At, Presentation Level)> _shown = new();
    private readonly Dictionary<string, (DateTimeOffset At, Presentation Level)> _dismissed = new(StringComparer.Ordinal);

    public AttentionArbiter(Func<CompiledPreferences> preferences) => _preferences = preferences;

    public IReadOnlyList<AttentionItem> Items => _items.OrderByDescending(i => i.Pinned).ThenByDescending(i => i.Level).ThenByDescending(i => i.LastAt).ToList();

    /// <summary>The level alone, for a task that is already on screen (the foreground task in Response); no card is made.</summary>
    public (Presentation Level, string Reason) RankOnly(AttentionInput input) => Rank(input, _preferences());

    public AttentionDecision Decide(AttentionInput input)
    {
        var prefs = _preferences();
        var (level, reason) = Rank(input, prefs);
        if (level == Presentation.None)
        {
            return new AttentionDecision(level, reason, null, false);
        }

        var key = input.MergeKey ?? $"{input.Kind.Wire()}:{input.TaskId}";
        var pinned = input.WatchedTerm is not null && input.Kind == TaskKind.Resolve;
        var existing = _items.FirstOrDefault(i => i.Key == key);

        // A dismissed key comes back only when it escalates.
        if (existing is null && _dismissed.TryGetValue(key, out var dismissed) && dismissed.Level >= level && input.At - dismissed.At < prefs.Cooldown)
        {
            return new AttentionDecision(Presentation.None, $"dismissed {(int)(input.At - dismissed.At).TotalSeconds}s ago at the same level; not re-shown", null, false);
        }

        if (existing is not null && (input.At - existing.LastAt < prefs.Cooldown || pinned))
        {
            var merged = existing with
            {
                Level = level > existing.Level ? level : existing.Level,
                Title = input.Title,
                Detail = input.Detail,
                LastAt = input.At,
                Occurrences = existing.Occurrences + 1,
                TaskIds = [.. existing.TaskIds, input.TaskId],
                TaskId = input.TaskId,
                Reason = reason + " · merged with earlier card",
            };
            _items[_items.IndexOf(existing)] = merged;
            return new AttentionDecision(merged.Level, merged.Reason, merged, true);
        }

        // Budgets: alerts per 10 minutes, results per 5 minutes; beyond them the card is downgraded, never dropped silently.
        if (level == Presentation.Alert && _shown.Count(s => s.Level == Presentation.Alert && input.At - s.At < TimeSpan.FromMinutes(10)) >= prefs.MaxAlertsPer10Minutes)
        {
            level = Presentation.Result;
            reason += $" · alert budget ({prefs.MaxAlertsPer10Minutes}/10 min) reached, shown as result";
        }
        if (level == Presentation.Result && !pinned && _shown.Count(s => s.Level == Presentation.Result && input.At - s.At < TimeSpan.FromMinutes(5)) >= prefs.MaxResultsPer5Minutes)
        {
            level = Presentation.Ambient;
            reason += $" · result budget ({prefs.MaxResultsPer5Minutes}/5 min) reached, shown as ambient";
        }

        var item = new AttentionItem(Ulid.NewUlid(input.At), level, input.Title, input.Detail, key, input.TaskId, input.Origin, input.Kind, input.At, input.At, 1, [input.TaskId], pinned, reason);
        if (existing is not null) _items.Remove(existing);
        _items.Add(item);
        _shown.Add((input.At, level));
        _shown.RemoveAll(s => input.At - s.At > TimeSpan.FromMinutes(30));
        return new AttentionDecision(level, reason, item, false);
    }

    private static (Presentation Level, string Reason) Rank(AttentionInput x, CompiledPreferences prefs)
    {
        if (x.Failed) return (x.Origin == TaskOrigin.Direct ? Presentation.Result : Presentation.Ambient, x.Origin == TaskOrigin.Direct ? "a direct task failed; the failure is shown" : "an observed task failed; noted quietly");
        if (x.PendingProposals > 0) return (Presentation.Proposal, $"{x.PendingProposals} proposal(s) await approval");

        if (x.Origin == TaskOrigin.Direct)
        {
            if (x.Kind == TaskKind.Research && x.HasAnswer) return (Presentation.Findings, "direct research task with findings");
            return (Presentation.Result, "a direct ask is always answered");
        }

        if (x.Kind == TaskKind.Remember) return (Presentation.Ambient, x.ExecutedOperations > 0 ? "note filed; subtle indicator only" : "note kept in the inbox; subtle indicator only");

        // Something ran (approved by the user or covered by a standing grant), or the user declined the proposal card:
        // a subtle confirmation at most, never a second alert about a finding that was already handled.
        if (x.ExecutedOperations > 0) return (Presentation.Ambient, $"{x.ExecutedOperations} operation(s) ran (approved or granted); subtle confirmation only");
        if (x.RejectedProposals > 0) return (Presentation.None, "the proposal was declined; nothing more to show");

        switch (x.Kind)
        {
            case TaskKind.Check:
                if (x.Consistent == true) return (Presentation.None, "observed statement agrees with stored facts; nothing to show");
                if (x.Consistent == false)
                {
                    if (x.SourcesCited >= AlertSources && x.Confidence >= AlertConfidence) return (Presentation.Alert, $"conflict with {x.SourcesCited} sources at confidence {x.Confidence:0.00}");
                    return (Presentation.Result, $"possible conflict but evidence is thin ({x.SourcesCited} source(s), confidence {x.Confidence:0.00}); shown as result, not alert");
                }
                return (Presentation.None, "check could not be decided; recorded in diagnostics only");
            case TaskKind.Resolve:
                if (x.WatchedTerm is not null) return (Presentation.Result, $"'{x.WatchedTerm}' is a watched term; pinned definition refreshed");
                return (x.HasAnswer ? Presentation.Result : Presentation.None, x.HasAnswer ? "definition found" : "no definition found; nothing to show");
            case TaskKind.Research:
                return (x.HasAnswer ? Presentation.Findings : Presentation.None, x.HasAnswer ? "findings ready" : "nothing learned");
            case TaskKind.Organize:
            case TaskKind.Improve:
                return (Presentation.None, "nothing to propose");
            case TaskKind.Answer:
                return (x.HasAnswer ? Presentation.Result : Presentation.None, x.HasAnswer ? "answer to something overheard" : "no answer");
            default:
                return (x.Suggested ?? Presentation.None, "default");
        }
    }

    public bool Dismiss(string itemId, DateTimeOffset now)
    {
        var item = _items.FirstOrDefault(i => i.ItemId == itemId);
        if (item is null) return false;
        _items.Remove(item);
        _dismissed[item.Key] = (now, item.Level);
        return true;
    }

    /// <summary>Removes a proposal card once its task is no longer waiting (approved, rejected, cancelled).</summary>
    public void Resolve(string taskId, Presentation? downgradeTo, string title, string detail, DateTimeOffset now)
    {
        var item = _items.FirstOrDefault(i => i.TaskId == taskId && i.Level == Presentation.Proposal);
        if (item is null) return;
        if (downgradeTo is null or Presentation.None) { _items.Remove(item); return; }
        _items[_items.IndexOf(item)] = item with { Level = downgradeTo.Value, Title = title, Detail = detail, LastAt = now, Reason = "proposal resolved" };
    }

    public void Clear() => _items.Clear();
}
