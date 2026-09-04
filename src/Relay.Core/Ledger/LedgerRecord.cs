using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relay.Core.Storage;

namespace Relay.Core.Ledger;

/// <summary>
/// One immutable ledger line. The <c>hash</c> is SHA-256 over the UTF-8 bytes of the line with
/// the trailing <c>,"hash":"…"</c> member removed, and <c>prev</c> is the hash of the preceding
/// record (all zeros for the first). Any edit, reorder, or deletion breaks the chain.
/// </summary>
public sealed record LedgerRecord(
    long Seq,
    string Id,
    DateTimeOffset Timestamp,
    string SessionId,
    string Type,
    string PreviousHash,
    JsonElement Data,
    string Hash)
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public string? DataString(string property)
        => Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public long? DataInt64(string property)
        => Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    public bool? DataBool(string property)
        => Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty(property, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
}

public static class LedgerFormat
{
    private const string HashMarker = ",\"hash\":\"";

    /// <summary>Builds the exact line (without newline) for a new record and returns it with its hash.</summary>
    public static (string Line, string Hash) Encode(long seq, string id, DateTimeOffset ts, string sessionId, string type, string prevHash, object data)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, Encoder = RelayJson.Compact.Encoder, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", seq);
            writer.WriteString("id", id);
            writer.WriteString("ts", FormatTimestamp(ts));
            writer.WriteString("session", sessionId);
            writer.WriteString("type", type);
            writer.WriteString("prev", prevHash);
            writer.WritePropertyName("data");
            JsonSerializer.Serialize(writer, data, data.GetType(), RelayJson.Compact);
            writer.WriteEndObject();
        }

        var body = buffer.WrittenSpan;
        var hash = Convert.ToHexStringLower(SHA256.HashData(body));
        var bodyText = Encoding.UTF8.GetString(body);
        // body ends with '}' — splice the hash member in as the final property.
        var line = string.Concat(bodyText.AsSpan(0, bodyText.Length - 1), HashMarker, hash, "\"}");
        return (line, hash);
    }

    public static string FormatTimestamp(DateTimeOffset ts) => ts.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Parses one line and recomputes its hash. Returns null with a reason when the line is malformed.</summary>
    public static LedgerRecord? TryDecode(string line, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(line)) { reason = "empty line"; return null; }
        var markerIndex = line.LastIndexOf(HashMarker, StringComparison.Ordinal);
        if (markerIndex < 0 || !line.EndsWith("\"}", StringComparison.Ordinal)) { reason = "missing hash member"; return null; }

        var body = string.Concat(line.AsSpan(0, markerIndex), "}");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException ex) { reason = "invalid JSON: " + ex.Message; return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { reason = "record is not an object"; return null; }
            if (!root.TryGetProperty("seq", out var seqEl) || !seqEl.TryGetInt64(out var seq)) { reason = "missing seq"; return null; }
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) { reason = "missing id"; return null; }
            if (!root.TryGetProperty("ts", out var tsEl) || !tsEl.TryGetDateTimeOffset(out var ts)) { reason = "missing ts"; return null; }
            if (!root.TryGetProperty("session", out var sessEl) || sessEl.ValueKind != JsonValueKind.String) { reason = "missing session"; return null; }
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) { reason = "missing type"; return null; }
            if (!root.TryGetProperty("prev", out var prevEl) || prevEl.ValueKind != JsonValueKind.String) { reason = "missing prev"; return null; }
            if (!root.TryGetProperty("data", out var dataEl)) { reason = "missing data"; return null; }
            if (!root.TryGetProperty("hash", out var hashEl) || hashEl.ValueKind != JsonValueKind.String) { reason = "missing hash"; return null; }

            var storedHash = hashEl.GetString()!;
            if (!string.Equals(storedHash, expectedHash, StringComparison.Ordinal))
            {
                reason = "hash mismatch";
                return null;
            }

            return new LedgerRecord(seq, idEl.GetString()!, ts, sessEl.GetString()!, typeEl.GetString()!, prevEl.GetString()!, dataEl.Clone(), storedHash);
        }
    }
}
