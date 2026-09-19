namespace Relay.Core.Application;

public sealed record RelayCommand(string Name, string? CaseId, IReadOnlyDictionary<string, string> Arguments);

public sealed record RelayQuery(string Name, string? CaseId);

public interface IRelayApplication
{
    Task<RelayCommandResult> ExecuteAsync(RelayCommand command, CancellationToken cancellationToken);
    Task<RelaySnapshot> QueryAsync(RelayQuery query, CancellationToken cancellationToken);
}

public sealed record RelayCommandResult(bool Ok, string Summary, string? CaseId, string? OperationId, string? Error);

public sealed record RelaySnapshot(bool Listening, string? ActiveCaseId, int PendingApprovals);

/// <summary>One application-owned background loop. The UI never pumps it.</summary>
public interface IRelayRuntimeService : IAsyncDisposable
{
    Task RunAsync(CancellationToken cancellationToken);
}
