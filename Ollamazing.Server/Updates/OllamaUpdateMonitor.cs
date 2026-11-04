using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ollamazing.Server.Configuration;
using Ollamazing.Server.Lifecycle;
using Ollamazing.Server.Status;

namespace Ollamazing.Server.Updates;

/// <summary>Checks GitHub for a newer Ollama release and publishes the outcome to every connected circuit.</summary>
/// <remarks>
/// Singleton: the check runs at startup and then on a timer. Its outcome is evaluated again whenever the installed version
/// or the hosting process of the server changes. The write probe of the capability check never runs at every status probe.
/// </remarks>
public sealed class OllamaUpdateMonitor : BackgroundService
{
    /// <summary>GitHub answers with the latest release that is neither a draft nor a prerelease.</summary>
    private const string LatestReleaseUrl = "https://api.github.com/repos/ollama/ollama/releases/latest";

    private readonly ServerStatusMonitor statusMonitor;
    private readonly OllamaServerController serverController;
    private readonly OllamaOptions ollamaOptions;
    private readonly ServerControlOptions controlOptions;
    private readonly ILogger<OllamaUpdateMonitor> logger;

    /// <summary>Client for the GitHub API; GitHub rejects requests that carry no User-Agent.</summary>
    private readonly HttpClient httpClient;

    /// <summary>Guards the evaluation, which runs from both the check thread and the status thread.</summary>
    private readonly Lock evaluationLock = new();

    /// <summary>Latest release from the last successful check; kept when a later check fails.</summary>
    private OllamaRelease? latestRelease;

    /// <summary>When the last successful check happened.</summary>
    private DateTimeOffset? checkedAt;

    /// <summary>Reason of the last failed check; null when the last check succeeded.</summary>
    private string? lastError;

    /// <summary>Version and hosting process seen at the previous status change, to evaluate only when they differ.</summary>
    private (string? Version, bool IsRunning, bool IsOwned) lastSeenServer;

    public OllamaUpdateMonitor(
        ServerStatusMonitor statusMonitor,
        OllamaServerController serverController,
        IOptions<OllamaOptions> ollamaOptions,
        IOptions<ServerControlOptions> controlOptions,
        ILogger<OllamaUpdateMonitor> logger)
    {
        this.statusMonitor = statusMonitor;
        this.serverController = serverController;
        this.ollamaOptions = ollamaOptions.Value;
        this.controlOptions = controlOptions.Value;
        this.logger = logger;

        // A request slower than this counts as a failed check instead of hanging the button.
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Ollamazing");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        // A server that comes up or changes version changes what the release is compared with.
        statusMonitor.Changed += OnServerStatusChanged;
    }

    /// <summary>True when the configuration asks for update checks.</summary>
    public bool IsEnabled => controlOptions.UpdateCheckHours > 0;

    /// <summary>Raised after every check and every change of the evaluation; handlers run on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Outcome of the latest evaluation.</summary>
    public OllamaUpdateStatus Current { get; private set; } = OllamaUpdateStatus.Initial;

