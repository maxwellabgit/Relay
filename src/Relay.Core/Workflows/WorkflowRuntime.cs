using Relay.Core.Execution;
using Relay.Core.Ledger;
using Relay.Core.Policy;
using Relay.Core.SelfChange;
using Relay.Core.Storage;

namespace Relay.Core.Workflows;

/// <summary>
/// Everything about workflows Relay builds for itself, bundled for the composition root: the store
/// (drafts, promoted), the builder (validate → dry-run → mark tested), and the executor's
/// <c>add_workflow</c> operation. Promotion is one change set, recorded like every other change Relay
/// makes to itself. No worker sandbox is required — a workflow is data, not executable source.
/// </summary>
public sealed class WorkflowRuntime : IWorkflowOperations
{
    private readonly Func<DateTimeOffset> _clock;

    public WorkflowRuntime(DataRoot root, ChangeSetStore changes, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Store = new WorkflowStore(root, changes);
        Builder = new WorkflowBuilder(Store, _clock);
    }

    public WorkflowStore Store { get; }
    public WorkflowBuilder Builder { get; }

    public IReadOnlyList<WorkflowDescriptor> Descriptors() => Store.Descriptors();

    public ExecutionResult Promote(Proposal proposal, Decision decision, string taskId, IExecutionSink sink)
    {
        var name = decision.NormalizedTarget.GetValueOrDefault("name") ?? "";
        var now = _clock();
        var result = Store.Promote(name, proposal.Reason, now, taskId, proposal.ProposalId);
        if (!result.Ok) return ExecutionResult.Fail(result.Error ?? "promotion failed");
        var set = result.ChangeSet!;
        var promoted = Store.Promoted(name);
        sink.Record(EventTypes.ChangeSetApplied, new { taskId, proposalId = proposal.ProposalId, changeSetId = set.ChangeSetId, kind = set.Kind, path = set.Path, key = name, afterSha256 = set.AfterSha256, acceptance = proposal.Target.GetValueOrDefault("acceptance") });
        sink.Record(EventTypes.WorkflowPromoted, new
        {
            taskId, proposalId = proposal.ProposalId, workflow = name, changeSetId = set.ChangeSetId, path = set.Path,
            description = promoted?.Description, version = promoted?.Version, steps = promoted?.Steps.Count, definitionSha256 = promoted?.DefinitionSha256, builtBy = promoted?.BuiltBy,
        });
        return ExecutionResult.Ok($"Workflow '{name}' promoted; it is available to the mind now and can be reverted from the Relay panel (change set {set.ChangeSetId})",
            new Dictionary<string, string> { ["workflow"] = name, ["changeSetId"] = set.ChangeSetId, ["path"] = set.Path });
    }
}
