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
    /// <summary>Identifier that lets a page address this job when cancelling it.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Pull reference in "model:tag" form, or a bare model name resolving to "latest".</summary>
    public required string ModelReference { get; init; }

    /// <summary>Moment the pull was requested, used to order the job list.</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    /// <summary>Phase of the download, written only by the pull task.</summary>
    public PullState State { get; internal set; } = PullState.Running;

    /// <summary>Last message of the pull stream; Ollama reports status and byte counts per layer, not per model.</summary>
    public PullModelResponse? LastProgress { get; internal set; }

    /// <summary>Message of the failure; null unless the pull failed.</summary>
    public string? Error { get; internal set; }

    /// <summary>Moment the pull reached a final state; null while it runs.</summary>
    public DateTimeOffset? FinishedAt { get; internal set; }

    /// <summary>Source that aborts the pull stream when the user cancels the job.</summary>
    internal CancellationTokenSource Cancellation { get; } = new();

    /// <summary>True while the download is still running.</summary>
    public bool IsActive => State == PullState.Running;
}

/// <summary>Runs model downloads in background so they survive page navigation and are visible from every circuit.</summary>
/// <remarks>Singleton; job progress is written by the pull tasks and read by components, which tolerate slightly stale values.</remarks>
public sealed class PullManager(IOllamaApiClient ollamaClient, ServerStatusMonitor statusMonitor, ILogger<PullManager> logger)
{
    /// <summary>Minimum interval between two progress notifications, keeping SignalR traffic bounded on fast links.</summary>
    private static readonly TimeSpan NotificationInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>Guards the job list, which the pull tasks and the circuits touch concurrently.</summary>
    private readonly Lock jobsLock = new();

    /// <summary>Every job started since the process began or since the user last cleared the finished ones.</summary>
    private readonly List<PullJob> jobs = [];

    /// <summary>Raised on progress and state changes of any job; handlers run on the pull task thread.</summary>
    public event Action? Changed;

    /// <summary>Snapshot of all jobs, most recent first.</summary>
    public IReadOnlyList<PullJob> Jobs
    {
        get
        {
            // A copy is handed out, so callers can enumerate while new pulls keep being registered.
            lock (jobsLock)
                return jobs.OrderByDescending(job => job.StartedAt).ToList();
        }
    }

    /// <summary>Number of downloads still running.</summary>
    public int ActiveCount => Jobs.Count(job => job.IsActive);

    /// <summary>Running job for a reference, used by pages to show inline progress next to the model.</summary>
    public PullJob? FindActive(string modelReference) => Jobs.FirstOrDefault(job => job.IsActive
        && job.ModelReference.Equals(modelReference, StringComparison.OrdinalIgnoreCase));

    /// <summary>Starts downloading a model, or returns the running job when the same reference is already being pulled.</summary>
    /// <remarks>Also used to update an installed model: pulling again fetches only the layers that changed.</remarks>
    public PullJob StartPull(string modelReference)
    {
        // The job is declared outside the lock because it is started and returned after the lock is released.
        PullJob job;
        lock (jobsLock)
        {
            // Two pulls of the same model would compete for the same blobs on the server.
            PullJob? runningJob = jobs.FirstOrDefault(candidate => candidate.IsActive
                && candidate.ModelReference.Equals(modelReference.Trim(), StringComparison.OrdinalIgnoreCase));
            if (runningJob is not null)
                return runningJob;

            // No equivalent pull is running, so the new job is registered under the trimmed reference.
            job = new PullJob { ModelReference = modelReference.Trim() };
            jobs.Add(job);
        }

        // The pull runs detached from the requesting circuit, which may disconnect long before it ends.
        // Subscribers learn about the new job before the caller gets it back.
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
        // Running downloads stay listed while the finished ones go; subscribers then redraw the list.
        lock (jobsLock)
            jobs.RemoveAll(job => !job.IsActive);
        Changed?.Invoke();
    }

    /// <summary>Consumes the OllamaSharp pull stream for one job, publishing its progress.</summary>
    private async Task RunPullAsync(PullJob job)
    {
        // Starting at the minimum makes the first progress message notify at once.
        DateTimeOffset lastNotification = DateTimeOffset.MinValue;
        try
        {
            // Each streamed message describes the layer being downloaded or the final verification steps.
            await foreach (PullModelResponse? progress in ollamaClient.PullModelAsync(new PullModelRequest { Model = job.ModelReference }, job.Cancellation.Token))
            {
                // Messages without content carry no progress.
                if (progress is null)
                    continue;
                // Only the latest message is kept, since pages show a single progress line per job.
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
            // Every outcome stamps the end time and notifies, so pages never show a finished job as running.
            // The cancellation source stays undisposed: a late click on Cancel must not hit a disposed object.
            job.FinishedAt = DateTimeOffset.Now;
            Changed?.Invoke();
        }

        // The installed list changes with the pull, so every open page gets the new model at once.
        await statusMonitor.ProbeNowAsync();
    }
}
