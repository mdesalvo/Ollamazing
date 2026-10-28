using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Llamazing.Web.Configuration;
using Microsoft.Extensions.Options;

namespace Llamazing.Web.Server;

/// <summary>Lifecycle actions available from the control panel.</summary>
public enum ServerAction
{
    Start,
    Stop,
    Restart
}

/// <summary>Outcome of a lifecycle action, kept for display in the control panel.</summary>
public sealed record ServerActionResult(ServerAction Action, bool Succeeded, string Message, DateTimeOffset ExecutedAt);

/// <summary>Server process found in the OS process table.</summary>
/// <param name="IsRunning">True when an Ollama process exists, regardless of whether its HTTP API answers yet.</param>
/// <param name="IsOwned">True when the process was started by Llamazing, which then also holds its log.</param>
/// <param name="RunningSince">When the process started; null when the OS hides it (processes of other users).</param>
public sealed record ServerProcessState(bool IsRunning, bool IsOwned, int? ProcessId, DateTimeOffset? RunningSince)
{
    public static ServerProcessState Stopped { get; } = new(false, false, null, null);
}

/// <summary>Starts, stops and inspects the Ollama server as a plain OS process, the same way on every platform.</summary>
/// <remarks>
/// Singleton: it owns the <c>ollama serve</c> child process it starts and buffers its output for the log viewer.
/// Servers started elsewhere (desktop app, terminal, service) are detected by process name and can be stopped
/// when the OS grants the permission.
/// </remarks>
public sealed class OllamaServerController : IDisposable
{
    /// <summary>Number of output lines kept from the owned server process.</summary>
    private const int ProcessLogCapacity = 2000;

    /// <summary>OS process name of the Ollama executable on every platform (ollama.exe on Windows).</summary>
    private const string ServerProcessName = "ollama";

    /// <summary>Time granted to a killed server to release its port before the next start.</summary>
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

    private readonly ServerControlOptions controlOptions;
    private readonly OllamaOptions ollamaOptions;
    private readonly ILogger<OllamaServerController> logger;
    private readonly ConcurrentQueue<string> processLog = new();
    private readonly Lock processLock = new();
    private Process? ownedProcess;

    public OllamaServerController(IOptions<ServerControlOptions> controlOptions, IOptions<OllamaOptions> ollamaOptions, ILogger<OllamaServerController> logger)
    {
        this.controlOptions = controlOptions.Value;
        this.ollamaOptions = ollamaOptions.Value;
        this.logger = logger;
    }

    /// <summary>Executes a lifecycle action and reports its outcome; expected failures are returned, not thrown.</summary>
    public ServerActionResult Execute(ServerAction action)
    {
        lock (processLock)
        {
            // Stop and restart both begin by terminating whatever server is running, owned or not.
            if (action is ServerAction.Stop or ServerAction.Restart)
            {
                string? stopError = StopServerProcesses();

                // A server that survives (owned by another account) keeps the port busy, so no start may follow.
                if (stopError is not null)
                    return new ServerActionResult(action, false, stopError, DateTimeOffset.Now);

                // A stop is complete once no server process is left.
                if (action == ServerAction.Stop)
                    return new ServerActionResult(action, true, "Server stopped", DateTimeOffset.Now);
            }

            // Starting next to a running server would make the second instance fail to bind the port.
            ServerProcessState currentState = GetProcessState();
            if (currentState.IsRunning)
                return new ServerActionResult(action, true, $"Already running with PID {currentState.ProcessId}", DateTimeOffset.Now);

            return StartServer(action);
        }
    }

    /// <summary>Finds the running server, preferring the one started by Llamazing.</summary>
    public ServerProcessState GetProcessState()
    {
        lock (processLock)
        {
            // The owned process is reported first because its log is available in the viewer.
            if (ownedProcess is { HasExited: false })
                return new ServerProcessState(true, true, ownedProcess.Id, ReadStartTime(ownedProcess));
        }

        // Any other server process was started by the desktop app, a terminal or a service manager.
        Process[] serverProcesses = Process.GetProcessesByName(ServerProcessName);
        try
        {
            // CLI sessions share the process name; the oldest process is the most likely to be the server.
            Process? oldestProcess = serverProcesses.MinBy(process => ReadStartTime(process) ?? DateTimeOffset.MaxValue);
            return oldestProcess is null
                ? ServerProcessState.Stopped
                : new ServerProcessState(true, false, oldestProcess.Id, ReadStartTime(oldestProcess));
        }
        finally
        {
            foreach (Process serverProcess in serverProcesses)
                serverProcess.Dispose();
        }
    }

    /// <summary>Returns the most recent server log lines, from the owned process output or the server log file.</summary>
    public async Task<string> GetLogsAsync(CancellationToken cancellationToken = default)
    {
        // A server started by Llamazing has its output buffered in memory.
        lock (processLock)
        {
            if (ownedProcess is not null && !processLog.IsEmpty)
                return string.Join('\n', processLog.TakeLast(controlOptions.LogLines));
        }

        // Servers started elsewhere write to a log file under the user profile.
        return await ReadLogFileTailAsync(cancellationToken);
    }

    /// <summary>Stops the owned server on shutdown when configured, so it does not outlive the dashboard that started it.</summary>
    public void Dispose()
    {
        lock (processLock)
        {
            if (controlOptions.StopOnExit && ownedProcess is { HasExited: false })
                ownedProcess.Kill(entireProcessTree: true);
            ownedProcess?.Dispose();
        }
    }

