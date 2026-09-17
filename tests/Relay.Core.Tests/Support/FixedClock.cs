using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Tests.Support;

/// <summary>Deterministic clock for recovery and characterization tests.</summary>
public sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset start) => UtcNow = start;
    public DateTimeOffset UtcNow { get; set; }
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Temporary data root cleaned up on dispose.</summary>
public sealed class TempDataRoot : IDisposable
{
    public TempDataRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "relay-core-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Root = new DataRoot(Path);
    }

    public string Path { get; }
    public DataRoot Root { get; }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { /* best effort */ }
    }
}
