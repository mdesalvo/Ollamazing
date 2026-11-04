using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ollamazing.Server.Configuration;
using Ollamazing.Server.Lifecycle;
using Ollamazing.Server.Status;
using ZstdSharp;

namespace Ollamazing.Server.Updates;

/// <summary>Applies a newer Ollama release to the installation: downloads, verifies and extracts its archives then swaps them in and restarts the server.</summary>
/// <remarks>Singleton: one update at a time runs in background so it survives page navigation and is visible from every circuit.</remarks>
public sealed class OllamaUpdater : IDisposable
{
    /// <summary>Folder inside the installation root that holds downloads and extracted files while an update runs.</summary>
    private const string StagingFolderName = ".ollamazing-update";

    /// <summary>Size of the buffer that copies a download to disk.</summary>
    private const int DownloadChunkSize = 1024 * 1024;

    /// <summary>Minimum interval between two progress notifications, keeping SignalR traffic bounded on fast links.</summary>
    private static readonly TimeSpan NotificationInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>How long the restarted server has to report the new version.</summary>
    private static readonly TimeSpan ServerStartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long the new binary has to print its version.</summary>
    private static readonly TimeSpan VersionCheckTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Pause between two probes while waiting for the restarted server.</summary>
    private static readonly TimeSpan ServerProbeInterval = TimeSpan.FromSeconds(1);

    private readonly OllamaUpdateMonitor updateMonitor;
    private readonly ServerStatusMonitor statusMonitor;
    private readonly OllamaServerController serverController;
    private readonly OllamaOptions ollamaOptions;
    private readonly ServerControlOptions controlOptions;
    private readonly ILogger<OllamaUpdater> logger;

    /// <summary>Client for the release downloads; the infinite timeout lets archives of several gigabytes stream to the end.</summary>
    private readonly HttpClient httpClient;

    /// <summary>Guards the check-and-set that decides whether an update may start.</summary>
    private readonly Lock startLock = new();

    /// <summary>Cancellation of the current run; never disposed so a late click on Cancel cannot hit a disposed object.</summary>
    private CancellationTokenSource? runCancellation;

    /// <summary>When the last progress notification went out.</summary>
    private DateTimeOffset lastNotification = DateTimeOffset.MinValue;

    /// <summary>True when a rollback of the current run could not restore every file.</summary>
    private bool rollbackFailed;

    public OllamaUpdater(
        OllamaUpdateMonitor updateMonitor,
        ServerStatusMonitor statusMonitor,
        OllamaServerController serverController,
        IOptions<OllamaOptions> ollamaOptions,
        IOptions<ServerControlOptions> controlOptions,
        ILogger<OllamaUpdater> logger)
    {
        this.updateMonitor = updateMonitor;
        this.statusMonitor = statusMonitor;
        this.serverController = serverController;
        this.ollamaOptions = ollamaOptions.Value;
        this.controlOptions = controlOptions.Value;
        this.logger = logger;

        // Downloads are bounded by the user's cancellation and not by a clock; GitHub rejects requests without a User-Agent.
        httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Ollamazing");
    }

    /// <summary>Raised on progress and stage changes; handlers run on the update thread.</summary>
    public event Action? Changed;

    /// <summary>Latest state of the update; null until an update has been started in this process.</summary>
    public UpdateProgress? Progress { get; private set; }

    /// <summary>Starts an update in background; does nothing while one is already running.</summary>
    public void Start()
    {
        CancellationToken runToken;
        lock (startLock)
        {
            // Two updates would fight over the same installation.
            if (Progress?.IsRunning == true)
                return;

            // The run publishes its first state before the lock opens so a second click finds it running.
            runCancellation = new CancellationTokenSource();
            runToken = runCancellation.Token;
            Progress = new UpdateProgress(updateMonitor.Current.LatestRelease?.Version ?? string.Empty, UpdateStage.Downloading, null, 0, null, null, null);
        }

        // The update runs detached from the requesting circuit which may disconnect long before it ends.
        _ = Task.Run(() => RunAsync(runToken));
        Changed?.Invoke();
    }

