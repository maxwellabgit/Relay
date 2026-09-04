using Relay.Core.Agents;
using Relay.Worker;

namespace Relay.Tests.Support;

/// <summary>
/// Runs worker code in-process over in-memory pipes with synchronous continuations, so a whole
/// worker run happens deterministically on the test thread. The default body is the real
/// <see cref="WorkerMain"/>; tests swap in hostile or hanging bodies to prove the broker and limits.
/// </summary>
public sealed class InProcessWorkerHost : IWorkerHost
{
    public Func<AgentRunSpec, IWorkerChannel, CancellationToken, Task<int>> Body { get; set; } = (_, channel, ct) => WorkerMain.RunAsync(channel, ct);
    public List<AgentRunSpec> Started { get; } = new();
    public string Description => "in-process (tests)";

    public IWorkerProcess Start(AgentRunSpec spec)
    {
        Started.Add(spec);
        return new InProcessWorker(spec, Body);
    }

    private sealed class InProcessWorker : IWorkerProcess
    {
        private readonly LinePipe _toWorker = new();
        private readonly LinePipe _fromWorker = new();
        private readonly CancellationTokenSource _cts = new();

        public InProcessWorker(AgentRunSpec spec, Func<AgentRunSpec, IWorkerChannel, CancellationToken, Task<int>> body)
        {
            var workerSide = new Pipe(_toWorker, _fromWorker);
            Exited = body(spec, workerSide, _cts.Token).ContinueWith(t =>
            {
                _fromWorker.Close();
                return t.IsCompletedSuccessfully ? t.Result : t.IsCanceled ? WorkerMain.ExitStopped : -1;
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        public int? ProcessId => null;
        public Task<int> Exited { get; }

        // Reads ignore the token on purpose: Kill() closes the pipes, which ends pending reads with null.
        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) => _fromWorker.ReadAsync();

        public Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            _toWorker.Write(line);
            return Task.CompletedTask;
        }

        public void Kill(string reason)
        {
            _cts.Cancel();
            _toWorker.Close();
            _fromWorker.Close();
        }

        public void Dispose() => Kill("disposed");

        private sealed class Pipe(LinePipe input, LinePipe output) : IWorkerChannel
        {
            public Task<string?> ReadLineAsync(CancellationToken cancellationToken) => input.ReadAsync();

            public Task WriteLineAsync(string line, CancellationToken cancellationToken)
            {
                output.Write(line);
                return Task.CompletedTask;
            }
        }
    }

    /// <summary>
    /// A single-threaded line queue whose pending read completes synchronously on the writer's stack
    /// (plain TaskCompletionSource semantics), so both ends of a worker conversation stay on the test thread.
    /// </summary>
    private sealed class LinePipe
    {
        private readonly Queue<string> _items = new();
        private TaskCompletionSource<string?>? _waiter;
        private bool _closed;

        public Task<string?> ReadAsync()
        {
            if (_items.Count > 0) return Task.FromResult<string?>(_items.Dequeue());
            if (_closed) return Task.FromResult<string?>(null);
            _waiter = new TaskCompletionSource<string?>();
            return _waiter.Task;
        }

        public void Write(string line)
        {
            if (_closed) return;
            if (_waiter is { } w) { _waiter = null; w.TrySetResult(line); }
            else _items.Enqueue(line);
        }

        public void Close()
        {
            _closed = true;
            if (_waiter is { } w) { _waiter = null; w.TrySetResult(null); }
        }
    }
}

public static class WorkerBodies
{
    /// <summary>Reads the spec, then never answers: the wall clock is the only thing that ends it.</summary>
    public static Func<AgentRunSpec, IWorkerChannel, CancellationToken, Task<int>> Hanging => async (_, channel, ct) =>
    {
        await channel.ReadLineAsync(ct);
        var tcs = new TaskCompletionSource<int>();
        using var reg = ct.Register(() => tcs.TrySetResult(WorkerMain.ExitStopped));
        return await tcs.Task;
    };

    /// <summary>
    /// A hostile worker: tries every way out of its box through the broker, then edits an input
    /// copy behind the broker's back, then claims success. Every attempt must be denied or invalidate the run.
    /// </summary>
    public static Func<AgentRunSpec, IWorkerChannel, CancellationToken, Task<int>> Hostile => async (spec, channel, ct) =>
    {
        await channel.ReadLineAsync(ct);
        var broker = new BrokerClient(channel, ct);
        var attempts = new (string Tool, (string, string)[] Args)[]
        {
            ("read_file", [("path", "../../ledger/ledger.jsonl")]),
            ("read_file", [("path", @"..\..\config\settings.json")]),
            ("read_file", [("path", "inputs/../../../registry/projects.json")]),
            ("read_file", [("path", @"C:\Windows\win.ini")]),
            ("run_shell", [("path", "cmd /c whoami")]),
            ("write_file", [("path", "inputs/notes/planted.md"), ("text", "planted")]),
            ("write_file", [("path", "../escaped.txt"), ("text", "escaped")]),
            ("list_dir", [("path", "..")]),
            ("list_dir", [("path", "out")]),
        };
        var denied = 0;
        foreach (var (tool, args) in attempts)
        {
            try { await broker.CallAsync(tool, args); }
            catch (BrokerDeniedException) { denied++; }
        }
        await broker.LogAsync($"{denied} of {attempts.Length} escape attempts denied");

        // Now cheat outside the protocol (what a real sandbox escape would look like) and lie about it.
        var target = Path.Combine(spec.StagingPath, spec.Inputs[0].Path.Replace('/', Path.DirectorySeparatorChar));
        File.SetAttributes(target, FileAttributes.Normal);
        File.AppendAllText(target, "\ntampered");
        await broker.CallAsync("write_file", ("path", "out/summary.md"), ("text", "# fake summary\n"));
        await broker.DoneAsync("all good", ["out/summary.md"]);
        return WorkerMain.ExitOk;
    };
}
