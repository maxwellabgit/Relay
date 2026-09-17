using System.Text.Json;

namespace Relay.Core.Cases;

/// <summary>
/// Dispatches persisted outbox commands OUTSIDE the case transition lock.
/// Exclusive claim prevents two workers from acquiring the same logical command.
/// </summary>
public sealed class CommandDispatcher : IDisposable
{
    private readonly CommandStore _store;
    private readonly RuntimeConcurrencyOptions _options;
    private readonly SemaphoreSlim _local;
    private readonly SemaphoreSlim _jev;
    private readonly SemaphoreSlim _search;
    private readonly Func<RuntimeCommand, CancellationToken, Task<CommandDispatchResult>> _execute;
    private readonly string _ownerId;
    private bool _disposed;

    public CommandDispatcher(
        CommandStore store,
        RuntimeConcurrencyOptions options,
        string ownerId,
        Func<RuntimeCommand, CancellationToken, Task<CommandDispatchResult>> execute)
    {
        _store = store;
        _options = options;
        _ownerId = ownerId;
        _execute = execute;
        _local = new SemaphoreSlim(Math.Max(1, options.LocalGeneration), Math.Max(1, options.LocalGeneration));
        _jev = new SemaphoreSlim(Math.Max(1, options.Jev), Math.Max(1, options.Jev));
        _search = new SemaphoreSlim(Math.Max(1, options.SearchFetchDelegate), Math.Max(1, options.SearchFetchDelegate));
    }

    public RuntimeConcurrencyOptions Options => _options;
    public string OwnerId => _ownerId;
    public SemaphoreSlim LocalGenerationGate => _local;
    public SemaphoreSlim JevGate => _jev;
    public SemaphoreSlim SearchFetchDelegateGate => _search;

    /// <summary>Attempts exclusive claim then executes. Returns null when claim fails.</summary>
    public async Task<CommandDispatchResult?> TryDispatchAsync(
        string commandId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_store.TryClaim(commandId, _ownerId, now, out var command) || command is null)
            return null;

        var gate = GateFor(command.Kind);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _execute(command, cancellationToken).ConfigureAwait(false);
            command = _store.TryLoad(commandId) ?? command;
            if (result.Ok)
            {
                command.Status = RuntimeCommandStatus.Completed;
                command.CompletedAt = now;
                command.ResultRef = result.ResultRef;
                command.Error = null;
            }
            else if (result.Cancelled)
            {
                command.Status = RuntimeCommandStatus.Cancelled;
                command.CompletedAt = now;
                command.Error = result.Error ?? "cancelled";
            }
            else
            {
                command.Status = RuntimeCommandStatus.Failed;
                command.CompletedAt = now;
                command.Error = result.Error ?? "failed";
            }
            _store.Save(command);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public bool TryClaim(string commandId, DateTimeOffset now, out RuntimeCommand? command)
        => _store.TryClaim(commandId, _ownerId, now, out command);

    public bool TryClaimLogical(string logicalKey, DateTimeOffset now, out RuntimeCommand? command)
        => _store.TryClaimLogical(logicalKey, _ownerId, now, out command);

    private SemaphoreSlim GateFor(string kind) => kind switch
    {
        RuntimeCommandKinds.RequestJudgments => _jev,
        RuntimeCommandKinds.RequestLocalJob => _local,
        RuntimeCommandKinds.RequestReasoningJob => _local,
        RuntimeCommandKinds.RetrieveContext => _search,
        RuntimeCommandKinds.ProposeOperation => _search, // may enqueue search/delegate ops
        RuntimeCommandKinds.DispatchAuthorizedOperation => _search,
        _ => _search,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _local.Dispose();
        _jev.Dispose();
        _search.Dispose();
    }
}

public sealed record CommandDispatchResult(
    bool Ok,
    string? ResultRef = null,
    string? Error = null,
    bool Cancelled = false,
    object? Result = null);
