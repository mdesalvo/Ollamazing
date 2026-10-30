using System.Globalization;
using System.Text.RegularExpressions;

namespace Ollamazing.Web.Catalog;

/// <summary>One selectable value of a facet, with the predicate deciding which models carry it.</summary>
public sealed record FacetOption(string Key, string Label, Func<CatalogModel, bool> Matches);

/// <summary>A dimension of the catalog the user can narrow by, such as class, pricing or capability.</summary>
public sealed record Facet(string Key, string Title, IReadOnlyList<FacetOption> Options);

/// <summary>Faceted query over the catalog: facets combine in AND, options of the same facet combine in OR.</summary>
/// <remarks>
/// Option counts are disjunctive: each count applies every other facet but ignores the selection of its own facet,
/// so the user sees how many models an extra option would add.
/// </remarks>
public sealed class CatalogQuery
{
    public const string ClassFacetKey = "class";
    public const string PricingFacetKey = "pricing";
    public const string CapabilityFacetKey = "capability";
    public const string ParametersFacetKey = "parameters";
    public const string LocalOptionKey = "local";
    public const string CloudOptionKey = "cloud";

    /// <summary>Parameter count bands in billions, lower bound inclusive and upper bound exclusive.</summary>
    private static readonly (string Key, string Label, double MinBillions, double MaxBillions)[] ParameterBands =
    [
        ("under-4b", "Under 4B", 0, 4),
        ("4b-14b", "4B – 14B", 4, 15),
        ("15b-40b", "15B – 40B", 15, 41),
        ("41b-100b", "41B – 100B", 41, 101),
        ("over-100b", "Over 100B", 101, double.MaxValue)
    ];

    private readonly Dictionary<string, HashSet<string>> selectedOptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Free text matched against name and description; empty means no constraint.</summary>
    public string SearchText { get; set; } = "";

    /// <summary>Lower bound of the memory size range in bytes; zero means no lower bound.</summary>
    public long MinSizeBytes { get; set; }

    /// <summary>Upper bound of the memory size range in bytes; <see cref="long.MaxValue"/> means no upper bound.</summary>
    public long MaxSizeBytes { get; set; } = long.MaxValue;

    public bool IsSizeRangeActive => MinSizeBytes > 0 || MaxSizeBytes < long.MaxValue;

    /// <summary>Lower bound of the input rate in dollars per million tokens; zero means no lower bound.</summary>
    public decimal MinInputPrice { get; set; }

    /// <summary>Upper bound of the input rate in dollars per million tokens; <see cref="decimal.MaxValue"/> means no upper bound.</summary>
    public decimal MaxInputPrice { get; set; } = decimal.MaxValue;

    /// <summary>Lower bound of the output rate in dollars per million tokens; zero means no lower bound.</summary>
    public decimal MinOutputPrice { get; set; }

    /// <summary>Upper bound of the output rate in dollars per million tokens; <see cref="decimal.MaxValue"/> means no upper bound.</summary>
    public decimal MaxOutputPrice { get; set; } = decimal.MaxValue;

    /// <summary>True when either price range is narrowed, which keeps only models with pay-as-you-go rates.</summary>
    public bool IsPriceRangeActive => MinInputPrice > 0 || MaxInputPrice < decimal.MaxValue || MinOutputPrice > 0 || MaxOutputPrice < decimal.MaxValue;

    /// <summary>True when the selection is restricted to cloud offerings, where memory size has no meaning.</summary>
    public bool IsCloudOnly => SelectedOf(ClassFacetKey) is { Count: 1 } classSelection && classSelection.Contains(CloudOptionKey);

    /// <summary>True when the selection is restricted to local weights, where token rates have no meaning.</summary>
    public bool IsLocalOnly => SelectedOf(ClassFacetKey) is { Count: 1 } classSelection && classSelection.Contains(LocalOptionKey);

    public bool HasSelections => selectedOptions.Values.Any(options => options.Count > 0);

    /// <summary>Number of selected options across all facets, shown on the collapsed filter panel.</summary>
    public int SelectionCount => selectedOptions.Values.Sum(options => options.Count);

    public bool IsSelected(string facetKey, string optionKey) => SelectedOf(facetKey)?.Contains(optionKey) == true;

    /// <summary>Selects an option, or deselects it when already selected.</summary>
    public void Toggle(string facetKey, string optionKey)
    {
        // Facets get their selection set on first use.
        if (!selectedOptions.TryGetValue(facetKey, out HashSet<string>? facetSelection))
            selectedOptions[facetKey] = facetSelection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A second click on a selected option removes it.
        if (!facetSelection.Remove(optionKey))
            facetSelection.Add(optionKey);
    }

    public void ClearSelections() => selectedOptions.Clear();

    /// <summary>True when at least one option of the facet is selected.</summary>
    public bool HasSelectionsIn(string facetKey) => SelectedOf(facetKey) is { Count: > 0 };

    /// <summary>Drops the selected options of one facet, leaving the other facets as they are.</summary>
    public void ClearSelections(string facetKey) => selectedOptions.Remove(facetKey);

