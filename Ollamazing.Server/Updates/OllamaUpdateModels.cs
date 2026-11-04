namespace Ollamazing.Server.Updates;

/// <summary>A stable Ollama release published on GitHub.</summary>
/// <param name="Version">Release tag without the leading "v", such as "0.40.14".</param>
/// <param name="Tag">Release tag as published on GitHub, such as "v0.40.14", which names the download folder of its assets.</param>
/// <param name="Url">Page of the release on GitHub, which holds its notes.</param>
/// <param name="PublishedAt">When GitHub published the release.</param>
/// <param name="AssetNames">File names of the archives attached to the release.</param>
/// <param name="AssetSizes">Size in bytes of every archive attached to the release, by file name.</param>
public sealed record OllamaRelease(string Version, string Tag, string Url, DateTimeOffset PublishedAt, IReadOnlyList<string> AssetNames, IReadOnlyDictionary<string, long> AssetSizes);

/// <summary>What Ollamazing can do about a newer release.</summary>
public enum UpdateMode
{
    /// <summary>Ollamazing can replace the installed files itself.</summary>
    InPlace,

    /// <summary>Ollamazing can only tell that a release exists; the user updates by hand.</summary>
    NotifyOnly
}

/// <summary>Whether the installation can be updated by Ollamazing and the facts that decided it.</summary>
/// <param name="Mode">Whether the installation can be updated in place or only reported.</param>
/// <param name="Reason">Full sentence saying why only a notification is possible; null for an update in place.</param>
/// <param name="ExecutablePath">Real path of the Ollama binary after following links; null when not found.</param>
/// <param name="InstallRoot">Folder holding the installation; null when the layout was not recognised.</param>
/// <param name="AssetNames">Archives to download and extract in this order, the main archive first; empty when only a notification is possible.</param>
public sealed record UpdateCapability(UpdateMode Mode, string? Reason, string? ExecutablePath, string? InstallRoot, IReadOnlyList<string> AssetNames);

/// <summary>Step of an update that Ollamazing applies in place.</summary>
public enum UpdateStage
{
    /// <summary>The archives and their checksums are being downloaded.</summary>
    Downloading,

    /// <summary>The downloaded archives are being unpacked into the staging folder.</summary>
    Extracting,

    /// <summary>The installed files are being swapped for the new ones.</summary>
    Installing,

    /// <summary>The server is being started again on the new version.</summary>
    Restarting,

    /// <summary>The update finished and the new version runs.</summary>
    Completed,

    /// <summary>The update stopped on an error and the installation is as it was.</summary>
    Failed,

    /// <summary>The user cancelled the update before the installation was touched.</summary>
    Cancelled
}

/// <summary>Snapshot of an update run for the pages that show it.</summary>
/// <param name="Version">Version being installed.</param>
/// <param name="Stage">Step the update is at or the way it ended.</param>
/// <param name="AssetName">Archive being downloaded or extracted; null in the other stages.</param>
/// <param name="BytesDone">Bytes of the current archive already downloaded or read.</param>
/// <param name="BytesTotal">Size of the current archive; null when unknown.</param>
/// <param name="Error">Full sentence saying why the update failed; null otherwise.</param>
/// <param name="FinishedAt">When the update ended; null while it runs.</param>
public sealed record UpdateProgress(string Version, UpdateStage Stage, string? AssetName, long BytesDone, long? BytesTotal, string? Error, DateTimeOffset? FinishedAt)
{
    /// <summary>True while the update has not ended.</summary>
    public bool IsRunning => Stage is UpdateStage.Downloading or UpdateStage.Extracting or UpdateStage.Installing or UpdateStage.Restarting;

    /// <summary>True while the installation is still untouched so the user can still back out.</summary>
    public bool CanCancel => Stage is UpdateStage.Downloading or UpdateStage.Extracting;
}

/// <summary>Outcome of comparing the installed Ollama with the latest GitHub release.</summary>
public sealed record OllamaUpdateStatus
{
    /// <summary>Version reported by the running server; null while the server is down.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>Latest stable release seen on GitHub; null until a check succeeds.</summary>
    public OllamaRelease? LatestRelease { get; init; }

    /// <summary>True when the latest release is newer than the installed version.</summary>
    public bool IsUpdateAvailable { get; init; }

    /// <summary>How the installation can be updated; null while no release is known.</summary>
    public UpdateCapability? Capability { get; init; }

    /// <summary>When GitHub last answered successfully.</summary>
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>Reason of the last failed check; null when the last check succeeded.</summary>
    public string? LastError { get; init; }

    /// <summary>State before any check has run.</summary>
    public static OllamaUpdateStatus Initial { get; } = new();
}
