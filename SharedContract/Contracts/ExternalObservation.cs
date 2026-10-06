using System.Globalization;
using System.Text;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

/// <summary>Cold observation parsing. Missing or invalid data is never measured zero.</summary>
public static class ExternalObservation
{
    /// <summary>Converts an actual unsigned kernel nanosecond counter to whole microseconds; missing/invalid is unavailable.</summary>
    public static long? ParseNanoseconds(string? text) =>
        ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value)
            ? checked((long)(value / 1000)) : null;

    /// <summary>Resolves one cgroup membership through actual mountinfo roots; null controller selects the unified hierarchy.</summary>
    /// <remarks>Cold parsing only. No filesystem access, fixed mount assumptions or traversal outside a matching mounted subtree.</remarks>
    public static string? ResolveCgroupDirectory(string? membership, string? mountinfo, string? controller)
    {
        if (membership is null || mountinfo is null) return null;
        string? memberPath = null;
        using (StringReader reader = new(membership))
        {
            while (reader.ReadLine() is { } line)
            {
                string[] parts = line.Split(':', 3);
                if (parts.Length != 3 || (controller is null
                    ? !string.Equals(parts[0], "0", StringComparison.Ordinal) || parts[1].Length != 0
                    : !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hierarchy)
                        || hierarchy <= 0 || !parts[1].Split(',').Contains(controller, StringComparer.Ordinal))) continue;
                if (memberPath is not null || !ValidCgroupPath(parts[2])) return null;
                memberPath = parts[2].TrimEnd('/');
                if (memberPath.Length == 0) memberPath = "/";
            }
        }

        if (memberPath is null) return null;
        string? result = null;
        int specificity = -1;
        bool ambiguous = false;
        using StringReader mounts = new(mountinfo);
        while (mounts.ReadLine() is { } line)
        {
            string[] halves = line.Split(" - ", 2, StringSplitOptions.None);
            if (halves.Length != 2) continue;
            string[] fields = halves[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string[] filesystem = halves[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6 || filesystem.Length != 3 || (controller is null
                ? !string.Equals(filesystem[0], "cgroup2", StringComparison.Ordinal)
                : !string.Equals(filesystem[0], "cgroup", StringComparison.Ordinal) || !filesystem[2].Split(',').Contains(controller, StringComparer.Ordinal))) continue;
            string? root = DecodeMountPath(fields[3]);
            string? point = DecodeMountPath(fields[4]);
            if (root is null || point is null || !ValidCgroupPath(root) || !ValidCgroupPath(point)) continue;
            root = root.TrimEnd('/');
            if (root.Length == 0) root = "/";
            string relative;
            if (string.Equals(root, "/", StringComparison.Ordinal)) relative = memberPath[1..];
            else if (string.Equals(memberPath, root, StringComparison.Ordinal)) relative = string.Empty;
            else if (memberPath.StartsWith(root + "/", StringComparison.Ordinal)) relative = memberPath[(root.Length + 1)..];
            else continue;
            string candidate = point.TrimEnd('/') + (relative.Length == 0 ? string.Empty : "/" + relative);
            if (candidate.Length == 0) candidate = "/";
            if (root.Length > specificity)
            {
                result = candidate;
                specificity = root.Length;
                ambiguous = false;
            }
            else if (root.Length == specificity && !string.Equals(result, candidate, StringComparison.Ordinal)) ambiguous = true;
        }

        return ambiguous ? null : result;
    }

    private static bool ValidCgroupPath(string path) => path.StartsWith('/') && !path.Contains('\0', StringComparison.Ordinal)
        && !path.Split('/').Any(static component => component is "." or "..");

    private static string? DecodeMountPath(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal)) return value;
        StringBuilder decoded = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\') decoded.Append(value[index]);
            else
            {
                if (index + 3 >= value.Length) return null;
                char? character = value.AsSpan(index + 1, 3) switch
                {
                    "040" => ' ',
                    "011" => '\t',
                    "012" => '\n',
                    "134" => '\\',
                    _ => null
                };
                if (character is null) return null;
                decoded.Append(character.Value);
                index += 3;
            }
        }

        return decoded.ToString();
    }

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

    public static IReadOnlyDictionary<string, long?> ParseCounters(string? text) => ParseCountersCore(text, nanosecondKey: null);

    internal static IReadOnlyDictionary<string, long?> ParseCpuCounters(string? text, bool v2) =>
        ParseCountersCore(text, v2 ? null : "throttled_time");

    private static Dictionary<string, long?> ParseCountersCore(string? text, string? nanosecondKey)
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

            long? value = parts.Length == 2 ? string.Equals(parts[0], nanosecondKey, StringComparison.Ordinal)
                ? ParseNanoseconds(parts[1]) : ParseCounter(parts[1]) : null;
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
