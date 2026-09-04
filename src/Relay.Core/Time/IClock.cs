namespace Relay.Core.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Schedules a single callback after a delay on the coordinator's thread. The desktop host
/// implements this with a dispatcher timer; tests implement it with a manual clock so every
/// timeout path is deterministic.
/// </summary>
public interface IScheduler
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}
