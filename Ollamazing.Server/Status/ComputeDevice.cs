using System.Globalization;
using System.Text.RegularExpressions;

namespace Ollamazing.Server.Status;

/// <summary>Compute device that Ollama detected at startup and runs its models on.</summary>
/// <param name="Name">Human readable name: the GPU description when present, else the device name, else the library.</param>
/// <param name="Library">Compute library Ollama uses for the device, such as CUDA, ROCm, Metal or cpu.</param>
/// <param name="IsGpu">True for an accelerator with its own video memory; false for the processor.</param>
/// <param name="TotalBytes">Total memory of the device in bytes: VRAM for a GPU and system RAM for the processor.</param>
public sealed record ComputeDevice(string Name, string Library, bool IsGpu, long TotalBytes);

/// <summary>Reads the compute devices from the <c>inference compute</c> lines that Ollama logs at every startup.</summary>
public static partial class ComputeDeviceLog
{
    /// <summary>Message that Ollama attaches to the log line of each detected device.</summary>
    private const string DeviceMessage = "msg=\"inference compute\"";

    /// <summary>Extracts the key=value pairs of a log line, with quoted values kept between their quotes.</summary>
    [GeneratedRegex("""(\w+)=("(?:[^"\\]|\\.)*"|\S*)""")]
    private static partial Regex KeyValuePattern();

    /// <summary>Returns the devices of the last startup found in the lines; null when none could be read.</summary>
    public static IReadOnlyList<ComputeDevice>? ParseLastStartup(IEnumerable<string> lines)
    {
        // Device lines of the startup being read; a gap of other lines starts a new startup.
        List<string> lastBlock = [];
        bool previousWasDevice = false;

        foreach (string line in lines)
        {
            // Any other line ends the current block, so the next device line belongs to a later startup.
            if (!line.Contains(DeviceMessage, StringComparison.Ordinal))
            {
                previousWasDevice = false;
                continue;
            }

            // The first device line after a gap replaces the block of the previous startup.
            if (!previousWasDevice)
                lastBlock = [];

            // The line joins its startup, which stays open until a line of another kind.
            lastBlock.Add(line);
            previousWasDevice = true;
        }

        // Lines without a usable library and total are dropped, since they cannot size a budget.
        List<ComputeDevice> devices = [];
        foreach (string deviceLine in lastBlock)
        {
            if (TryParseDevice(deviceLine) is { } device)
                devices.Add(device);
        }

        // Nothing readable means the hardware is unknown, which callers tell apart from an empty list.
        return devices.Count == 0 ? null : devices;
    }

    /// <summary>Builds a device from one log line; null when the library or the total is missing or unreadable.</summary>
    private static ComputeDevice? TryParseDevice(string line)
    {
        // The fields are indexed by key, so they are read by name whatever order a given Ollama version prints them in.
        Dictionary<string, string> values = [];
        foreach (Match pair in KeyValuePattern().Matches(line))
        {
            // Quoted values lose their quotes, so descriptions and sizes read as plain text.
            string value = pair.Groups[2].Value;
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            values[pair.Groups[1].Value] = value;
        }

        // Without a library the line is not a device; without a readable total it cannot size the budget.
        if (!values.TryGetValue("library", out string? library) || library.Length == 0
            || !values.TryGetValue("total", out string? total) || TryParseBytes(total) is not { } totalBytes)
            return null;

        // The description names a GPU best; the device name and the library are the fallbacks.
        string name = values.GetValueOrDefault("description", "");
        if (name.Length == 0)
            name = values.GetValueOrDefault("name", "");
        if (name.Length == 0)
            name = library;

        // The processor is the only device without video memory of its own.
        bool isGpu = !string.Equals(library, "cpu", StringComparison.OrdinalIgnoreCase);
        return new ComputeDevice(name, library, isGpu, totalBytes);
    }

    /// <summary>Converts a size such as "24.0 GiB" to bytes; null when the number or the unit is not recognised.</summary>
    private static long? TryParseBytes(string text)
    {
        // Ollama prints the number and the binary unit separated by one space.
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double amount))
            return null;

        // Units are matched exactly as Ollama writes them, each one 1024 times the previous.
        int power = parts[1] switch
        {
            "B" => 0,
            "KiB" => 1,
            "MiB" => 2,
            "GiB" => 3,
            "TiB" => 4,
            _ => -1
        };
        if (power < 0)
            return null;

        // The printed figure has one decimal, so the bytes are rounded to the nearest whole one.
        return (long)Math.Round(amount * Math.Pow(1024, power));
    }
}
