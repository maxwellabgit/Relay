using System.Security.Cryptography;
using System.Text;

namespace Relay.Core.Ledger;

/// <summary>
/// What the ledger may say about words that were only overheard. Segments enter the ledger as hashes and excerpts as
/// ids; text that the mind, a planner or an executor writes about an overheard task (titles, answers, proposal reasons,
/// note text in a target) can quote those words, so the ledger records a fingerprint (length and a SHA-256 prefix)
/// and the task record, the excerpt and the execution journal keep the text under retention.
/// </summary>
public static class Withheld
{
    public const string Prefix = "withheld: ";

    public static string Fingerprint(string text) => $"{Prefix}{text.Length} chars, sha256 {Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16]}";

    /// <summary>Ids, slugs, types, numbers and flags carry no words; anything with whitespace, or longer than an id, reads as prose.</summary>
    public static bool LooksLikeProse(string value) => value.Length > 64 || value.Any(char.IsWhiteSpace);

    /// <summary>A proposal target for the ledger: prose values fingerprinted, references kept, so the audit trail still says what was touched.</summary>
    public static IReadOnlyDictionary<string, string> Target(IReadOnlyDictionary<string, string> target)
        => target.ToDictionary(kv => kv.Key, kv => LooksLikeProse(kv.Value) ? Fingerprint(kv.Value) : kv.Value, StringComparer.Ordinal);
}