    /// <summary>Requests cancellation of the update while the installation is still untouched.</summary>
    public void Cancel()
    {
        // From the installing stage on a cancellation would leave a half-swapped installation.
        if (Progress?.CanCancel == true)
            runCancellation?.Cancel();
    }

    /// <summary>Cancels a running update and releases the download client.</summary>
    public void Dispose()
    {
        runCancellation?.Cancel();
        httpClient.Dispose();
    }

    /// <summary>Runs every step of the update and publishes how it ended.</summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        string? stagingRoot = null;
        rollbackFailed = false;
        try
        {
            // The release and the capability are read again because the state may have changed since the page was drawn.
            OllamaUpdateStatus currentStatus = updateMonitor.Current;
            if (currentStatus is not { LatestRelease: { } release, IsUpdateAvailable: true })
                throw new UpdateException("No newer release is known.");

            // Only an installation that can be updated in place goes on.
            UpdateCapability capability = UpdateCapabilityDetector.Detect(ollamaOptions, controlOptions, serverController.GetProcessState(), release);
            if (capability is not { Mode: UpdateMode.InPlace, ExecutablePath: { } executablePath, InstallRoot: { } installRoot })
                throw new UpdateException(capability.Reason ?? "This installation cannot be updated in place.");

            // A running server is stopped for the swap and started again afterwards.
            bool wasRunning = serverController.GetProcessState().IsRunning;

            // The staging folder sits in the installation so renames never cross a file system.
            stagingRoot = Path.Combine(installRoot, StagingFolderName);
            string downloadsFolder = Path.Combine(stagingRoot, "downloads");
            string extractedFolder = Path.Combine(stagingRoot, "extracted");

            // A leftover of an interrupted run must not leak old files into this one.
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);
            Directory.CreateDirectory(downloadsFolder);
            Directory.CreateDirectory(extractedFolder);

            // Every archive is checked against the checksum list that the release publishes.
            Dictionary<string, string> checksums = await DownloadChecksumsAsync(release, capability.AssetNames, cancellationToken);

            // One bar covers every archive so the total is known up front when the release publishes all their sizes.
            long? downloadTotal = 0;
            foreach (string assetName in capability.AssetNames)
            {
                // A single missing size makes the total unknown.
                if (release.AssetSizes.TryGetValue(assetName, out long assetSize))
                    downloadTotal += assetSize;
                else
                {
                    downloadTotal = null;
                    break;
                }
            }

            // Archives are fetched in order so later companions can overwrite files of the main archive.
            long downloadedBytes = 0;
            foreach (string assetName in capability.AssetNames)
                downloadedBytes += await DownloadAssetAsync(release, assetName, checksums[assetName], downloadsFolder, downloadedBytes, downloadTotal, cancellationToken);

            // The unpacking bar counts the compressed bytes of all the archives that were downloaded.
            long extractTotal = capability.AssetNames.Sum(assetName => new FileInfo(Path.Combine(downloadsFolder, assetName)).Length);

            // Extraction follows the same order for the same reason.
            long extractedBytes = 0;
            foreach (string assetName in capability.AssetNames)
                extractedBytes += await ExtractAssetAsync(assetName, downloadsFolder, extractedFolder, extractedBytes, extractTotal, cancellationToken);

            // The extracted files must contain the binary and the libraries at the places of the installed ones.
            string relativeExecutable = Path.GetRelativePath(installRoot, executablePath);
            string newExecutable = Path.Combine(extractedFolder, relativeExecutable);
            string newLibraries = Path.Combine(extractedFolder, "lib", "ollama");
            if (!File.Exists(newExecutable))
                throw new UpdateException($"The archive does not contain {relativeExecutable}.");
            if (!Directory.Exists(newLibraries))
                throw new UpdateException("The archive does not contain lib/ollama.");

            // A binary that does not announce the release version is not installed over a working one.
            await VerifyNewBinaryAsync(newExecutable, release.Version, cancellationToken);

            // Up to here nothing in the installation has changed.
            await InstallAsync(release, executablePath, installRoot, stagingRoot, newExecutable, newLibraries, wasRunning, cancellationToken);

            // The staging folder holds only leftovers now.
            DeleteStagingRoot(stagingRoot);
            Finish(UpdateStage.Completed, null);

