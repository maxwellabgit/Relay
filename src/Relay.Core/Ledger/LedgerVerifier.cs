using System.Text;
using Relay.Core.Time;

namespace Relay.Core.Ledger;

public enum LedgerHealth
{
    /// <summary>Every record parsed and chained correctly.</summary>
    Ok,
    /// <summary>Records chained correctly but the file ended in an incomplete line (crash mid-write).</summary>
    TornTail,
    /// <summary>A complete record failed to parse, failed its hash, or broke the chain. Tamper-evident failure.</summary>
    IntegrityFailure,
}

public sealed record LedgerVerification(
    LedgerHealth Health,
    IReadOnlyList<LedgerRecord> Records,
    long LastSeq,
    string LastHash,
    long GoodByteLength,
    long FileLength,
    long? BrokenSeq,
    string? Reason)
{
    public int RecordCount => Records.Count;
}

/// <summary>
/// Reads the entire ledger and checks every link. The whole file is read on every start; the
/// v0.1 ledger is small and a full verification is the only way to know the record is intact.
/// </summary>
public static class LedgerVerifier
{
    public static LedgerVerification Verify(string path)
    {
        if (!File.Exists(path))
        {
            return new LedgerVerification(LedgerHealth.Ok, [], 0, LedgerRecord.GenesisHash, 0, 0, null, null);
        }

        var bytes = ReadAllBytesShared(path);
        var records = new List<LedgerRecord>();
        long expectedSeq = 1;
        var expectedPrev = LedgerRecord.GenesisHash;
        long goodLength = 0;
        var offset = 0;

        while (offset < bytes.Length)
        {
            var newline = Array.IndexOf(bytes, (byte)'\n', offset);
            if (newline < 0)
            {
                // Incomplete final line: the process died between write and flush of a record.
                return new LedgerVerification(LedgerHealth.TornTail, records, expectedSeq - 1, expectedPrev, goodLength, bytes.Length, null,
                    $"final {bytes.Length - offset} byte(s) do not end in a newline");
            }

            var line = Encoding.UTF8.GetString(bytes, offset, newline - offset).TrimEnd('\r');
            var lineEnd = newline + 1;

            if (line.Length == 0)
            {
                // Tolerate a stray blank line only at the very end.
                if (lineEnd >= bytes.Length) { goodLength = lineEnd; break; }
                return Fail(records, expectedSeq, expectedPrev, goodLength, bytes.Length, "blank line inside ledger");
            }

            var record = LedgerFormat.TryDecode(line, out var reason);
            if (record is null)
            {
                if (lineEnd >= bytes.Length && reason is not null && reason.StartsWith("invalid JSON", StringComparison.Ordinal))
                {
                    // A newline was written but the JSON is cut off — still a torn tail.
                    return new LedgerVerification(LedgerHealth.TornTail, records, expectedSeq - 1, expectedPrev, goodLength, bytes.Length, null, reason);
                }
                return Fail(records, expectedSeq, expectedPrev, goodLength, bytes.Length, $"record {expectedSeq}: {reason}");
            }
            if (record.Seq != expectedSeq)
            {
                return Fail(records, expectedSeq, expectedPrev, goodLength, bytes.Length, $"expected seq {expectedSeq} but found {record.Seq}");
            }
            if (!string.Equals(record.PreviousHash, expectedPrev, StringComparison.Ordinal))
            {
                return Fail(records, expectedSeq, expectedPrev, goodLength, bytes.Length, $"record {record.Seq}: prev hash does not match record {record.Seq - 1}");
            }

            records.Add(record);
            expectedSeq++;
            expectedPrev = record.Hash;
            goodLength = lineEnd;
            offset = lineEnd;
        }

        return new LedgerVerification(LedgerHealth.Ok, records, expectedSeq - 1, expectedPrev, goodLength, bytes.Length, null, null);
    }

    private static LedgerVerification Fail(List<LedgerRecord> records, long seq, string prev, long good, long total, string reason)
        => new(LedgerHealth.IntegrityFailure, records, seq - 1, prev, good, total, seq, reason);

    /// <summary>Reads the file while another handle (the live writer) holds it open for append.</summary>
    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream(checked((int)Math.Min(stream.Length, int.MaxValue)));
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// Repairs a torn tail by copying the incomplete bytes to the quarantine directory and
    /// truncating the ledger to the last complete record. Nothing is discarded: the quarantined
    /// bytes remain available for inspection. Returns the quarantine path.
    /// </summary>
    public static string RepairTornTail(string ledgerPath, string quarantineDirectory, LedgerVerification verification, IClock clock)
    {
        if (verification.Health != LedgerHealth.TornTail) throw new InvalidOperationException("Ledger is not in the TornTail state.");
        Directory.CreateDirectory(quarantineDirectory);
        var stamp = clock.UtcNow.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var quarantinePath = Path.Combine(quarantineDirectory, $"{stamp}-torn-tail.bin");

        using (var stream = new FileStream(ledgerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var tornLength = stream.Length - verification.GoodByteLength;
            var torn = new byte[tornLength];
            stream.Position = verification.GoodByteLength;
            stream.ReadExactly(torn);
            File.WriteAllBytes(quarantinePath, torn);
            stream.SetLength(verification.GoodByteLength);
            stream.Flush(flushToDisk: true);
        }
        return quarantinePath;
    }
}
