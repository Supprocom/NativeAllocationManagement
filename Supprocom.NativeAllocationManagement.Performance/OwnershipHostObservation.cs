using System.Runtime;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Performance;

// These cold observations intentionally sit outside ALL measured phases.
// Raw PSI/load/stat counters are host aggregate, not attribution to this process.
// Cgroup sources are resolved through this child's membership and mount roots.
internal sealed record OwnershipHostObservation(DateTimeOffset BeganAtUtc, DateTimeOffset CompletedAtUtc, int ProcessId,
    IReadOnlyDictionary<string, string?> LinuxFiles, CgroupMemorySnapshot Cgroup)
{
    private static readonly string[] Paths =
    [
        "/proc/cpuinfo", "/proc/loadavg", "/proc/stat", "/proc/meminfo",
        "/proc/pressure/cpu", "/proc/pressure/memory", "/proc/pressure/io",
        "/proc/self/status", "/proc/self/cgroup", "/proc/self/mountinfo"
    ];

    internal static OwnershipHostObservation Capture()
    {
        DateTimeOffset begin = DateTimeOffset.UtcNow;
        Dictionary<string, string?> files = new(StringComparer.Ordinal);
        foreach (string path in Paths) files.Add(path, OperatingSystem.IsLinux() ? ReadOptionalText(path) : null);
        CgroupMemorySnapshot cgroup = OperatingSystem.IsLinux()
            ? CgroupMemorySnapshot.ReadFromProcFiles(files["/proc/self/cgroup"], files["/proc/self/mountinfo"]) : default;
        return new(begin, DateTimeOffset.UtcNow, Environment.ProcessId, files, cgroup);
    }

    internal static string? ReadOptionalText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

internal sealed record OwnershipRuntimeConfiguration(int ProcessorCount, bool ServerGc, string GcLatencyMode,
    IReadOnlyDictionary<string, string?> EnvironmentSettings)
{
    // Explicit diagnostic allowlist: never copy arbitrary (possibly secret) environment variables.
    private static readonly string[] SettingNames =
    [
        "DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_TC_QuickJit", "DOTNET_TC_QuickJitForLoops",
        "DOTNET_ReadyToRun", "DOTNET_gcServer", "DOTNET_gcConcurrent", "DOTNET_GCHeapHardLimit",
        "DOTNET_GCHeapHardLimitPercent", "DOTNET_GCHeapCount", "DOTNET_PROCESSOR_COUNT",
        "COMPlus_TieredCompilation", "COMPlus_TieredPGO", "COMPlus_TC_QuickJit", "COMPlus_TC_QuickJitForLoops",
        "COMPlus_ReadyToRun", "COMPlus_gcServer", "COMPlus_gcConcurrent", "COMPlus_GCHeapHardLimit",
        "COMPlus_GCHeapHardLimitPercent", "COMPlus_GCHeapCount"
    ];

    internal static OwnershipRuntimeConfiguration Capture()
    {
        Dictionary<string, string?> settings = new(StringComparer.Ordinal);
        foreach (string name in SettingNames) settings.Add(name, Environment.GetEnvironmentVariable(name));
        return new(Environment.ProcessorCount, GCSettings.IsServerGC, GCSettings.LatencyMode.ToString(), settings);
    }
}
