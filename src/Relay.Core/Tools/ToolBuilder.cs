using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Core.Mind;
using Relay.Core.Model;

namespace Relay.Core.Tools;

/// <summary>One draft from the model: the package when it parsed and validated, otherwise what was wrong (fed back on the retry).</summary>
public sealed record ToolDraftResult(bool Ok, ToolPackage? Package, string? Error, string? Raw, int PromptChars, int PromptTokens, int CompletionTokens, long ElapsedMs);

/// <summary>
/// One test's outcome. <see cref="Detail"/> is what everyone may see (the mind, the ledger, the panel) and never
/// quotes what the tool returned; <see cref="Result"/> is the clipped result JSON for the drafter alone. Tests run
/// against a pinned clock, so a result is a value from that fixed instant, and a mind that read one in its
/// transcript would take it for the present.
/// </summary>
public sealed record ToolTestOutcome(int Index, bool Passed, string Detail, string RunId, long ElapsedMs, string? Result = null);

public sealed record ToolTestReport(bool Passed, IReadOnlyList<ToolTestOutcome> Outcomes, ToolPackage Package)
{
    /// <summary>The report as the mind and the ledger see it: which tests passed and what was wrong, without results.</summary>
    public string Summary => Passed
        ? $"{Outcomes.Count} of {Outcomes.Count} test(s) passed in the sandbox"
        : $"{Outcomes.Count(o => o.Passed)} of {Outcomes.Count} test(s) passed; " + string.Join("; ", Outcomes.Where(o => !o.Passed).Select(o => $"test {o.Index}: {o.Detail}"));

    /// <summary>The report as the drafter sees it on a retry: the same, with each failing test's actual result quoted.</summary>
    public string ForDrafter => Passed ? Summary
        : $"{Outcomes.Count(o => o.Passed)} of {Outcomes.Count} test(s) passed with the clock pinned at {ToolBuilder.TestInstant:yyyy-MM-dd'T'HH:mm:ss'Z'}; "
          + string.Join("; ", Outcomes.Where(o => !o.Passed).Select(o => $"test {o.Index}: {o.Detail}" + (o.Result is null ? "" : $" (result: {o.Result})")));
}

/// <summary>A stage of a build as it happens: drafted (the package exists), tested (all tests passed), failed (this attempt did not; a retry may follow).</summary>
public sealed record BuildProgress(string Stage, string Detail, ToolPackage? Package, int Attempt, int Attempts);

/// <summary>What a whole build came to: a tested draft in staging, or why not.</summary>
public sealed record BuildOutcome(bool Ok, ToolPackage? Package, string Summary, int Attempts, int PromptTokens, int CompletionTokens, IReadOnlyList<ToolTestOutcome> Tests);

/// <summary>
/// Turns the mind's build move into a tested draft: the fixed build prompt asks the model for a package
/// under a schema, the package is validated, its tests run in the worker sandbox, and one retry feeds the
/// failure back verbatim. Nothing here promotes anything: the tested draft waits in staging for the
/// user's one approval (<c>add_tool</c>), and the loop observes each stage as it happens.
/// </summary>
public sealed partial class ToolBuilder
{
    public const int MaxAttempts = 2;
    public const int MaxOutputTokens = 2_500;

    /// <summary>
    /// The present a draft's tests run against: fixed, so the prompt can state what the host functions return then and
    /// the model can write tests with exact values — the check that catches a source returning the wrong time while
    /// passing every shape test. A Friday noon UTC in September: London on summer time, New York on daylight time,
    /// Tokyo without either, Kolkata on a half-hour offset.
    /// </summary>
    public static readonly DateTimeOffset TestInstant = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    public static readonly string[] AnchorZones = ["UTC", "Europe/London", "Europe/Berlin", "America/New_York", "America/Los_Angeles", "Asia/Kolkata", "Asia/Tokyo", "Australia/Sydney"];

