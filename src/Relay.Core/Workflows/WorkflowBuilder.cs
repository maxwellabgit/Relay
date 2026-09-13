namespace Relay.Core.Workflows;

/// <summary>Result of validating and dry-running a draft workflow before promotion.</summary>
public sealed record WorkflowTestReport(bool Passed, WorkflowDefinition Package, string Summary, IReadOnlyList<string> Problems);

/// <summary>
/// Thin twin of the tool builder for workflows: accept a hand-authored (or mind-drafted) definition,
/// validate its shape, dry-run the steps with no side effects, and mark <see cref="WorkflowDefinition.TestedAt"/>
/// when the dry-run passes. There is no sandbox language — steps are data the TaskLoop already understands.
/// </summary>
public sealed class WorkflowBuilder
{
    private readonly WorkflowStore _store;
    private readonly Func<DateTimeOffset> _clock;

    public WorkflowBuilder(WorkflowStore store, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public WorkflowStore Store => _store;

    /// <summary>
    /// Validates the definition, dry-runs each step (structural only — no tools, network or proposals),
    /// and when it passes writes the draft with <c>testedSha256</c> / <c>testedAt</c> set.
    /// </summary>
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

        // Dry-run: each step is already shape-checked by Validate; nothing reaches the machine.
        var now = _clock();
        var tested = new WorkflowDefinition
        {
            Name = draft.Name, Description = draft.Description, Version = draft.Version, Steps = draft.Steps,
            BuiltBy = draft.BuiltBy ?? "hand", TaskId = taskId ?? draft.TaskId, Justification = draft.Justification,
            DraftedAt = draft.DraftedAt ?? now, TestedSha256 = draft.DefinitionSha256, TestedAt = now,
        };
        _store.SaveDraft(tested);
        var kinds = string.Join(" → ", tested.Steps.Select(s => s.Kind));
        return new WorkflowTestReport(true, tested, $"{tested.Steps.Count} step(s) dry-ran cleanly ({kinds}).", []);
    }

    private static WorkflowDefinition WithMeta(WorkflowDefinition draft, string? taskId, bool tested) => new()
    {
        Name = draft.Name, Description = draft.Description, Version = draft.Version, Steps = draft.Steps,
        BuiltBy = draft.BuiltBy, TaskId = taskId ?? draft.TaskId, Justification = draft.Justification,
        DraftedAt = draft.DraftedAt, TestedSha256 = tested ? draft.DefinitionSha256 : draft.TestedSha256, TestedAt = tested ? DateTimeOffset.UtcNow : draft.TestedAt,
    };
}
