using Microsoft.Extensions.Logging;
using OllamaSharp;
using OllamaSharp.Models;
using Ollamazing.Server.Status;

namespace Ollamazing.Server.Pulls;

/// <summary>Lifecycle of a model download.</summary>
public enum PullState
{
    Running,
    Paused,
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

    /// <summary>Moment the pull reached a final state; null while it runs or is paused.</summary>
    public DateTimeOffset? FinishedAt { get; internal set; }

    /// <summary>Source that aborts the current pull stream when the user pauses or cancels the job; each resume installs a fresh one.</summary>
    internal CancellationTokenSource Cancellation { get; set; } = new();

    /// <summary>True between a Pause request and the moment the pull task records the stop, so the stop is stored as a pause.</summary>
    internal bool IsPauseRequested { get; set; }

    /// <summary>True while the download is still running.</summary>
    public bool IsActive => State == PullState.Running;

    /// <summary>True while the download is stopped by the user and can be resumed.</summary>
    public bool IsPaused => State == PullState.Paused;

    /// <summary>True once the download reached a final state, which excludes paused jobs.</summary>
    public bool IsFinished => State is PullState.Completed or PullState.Failed or PullState.Cancelled;
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
    public int ActiveCount
    {
        get
        {
            // The header counts the running pulls at every redraw, so the list is read in place while the pull tasks change it.
            lock (jobsLock)
                return jobs.Count(job => job.IsActive);
        }
    }

    /// <summary>Running job for a reference, used by pages to show inline progress next to the model.</summary>
    public PullJob? FindActive(string modelReference)
    {
        // Pages ask on every redraw while pull tasks change the list, so the search runs under the lock.
        lock (jobsLock)
            return jobs.FirstOrDefault(job => job.IsActive && job.ModelReference.Equals(modelReference, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Starts downloading a model, or returns the running job when the same reference is already being pulled; a paused job of that reference is resumed.</summary>
    /// <remarks>Also used to update an installed model: pulling again fetches only the layers that changed.</remarks>
    public PullJob StartPull(string modelReference)
    {
        // The job is registered under the trimmed reference so " llama3 " and "llama3" are the same pull.
        string reference = modelReference.Trim();

        // The job and the resume flag are declared outside the lock because they are used after it is released.
        PullJob job;
        bool isResume;
        lock (jobsLock)
        {
            // Two pulls of the same model would compete for the same blobs on the server.
            PullJob? existingJob = jobs.FirstOrDefault(candidate => (candidate.IsActive || candidate.IsPaused) && candidate.ModelReference.Equals(reference, StringComparison.OrdinalIgnoreCase));

            // A running pull of the same reference already does what the caller asks.
            if (existingJob is { IsActive: true })
                return existingJob;

            // Only a paused pull can be left here so it is continued below.
            isResume = existingJob is not null;

            // No equivalent pull exists when nothing was found, so the new job is registered.
            job = existingJob ?? new PullJob { ModelReference = reference };
            if (!isResume)
                jobs.Add(job);
        }

        // Ollama continues from the partial blobs the paused pull left behind, in the same card.
        if (isResume)
        {
            // The resume runs outside the lock because it takes the lock itself.
            Resume(job.Id);
            return job;
        }

        // The pull runs detached from the requesting circuit, which may disconnect long before it ends.
        _ = Task.Run(() => RunPullAsync(job));

        // Subscribers learn about the new job before the caller gets it back.
        Changed?.Invoke();
        return job;
    }

    /// <summary>Looks a job up by its identifier; null when the list no longer holds it.</summary>
    private PullJob? FindJob(Guid jobId)
    {
        // The lookup runs under the lock because pull tasks and circuits change the list concurrently.
        lock (jobsLock)
            return jobs.FirstOrDefault(job => job.Id == jobId);
    }

    /// <summary>Stops a running download and keeps it in the list as paused; Ollama keeps the partial blobs for the resume.</summary>
    public void Pause(Guid jobId)
    {
        // Unknown jobs and jobs that are not running have nothing left to pause.
        PullJob? job = FindJob(jobId);
        if (job is { IsActive: true })
        {
            // The flag is set first so the pull task records the stop as a pause when the stream aborts.
            job.IsPauseRequested = true;
            job.Cancellation.Cancel();
        }
    }

    /// <summary>Continues a paused download from the partial blobs that Ollama kept.</summary>
    public void Resume(Guid jobId)
    {
        PullJob? job;
        lock (jobsLock)
        {
            // Only a paused job can be resumed; a double click or a concurrent resume finds it already running.
            job = jobs.FirstOrDefault(candidate => candidate.Id == jobId);
            if (job is not { IsPaused: true })
                return;

            // The previous source has already fired, so the new run needs its own.
            job.State = PullState.Running;
            job.IsPauseRequested = false;
            job.Cancellation = new CancellationTokenSource();
        }

        // The pull runs detached from the requesting circuit, as in StartPull.
        _ = Task.Run(() => RunPullAsync(job));
        Changed?.Invoke();
    }

    /// <summary>Stops a running download or discards a paused one; Ollama keeps the partial blobs for a later pull.</summary>
    public void Cancel(Guid jobId)
    {
        // Unknown or finished jobs have nothing left to cancel.
        PullJob? job = FindJob(jobId);
        if (job is { IsActive: true })
        {
            // A Cancel that follows a Pause still in flight wins, so the stop is recorded as a cancellation.
            job.IsPauseRequested = false;
            job.Cancellation.Cancel();
        }
        else if (job is { IsPaused: true })
        {
            // No pull task is running for a paused job, so the state is written here.
            lock (jobsLock)
            {
                // A resume may have won the race between the check above and the lock.
                if (!job.IsPaused)
                    return;

                job.State = PullState.Cancelled;
                job.FinishedAt = DateTimeOffset.Now;
            }

            Changed?.Invoke();
        }
    }

    /// <summary>Removes finished jobs from the list, leaving running and paused downloads untouched.</summary>
    public void ClearFinished()
    {
        // Running and paused downloads stay listed while the finished ones go; subscribers then redraw the list.
        lock (jobsLock)
            jobs.RemoveAll(job => job.IsFinished);
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
            // The stop comes only from the user, through Pause or Cancel.
            job.State = job.IsPauseRequested ? PullState.Paused : PullState.Cancelled;
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
            // Every outcome notifies and every final one stamps the end time; a paused job has not ended yet.
            // The cancellation source stays undisposed: a late click on Cancel must not hit a disposed object.
            if (job.State != PullState.Paused)
                job.FinishedAt = DateTimeOffset.Now;
            Changed?.Invoke();
        }

        // The installed list changes with the pull, so every open page gets the new model at once.
        await statusMonitor.ProbeNowAsync();
    }
}
