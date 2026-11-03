namespace Ollamazing.Server.Updates;

/// <summary>A stable Ollama release published on GitHub.</summary>
/// <param name="Version">Release tag without the leading "v", such as "0.40.14".</param>
/// <param name="Url">Page of the release on GitHub, which holds its notes.</param>
/// <param name="PublishedAt">When GitHub published the release.</param>
/// <param name="AssetNames">File names of the archives attached to the release.</param>
public sealed record OllamaRelease(string Version, string Url, DateTimeOffset PublishedAt, IReadOnlyList<string> AssetNames);

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
public sealed record UpdateCapability(UpdateMode Mode, string? Reason, string? ExecutablePath, string? InstallRoot);

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
