using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Relay.Core.Usage;

/// <summary>
/// Stable friction grouping key. Three unrelated corrections must never share one proposal.
/// </summary>
public sealed class PatternSignature
{
    [JsonPropertyName("frictionKind")] public required string FrictionKind { get; init; }
    [JsonPropertyName("capabilityId")] public string CapabilityId { get; init; } = "";
    [JsonPropertyName("capabilityVersion")] public int CapabilityVersion { get; init; }
    [JsonPropertyName("projectId")] public string ProjectId { get; init; } = "global";
    [JsonPropertyName("subject")] public string Subject { get; init; } = "";
    [JsonPropertyName("category")] public string Category { get; init; } = "";

    public string Key => string.Join('|',
        FrictionKind,
        CapabilityId,
        CapabilityVersion.ToString(),
        string.IsNullOrWhiteSpace(ProjectId) ? "global" : ProjectId,
        NormalizeSubject(Subject),
        Category);

    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Key)));

    public static string NormalizeSubject(string subject) =>
        subject.Trim().ToUpperInvariant();

    public static PatternSignature ForAcronymCorrection(
        string projectId,
        string acronym,
        string capabilityId = "glossary.acronym.resolve",
        int capabilityVersion = 1) => new()
    {
        FrictionKind = FrictionKinds.RepeatedCorrection,
        CapabilityId = capabilityId,
        CapabilityVersion = capabilityVersion,
        ProjectId = projectId,
        Subject = NormalizeSubject(acronym),
        Category = "acronym_correction",
    };
}
