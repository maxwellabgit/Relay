using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Config;
using Relay.Core.Projects;
using Relay.Core.Storage;

namespace Relay.Core.Agents;

public sealed record AgentInput(
    [property: JsonPropertyName("path")] string Path,          // relative to the staging folder, always under inputs/
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("source")] string Source);     // the canonical file it was copied from

public sealed record AgentLimits(
    [property: JsonPropertyName("wallClockSeconds")] int WallClockSeconds,
    [property: JsonPropertyName("maxReadBytes")] long MaxReadBytes,
    [property: JsonPropertyName("maxWriteBytes")] long MaxWriteBytes,
    [property: JsonPropertyName("maxToolCalls")] int MaxToolCalls,
    [property: JsonPropertyName("maxMemoryBytes")] long MaxMemoryBytes);

/// <summary>
/// Everything a worker run is allowed to be, fixed before launch and written beside the run
/// (contract §8). Inputs are copies, never links; the allowlists are the only things the broker
/// consults; the limits are enforced by the runtime (wall clock, calls, bytes) and the host (memory, processes).
/// </summary>
public sealed class AgentRunSpec
{
    public const string InputsFolder = "inputs";
    public const string OutFolder = "out";
    public static readonly string[] DefaultTools = ["list_dir", "read_file", "write_file"];

    [JsonPropertyName("runId")] public required string RunId { get; init; }
    [JsonPropertyName("proposalId")] public required string ProposalId { get; init; }
    [JsonPropertyName("turnId")] public required string TurnId { get; init; }
    [JsonPropertyName("projectId")] public required string ProjectId { get; init; }
    [JsonPropertyName("projectSlug")] public required string ProjectSlug { get; init; }
    [JsonPropertyName("task")] public required string Task { get; init; }
    [JsonPropertyName("objective")] public required string Objective { get; init; }
    [JsonPropertyName("inputs")] public required IReadOnlyList<AgentInput> Inputs { get; init; }
    [JsonPropertyName("readAllow")] public IReadOnlyList<string> ReadAllow { get; init; } = [InputsFolder + "/**"];
    [JsonPropertyName("writeAllow")] public IReadOnlyList<string> WriteAllow { get; init; } = [OutFolder + "/**"];
    [JsonPropertyName("toolAllowlist")] public IReadOnlyList<string> ToolAllowlist { get; init; } = DefaultTools;
    [JsonPropertyName("network")] public bool Network { get; init; }
    [JsonPropertyName("limits")] public required AgentLimits Limits { get; init; }
    [JsonPropertyName("stagingPath")] public required string StagingPath { get; init; }
    [JsonPropertyName("requiredOutputs")] public required IReadOnlyList<string> RequiredOutputs { get; init; }
    [JsonPropertyName("createdAt")] public required DateTimeOffset CreatedAt { get; init; }

    [JsonIgnore] public string SpecPath => System.IO.Path.Combine(StagingPath, "run.json");
    [JsonIgnore] public string OutPath => System.IO.Path.Combine(StagingPath, OutFolder);
    [JsonIgnore] public string InputsPath => System.IO.Path.Combine(StagingPath, InputsFolder);