            // Every page learns the new version at once.
            await statusMonitor.ProbeNowAsync();
        }
        catch (OperationCanceledException)
        {
            // Cancellation comes only from the user or from shutdown.
            DeleteStagingRootUnlessKept(stagingRoot);
            Finish(UpdateStage.Cancelled, null);
        }
        catch (Exception updateError)
        {
            // Expected failures carry a sentence for the user and unexpected ones surface with the message of the exception.
            logger.LogWarning(updateError, "Update to Ollama {Version} failed", Progress?.Version);
            string errorMessage = updateError.Message;
            if (rollbackFailed)
                errorMessage += $" The previous files are kept in {stagingRoot}.";
            DeleteStagingRootUnlessKept(stagingRoot);
            Finish(UpdateStage.Failed, errorMessage);
        }
    }

    /// <summary>Reads the checksum list of the release and returns the lowercase hash of every wanted archive.</summary>
    private async Task<Dictionary<string, string>> DownloadChecksumsAsync(OllamaRelease release, IReadOnlyList<string> assetNames, CancellationToken cancellationToken)
    {
        // The list is a small text file published next to the archives.
        string checksumText = await httpClient.GetStringAsync(AssetUrl(release, "sha256sum.txt"), cancellationToken);

        // Each line reads "<hash>  ./<name>" so the first token is the hash and the last one is the name.
        Dictionary<string, string> checksums = new(StringComparer.Ordinal);
        foreach (string line in checksumText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Blank or malformed lines carry no checksum.
            string[] tokens = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
                continue;

            // The name may be written relative to the current folder.
            string assetName = tokens[^1].StartsWith("./", StringComparison.Ordinal) ? tokens[^1][2..] : tokens[^1];
            checksums[assetName] = tokens[0].ToLowerInvariant();
        }

        // An archive without a checksum cannot be trusted so the update does not start.
        foreach (string assetName in assetNames)
        {
            if (!checksums.ContainsKey(assetName))
                throw new UpdateException($"Release {release.Version} publishes no checksum for {assetName}.");
        }

        // The caller indexes the list by archive name.
        return checksums;
    }

    /// <summary>Downloads one archive into the downloads folder and returns its size after rejecting it when its checksum differs.</summary>
    private async Task<long> DownloadAssetAsync(OllamaRelease release, string assetName, string expectedChecksum, string downloadsFolder, long bytesBefore, long? downloadTotal, CancellationToken cancellationToken)
    {
        // Headers are enough to start: the body streams straight to disk.
        using HttpResponseMessage response = await httpClient.GetAsync(AssetUrl(release, assetName), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // The bar spans all the archives when their total is known.
        long? bytesTotal = downloadTotal;
        if (downloadTotal is null)
        {
            // Without a known total the bar falls back to this archive alone so it restarts from zero.
            bytesTotal = response.Content.Headers.ContentLength;
            bytesBefore = 0;
        }
        ReportStage(UpdateStage.Downloading, assetName, bytesBefore, bytesTotal);

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream archiveFile = new(Path.Combine(downloadsFolder, assetName), FileMode.Create, FileAccess.Write, FileShare.None, DownloadChunkSize, useAsync: true);

        // The hash grows with the bytes written so the archive is never read twice.
        using IncrementalHash archiveHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] chunk = new byte[DownloadChunkSize];
        long bytesDone = 0;
        int bytesRead;
        while ((bytesRead = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            // The chunk reaches the disk and the hash in the order it arrived.
            await archiveFile.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
            archiveHash.AppendData(chunk, 0, bytesRead);

            // The bar follows the bytes saved so far.
            bytesDone += bytesRead;
            ReportBytes(bytesBefore + bytesDone, bytesTotal);
        }

        // A mismatch means a damaged or tampered download: nothing of it may reach the installation.
        string actualChecksum = Convert.ToHexStringLower(archiveHash.GetHashAndReset());
        if (!string.Equals(actualChecksum, expectedChecksum, StringComparison.Ordinal))
            throw new UpdateException($"The download of {assetName} is corrupted: its checksum does not match.");

        // The caller adds this archive to the bytes that precede the next one.
        return bytesDone;
    }

    /// <summary>Unpacks one archive over the extracted folder then deletes it to free disk space and returns its length.</summary>
    private async Task<long> ExtractAssetAsync(string assetName, string downloadsFolder, string extractedFolder, long bytesBefore, long extractTotal, CancellationToken cancellationToken)
    {
        // The bar of extraction counts the compressed bytes consumed out of the size of all the archives.
        string archivePath = Path.Combine(downloadsFolder, assetName);
        long archiveLength = new FileInfo(archivePath).Length;
        ReportStage(UpdateStage.Extracting, assetName, bytesBefore, extractTotal);

        // The archive format follows from the extension that the detector accepted.
        if (assetName.EndsWith(".tar.zst", StringComparison.Ordinal))
        {
            // Linux archives are tar streams compressed with zstd.
            await using FileStream archiveFile = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, DownloadChunkSize, useAsync: true);
            await using ProgressStream countedStream = new(archiveFile, position => ReportBytes(bytesBefore + position, extractTotal));
            await using DecompressionStream decompressedStream = new(countedStream);
            await TarFile.ExtractToDirectoryAsync(decompressedStream, extractedFolder, overwriteFiles: true, cancellationToken);
        }
        else
        {
            // Windows archives are zip files which extract without a progress hook.
            cancellationToken.ThrowIfCancellationRequested();
            ZipFile.ExtractToDirectory(archivePath, extractedFolder, overwriteFiles: true);
        }

        // The extracted files replace the archive so the staging folder never holds both.
        File.Delete(archivePath);

        // The caller adds this length to the bytes that precede the next archive.
        return archiveLength;
    }

    /// <summary>Runs the new binary with -v and fails when it does not report the release version.</summary>
    private static async Task VerifyNewBinaryAsync(string newExecutable, string version, CancellationToken cancellationToken)
    {
        // The version flag works with or without a running server and prints on either stream.
        ProcessStartInfo startInfo = new(newExecutable, "-v")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using Process process = Process.Start(startInfo) ?? throw new UpdateException("The new binary could not be started.");
        try
        {
            // Both streams are drained so a full pipe never blocks the child.
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);

            // A binary that hangs is killed once the time to answer is over.
            using CancellationTokenSource versionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            versionTimeout.CancelAfter(VersionCheckTimeout);
            try
            {
                await process.WaitForExitAsync(versionTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Only the version timeout ended the wait so the process is killed and the output read as far as it got.
                process.Kill(entireProcessTree: true);
            }

            // The release version must appear in what the binary printed.
            string combinedOutput = await standardOutput + await standardError;
            if (!combinedOutput.Contains(version, StringComparison.Ordinal))
                throw new UpdateException($"The new binary does not report version {version}.");
        }
        finally
        {
            // A cancellation or an error must not leave the probe running.
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>Swaps the new files into the installation and restarts the server restoring the previous version on any failure.</summary>
    private async Task InstallAsync(
        OllamaRelease release,
        string executablePath,
        string installRoot,
        string stagingRoot,
        string newExecutable,
        string newLibraries,
        bool wasRunning,
        CancellationToken cancellationToken)
    {
        // From here on the installation changes and the user can no longer cancel.
        ReportStage(UpdateStage.Installing, null, 0, null);

        // The swap cannot replace files under a running server.
        if (wasRunning)
        {
            ServerActionResult stopResult = serverController.Execute(ServerAction.Stop);
            if (!stopResult.Succeeded)
                throw new UpdateException(stopResult.Message);
        }

        // The installed files move into the staging folder so a rollback only moves them back.
        string installedLibraries = Path.Combine(installRoot, "lib", "ollama");
        string previousLibraries = Path.Combine(stagingRoot, "previous-lib");
        string previousExecutable = Path.Combine(stagingRoot, "previous-" + Path.GetFileName(executablePath));

        // Each flag records a completed move so a rollback undoes exactly what happened.
        bool librariesMoved = false;
        bool executableMoved = false;
        bool newLibrariesPlaced = false;
        bool newExecutablePlaced = false;
        try
        {
            // The installed libraries step aside first.
            Directory.Move(installedLibraries, previousLibraries);
            librariesMoved = true;

            // The installed binary follows them.
            File.Move(executablePath, previousExecutable);
            executableMoved = true;

            // The new libraries take the place of the old ones.
            Directory.Move(newLibraries, installedLibraries);
            newLibrariesPlaced = true;

            // The new binary goes where the old one was so the configured path keeps working.
            File.Move(newExecutable, executablePath);
            newExecutablePlaced = true;
        }
        catch (Exception swapError) when (swapError is IOException or UnauthorizedAccessException)
        {
            // The installation goes back to the previous version and the server comes back on it.
            RollBack(executablePath, installedLibraries, previousLibraries, previousExecutable, librariesMoved, executableMoved, newLibrariesPlaced, newExecutablePlaced);
            if (wasRunning && !rollbackFailed)
                serverController.Execute(ServerAction.Start);
            throw new UpdateException($"Installing the new files failed: {swapError.Message}" + (rollbackFailed ? string.Empty : " The previous version was restored."));
        }

        // A server that was stopped for the swap must come back on the new version.
        if (!wasRunning)
            return;
        ReportStage(UpdateStage.Restarting, null, 0, null);
        if (await StartAndWaitForVersionAsync(release.Version, cancellationToken))
            return;

        // The new version does not come up so the previous one takes its place again.
        serverController.Execute(ServerAction.Stop);
        RollBack(executablePath, installedLibraries, previousLibraries, previousExecutable, true, true, true, true);
        if (!rollbackFailed)
            serverController.Execute(ServerAction.Start);
        throw new UpdateException($"Ollama {release.Version} did not start." + (rollbackFailed ? string.Empty : " The previous version was restored."));
    }

    /// <summary>Starts the server and waits until it reports the wanted version; false when it does not within the timeout.</summary>
    private async Task<bool> StartAndWaitForVersionAsync(string version, CancellationToken cancellationToken)
    {
        // A server that cannot even start will not report anything.
        ServerActionResult startResult = serverController.Execute(ServerAction.Start);
        if (!startResult.Succeeded)
            return false;

        // The probe runs every second until the new version answers or the time is over.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ServerStartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            // A reachable server on the wanted version proves the new files work.
            await statusMonitor.ProbeNowAsync(cancellationToken);
            if (statusMonitor.Current.IsReachable && string.Equals(statusMonitor.Current.Version, version, StringComparison.Ordinal))
                return true;
            await Task.Delay(ServerProbeInterval, cancellationToken);
        }

        // The server never reported the new version in time.
        return false;
    }

    /// <summary>Undoes in reverse order the moves that completed and records when a file could not be restored.</summary>
    private void RollBack(
        string executablePath,
        string installedLibraries,
        string previousLibraries,
        string previousExecutable,
        bool librariesMoved,
        bool executableMoved,
        bool newLibrariesPlaced,
        bool newExecutablePlaced)
    {
        // Each undo is isolated so one failure still lets the others restore what they can.
        if (newExecutablePlaced)
            TryUndo(() => File.Delete(executablePath), "removing the new binary");
        if (newLibrariesPlaced)
            TryUndo(() => Directory.Delete(installedLibraries, recursive: true), "removing the new libraries");
        if (executableMoved)
            TryUndo(() => File.Move(previousExecutable, executablePath), "restoring the previous binary");
        if (librariesMoved)
            TryUndo(() => Directory.Move(previousLibraries, installedLibraries), "restoring the previous libraries");
    }

    /// <summary>Runs one rollback step and logs its failure instead of throwing.</summary>
    private void TryUndo(Action undoStep, string description)
    {
        try
        {
            undoStep();
        }
        catch (Exception undoError)
        {
            // A step that fails leaves files in the staging folder which the final message points to.
            logger.LogError(undoError, "Rollback failed while {Description}", description);
            rollbackFailed = true;
        }
    }

    /// <summary>Deletes the staging folder of a run that did not complete unless it holds the only copy of the previous files.</summary>
    private void DeleteStagingRootUnlessKept(string? stagingRoot)
    {
        // Without a staging folder the run failed before touching the disk and with a failed rollback the folder is the recovery copy.
        if (stagingRoot is null || rollbackFailed)
            return;
        DeleteStagingRoot(stagingRoot);
    }

    /// <summary>Deletes the staging folder and only logs a failure since the update itself has already ended.</summary>
    private void DeleteStagingRoot(string stagingRoot)
    {
        try
        {
            // A folder that is already gone needs no work.
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);
        }
        catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
        {
            // Leftovers are removed by the next update.
            logger.LogWarning(deleteError, "Removing the staging folder {StagingRoot} failed", stagingRoot);
        }
    }

    /// <summary>Builds the download address of a release asset.</summary>
    private static string AssetUrl(OllamaRelease release, string assetName) =>
        $"https://github.com/ollama/ollama/releases/download/{release.Tag}/{assetName}";

    /// <summary>Publishes a new stage immediately.</summary>
    private void ReportStage(UpdateStage stage, string? assetName, long bytesDone, long? bytesTotal)
    {
        // A stage change always reaches the pages.
        Progress = new UpdateProgress(Progress?.Version ?? string.Empty, stage, assetName, bytesDone, bytesTotal, null, null);
        lastNotification = DateTimeOffset.UtcNow;
        Changed?.Invoke();
    }

    /// <summary>Updates the byte counters of the current stage and notifies at most once per interval.</summary>
    private void ReportBytes(long bytesDone, long? bytesTotal)
    {
        // Counters make sense only inside a stage.
        if (Progress is not { } currentProgress)
            return;
        Progress = currentProgress with { BytesDone = bytesDone, BytesTotal = bytesTotal };

        // The throttle keeps fast links from flooding the circuits.
        if (DateTimeOffset.UtcNow - lastNotification >= NotificationInterval)
        {
            lastNotification = DateTimeOffset.UtcNow;
            Changed?.Invoke();
        }
    }

    /// <summary>Publishes how the update ended.</summary>
    private void Finish(UpdateStage stage, string? error)
    {
        // The final state is never throttled.
        Progress = new UpdateProgress(Progress?.Version ?? string.Empty, stage, null, 0, null, error, DateTimeOffset.Now);
        Changed?.Invoke();
    }

    /// <summary>Failure with a sentence that is ready to show to the user.</summary>
    private sealed class UpdateException(string message) : Exception(message);

    /// <summary>Read-only stream that reports the position of the wrapped stream after every read.</summary>
    private sealed class ProgressStream(Stream innerStream, Action<long> onPositionChanged) : Stream
    {
        /// <summary>Reads are forwarded to the wrapped stream.</summary>
        public override bool CanRead => true;

        /// <summary>The decompressor reads forward only.</summary>
        public override bool CanSeek => false;

        /// <summary>The wrapped archive is never written.</summary>
        public override bool CanWrite => false;

        /// <summary>Length of the wrapped stream.</summary>
        public override long Length => innerStream.Length;

        /// <summary>Position of the wrapped stream; it cannot be set.</summary>
        public override long Position
        {
            get => innerStream.Position;
            set => throw new NotSupportedException();
        }

        /// <summary>Reads from the wrapped stream and reports the new position.</summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            // The caller gets the bytes and the counter follows them.
            int bytesRead = innerStream.Read(buffer, offset, count);
            onPositionChanged(innerStream.Position);
            return bytesRead;
        }

        /// <summary>Reads from the wrapped stream into a span and reports the new position.</summary>
        public override int Read(Span<byte> buffer)
        {
            // The caller gets the bytes and the counter follows them.
            int bytesRead = innerStream.Read(buffer);
            onPositionChanged(innerStream.Position);
            return bytesRead;
        }

        /// <summary>Reads asynchronously from the wrapped stream and reports the new position.</summary>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // The caller gets the bytes and the counter follows them.
            int bytesRead = await innerStream.ReadAsync(buffer, cancellationToken);
            onPositionChanged(innerStream.Position);
            return bytesRead;
        }

        /// <summary>Nothing is buffered for writing.</summary>
        public override void Flush()
        {
        }

        /// <summary>Seeking is not supported.</summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>Resizing is not supported.</summary>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>Writing is not supported.</summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
