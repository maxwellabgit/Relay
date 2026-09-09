using System.Globalization;
using System.Text;

namespace Relay.Core.Mind;

/// <summary>
/// One typed move, the only thing the mind can do per step. The wire form is flat (type, text, name,
/// args, done) so that one small schema covers every move; <see cref="MoveSchema.Parse"/> turns the
/// flat object into one of these records and validates what each type needs.
/// </summary>
public abstract record Move(string Type)
{
    public const string Say = "say";
    public const string UseTool = "use_tool";
    public const string Propose = "propose";
    public const string Delegate = "delegate";
    public const string Build = "build";
    public const string AskUser = "ask_user";
    public const string Wait = "wait";
    public const string Stop = "stop";

    public static readonly string[] Types = [Say, UseTool, Propose, Delegate, Build, AskUser, Wait, Stop];

    /// <summary>One line for the transcript and the ledger: the type and what it names, never long prose.</summary>
    public abstract string Brief();

    protected static string Clip(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..(max - 1)] + "…";

    protected static string Map(IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0) return "{}";
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (k, v) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(k).Append('=').Append(Clip(v, 120));
        }
        return sb.Append('}').ToString();
    }
}

/// <summary>Text for the user. <see cref="Done"/> true ends the task with this text as the answer; false narrates while the mind continues.</summary>
public sealed record SayMove(string Text, bool Done) : Move(Say)
{
    public override string Brief() => $"say{(Done ? " (done)" : "")} \"{Clip(Text, 160)}\"";
}

/// <summary>A read-only tool call; the result comes back as a <see cref="ToolObserved"/>.</summary>
public sealed record UseToolMove(string Tool, IReadOnlyDictionary<string, string> Args) : Move(UseTool)
{
    public override string Brief() => $"use_tool {Tool} {Map(Args)}";
}

/// <summary>An action for policy to decide and, when the user approves, the executor to run. Target is a flat string map exactly like a <see cref="Policy.Proposal"/>.</summary>
public sealed record ProposeMove(string Action, IReadOnlyDictionary<string, string> Target, string Reason) : Move(Propose)
{
    public override string Brief() => $"propose {Action} {Map(Target)}";
}

/// <summary>Send a prompt the mind wrote, plus selected local references, to a named external profile. Leaves the machine only after approval.</summary>
public sealed record DelegateMove(string Profile, string Prompt, IReadOnlyList<string> Refs, int BudgetTokens, bool AllowSearch) : Move(Delegate)
{
    public override string Brief() => $"delegate {Profile} (refs {Refs.Count}, budget {BudgetTokens}{(AllowSearch ? ", search" : "")}) \"{Clip(Prompt, 120)}\"";
}

/// <summary>Ask Relay to build a new tool: a name, a one-line justification, and the inputs and outputs it must have.</summary>
public sealed record BuildMove(string Name, string Justification, string Inputs, string Outputs) : Move(Build)
{
    public override string Brief() => $"build {Name} (in: {Clip(Inputs, 80)}; out: {Clip(Outputs, 80)}) \"{Clip(Justification, 120)}\"";
}

/// <summary>One question for the user, optionally with choices; the reply arrives as a <see cref="UserObserved"/>.</summary>
public sealed record AskUserMove(string Question, IReadOnlyList<string> Options) : Move(AskUser)
{
    public override string Brief() => $"ask_user \"{Clip(Question, 160)}\"" + (Options.Count == 0 ? "" : $" [{string.Join(" | ", Options)}]");
}

/// <summary>Nothing to do: ends an overheard task that meant nothing, or keeps waiting while an operation runs.</summary>
public sealed record WaitMove(string Reason) : Move(Wait)
{
    public override string Brief() => $"wait \"{Clip(Reason, 160)}\"";
}

/// <summary>Cancel the running delegate or build.</summary>
public sealed record StopMove(string Reason) : Move(Stop)
{
    public override string Brief() => $"stop \"{Clip(Reason, 160)}\"";
}

/// <summary>How risky the mind believes its own next move is, per axis, 0–1. The deterministic fundamental-operation flag reads these; it can only raise the bar the hard rules set.</summary>
public sealed record RiskRead(double Core, double Security, double Loop, double Destructive)
{
    public static readonly RiskRead None = new(0, 0, 0, 0);
    public double Max => Math.Max(Math.Max(Core, Security), Math.Max(Loop, Destructive));
}

/// <summary>
/// The mind's own triage of the task, produced on every step: what is being asked, how complex it is
/// (0 trivial … 1 beyond a local model), what it needs, how significant and how sensitive the material
/// is, and the risk of its next move. Features for the <see cref="Decisions.Decider"/>; never a verdict.
/// </summary>
public sealed record MindRead(string Intent, double Complexity, IReadOnlyList<string> Needs, double Significance, double Sensitivity, RiskRead Risk)
{
    public const string NeedNone = "none";
    public const string NeedLocalNotes = "local_notes";
    public const string NeedWorldKnowledge = "world_knowledge";
    public const string NeedNewTool = "new_tool";
    public const string NeedExternalReasoning = "external_reasoning";
    public const string NeedUserInput = "user_input";
    public static readonly string[] KnownNeeds = [NeedNone, NeedLocalNotes, NeedWorldKnowledge, NeedNewTool, NeedExternalReasoning, NeedUserInput];

    public bool Has(string need) => Needs.Contains(need, StringComparer.Ordinal);

    public string Brief()
        => $"intent \"{Intent}\" · complexity {Complexity.ToString("0.##", CultureInfo.InvariantCulture)} · needs {string.Join(",", Needs)} · significance {Significance.ToString("0.##", CultureInfo.InvariantCulture)} · sensitivity {Sensitivity.ToString("0.##", CultureInfo.InvariantCulture)} · risk max {Risk.Max.ToString("0.##", CultureInfo.InvariantCulture)}";
}

/// <summary>
/// One reply from the mind: its read, its move, its feed sentence, and the metrics of the call. A
/// failed step (model unavailable, contract broken) has <see cref="Error"/> set and no move; the loop
/// decides whether to retry.
/// </summary>
public sealed record MindStep(MindRead? Read, Move? Move, string Feed, string? Raw, int PromptChars, int PromptTokens, int CompletionTokens, long ElapsedMs, string? Error)
{
    public bool Ok => Error is null && Move is not null;

    public static MindStep Failed(string error, string? raw, int promptChars, long elapsedMs, int promptTokens = 0, int completionTokens = 0)
        => new(null, null, "", raw, promptChars, promptTokens, completionTokens, elapsedMs, error);

    /// <summary>A step built in code (tests, scripted minds): the feed defaults to the move's brief.</summary>
    public static MindStep Of(Move move, string? feed = null, MindRead? read = null)
        => new(read, move, feed ?? move.Brief(), null, 0, 0, 0, 0, null);
}
