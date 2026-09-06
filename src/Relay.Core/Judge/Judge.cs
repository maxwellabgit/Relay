using System.Security.Cryptography;
using System.Text;
using Relay.Core.Tasks;

namespace Relay.Core.Judge;

/// <summary>One timestamped piece of an enabled stream. Text lives in the rolling buffer and in selected excerpts only.</summary>
public sealed record StreamSegment(string SegmentId, DateTimeOffset At, string Text, string? Speaker = null)
{
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
}

/// <summary>What the judge may know about the session besides the words: names it can route to, terms it should watch for, what was recently discussed.</summary>
public sealed record JudgeContext(
    IReadOnlyList<string> ActiveProjects,
    IReadOnlyList<string> WatchedTerms,
    IReadOnlyList<string> RecentTopics,
    string? PreferenceFragment)
{
    public static readonly JudgeContext Empty = new([], [], [], null);
}

/// <summary>
/// One judgment request. For observed input the window is the rolling buffer and <see cref="NewSegmentIds"/>
/// names what arrived since the last check; for a direct ask <see cref="DirectText"/> is the instruction.
/// </summary>
public sealed record JudgeRequest(
    TaskOrigin Origin,
    IReadOnlyList<StreamSegment> Window,
    IReadOnlyList<string> NewSegmentIds,
    string? DirectText,
    JudgeContext Context,
    DateTimeOffset At);

/// <summary>
/// One thing the judge found significant. <see cref="FocusedPrompt"/> is what the planner is asked;
/// <see cref="SegmentIds"/> are the only segments retained for it. <see cref="NoteText"/> is set for
/// remember findings and is the note as it should be filed (the judge's concise restatement).
/// </summary>
public sealed record JudgeFinding(
    TaskKind Kind,
    double Confidence,
    string Summary,
    string Why,
    string FocusedPrompt,
    IReadOnlyList<string> SegmentIds,
    string? Topic = null,
    string? ProjectHint = null,
    Presentation? Presentation = null,
    string? NoteText = null,
    string? NoteType = null,
    string? MergeKey = null);

public sealed record JudgeDecision(
    IReadOnlyList<JudgeFinding> Findings,
    string Producer,
    int PromptTokens,
    int CompletionTokens,
    long ElapsedMs,
    string? Error = null,
    string? Raw = null)
{
    public static JudgeDecision Nothing(string producer, long elapsedMs = 0) => new([], producer, 0, 0, elapsedMs);
    public static JudgeDecision Failed(string producer, string error, long elapsedMs = 0) => new([], producer, 0, 0, elapsedMs, error);
    public bool Significant => Findings.Count > 0;
}

/// <summary>
/// RELAY0's continuous judgment: is anything here significant, what kind of task is it, which words
/// substantiate it. Implementations never execute anything and never see canonical files directly.
/// </summary>
public interface IJudge
{
    string Name { get; }
    Task<JudgeDecision> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken);
}

/// <summary>A judge that never finds anything. Used when judging is switched off.</summary>
public sealed class NullJudge : IJudge
{
    public string Name => "off";
    public Task<JudgeDecision> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken) => Task.FromResult(JudgeDecision.Nothing(Name));
}
