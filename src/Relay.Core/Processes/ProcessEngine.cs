using System.Text.Json;
using Relay.Core.Ids;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Processes;

public sealed class ProcessValidationResult
{
    public bool Ok { get; init; }
    public List<string> Errors { get; init; } = [];
}

/// <summary>
/// Validates process graphs (node types, refs, cycles) and persists run position/outputs.
/// Retry edges require explicit bounds.
/// </summary>
public sealed class ProcessEngine
{
    private readonly DataRoot _root;
    private readonly IClock _clock;

    public ProcessEngine(DataRoot root, IClock clock)
    {
        _root = root;
        _clock = clock;
    }

    public string RunsDirectory => Path.Combine(_root.Path, "workflows", "runs");

    public ProcessValidationResult Validate(ProcessDefinition def)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(def.Id)) errors.Add("missing_id");
        if (string.IsNullOrWhiteSpace(def.Entry)) errors.Add("missing_entry");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in def.Nodes)
        {
            if (!ids.Add(node.Id)) errors.Add("duplicate_node:" + node.Id);
            if (!ProcessNodeTypes.All.Contains(node.Type))
                errors.Add("unknown_node_type:" + node.Type);
        }

        if (!ids.Contains(def.Entry)) errors.Add("entry_missing:" + def.Entry);

        foreach (var edge in def.Edges)
        {
            if (!ids.Contains(edge.From)) errors.Add("edge_from_missing:" + edge.From);
            if (!ids.Contains(edge.To)) errors.Add("edge_to_missing:" + edge.To);
            if (edge.Retry is not null && edge.Retry.MaxAttempts < 1)
                errors.Add("retry_unbounded:" + edge.From + "->" + edge.To);
        }

        var adj = def.Edges
            .Where(e => e.Retry is null)
            .GroupBy(e => e.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.To).ToList(), StringComparer.Ordinal);

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Dfs(string n)
        {
            if (!visiting.Add(n)) { errors.Add("cycle:" + n); return true; }
            if (!visited.Add(n)) { visiting.Remove(n); return false; }
            if (adj.TryGetValue(n, out var next))
            {
                foreach (var t in next)
                    if (Dfs(t)) { visiting.Remove(n); return true; }
            }
            visiting.Remove(n);
            return false;
        }
        foreach (var n in ids)
            Dfs(n);

        foreach (var node in def.Nodes.Where(n => n.Type != ProcessNodeTypes.Complete))
        {
            if (def.Edges.All(e => e.From != node.Id))
                errors.Add("dangling_node:" + node.Id);
        }

        return new ProcessValidationResult { Ok = errors.Count == 0, Errors = errors };
    }

    public ProcessRunState Start(ProcessDefinition def, string? caseId = null)
    {
        var validation = Validate(def);
        if (!validation.Ok)
            throw new InvalidOperationException("invalid_workflow:" + string.Join(",", validation.Errors));

        var run = new ProcessRunState
        {
            RunId = Ulid.NewUlid(_clock.UtcNow),
            WorkflowId = def.Id,
            WorkflowVersion = def.Version,
            CaseId = caseId,
            CurrentNodeId = def.Entry,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        };
        Save(run);
        return run;
    }

    public ProcessRunState Advance(ProcessDefinition def, ProcessRunState run, string? nextNodeId = null, JsonElement? output = null)
    {
        if (run.CurrentNodeId is null)
            throw new InvalidOperationException("run_complete");

        if (output is not null)
            run.NodeOutputs[run.CurrentNodeId] = output.Value;

        var current = run.CurrentNodeId;
        string? dest = nextNodeId;
        if (dest is null)
        {
            var edges = def.Edges.Where(e => e.From == current).ToList();
            var plain = edges.FirstOrDefault(e => e.Retry is null && e.From != e.To)
                        ?? edges.FirstOrDefault(e => e.From != e.To);
            dest = plain?.To;
        }

        if (dest is null)
        {
            run.Status = "blocked";
            run.BlockReason = "no_edge";
            Save(run);
            return run;
        }

        var node = def.Nodes.First(n => n.Id == dest);
        run.CurrentNodeId = dest;
        run.UpdatedAt = _clock.UtcNow;
        if (node.Type == ProcessNodeTypes.Complete)
            run.Status = "completed";
        Save(run);
        return run;
    }

    public bool TryRetry(ProcessDefinition def, ProcessRunState run, string fromNodeId)
    {
        var edge = def.Edges.FirstOrDefault(e => e.From == fromNodeId && e.Retry is not null);
        if (edge?.Retry is null) return false;
        var key = fromNodeId + "->" + edge.To;
        run.RetryCounts.TryGetValue(key, out var count);
        if (count >= edge.Retry.MaxAttempts) return false;
        run.RetryCounts[key] = count + 1;
        run.CurrentNodeId = edge.To;
        run.Status = "active";
        run.UpdatedAt = _clock.UtcNow;
        Save(run);
        return true;
    }

    public void Save(ProcessRunState run)
    {
        Directory.CreateDirectory(RunsDirectory);
        AtomicFile.WriteAllText(
            Path.Combine(RunsDirectory, run.RunId + ".json"),
            JsonSerializer.Serialize(run, RelayJson.Indented));
    }

    public ProcessRunState? TryLoad(string runId)
    {
        var text = AtomicFile.ReadAllTextIfExists(Path.Combine(RunsDirectory, runId + ".json"));
        return text is null ? null : JsonSerializer.Deserialize<ProcessRunState>(text, RelayJson.Indented);
    }
}
