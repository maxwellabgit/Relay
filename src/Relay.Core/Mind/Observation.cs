using System.Globalization;
using System.Text;

namespace Relay.Core.Mind;

/// <summary>
/// One thing the loop saw. The transcript of a task is a list of these: what the user said or what
/// was overheard, each move the mind made, and what each move caused (a tool result, a policy
/// decision, an approval, an execution result, a delegate's partial reply, a build stage, a user's
/// reply, a system notice). The mind is shown the whole transcript on every step, rendered by
/// <see cref="Render"/>, so it never has to guess what its last move did.
/// </summary>
public abstract record Observation(DateTimeOffset At)
{
    /// <summary>Stable wire name for records and tests.</summary>
    public abstract string Kind { get; }

    /// <summary>The compact text the mind reads. Long payloads are clipped; ids are kept whole.</summary>
    public abstract string Render();

    public const int MaxDataChars = 6_000;

    protected static string Clip(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    protected static string Map(IReadOnlyDictionary<string, string> map, int maxValueChars = 160)
    {
        if (map.Count == 0) return "{}";
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (k, v) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(k).Append('=').Append(Clip(v, maxValueChars));
        }
        return sb.Append('}').ToString();
    }

    protected static string Num(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>What started the task: a typed ask, an overheard window, or a follow-up handed down by the engine.</summary>
public sealed record InputObserved(DateTimeOffset At, string Source, string Text, string? ExcerptId = null, double? Seconds = null) : Observation(At)
{
    public const string Ask = "ask";
    public const string Heard = "heard";
    public const string FollowUp = "follow_up";

    public override string Kind => "input";

    public override string Render()
    {
        var head = Source switch
        {
            Heard => "heard" + (ExcerptId is null ? "" : $" (excerpt {ExcerptId}" + (Seconds is { } s ? $", {Num(s)}s" : "") + ")"),
            FollowUp => "follow-up",
            _ => "ask",
        };
        return $"{head}: \"{Clip(Text, MaxDataChars)}\"";
    }
}

/// <summary>The mind's own step, kept in the transcript so it sees what it did and what it told the user.</summary>
public sealed record MoveObserved(DateTimeOffset At, Move Move, string Feed) : Observation(At)
{
    public override string Kind => "move";
    public override string Render() => $"you → {Move.Brief()}" + (string.IsNullOrWhiteSpace(Feed) ? "" : $" · feed \"{Clip(Feed, 200)}\"");
}

/// <summary>A read-only tool returned. <see cref="Ids"/> are the ids the result contained; only these may be cited or referenced.</summary>
public sealed record ToolObserved(DateTimeOffset At, string Tool, IReadOnlyDictionary<string, string> Args, bool Ok, string Summary, string? Data, IReadOnlyList<string> Ids) : Observation(At)
{
    public override string Kind => "tool";

    public override string Render()
    {
        var sb = new StringBuilder();
        sb.Append("tool ").Append(Tool).Append(' ').Append(Map(Args)).Append(" → ").Append(Ok ? "ok" : "error").Append(" · ").Append(Clip(Summary, 300));
        if (!string.IsNullOrEmpty(Data)) sb.Append('\n').Append(Clip(Data, MaxDataChars));
        return sb.ToString();
    }
}

/// <summary>Policy decided a proposal the mind made: allowed (it ran or will run now), needs_approval (the user is being asked), or denied (with reasons).</summary>
public sealed record PolicyObserved(DateTimeOffset At, string ProposalId, string Action, string Outcome, IReadOnlyList<string> Reasons) : Observation(At)
{
    public const string Allowed = "allowed";
    public const string NeedsApproval = "needs_approval";
    public const string Denied = "denied";

    public override string Kind => "policy";

    public override string Render()
    {
        var text = $"policy: {Action} (proposal {ProposalId}) → {Outcome}";
        return Reasons.Count == 0 ? text : text + " · " + Clip(string.Join("; ", Reasons), 600);
    }
}

/// <summary>The user answered an approval request.</summary>
public sealed record ApprovalObserved(DateTimeOffset At, string ProposalId, string Action, bool Granted, string? Reason = null) : Observation(At)
{
    public override string Kind => "approval";
    public override string Render() => $"user {(Granted ? "approved" : "rejected")} {Action} (proposal {ProposalId})" + (string.IsNullOrWhiteSpace(Reason) ? "" : $": {Clip(Reason, 300)}");
}

/// <summary>An approved or allowed operation ran. <see cref="Outputs"/> carries ids the mind may use next (an artifact, a note, a project).</summary>
public sealed record ExecutionObserved(DateTimeOffset At, string ProposalId, string Action, bool Ok, string Summary, IReadOnlyDictionary<string, string> Outputs) : Observation(At)
{
    public override string Kind => "executed";
    public override string Render() => $"executed {Action} (proposal {ProposalId}) → {(Ok ? "ok" : "failed")} · {Clip(Summary, 400)}" + (Outputs.Count == 0 ? "" : " · " + Map(Outputs, 400));
}

/// <summary>
/// A delegate (another AI) is at work. <c>partial</c> observations carry how much has streamed so far and the
/// tail of it, so the mind can narrate or stop; <c>returned</c> carries the artifact id and the head of the reply.
/// </summary>
public sealed record DelegateObserved(DateTimeOffset At, string RequestId, string Profile, string Stage, int Chars, string? Text, string? ArtifactId = null) : Observation(At)
{
    public const string Started = "started";
    public const string Partial = "partial";
    public const string Returned = "returned";
    public const string Failed = "failed";
    public const string Stopped = "stopped";

    public override string Kind => "delegate";

    public override string Render()
    {
        var sb = new StringBuilder();
        sb.Append("delegate ").Append(Profile).Append(" (request ").Append(RequestId).Append(") ").Append(Stage);
        if (Stage is Partial or Returned) sb.Append(" · ").Append(Chars).Append(" chars");
        if (ArtifactId is not null) sb.Append(" · artifact ").Append(ArtifactId);
        if (!string.IsNullOrEmpty(Text)) sb.Append(Stage == Partial ? " · tail: \"" : " · \"").Append(Clip(Text, Stage == Partial ? 400 : 2_000)).Append('"');
        return sb.ToString();
    }
}

/// <summary>A tool build moved through a stage: drafted, tested, promoted, failed, or unavailable in this build.</summary>
public sealed record BuildObserved(DateTimeOffset At, string Tool, string Stage, string Detail) : Observation(At)
{
    public const string Drafted = "drafted";
    public const string Tested = "tested";
    public const string Promoted = "promoted";
    public const string Failed = "failed";
    public const string Unavailable = "unavailable";

    public override string Kind => "build";
    public override string Render() => $"build {Tool} → {Stage}" + (string.IsNullOrWhiteSpace(Detail) ? "" : $" · {Clip(Detail, 600)}");
}

/// <summary>The user spoke to the task directly: a reply to a question, an override of a route, a dismissal, a stop.</summary>
public sealed record UserObserved(DateTimeOffset At, string What, string Text) : Observation(At)
{
    public const string Reply = "reply";
    public const string Override = "override";
    public const string Dismiss = "dismiss";
    public const string Stop = "stop";

    public override string Kind => "user";
    public override string Render() => $"user {What}: \"{Clip(Text, 1_000)}\"";
}

/// <summary>The engine speaking: a budget reached, a contract problem in the last reply, a route hint, a move that is unavailable.</summary>
public sealed record SystemObserved(DateTimeOffset At, string Text) : Observation(At)
{
    public override string Kind => "system";
    public override string Render() => "system: " + Clip(Text, 1_000);
}
