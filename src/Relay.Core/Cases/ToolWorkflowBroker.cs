using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.SelfChange;
using Relay.Core.Storage;
using Relay.Core.Tools;
using Relay.Core.Workflows;

namespace Relay.Core.Cases;

/// <summary>
/// Applies tool/workflow build and promote operations on the CaseRuntime path.
/// Reuses <see cref="ToolStore"/> / <see cref="WorkflowStore"/>; promotion is one change set.
/// </summary>
public sealed class ToolWorkflowBroker
{
    private readonly DataRoot _root;
    private readonly ObjectStore _objects;
    private readonly ToolServices _services;
    private readonly ChangeSetStore _changes;
    private readonly ToolStore _tools;
    private readonly WorkflowStore _workflows;
    private readonly HostFunctions _hostFunctions;
    private readonly Func<DateTimeOffset> _clock;

    public ToolWorkflowBroker(DataRoot root, ObjectStore objects, ToolServices services, Func<DateTimeOffset> clock)
    {
        _root = root;
        _objects = objects;
        _services = services;
        _clock = clock;
        _changes = new ChangeSetStore(root);
        _tools = new ToolStore(root, _changes);
        _workflows = new WorkflowStore(root, _changes);
        _hostFunctions = new HostFunctions(clock);
    }

    public ToolStore Tools => _tools;
    public WorkflowStore Workflows => _workflows;
    public ChangeSetStore Changes => _changes;
    public HostFunctions HostFunctions => _hostFunctions;
    public bool CanBuild => _services.CanBuild;
    public bool CanRun => _services.CanRunPromoted;

    public bool Handles(string capability)
        => capability is ToolCapabilities.PromoteTool
            or ToolCapabilities.RevertTool
            or ToolCapabilities.PromoteWorkflow
            or ToolCapabilities.RevertWorkflow;

    public async Task<OperationApplyResult> ApplyAsync(OperationEnvelope envelope, CancellationToken cancellationToken = default)
    {
        return envelope.Capability switch
        {
            ToolCapabilities.PromoteTool => await PromoteToolAsync(envelope, cancellationToken).ConfigureAwait(false),
            ToolCapabilities.RevertTool => RevertChange(envelope, ChangeKinds.Tool),
            ToolCapabilities.PromoteWorkflow => PromoteWorkflow(envelope),
            ToolCapabilities.RevertWorkflow => RevertChange(envelope, ChangeKinds.Workflow),
            _ => new OperationApplyResult(false, "Not a tool/workflow capability.", Error: envelope.Capability),
        };
    }

    /// <summary>
    /// Stage 1 generalize + stage 2 draft + sandbox tests. Does not promote.
    /// Returns a tested draft ready for a promote approval card (name + manifest).
    /// </summary>
    public async Task<ToolBuildResult> BuildToolAsync(
        string proposedName,
        string ask,
        string justification,
        string inputs,
        string outputs,
        string? taskId,
        CancellationToken cancellationToken = default)
    {
        if (_services.Drafter is null || _services.Runner is null)
            return ToolBuildResult.Fail("Tool build requires a drafter and a package runner.");

        var generalization = ToolGeneralizer.FromAsk(ask, proposedName, justification, inputs, outputs);
        // App validates name / conflicts (drafter may propose world_clock).
        var name = NormalizeName(generalization.ProposedName);
        if (!ToolPackage.ValidName(name))
            return ToolBuildResult.Fail($"Proposed name '{name}' is not a valid snake_case tool name.");
        if (_tools.ReservedNames().Contains(name, StringComparer.Ordinal))
            return ToolBuildResult.Fail($"A tool named '{name}' already exists.");

        var drafted = await _services.Drafter.DraftAsync(generalization, name, cancellationToken).ConfigureAwait(false);
        // Force the validated name even if the drafter suggested something else.
        drafted = ClonePackage(drafted, name, generalization.Justification, taskId, _clock());

        var problems = drafted.Validate(_tools.ReservedNames());
        if (problems.Count > 0)
            return ToolBuildResult.Fail("Draft failed validation: " + string.Join("; ", problems), generalization, drafted);

        // Reject drafts whose only tests reuse the original ask city.
        if (!HasNonOriginalTests(drafted, ask))
            return ToolBuildResult.Fail("Draft tests must include examples not taken from the original request.", generalization, drafted);

        var report = await TestPackageAsync(drafted, taskId, cancellationToken).ConfigureAwait(false);
        if (!report.Passed)
            return ToolBuildResult.Fail(report.Summary, generalization, report.Package, report);

        _tools.SaveDraft(report.Package);
        return ToolBuildResult.Succeed(generalization, report.Package, report);
    }

