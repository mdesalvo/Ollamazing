using Microsoft.Extensions.Options;

namespace Ollamazing.Web.Catalog;

/// <summary>Holds the ollama.com catalog for every connected circuit, refreshing it and loading tag sizes in background.</summary>
/// <remarks>
/// Singleton: all state changes happen under <see cref="stateLock"/> and publish an immutable snapshot,
/// so components can read <see cref="Models"/> from any thread without copying.
/// </remarks>
public sealed class CatalogService(
    OllamaCatalogScraper scraper,
    IOptions<CatalogOptions> options,
    CatalogStore store,
    ILogger<CatalogService> logger)
{
    private readonly Lock stateLock = new();

    /// <summary>Serialises refreshes, so a manual click during the startup refresh does not scrape twice.</summary>
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    /// <summary>Models currently being enriched with tags, so on-demand and background loads never overlap.</summary>
    private readonly HashSet<string> tagLoadsInFlight = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<CatalogModel> models = [];

    /// <summary>Raised after any change of models, tags or refresh progress; handlers run on the background thread.</summary>
    public event Action? Changed;

    /// <summary>Current catalog in popularity order.</summary>
    public IReadOnlyList<CatalogModel> Models => models;

    /// <summary>When the model list was last read from ollama.com; null before the first successful refresh.</summary>
    public DateTimeOffset? RefreshedAt { get; private set; }

    public bool IsRefreshing { get; private set; }

    /// <summary>Explanatory paragraph of the ollama.com pricing table: unit of the rates and off-peak window.</summary>
    public string? PricingNote { get; private set; }

    /// <summary>Message of the last failed refresh, cleared by the next successful one.</summary>
    public string? LastError { get; private set; }

    /// <summary>Number of models whose tags page has been read, for the size loading progress.</summary>
    public int ModelsWithTags => models.Count(model => model.TagsLoadedAt is not null);

    /// <summary>Loads the stored catalog from the database, then refreshes from ollama.com when it is missing or stale.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // A stored snapshot makes the catalog usable immediately after a restart.
        CatalogSnapshot? snapshot = null;
        try
        {
            snapshot = await store.LoadAsync(cancellationToken);
        }
        catch (Exception databaseError) when (databaseError is not OperationCanceledException)
        {
            // An unreadable database or data folder costs only the stored copy: the refresh that follows rebuilds it.
            logger.LogWarning(databaseError, "Ignoring unreadable catalog database {DatabasePath}", store.DatabasePath);
        }

        // A first run has no snapshot and simply waits for the refresh.
        if (snapshot is not null)
        {
            lock (stateLock)
            {
                models = snapshot.Models;
                RefreshedAt = snapshot.RefreshedAt;
                PricingNote = snapshot.PricingNote;
            }
            Changed?.Invoke();
        }

        // A fresh snapshot only needs its missing tag pages completed.
        bool cacheIsFresh = RefreshedAt is not null && DateTimeOffset.UtcNow - RefreshedAt < TimeSpan.FromHours(options.Value.CacheTtlHours);
        if (cacheIsFresh)
            await LoadMissingTagsAsync(cancellationToken);
        else
            await RefreshAsync(cancellationToken);
    }

    /// <summary>Re-reads the model list from ollama.com, keeps tags of models not updated since, then loads the missing tags.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // A refresh already running will deliver the same result; a second one would only double the traffic.
        if (!await refreshGate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            // The refreshing flag drives the spinner of every open catalog page.
            SetRefreshing(true);
            IReadOnlyList<CatalogModel> freshModels = await scraper.ReadModelListAsync(cancellationToken);
            CatalogPricing? pricing = await ReadPricingAsync(cancellationToken);

            // Models listed for the first time after the catalog was built are the ones the "new" badge marks.
            bool isFirstBuild = RefreshedAt is null;

            // Each fresh model inherits what the previous snapshot already knew and the new list does not carry.
            Dictionary<string, CatalogModel> previousModels = models.ToDictionary(model => model.Name, StringComparer.OrdinalIgnoreCase);
            List<CatalogModel> mergedModels = freshModels
                .Select(freshModel => MergeModel(freshModel, previousModels.GetValueOrDefault(freshModel.Name), pricing, isFirstBuild))
                .ToList();

            // The new list replaces the old one as a whole and is persisted before tag loading starts.
            DateTimeOffset refreshedAt = DateTimeOffset.UtcNow;
            lock (stateLock)
            {
                models = mergedModels;
                RefreshedAt = refreshedAt;
                LastError = null;
                PricingNote = pricing?.Note ?? PricingNote;
            }
            await TryPersistAsync(token => store.ReplaceCatalogAsync(new CatalogSnapshot(refreshedAt, mergedModels, PricingNote), token), cancellationToken);
        }
        catch (Exception refreshError) when (refreshError is not OperationCanceledException)
        {
            // A failed refresh keeps the previous catalog on screen and reports why.
            logger.LogWarning(refreshError, "Catalog refresh failed");
            LastError = refreshError.Message;
        }
        finally
        {
            SetRefreshing(false);
            refreshGate.Release();
        }

        // Sizes are loaded after the list is published, so the page fills in progressively.
        await LoadMissingTagsAsync(cancellationToken);
    }

    /// <summary>Reads the cloud rates, tolerating a failure of the pricing page alone so the model list still refreshes.</summary>
    private async Task<CatalogPricing?> ReadPricingAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await scraper.ReadPricingAsync(cancellationToken);
        }
        catch (Exception pricingError) when (pricingError is not OperationCanceledException)
        {
            // Without fresh rates the previous ones stay on screen until the next refresh.
            logger.LogWarning(pricingError, "Reading cloud pricing failed");
            return null;
        }
    }

    /// <summary>Completes a freshly listed model with its rates, its first sighting and the tags read before when still valid.</summary>
    /// <param name="pricing">Fresh rates; null when the pricing page could not be read and the previous rates are kept.</param>
    /// <param name="isFirstBuild">True when no catalog existed before this refresh, so no model counts as newly appeared.</param>
    private static CatalogModel MergeModel(CatalogModel freshModel, CatalogModel? previousModel, CatalogPricing? pricing, bool isFirstBuild)
    {
        // A known model keeps its first sighting; an unknown one is new unless the whole catalog is being built for the first time.
        DateTimeOffset? firstSeenAt = previousModel is not null ? previousModel.FirstSeenAt : isFirstBuild ? null : DateTimeOffset.UtcNow;

        // Rates come from the pricing page, falling back to the last ones known for the model.
        CatalogModel pricedModel = freshModel with
        {
            Prices = pricing?.PricesOf(freshModel.Name) ?? previousModel?.Prices ?? [],
            FirstSeenAt = firstSeenAt
        };

        // Tags are carried over when the model has not been updated since they were read, saving one request per model.
        return previousModel is { TagsLoadedAt: not null } && previousModel.UpdatedOn == freshModel.UpdatedOn
            ? pricedModel with { Tags = previousModel.Tags, TagsLoadedAt = previousModel.TagsLoadedAt }
            : pricedModel;
    }

    /// <summary>Makes sure the tags of one model are loaded, fetching them right away when the user opens its details.</summary>
    public async Task EnsureTagsAsync(string modelName, CancellationToken cancellationToken = default)
    {
        // Models already enriched need no request.
        CatalogModel? model = models.FirstOrDefault(candidate => candidate.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase));
        if (model is null || model.TagsLoadedAt is not null)
            return;

        await LoadTagsAsync(modelName, cancellationToken);
    }

    /// <summary>Reads the tags page of every model that has none yet, with the configured parallelism.</summary>
    private async Task LoadMissingTagsAsync(CancellationToken cancellationToken)
    {
        // The work list is taken from the snapshot; models added later are picked up by the next refresh.
        List<string> pendingModelNames = models.Where(model => model.TagsLoadedAt is null).Select(model => model.Name).ToList();
        if (pendingModelNames.Count == 0)
            return;

        // Low parallelism keeps the scrape polite while still completing in about a minute.
        await Parallel.ForEachAsync(
            pendingModelNames,
            new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaxConcurrentRequests, CancellationToken = cancellationToken },
            async (modelName, token) => await LoadTagsAsync(modelName, token));
    }

    /// <summary>Fetches the tags of a model and publishes the enriched model, tolerating failures of single pages.</summary>
    private async Task LoadTagsAsync(string modelName, CancellationToken cancellationToken)
    {
        // A load already in flight for this model will publish the same tags.
        lock (stateLock)
        {
            if (!tagLoadsInFlight.Add(modelName))
                return;
        }

        try
        {
            IReadOnlyList<CatalogTag> tags = await scraper.ReadTagsAsync(modelName, cancellationToken);

            // The same instant stamps the in-memory model and its stored row.
            DateTimeOffset loadedAt = DateTimeOffset.UtcNow;

            // The model is replaced in place, preserving the popularity order of the list.
            lock (stateLock)
            {
                models = models
                    .Select(model => model.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase)
                        ? model with { Tags = tags, TagsLoadedAt = loadedAt }
                        : model)
                    .ToList();
            }
            Changed?.Invoke();
            await TryPersistAsync(token => store.SaveTagsAsync(modelName, tags, loadedAt, token), cancellationToken);
        }
        catch (Exception tagsError) when (tagsError is not OperationCanceledException)
        {
            // A single unreadable tags page leaves that model without sizes; it is retried on the next refresh.
            logger.LogWarning(tagsError, "Reading tags of {ModelName} failed", modelName);
        }
        finally
        {
            lock (stateLock)
                tagLoadsInFlight.Remove(modelName);
        }
    }

    /// <summary>Publishes the refreshing flag to every open catalog page.</summary>
    private void SetRefreshing(bool isRefreshing)
    {
        IsRefreshing = isRefreshing;
        Changed?.Invoke();
    }

    /// <summary>Runs a database write, tolerating its failure so the in-memory catalog and the refresh error message stay untouched.</summary>
    private async Task TryPersistAsync(Func<CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        try
        {
            await write(cancellationToken);
        }
        catch (Exception databaseError) when (databaseError is not OperationCanceledException)
        {
            // A locked or read-only database or data folder costs only the stored copy.
            logger.LogWarning(databaseError, "Saving the catalog to {DatabasePath} failed", store.DatabasePath);
        }
    }
}

/// <summary>Starts the catalog load when the application boots, without delaying the first request.</summary>
public sealed class CatalogStartupService(CatalogService catalogService) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => catalogService.InitializeAsync(stoppingToken);
}
