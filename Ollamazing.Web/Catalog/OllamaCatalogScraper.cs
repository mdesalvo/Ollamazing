using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;

namespace Ollamazing.Web.Catalog;

/// <summary>Reads the public model catalog from the ollama.com HTML pages, which expose no JSON API.</summary>
/// <remarks>Every markup assumption lives in this class, so a redesign of ollama.com is fixed here only.</remarks>
public sealed partial class OllamaCatalogScraper(HttpClient httpClient, IOptions<CatalogOptions> options, ILogger<OllamaCatalogScraper> logger)
{
    /// <summary>Badges that describe the offering rather than a capability of the model.</summary>
    private static readonly HashSet<string> OfferingBadges = new(StringComparer.OrdinalIgnoreCase) { "cloud", "free" };

    private readonly HtmlParser htmlParser = new();

    /// <summary>Walks every search results page and returns the catalog in popularity order, without tags.</summary>
    public async Task<IReadOnlyList<CatalogModel>> ReadModelListAsync(CancellationToken cancellationToken)
    {
        // Models are collected by name because the last page can repeat entries of the previous ones.
        Dictionary<string, CatalogModel> modelsByName = new(StringComparer.OrdinalIgnoreCase);

        for (int pageNumber = 1; pageNumber <= options.Value.MaxPages; pageNumber++)
        {
            // Only the htmx fragment honours the page parameter: a plain request always serves page one.
            using HttpRequestMessage request = new(HttpMethod.Get, $"{options.Value.BaseUrl}/search?page={pageNumber}");
            request.Headers.Add("HX-Request", "true");
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            string html = await response.Content.ReadAsStringAsync(cancellationToken);

            // A page with no new model marks the end of the catalog.
            IReadOnlyList<CatalogModel> pageModels = ParseSearchPage(html, modelsByName.Count);
            int newModelCount = 0;
            foreach (CatalogModel pageModel in pageModels)
            {
                // Popularity rank keeps the position of the first appearance.
                if (modelsByName.TryAdd(pageModel.Name, pageModel))
                    newModelCount++;
            }
            if (newModelCount == 0)
                break;
        }

        logger.LogInformation("Catalog listing read: {ModelCount} models", modelsByName.Count);

        // Insertion order of the dictionary is the popularity order of ollama.com.
        return modelsByName.Values.ToList();
    }

    /// <summary>Reads the tags page of a model, which is the only place publishing per-tag download sizes.</summary>
    public async Task<IReadOnlyList<CatalogTag>> ReadTagsAsync(string modelName, CancellationToken cancellationToken)
    {
        // The tags page is a full HTML document; no htmx header is needed here.
        string html = await httpClient.GetStringAsync($"{options.Value.BaseUrl}/library/{Uri.EscapeDataString(modelName)}/tags", cancellationToken);

        return ParseTagsPage(html, modelName);
    }

    /// <summary>Reads the "Model pricing" table, the only place publishing per-token cloud rates.</summary>
    public async Task<CatalogPricing> ReadPricingAsync(CancellationToken cancellationToken)
    {
        // The pricing page is a full HTML document, like the tags pages.
        string html = await httpClient.GetStringAsync($"{options.Value.BaseUrl}/pricing", cancellationToken);

        CatalogPricing pricing = ParsePricingPage(html);
        logger.LogInformation("Cloud pricing read: {ModelCount} priced models", pricing.PricesByModel.Count);
        return pricing;
    }

