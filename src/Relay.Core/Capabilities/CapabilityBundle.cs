using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Ids;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Capabilities;

/// <summary>Versioned capability bundle: tools/workflows/decisions/permissions/evals/rollback.</summary>
public sealed class CapabilityBundle
{
    [JsonPropertyName("bundleId")] public required string BundleId { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("manifest")] public Dictionary<string, JsonElement> Manifest { get; init; } = new(StringComparer.Ordinal);
    [JsonPropertyName("toolIds")] public List<string> ToolIds { get; init; } = [];
    [JsonPropertyName("workflowIds")] public List<string> WorkflowIds { get; init; } = [];
    [JsonPropertyName("decisionDefinitionIds")] public List<string> DecisionDefinitionIds { get; init; } = [];
    [JsonPropertyName("applicability")] public string Applicability { get; init; } = "";
    [JsonPropertyName("permissions")] public List<string> Permissions { get; init; } = [];
    [JsonPropertyName("evalRefs")] public List<string> EvalRefs { get; init; } = [];
    [JsonPropertyName("evalResults")] public List<CapabilityEvalResult> EvalResults { get; init; } = [];
    [JsonPropertyName("rollbackTarget")] public string? RollbackTarget { get; init; }
    [JsonPropertyName("contentHash")] public string ContentHash { get; set; } = "";
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
}

public sealed class CapabilityEvalResult
{
    [JsonPropertyName("evalId")] public required string EvalId { get; init; }
    [JsonPropertyName("passed")] public bool Passed { get; init; }
    [JsonPropertyName("reportHash")] public required string ReportHash { get; init; }
    [JsonPropertyName("at")] public DateTimeOffset At { get; init; }
}

public sealed class CapabilityActivationRecord
{
    [JsonPropertyName("activationId")] public required string ActivationId { get; init; }
    [JsonPropertyName("bundleId")] public required string BundleId { get; init; }
    [JsonPropertyName("contentHash")] public required string ContentHash { get; init; }
    [JsonPropertyName("evalReportHash")] public required string EvalReportHash { get; init; }
    [JsonPropertyName("activatedAt")] public DateTimeOffset ActivatedAt { get; init; }
    [JsonPropertyName("active")] public bool Active { get; set; } = true;
    [JsonPropertyName("rolledBackTo")] public string? RolledBackTo { get; set; }
    [JsonPropertyName("permissionExpansionSeparate")] public bool PermissionExpansionSeparate { get; init; } = true;
}

public static class CapabilityBundleHasher
{
    public static string Compute(CapabilityBundle bundle)
    {
        var payload = JsonSerializer.Serialize(new
        {
            bundle.Name,
            bundle.Version,
            bundle.ToolIds,
            bundle.WorkflowIds,
            bundle.DecisionDefinitionIds,
            bundle.Applicability,
            bundle.Permissions,
            bundle.EvalRefs,
            bundle.RollbackTarget,
            manifest = bundle.Manifest,
        }, RelayJson.Compact);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}

public sealed class CapabilityBundleStore
{
    private readonly DataRoot _root;
    private readonly IClock _clock;

    public CapabilityBundleStore(DataRoot root, IClock clock)
    {
        _root = root;
        _clock = clock;
    }

    public string BundlesDirectory => Path.Combine(_root.Path, "capabilities", "bundles");
    public string ActivationsDirectory => Path.Combine(_root.Path, "capabilities", "activations");
    public string CasePinsDirectory => Path.Combine(_root.Path, "capabilities", "case-pins");

    public CapabilityBundle Save(CapabilityBundle bundle)
    {
        bundle.ContentHash = CapabilityBundleHasher.Compute(bundle);
        Directory.CreateDirectory(BundlesDirectory);
        AtomicFile.WriteAllText(
            Path.Combine(BundlesDirectory, bundle.BundleId + ".json"),
            JsonSerializer.Serialize(bundle, RelayJson.Indented));
        return bundle;
    }

    public CapabilityBundle? TryLoad(string bundleId)
    {
        var text = AtomicFile.ReadAllTextIfExists(Path.Combine(BundlesDirectory, bundleId + ".json"));
        return text is null ? null : JsonSerializer.Deserialize<CapabilityBundle>(text, RelayJson.Indented);
    }