    public async Task<ToolRunResult> RunPromotedAsync(
        string name,
        IReadOnlyDictionary<string, string> args,
        string purpose,
        string? taskId,
        CancellationToken cancellationToken = default)
    {
        if (_services.Runner is null)
            return new ToolRunResult(false, null, "No tool package runner is bound.", "", 0, 0, 0, [], []);
        var tool = _tools.Promoted(name);
        if (tool is null)
            return new ToolRunResult(false, null, $"No promoted tool named '{name}'.", "", 0, 0, 0, [], []);
        return await _services.Runner.RunAsync(tool, args, purpose, taskId, ToolRunner.DefaultCallTimeoutSeconds, at: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public WorkflowTestReport TestWorkflow(WorkflowDefinition draft, string? taskId = null)
    {
        var builder = new WorkflowBuilder(_workflows, _clock);
        return builder.TestWithFixtures(draft, taskId);
    }

    private async Task<OperationApplyResult> PromoteToolAsync(OperationEnvelope envelope, CancellationToken cancellationToken)
    {
        var name = ReqString(envelope, "name");
        var reason = OptString(envelope, "reason") ?? OptString(envelope, "justification") ?? $"Promote tool '{name}'";
        var draft = _tools.Draft(name);
        if (draft is null)
            return new OperationApplyResult(false, $"No draft tool named '{name}'.", Error: "missing_draft");
        if (!draft.Tested)
        {
            // Re-test at promote time if somehow unmarked (defense in depth).
            var report = await TestPackageAsync(draft, envelope.CaseId, cancellationToken).ConfigureAwait(false);
            if (!report.Passed)
                return new OperationApplyResult(false, report.Summary, Error: "tests_failed");
            _tools.SaveDraft(report.Package);
        }

        var result = _tools.Promote(name, reason, _clock(), envelope.CaseId, envelope.OperationId);
        if (!result.Ok)
            return new OperationApplyResult(false, result.Error ?? "promote failed", Error: result.Error);

        var promoted = _tools.Promoted(name)!;
        var blob = _objects.PutJson(new
        {
            tool = name,
            changeSetId = result.ChangeSet!.ChangeSetId,
            path = result.ChangeSet.Path,
            description = promoted.Description,
            arguments = promoted.Arguments.Select(a => a.Name).ToList(),
            hostFunctions = promoted.HostFunctionNames,
            tests = promoted.Tests.Count,
            sourceSha256 = promoted.SourceSha256,
            manifest = new
            {
                promoted.Name,
                promoted.Description,
                arguments = promoted.Arguments,
                hostFunctions = promoted.HostFunctionNames,
                testZones = promoted.Tests.Select(t => t.Args.GetValueOrDefault("zone")).Where(z => z is not null).ToList(),
            },
        });
        return new OperationApplyResult(true, $"Tool '{name}' promoted (change set {result.ChangeSet.ChangeSetId}).", blob.ObjectId);
    }

    private OperationApplyResult PromoteWorkflow(OperationEnvelope envelope)
    {
        var name = ReqString(envelope, "name");
        var reason = OptString(envelope, "reason") ?? $"Promote workflow '{name}'";
        var draft = _workflows.Draft(name);
        if (draft is null)
            return new OperationApplyResult(false, $"No draft workflow named '{name}'.", Error: "missing_draft");
        if (!draft.Tested)
            return new OperationApplyResult(false, $"Draft '{name}' has not passed fixture evaluation.", Error: "untested");

        var result = _workflows.Promote(name, reason, _clock(), envelope.CaseId, envelope.OperationId);
        if (!result.Ok)
            return new OperationApplyResult(false, result.Error ?? "promote failed", Error: result.Error);

        var promoted = _workflows.Promoted(name)!;
        var blob = _objects.PutJson(new
        {
            workflow = name,
            changeSetId = result.ChangeSet!.ChangeSetId,
            permissions = promoted.PermissionUnion,
            version = promoted.Version,
            steps = promoted.Steps.Count,
        });
        return new OperationApplyResult(true, $"Workflow '{name}' promoted (change set {result.ChangeSet.ChangeSetId}).", blob.ObjectId);
    }

    private OperationApplyResult RevertChange(OperationEnvelope envelope, string expectedKind)
    {
        var changeSetId = ReqString(envelope, "changeSetId");
        var reason = OptString(envelope, "reason") ?? "reverted";
        var set = _changes.Read(changeSetId);
        if (set is null)
            return new OperationApplyResult(false, $"No change set {changeSetId}.", Error: "missing");
        if (!string.Equals(set.Kind, expectedKind, StringComparison.Ordinal))
            return new OperationApplyResult(false, $"Change set kind is '{set.Kind}', expected '{expectedKind}'.", Error: "kind_mismatch");

        var result = _changes.Revert(changeSetId, reason, _clock());
        if (!result.Ok)
            return new OperationApplyResult(false, result.Error ?? "revert failed", Error: result.Error);

        var blob = _objects.PutJson(new { changeSetId, kind = expectedKind, reverted = true, path = set.Path });
        return new OperationApplyResult(true, $"Reverted change set {changeSetId}.", blob.ObjectId);
    }

    private async Task<ToolTestReport> TestPackageAsync(ToolPackage package, string? taskId, CancellationToken cancellationToken)
    {
        var outcomes = new List<ToolTestOutcome>();
        for (var i = 0; i < package.Tests.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var test = package.Tests[i];
            var run = await _services.Runner!.RunAsync(
                package, test.Args, $"test {i + 1} of {package.Name}", taskId,
                ToolRunner.DefaultCallTimeoutSeconds, ToolBuilder.TestInstant, cancellationToken).ConfigureAwait(false);
            if (!run.Ok)
            {
                outcomes.Add(new ToolTestOutcome(i + 1, false, run.Error ?? "failed", run.RunId, run.ElapsedMs));
                continue;
            }
            var problem = ToolPackage.Check(test, run.ResultJson!);
            outcomes.Add(problem is null
                ? new ToolTestOutcome(i + 1, true, "ok", run.RunId, run.ElapsedMs, Clip(run.ResultJson!, 200))
                : new ToolTestOutcome(i + 1, false, problem, run.RunId, run.ElapsedMs, Clip(run.ResultJson!, 200)));
        }
        var passed = outcomes.Count > 0 && outcomes.All(o => o.Passed);
        var tested = passed
            ? ClonePackage(package, package.Name, package.Justification, package.TaskId, package.DraftedAt ?? _clock(), testedSha: package.SourceSha256, testedAt: _clock())
            : package;
        return new ToolTestReport(passed, outcomes, tested);
    }

    private static bool HasNonOriginalTests(ToolPackage package, string ask)
    {
        foreach (var t in package.Tests)
        {
            foreach (var v in t.Args.Values)
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                var city = v.Contains('/') ? v[(v.LastIndexOf('/') + 1)..] : v;
                if (!ask.Contains(city, StringComparison.OrdinalIgnoreCase)
                    && !ask.Contains(v, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string NormalizeName(string name)
        => (name ?? "").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    private static ToolPackage ClonePackage(
        ToolPackage p, string name, string? justification, string? taskId, DateTimeOffset draftedAt,
        string? testedSha = null, DateTimeOffset? testedAt = null) => new()
    {
        Name = name,
        Description = p.Description,
        Arguments = p.Arguments,
        HostFunctionNames = p.HostFunctionNames,
        Source = p.Source,
        Tests = p.Tests,
        BuiltBy = p.BuiltBy,
        TaskId = taskId ?? p.TaskId,
        Justification = justification ?? p.Justification,
        DraftedAt = draftedAt,
        TestedSha256 = testedSha ?? p.TestedSha256,
        TestedAt = testedAt ?? p.TestedAt,
        PromotedAt = p.PromotedAt,
    };

    private static string ReqString(OperationEnvelope envelope, string key)
    {
        if (!envelope.Arguments.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(el.GetString()))
            throw new InvalidOperationException($"Missing argument '{key}'.");
        return el.GetString()!;
    }

    private static string? OptString(OperationEnvelope envelope, string key)
    {
        if (!envelope.Arguments.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String) return null;
        return el.GetString();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

public sealed record ToolBuildResult(
    bool Ok,
    string Summary,
    ToolGeneralization? Generalization,
    ToolPackage? Package,
    ToolTestReport? Tests)
{
    public static ToolBuildResult Succeed(ToolGeneralization g, ToolPackage p, ToolTestReport t)
        => new(true, t.Summary, g, p, t);
    public static ToolBuildResult Fail(string summary, ToolGeneralization? g = null, ToolPackage? p = null, ToolTestReport? t = null)
        => new(false, summary, g, p, t);
}
