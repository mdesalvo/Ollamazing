using Llamazing.Web.Configuration;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

namespace Llamazing.Web.Server;

/// <summary>Point-in-time view of the Ollama server: liveness, version, installed and resident models, hosting process.</summary>
public sealed record ServerStatus
{
    /// <summary>True when the HTTP API answered the last probe.</summary>
    public bool IsReachable { get; init; }

    public string? Version { get; init; }

    public ServerProcessState Process { get; init; } = ServerProcessState.Stopped;

    /// <summary>Models installed on the server; empty when the API is not reachable.</summary>
    public IReadOnlyList<Model> LocalModels { get; init; } = [];

    /// <summary>Models resident in memory; empty when the API is not reachable.</summary>
    public IReadOnlyList<RunningModel> RunningModels { get; init; } = [];

    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>True until the first probe completes, so pages can tell "checking" from "down".</summary>
    public bool IsInitial { get; init; }

    public static ServerStatus Initial { get; } = new() { IsInitial = true };
}

/// <summary>Probes the Ollama server periodically and publishes its status to every connected circuit.</summary>
/// <remarks>
/// One probe loop serves all users: components subscribe to <see cref="Changed"/> instead of polling on their own,
/// and every tab sees the effect of an operation made in another one within a poll interval.
/// </remarks>
public sealed class ServerStatusMonitor(
    IOllamaApiClient ollamaClient,
    OllamaServerController serverController,
    IOptions<OllamaOptions> options,
    ILogger<ServerStatusMonitor> logger) : BackgroundService
{
    /// <summary>A probe slower than this counts as unreachable, keeping the dashboard responsive when the server hangs.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Serialises probes, since an immediate probe can be requested while a periodic one is running.</summary>
    private readonly SemaphoreSlim probeGate = new(1, 1);

    /// <summary>Raised after every probe; handlers run on the probe thread.</summary>
    public event Action? Changed;

    public ServerStatus Current { get; private set; } = ServerStatus.Initial;

    /// <summary>Probes the server right away, used after a lifecycle command or a model operation.</summary>
    public async Task ProbeNowAsync(CancellationToken cancellationToken = default)
    {
        await probeGate.WaitAsync(cancellationToken);
        try
        {
            Current = await ProbeAsync(cancellationToken);
        }
        finally
        {
            probeGate.Release();
        }
        Changed?.Invoke();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The interval is clamped to one second so a misconfiguration cannot hammer the server.
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(Math.Max(1, options.Value.StatusPollSeconds)));
        do
        {
            try
            {
                await ProbeNowAsync(stoppingToken);
            }
            catch (Exception probeError) when (probeError is not OperationCanceledException)
            {
                // An unexpected failure must not stop the loop: the next tick tries again.
                logger.LogWarning(probeError, "Server status probe failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Collects process state, liveness, version and model lists in one status record.</summary>
    private async Task<ServerStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        // The process state is meaningful even when the API is down, e.g. while the server is still starting.
        ServerProcessState processState = serverController.GetProcessState();

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            // A server that does not answer has no models to list.
            if (!await ollamaClient.IsRunningAsync(timeout.Token))
                return new ServerStatus { Process = processState };

            string version = await ollamaClient.GetVersionAsync(timeout.Token);
            IEnumerable<Model> localModels = await ollamaClient.ListLocalModelsAsync(timeout.Token);
            IEnumerable<RunningModel> runningModels = await ollamaClient.ListRunningModelsAsync(timeout.Token);
            return new ServerStatus
            {
                IsReachable = true,
                Version = version,
                Process = processState,
                LocalModels = localModels.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                RunningModels = runningModels.ToList()
            };
        }
        catch (Exception unreachableError) when (unreachableError is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Connection refused or timeout: the server is down, starting or hung.
            return new ServerStatus { Process = processState };
        }
    }
}
