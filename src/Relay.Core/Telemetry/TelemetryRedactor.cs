using System.Security.Cryptography;
using System.Text;

namespace Relay.Core.Telemetry;

/// <summary>
/// Rejects or hashes sensitive property keys. Records character counts and SHA-256 hashes
/// instead of content for keys named text, body, prompt, content, transcript, answer,
/// expected, actual, summary, detail, apiKey, secret, and authorization.
/// </summary>
public static class TelemetryRedactor
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "text",
        "body",
        "prompt",
        "content",
        "transcript",
        "answer",
        "expected",
        "actual",
        "summary",
        "detail",
        "apiKey",
        "secret",
        "authorization",
    };

    public static bool IsSensitiveKey(string key) =>
        !string.IsNullOrWhiteSpace(key) && SensitiveKeys.Contains(key);

    /// <summary>
    /// Returns a new property map with sensitive values replaced by length + hash metadata.
    /// Throws <see cref="TelemetryRedactionException"/> when a sensitive key is present
    /// and <paramref name="rejectSensitive"/> is true.
    /// </summary>
    public static Dictionary<string, string> Redact(
        IReadOnlyDictionary<string, string>? properties,
        bool rejectSensitive = false)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (properties is null || properties.Count == 0)
            return result;

        foreach (var (key, value) in properties)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!IsSensitiveKey(key))
            {
                result[key] = value ?? "";
                continue;
            }

            if (rejectSensitive)
                throw new TelemetryRedactionException(key);

            var raw = value ?? "";
            result[key + ".charCount"] = raw.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            result[key + ".sha256"] = Sha256Hex(raw);
            // Do not copy the raw sensitive value.
        }

        return result;
    }

    public static string Sha256Hex(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class TelemetryRedactionException : Exception
{
    public TelemetryRedactionException(string key)
        : base($"Telemetry property '{key}' is sensitive and must not be recorded as plaintext.")
    {
        Key = key;
    }

    public string Key { get; }
}