    public CapabilityBundle Create(
        string name,
        string version,
        IEnumerable<string>? tools = null,
        IEnumerable<string>? workflows = null,
        IEnumerable<string>? decisions = null,
        IEnumerable<string>? permissions = null,
        string? rollbackTarget = null,
        IEnumerable<CapabilityEvalResult>? evals = null)
    {
        var bundle = new CapabilityBundle
        {
            BundleId = Ulid.NewUlid(_clock.UtcNow),
            Name = name,
            Version = version,
            ToolIds = tools?.ToList() ?? [],
            WorkflowIds = workflows?.ToList() ?? [],
            DecisionDefinitionIds = decisions?.ToList() ?? [],
            Permissions = permissions?.ToList() ?? [],
            RollbackTarget = rollbackTarget,
            EvalResults = evals?.ToList() ?? [],
            CreatedAt = _clock.UtcNow,
        };
        return Save(bundle);
    }

    public void PinCase(string caseId, string bundleId, string contentHash)
    {
        Directory.CreateDirectory(CasePinsDirectory);
        AtomicFile.WriteAllText(
            Path.Combine(CasePinsDirectory, caseId + ".json"),
            JsonSerializer.Serialize(new { caseId, bundleId, contentHash, at = _clock.UtcNow }, RelayJson.Indented));
    }

    public (string BundleId, string ContentHash)? TryGetCasePin(string caseId)
    {
        var text = AtomicFile.ReadAllTextIfExists(Path.Combine(CasePinsDirectory, caseId + ".json"));
        if (text is null) return null;
        using var doc = JsonDocument.Parse(text);
        var bid = doc.RootElement.GetProperty("bundleId").GetString()!;
        var hash = doc.RootElement.GetProperty("contentHash").GetString()!;
        return (bid, hash);
    }
}

/// <summary>
/// Activation binds content hash + eval report. Changing code/questions/criteria/permissions
/// invalidates approval. Permission expansion is a separate approval. In-process Jint is test-only.
/// </summary>
public sealed class CapabilityActivation
{
    private readonly CapabilityBundleStore _store;
    private readonly IClock _clock;

    public CapabilityActivation(CapabilityBundleStore store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public const string JintIsTestHelperOnly = "In-process Jint is a test helper only — not a production runtime.";

    public CapabilityActivationRecord? Activate(CapabilityBundle bundle, string evalReportHash)
    {
        var hash = CapabilityBundleHasher.Compute(bundle);
        if (!string.Equals(hash, bundle.ContentHash, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(bundle.ContentHash))
            return null; // hash drift — approval invalid

        if (bundle.EvalResults.All(e => !e.Passed))
            return null;

        var matching = bundle.EvalResults.FirstOrDefault(e =>
            e.Passed && string.Equals(e.ReportHash, evalReportHash, StringComparison.OrdinalIgnoreCase));
        if (matching is null) return null;

        var record = new CapabilityActivationRecord
        {
            ActivationId = Ulid.NewUlid(_clock.UtcNow),
            BundleId = bundle.BundleId,
            ContentHash = hash,
            EvalReportHash = evalReportHash,
            ActivatedAt = _clock.UtcNow,
            Active = true,
        };
        Directory.CreateDirectory(_store.ActivationsDirectory);
        AtomicFile.WriteAllText(
            Path.Combine(_store.ActivationsDirectory, record.ActivationId + ".json"),
            JsonSerializer.Serialize(record, RelayJson.Indented));
        return record;
    }

    public bool IsApprovalValid(CapabilityBundle current, CapabilityActivationRecord activation)
    {
        var hash = CapabilityBundleHasher.Compute(current);
        return activation.Active
               && string.Equals(hash, activation.ContentHash, StringComparison.OrdinalIgnoreCase)
               && string.Equals(current.BundleId, activation.BundleId, StringComparison.Ordinal);
    }

    /// <summary>Permission expansion requires a separate approval — never piggybacks on activation.</summary>
    public bool RequiresSeparatePermissionApproval(
        CapabilityBundle previous,
        CapabilityBundle next)
    {
        var prev = new HashSet<string>(previous.Permissions, StringComparer.Ordinal);
        return next.Permissions.Any(p => !prev.Contains(p));
    }

    public CapabilityActivationRecord? Rollback(CapabilityActivationRecord active, string rollbackTargetBundleId)
    {
        active.Active = false;
        active.RolledBackTo = rollbackTargetBundleId;
        AtomicFile.WriteAllText(
            Path.Combine(_store.ActivationsDirectory, active.ActivationId + ".json"),
            JsonSerializer.Serialize(active, RelayJson.Indented));
        return active;
    }
}
