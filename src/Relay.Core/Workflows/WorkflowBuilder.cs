using System.Text.RegularExpressions;

namespace Relay.Core.Workflows;

/// <summary>Result of validating and evaluating a draft workflow before promotion.</summary>
public sealed record WorkflowTestReport(bool Passed, WorkflowDefinition Package, string Summary, IReadOnlyList<string> Problems);

/// <summary>One fixture evaluation trace.</summary>
public sealed record WorkflowFixtureResult(string Name, bool Passed, string Detail, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Validates a workflow and evaluates fixtures (value refs, wait/resume, expected outputs).
/// Structural-only dry-run is insufficient for promotion — at least one fixture must pass.
/// </summary>
public sealed class WorkflowBuilder
{
    private static readonly Regex ValueRef = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    private readonly WorkflowStore _store;
    private readonly Func<DateTimeOffset> _clock;

    public WorkflowBuilder(WorkflowStore store, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public WorkflowStore Store => _store;

    /// <summary>Legacy structural dry-run (no fixture execution). Prefer <see cref="TestWithFixtures"/> for promotion on CaseRuntime.</summary>
    public WorkflowTestReport Test(WorkflowDefinition draft, string? taskId = null)
    {
        var reserved = _store.ReservedNames().Where(n => n != draft.Name);
        var problems = draft.Validate(reserved).ToList();
        if (problems.Count > 0)
        {
            var failed = WithMeta(draft, taskId, tested: false);
            _store.SaveDraft(failed);
            return new WorkflowTestReport(false, failed, $"Dry-run failed: {string.Join("; ", problems)}", problems);
        }

        var now = _clock();
        var tested = new WorkflowDefinition
        {
            Name = draft.Name, Description = draft.Description, Version = draft.Version,
            Inputs = draft.Inputs, Outputs = draft.Outputs, Steps = draft.Steps, Fixtures = draft.Fixtures,
            BuiltBy = draft.BuiltBy ?? "hand", TaskId = taskId ?? draft.TaskId, Justification = draft.Justification,
            DraftedAt = draft.DraftedAt ?? now, TestedSha256 = draft.DefinitionSha256, TestedAt = now,
        };
        _store.SaveDraft(tested);
        var kinds = string.Join(" → ", tested.Steps.Select(s => s.Kind));
        return new WorkflowTestReport(true, tested, $"{tested.Steps.Count} step(s) dry-ran cleanly ({kinds}).", []);
    }

    /// <summary>
    /// Validates shape, then runs each fixture with value-ref resolution and wait/resume.
    /// Marks tested only when every fixture passes. Required for CaseRuntime promotion.
    /// </summary>
    public WorkflowTestReport TestWithFixtures(WorkflowDefinition draft, string? taskId = null)
    {
        var reserved = _store.ReservedNames().Where(n => n != draft.Name);
        var problems = draft.Validate(reserved).ToList();
        if (draft.Fixtures.Count == 0)
            problems.Add("at least one fixture is required for promotion (fixture-based evaluation, not structural-only)");

        if (problems.Count > 0)
        {
            var failed = WithMeta(draft, taskId, tested: false);
            _store.SaveDraft(failed);
            return new WorkflowTestReport(false, failed, $"Evaluation failed: {string.Join("; ", problems)}", problems);
        }

        var fixtureProblems = new List<string>();
        foreach (var fixture in draft.Fixtures)
        {
            var result = EvaluateFixture(draft, fixture);
            if (!result.Passed)
                fixtureProblems.Add($"{fixture.Name}: {result.Detail}");
        }

        if (fixtureProblems.Count > 0)
        {
            var failed = WithMeta(draft, taskId, tested: false);
            _store.SaveDraft(failed);
            return new WorkflowTestReport(false, failed, "Fixture evaluation failed: " + string.Join("; ", fixtureProblems), fixtureProblems);
        }

        var now = _clock();
        var tested = new WorkflowDefinition
        {
            Name = draft.Name,
            Description = draft.Description,
            Version = draft.Version,
            Inputs = draft.Inputs,
            Outputs = draft.Outputs,
            Steps = draft.Steps,
            Fixtures = draft.Fixtures,
            BuiltBy = draft.BuiltBy ?? "hand",
            TaskId = taskId ?? draft.TaskId,
            Justification = draft.Justification,
            DraftedAt = draft.DraftedAt ?? now,
            TestedSha256 = draft.DefinitionSha256,
            TestedAt = now,
        };
        _store.SaveDraft(tested);
        var kinds = string.Join(" → ", tested.Steps.Select(s => s.Kind));
        var perms = string.Join(", ", tested.PermissionUnion);
        return new WorkflowTestReport(true, tested,
            $"{tested.Fixtures.Count} fixture(s) passed; {tested.Steps.Count} step(s) ({kinds}); permissions: {perms}.", []);
    }

    /// <summary>Execute one fixture: resolve <c>${input.x}</c> / <c>${stepId.field}</c>, honour wait+resume.</summary>
    public static WorkflowFixtureResult EvaluateFixture(WorkflowDefinition def, WorkflowFixture fixture)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in fixture.Inputs)
            values["input." + k] = v;

        for (var i = 0; i < def.Steps.Count; i++)
        {
            var step = def.Steps[i];
            var kind = (step.Kind ?? "").Trim().ToLowerInvariant().Replace('-', '_');
            var args = ResolveArgs(step.Args ?? new Dictionary<string, string>(), values);
            var stepId = string.IsNullOrWhiteSpace(step.Id) ? $"step{i}" : step.Id!;

            switch (kind)
            {
                case "wait":
                {
                    var key = args.GetValueOrDefault("key") ?? args.GetValueOrDefault("reason") ?? "wait";
                    if (fixture.Resume is null || !fixture.Resume.TryGetValue(key, out var resumed) && !fixture.Resume.TryGetValue("value", out resumed))
                        return new WorkflowFixtureResult(fixture.Name, false, $"wait '{key}' had no resume value in fixture", values);
                    values[stepId] = resumed;
                    values[stepId + ".value"] = resumed;
                    if (!string.IsNullOrWhiteSpace(step.Out))
                        values[step.Out!] = resumed;
                    break;
                }
                case "set":
                {
                    var name = args.GetValueOrDefault("name") ?? args.GetValueOrDefault("key") ?? stepId;
                    var value = args.GetValueOrDefault("value") ?? ResolveRef(args.GetValueOrDefault("from") ?? "", values);
                    values[name] = value;
                    values[stepId] = value;
                    values[stepId + ".value"] = value;
                    if (!string.IsNullOrWhiteSpace(step.Out))
                        values[step.Out!] = value;
                    break;
                }
                case "use_tool":
                {
                    // Fixture simulation: record the resolved tool call; value comes from resume or a synthetic echo.
                    var tool = args.GetValueOrDefault("name") ?? args.GetValueOrDefault("tool") ?? "tool";
                    var zone = args.GetValueOrDefault("zone") ?? args.GetValueOrDefault("query") ?? "";
                    string result;
                    if (fixture.Resume is not null && fixture.Resume.TryGetValue(stepId, out var r))
                        result = r;
                    else if (fixture.Resume is not null && fixture.Resume.TryGetValue(tool, out r))
                        result = r;
                    else
                        result = $"{tool}:{zone}";
                    values[stepId] = result;
                    values[stepId + ".result"] = result;
                    if (!string.IsNullOrWhiteSpace(zone))
                        values[stepId + ".zone"] = zone;
                    if (!string.IsNullOrWhiteSpace(step.Out))
                        values[step.Out!] = result;
                    break;
                }
                case "say":
                case "format":
                {
                    var text = args.GetValueOrDefault("text") ?? "";
                    values[stepId] = text;
                    values[stepId + ".text"] = text;
                    if (!string.IsNullOrWhiteSpace(step.Out))
                        values[step.Out!] = text;
                    break;
                }
                case "retrieve":
                case "search":
                case "delegate":
                {
                    var q = args.GetValueOrDefault("query") ?? args.GetValueOrDefault("prompt") ?? args.GetValueOrDefault("text") ?? "";
                    var result = fixture.Resume?.GetValueOrDefault(stepId) ?? $"{kind}:{q}";
                    values[stepId] = result;
                    if (!string.IsNullOrWhiteSpace(step.Out))
                        values[step.Out!] = result;
                    break;
                }
                default:
                    return new WorkflowFixtureResult(fixture.Name, false, $"unsupported step kind '{kind}'", values);
            }
        }

        if (fixture.Expected is { Count: > 0 })
        {
            foreach (var (key, expected) in fixture.Expected)
            {
                var actual = values.GetValueOrDefault(key) ?? values.GetValueOrDefault("output." + key);
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    return new WorkflowFixtureResult(fixture.Name, false, $"expected {key}='{expected}' but got '{actual ?? "<missing>"}'", values);
            }
        }

        return new WorkflowFixtureResult(fixture.Name, true, "ok", values);
    }

    /// <summary>Run a promoted workflow against live inputs (fixture-style value flow).</summary>
    public static WorkflowFixtureResult Run(
        WorkflowDefinition def,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, string>? resume = null)
    {
        var fixture = new WorkflowFixture("live", inputs, Expected: null, Resume: resume);
        return EvaluateFixture(def, fixture);
    }

    private static Dictionary<string, string> ResolveArgs(IReadOnlyDictionary<string, string> args, IReadOnlyDictionary<string, string> values)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in args)
            resolved[k] = ResolveRef(v, values);
        return resolved;
    }

    private static string ResolveRef(string template, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(template)) return template;
        return ValueRef.Replace(template, m =>
        {
            var key = m.Groups[1].Value.Trim();
            return values.TryGetValue(key, out var v) ? v : m.Value;
        });
    }

    private static WorkflowDefinition WithMeta(WorkflowDefinition draft, string? taskId, bool tested) => new()
    {
        Name = draft.Name,
        Description = draft.Description,
        Version = draft.Version,
        Inputs = draft.Inputs,
        Outputs = draft.Outputs,
        Steps = draft.Steps,
        Fixtures = draft.Fixtures,
        BuiltBy = draft.BuiltBy,
        TaskId = taskId ?? draft.TaskId,
        Justification = draft.Justification,
        DraftedAt = draft.DraftedAt,
        TestedSha256 = tested ? draft.DefinitionSha256 : draft.TestedSha256,
        TestedAt = tested ? DateTimeOffset.UtcNow : draft.TestedAt,
    };
}