    /// <summary>Turns one search results fragment into catalog models, ranking them after <paramref name="rankOffset"/>.</summary>
    private IReadOnlyList<CatalogModel> ParseSearchPage(string html, int rankOffset)
    {
        IDocument document = htmlParser.ParseDocument(html);
        List<CatalogModel> models = [];

        // Each result is a list item wrapping a single link to the library page of the model.
        foreach (IElement listItem in document.QuerySelectorAll("li"))
        {
            // Items without a library link belong to menus and filters, not to results.
            IElement? modelLink = listItem.QuerySelector("a[href^='/library/']");
            if (modelLink is null)
                continue;

            // The library path is the canonical pull name of the model.
            string name = modelLink.GetAttribute("href")!["/library/".Length..];

            // Badges mix capabilities, parameter sizes and the "cloud" and "free" offering markers.
            List<string> badges = listItem.QuerySelectorAll("span.rounded-md")
                .Select(badge => badge.TextContent.Trim())
                .Where(badgeText => badgeText.Length > 0)
                .ToList();
            List<string> parameterSizes = badges.Where(badge => ParameterSizePattern().IsMatch(badge)).ToList();

            // The usage indicator renders four "$" glyphs, the paid level ones in the ink colour.
            IElement? usageIndicator = listItem.QuerySelector("span[title$=' usage']");
            int usageLevel = usageIndicator?.QuerySelectorAll("span.text-ink").Length ?? 0;

            // Download counters are present only on models with pullable weights.
            string? downloadsTitle = listItem.QuerySelector("span[title$=' downloads']")?.GetAttribute("title");

            models.Add(new CatalogModel
            {
                Name = name,
                Description = listItem.QuerySelector("p")?.TextContent.Trim() ?? "",
                Capabilities = badges.Where(badge => !OfferingBadges.Contains(badge) && !parameterSizes.Contains(badge)).ToList(),
                ParameterSizes = parameterSizes,
                OffersCloud = badges.Contains("cloud", StringComparer.OrdinalIgnoreCase),
                IsCloudFree = badges.Contains("free", StringComparer.OrdinalIgnoreCase),
                CloudUsageLevel = usageLevel,
                Downloads = ParseDownloads(downloadsTitle),
                ContextWindow = listItem.QuerySelector("span[title='Context window']")?.TextContent.Trim(),
                UpdatedOn = ParseUpdatedOn(listItem.QuerySelector("span[title^='Updated ']")?.GetAttribute("title")),
                PopularityRank = rankOffset + models.Count + 1
            });
        }

        return models;
    }

    /// <summary>Extracts the tag rows of a tags page from their compact (mobile) rendering, which carries all fields in one line.</summary>
    private IReadOnlyList<CatalogTag> ParseTagsPage(string html, string modelName)
    {
        IDocument document = htmlParser.ParseDocument(html);
        Dictionary<string, CatalogTag> tagsByName = new(StringComparer.OrdinalIgnoreCase);
        string tagLinkPrefix = $"/library/{modelName}:";

        // Each tag is rendered twice; only the compact link contains the digest span with the metadata line.
        foreach (IElement tagLink in document.QuerySelectorAll($"a[href^='{tagLinkPrefix}']"))
        {
            // The desktop rendering has no digest span and is skipped.
            IElement? digestSpan = tagLink.QuerySelector("span.font-mono");
            if (digestSpan is null)
                continue;

            // The metadata line reads "digest • size • context window • inputs • age", with size missing on cloud tags.
            string metadataLine = digestSpan.ParentElement?.TextContent ?? "";
            string[] metadataParts = metadataLine.Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            // The tag reference is the link target, which is more reliable than the visible label.
            string fullName = Uri.UnescapeDataString(tagLink.GetAttribute("href")!["/library/".Length..]);

            // The "latest" pill marks the tag a bare model name resolves to.
            bool isLatest = tagLink.QuerySelectorAll("span.rounded-full").Any(pill => pill.TextContent.Trim() == "latest");

            tagsByName.TryAdd(fullName, new CatalogTag
            {
                FullName = fullName,
                Digest = digestSpan.TextContent.Trim(),
                SizeBytes = metadataParts.Select(ParseSize).FirstOrDefault(size => size is not null),
                ContextWindow = metadataParts.Select(part => ContextWindowPattern().Match(part)).FirstOrDefault(match => match.Success)?.Groups[1].Value,
                Inputs = metadataParts.Select(part => InputsPattern().Match(part)).FirstOrDefault(match => match.Success)?.Groups[1].Value,
                IsLatest = isLatest || fullName.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            });
        }

        return tagsByName.Values.ToList();
    }

