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
        "- Define function run(args). args is an object whose values are all strings (they come from another program). Convert and validate them yourself.\n" +
        "- Return a JSON-serializable value: an object with short, clearly named keys is best. Never return undefined.\n" +
        "- On bad input, throw new Error(\"<plain message>\"). Do not catch errors from relay.* unless you can do better than the message.\n" +
        "- Deterministic code only: no random values, no dependence on the machine's own clock (Date) — the present comes from relay.now() and relay.zone(). Finish in well under 10 seconds; no endless loops, no deep recursion.\n" +
        "- Keep the source under 4000 characters. Use var or const and plain functions; no async, no generators.\n\n" +
        "Tests: give 1–4 tests. Each names its arguments (all of them declared, required ones present) and what must hold: keys (names the result object must have) " +
        "and/or contains (a substring the result's JSON must contain). Tests run in the real sandbox, so they must not depend on the exact current time; check the shape and the stable parts (the zone name, a fixed conversion).\n\n" +
        "Reply with one JSON object and nothing else: {\"description\": \"<one sentence, what the tool returns>\", \"arguments\": [{\"name\": \"snake_case\", \"description\": \"…\", \"required\": true}], " +
        "\"hostFunctions\": [\"time.zone\"], \"source\": \"<the JavaScript>\", \"tests\": [{\"args\": {\"zone\": \"Europe/London\"}, \"keys\": [\"time\"], \"contains\": \"Europe/London\"}]}. " +
        "hostFunctions lists only the relay functions the source actually calls.";

    public const string Schema = """
        {"type":"object","properties":{
        "description":{"type":"string"},
        "arguments":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"description":{"type":"string"},"required":{"type":"boolean"}},"required":["name","description","required"],"additionalProperties":false}},
        "hostFunctions":{"type":"array","items":{"type":"string","enum":["time.now","time.zone"]}},
        "source":{"type":"string"},
        "tests":{"type":"array","items":{"type":"object","properties":{"args":{"type":"object","additionalProperties":{"type":"string"}},"keys":{"type":"array","items":{"type":"string"}},"contains":{"type":"string"}},"required":["args","keys","contains"],"additionalProperties":false}}},
        "required":["description","arguments","hostFunctions","source","tests"],"additionalProperties":false}
        """;

    public static string System(string? instructionsOverride)
    {
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(instructionsOverride) ? DefaultInstructions : instructionsOverride!.Trim());
        sb.Append("\n\nThe relay object (the only bridge to the machine):\n");
        sb.Append(HostFunctions.Describe(HostFunctions.Catalog.Keys)).Append('\n');
        sb.Append("- relay.log(text) and console.log(text) — a progress line for Relay's activity feed; not part of the result.\n");
        sb.Append("Every other global is standard JavaScript (JSON, Math, String, Array, Object, RegExp, Number, Date for arithmetic on values you were given).\n");
        return sb.ToString();
    }

    /// <summary>The request: what the mind asked for, the ask it came from, and, on a retry, what went wrong last time.</summary>
    public static string User(BuildMove move, string ask, string? previousFailure)
    {
        var sb = new StringBuilder();
        sb.Append("Tool name: ").Append(move.Name).Append('\n');
        sb.Append("Why it is needed: ").Append(move.Justification).Append('\n');
        if (!string.IsNullOrWhiteSpace(move.Inputs)) sb.Append("Inputs the mind expects: ").Append(move.Inputs).Append('\n');
        if (!string.IsNullOrWhiteSpace(move.Outputs)) sb.Append("Outputs the mind expects: ").Append(move.Outputs).Append('\n');
        if (!string.IsNullOrWhiteSpace(ask)) sb.Append("The user's request that needs it: \"").Append(ask.Length > 400 ? ask[..400] + "…" : ask).Append("\"\n");
        if (!string.IsNullOrWhiteSpace(previousFailure))
        {
            sb.Append("\nYour previous draft failed. Fix the cause and reply with the whole package again:\n").Append(previousFailure.Length > 1_500 ? previousFailure[..1_500] + "…" : previousFailure).Append('\n');
        }
        sb.Append("\nReply with the tool package as one JSON object.");
        return sb.ToString();
    }
}
