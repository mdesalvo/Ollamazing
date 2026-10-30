using System.Globalization;

namespace Ollamazing.Web.Components.Shared;

/// <summary>Formatting rules shared by every page, so sizes and times read the same everywhere.</summary>
public static class Display
{
    /// <summary>Formats a byte count with decimal units, matching the sizes printed by ollama.com and the Ollama CLI.</summary>
    public static string Bytes(long? bytes)
    {
        // Unknown sizes (cloud tags, not loaded yet) are shown as a dash.
        if (bytes is not { } byteCount)
            return "–";

        // Each unit is a thousand times the previous one.
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = byteCount;
        int unitIndex = 0;
        while (value >= 1000 && unitIndex < units.Length - 1)
        {
            value /= 1000;
            unitIndex++;
        }

        // One decimal is kept only where it is significant, as in "5.2 GB" versus "142 GB".
        string format = value < 10 && unitIndex > 0 ? "0.#" : "0";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    /// <summary>Formats an amount in US dollars as ollama.com prints it, as in "$30.00" or "$0.006".</summary>
    public static string Price(decimal? price)
    {
        // Amounts that do not apply, such as a rate the model lacks, are shown as a dash.
        if (price is not { } amount)
            return "–";

        // Cents are always shown; sub-cent rates keep the digits that make them non-zero.
        return $"${amount.ToString("0.00##", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Formats a large counter compactly, as in "79.4K" downloads.</summary>
    public static string Count(long count) => count switch
    {
        >= 1_000_000 => $"{(count / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture)}M",
        >= 1_000 => $"{(count / 1_000d).ToString("0.#", CultureInfo.InvariantCulture)}K",
        _ => count.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>Formats a moment relative to now, in the past ("3 min ago") or in the future ("in 4 min").</summary>
    public static string Relative(DateTimeOffset moment)
    {
        TimeSpan distance = DateTimeOffset.Now - moment;
        bool isFuture = distance < TimeSpan.Zero;
        TimeSpan magnitude = distance.Duration();

        // The coarsest unit that keeps the number readable is chosen.
        string amount = magnitude switch
        {
            { TotalSeconds: < 45 } => "a few seconds",
            { TotalMinutes: < 60 } => $"{Math.Max(1, (int)magnitude.TotalMinutes)} min",
            { TotalHours: < 24 } => $"{(int)magnitude.TotalHours} h",
            { TotalDays: < 60 } => $"{(int)magnitude.TotalDays} days",
            _ => $"{(int)(magnitude.TotalDays / 30)} months"
        };
        return isFuture ? $"in {amount}" : $"{amount} ago";
    }

    /// <summary>Formats an elapsed duration compactly with its two largest units, as in "2d 4h", "3h 12m", "12m" or "5s".</summary>
    public static string Duration(TimeSpan duration) => duration switch
    {
        { TotalDays: >= 1 } => $"{(int)duration.TotalDays}d {duration.Hours}h",
        { TotalHours: >= 1 } => $"{(int)duration.TotalHours}h {duration.Minutes}m",
        // Seconds matter only in the first minute; after it they would just flicker at every refresh.
        { TotalMinutes: >= 1 } => $"{duration.Minutes}m",
        _ => $"{Math.Max(0, duration.Seconds)}s"
    };
}
