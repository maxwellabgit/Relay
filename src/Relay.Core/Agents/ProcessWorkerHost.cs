using System.Diagnostics;
using System.Text;

namespace Relay.Core.Agents;

/// <summary>
/// Launches Relay.Worker as a child process with redirected stdio, the run's staging folder as its
/// working directory, and a cleared environment (no user profile, no credentials, TEMP inside
/// staging). The Windows host derives from this and adds the job object; this class is what the
/// tests use to prove the protocol against the real executable.
/// </summary>
public class ProcessWorkerHost : IWorkerHost
{
    private readonly string _fileName;
    private readonly string _argumentsPrefix;

    public ProcessWorkerHost(string workerPath)
    {
        if (!File.Exists(workerPath)) throw new FileNotFoundException("Worker executable not found.", workerPath);
        if (workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            _fileName = ResolveDotnetHost() ?? throw new FileNotFoundException("dotnet host not found for framework-dependent worker.");
            _argumentsPrefix = Quote(workerPath);
        }
        else
        {
            _fileName = workerPath;
            _argumentsPrefix = "";
        }
        WorkerPath = workerPath;
    }

    public string WorkerPath { get; }
    public virtual string Description => "child process, cleared environment";

    /// <summary>Finds Relay.Worker beside the running application (exe first, then the framework-dependent dll).</summary>
    public static string? Locate(string? configured, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        foreach (var candidate in new[] { "Relay.Worker.exe", "Relay.Worker.dll" })
        {
            var path = Path.Combine(baseDirectory, candidate);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public IWorkerProcess Start(AgentRunSpec spec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _fileName,
            Arguments = _argumentsPrefix,
            WorkingDirectory = spec.StagingPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.Environment.Clear();
        var tmp = Path.Combine(spec.StagingPath, "tmp");
        Directory.CreateDirectory(tmp);
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        psi.Environment["SystemRoot"] = systemRoot;
        psi.Environment["windir"] = systemRoot;
        psi.Environment["PATH"] = Path.Combine(systemRoot, "System32");
        psi.Environment["TEMP"] = tmp;
        psi.Environment["TMP"] = tmp;
        psi.Environment["USERPROFILE"] = tmp;
        psi.Environment["LOCALAPPDATA"] = tmp;
        psi.Environment["APPDATA"] = tmp;
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        if (Path.GetDirectoryName(_fileName) is { } dotnetRoot && Path.GetFileNameWithoutExtension(_fileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.Environment["DOTNET_ROOT"] = dotnetRoot;
        else if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } configuredRoot)
            psi.Environment["DOTNET_ROOT"] = configuredRoot;
        psi.Environment["RELAY_RUN_ID"] = spec.RunId;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => { try { exited.TrySetResult(process.ExitCode); } catch (InvalidOperationException) { exited.TrySetResult(-1); } };
        if (!process.Start()) throw new InvalidOperationException("The worker process did not start.");
        try { OnStarted(process, spec); }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        process.BeginErrorReadLine();
        return new ChildProcess(process, exited);
    }

    /// <summary>Hook for platform confinement (job object, restricted token). Throwing here kills the child.</summary>
    protected virtual void OnStarted(Process process, AgentRunSpec spec) { }

    private static string? ResolveDotnetHost()
    {
        var candidates = new List<string>();
        if (Environment.ProcessPath is { } self && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) candidates.Add(self);
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } root) candidates.Add(Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        // The runtime this process runs on lives at <dotnet root>\shared\Microsoft.NETCore.App\<version>\; the host is three levels up.
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        if (Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir.TrimEnd(Path.DirectorySeparatorChar)))) is { } fromRuntime)
            candidates.Add(Path.Combine(fromRuntime, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet", "dotnet.exe"));
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) candidates.Add(Path.Combine(dir, "dotnet.exe"));
        }
        else
        {
            candidates.Add("/usr/bin/dotnet");
            candidates.Add("/usr/local/share/dotnet/dotnet");
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    private sealed class ChildProcess : IWorkerProcess
    {
        private readonly Process _process;
        private readonly TaskCompletionSource<int> _exited;
        private readonly StringBuilder _stderr = new();

        public ChildProcess(Process process, TaskCompletionSource<int> exited)
        {
            _process = process;
            _exited = exited;
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null && _stderr.Length < 4000) _stderr.AppendLine(e.Data); };
        }

        public int? ProcessId { get { try { return _process.Id; } catch (InvalidOperationException) { return null; } } }
        public Task<int> Exited => _exited.Task;
        public string StandardError => _stderr.ToString();

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) => _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();

        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Kill(string reason)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        public void Dispose()
        {
            Kill("disposed");
            _process.Dispose();
        }
    }
}
