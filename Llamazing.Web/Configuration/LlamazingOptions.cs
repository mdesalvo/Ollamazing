namespace Llamazing.Web.Configuration;

/// <summary>Connection settings for the Ollama HTTP API driven through OllamaSharp.</summary>
public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    /// <summary>Base address of the Ollama server; the default matches a stock local installation.</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>How long a warmed-up model stays resident on the server, in Ollama keep_alive syntax (e.g. "30m", "-1").</summary>
    public string WarmUpKeepAlive { get; set; } = "30m";

    /// <summary>Interval between two status probes of the server, in seconds.</summary>
    public int StatusPollSeconds { get; set; } = 3;
}

/// <summary>Settings for starting and stopping the Ollama server from the control panel.</summary>
/// <remarks>The server is managed as a plain OS process through .NET, identically on Windows, macOS and Linux.</remarks>
public sealed class ServerControlOptions
{
    public const string SectionName = "ServerControl";

    /// <summary>Path of the ollama executable; a bare name is looked up in PATH.</summary>
    public string ExecutablePath { get; set; } = "ollama";

    /// <summary>Extra environment variables for the server started by Llamazing (OLLAMA_MODELS, OLLAMA_KEEP_ALIVE...).</summary>
    public Dictionary<string, string> Environment { get; set; } = [];

    /// <summary>Starts the server when Llamazing boots and finds no Ollama running.</summary>
    public bool StartOnLaunch { get; set; }

    /// <summary>Stops the server started by Llamazing when Llamazing shuts down; servers started elsewhere are never touched.</summary>
    public bool StopOnExit { get; set; } = true;

    /// <summary>Log file of a server started elsewhere; empty selects the default location of the Ollama desktop app.</summary>
    public string LogFile { get; set; } = "";

    /// <summary>Number of log lines shown by the log viewer of the control panel.</summary>
    public int LogLines { get; set; } = 200;
}

/// <summary>Settings for reading the public model catalog published on ollama.com.</summary>
public sealed class CatalogOptions
{
    public const string SectionName = "Catalog";

    public string BaseUrl { get; set; } = "https://ollama.com";

    /// <summary>File holding the last scraped catalog, so a restart does not hit ollama.com again.</summary>
    public string CacheFile { get; set; } = "data/catalog-cache.json";

    /// <summary>Age after which the cached catalog is considered stale and refreshed in background.</summary>
    public int CacheTtlHours { get; set; } = 24;

    /// <summary>Maximum number of tag pages fetched in parallel, kept low to stay polite with ollama.com.</summary>
    public int MaxConcurrentRequests { get; set; } = 4;

    /// <summary>Safety cap on the number of search pages walked during a refresh.</summary>
    public int MaxPages { get; set; } = 60;
}
