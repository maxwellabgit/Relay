using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Workflows;

namespace Relay.Core.Session;

/// <summary>
/// Minimal workflow run path (Alpha Step 4): the mind's <c>run_workflow</c> move expands a promoted
/// definition's steps into the same observations the loop already understands (use_tool / retrieve /
/// search / delegate / say·format). Waits for approval or a delegate go through TaskLoop like any other move.
/// </summary>
public sealed partial class SessionCoordinator
{
    /// <summary>After add_workflow executed: the mind's workflow list gains the definition now.</summary>
    private WorkflowObserved WorkflowPromoted(TaskState task, string name)
    {
        var workflows = _services.Workflows!;
        if (task.Loop is not null) task.Loop.Context.Workflows = workflows.Descriptors();
        var def = workflows.Store.Promoted(name);
        Append(EventTypes.TurnProgress, new { taskId = task.TaskId, text = $"Added the workflow {name}" });
        var detail = def is null
            ? $"The workflow '{name}' is available now: run_workflow {name}."
            : $"The workflow is available now: run_workflow {name} — {def.Description} [{string.Join(" → ", def.Steps.Select(s => s.Kind))}].";
        return new WorkflowObserved(_clock.UtcNow, name, WorkflowObserved.Promoted, detail);
    }

    private MoveOutcome MindRunWorkflow(TaskState task, ToolBroker tools, RunWorkflowMove move)
    {
        var now = _clock.UtcNow;
        var runtime = _services.Workflows;
        if (runtime is null)
            return MoveOutcome.Of(new WorkflowObserved(now, move.Name, WorkflowObserved.Unavailable, "Workflows are not configured in this build."));
        var def = runtime.Store.Promoted(move.Name);
        if (def is null)
            return MoveOutcome.Of(new WorkflowObserved(now, move.Name, WorkflowObserved.Failed, $"There is no promoted workflow named '{move.Name}'."));

        var observations = new List<Observation>
        {
            new WorkflowObserved(now, move.Name, WorkflowObserved.Started, $"Running {def.Steps.Count} step(s): {string.Join(" → ", def.Steps.Select(s => s.Kind))}"),
        };
        Append(EventTypes.WorkflowRan, new { taskId = task.TaskId, workflow = move.Name, steps = def.Steps.Count, version = def.Version, definitionSha256 = def.DefinitionSha256 });

        for (var i = 0; i < def.Steps.Count; i++)
        {
            var step = def.Steps[i];
            var kind = (step.Kind ?? "").Trim().ToLowerInvariant().Replace('-', '_');
            var args = MergeArgs(step.Args, move.Args);
            observations.Add(new WorkflowObserved(now, move.Name, WorkflowObserved.Step, $"{kind}", i));

            MoveOutcome stepOutcome = kind switch
            {
                "use_tool" => ExpandUseTool(task, tools, args),
                "retrieve" => MindUseTool(task, tools, new UseToolMove("search", ToolArgs(args, "query", "project", "limit", "exclude"))),
                "search" => MindUseTool(task, tools, new UseToolMove("web_search", ToolArgs(args, "query", "limit"))),
                "say" or "format" => MoveOutcome.Of(new SystemObserved(now, args.GetValueOrDefault("text") ?? "")),
                "delegate" => ExpandDelegate(task, args),
                _ => MoveOutcome.Of(new WorkflowObserved(now, move.Name, WorkflowObserved.Failed, $"Unknown step kind '{step.Kind}'.")),
            };
            observations.AddRange(stepOutcome.Observations);
            if (stepOutcome.WaitFor is not null)
            {
                Append(EventTypes.TurnProgress, new { taskId = task.TaskId, text = $"Workflow {move.Name} waiting at step {i + 1}" });
                return new MoveOutcome(observations, stepOutcome.WaitFor);
            }
            if (stepOutcome.Observations.OfType<WorkflowObserved>().Any(o => o.Stage == WorkflowObserved.Failed))
                return new MoveOutcome(observations);
        }

        observations.Add(new WorkflowObserved(now, move.Name, WorkflowObserved.Finished, $"Completed all {def.Steps.Count} step(s)."));
        Append(EventTypes.TurnProgress, new { taskId = task.TaskId, text = $"Finished workflow {move.Name}" });
        return new MoveOutcome(observations);
    }

    private MoveOutcome ExpandUseTool(TaskState task, ToolBroker tools, IReadOnlyDictionary<string, string> args)
    {
        var tool = (args.GetValueOrDefault("name") ?? args.GetValueOrDefault("tool") ?? "").Trim();
        if (tool.Length == 0)
            return MoveOutcome.Of(new WorkflowObserved(_clock.UtcNow, "", WorkflowObserved.Failed, "use_tool step needs args.name"));
        var callArgs = args.Where(kv => kv.Key is not ("name" or "tool"))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return MindUseTool(task, tools, new UseToolMove(MoveSchema.ToolName(tool), callArgs));
    }

    private MoveOutcome ExpandDelegate(TaskState task, IReadOnlyDictionary<string, string> args)
    {
        var profile = (args.GetValueOrDefault("profile") ?? "").Trim();
        var prompt = (args.GetValueOrDefault("prompt") ?? args.GetValueOrDefault("objective") ?? args.GetValueOrDefault("text") ?? "").Trim();
        if (profile.Length == 0 || prompt.Length == 0)
            return MoveOutcome.Of(new WorkflowObserved(_clock.UtcNow, "", WorkflowObserved.Failed, "delegate step needs args.profile and args.prompt"));
        var refs = (args.GetValueOrDefault("refs") ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var budget = int.TryParse(args.GetValueOrDefault("budget_tokens") ?? args.GetValueOrDefault("budget"), out var b) ? b : MoveSchema.DefaultDelegateBudget;
        var search = string.Equals(args.GetValueOrDefault("allow_search"), "true", StringComparison.OrdinalIgnoreCase);
        if (task.Loop is null)
            return MoveOutcome.Of(new WorkflowObserved(_clock.UtcNow, "", WorkflowObserved.Failed, "No task loop to delegate from."));
        return MindDelegate(task, task.Loop, new DelegateMove(profile, prompt, refs, budget, search));
    }

    private static IReadOnlyDictionary<string, string> MergeArgs(IReadOnlyDictionary<string, string>? step, IReadOnlyDictionary<string, string> run)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (step is not null) foreach (var (k, v) in step) merged[k] = v;
        // Run-time args overlay step defaults (e.g. query from the ask).
        foreach (var (k, v) in run) if (k is not ("name" or "tool")) merged[k] = v;
        return merged;
    }

    private static Dictionary<string, string> ToolArgs(IReadOnlyDictionary<string, string> args, params string[] keys)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
            if (args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) result[key] = value;
        return result;
    }
}
