using System.Text;
using Relay.Core.Mind;

namespace Relay.Core.Tools;

/// <summary>
/// The fixed tool-build prompt (docs/09: a utility prompt, not a role). The mind decides that a tool is
/// needed and names its inputs and outputs; this prompt turns that into a package — description,
/// arguments, declared host functions, JavaScript source, tests — under one schema. Replaceable by
/// <c>config\prompts\build.md</c>. The same prompt serves a retry, with the failure appended.
/// </summary>
public static class BuildPrompt
{
    public const string PromptName = "build";
    public const string SchemaName = "tool_draft";

    public const string DefaultInstructions =
        "You write small JavaScript tools for Relay, a local-first personal orchestrator. A tool runs in a sandbox (Jint, strict mode, ECMAScript 2023) " +
        "with no file system, no network, no process and no modules: no require, import, fetch, XMLHttpRequest, process, setTimeout. " +
        "The only way to reach the machine is the relay object, whose functions are listed below; each call is checked and recorded by Relay.\n\n" +
        "Contract:\n" +
        "- Define function run(args). args is an object whose values are all strings (they come from another program). Check that required ones are present and non-empty; convert numbers with Number().\n" +
        "- Do not invent stricter checks: a value the machine knows better than you (an IANA zone id, for instance) is checked by the relay function that takes it, which throws a clear message when it is bad. Pass such values through unchanged — no regular expression, no list of allowed values.\n" +
        "- Return a JSON-serializable value: an object with short, clearly named keys is best. Echo the inputs that identify the result (the zone name). Never return undefined.\n" +
        "- On bad input, throw new Error(\"<plain message>\"). Do not catch errors from relay.*: their messages are already what the user should see.\n" +
        "- Deterministic code only: no random values, no dependence on the machine's own clock (Date) — the present comes from relay.now() and relay.zone(). Finish in well under 10 seconds; no endless loops, no deep recursion.\n" +
        "- Keep the source under 4000 characters. Use var or const and plain functions; no async, no generators.\n\n" +
        "Example, a local-time tool: function run(args) { if (!args.zone) throw new Error(\"zone is required\"); var z = relay.zone(args.zone); " +
        "return { zone: z.zone, time: z.local.time, date: z.local.date, weekday: z.local.weekday, offset: z.offset }; } " +
        "relay.now() is the UTC instant; the local time anywhere is only ever z.local.* from relay.zone.\n\n" +
        "Tests: give 2–4 tests. Each names its arguments (all of them declared, required ones present) and what must hold: keys (names the result object must have) " +
        "and contains (a substring the JSON of the result must contain, or \"\" for none). Tests run your source in the real sandbox with the clock pinned at the instant given below, " +
        "so check exact values from the table there — the local time for the zone, not the zone name — and at least one test must use a zone whose local time differs from UTC. " +
        "A test that only checks the shape of the result passes a wrong answer.\n\n" +
        "Reply with one JSON object and nothing else: {\"description\": \"<one sentence, what the tool returns>\", \"arguments\": [{\"name\": \"snake_case\", \"description\": \"…\", \"required\": true}], " +
        "\"hostFunctions\": [\"time.zone\"], \"source\": \"<the JavaScript>\", \"tests\": [{\"args\": {\"zone\": \"Asia/Tokyo\"}, \"keys\": [\"zone\", \"time\"], \"contains\": \"21:00\"}]}. " +
        "hostFunctions lists the relay functions the source calls, by their names in the list below.";

    public const string Schema = """
        {"type":"object","properties":{
        "description":{"type":"string"},
        "arguments":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"description":{"type":"string"},"required":{"type":"boolean"}},"required":["name","description","required"],"additionalProperties":false}},
        "hostFunctions":{"type":"array","items":{"type":"string","enum":["time.now","time.zone"]}},
        "source":{"type":"string"},
        "tests":{"type":"array","items":{"type":"object","properties":{"args":{"type":"object","additionalProperties":{"type":"string"}},"keys":{"type":"array","items":{"type":"string"}},"contains":{"type":"string"}},"required":["args","keys","contains"],"additionalProperties":false}}},
        "required":["description","arguments","hostFunctions","source","tests"],"additionalProperties":false}
        """;

    /// <param name="anchors">What the host functions return at the instant the tests run (<see cref="HostFunctions.Anchors"/>), so tests can name exact values.</param>
    public static string System(string? instructionsOverride, string? anchors = null)
    {
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(instructionsOverride) ? DefaultInstructions : instructionsOverride!.Trim());
        sb.Append("\n\nThe relay object (the only bridge to the machine):\n");
        sb.Append(HostFunctions.Describe(HostFunctions.Catalog.Keys)).Append('\n');
        sb.Append("- relay.log(text) and console.log(text) — a progress line for Relay's activity feed; not part of the result.\n");
        sb.Append("Every other global is standard JavaScript (JSON, Math, String, Array, Object, RegExp, Number, Date for arithmetic on values you were given).\n");
        if (!string.IsNullOrWhiteSpace(anchors))
        {
            sb.Append("\nWhen the tests run, the clock is pinned: ").Append(anchors.Trim()).Append('\n');
            sb.Append("Use these values in your tests' contains. In real use the same functions return the real present.\n");
        }
        return sb.ToString();
    }

    /// <summary>The request: what the mind asked for, the ask it came from, and, on a retry, what went wrong last time.</summary>
    public static string User(BuildMove move, string ask, string? previousFailure)
    {
        var sb = new StringBuilder();
        sb.Append("Tool name: ").Append(move.Name).Append('\n');
        sb.Append("Why it is needed: ").Append(move.Justification).Append('\n');
        sb.Append("Inputs the mind expects: ").Append(Stated(move.Inputs) ? move.Inputs : "not stated").Append('\n');
        sb.Append("Outputs the mind expects: ").Append(Stated(move.Outputs) ? move.Outputs : "not stated — return the useful fields with short names at the top level of the result").Append('\n');
        sb.Append("Make the tool general even if the inputs above say none: whatever varies between requests of this kind (a zone, a place, a query) is an argument, never a value baked into the source.\n");
        if (!string.IsNullOrWhiteSpace(ask)) sb.Append("The user's request that needs it: \"").Append(ask.Length > 400 ? ask[..400] + "…" : ask).Append("\"\n");
        if (!string.IsNullOrWhiteSpace(previousFailure))
        {
            sb.Append("\nYour previous draft failed. Fix the cause and reply with the whole package again:\n").Append(previousFailure.Length > 1_500 ? previousFailure[..1_500] + "…" : previousFailure).Append('\n');
        }
        sb.Append("\nReply with the tool package as one JSON object.");
        return sb.ToString();
    }

    /// <summary>Whether the mind said anything usable: "none", "n/a" and the like mean it did not.</summary>
    private static bool Stated(string? text)
        => !string.IsNullOrWhiteSpace(text) && text.Trim().Trim('.').ToLowerInvariant() is not ("none" or "n/a" or "na" or "-" or "nothing" or "not stated" or "unknown");
}