    /// <summary>Builds the facets for the given catalog, with predicates that depend on the current selection.</summary>
    public IReadOnlyList<Facet> BuildFacets(IReadOnlyList<CatalogModel> models)
    {
        // With only the cloud class selected, "free" refers to the free cloud tier rather than to downloadable weights.
        bool isCloudOnly = IsCloudOnly;

        // Capabilities are discovered from the catalog, so new badges on ollama.com appear without code changes.
        List<FacetOption> capabilityOptions = models
            .SelectMany(model => model.Capabilities)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(capability => new FacetOption(capability, capability, model => model.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        return
        [
            new Facet(ClassFacetKey, "Class", [
                new FacetOption(LocalOptionKey, "Local", model => model.OffersLocal),
                new FacetOption(CloudOptionKey, "Cloud", model => model.OffersCloud)
            ]),
            new Facet(PricingFacetKey, "Pricing", [
                new FacetOption("free", "Free", model => isCloudOnly ? model.IsCloudFree : model.HasFreeOption),
                new FacetOption("paid", "Paid", model => model.HasPaidOption)
            ]),
            new Facet(CapabilityFacetKey, "Capabilities", capabilityOptions),
            new Facet(ParametersFacetKey, "Parameters", ParameterBands
                .Select(band => new FacetOption(band.Key, band.Label, model => model.ParameterSizes
                    .Select(ParseBillions)
                    .Any(billions => billions >= band.MinBillions && billions < band.MaxBillions)))
                .ToList())
        ];
    }

    /// <summary>True when a model satisfies search text, size range, price ranges and every facet except <paramref name="ignoredFacetKey"/>.</summary>
    public bool Matches(CatalogModel model, IReadOnlyList<Facet> facets, string? ignoredFacetKey = null)
        => MatchesText(model) && MatchesSizeRange(model) && MatchesPriceRange(model) && facets.All(facet => facet.Key == ignoredFacetKey || MatchesFacet(model, facet));

    /// <summary>Number of models the option would show, applying every other facet (disjunctive count).</summary>
    public int CountOption(IReadOnlyList<CatalogModel> models, IReadOnlyList<Facet> facets, Facet facet, FacetOption option)
        => models.Count(model => option.Matches(model) && Matches(model, facets, facet.Key));

    /// <summary>Models excluded only because their tag sizes are not loaded yet while a size range is active.</summary>
    public int CountAwaitingSizes(IReadOnlyList<CatalogModel> models, IReadOnlyList<Facet> facets)
        => !IsSizeRangeActive || IsCloudOnly
            ? 0
            : models.Count(model => model.TagsLoadedAt is null && MatchesText(model) && facets.All(facet => MatchesFacet(model, facet)));

    /// <summary>True when a downloadable tag falls within the size range.</summary>
    public bool IsInSizeRange(CatalogTag tag) => tag.SizeBytes is { } size && size >= MinSizeBytes && size <= MaxSizeBytes;

    /// <summary>Converts a parameter size badge to billions: "270m" is 0.27, "e4b" is 4, "8x7b" is 56.</summary>
    private static double ParseBillions(string parameterSize)
    {
        // Badges that do not follow the known shapes fall outside every band.
        Match sizeMatch = OllamaCatalogScraper.ParameterSizePattern().Match(parameterSize);
        if (!sizeMatch.Success)
            return double.NaN;

        // Mixture-of-experts badges multiply the expert count by the expert size.
        double expertCount = sizeMatch.Groups[1].Success ? double.Parse(sizeMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
        double amount = double.Parse(sizeMatch.Groups[2].Value, CultureInfo.InvariantCulture);
        double unitInBillions = char.ToLowerInvariant(sizeMatch.Groups[3].Value[0]) switch
        {
            'm' => 0.001,
            't' => 1000,
            _ => 1
        };
        return expertCount * amount * unitInBillions;
    }

    private HashSet<string>? SelectedOf(string facetKey) => selectedOptions.GetValueOrDefault(facetKey);

    /// <summary>Options of the same facet are alternatives: one matching option is enough.</summary>
    private bool MatchesFacet(CatalogModel model, Facet facet)
    {
        // A facet with nothing selected does not constrain the result.
        HashSet<string>? facetSelection = SelectedOf(facet.Key);
        if (facetSelection is not { Count: > 0 })
            return true;

        return facet.Options.Any(option => facetSelection.Contains(option.Key) && option.Matches(model));
    }

    private bool MatchesText(CatalogModel model)
        => SearchText.Length == 0
           || model.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
           || model.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

    /// <summary>A model matches the size range when at least one of its downloadable tags falls inside it.</summary>
    private bool MatchesSizeRange(CatalogModel model)
    {
        // An open range, or a cloud-only selection, does not constrain size.
        if (!IsSizeRangeActive || IsCloudOnly)
            return true;

        // Unknown sizes cannot be proven inside the range; they are reported by CountAwaitingSizes.
        return model.TagsLoadedAt is not null && model.LocalTags.Any(IsInSizeRange);
    }

    /// <summary>A model matches the price ranges when its cheapest standard rates fall inside both; an open range does not constrain.</summary>
    private bool MatchesPriceRange(CatalogModel model)
    {
        // Open ranges let every model through, priced or not; a local-only selection ignores the price ranges, as a cloud-only one ignores the size range.
        if (!IsPriceRangeActive || IsLocalOnly)
            return true;

        // Only the standard rates count: off-peak discounts apply to part of the day only.
        List<CatalogPrice> standardPrices = model.Prices.Where(price => !price.IsOffPeak).ToList();
        if (standardPrices.Count == 0)
            return false;

        // A missing rate means that side is not billed, as for the output of embedding models.
        decimal inputRate = standardPrices.Min(price => price.Input ?? 0);
        decimal outputRate = standardPrices.Min(price => price.Output ?? 0);
        return inputRate >= MinInputPrice && inputRate <= MaxInputPrice
            && outputRate >= MinOutputPrice && outputRate <= MaxOutputPrice;
    }
}