    /// <summary>Builds the staging folder: read-only copies of the project's notes under inputs/, an empty out/, and run.json.</summary>
    public static AgentRunSpec Prepare(DataRoot root, ProjectRecord project, string runId, string proposalId, string turnId, string task, string objective, WorkerSettings settings, DateTimeOffset now)
    {
        var staging = System.IO.Path.Combine(root.AgentsDirectory, runId);
        Directory.CreateDirectory(System.IO.Path.Combine(staging, InputsFolder));
        Directory.CreateDirectory(System.IO.Path.Combine(staging, OutFolder));
        Directory.CreateDirectory(System.IO.Path.Combine(staging, "tmp"));

        var inputs = new List<AgentInput>();
        foreach (var folder in new[] { "notes", "decisions", "tasks", "conversations" })
        {
            var dir = System.IO.Path.Combine(project.RootPath, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal))
            {
                var relative = $"{InputsFolder}/{folder}/{System.IO.Path.GetFileName(file)}";
                var destination = System.IO.Path.Combine(staging, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                var bytes = File.ReadAllBytes(file);
                File.WriteAllBytes(destination, bytes);
                File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
                inputs.Add(new AgentInput(relative, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength, file));
            }
        }

        var spec = new AgentRunSpec
        {
            RunId = runId,
            ProposalId = proposalId,
            TurnId = turnId,
            ProjectId = project.Id,
            ProjectSlug = project.Slug,
            Task = task,
            Objective = objective,
            Inputs = inputs,
            Limits = new AgentLimits(settings.WallClockSeconds, settings.MaxReadBytes, settings.MaxWriteBytes, settings.MaxToolCalls, settings.MemoryMb * 1024L * 1024L),
            StagingPath = staging,
            RequiredOutputs = task == "summarize" ? [OutFolder + "/summary.md"] : [],
            CreatedAt = now,
        };
        AtomicFile.WriteAllText(spec.SpecPath, JsonSerializer.Serialize(spec, RelayJson.Indented));
        return spec;
    }

    public static AgentRunSpec? Load(string stagingPath)
    {
        var path = System.IO.Path.Combine(stagingPath, "run.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<AgentRunSpec>(File.ReadAllText(path), RelayJson.Indented); }
        catch (JsonException) { return null; }
    }

    /// <summary>Inputs are read-only copies; if any changed during the run the worker escaped the broker and the run is invalid.</summary>
    public IReadOnlyList<string> VerifyInputsUnchanged()
    {
        var problems = new List<string>();
        foreach (var input in Inputs)
        {
            var path = System.IO.Path.Combine(StagingPath, input.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { problems.Add($"{input.Path} was removed"); continue; }
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            if (hash != input.Sha256) problems.Add($"{input.Path} was modified");
        }
        return problems;
    }

    /// <summary>The message the worker receives first. It carries no paths outside staging and nothing about Relay.</summary>
    public string ToSpecMessage() => JsonSerializer.Serialize(new
    {
        type = "spec",
        runId = RunId,
        task = Task,
        objective = Objective,
        projectSlug = ProjectSlug,
        inputs = Inputs.Select(i => i.Path),
        requiredOutputs = RequiredOutputs,
        limits = new { Limits.WallClockSeconds, Limits.MaxToolCalls, Limits.MaxReadBytes, Limits.MaxWriteBytes },
    }, RelayJson.Compact);
}

/// <summary>Written beside run.json when the run ends, so later turns (apply) and Review can see what happened without the ledger.</summary>
public sealed record AgentRunStatus(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("task")] string Task,
    [property: JsonPropertyName("state")] string State,            // launched | completed | terminated | failed
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("outputs")] IReadOnlyList<string> Outputs,
    [property: JsonPropertyName("toolCalls")] int ToolCalls,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("endedAt")] DateTimeOffset? EndedAt,
    [property: JsonPropertyName("applied")] bool Applied = false)
{
    public static string PathFor(string stagingPath) => System.IO.Path.Combine(stagingPath, "status.json");

    public void Save(string stagingPath) => AtomicFile.WriteAllText(PathFor(stagingPath), JsonSerializer.Serialize(this, RelayJson.Indented));

    public static AgentRunStatus? Load(string stagingPath)
    {
        var path = PathFor(stagingPath);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<AgentRunStatus>(File.ReadAllText(path), RelayJson.Indented); }
        catch (JsonException) { return null; }
    }

    /// <summary>All runs under staging\agents, newest first.</summary>
    public static IReadOnlyList<AgentRunStatus> All(DataRoot root)
    {
        if (!Directory.Exists(root.AgentsDirectory)) return [];
        return Directory.EnumerateDirectories(root.AgentsDirectory)
            .Select(Load).Where(s => s is not null).Select(s => s!)
            .OrderByDescending(s => s.StartedAt).ToList();
    }
}
