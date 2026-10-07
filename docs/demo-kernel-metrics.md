# Demo kernel observations (0.3.0 development)

`CgroupMemorySnapshot` observes a Linux kernel cgroup, not NAM's budget or one
process's exclusive memory. Its 29-property inventory is checked against compiled
metadata by [the diagnostic contracts](../conformance/cgroup-diagnostic-contracts.json).

`Read` resolves `/proc/self/cgroup` memberships through `/proc/self/mountinfo`
filesystem/controller, mounted subtree root and mountpoint. It uses whole path
and controller boundaries, decodes kernel mount escapes once, and refuses
traversal, unmatched roots and ambiguous equal-specificity mounts. A more specific
matching subtree is used when available. No mount, namespace or cgroup setting
is changed. Missing/inaccessible proc data does not fall back to a guessed group.
Namespace-private mounts rooted at `/` work normally; an inaccessible/outside
namespace subtree is unavailable, not a guessed host-wide measurement.

`ReadFromDirectory` reads explicit colocated roots; `ReadFromDirectories` accepts
separate memory, CPU bandwidth and CPU accounting roots. `ReadFromProcFiles`
performs the same observation using supplied proc identities. V1 controllers may
belong to different hierarchies, so memory-path provenance never pretends to
identify CPU. `FromFiles` parses supplied contents without inventing source paths.

Each file is sampled independently, not one atomic transaction. Kernel counters,
peaks and limits belong to their external hierarchy, including the kernel's
documented descendants; the reader neither resets them nor establishes a new
measurement epoch. External reset/replacement means a decreasing history cannot
be treated as a valid monotonic delta. Null means missing, invalid, unsupported,
ambiguous or unrepresentable. Valid zero is a real observation. Unsigned v1
nanoseconds are divided by 1000 before conversion to Int64; sub-microsecond
remainder is truncated, not claimed as measured precision.

| Property | Source and units |
| --- | --- |
| `Available` | Whether the actual memory-current value parsed; false is not a measured zero and does not erase separately valid fields. |
| `LimitBytes` | V2 `memory.max` or v1 `memory.limit_in_bytes`, bytes; unlimited is not a zero-byte cap. |
| `CurrentBytes` | V2 `memory.current` or v1 `memory.usage_in_bytes`, bytes. |
| `PeakBytes` | V2 `memory.peak` or v1 `memory.max_usage_in_bytes`, kernel high-water bytes, not a reset per request. |
| `LowEvents` | V2 `memory.events` low events; v1 unavailable. |
| `HighEvents` | V2 high events; v1 unavailable. |
| `MaxEvents` | V2 max events; not v1 fail count. |
| `OomEvents` | V2 oom events; v1 under_oom state is not an event history. |
| `OomKillEvents` | V2 `memory.events` or v1 `memory.oom_control` oom_kill events. |
| `OomGroupKillEvents` | V2 oom_group_kill events; absent kernels/v1 unavailable. |
| `AnonBytes` | V2 `memory.stat` anon bytes; v1 RSS is not substituted. |
| `FileBytes` | V2 file bytes or v1 total_cache bytes. |
| `SwapCurrentBytes` | V2 `memory.swap.current` or v1 total_swap bytes. |
| `SwapPeakBytes` | V2 `memory.swap.peak` high-water bytes; v1 unavailable. |
| `CpuUsageMicroseconds` | V2 usage_usec or v1 unsigned `cpuacct.usage` nanoseconds / 1000. |
| `CpuUserMicroseconds` | V2 user_usec or v1 unsigned `cpuacct.usage_user` nanoseconds / 1000. |
| `CpuSystemMicroseconds` | V2 system_usec or v1 unsigned `cpuacct.usage_sys` nanoseconds / 1000. |
| `CpuPeriods` | `cpu.stat` nr_periods count on the actual CPU bandwidth hierarchy. |
| `CpuThrottledPeriods` | `cpu.stat` nr_throttled count, not an invented ratio. |
| `CpuThrottledMicroseconds` | V2 throttled_usec or v1 unsigned throttled_time nanoseconds / 1000. |
| `PageFaults` | V2 pgfault or v1 total_pgfault events. |
| `MajorPageFaults` | V2 pgmajfault or v1 total_pgmajfault events. |
| `LimitUnlimited` | Explicit v2 max or v1 unlimited/sentinel state; unknown remains null. |
| `Version` | Actual memory/CPU-interface identity, one or two; absent interfaces are unavailable. |
| `V1RssBytes` | V1 total_rss bytes, including swap cache; not v2 anon. |
| `LimitHitEvents` | V1 memory.failcnt events; not OOM-kill or v2 max events. |
| `SourcePath` | Actual resolved/explicit memory directory, not a pointer/owner identity. |
| `CpuSourcePath` | Actual directory whose cpu.stat was read; missing file means null. |
| `CpuAccountingSourcePath` | Actual v1 directory with read cpuacct.usage/user/sys files; v2/missing means null. |

Older v1 `cpuacct.stat` user/system values use USER_HZ. They are not guessed as
microseconds when the precise nanosecond files are unavailable. Unsupported
interfaces remain explicitly unavailable; no private-runtime counter is filled
with a pretend zero. JSON preserves null and measured zero plus each source path.
Sampling/parser/serialization work is cold observability cost, not allocator
per-element work or evidence of native performance superiority.

Definitions follow the primary [proc mountinfo contract](https://docs.kernel.org/filesystems/proc.html),
[v1 CPU accounting units](https://docs.kernel.org/admin-guide/cgroup-v1/cpuacct.html),
[v1 bandwidth statistics](https://docs.kernel.org/scheduler/sched-bwc.html),
[v2 CPU interface](https://docs.kernel.org/admin-guide/cgroup-v2.html) and
[pinned v6.17 accounting fields](https://github.com/torvalds/linux/blob/v6.17/kernel/sched/cpuacct.c).