    /// <summary>Launches the owned server and reports the outcome of <paramref name="action"/>.</summary>
    private ServerActionResult StartServer(ServerAction action)
    {
        try
        {
            // The log buffer restarts with the process, so the viewer never mixes two server lifetimes.
            ownedProcess?.Dispose();
            processLog.Clear();
            ownedProcess = StartOwnedProcess();
            logger.LogInformation("Started ollama serve with PID {ProcessId}", ownedProcess.Id);
            return new ServerActionResult(action, true, $"Started with PID {ownedProcess.Id}", DateTimeOffset.Now);
        }
        catch (Exception startError) when (startError is Win32Exception or InvalidOperationException)
        {
            // A wrong executable path is the usual cause and is reported verbatim.
            logger.LogWarning(startError, "Starting {Executable} failed", controlOptions.ExecutablePath);
            ownedProcess = null;
            return new ServerActionResult(action, false, $"Cannot start '{controlOptions.ExecutablePath} serve': {startError.Message}", DateTimeOffset.Now);
        }
    }

    /// <summary>Kills every Ollama process, owned or external; returns an error message when one survives.</summary>
    /// <remarks>Processes named "ollama" include CLI sessions such as <c>ollama run</c>, which lose their server anyway.</remarks>
    private string? StopServerProcesses()
    {
        List<string> failures = [];
        foreach (Process serverProcess in Process.GetProcessesByName(ServerProcessName))
        {
            using (serverProcess)
            {
                try
                {
                    // Model runners are children of the server and hold the GPU memory, so they go down with it.
                    serverProcess.Kill(entireProcessTree: true);
                    serverProcess.WaitForExit(ExitTimeout);
                }
                catch (Exception killError) when (killError is Win32Exception or InvalidOperationException)
                {
                    // Access denied means the server runs under another account, which only its owner can stop.
                    failures.Add($"PID {serverProcess.Id}: {killError.Message}");
                }
            }
        }

        return failures.Count == 0
            ? null
            : $"Could not stop every Ollama process ({string.Join("; ", failures)}). The server runs under another account: stop it there, then let Llamazing start its own.";
    }

    /// <summary>Launches <c>ollama serve</c> bound to the configured address, buffering its output for the log viewer.</summary>
    private Process StartOwnedProcess()
    {
        ProcessStartInfo startInfo = new(controlOptions.ExecutablePath, ["serve"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // The server must listen where the API client connects, so OLLAMA_HOST is derived from the client base URL.
        Uri baseUri = new(ollamaOptions.BaseUrl);
        startInfo.Environment["OLLAMA_HOST"] = $"{baseUri.Host}:{baseUri.Port}";

        // Server tuning from configuration is applied last, so it can also override OLLAMA_HOST deliberately.
        foreach ((string variableName, string variableValue) in controlOptions.Environment)
            startInfo.Environment[variableName] = variableValue;

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };

        // Ollama writes its log to stderr; both streams end up in the same bounded buffer.
        process.OutputDataReceived += (_, line) => AppendProcessLog(line.Data);
        process.ErrorDataReceived += (_, line) => AppendProcessLog(line.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Adds a line to the owned process log, dropping the oldest ones beyond the capacity.</summary>
    private void AppendProcessLog(string? line)
    {
        // A null line signals the end of a stream and carries no text.
        if (line is null)
            return;

        processLog.Enqueue(line);
        while (processLog.Count > ProcessLogCapacity)
            processLog.TryDequeue(out _);
    }

    /// <summary>Reads the last lines of the server log file, from configuration or the default location of the Ollama desktop app.</summary>
    private async Task<string> ReadLogFileTailAsync(CancellationToken cancellationToken)
    {
        // The Windows app logs under LocalApplicationData, the macOS app under ~/.ollama/logs.
        string logFile = !string.IsNullOrWhiteSpace(controlOptions.LogFile)
            ? Environment.ExpandEnvironmentVariables(controlOptions.LogFile)
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ollama", "server.log")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "logs", "server.log");

        // A missing file is explained, so the user knows which setting to fill in.
        if (!File.Exists(logFile))
            return $"No server log available: {logFile} does not exist.\nStart the server from Llamazing to capture its output, or set ServerControl:LogFile.";

        // The server keeps the file open for writing, so it is read with shared access.
        await using FileStream logStream = new(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(logStream);
        string content = await reader.ReadToEndAsync(cancellationToken);
        return string.Join('\n', content.Split('\n').TakeLast(controlOptions.LogLines));
    }

    /// <summary>Start time of a process, or null when the OS denies access to it.</summary>
    private static DateTimeOffset? ReadStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception accessError) when (accessError is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Unknown start time does not prevent reporting the process as running.
            return null;
        }
    }
}

/// <summary>Starts the Ollama server when Llamazing boots, if configured and no server is running yet.</summary>
public sealed class ServerAutoStartService(OllamaServerController serverController, IOptions<ServerControlOptions> options, ILogger<ServerAutoStartService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Auto-start is opt-in and never competes with a server already running.
        if (!options.Value.StartOnLaunch || serverController.GetProcessState().IsRunning)
            return Task.CompletedTask;

        ServerActionResult result = serverController.Execute(ServerAction.Start);
        logger.LogInformation("Auto-start of Ollama: {Message}", result.Message);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
