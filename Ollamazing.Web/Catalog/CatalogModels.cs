namespace Ollamazing.Web.Catalog;

/// <summary>A model family listed in the ollama.com catalog, as read from the search page and its tags page.</summary>
public sealed record CatalogModel
{
    /// <summary>Library name used as the repository part of a pull reference (e.g. "qwen3").</summary>
    public required string Name { get; init; }

    public string Description { get; init; } = "";

    /// <summary>Capability badges published by ollama.com (tools, vision, thinking, embedding...).</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Parameter sizes of the published variants, as badges read them (e.g. "8b", "e4b", "8x7b").</summary>
    public IReadOnlyList<string> ParameterSizes { get; init; } = [];

    /// <summary>True when ollama.com offers the model on its cloud, regardless of downloadable weights.</summary>
    public bool OffersCloud { get; init; }

    /// <summary>True when the cloud offering carries the "free" badge, so it does not consume paid usage.</summary>
    public bool IsCloudFree { get; init; }

    /// <summary>Cloud usage cost from 1 (low) to 4, read from the "$$$$" indicator; zero when not published.</summary>
    public int CloudUsageLevel { get; init; }

    /// <summary>Total pulls reported by ollama.com; present only for models with downloadable weights.</summary>
    public long? Downloads { get; init; }

    /// <summary>Context window label shown on the search page for cloud models (e.g. "1M").</summary>
    public string? ContextWindow { get; init; }

    public DateOnly? UpdatedOn { get; init; }

    /// <summary>Position in the catalog popularity ranking, which is the order of the search pages.</summary>
    public int PopularityRank { get; init; }

    /// <summary>Pay-as-you-go cloud rates read from the pricing page; empty when ollama.com publishes none for the model.</summary>
    public IReadOnlyList<CatalogPrice> Prices { get; init; } = [];

    /// <summary>Tags read from the model tags page; empty until <see cref="TagsLoadedAt"/> is set.</summary>
    public IReadOnlyList<CatalogTag> Tags { get; init; } = [];

    /// <summary>When the tags page was last read; null means sizes are not known yet.</summary>
    public DateTimeOffset? TagsLoadedAt { get; init; }

    /// <summary>When the model first appeared in a refresh; null for the models present when the catalog was first built.</summary>
    public DateTimeOffset? FirstSeenAt { get; init; }

    /// <summary>How many days a model that appeared on ollama.com keeps the "new" badge.</summary>
    public const int NewModelDays = 14;

    /// <summary>True while a model that appeared after the catalog was first built is recent.</summary>
    public bool IsNew => FirstSeenAt is { } firstSeenAt && DateTimeOffset.UtcNow - firstSeenAt < TimeSpan.FromDays(NewModelDays);

    /// <summary>True when weights can be pulled and run locally, known exactly once tags are loaded.</summary>
    public bool OffersLocal => TagsLoadedAt is not null
        ? Tags.Any(tag => !tag.IsCloud)
        : !OffersCloud || Downloads is not null;

    /// <summary>True when at least one way of running the model costs nothing: local weights or a free cloud tier.</summary>
    public bool HasFreeOption => OffersLocal || IsCloudFree;

    /// <summary>True when the cloud offering is billed against the paid usage quota.</summary>
    public bool HasPaidOption => OffersCloud && !IsCloudFree;

    /// <summary>Downloadable tags only, which are the ones the memory size filter applies to.</summary>
    public IEnumerable<CatalogTag> LocalTags => Tags.Where(tag => !tag.IsCloud);

    /// <summary>Pull reference used by the quick download button: the "latest" alias when present, else the first tag.</summary>
    public string DefaultPullReference =>
        Tags.FirstOrDefault(tag => tag.IsLatest)?.FullName
        ?? Tags.FirstOrDefault()?.FullName
        ?? Name;
}

/// <summary>A single pullable tag of a catalog model, such as "qwen3:8b" or "glm-5.3:cloud".</summary>
public sealed record CatalogTag
{
    /// <summary>Complete pull reference in "model:tag" form.</summary>
    public required string FullName { get; init; }

    /// <summary>Download size in bytes, a good proxy of the memory needed to run it; null for cloud tags.</summary>
    /// <remarks>Tags published with a size range (e.g. "18GB - 20GB") keep the upper bound, the safe figure for memory planning.</remarks>
    public long? SizeBytes { get; init; }

    public string? ContextWindow { get; init; }

    /// <summary>Accepted input modalities as printed by ollama.com (e.g. "Text, Image").</summary>
    public string? Inputs { get; init; }

    public string? Digest { get; init; }

    /// <summary>True when this tag is flagged as "latest", which is what a pull without tag resolves to.</summary>
    public bool IsLatest { get; init; }

    /// <summary>Cloud tags have no downloadable weights, which is why they carry no size.</summary>
    public bool IsCloud => SizeBytes is null;

    /// <summary>Tag part of the reference, after the colon.</summary>
    public string TagName => FullName[(FullName.IndexOf(':') + 1)..];
}

/// <summary>One row of the "Model pricing" table of ollama.com: cloud rates in US dollars per million tokens.</summary>
public sealed record CatalogPrice
{
    /// <summary>Tag the rate is restricted to (e.g. "120b" for "gpt-oss:120b"); null when it covers every cloud tag of the model.</summary>
    public string? Variant { get; init; }

    /// <summary>True for the discounted rate applied outside the peak hours stated in <see cref="CatalogPricing.Note"/>.</summary>
    public bool IsOffPeak { get; init; }

    /// <summary>Rate of prompt tokens; null when the table prints a dash.</summary>
    public decimal? Input { get; init; }

    /// <summary>Rate of prompt tokens served from the cache; null when the model offers no cached rate.</summary>
    public decimal? CachedInput { get; init; }

    /// <summary>Rate of generated tokens; null when the model produces no billed output, as embedding models do.</summary>
    public decimal? Output { get; init; }
}

/// <summary>Content of the "Model pricing" section of ollama.com, indexed by library name.</summary>
/// <param name="Note">Explanatory paragraph above the table, which states the unit and the off-peak window.</param>
public sealed record CatalogPricing(string? Note, IReadOnlyDictionary<string, IReadOnlyList<CatalogPrice>> PricesByModel)
{
    /// <summary>Rates published for a model, or none when the model is not in the table.</summary>
    public IReadOnlyList<CatalogPrice> PricesOf(string modelName)
        => PricesByModel.TryGetValue(modelName, out IReadOnlyList<CatalogPrice>? prices) ? prices : [];
}

/// <summary>The catalog as persisted between restarts.</summary>
/// <param name="PricingNote">Explanatory paragraph of the pricing table, kept with the rates it describes.</param>
public sealed record CatalogSnapshot(DateTimeOffset RefreshedAt, IReadOnlyList<CatalogModel> Models, string? PricingNote);
