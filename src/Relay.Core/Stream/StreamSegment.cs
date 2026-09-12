using System.Security.Cryptography;
using System.Text;

namespace Relay.Core.Stream;

/// <summary>One timestamped piece of an enabled stream. Text lives in the rolling buffer and in selected excerpts only.</summary>
public sealed record StreamSegment(string SegmentId, DateTimeOffset At, string Text, string? Speaker = null)
{
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
}
