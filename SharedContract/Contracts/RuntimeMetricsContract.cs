using System.Text.Json;
using System.Text.Json.Serialization;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public readonly record struct ChildRunResult(
    string Implementation,
    PipelineResult Result,
    double ElapsedMilliseconds,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long HeapBytesAfterRun,
    long PeakWorkingSetBytes,
    long? LargeObjectHeapBytesAfterRun = null,
    long ColdManagedAllocatedBytes = 0,
    PressureRunMetrics? Pressure = null,
    double ColdElapsedMilliseconds = 0)
{
    public string ToJson() => JsonSerializer.Serialize(this, VoxelJson.Options);

    public static ChildRunResult FromJson(string json) =>
        JsonSerializer.Deserialize<ChildRunResult>(json, VoxelJson.Options);
}

public readonly record struct PressureRunMetrics(
    bool Enabled,
    bool CgroupAvailable,
    long? CgroupLimitBytes,
    long? CgroupCurrentBeforeBytes,
    long? CgroupCurrentAfterBytes,
    long? CgroupPeakBytes,
    long? CgroupOomEvents,
    long? CgroupOomKillEvents,
    long? CgroupAnonBytes,
    long? CgroupFileBytes,
    long? TotalAvailableMemoryBytes,
    long? MemoryLoadBytes,
    long? HighMemoryLoadThresholdBytes,
    long? CommittedHeapBytes,
    long? HeapBytes,
    long? LargeObjectHeapBytes,
    long? FragmentedHeapBytes,
    double TotalPauseMilliseconds);

/// <summary>
/// Optional kernel observations, not an atomic cross-file snapshot. Null means
/// unavailable; zero is a successfully parsed measurement. Peak is the kernel's
/// historical high water, not a per-request reset. V1 RSS includes swap cache and
/// is reported separately rather than relabeled as the v2 anonymous counter.
/// </summary>
public readonly record struct CgroupMemorySnapshot(
    bool Available,
    long? LimitBytes,
    long? CurrentBytes,
    long? PeakBytes,
    long? LowEvents,
    long? HighEvents,
    long? MaxEvents,
    long? OomEvents,
    long? OomKillEvents,
    long? OomGroupKillEvents,
    long? AnonBytes,
    long? FileBytes,
    long? SwapCurrentBytes = null,
    long? SwapPeakBytes = null,
    long? CpuUsageMicroseconds = null,
    long? CpuUserMicroseconds = null,
    long? CpuSystemMicroseconds = null,
    long? CpuPeriods = null,
    long? CpuThrottledPeriods = null,
    long? CpuThrottledMicroseconds = null,
    long? PageFaults = null,
    long? MajorPageFaults = null,
    bool? LimitUnlimited = null,
    int? Version = null,
    long? V1RssBytes = null,
    long? LimitHitEvents = null,
    string? SourcePath = null)
{
    public static CgroupMemorySnapshot Read() =>
        OperatingSystem.IsLinux() && FindCgroupRoot() is { } root
            ? ReadFromDirectory(root)
            : default;

    public static CgroupMemorySnapshot ReadFromDirectory(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        bool v2 = File.Exists(Path.Combine(root, "memory.current"));
        string[] files = v2
            ? ["memory.max", "memory.current", "memory.peak", "memory.events", "memory.stat", "memory.swap.current", "memory.swap.peak", "cpu.stat"]
            : ["memory.limit_in_bytes", "memory.usage_in_bytes", "memory.max_usage_in_bytes", "memory.stat", "memory.failcnt", "memory.oom_control"];
        Dictionary<string, string> contents = new(StringComparer.Ordinal);
        foreach (string file in files)
        {
            if (ReadText(Path.Combine(root, file)) is { } text)
            {
                contents.Add(file, text);
            }
        }

        return FromFiles(contents) with { SourcePath = root };
    }

    public static CgroupMemorySnapshot FromFiles(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        bool v2 = files.ContainsKey("memory.current") || files.ContainsKey("memory.max");
        bool v1 = files.ContainsKey("memory.usage_in_bytes") || files.ContainsKey("memory.limit_in_bytes");
        if (!v2 && !v1)
        {
            return default;
        }

        (long? limit, bool? unlimited) = ExternalObservation.ParseLimit(
            files.GetValueOrDefault(v2 ? "memory.max" : "memory.limit_in_bytes"), v2);
        long? current = ExternalObservation.ParseCounter(files.GetValueOrDefault(v2 ? "memory.current" : "memory.usage_in_bytes"));
        IReadOnlyDictionary<string, long?> events = ExternalObservation.ParseCounters(files.GetValueOrDefault(v2 ? "memory.events" : "memory.oom_control"));
        IReadOnlyDictionary<string, long?> stat = ExternalObservation.ParseCounters(files.GetValueOrDefault("memory.stat"));
        IReadOnlyDictionary<string, long?> cpu = ExternalObservation.ParseCounters(files.GetValueOrDefault("cpu.stat"));
        return new CgroupMemorySnapshot(
            current.HasValue, limit, current,
            ExternalObservation.ParseCounter(files.GetValueOrDefault(v2 ? "memory.peak" : "memory.max_usage_in_bytes")),
            v2 ? events.GetValueOrDefault("low") : null,
            v2 ? events.GetValueOrDefault("high") : null,
            v2 ? events.GetValueOrDefault("max") : null,
            v2 ? events.GetValueOrDefault("oom") : null,
            events.GetValueOrDefault("oom_kill"),
            v2 ? events.GetValueOrDefault("oom_group_kill") : null,
            v2 ? stat.GetValueOrDefault("anon") : null,
            stat.GetValueOrDefault(v2 ? "file" : "total_cache"),
            v2 ? ExternalObservation.ParseCounter(files.GetValueOrDefault("memory.swap.current")) : stat.GetValueOrDefault("total_swap"),
            v2 ? ExternalObservation.ParseCounter(files.GetValueOrDefault("memory.swap.peak")) : null,
            v2 ? cpu.GetValueOrDefault("usage_usec") : null,
            v2 ? cpu.GetValueOrDefault("user_usec") : null,
            v2 ? cpu.GetValueOrDefault("system_usec") : null,
            v2 ? cpu.GetValueOrDefault("nr_periods") : null,
            v2 ? cpu.GetValueOrDefault("nr_throttled") : null,
            v2 ? cpu.GetValueOrDefault("throttled_usec") : null,
            stat.GetValueOrDefault(v2 ? "pgfault" : "total_pgfault"),
            stat.GetValueOrDefault(v2 ? "pgmajfault" : "total_pgmajfault"),
            unlimited, v2 ? 2 : 1,
            v2 ? null : stat.GetValueOrDefault("total_rss"),
            v2 ? null : ExternalObservation.ParseCounter(files.GetValueOrDefault("memory.failcnt")));
    }

    private static string? FindCgroupRoot()
    {
        if (ReadText("/proc/self/cgroup") is { } membership)
        {
            foreach (string line in membership.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("0::", StringComparison.Ordinal))
                {
                    string root = Path.Join("/sys/fs/cgroup", line[3..].Trim().TrimStart('/'));
                    if (File.Exists(Path.Combine(root, "memory.current")))
                    {
                        return root;
                    }
                }
                else
                {
                    string[] parts = line.Split(':', 3);
                    if (parts.Length == 3 && parts[1].Split(',').Contains("memory", StringComparer.Ordinal))
                    {
                        string root = Path.Join("/sys/fs/cgroup/memory", parts[2].Trim().TrimStart('/'));
                        if (File.Exists(Path.Combine(root, "memory.usage_in_bytes")))
                        {
                            return root;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public static class VoxelJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };
}