    /// <summary>Extracts the rows of the "Model pricing" table, grouping them by the library model they link to.</summary>
    private CatalogPricing ParsePricingPage(string html)
    {
        IDocument document = htmlParser.ParseDocument(html);
        Dictionary<string, List<CatalogPrice>> pricesByModel = new(StringComparer.OrdinalIgnoreCase);

        // A redesign that drops the section leaves every model unpriced instead of failing the refresh.
        IElement? pricingSection = document.QuerySelector("section#model-pricing");
        if (pricingSection is null)
            return new CatalogPricing(null, new Dictionary<string, IReadOnlyList<CatalogPrice>>());

        // The paragraph above the table explains the unit and when the off-peak rates apply.
        string? note = pricingSection.QuerySelector("p")?.TextContent;
        note = note is null ? null : string.Join(' ', note.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // Each row reads "model link • input • cached input • output".
        foreach (IElement row in pricingSection.QuerySelectorAll("tbody tr"))
        {
            // Rows without a library link or with fewer columns are not rates.
            IElement? modelLink = row.QuerySelector("a[href^='/library/']");
            IHtmlCollection<IElement> cells = row.QuerySelectorAll("td");
            if (modelLink is null || cells.Length < 4)
                continue;

            // The label adds the restricted tag ("gpt-oss:120b") and the "(Off-Peak)" marker to the model name.
            Match labelMatch = PricingLabelPattern().Match(modelLink.TextContent.Trim());
            string modelName = modelLink.GetAttribute("href")!["/library/".Length..];

            // Rows of the same model are kept in table order: peak before off-peak, larger variant first.
            if (!pricesByModel.TryGetValue(modelName, out List<CatalogPrice>? modelPrices))
                pricesByModel[modelName] = modelPrices = [];
            modelPrices.Add(new CatalogPrice
            {
                Variant = labelMatch.Groups[1].Success ? labelMatch.Groups[1].Value : null,
                IsOffPeak = labelMatch.Groups[2].Success,
                Input = ParsePrice(cells[1].TextContent),
                CachedInput = ParsePrice(cells[2].TextContent),
                Output = ParsePrice(cells[3].TextContent)
            });
        }

        return new CatalogPricing(note, pricesByModel.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<CatalogPrice>)entry.Value,
            StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Reads a rate such as "$0.006"; the dash printed for rates that do not apply yields null.</summary>
    private static decimal? ParsePrice(string cellText)
        => decimal.TryParse(cellText.Trim().TrimStart('$'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal price)
            ? price
            : null;

    /// <summary>Converts a size label such as "5.2GB", "523MB" or "18GB - 20GB" to bytes, using the decimal units ollama.com prints.</summary>
    private static long? ParseSize(string label)
    {
        // A size range keeps its upper bound, which is what the largest variant needs in memory.
        string[] rangeBounds = label.Split(" - ", StringSplitOptions.TrimEntries);
        if (rangeBounds.Length == 2)
            return ParseSize(rangeBounds[1]);

        // Labels that are not sizes (context window, inputs, age) are not part of the size column.
        Match sizeMatch = SizePattern().Match(label);
        if (!sizeMatch.Success)
            return null;

        // The multiplier follows the unit suffix; ollama.com uses powers of 1000.
        double value = double.Parse(sizeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        double multiplier = sizeMatch.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => 1e3,
            "MB" => 1e6,
            "GB" => 1e9,
            "TB" => 1e12,
            _ => 1
        };
        return (long)(value * multiplier);
    }

    /// <summary>Reads the counter out of a title such as "6,457 downloads".</summary>
    private static long? ParseDownloads(string? title)
    {
        // Missing titles mean the model is cloud-only and has no download counter.
        if (title is null)
            return null;

        // Thousands separators are stripped before parsing the leading number.
        string digits = new(title.TakeWhile(character => character != ' ').Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out long downloads) ? downloads : null;
    }

    /// <summary>Reads the date out of a title such as "Updated October 17, 2026".</summary>
    private static DateOnly? ParseUpdatedOn(string? title)
    {
        // Missing or reformatted titles leave the update date unknown instead of failing the whole page.
        if (title is null)
            return null;
        return DateOnly.TryParseExact(title["Updated ".Length..], "MMMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly updatedOn)
            ? updatedOn
            : null;
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(B|KB|MB|GB|TB)$", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();

    /// <summary>Parameter size badges: "8b", "0.6b", "e4b" (effective size), "8x7b" (mixture of experts), "270m".</summary>
    /// <remarks>Groups: 1 = expert count (optional), 2 = amount, 3 = unit (m, b or t).</remarks>
    [GeneratedRegex(@"^(?:(\d+)x)?e?(\d+(?:\.\d+)?)([mbt])$", RegexOptions.IgnoreCase)]
    internal static partial Regex ParameterSizePattern();

    /// <summary>Model label of a pricing row: "glm-5.3", "gpt-oss:120b", "deepseek-v4-pro (Off-Peak)".</summary>
    /// <remarks>Groups: 1 = restricted tag (optional), 2 = off-peak marker (optional).</remarks>
    [GeneratedRegex(@"^[^:\s(]+(?::(\S+))?\s*(\(off-peak\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex PricingLabelPattern();

    [GeneratedRegex(@"^(\S+)\s+context window$", RegexOptions.IgnoreCase)]
    private static partial Regex ContextWindowPattern();

    [GeneratedRegex(@"^(.+?)\s+input$", RegexOptions.IgnoreCase)]
    private static partial Regex InputsPattern();
}
