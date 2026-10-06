# Demo process and GC observations (0.3.0 development)

`PressureRuntimeSnapshot.Capture` reports actual cold runtime/process/kernel
observations. Its20 properties and the two nested compilation properties have
[typed contracts and executable proofs](../conformance/pressure-runtime-diagnostic-contracts.json).
This is not the complete release API/diagnostic inventory or a performance verdict.

Capture is sequential, not atomic. It first obtains one `GCMemoryInfo`, then reads
process, GC/configuration and kernel values. Sampling itself can allocate managed
storage and consume CPU. Completed-GC fields all refer to that one GC observation;
they are not fresh live-heap measurements at the later `Utc` timestamp. The cold
collector does not reset the runtime, process or external kernel histories.

The actual no-GC `GCMemoryInfo.Index == 0` state maps all eight completed-GC fields
to null, not a fabricated memory zero. Public capture samples that actual predicate
once. The private mapping core is tested with explicit absence and an actual
completed collection, and the public predicate is checked; this is branch evidence,
not a fresh-process no-GC claim. A bare default `GCMemoryInfo` is invalid to read:
its getters throw, unlike the runtime's valid zero-index result. A default
`PressureRuntimeSnapshot` is an uninitialized value, not a captured measurement:
it has no UTC observation or captured configuration. OS/runtime query failures
propagate rather than returning a successful snapshot filled with pretend zeros.

| Property | Actual source, units and lifetime |
| --- | --- |
| `Utc` | UTC wall-clock at capture; not a monotonic duration clock or atomic observation of every field. |
| `TotalAllocatedBytes` | Precise `GC.GetTotalAllocatedBytes(true)`, cumulative managed bytes since runtime startup; excludes native bytes and is not live memory or reclaimed/RSS bytes. |
| `Gen0Collections` | `GC.CollectionCount(0)`, runtime-lifetime collection count for generation0. |
| `Gen1Collections` | `GC.CollectionCount(1)`, runtime-lifetime collection count for generation1. |
| `Gen2Collections` | `GC.CollectionCount(2)`, runtime-lifetime collection count for generation2. |
| `TotalPauseMilliseconds` | Runtime-lifetime `GC.GetTotalPauseDuration`, milliseconds paused for GC; not wall time or a percentage. |
| `TotalAvailableMemoryBytes` | Bytes available to GC in the associated completed-GC observation; not NAM's allocator cap. |
| `MemoryLoadBytes` | Physical memory load bytes in that GC observation; not one process's exclusive usage. |
| `HighMemoryLoadThresholdBytes` | GC high-memory-load threshold bytes in that observation. |
| `TotalCommittedBytes` | Committed managed heap bytes reported by that GC observation; not native budget charges. |
| `HeapSizeBytes` | Heap size at that collection, not a current live-object count. |
| `FragmentedBytes` | Heap fragmentation bytes at that collection; an observed zero is valid. |
| `LargeObjectHeapBytes` | Generation-info slot3's `SizeAfterBytes` in that collection; missing slot is null, not all large/pinned/native memory. |
| `ProcessWorkingSetBytes` | Fresh current-process resident working set, bytes, including shared and private pages; not peak usage, reserved bytes or an allocator-only measure. |
| `ProcessCpuMilliseconds` | Actual process CPU time across its threads, cumulative milliseconds; not CPU percent or wall-clock time. |
| `ProcessorCount` | Runtime's available logical processors; startup value includes runtime affinity/quota rules, not physical machine CPUs or a continuously updated quota. |
| `Cgroup` | Independently available [kernel fields and provenance](demo-kernel-metrics.md); hierarchy-wide storage is not process-exclusive or a NAM budget. |
| `GcConfiguration` | Actual runtime-returned GC configuration, copied to independent invariant strings; values can differ from what a user requested. Missing entries are not synthesized. |
| `CompilationConfiguration` | The two raw environment strings below; not a report of effective JIT defaults or runtime-applied settings. |
| `LastCompletedGcIndex` | Identity of the sampled completed GC; null if none. Background/foreground completion can expose indices out of order, so it is not a monotonic clock. |
| `TieredCompilation` | `DOTNET_TieredCompilation` as read, or empty if absent. Empty is unavailable requested configuration, not disabled/default inference. |
| `TieredPgo` | `DOTNET_TieredPGO` as read, or empty if absent. Other configuration mechanisms and runtime startup state are not reconstructed. |

Histories belong to their own runtime/process/hierarchy lifetime. A valid zero is
preserved; unavailable nullable fields remain null through JSON. Do not subtract
uninitialized values, different processes or externally reset kernel counters.
Memory being returned to an owner, a GC occurring or a managed reference being
dropped does not establish physical RSS release. A NAM native-byte budget does not
bound the complete process working set or all managed metadata.

Definitions follow primary .NET10 documentation for [GC observations and missing
collections](https://learn.microsoft.com/en-us/dotnet/api/system.gcmemoryinfo?view=net-10.0),
[GC index ordering](https://learn.microsoft.com/en-us/dotnet/api/system.gcmemoryinfo.index?view=net-10.0),
[managed allocation totals](https://learn.microsoft.com/en-us/dotnet/api/system.gc.gettotalallocatedbytes?view=net-10.0),
[pause duration](https://learn.microsoft.com/en-us/dotnet/api/system.gc.gettotalpauseduration?view=net-10.0),
[actual GC configuration](https://learn.microsoft.com/en-us/dotnet/api/system.gc.getconfigurationvariables?view=net-10.0),
[working sets](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.workingset64?view=net-10.0)
and [available processor count](https://learn.microsoft.com/en-us/dotnet/api/system.environment.processorcount?view=net-10.0).
Local contracts do not certify final Windows/ARM64/trimming/NativeAOT deployments,
general performance superiority or0.3.0 shipping acceptance.
