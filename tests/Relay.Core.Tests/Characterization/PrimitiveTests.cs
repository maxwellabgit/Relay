using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Storage;
using Relay.Core.Tests.Support;
using Relay.Core.Workspaces;

namespace Relay.Core.Tests.Characterization;

public class LedgerChainTests : IDisposable
{
    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void AppendsAreChainedAndVerify()
    {
        _tmp.Root.EnsureLayout(_clock);
        using (var ledger = FileLedger.Open(_tmp.Root.LedgerPath, LedgerVerifier.Verify(_tmp.Root.LedgerPath), "S1", _clock))
        {
            ledger.Append("a", new { n = 1 });
            ledger.Append("b", new { text = "hello" });
            ledger.Append("c", new { nested = true });
        }

        var v = LedgerVerifier.Verify(_tmp.Root.LedgerPath);
        Assert.Equal(LedgerHealth.Ok, v.Health);
        Assert.Equal(3, v.RecordCount);
        Assert.Equal(LedgerRecord.GenesisHash, v.Records[0].PreviousHash);
        Assert.Equal(v.Records[0].Hash, v.Records[1].PreviousHash);
        Assert.Equal(v.Records[1].Hash, v.Records[2].PreviousHash);
    }

    public void Dispose() => _tmp.Dispose();
}

public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "relay-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void WriteAllText_IsReadableAndComplete()
    {
        var path = Path.Combine(_dir, "note.json");
        AtomicFile.WriteAllText(path, "{\"ok\":true}");
        Assert.Equal("{\"ok\":true}", File.ReadAllText(path));
        Assert.Equal("{\"ok\":true}", AtomicFile.ReadAllTextIfExists(path));
    }

    [Fact]
    public void WriteAllText_CreatesParentDirectory()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "file.txt");
        AtomicFile.WriteAllText(path, "x");
        Assert.True(File.Exists(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}

public class UlidTests
{
    [Fact]
    public void NewUlid_IsValidAndLength26()
    {
        var id = Ulid.NewUlid(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(26, id.Length);
        Assert.True(Ulid.IsValid(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    public void IsValid_RejectsBadInput(string? value) => Assert.False(Ulid.IsValid(value));
}

public class PathGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-pathguard-" + Guid.NewGuid().ToString("N"));

    public PathGuardTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void IsWithin_AcceptsPathUnderRoot()
    {
        var child = Path.Combine(_root, "a", "b.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(child)!);
        File.WriteAllText(child, "x");
        var canonicalRoot = PathGuard.Canonicalize(_root);
        var canonicalChild = PathGuard.Canonicalize(child);
        Assert.True(PathGuard.IsWithin(canonicalChild, canonicalRoot));
    }

    [Fact]
    public void Check_RejectsPathOutsideRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), "relay-outside-" + Guid.NewGuid().ToString("N"), "x.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "x");
        var canonicalRoot = PathGuard.Canonicalize(_root);
        var check = PathGuard.Check(outside, [canonicalRoot]);
        Assert.False(check.Ok);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
