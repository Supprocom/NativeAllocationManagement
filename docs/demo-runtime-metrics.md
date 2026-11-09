# Demo: process and GC observations

`PressureRuntimeSnapshot` is part of the voxel demo's shared contract (`SharedContract`), not the NuGet package (reference `SharedContract/SharedContract.csproj` to use it). It records the .NET runtime, process and cgroup state at one moment, so a report can show what the process did around a workload.

```csharp
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

PressureRuntimeSnapshot before = PressureRuntimeSnapshot.Capture();
// ... run the workload ...
PressureRuntimeSnapshot after = PressureRuntimeSnapshot.Capture();
Console.WriteLine($"managed bytes allocated: {after.TotalAllocatedBytes - before.TotalAllocatedBytes}");

string json = JsonSerializer.Serialize(after);
PressureRuntimeSnapshot copy = JsonSerializer.Deserialize<PressureRuntimeSnapshot>(json);
```

## Reading the values

- Capture is a cold call that allocates and uses CPU. It reads one `GCMemoryInfo` first, then the process, GC settings and cgroup, one after another. It is not one atomic instant.
- Fields marked "last GC" come from that one `GCMemoryInfo`: the last completed collection, not live memory at `Utc`. Before the first collection they are `null` (`LastCompletedGcIndex` is `null` too).
- `null` means unavailable. A real zero is `0`.
- Totals count from the start of their own runtime, process or cgroup. Subtract only two snapshots of the same process, and never across a reset counter.
- A GC, a dropped reference or memory returned to a NAM owner does not prove the OS got memory back. A NAM budget does not bound the working set or managed metadata.
- A `default` snapshot is not a capture.
- OS or runtime query failures throw; they never become zeros.

| Property | Source, units and scope |
|---|---|
| `Utc` | wall clock at capture; not a duration clock |
| `TotalAllocatedBytes` | `GC.GetTotalAllocatedBytes(true)`: managed bytes since runtime start; no native bytes |
| `Gen0Collections`, `Gen1Collections`, `Gen2Collections` | `GC.CollectionCount(0..2)` since runtime start |
| `TotalPauseMilliseconds` | `GC.GetTotalPauseDuration`: time paused for GC, ms; not a percentage |
| `TotalAvailableMemoryBytes` | last GC: bytes available to the GC; not a NAM cap |
| `MemoryLoadBytes` | last GC: physical memory load of the machine, not this process |
| `HighMemoryLoadThresholdBytes` | last GC: the high-memory-load threshold |
| `TotalCommittedBytes` | last GC: committed managed heap; not NAM budget charges |
| `HeapSizeBytes` | last GC: heap size at that collection |
| `FragmentedBytes` | last GC: heap fragmentation; an observed zero is valid |
| `LargeObjectHeapBytes` | last GC: large-object heap size after collection; `null` if missing. Not all large, pinned or native memory |
| `ProcessWorkingSetBytes` | current working set, bytes, shared and private pages; not a peak |
| `ProcessCpuMilliseconds` | CPU time of all process threads, cumulative ms; not a percentage |
| `ProcessorCount` | `Environment.ProcessorCount` at capture, including affinity and quota rules |
| `Cgroup` | a [`CgroupMemorySnapshot`](demo-kernel-metrics.md); container-wide, not process-exclusive |
| `GcConfiguration` | the runtime's GC configuration, copied to strings; can differ from what you requested |
| `CompilationConfiguration` | the two environment strings below |
| `LastCompletedGcIndex` | index of the sampled GC; `null` if none. Background and foreground GCs can finish out of order, so it is not a clock |
| `TieredCompilation` | `DOTNET_TieredCompilation` as read; empty if unset (empty is not "default") |
| `TieredPgo` | `DOTNET_TieredPGO` as read; empty if unset |

`TieredCompilation` and `TieredPgo` belong to `CompilationConfiguration`; the other rows belong to `PressureRuntimeSnapshot`.

## JSON

Every member of `PressureRuntimeSnapshot` and of `CompilationConfiguration` must be present when reading JSON. A missing member throws `JsonException`. Unavailable values are written as explicit `null`, and measured zeros as `0`.

Definitions follow the .NET documentation for [`GCMemoryInfo`](https://learn.microsoft.com/en-us/dotnet/api/system.gcmemoryinfo?view=net-10.0), [`GCMemoryInfo.Index`](https://learn.microsoft.com/en-us/dotnet/api/system.gcmemoryinfo.index?view=net-10.0), [`GC.GetTotalAllocatedBytes`](https://learn.microsoft.com/en-us/dotnet/api/system.gc.gettotalallocatedbytes?view=net-10.0), [`GC.GetTotalPauseDuration`](https://learn.microsoft.com/en-us/dotnet/api/system.gc.gettotalpauseduration?view=net-10.0), [`GC.GetConfigurationVariables`](https://learn.microsoft.com/en-us/dotnet/api/system.gc.getconfigurationvariables?view=net-10.0), [`Process.WorkingSet64`](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.workingset64?view=net-10.0) and [`Environment.ProcessorCount`](https://learn.microsoft.com/en-us/dotnet/api/system.environment.processorcount?view=net-10.0).
