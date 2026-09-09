using Relay.Core.Agents;
using Relay.Core.Config;
using Relay.Core.Execution;
using Relay.Core.Ledger;
using Relay.Core.Model;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.SelfChange;
using Relay.Core.Storage;

namespace Relay.Core.Tools;

/// <summary>
/// Everything about tools Relay builds for itself, bundled for the composition root: the store (drafts,
/// promoted), the runner (one sandboxed call), the builder (draft → test → retry) and the host functions.
/// Also the executor's <c>add_tool</c> operation: promotion is one change set, recorded like every other
/// change Relay makes to itself. Built with the same worker host as agent runs; without a worker host
/// there is no sandbox and therefore no building.
/// </summary>
public sealed class ToolRuntime : IToolOperations
{
    private readonly Func<DateTimeOffset> _clock;

    public ToolRuntime(DataRoot root, ChangeSetStore changes, IWorkerHost host, WorkerSettings settings, Func<IModelClient?> drafter, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Functions = new HostFunctions(_clock);
        Store = new ToolStore(root, changes);
        Runner = new ToolRunner(root, host, Functions, settings, _clock);
        Builder = new ToolBuilder(Store, Runner, drafter, _clock, () => AtomicFile.ReadAllTextIfExists(Path.Combine(root.PromptsDirectory, BuildPrompt.PromptName + ".md")));
    }

    public HostFunctions Functions { get; }
    public ToolStore Store { get; }
    public ToolRunner Runner { get; }
    public ToolBuilder Builder { get; }

    /// <summary>Whether the mind may use the build move: a sandbox exists (it does, here) and a model can draft.</summary>
    public bool CanBuild => Builder.CanDraft;

    /// <summary>The built-in read-only tools followed by every promoted tool, as the mind is told about them.</summary>
    public IReadOnlyList<ToolDescriptor> AllDescriptors() => [.. ToolBroker.Descriptors, .. Store.Descriptors()];

    public ExecutionResult Promote(Proposal proposal, Decision decision, string taskId, IExecutionSink sink)
    {
        var name = decision.NormalizedTarget.GetValueOrDefault("name") ?? "";
        var now = _clock();
        var result = Store.Promote(name, proposal.Reason, now, taskId, proposal.ProposalId);
        if (!result.Ok) return ExecutionResult.Fail(result.Error ?? "promotion failed");
        var set = result.ChangeSet!;
        var promoted = Store.Promoted(name);
        sink.Record(EventTypes.ChangeSetApplied, new { taskId, proposalId = proposal.ProposalId, changeSetId = set.ChangeSetId, kind = set.Kind, path = set.Path, key = name, afterSha256 = set.AfterSha256, acceptance = proposal.Target.GetValueOrDefault("acceptance") });
        sink.Record(EventTypes.ToolPromoted, new
        {
            taskId, proposalId = proposal.ProposalId, tool = name, changeSetId = set.ChangeSetId, path = set.Path,
            description = promoted?.Description, arguments = promoted?.Arguments.Select(a => a.Name), hostFunctions = promoted?.HostFunctionNames, sourceSha256 = promoted?.SourceSha256, tests = promoted?.Tests.Count, builtBy = promoted?.BuiltBy,
        });
        return ExecutionResult.Ok($"Tool '{name}' promoted; it is available to the mind now and can be reverted from the Relay panel (change set {set.ChangeSetId})",
            new Dictionary<string, string> { ["tool"] = name, ["changeSetId"] = set.ChangeSetId, ["path"] = set.Path });
    }
}
