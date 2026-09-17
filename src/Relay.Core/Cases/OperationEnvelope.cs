using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Storage;

namespace Relay.Core.Cases;

public static class OperationStatus
{
    public const string Requested = "requested";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Executing = "executing";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static readonly string[] All =
    [
        Requested, AwaitingApproval, Approved, Denied, Executing, Completed, Failed, Cancelled
    ];
}

/// <summary>A reference to a stored object (or a slice of one) bound into an operation.</summary>
public sealed class ObjectRef
{
    [JsonPropertyName("objectId")] public required string ObjectId { get; init; }
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("selector")] public string? Selector { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
}

/// <summary>
/// Every side effect is bound by one envelope. Approval binds <see cref="CanonicalHash"/>;
/// editing the payload creates a new version that needs a new policy decision.
/// </summary>
public sealed class OperationEnvelope
{
    [JsonPropertyName("operationId")] public required string OperationId { get; init; }
    [JsonPropertyName("caseId")] public required string CaseId { get; init; }
    [JsonPropertyName("caseVersion")] public long CaseVersion { get; set; }
    [JsonPropertyName("causedByEventId")] public string? CausedByEventId { get; set; }
    [JsonPropertyName("capability")] public required string Capability { get; init; }
    [JsonPropertyName("capabilityVersion")] public int CapabilityVersion { get; init; } = 1;
    [JsonPropertyName("arguments")] public Dictionary<string, JsonElement> Arguments { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("inputRefs")] public List<ObjectRef> InputRefs { get; set; } = [];
    [JsonPropertyName("requestedScope")] public Dictionary<string, JsonElement> RequestedScope { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("grantedScope")] public Dictionary<string, JsonElement> GrantedScope { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("approvalId")] public string? ApprovalId { get; set; }
    [JsonPropertyName("idempotencyKey")] public required string IdempotencyKey { get; init; }
    [JsonPropertyName("preconditions")] public List<string> Preconditions { get; set; } = [];
    [JsonPropertyName("status")] public string Status { get; set; } = OperationStatus.Requested;
    [JsonPropertyName("resultRef")] public string? ResultRef { get; set; }
    [JsonPropertyName("canonicalHash")] public string? CanonicalHashValue { get; set; }
    [JsonPropertyName("sideEffectCount")] public int SideEffectCount { get; set; }

    /// <summary>
    /// Stable SHA-256 over the immutable request fields (excludes status, approval, result, side-effect counters).
    /// </summary>
    public string CanonicalHash()
    {
        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operationId"] = OperationId,
            ["caseId"] = CaseId,
            ["caseVersion"] = CaseVersion,
            ["causedByEventId"] = CausedByEventId,
            ["capability"] = Capability,
            ["capabilityVersion"] = CapabilityVersion,
            ["arguments"] = SortElementMap(Arguments),
            ["inputRefs"] = InputRefs
                .OrderBy(r => r.ObjectId, StringComparer.Ordinal)
                .ThenBy(r => r.Selector ?? "", StringComparer.Ordinal)
                .Select(r => new Dictionary<string, object?>
                {
                    ["objectId"] = r.ObjectId,
                    ["version"] = r.Version,
                    ["selector"] = r.Selector,
                    ["sha256"] = r.Sha256,
                })
                .ToList(),
            ["requestedScope"] = SortElementMap(RequestedScope),
            ["grantedScope"] = SortElementMap(GrantedScope),
            ["idempotencyKey"] = IdempotencyKey,
            ["preconditions"] = Preconditions.OrderBy(p => p, StringComparer.Ordinal).ToList(),
        };

        var json = JsonSerializer.Serialize(payload, CanonicalJsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static SortedDictionary<string, object?> SortElementMap(Dictionary<string, JsonElement> map)
    {
        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sorted[k] = JsonSerializer.Deserialize<object>(v.GetRawText(), RelayJson.Compact);
        return sorted;
    }

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
