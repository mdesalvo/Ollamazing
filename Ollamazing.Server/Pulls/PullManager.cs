using Microsoft.Extensions.Logging;
using OllamaSharp;
using OllamaSharp.Models;
using Ollamazing.Server.Status;

namespace Ollamazing.Server.Pulls;

/// <summary>Lifecycle of a model download.</summary>
public enum PullState
{
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>A model download tracked by <see cref="PullManager"/>, exposing the latest progress message streamed by Ollama.</summary>
public sealed class PullJob
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Pull reference in "model:tag" form, or a bare model name resolving to "latest".</summary>
    public required string ModelReference { get; init; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public PullState State { get; internal set; } = PullState.Running;

    /// <summary>Last message of the pull stream; Ollama reports status and byte counts per layer, not per model.</summary>
    public PullModelResponse? LastProgress { get; internal set; }

    public string? Error { get; internal set; }

    public DateTimeOffset? FinishedAt { get; internal set; }

    internal CancellationTokenSource Cancellation { get; } = new();

    public bool IsActive => State == PullState.Running;
}

/// <summary>Runs model downloads in background so they survive page navigation and are visible from every circuit.</summary>
/// <remarks>Singleton; job progress is written by the pull tasks and read by components, which tolerate slightly stale values.</remarks>
public sealed class PullManager(IOllamaApiClient ollamaClient, ServerStatusMonitor statusMonitor, ILogger<PullManager> logger)
{
    /// <summary>Minimum interval between two progress notifications, keeping SignalR traffic bounded on fast links.</summary>
    private static readonly TimeSpan NotificationInterval = TimeSpan.FromMilliseconds(300);

    private readonly Lock jobsLock = new();
    private readonly List<PullJob> jobs = [];

    /// <summary>Raised on progress and state changes of any job; handlers run on the pull task thread.</summary>
    public event Action? Changed;

    /// <summary>Snapshot of all jobs, most recent first.</summary>
    public IReadOnlyList<PullJob> Jobs
    {
        get
        {
            lock (jobsLock)
                return jobs.OrderByDescending(job => job.StartedAt).ToList();
        }
    }

    public int ActiveCount => Jobs.Count(job => job.IsActive);

    /// <summary>Running job for a reference, used by pages to show inline progress next to the model.</summary>
    public PullJob? FindActive(string modelReference) => Jobs.FirstOrDefault(job => job.IsActive
        && job.ModelReference.Equals(modelReference, StringComparison.OrdinalIgnoreCase));

    /// <summary>Starts downloading a model, or returns the running job when the same reference is already being pulled.</summary>
    /// <remarks>Also used to update an installed model: pulling again fetches only the layers that changed.</remarks>
    public PullJob StartPull(string modelReference)
    {
        PullJob job;
        lock (jobsLock)
        {
            // Two pulls of the same model would compete for the same blobs on the server.
            PullJob? runningJob = jobs.FirstOrDefault(candidate => candidate.IsActive
                && candidate.ModelReference.Equals(modelReference.Trim(), StringComparison.OrdinalIgnoreCase));
            if (runningJob is not null)
                return runningJob;

            job = new PullJob { ModelReference = modelReference.Trim() };
            jobs.Add(job);
        }

        // The pull runs detached from the requesting circuit, which may disconnect long before it ends.
        _ = Task.Run(() => RunPullAsync(job));
        Changed?.Invoke();
        return job;
    }

    /// <summary>Requests cancellation of a running download; Ollama keeps the partial blobs for a later resume.</summary>
    public void Cancel(Guid jobId)
    {
        // Unknown or finished jobs have nothing left to cancel.
        PullJob? job = Jobs.FirstOrDefault(candidate => candidate.Id == jobId);
        if (job is { IsActive: true })
            job.Cancellation.Cancel();
    }

    /// <summary>Removes finished jobs from the list, leaving running downloads untouched.</summary>
    public void ClearFinished()
    {
        lock (jobsLock)
            jobs.RemoveAll(job => !job.IsActive);
        Changed?.Invoke();
    }

    /// <summary>Consumes the OllamaSharp pull stream for one job, publishing its progress.</summary>
    private async Task RunPullAsync(PullJob job)
    {
        DateTimeOffset lastNotification = DateTimeOffset.MinValue;
        try
        {
            // Each streamed message describes the layer being downloaded or the final verification steps.
            await foreach (PullModelResponse? progress in ollamaClient.PullModelAsync(new PullModelRequest { Model = job.ModelReference }, job.Cancellation.Token))
            {
                // Messages without content carry no progress.
                if (progress is null)
                    continue;
                job.LastProgress = progress;

                // Notifications are throttled; the final state is always published below.
                if (DateTimeOffset.UtcNow - lastNotification >= NotificationInterval)
                {
                    lastNotification = DateTimeOffset.UtcNow;
                    Changed?.Invoke();
                }
            }

            // A stream ending without errors means the manifest was written and the model is usable.
            job.State = PullState.Completed;
            logger.LogInformation("Pulled {ModelReference}", job.ModelReference);
        }
        catch (OperationCanceledException)
        {
            // Cancellation comes only from the user, through Cancel.
            job.State = PullState.Cancelled;
        }
        catch (Exception pullError)
        {
            // Unknown models, missing cloud sign-in and full disks all surface here with Ollama's own message.
            logger.LogWarning(pullError, "Pull of {ModelReference} failed", job.ModelReference);
            job.State = PullState.Failed;
            job.Error = pullError.Message;
        }
        finally
        {
            // The cancellation source stays undisposed: a late click on Cancel must not hit a disposed object.
            job.FinishedAt = DateTimeOffset.Now;
            Changed?.Invoke();
        }

        // The installed list changes with the pull, so every open page gets the new model at once.
        await statusMonitor.ProbeNowAsync();
    }
}
