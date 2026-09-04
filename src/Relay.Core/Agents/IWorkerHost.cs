namespace Relay.Core.Agents;

/// <summary>A running worker as the runtime sees it: two line pipes, an exit, and a kill switch.</summary>
public interface IWorkerProcess : IDisposable
{
    int? ProcessId { get; }
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);
    Task WriteLineAsync(string line, CancellationToken cancellationToken);
    /// <summary>Completes with the exit code once the worker has exited for any reason.</summary>
    Task<int> Exited { get; }
    void Kill(string reason);
}

/// <summary>
/// Starts worker processes. The Windows implementation launches Relay.Worker.exe inside a job
/// object with a cleared environment and the staging folder as its working directory; tests use
/// an in-process host that runs the same worker code over in-memory pipes.
/// </summary>
public interface IWorkerHost
{
    string Description { get; }
    IWorkerProcess Start(AgentRunSpec spec);
}
