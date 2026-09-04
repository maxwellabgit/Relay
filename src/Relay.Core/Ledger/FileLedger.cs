using System.Text;
using Relay.Core.Ids;
using Relay.Core.Time;

namespace Relay.Core.Ledger;

public interface ILedger
{
    string Path { get; }
    long LastSeq { get; }
    string LastHash { get; }
    string SessionId { get; }

    /// <summary>Appends one record and flushes it to disk before returning. Throws <see cref="LedgerWriteException"/> on failure.</summary>
    LedgerRecord Append(string type, object data);

    event Action<LedgerRecord>? Appended;
}

public sealed class LedgerWriteException : Exception
{
    public LedgerWriteException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Single-writer append-only ledger. The file is held open with an exclusive write share for
/// the life of the process, which doubles as the "one Relay instance per data root" guard.
/// Every append is flushed to disk (FlushFileBuffers) before the call returns.
/// </summary>
public sealed class FileLedger : ILedger, IDisposable
{
    private readonly FileStream _stream;
    private readonly IClock _clock;
    private readonly object _gate = new();

    private FileLedger(string path, FileStream stream, long lastSeq, string lastHash, string sessionId, IClock clock)
    {
        Path = path;
        _stream = stream;
        LastSeq = lastSeq;
        LastHash = lastHash;
        SessionId = sessionId;
        _clock = clock;
    }

    public string Path { get; }
    public long LastSeq { get; private set; }
    public string LastHash { get; private set; }
    public string SessionId { get; }

    public event Action<LedgerRecord>? Appended;

    /// <summary>Opens the ledger for appending after it has been verified. The chain continues from the verified tail.</summary>
    public static FileLedger Open(string path, LedgerVerification verified, string sessionId, IClock clock)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        return new FileLedger(path, stream, verified.LastSeq, verified.LastHash, sessionId, clock);
    }

    public LedgerRecord Append(string type, object data)
    {
        lock (_gate)
        {
            var seq = LastSeq + 1;
            var ts = _clock.UtcNow;
            var id = Ulid.NewUlid(ts);
            var (line, hash) = LedgerFormat.Encode(seq, id, ts, SessionId, type, LastHash, data);
            var record = LedgerFormat.TryDecode(line, out var reason) ?? throw new InvalidOperationException("Encoded record failed self-check: " + reason);

            try
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                throw new LedgerWriteException($"Could not append '{type}' to the ledger: {ex.Message}", ex);
            }

            LastSeq = seq;
            LastHash = hash;
            Appended?.Invoke(record);
            return record;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream.Dispose();
        }
    }
}
