namespace Relay.Worker;

/// <summary>Protocol driver shared by the real process and the in-process test host.</summary>
public static class WorkerMain
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitBadSpec = 2;
    public const int ExitStopped = 3;

    public static async Task<int> RunAsync(IWorkerChannel channel, CancellationToken cancellationToken)
    {
        var broker = new BrokerClient(channel, cancellationToken);
        RunSpecMessage spec;
        try
        {
            var first = await channel.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (first is null) return ExitBadSpec;
            spec = RunSpecMessage.Parse(first);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            await broker.FailedAsync("Bad run spec: " + ex.Message).ConfigureAwait(false);
            return ExitBadSpec;
        }

        try
        {
            var (summary, outputs) = spec.Task switch
            {
                "summarize" => await SummarizeTask.RunAsync(spec, broker).ConfigureAwait(false),
                _ => throw new NotSupportedException($"Unknown task '{spec.Task}'."),
            };
            await broker.DoneAsync(summary, outputs).ConfigureAwait(false);
            return ExitOk;
        }
        catch (OperationCanceledException)
        {
            return ExitStopped;
        }
        catch (Exception ex) when (ex is BrokerDeniedException or NotSupportedException or EndOfStreamException or InvalidDataException or System.Text.Json.JsonException)
        {
            try { await broker.FailedAsync(ex.Message).ConfigureAwait(false); } catch (Exception) { /* channel gone */ }
            return ExitFailed;
        }
    }
}
