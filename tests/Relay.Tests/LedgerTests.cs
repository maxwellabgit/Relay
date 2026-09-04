using System.Text;
using Relay.Core.Ledger;
using Relay.Tests.Support;

namespace Relay.Tests;

public class LedgerTests : IDisposable
{
    private readonly TempRoot _tmp = new();
    private readonly FixedClock _clock = new(Harness.T0);

    private string LedgerPath => _tmp.Root.LedgerPath;

    private FileLedger Open(string session = "S1")
    {
        _tmp.Root.EnsureLayout(_clock);
        var verification = LedgerVerifier.Verify(LedgerPath);
        return FileLedger.Open(LedgerPath, verification, session, _clock);
    }

    [Fact]
    public void AppendsAreChainedAndVerify()
    {
        using (var ledger = Open())
        {
            ledger.Append("a", new { n = 1 });
            ledger.Append("b", new { text = "hello, \"world\"\nline two" });
            ledger.Append("c", new { nested = new { x = new[] { 1, 2, 3 } } });
        }

        var v = LedgerVerifier.Verify(LedgerPath);
        Assert.Equal(LedgerHealth.Ok, v.Health);
        Assert.Equal(3, v.RecordCount);
        Assert.Equal(LedgerRecord.GenesisHash, v.Records[0].PreviousHash);
        Assert.Equal(v.Records[0].Hash, v.Records[1].PreviousHash);
        Assert.Equal(v.Records[1].Hash, v.Records[2].PreviousHash);
        Assert.Equal("hello, \"world\"\nline two", v.Records[1].DataString("text"));
        Assert.Equal(3, File.ReadAllLines(LedgerPath).Length);
    }

    [Fact]
    public void ChainContinuesAcrossReopen()
    {
        using (var ledger = Open("S1")) ledger.Append("a", new { });
        using (var ledger = Open("S2"))
        {
            Assert.Equal(1, ledger.LastSeq);
            ledger.Append("b", new { });
            Assert.Equal(2, ledger.LastSeq);
        }
        var v = LedgerVerifier.Verify(LedgerPath);
        Assert.Equal(LedgerHealth.Ok, v.Health);
        Assert.Equal("S1", v.Records[0].SessionId);
        Assert.Equal("S2", v.Records[1].SessionId);
    }

    [Fact]
    public void TextContainingTheHashMarkerRoundTrips()
    {
        var tricky = "user said ,\"hash\":\"deadbeef\"} and kept talking";
        using (var ledger = Open()) ledger.Append("capture", new { text = tricky });
        var v = LedgerVerifier.Verify(LedgerPath);
        Assert.Equal(LedgerHealth.Ok, v.Health);
        Assert.Equal(tricky, v.Records[0].DataString("text"));
    }

    [Fact]
    public void EditingARecordIsDetected()
    {
        using (var ledger = Open())
        {
            ledger.Append("a", new { amount = 10 });
            ledger.Append("b", new { });
            ledger.Append("c", new { });
        }
        var lines = File.ReadAllLines(LedgerPath);
        lines[0] = lines[0].Replace("\"amount\":10", "\"amount\":99");
        File.WriteAllLines(LedgerPath, lines);

        var v = LedgerVerifier.Verify(LedgerPath);
        Assert.Equal(LedgerHealth.IntegrityFailure, v.Health);
        Assert.Equal(1, v.BrokenSeq);
        Assert.Contains("hash mismatch", v.Reason);
        Assert.Empty(v.Records);
    }

    [Fact]
    public void RemovingAMiddleRecordIsDetected()
    {
        using (var ledger = Open())
        {
            ledger.Append("a", new { });
            ledger.Append("b", new { });
            ledger.Append("c", new { });
        }
        var lines = File.ReadAllLines(LedgerPath).ToList();
        lines.RemoveAt(1);
        File.WriteAllLines(LedgerPath, lines);

        var v = LedgerVerifier.Verify(LedgerPath);
        Assert.Equal(LedgerHealth.IntegrityFailure, v.Health);
        Assert.Equal(2, v.BrokenSeq);
        Assert.Single(v.Records);
    }

    [Fact]
    public void TruncatingTheTailIsNotAnIntegrityFailureButIsRepaired()
    {
        using (var ledger = Open())
        {
            ledger.Append("a", new { });
            ledger.Append("b", new { text = "this record will be torn" });
        }
        var bytes = File.ReadAllBytes(LedgerPath);
        var firstNewline = Array.IndexOf(bytes, (byte)'\n');
        var torn = bytes[..(firstNewline + 1 + 20)]; // keep record 1 plus 20 bytes of record 2
        File.WriteAllBytes(LedgerPath, torn);

        var v = LedgerVerifier.Verify(LedgerPath);
        Assert.Equal(LedgerHealth.TornTail, v.Health);
        Assert.Single(v.Records);
        Assert.Equal(firstNewline + 1, v.GoodByteLength);

        var quarantine = LedgerVerifier.RepairTornTail(LedgerPath, _tmp.Root.LedgerQuarantineDirectory, v, _clock);
        Assert.True(File.Exists(quarantine));
        Assert.Equal(20, new FileInfo(quarantine).Length);

        var after = LedgerVerifier.Verify(LedgerPath);
        Assert.Equal(LedgerHealth.Ok, after.Health);
        Assert.Single(after.Records);

        // The chain continues from the surviving record.
        using (var ledger = Open("S2")) ledger.Append("c", new { });
        Assert.Equal(LedgerHealth.Ok, LedgerVerifier.Verify(LedgerPath).Health);
        Assert.Equal(2, LedgerVerifier.Verify(LedgerPath).RecordCount);
    }

    [Fact]
    public void SecondWriterCannotOpenTheLedger()
    {
        using var first = Open();
        var verification = LedgerVerifier.Verify(LedgerPath);
        Assert.Throws<IOException>(() => FileLedger.Open(LedgerPath, verification, "S2", _clock));
    }

    [Fact]
    public void LineFormatIsStable()
    {
        var (line, hash) = LedgerFormat.Encode(1, "01ARZ3NDEKTSV4RRFFQ69G5FAV", Harness.T0, "S", "t", LedgerRecord.GenesisHash, new { k = "v" });
        Assert.StartsWith("{\"seq\":1,\"id\":\"01ARZ3NDEKTSV4RRFFQ69G5FAV\",\"ts\":\"2026-09-04T12:00:00.0000000Z\",\"session\":\"S\",\"type\":\"t\",\"prev\":\"" + LedgerRecord.GenesisHash + "\",\"data\":{\"k\":\"v\"},\"hash\":\"", line);
        Assert.EndsWith(hash + "\"}", line);
        Assert.Equal(64, hash.Length);
        var decoded = LedgerFormat.TryDecode(line, out var reason);
        Assert.NotNull(decoded);
        Assert.Null(reason);
        Assert.DoesNotContain('\n', line);
    }

    public void Dispose() => _tmp.Dispose();
}