    /// <summary>Stops following the server status when the application shuts down.</summary>
    public override void Dispose()
    {
        statusMonitor.Changed -= OnServerStatusChanged;
        httpClient.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A disabled check never contacts GitHub.
        if (!IsEnabled)
            return;

        // The interval is clamped to one hour so a misconfiguration cannot hammer GitHub.
        using PeriodicTimer timer = new(TimeSpan.FromHours(Math.Max(1, controlOptions.UpdateCheckHours)));
        do
        {
            try
            {
                // The new release or the new error is evaluated against the installed version and published.
                await FetchLatestReleaseAsync(stoppingToken);
                Evaluate();
                Changed?.Invoke();
            }
            catch (Exception checkError) when (checkError is not OperationCanceledException)
            {
                // An unexpected failure must not stop the loop: the next tick tries again.
                logger.LogWarning(checkError, "Update check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Downloads the latest release description and stores it or the reason it could not be read.</summary>
    private async Task FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            GitHubRelease? gitHubRelease = await httpClient.GetFromJsonAsync<GitHubRelease>(LatestReleaseUrl, cancellationToken);

            // A release without tag or page cannot be compared or shown.
            if (gitHubRelease?.TagName is null || gitHubRelease.HtmlUrl is null)
            {
                lastError = "GitHub returned an incomplete release.";
                return;
            }

            // The sizes let the page tell the user how big the download is before it starts.
            Dictionary<string, long> assetSizes = new(StringComparer.Ordinal);
            foreach (GitHubAsset asset in gitHubRelease.Assets ?? [])
            {
                // Two assets with the same name cannot be told apart so the first one wins.
                assetSizes.TryAdd(asset.Name, asset.Size);
            }

            // Ollama tags releases as "v0.40.14" while the server reports "0.40.14".
            latestRelease = new OllamaRelease(
                gitHubRelease.TagName.TrimStart('v'),
                gitHubRelease.TagName,
                gitHubRelease.HtmlUrl,
                gitHubRelease.PublishedAt,
                (gitHubRelease.Assets ?? []).Select(asset => asset.Name).ToList(),
                assetSizes);
            checkedAt = DateTimeOffset.Now;
            lastError = null;
        }
        catch (Exception fetchError) when (fetchError is HttpRequestException or JsonException or NotSupportedException || (fetchError is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // An unreachable GitHub, a body that is not a release or a timeout: the previous release stays visible next to the reason.
            logger.LogWarning(fetchError, "Checking for a new Ollama release failed");
            lastError = fetchError.Message;
        }
    }

    /// <summary>Compares the installed version with the stored release and decides how an update could be applied.</summary>
    private void Evaluate()
    {
        lock (evaluationLock)
        {
            // The installed version is known only while the server answers.
            string? installedVersion = statusMonitor.Current.Version;
            OllamaRelease? knownRelease = latestRelease;

            // An update exists only when both versions are readable and the release is newer.
            bool isUpdateAvailable = knownRelease is not null
                && TryParseVersion(installedVersion, out Version? installed)
                && TryParseVersion(knownRelease.Version, out Version? latest)
                && latest > installed;

            // How to update matters only once a release is known.
            UpdateCapability? capability = knownRelease is null
                ? null
                : UpdateCapabilityDetector.Detect(ollamaOptions, controlOptions, serverController.GetProcessState(), knownRelease);

            Current = new OllamaUpdateStatus
            {
                InstalledVersion = installedVersion,
                LatestRelease = knownRelease,
                IsUpdateAvailable = isUpdateAvailable,
                Capability = capability,
                CheckedAt = checkedAt,
                LastError = lastError
            };
        }
    }

    /// <summary>Evaluates again when the installed version or the hosting process of the server has changed.</summary>
    private void OnServerStatusChanged()
    {
        // The first probe has not answered yet so there is nothing to compare.
        ServerStatus serverStatus = statusMonitor.Current;
        if (serverStatus.IsInitial)
            return;

        // Most probes repeat the previous state and must not trigger the write probe again.
        (string? Version, bool IsRunning, bool IsOwned) currentServer = (serverStatus.Version, serverStatus.Process.IsRunning, serverStatus.Process.IsOwned);
        if (currentServer == lastSeenServer)
            return;
        lastSeenServer = currentServer;

        // A server that came up or was updated changes both the comparison and the capability.
        Evaluate();
        Changed?.Invoke();
    }

    /// <summary>Reads a release version such as "v0.40.14-rc1" into a comparable value; false when it is not numeric.</summary>
    private static bool TryParseVersion(string? text, out Version? version)
    {
        // A missing version cannot be compared.
        version = null;
        if (text is null)
            return false;

        // Prerelease and build suffixes do not take part in the comparison.
        string numericPart = text.TrimStart('v');
        int suffixStart = numericPart.IndexOfAny(['-', '+']);
        if (suffixStart >= 0)
            numericPart = numericPart[..suffixStart];
        return Version.TryParse(numericPart, out version);
    }

    /// <summary>Release as GitHub serialises it, reduced to the fields in use.</summary>
    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    /// <summary>Archive attached to a GitHub release.</summary>
    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Size of the archive in bytes.</summary>
        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