    private readonly ToolStore _store;
    private readonly ToolRunner _runner;
    private readonly Func<IModelClient?> _drafter;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string?> _instructions;

    public ToolBuilder(ToolStore store, ToolRunner runner, Func<IModelClient?> drafter, Func<DateTimeOffset>? clock = null, Func<string?>? instructionsOverride = null)
    {
        _store = store;
        _runner = runner;
        _drafter = drafter;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _instructions = instructionsOverride ?? (() => null);
    }

    public ToolStore Store => _store;
    public ToolRunner Runner => _runner;
    /// <summary>Drafting needs a model; without one, builds are unavailable and the mind is told so.</summary>
    public bool CanDraft => _drafter() is not null;
    public string DrafterName => _drafter()?.Model ?? "none";

    /// <summary>Draft, validate, test, retry once. <paramref name="progress"/> is awaited after each stage so the host can show it to the mind before the next begins.</summary>
    public async Task<BuildOutcome> BuildAsync(BuildMove move, string ask, string? taskId, Func<BuildProgress, Task> progress, CancellationToken cancellationToken)
    {
        if (_drafter() is null) return new BuildOutcome(false, null, "No model is configured to draft tools.", 0, 0, 0, []);
        string? failure = null;
        int promptTokens = 0, completionTokens = 0;
        IReadOnlyList<ToolTestOutcome> lastTests = [];
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var draft = await DraftAsync(move, ask, taskId, failure, cancellationToken).ConfigureAwait(false);
            promptTokens += draft.PromptTokens;
            completionTokens += draft.CompletionTokens;
            if (!draft.Ok)
            {
                failure = draft.Error;
                await progress(new BuildProgress(BuildObserved.Failed, $"attempt {attempt}: {draft.Error}", null, attempt, MaxAttempts)).ConfigureAwait(false);
                continue;
            }
            var package = draft.Package!;
            _store.SaveDraft(package);
            await progress(new BuildProgress(BuildObserved.Drafted, Describe(package), package, attempt, MaxAttempts)).ConfigureAwait(false);

            var report = await TestAsync(package, taskId, cancellationToken).ConfigureAwait(false);
            lastTests = report.Outcomes;
            if (report.Passed)
            {
                _store.SaveDraft(report.Package);
                await progress(new BuildProgress(BuildObserved.Tested, report.Summary, report.Package, attempt, MaxAttempts)).ConfigureAwait(false);
                return new BuildOutcome(true, report.Package, report.Summary, attempt, promptTokens, completionTokens, report.Outcomes);
            }
            failure = report.ForDrafter + WhereItFailed(package.Source, report.Outcomes) + "\n\nThe source that failed:\n" + package.Source;
            await progress(new BuildProgress(BuildObserved.Failed, $"attempt {attempt}: {report.Summary}", package, attempt, MaxAttempts)).ConfigureAwait(false);
        }
        return new BuildOutcome(false, null, $"The tool could not be built in {MaxAttempts} attempts. Last failure: {Clip(failure ?? "unknown", 600)}", MaxAttempts, promptTokens, completionTokens, lastTests);
    }

    /// <summary>One schema-constrained completion, parsed into a package and validated. The package's name is the mind's; the model supplies the rest.</summary>
    public async Task<ToolDraftResult> DraftAsync(BuildMove move, string ask, string? taskId, string? previousFailure, CancellationToken cancellationToken)
    {
        var drafter = _drafter();
        if (drafter is null) return new ToolDraftResult(false, null, "No model is configured to draft tools.", null, 0, 0, 0, 0);
        var watch = Stopwatch.StartNew();
        var messages = new List<ModelMessage> { new("system", BuildPrompt.System(_instructions(), _runner.Functions.Anchors(TestInstant, AnchorZones))), new("user", BuildPrompt.User(move, ask, previousFailure)) };
        var promptChars = messages.Sum(m => m.Content.Length);
        var response = await drafter.CompleteAsync(new ModelRequest(drafter.Model, messages, MaxOutputTokens, JsonObject: true, JsonSchema: BuildPrompt.Schema, SchemaName: BuildPrompt.SchemaName), cancellationToken).ConfigureAwait(false);
        if (!response.Ok) return new ToolDraftResult(false, null, "The drafting model was unavailable: " + response.Error, null, promptChars, response.PromptTokens, response.CompletionTokens, watch.ElapsedMilliseconds);
        var raw = response.Content ?? "";
        ToolPackage package;
        try { package = Parse(move, raw, taskId, drafter.Model); }
        catch (FormatException ex) { return new ToolDraftResult(false, null, "The draft did not follow the package contract: " + ex.Message, raw, promptChars, response.PromptTokens, response.CompletionTokens, watch.ElapsedMilliseconds); }
        var problems = package.Validate(_store.ReservedNames());
        if (problems.Count > 0) return new ToolDraftResult(false, null, "The draft is not a valid package: " + string.Join("; ", problems), raw, promptChars, response.PromptTokens, response.CompletionTokens, watch.ElapsedMilliseconds);
        return new ToolDraftResult(true, package, null, raw, promptChars, response.PromptTokens, response.CompletionTokens, watch.ElapsedMilliseconds);
    }

    /// <summary>Runs every test in the sandbox with the clock pinned at <see cref="TestInstant"/>. On success the returned package carries the tested hash, which promotion requires.</summary>
    public async Task<ToolTestReport> TestAsync(ToolPackage package, string? taskId, CancellationToken cancellationToken)
    {
        var outcomes = new List<ToolTestOutcome>();
        for (var i = 0; i < package.Tests.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var test = package.Tests[i];
            var run = await _runner.RunAsync(package, test.Args, $"test {i + 1} of {package.Name}", taskId, ToolRunner.DefaultCallTimeoutSeconds, TestInstant, cancellationToken).ConfigureAwait(false);
            if (!run.Ok) { outcomes.Add(new ToolTestOutcome(i + 1, false, run.Error ?? "failed", run.RunId, run.ElapsedMs)); continue; }
            var problem = ToolPackage.Check(test, run.ResultJson!);
            outcomes.Add(problem is null
                ? new ToolTestOutcome(i + 1, true, "ok", run.RunId, run.ElapsedMs, Clip(run.ResultJson!, 200))
                : new ToolTestOutcome(i + 1, false, problem, run.RunId, run.ElapsedMs, Clip(run.ResultJson!, 200)));
        }
        var passed = outcomes.Count > 0 && outcomes.All(o => o.Passed);
        var tested = passed ? With(package, p => new ToolPackage
        {
            Name = p.Name, Description = p.Description, Arguments = p.Arguments, HostFunctionNames = p.HostFunctionNames, Source = p.Source, Tests = p.Tests,
            BuiltBy = p.BuiltBy, TaskId = p.TaskId, Justification = p.Justification, DraftedAt = p.DraftedAt, TestedSha256 = p.SourceSha256, TestedAt = _clock(),
        }) : package;
        return new ToolTestReport(passed, outcomes, tested);
    }

    private static ToolPackage With(ToolPackage p, Func<ToolPackage, ToolPackage> f) => f(p);

    /// <summary>
    /// When a failed test's error names a line of the source, that line and the one before it are quoted back, and an
    /// error the source threw itself is named as such: a small model that reads "Invalid zone (line 5)" tends to
    /// rewrite the message; shown the check that threw it against a valid test input, it removes the check.
    /// </summary>
    public static string WhereItFailed(string source, IReadOnlyList<ToolTestOutcome> outcomes)
    {
        var lines = source.Split('\n');
        var seen = new HashSet<int>();
        var sb = new StringBuilder();
        foreach (var o in outcomes.Where(o => !o.Passed))
        {
            var m = LinePattern().Match(o.Detail);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n) || n < 1 || n > lines.Length || !seen.Add(n)) continue;
            sb.Append($"\n\nTest {o.Index} failed at line {n} of your source");
            var own = lines[n - 1].Contains("throw", StringComparison.Ordinal);
            sb.Append(own ? ", in an error your own code throws. The test's arguments are valid, so the check that led to this throw is wrong: remove it or loosen it.\n" : ".\n");
            for (var i = Math.Max(1, n - 1); i <= n; i++) sb.Append($"  {i}: {lines[i - 1].TrimEnd('\r')}\n");
        }
        return sb.ToString();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\(line (\d+)\)")]
    private static partial System.Text.RegularExpressions.Regex LinePattern();

    private ToolPackage Parse(BuildMove move, string raw, string? taskId, string drafter)
    {
        JsonObject root;
        try { root = JsonNode.Parse(raw)?.AsObject() ?? throw new FormatException("the reply was not a JSON object"); }
        catch (JsonException ex) { throw new FormatException("the reply was not valid JSON: " + ex.Message); }
        catch (InvalidOperationException) { throw new FormatException("the reply was not a JSON object"); }

        var arguments = new List<ToolArgument>();
        if (root["arguments"] is JsonArray args)
            foreach (var a in args.OfType<JsonObject>())
            {
                var name = Str(a["name"]).Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
                if (name.Length == 0) continue;
                arguments.Add(new ToolArgument(name, Str(a["description"]).Trim(), a["required"] is JsonValue v && v.TryGetValue<bool>(out var req) ? req : !string.Equals(Str(a["required"]), "false", StringComparison.OrdinalIgnoreCase)));
            }
        var hostFunctions = (root["hostFunctions"] as JsonArray)?.Select(Str).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList() ?? [];
        var tests = new List<ToolTest>();
        if (root["tests"] is JsonArray ts)
            foreach (var t in ts.OfType<JsonObject>())
            {
                var testArgs = (t["args"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => Str(kv.Value), StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);
                var keys = (t["keys"] as JsonArray)?.Select(Str).Where(s => s.Length > 0).ToList();
                var contains = Str(t["contains"]);
                var matches = Str(t["matches"]);
                tests.Add(new ToolTest(testArgs, keys is { Count: > 0 } ? keys : null, contains.Length > 0 ? contains : null, matches.Length > 0 ? matches : null));
            }
        var source = Str(root["source"]);
        if (source.Trim().Length == 0) throw new FormatException("the package has no source");
        // Host functions the source calls but the draft did not declare are declared for it: the declaration exists to bound the run, and the source is what runs.
        // Both spellings the bridge accepts count: relay.zone(...) and relay.time.zone(...).
        foreach (var fn in HostFunctions.Catalog.Values)
            if (!hostFunctions.Contains(fn.Name) && (source.Contains("relay." + fn.JsName + "(", StringComparison.Ordinal) || source.Contains("relay." + fn.Name + "(", StringComparison.Ordinal))) hostFunctions.Add(fn.Name);
        return new ToolPackage
        {
            Name = move.Name,
            Description = Str(root["description"]).Trim(),
            Arguments = arguments,
            HostFunctionNames = hostFunctions,
            Source = source,
            Tests = tests,
            BuiltBy = drafter,
            TaskId = taskId,
            Justification = move.Justification,
            DraftedAt = _clock(),
        };
    }

    private static string Describe(ToolPackage p)
        => $"{p.Name}({string.Join(", ", p.Arguments.Select(a => a.Required ? a.Name : a.Name + "?"))}): {p.Description} · {p.Source.Length} chars of JavaScript · host functions: {(p.HostFunctionNames.Count == 0 ? "none" : string.Join(", ", p.HostFunctionNames))} · {p.Tests.Count} test(s) queued";

    private static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString().Trim('"'),
        _ => node.ToJsonString(),
    };

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
