using System.Runtime.InteropServices;
using Ollamazing.Server.Configuration;
using Ollamazing.Server.Lifecycle;

namespace Ollamazing.Server.Updates;

/// <summary>Decides whether Ollamazing could replace the installed Ollama files or can only notify.</summary>
/// <remarks>
/// The checks run from the cheapest and most permanent obstacle to the transient one: the write probe touches the disk
/// so it runs only when everything else has passed. A server started elsewhere is reported last since starting it from Ollamazing fixes it.
/// </remarks>
public static class UpdateCapabilityDetector
{
    /// <summary>Returns the first obstacle found to updating in place; an in-place capability when there is none.</summary>
    public static UpdateCapability Detect(OllamaOptions ollamaOptions, ServerControlOptions controlOptions, ServerProcessState processState, OllamaRelease release)
    {
        // A server on another machine cannot be reached through the local file system.
        Uri serverUri = new(ollamaOptions.BaseUrl);
        if (!serverUri.IsLoopback)
            return NotifyOnly($"The server at {serverUri.Host} runs on another machine.", null, null);

        // Every later check needs the real binary behind the configured name.
        string? executablePath = ResolveExecutable(controlOptions.ExecutablePath);
        if (executablePath is null)
            return NotifyOnly($"The executable '{controlOptions.ExecutablePath}' was not found.", null, null);

        // The files of the executable's folder tell which kind of installation this is.
        string executableFolder = Path.GetDirectoryName(executablePath) ?? string.Empty;

        // The desktop app ships its own updater and would fight an update made from here.
        if (executablePath.Contains(".app/Contents/", StringComparison.Ordinal) || File.Exists(Path.Combine(executableFolder, "ollama app.exe")))
            return NotifyOnly("This Ollama belongs to the desktop app, which updates itself.", executablePath, null);

        // Homebrew keeps its own record of the installed files.
        if (executablePath.Contains("/Cellar/", StringComparison.Ordinal))
            return NotifyOnly("This Ollama is managed by Homebrew.", executablePath, null);

        // The official archives keep the runtime libraries in lib/ollama next to or one level above the binary.
        string? installRoot = FindInstallRoot(executableFolder);
        if (installRoot is null)
            return NotifyOnly($"The files in {executableFolder} are not laid out like an official Ollama archive.", executablePath, null);

        // The release must publish the archive that matches this machine.
        string? expectedAssetName = ExpectedAssetName();
        if (expectedAssetName is null || !release.AssetNames.Contains(expectedAssetName, StringComparer.Ordinal))
            return NotifyOnly($"Release {release.Version} has no archive for {RuntimeInformation.RuntimeIdentifier}.", executablePath, installRoot);

        // An update replaces both the binary and the libraries, so Ollamazing must be able to write to both folders.
        foreach (string folderToProbe in new[] { executableFolder, Path.Combine(installRoot, "lib", "ollama") })
        {
            if (!CanWriteTo(folderToProbe))
                return NotifyOnly($"Ollamazing cannot write to {folderToProbe}.", executablePath, installRoot);
        }

        // Replacing files under a server that Ollamazing does not own would leave it running the old binary.
        if (processState.IsRunning && !processState.IsOwned)
            return NotifyOnly("The server was started outside Ollamazing: start it from here to allow updates in place.", executablePath, installRoot);

        // Nothing stands in the way of an update.
        return new UpdateCapability(UpdateMode.InPlace, null, executablePath, installRoot);
    }

    /// <summary>Builds the capability that reports an obstacle together with the paths found before it.</summary>
    private static UpdateCapability NotifyOnly(string reason, string? executablePath, string? installRoot) =>
        new(UpdateMode.NotifyOnly, reason, executablePath, installRoot);

    /// <summary>Finds the real file behind the configured executable name; null when it does not exist.</summary>
    private static string? ResolveExecutable(string configuredPath)
    {
        try
        {
            // A bare name is searched like a shell would; a path is taken as given.
            string? foundPath = FindExecutable(configuredPath);
            if (foundPath is null)
                return null;

            // A symbolic link on PATH must lead to the folder that really holds the installation.
            return File.ResolveLinkTarget(foundPath, returnFinalTarget: true)?.FullName ?? foundPath;
        }
        catch (Exception resolutionError) when (resolutionError is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A path that cannot be inspected is as good as missing.
            return null;
        }
    }

    /// <summary>Locates the executable file from a full path, a relative path or a bare name looked up in PATH.</summary>
    private static string? FindExecutable(string configuredPath)
    {
        // A full path needs no search.
        if (Path.IsPathFullyQualified(configuredPath))
            return File.Exists(configuredPath) ? configuredPath : null;

        // A relative path with a folder part is relative to the working directory.
        if (configuredPath.Contains(Path.DirectorySeparatorChar) || configuredPath.Contains(Path.AltDirectorySeparatorChar))
        {
            string fullPath = Path.GetFullPath(configuredPath);
            return File.Exists(fullPath) ? fullPath : null;
        }

        // A bare name is looked up in every PATH folder in order; the first match is the one a shell would run.
        string[] searchFolders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (string searchFolder in searchFolders)
        {
            // Windows runs "ollama" as "ollama.exe" when the name has no extension.
            List<string> candidatePaths = [Path.Combine(searchFolder, configuredPath)];
            if (OperatingSystem.IsWindows() && !Path.HasExtension(configuredPath))
                candidatePaths.Add(Path.Combine(searchFolder, configuredPath + ".exe"));

            // The first candidate that exists wins.
            string? existingPath = candidatePaths.FirstOrDefault(File.Exists);
            if (existingPath is not null)
                return existingPath;
        }

        // The name is on no PATH folder.
        return null;
    }

    /// <summary>Returns the folder holding the installation when it follows the official archive layout; null otherwise.</summary>
    private static string? FindInstallRoot(string executableFolder)
    {
        // Windows archives keep lib/ollama next to the binary.
        if (Directory.Exists(Path.Combine(executableFolder, "lib", "ollama")))
            return executableFolder;

        // Linux archives put the binary in bin/ and the libraries in the folder above.
        string? parentFolder = Path.GetDirectoryName(executableFolder);
        if (parentFolder is not null && Directory.Exists(Path.Combine(parentFolder, "lib", "ollama")))
            return parentFolder;

        // Neither layout matches.
        return null;
    }

    /// <summary>Names the archive of this machine in the release assets; null when Ollama publishes none for it.</summary>
    private static string? ExpectedAssetName()
    {
        // Ollama names its archives with amd64 and arm64 only.
        string? architectureName = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => null
        };

        // The macOS archive is a single universal build.
        if (OperatingSystem.IsMacOS())
            return "ollama-darwin.tgz";

        // Linux and Windows archives exist per architecture.
        if (architectureName is null)
            return null;
        if (OperatingSystem.IsLinux())
            return $"ollama-linux-{architectureName}.tar.zst";
        if (OperatingSystem.IsWindows())
            return $"ollama-windows-{architectureName}.zip";

        // No archive exists for other systems.
        return null;
    }

    /// <summary>Tells whether a file can be created in the folder by creating one that vanishes at once.</summary>
    private static bool CanWriteTo(string folder)
    {
        try
        {
            // The probe file is deleted as soon as it is closed so nothing is left in the installation.
            string probePath = Path.Combine(folder, $".ollamazing-{Guid.NewGuid():N}.tmp");
            using FileStream probeStream = new(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception writeError) when (writeError is UnauthorizedAccessException or IOException)
        {
            // The folder is read-only for this user or does not exist.
            return false;
        }
    }
}
