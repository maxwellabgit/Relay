using System.Security.Cryptography;
using System.Text;

namespace Relay.Core.Policy;

/// <summary>A single-use, time-limited grant to execute exactly one proposal as approved (contract §8.2).</summary>
public sealed record Capability(string ProposalId, string ProposalHash, string Action, DateTimeOffset ExpiresAt, string Nonce, string Signature);

public sealed record CapabilityCheck(bool Ok, string? Reason);

/// <summary>
/// Issues and validates capabilities for one session. The key lives only in process memory, so
/// a capability found on disk or replayed from a previous run can never validate. Validation
/// consumes the nonce: the executor cannot run the same grant twice.
/// </summary>
public sealed class CapabilityIssuer
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

    public Capability Issue(Proposal proposal, DateTimeOffset now)
    {
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var expires = now + Lifetime;
        var signature = Sign(proposal.ProposalId, proposal.Hash(), proposal.Action, expires, nonce);
        return new Capability(proposal.ProposalId, proposal.Hash(), proposal.Action, expires, nonce, signature);
    }

    /// <summary>Validates and consumes. A second call with the same capability fails.</summary>
    public CapabilityCheck Consume(Capability capability, Proposal proposal, DateTimeOffset now)
    {
        if (capability.ProposalId != proposal.ProposalId) return new(false, "Capability was issued for a different proposal.");
        if (capability.ProposalHash != proposal.Hash()) return new(false, "Proposal changed after approval; the capability no longer matches.");
        if (capability.Action != proposal.Action) return new(false, "Capability action does not match the proposal.");
        if (now > capability.ExpiresAt) return new(false, "Capability expired.");
        var expected = Sign(capability.ProposalId, capability.ProposalHash, capability.Action, capability.ExpiresAt, capability.Nonce);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(capability.Signature)))
            return new(false, "Capability signature is invalid for this session.");
        if (!_consumed.Add(capability.Nonce)) return new(false, "Capability was already used.");
        return new(true, null);
    }

    private string Sign(string proposalId, string hash, string action, DateTimeOffset expires, string nonce)
    {
        var payload = Encoding.UTF8.GetBytes($"{proposalId}\n{hash}\n{action}\n{expires:O}\n{nonce}");
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, payload));
    }
}
