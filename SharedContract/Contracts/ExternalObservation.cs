using System.Globalization;
using System.Text;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

/// <summary>Cold observation parsing. Missing or invalid data is never measured zero.</summary>
public static class ExternalObservation
{
    public static long? ParseCounter(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) && value >= 0 ? value : null;

    public static int? ParseCount(string? text) =>
        ParseCounter(text) is { } value && value <= int.MaxValue ? (int)value : null;

    public static (long? Bytes, bool? Unlimited) ParseLimit(string? text, bool v2)
    {
        string? value = text?.Trim();
        if (v2 ? string.Equals(value, "max", StringComparison.Ordinal) : string.Equals(value, "-1", StringComparison.Ordinal))
        {
            return (null, true);
        }

        long? bytes = ParseCounter(value);
        if (!v2 && bytes >= long.MaxValue - (Environment.SystemPageSize - 1))
        {
            return (null, true);
        }

        return (bytes, bytes.HasValue ? false : null);
    }

    public static IReadOnlyDictionary<string, long?> ParseCounters(string? text)
    {
        Dictionary<string, long?> values = new(StringComparer.Ordinal);
        if (text is null)
        {
            return values;
        }

        using StringReader reader = new(text);
        while (reader.ReadLine() is { } line)
        {
            string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            long? value = parts.Length == 2 ? ParseCounter(parts[1]) : null;
            if (!values.TryAdd(parts[0], value))
            {
                values[parts[0]] = null; // Ambiguous duplicates are not valid observations.
            }
        }

        return values;
    }

    public static IReadOnlyDictionary<string, string> ParseFileSections(int exitCode, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        if (exitCode != 0)
        {
            return files;
        }

        using StringReader reader = new(text);
        string? file = null;
        StringBuilder content = new();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 2 && line[0] == '[' && line[^1] == ']')
            {
                CommitSection();
                file = line[1..^1];
                content.Clear();
            }
            else if (file is not null)
            {
                content.AppendLine(line);
            }
        }

        CommitSection();
        return files;

        void CommitSection()
        {
            if (file is not null && !files.TryAdd(file, content.ToString()))
            {
                files[file] = string.Empty;
            }
        }
    }

    public static long? Delta(long? before, long? after) =>
        before is >= 0 && after >= before ? after - before : null;

    public static long? Maximum(long? first, long? second) =>
        first.HasValue ? second.HasValue ? Math.Max(first.Value, second.Value) : first : second;

    public static double? CpuMilliseconds(long? userTicks, long? systemTicks, long? frequency) =>
        userTicks is >= 0 && systemTicks is >= 0 && frequency is > 0
            ? ((double)userTicks.Value + systemTicks.Value) * 1000.0 / frequency.Value
            : null;

    public static double? CpuCores(long? usageMicroseconds, double? elapsedMilliseconds) =>
        usageMicroseconds is >= 0 && elapsedMilliseconds is > 0 && double.IsFinite(elapsedMilliseconds.Value)
            ? usageMicroseconds.Value / 1000.0 / elapsedMilliseconds.Value : null;

    public static (long? First, long? Second) ParseUsage(string? text)
    {
        string[] parts = (text ?? string.Empty).Split('/', StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (ParseByteSize(parts[0]), ParseByteSize(parts[1])) : (null, null);
    }

    public static long? ParseByteSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string value = text.Trim();
        int split = 0;
        while (split < value.Length && (char.IsAsciiDigit(value[split]) || value[split] == '.'))
        {
            split++;
        }

        if (!decimal.TryParse(value.AsSpan(0, split), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number))
        {
            return null;
        }

        long? multiplier = value[split..].Trim() switch
        {
            "B" or "" => 1,
            "kB" or "KB" => 1_000,
            "KiB" => 1 << 10,
            "MB" => 1_000_000,
            "MiB" => 1 << 20,
            "GB" => 1_000_000_000,
            "GiB" => 1L << 30,
            "TB" => 1_000_000_000_000,
            "TiB" => 1L << 40,
            _ => null
        };
        // Check before multiplying: even a huge valid decimal cannot overflow.
        return multiplier is { } scale && number <= (decimal)long.MaxValue / scale
            ? (long)(number * scale) : null;
    }

    public static double? ParsePercent(string? text) =>
        double.TryParse(text?.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value) && value >= 0 ? value : null;
}
