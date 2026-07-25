using System.Text.Json;

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
    long LargeObjectHeapBytesAfterRun = 0,
    long ColdManagedAllocatedBytes = 0,
    PressureRunMetrics? Pressure = null)
{
    public string ToJson() => JsonSerializer.Serialize(this, VoxelJson.Options);

    public static ChildRunResult FromJson(string json) =>
        JsonSerializer.Deserialize<ChildRunResult>(json, VoxelJson.Options);
}

public readonly record struct PressureRunMetrics(
    bool Enabled,
    bool CgroupAvailable,
    long CgroupLimitBytes,
    long CgroupCurrentBeforeBytes,
    long CgroupCurrentAfterBytes,
    long CgroupPeakBytes,
    long CgroupOomEvents,
    long CgroupOomKillEvents,
    long CgroupAnonBytes,
    long CgroupFileBytes,
    long TotalAvailableMemoryBytes,
    long MemoryLoadBytes,
    long HighMemoryLoadThresholdBytes,
    long CommittedHeapBytes,
    long HeapBytes,
    long LargeObjectHeapBytes,
    long FragmentedHeapBytes,
    double TotalPauseMilliseconds);

public readonly record struct CgroupMemorySnapshot(
    bool Available,
    long LimitBytes,
    long CurrentBytes,
    long PeakBytes,
    long OomEvents,
    long OomKillEvents,
    long AnonBytes,
    long FileBytes)
{
    public static CgroupMemorySnapshot Read()
    {
        if (!OperatingSystem.IsLinux())
        {
            return default;
        }

        string? root = FindCgroupRoot();
        if (root is null)
        {
            return default;
        }

        long limit = ReadLong(Path.Combine(root, "memory.max"));
        long current = ReadLong(Path.Combine(root, "memory.current"));
        long peak = ReadLong(Path.Combine(root, "memory.peak"));
        if (limit == 0 && current == 0 && peak == 0)
        {
            limit = ReadLong(Path.Combine(root, "memory.limit_in_bytes"));
            current = ReadLong(Path.Combine(root, "memory.usage_in_bytes"));
            peak = ReadLong(Path.Combine(root, "memory.max_usage_in_bytes"));
        }

        (long oom, long oomKill) = ReadEvents(root);
        (long anon, long file) = ReadStat(root);
        return new CgroupMemorySnapshot(
            limit > 0 || current > 0 || peak > 0,
            limit,
            current,
            peak,
            oom,
            oomKill,
            anon,
            file);
    }

    private static string? FindCgroupRoot()
    {
        string[] candidates =
        [
            "/sys/fs/cgroup",
            "/sys/fs/cgroup/memory"
        ];
        for (int index = 0; index < candidates.Length; index++)
        {
            string candidate = candidates[index];
            if (File.Exists(Path.Combine(candidate, "memory.current"))
                || File.Exists(Path.Combine(candidate, "memory.usage_in_bytes")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static long ReadLong(string path)
    {
        try
        {
            string text = File.ReadAllText(path).Trim();
            return text is "" or "max"
                ? 0
                : long.TryParse(text, out long value) && value >= 0
                    ? value
                    : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static (long Oom, long OomKill) ReadEvents(string root)
    {
        string path = Path.Combine(root, "memory.events");
        try
        {
            long oom = 0;
            long oomKill = 0;
            foreach (string line in File.ReadLines(path))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !long.TryParse(parts[1], out long value))
                {
                    continue;
                }

                if (parts[0] == "oom")
                {
                    oom = value;
                }
                else if (parts[0] == "oom_kill")
                {
                    oomKill = value;
                }
            }

            return (oom, oomKill);
        }
        catch (IOException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static (long Anon, long File) ReadStat(string root)
    {
        string path = Path.Combine(root, "memory.stat");
        try
        {
            long anon = 0;
            long file = 0;
            foreach (string line in File.ReadLines(path))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !long.TryParse(parts[1], out long value))
                {
                    continue;
                }

                if (parts[0] == "anon")
                {
                    anon = value;
                }
                else if (parts[0] == "file")
                {
                    file = value;
                }
            }

            return (anon, file);
        }
        catch (IOException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }
    }
}

public static class VoxelJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}


