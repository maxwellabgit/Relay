using System.Text;
using Relay.Worker;

// Relay.Worker: a temporary, isolated worker process. It is started by Relay's executor inside a
// job object with its staging folder as the working directory, receives its run spec as the
// first JSON line on stdin, and performs every read and write through the broker on the other
// end of the pipe. It exits when the task is done, fails, or Relay tells it to stop.
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var channel = new StdioChannel(Console.In, Console.Out);
return await WorkerMain.RunAsync(channel, cts.Token).ConfigureAwait(false);
