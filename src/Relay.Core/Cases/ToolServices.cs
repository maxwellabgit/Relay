using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Tools;

namespace Relay.Core.Cases;

/// <summary>Runs a <see cref="ToolPackage"/> the same way the worker sandbox would (host functions only).</summary>
public interface IToolPackageRunner
{
    Task<ToolRunResult> RunAsync(
        ToolPackage tool,
        IReadOnlyDictionary<string, string> args,
        string purpose,
        string? taskId,
        int timeoutSeconds,
        DateTimeOffset? at,
        CancellationToken cancellationToken);
}

/// <summary>Drafts a tool package from a generalization (scripted or model-backed).</summary>
public interface IToolDrafter
{
    string Name { get; }
    Task<ToolPackage> DraftAsync(ToolGeneralization generalization, string proposedName, CancellationToken cancellationToken);
}

/// <summary>
/// Stage 1 of tool build: stable purpose, variable inputs/outputs, required host capabilities,
/// and counterexamples that were not in the original ask.
/// </summary>
public sealed class ToolGeneralization
{
    [JsonPropertyName("purpose")] public required string Purpose { get; init; }
    [JsonPropertyName("variableInputs")] public IReadOnlyList<string> VariableInputs { get; init; } = [];
    [JsonPropertyName("outputs")] public IReadOnlyList<string> Outputs { get; init; } = [];
    [JsonPropertyName("requiredHostCapabilities")] public IReadOnlyList<string> RequiredHostCapabilities { get; init; } = [];
    [JsonPropertyName("counterexamples")] public IReadOnlyList<string> Counterexamples { get; init; } = [];
    [JsonPropertyName("originalAsk")] public string OriginalAsk { get; init; } = "";
    [JsonPropertyName("proposedName")] public string ProposedName { get; init; } = "";
    [JsonPropertyName("justification")] public string Justification { get; init; } = "";
}

/// <summary>Injectable tool/workflow adapters for <see cref="CaseRuntime"/>.</summary>
public sealed class ToolServices
{
    public IToolPackageRunner? Runner { get; init; }
    public IToolDrafter? Drafter { get; init; }
    public Func<DateTimeOffset>? Clock { get; init; }

    public bool CanBuild => Runner is not null && Drafter is not null;
    public bool CanRunPromoted => Runner is not null;
}

/// <summary>Operation capabilities for tool and workflow build on the CaseRuntime path.</summary>
public static class ToolCapabilities
{
    public const string PromoteTool = "tool.promote";
    public const string RevertTool = "tool.revert";
    public const string PromoteWorkflow = "workflow.promote";
    public const string RevertWorkflow = "workflow.revert";
}

/// <summary>Scripted drafter that always returns a generalized world_clock package.</summary>
public sealed class WorldClockToolDrafter : IToolDrafter
{
    public const string Source = """
        function run(args) {
          var zone = String(args.zone || "").trim();
          if (!zone) throw new Error("zone is required, e.g. Europe/London");
          var z = relay.zone(zone);
          return { zone: z.zone, time: z.local.time, date: z.local.date, weekday: z.local.weekday, offset: z.offset, utc: z.utc };
        }
        """;

    public string Name => "drafter:world-clock";

    public Task<ToolPackage> DraftAsync(ToolGeneralization generalization, string proposedName, CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(proposedName) ? "world_clock" : proposedName.Trim().ToLowerInvariant();
        // Counterexamples must not be the original ask city — London / Kathmandu prove generalization.
        var tests = generalization.Counterexamples
            .Where(z => !string.IsNullOrWhiteSpace(z))
            .Take(ToolPackage.MaxTests)
            .Select(z => new ToolTest(
                new Dictionary<string, string> { ["zone"] = z },
                Keys: ["zone", "time", "date", "weekday"],
                Contains: z))
            .ToList();
        if (tests.Count == 0)
        {
            tests =
            [
                new ToolTest(new Dictionary<string, string> { ["zone"] = "Europe/London" }, ["zone", "time", "date"], "Europe/London"),
                new ToolTest(new Dictionary<string, string> { ["zone"] = "Asia/Kathmandu" }, ["zone", "time", "date"], "Asia/Kathmandu"),
            ];
        }

        var package = new ToolPackage
        {
            Name = name,
            Description = string.IsNullOrWhiteSpace(generalization.Purpose)
                ? "The current local time, date and weekday in an IANA time zone."
                : generalization.Purpose.Length > ToolPackage.MaxDescriptionChars
                    ? generalization.Purpose[..ToolPackage.MaxDescriptionChars]
                    : generalization.Purpose,
            Arguments = [new ToolArgument("zone", "IANA zone id such as Europe/London or Asia/Tokyo")],
            HostFunctionNames = generalization.RequiredHostCapabilities.Count > 0
                ? generalization.RequiredHostCapabilities.ToList()
                : [HostFunctions.TimeZone],
            Source = Source,
            Tests = tests,
            BuiltBy = Name,
            Justification = generalization.Justification,
            DraftedAt = DateTimeOffset.UtcNow,
        };
        return Task.FromResult(package);
    }
}

/// <summary>Deterministic generalizer for clock-style asks (Tokyo → reusable world clock).</summary>
public static class ToolGeneralizer
{
    public static readonly string[] DefaultCounterexamples = ["Europe/London", "Asia/Kathmandu"];

    public static ToolGeneralization FromAsk(string ask, string proposedName, string justification, string inputs, string outputs)
    {
        var purpose = string.IsNullOrWhiteSpace(justification)
            ? "Return the current local time, date and weekday for any IANA time zone."
            : justification.Trim();
        var variableInputs = SplitList(inputs);
        if (variableInputs.Count == 0) variableInputs = ["zone"];
        var outs = SplitList(outputs);
        if (outs.Count == 0) outs = ["time", "date", "weekday", "offset"];

        // Never use the city from the original ask as the only test — force counterexamples.
        var counters = DefaultCounterexamples
            .Where(z => !AskMentionsZone(ask, z))
            .ToList();
        if (counters.Count == 0) counters = ["America/New_York", "Australia/Sydney"];

        var name = string.IsNullOrWhiteSpace(proposedName) ? "world_clock" : proposedName;
        return new ToolGeneralization
        {
            Purpose = purpose,
            VariableInputs = variableInputs,
            Outputs = outs,
            RequiredHostCapabilities = [HostFunctions.TimeZone],
            Counterexamples = counters,
            OriginalAsk = ask,
            ProposedName = name,
            Justification = justification,
        };
    }

    private static List<string> SplitList(string text)
        => (text ?? "").Split([',', ';', '|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToList();

    private static bool AskMentionsZone(string ask, string zone)
    {
        var city = zone.Contains('/') ? zone[(zone.LastIndexOf('/') + 1)..] : zone;
        return ask.Contains(city, StringComparison.OrdinalIgnoreCase)
            || ask.Contains(zone, StringComparison.OrdinalIgnoreCase);
    }
}
