using System.Globalization;
using System.Text;
using Relay.Core.Decisions;
using Relay.Core.Mind;
using Relay.Core.Policy;

namespace Relay.Core.Usage;

/// <summary>
/// One improvement suggested from repeated friction in usage lines: the action to propose, the
/// contract-bearing target, the motivating examples, and a short pattern name for the ledger.
/// </summary>
public sealed record FrictionSuggestion(
    string Pattern,
    string Action,
    IReadOnlyDictionary<string, string> Target,
    string Reason,
    IReadOnlyList<string> ExampleTaskIds);

/// <summary>
/// Reads recent usage lines and turns a repeated friction pattern into one improvement suggestion.
/// Threshold is three similar events in the window. Prefers a concrete preference when the evidence
/// is clear; otherwise a prompt-line preference. Never auto-applies — the coordinator still needs Tier B approval.
/// </summary>
public static class FrictionReview
{
    public const int Threshold = 3;
    public const long LongWallMs = 45_000;
    public const int LongSteps = 8;

    /// <summary>Returns the strongest friction pattern at or above <see cref="Threshold"/>, or null when nothing repeats enough.</summary>
    public static FrictionSuggestion? Suggest(IReadOnlyList<UsageLine> lines, int threshold = Threshold)
    {
        if (lines.Count < threshold) return null;

        FrictionSuggestion? best = null;
        var bestCount = 0;

        void Consider(int count, FrictionSuggestion? suggestion)
        {
            if (suggestion is null || count < threshold) return;
            // Earlier candidates are higher priority; equal counts keep the first.
            if (best is not null && count <= bestCount) return;
            best = suggestion;
            bestCount = count;
        }

        Consider(CountNeed(lines, MindRead.NeedNewTool, out var toolIds), NeedNewTool(toolIds));
        Consider(CountSearchNeed(lines, out var searchIds), NeedSearch(searchIds));
        Consider(CountOutcome(lines, "denied", out var deniedIds), RepeatedDeny(deniedIds));
        Consider(CountRoute(lines, Decider.OfferDelegate, out var delIds), RepeatedDelegate(delIds));
        Consider(CountLongPath(lines, out var longIds), LongPath(longIds));

        return best;
    }

    private static FrictionSuggestion NeedNewTool(IReadOnlyList<string> ids)
        => Preference(
            "need:new_tool",
            "response.promptLine",
            "When the same capability gap appears repeatedly, use build so the user can approve drafting a personal tool.",
            "A personal tool for a repeated capability gap",
            "preferences.json (one prompt line)",
            "one preference key",
            "The next similar task drafts or reuses a tool instead of stopping at new_tool",
            ids,
            "Repeated new_tool need");

    private static FrictionSuggestion NeedSearch(IReadOnlyList<string> ids)
        => Preference(
            "need:search",
            "sources.allowOnlineSearch",
            "true",
            "Standing online search for repeated research friction",
            "preferences.json (sources.allowOnlineSearch)",
            "one preference key",
            "The next research task may search without a fresh sources grant",
            ids,
            "Repeated external_reasoning / world_knowledge need");

    private static FrictionSuggestion RepeatedDeny(IReadOnlyList<string> ids)
        => Preference(
            "outcome:denied",
            "response.promptLine",
            "When policy has denied the same kind of proposal repeatedly, explain the denial and take another path instead of proposing it again unchanged.",
            "Fewer repeated policy denials",
            "preferences.json (one prompt line)",
            "one preference key",
            "The next similar ask does not re-propose the denied action without new evidence",
            ids,
            "Repeated denied outcome");

    private static FrictionSuggestion RepeatedDelegate(IReadOnlyList<string> ids)
        => Preference(
            "route:offer_delegate",
            "sources.allowOnlineSearch",
            "true",
            "Standing online search when tasks repeatedly need a delegate",
            "preferences.json (sources.allowOnlineSearch)",
            "one preference key",
            "The next offer_delegate path can search under the standing grant",
            ids,
            "Repeated offer_delegate route");

    private static FrictionSuggestion LongPath(IReadOnlyList<string> ids)
        => Preference(
            "path:long",
            "response.verbosity",
            "concise",
            "Shorter answers after repeatedly long task paths",
            "preferences.json (response.verbosity)",
            "one preference key",
            "The next answer stays under the concise length budget",
            ids,
            "Repeated long path (wall time or step count)");

    private static FrictionSuggestion Preference(
        string pattern, string key, string value,
        string benefit, string permissions, string scope, string acceptance,
        IReadOnlyList<string> ids, string headline)
    {
        var examples = string.Join(", ", ids.Take(5));
        var reason = new StringBuilder()
            .Append(headline).Append(" in ").Append(ids.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" recent task(s): ").Append(examples)
            .Append(". Propose ").Append(Actions.UpdatePreference).Append(' ').Append(key).Append('=').Append(value).Append('.')
            .ToString();
        var target = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["key"] = key,
            ["value"] = value,
            ["benefit"] = benefit,
            ["permissions"] = permissions,
            ["scope"] = scope,
            ["acceptance"] = acceptance,
        };
        return new FrictionSuggestion(pattern, Actions.UpdatePreference, target, reason, ids);
    }

    private static int CountNeed(IReadOnlyList<UsageLine> lines, string need, out List<string> ids)
    {
        ids = lines.Where(l => l.Needs.Contains(need, StringComparer.Ordinal)).Select(l => l.TaskId).Distinct(StringComparer.Ordinal).ToList();
        return ids.Count;
    }

    private static int CountSearchNeed(IReadOnlyList<UsageLine> lines, out List<string> ids)
    {
        ids = lines.Where(l => l.Needs.Contains(MindRead.NeedExternalReasoning, StringComparer.Ordinal)
                || l.Needs.Contains(MindRead.NeedWorldKnowledge, StringComparer.Ordinal))
            .Select(l => l.TaskId).Distinct(StringComparer.Ordinal).ToList();
        return ids.Count;
    }

    private static int CountOutcome(IReadOnlyList<UsageLine> lines, string outcome, out List<string> ids)
    {
        ids = lines.Where(l => string.Equals(l.Outcome, outcome, StringComparison.Ordinal)).Select(l => l.TaskId).Distinct(StringComparer.Ordinal).ToList();
        return ids.Count;
    }

    private static int CountRoute(IReadOnlyList<UsageLine> lines, string route, out List<string> ids)
    {
        ids = lines.Where(l => string.Equals(l.Route, route, StringComparison.Ordinal)).Select(l => l.TaskId).Distinct(StringComparer.Ordinal).ToList();
        return ids.Count;
    }

    private static int CountLongPath(IReadOnlyList<UsageLine> lines, out List<string> ids)
    {
        ids = lines.Where(l => l.WallMs >= LongWallMs || l.Steps >= LongSteps).Select(l => l.TaskId).Distinct(StringComparer.Ordinal).ToList();
        return ids.Count;
    }
}
