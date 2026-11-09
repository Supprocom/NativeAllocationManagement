# Demo: Linux cgroup memory and CPU

`CgroupMemorySnapshot` is part of the voxel demo's shared contract (`SharedContract`), not the NuGet package (reference `SharedContract/SharedContract.csproj` to use it). It reads the Linux cgroup the process runs in. That is the container's or service's total, not NAM's budget and not this process alone.

```csharp
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

CgroupMemorySnapshot cgroup = CgroupMemorySnapshot.Read();
Console.WriteLine(cgroup.Available
    ? $"cgroup v{cgroup.Version}: {cgroup.CurrentBytes} of {cgroup.LimitBytes?.ToString() ?? "no limit"} bytes"
    : "no cgroup memory data");
```

| Call | Reads |
|---|---|
| `Read()` | this process's cgroup, found through `/proc/self/cgroup` and `/proc/self/mountinfo` |
| `ReadFromDirectory(root)` | one directory holding the memory and CPU files |
| `ReadFromDirectories(memoryRoot, cpuRoot, accountingRoot)` | separate directories (cgroup v1 can keep them in different hierarchies) |
| `ReadFromProcFiles(membership, mountinfo)` | the same lookup as `Read()`, on supplied proc text |
| `FromFiles(files)` | supplied file contents, with no source path |

## Reading the values

- Every count and byte value is `long?`. `null` means missing, unreadable, unsupported or ambiguous. A real zero is `0`.
- If the mount cannot be resolved, the values are `null`. The reader does not guess a group.
- Files are read one after another, so fields are not one atomic instant.
- Counters belong to the kernel's hierarchy. The reader never resets them. If a counter went down between two reads, something reset it; do not subtract.
- Version 1 nanosecond CPU values are divided by 1000; the remainder is dropped.
- JSON keeps `null`, zeros and the source paths.

| Property | Source and units |
|---|---|
| `Available` | `CurrentBytes` was read. `false` is not a measured zero and does not blank the other fields |
| `Version` | `1` or `2`; absent interfaces are unavailable |
| `LimitBytes` | v2 `memory.max`, v1 `memory.limit_in_bytes`, bytes |
| `LimitUnlimited` | `true` for an explicit "no limit", `false` for a finite limit; `null` if unknown. Unlimited is not a zero-byte cap |
| `CurrentBytes` | v2 `memory.current`, v1 `memory.usage_in_bytes`, bytes |
| `PeakBytes` | v2 `memory.peak`, v1 `memory.max_usage_in_bytes`: the kernel's high-water mark, not reset per request |
| `LowEvents`, `HighEvents`, `MaxEvents` | v2 `memory.events` counts; v1: unavailable |
| `OomEvents` | v2 `oom` count; v1: unavailable |
| `OomKillEvents` | v2 `memory.events` or v1 `memory.oom_control` `oom_kill` count |
| `OomGroupKillEvents` | v2 `oom_group_kill` count; v1 and older kernels: unavailable |
| `AnonBytes` | v2 `memory.stat` `anon`, bytes; v1: unavailable (v1 RSS is separate) |
| `FileBytes` | v2 `file` or v1 `total_cache`, bytes |
| `SwapCurrentBytes` | v2 `memory.swap.current`, v1 `total_swap`, bytes |
| `SwapPeakBytes` | v2 `memory.swap.peak`; v1: unavailable |
| `V1RssBytes` | v1 `total_rss` (includes swap cache), bytes; not the same as v2 `anon` |
| `LimitHitEvents` | v1 `memory.failcnt`; not an OOM kill and not v2 `max` |
| `PageFaults`, `MajorPageFaults` | v2 `pgfault`, `pgmajfault`; v1 `total_pgfault`, `total_pgmajfault` |
| `CpuUsageMicroseconds` | v2 `usage_usec`, v1 `cpuacct.usage` |
| `CpuUserMicroseconds` | v2 `user_usec`, v1 `cpuacct.usage_user` |
| `CpuSystemMicroseconds` | v2 `system_usec`, v1 `cpuacct.usage_sys` |
| `CpuPeriods` | `cpu.stat` `nr_periods` of the CPU-bandwidth hierarchy |
| `CpuThrottledPeriods` | `cpu.stat` `nr_throttled` |
| `CpuThrottledMicroseconds` | v2 `throttled_usec`, v1 `throttled_time` |
| `SourcePath` | the memory directory that was read |
| `CpuSourcePath` | the directory whose `cpu.stat` was read; `null` if the file was missing |
| `CpuAccountingSourcePath` | the v1 directory with `cpuacct.usage`, `.usage_user`, `.usage_sys`; `null` for v2 or if missing |

If the v1 `cpuacct` nanosecond files are missing, the CPU fields are `null`.

Reading is a cold operation. Do not call it in per-element code, and do not read its cost as an allocator result.

The values follow the kernel documentation: [proc mountinfo](https://docs.kernel.org/filesystems/proc.html), [v1 CPU accounting](https://docs.kernel.org/admin-guide/cgroup-v1/cpuacct.html), [v1 bandwidth statistics](https://docs.kernel.org/scheduler/sched-bwc.html) and [the v2 interface](https://docs.kernel.org/admin-guide/cgroup-v2.html).
