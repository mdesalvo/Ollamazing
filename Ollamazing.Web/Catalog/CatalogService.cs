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
    /// <summary>Guards the published catalog state against concurrent writers.</summary>
    private readonly Lock stateLock = new();

    /// <summary>Serialises refreshes of the catalog.</summary>
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    /// <summary>Names of the models whose tags are being loaded right now.</summary>
    private readonly HashSet<string> tagLoadsInFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Latest published catalog, replaced as a whole on every change.</summary>
    private IReadOnlyList<CatalogModel> models = [];

    /// <summary>Raised after any change of models, tags or refresh progress; handlers run on the background thread.</summary>
    public event Action? Changed;

    /// <summary>Current catalog in popularity order.</summary>
    public IReadOnlyList<CatalogModel> Models => models;

    /// <summary>When the model list was last read from ollama.com; null before the first successful refresh.</summary>
    public DateTimeOffset? RefreshedAt { get; private set; }

    /// <summary>True while a refresh from ollama.com is running.</summary>
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
            // The stored catalog is read first so that pages have models to show before any network call.
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
            // The stored state is published as a whole so readers never see a mix of stored and fresh values.
            lock (stateLock)
            {
                models = snapshot.Models;
                RefreshedAt = snapshot.RefreshedAt;
                PricingNote = snapshot.PricingNote;
            }

            // Open pages show the stored catalog straight away.
            Changed?.Invoke();
        }

        // A fresh snapshot only needs its missing tag pages completed.
        bool cacheIsFresh = RefreshedAt is not null && DateTimeOffset.UtcNow - RefreshedAt < TimeSpan.FromHours(options.Value.CacheTtlHours);
        if (cacheIsFresh)
            // Within the time to live the stored list is trusted and only the tag pages that are missing are read.
            await LoadMissingTagsAsync(cancellationToken);
        else
            // A missing or stale catalog is rebuilt from ollama.com.
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

            // The search pages give the current model list in popularity order.
            IReadOnlyList<CatalogModel> freshModels = await scraper.ReadModelListAsync(cancellationToken);

            // The cloud rates come from a separate page and are merged into the models below.
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

            // The stored copy lets the next restart start with a filled catalog.
            await TryPersistAsync(token => store.ReplaceCatalogAsync(new CatalogSnapshot(refreshedAt, mergedModels, PricingNote), token), cancellationToken);
        }
        catch (Exception refreshError) when (refreshError is not OperationCanceledException)
        {
            // A failed refresh keeps the previous catalog on screen and reports why.
            logger.LogWarning(refreshError, "Catalog refresh failed");

            // The message is shown on the catalog page until the next refresh succeeds.
            LastError = refreshError.Message;
        }
        finally
        {
            // The spinner stops and the next refresh may start, whatever the outcome.
            SetRefreshing(false);
            refreshGate.Release();
        }

        // Sizes are loaded after the list is published, so the page fills in progressively.
        await LoadMissingTagsAsync(cancellationToken);
    }

    /// <summary>Reads the cloud rates from the ollama.com pricing page.</summary>
    private async Task<CatalogPricing?> ReadPricingAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The rates are optional: the model list is complete without them.
            return await scraper.ReadPricingAsync(cancellationToken);
        }
        catch (Exception pricingError) when (pricingError is not OperationCanceledException)
        {
            // A failure of the pricing page alone must not stop the model list from refreshing.
            logger.LogWarning(pricingError, "Reading cloud pricing failed");

            // Null tells the merge to keep the previous rates until the next refresh.
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
            // An unknown model has nothing to enrich and a loaded one is already complete.
            return;

        // The user is waiting on this model, so its tags are fetched immediately.
        await LoadTagsAsync(modelName, cancellationToken);
    }

    /// <summary>Reads the tags page of every model that has none yet, with the configured parallelism.</summary>
    private async Task LoadMissingTagsAsync(CancellationToken cancellationToken)
    {
        // The work list is taken from the snapshot; models added later are picked up by the next refresh.
        List<string> pendingModelNames = models.Where(model => model.TagsLoadedAt is null).Select(model => model.Name).ToList();
        if (pendingModelNames.Count == 0)
            // Nothing is missing, so no request is made.
            return;

        // Low parallelism keeps the scrape polite while still completing in about a minute.
        await Parallel.ForEachAsync(
            pendingModelNames,
            new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaxConcurrentRequests, CancellationToken = cancellationToken },
            // Each model is enriched independently; failures are absorbed inside LoadTagsAsync.
            async (modelName, token) => await LoadTagsAsync(modelName, token));
    }

    /// <summary>Fetches the tags of a model and publishes the enriched model, tolerating failures of single pages.</summary>
    private async Task LoadTagsAsync(string modelName, CancellationToken cancellationToken)
    {
        // A load already in flight for this model will publish the same tags.
        lock (stateLock)
        {
            if (!tagLoadsInFlight.Add(modelName))
                // The on-demand load and the background load of the same model must never overlap.
                return;
        }

        try
        {
            // The tags page gives the sizes that the memory filter needs.
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

            // Open pages show the sizes of this model as soon as they are known.
            Changed?.Invoke();

            // The tags are stored so that a restart does not read the page again.
            await TryPersistAsync(token => store.SaveTagsAsync(modelName, tags, loadedAt, token), cancellationToken);
        }
        catch (Exception tagsError) when (tagsError is not OperationCanceledException)
        {
            // A single unreadable tags page leaves that model without sizes; it is retried on the next refresh.
            logger.LogWarning(tagsError, "Reading tags of {ModelName} failed", modelName);
        }
        finally
        {
            // The model becomes loadable again, which matters after a failure.
            lock (stateLock)
                tagLoadsInFlight.Remove(modelName);
        }
    }

    /// <summary>Publishes the refreshing flag to every open catalog page.</summary>
    private void SetRefreshing(bool isRefreshing)
    {
        // The flag drives the spinner of every open catalog page.
        IsRefreshing = isRefreshing;

        // Pages re-render to show or hide the spinner.
        Changed?.Invoke();
    }

    /// <summary>Runs a database write of the catalog.</summary>
    private async Task TryPersistAsync(Func<CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        try
        {
            // The write is an optional copy: the in-memory catalog is already published.
            await write(cancellationToken);
        }
        catch (Exception databaseError) when (databaseError is not OperationCanceledException)
        {
            // A locked or read-only database or data folder costs only the stored copy and must not surface as a refresh error.
            logger.LogWarning(databaseError, "Saving the catalog to {DatabasePath} failed", store.DatabasePath);
        }
    }
}

/// <summary>Starts the catalog load when the application boots, without delaying the first request.</summary>
public sealed class CatalogStartupService(CatalogService catalogService) : BackgroundService
{
    // The returned task runs in background, so host startup does not wait for the first catalog load.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => catalogService.InitializeAsync(stoppingToken);
}
