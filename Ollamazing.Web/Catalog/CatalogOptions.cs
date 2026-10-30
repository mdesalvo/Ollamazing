namespace Ollamazing.Web.Catalog;

/// <summary>Settings for reading the public model catalog published on ollama.com.</summary>
public sealed class CatalogOptions
{
    public const string SectionName = "Catalog";

    public string BaseUrl { get; set; } = "https://ollama.com";

    /// <summary>Path of the SQLite database holding the catalog; empty means "Ollamazing/ollamazing.db" in the per-user application data folder.</summary>
    public string DatabasePath { get; set; } = "";

    /// <summary>Age after which the cached catalog is considered stale and refreshed in background.</summary>
    public int CacheTtlHours { get; set; } = 24;

    /// <summary>Maximum number of tag pages fetched in parallel, kept low to stay polite with ollama.com.</summary>
    public int MaxConcurrentRequests { get; set; } = 4;

    /// <summary>Safety cap on the number of search pages walked during a refresh.</summary>
    public int MaxPages { get; set; } = 60;
}
