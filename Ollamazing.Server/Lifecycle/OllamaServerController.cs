using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ollamazing.Server.Configuration;

namespace Ollamazing.Server.Lifecycle;

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
/// <param name="IsOwned">True when the process was started by Ollamazing, which then also holds its log.</param>
/// <param name="ProcessId">Operating system identifier of the process; null when none exists.</param>
/// <param name="RunningSince">When the process started; null when the OS hides it (processes of other users).</param>
public sealed record ServerProcessState(bool IsRunning, bool IsOwned, int? ProcessId, DateTimeOffset? RunningSince)
{
    /// <summary>State reported when no Ollama process exists.</summary>
    public static ServerProcessState Stopped { get; } = new(false, false, null, null);
}

/// <summary>Starts, stops and inspects the Ollama server as a plain OS process, the same way on every platform.</summary>
/// <remarks>Singleton: it owns the <c>ollama serve</c> child process it starts and buffers its output for the log viewer.</remarks>
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

    /// <summary>Bounded buffer of the owned process output, fed by its output events.</summary>
    private readonly ConcurrentQueue<string> processLog = new();

    /// <summary>Guards the owned process, since circuits issue lifecycle commands concurrently.</summary>
    private readonly Lock processLock = new();

    /// <summary>Server started by Ollamazing; null when none was started or the last start failed.</summary>
    private Process? ownedProcess;

    /// <summary>Reads the bound option values that drive the server process and its log.</summary>
    public OllamaServerController(IOptions<ServerControlOptions> controlOptions, IOptions<OllamaOptions> ollamaOptions, ILogger<OllamaServerController> logger)
    {
        // Only the bound values are kept, since options do not change while the application runs.
        this.controlOptions = controlOptions.Value;
        this.ollamaOptions = ollamaOptions.Value;
        this.logger = logger;
    }

    /// <summary>Executes a lifecycle action and reports its outcome; expected failures are returned, not thrown.</summary>
    public ServerActionResult Execute(ServerAction action)
    {
        // Commands are serialised, so two clicks cannot start two servers.
        lock (processLock)
        {
            // Stop and restart both begin by terminating whatever server is running, owned or not.
            if (action is ServerAction.Stop or ServerAction.Restart)
            {
                // A null result means that no server process is left.
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

            // Nothing runs any more, so the owned server is launched and its outcome handed back.
            return StartServer(action);
        }
    }

    /// <summary>Finds the running server, preferring the one started by Ollamazing.</summary>
    public ServerProcessState GetProcessState()
    {
        // A concurrent start or stop may replace the owned process while it is inspected.
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
            // Process objects hold OS handles, so every one is released including those not chosen.
            foreach (Process serverProcess in serverProcesses)
                serverProcess.Dispose();
        }
    }

    /// <summary>Returns the most recent server log lines, from the owned process output or the server log file.</summary>
    public async Task<string> GetLogsAsync(CancellationToken cancellationToken = default)
    {
        // A server started by Ollamazing has its output buffered in memory.
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
        // A start or stop in flight finishes before the process is torn down.
        lock (processLock)
        {
            // A failure is only logged: shutdown must complete even when a runner cannot be stopped.
            if (controlOptions.StopOnExit && ownedProcess is { HasExited: false } && KillProcessTree(ownedProcess) is { } killFailure)
                logger.LogWarning("Stopping the owned server on exit failed: {Failure}", killFailure);
            // The handle is released whether or not the process was stopped.
            ownedProcess?.Dispose();
        }
    }

    /// <summary>Launches the owned server and reports the outcome of <paramref name="action"/>.</summary>
    private ServerActionResult StartServer(ServerAction action)
    {
        try
        {
            // The log buffer restarts with the process, so the viewer never mixes two server lifetimes.
            // The new process is recorded as owned before the outcome is reported.
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
        // Collects one entry for each process that the OS refused to end.
        List<string> failures = [];

        // Every process is tried even after a failure, so one denied process does not spare the others.
        foreach (Process serverProcess in Process.GetProcessesByName(ServerProcessName))
        {
            using (serverProcess)
            {
                // Access denied means the server runs under another account, which only its owner can stop.
                if (KillProcessTree(serverProcess) is { } killFailure)
                {
                    logger.LogWarning("Stopping Ollama process {ProcessId} failed: {Failure}", serverProcess.Id, killFailure);
                    failures.Add($"PID {serverProcess.Id}: {killFailure}");
                }
            }
        }

        // A null result tells the caller that no server process survived.
        return failures.Count == 0
            ? null
            : $"Could not stop every Ollama process ({string.Join("; ", failures)}). The server runs under another account: stop it there, then let Ollamazing start its own.";
    }

    /// <summary>Kills a server with its model runners and waits for it to exit; returns the reason when the OS refuses.</summary>
    private static string? KillProcessTree(Process process)
    {
        try
        {
            // Model runners are children of the server and hold the GPU memory, so they go down with it.
            // Waiting for the exit frees the port before a start follows; null reports success.
            process.Kill(entireProcessTree: true);
            process.WaitForExit(ExitTimeout);
            return null;
        }
        catch (AggregateException treeError)
        {
            // Killing a tree reports every process it could not end, typically the same denial repeated.
            return string.Join("; ", treeError.InnerExceptions.Select(processError => processError.Message).Distinct());
        }
        catch (Exception killError) when (killError is Win32Exception or InvalidOperationException)
        {
            // A single denial, or a process that vanished in the meantime.
            return killError.Message;
        }
    }

    /// <summary>Launches <c>ollama serve</c> bound to the configured address, buffering its output for the log viewer.</summary>
    private Process StartOwnedProcess()
    {
        // Output is redirected for the log viewer; no shell or window is involved, which keeps the start identical on every platform.
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

        // The process is built unstarted so the log handlers can be attached before it writes its first line.
        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };

        // Ollama writes its log to stderr; both streams end up in the same bounded buffer.
        // The server is started and the readers are begun only after the handlers are attached.
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

        // The buffer is capped, so a long-running server cannot grow memory without bound.
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
            return $"No server log available: {logFile} does not exist.\nStart the server from Ollamazing to capture its output, or set ServerControl:LogFile.";

        // The server keeps the file open for writing, so it is read with shared access.
        // The tail is cut after reading, since the file has no line index.
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
            // The start time identifies the oldest process and tells since when the server runs.
            return process.StartTime;
        }
        catch (Exception accessError) when (accessError is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Unknown start time does not prevent reporting the process as running.
            return null;
        }
    }
}

/// <summary>Starts the Ollama server when Ollamazing boots, if configured and no server is running yet.</summary>
public sealed class ServerAutoStartService(OllamaServerController serverController, IOptions<ServerControlOptions> options, ILogger<ServerAutoStartService> logger) : IHostedService
{
    /// <summary>Starts the server at boot when configured and no server is running yet.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Auto-start is opt-in and never competes with a server already running.
        if (!options.Value.StartOnLaunch || serverController.GetProcessState().IsRunning)
            return Task.CompletedTask;

        // The outcome is only logged, since nobody waits for an answer while the application boots.
        ServerActionResult result = serverController.Execute(ServerAction.Start);
        logger.LogInformation("Auto-start of Ollama: {Message}", result.Message);
        return Task.CompletedTask;
    }

    /// <summary>Leaves the server alone, since the controller stops its own server when it is disposed.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
