# Owner statistics: totals and gauges

`GetStatistics()` on a pool, prepared pool, region, arena, builder, workspace or concurrent owner returns a `NativeOwnerStatistics`. It has two kinds of fields:

| Kind | Fields | Behaviour |
|---|---|---|
| Gauges (the state right now) | `RequestedBytes`, `RetainedBytes`, `RetiredBytes`, `SegmentCount`, `AvailableSegmentCount`, `RetiredSegmentCount` | always exact |
| Totals (the owner's lifetime) | `FreshSegmentAllocationCount`, `TrimCallCount`, `TrimmedBytes` | only grow |

```csharp
using Supprocom.NativeAllocationManagement;

using NativePool<int> pool = new(preLease: 4_096);
using (Pooled<int> values = pool.Rent(1_024, 0))
{
}

nuint released = pool.TrimRetainedMemoryByBytes(4_096);
NativeOwnerStatistics stats = pool.GetStatistics();

Console.WriteLine($"fresh={stats.FreshSegmentAllocationCount} trims={stats.TrimCallCount} "
    + $"trimmed={stats.TrimmedBytes} overflowed={stats.HistoryOverflowed} released={released}");
```

## Reading the totals

| Field | Counts |
|---|---|
| `FreshSegmentAllocationCount` | first-time backing allocations; reusing a segment does not count |
| `TrimCallCount` | completed trim calls, including calls that released nothing |
| `TrimmedBytes` | bytes physically released by trimming |
| `HistoryOverflowed` | `true` once a total would pass `long.MaxValue` |

A total stops at `long.MaxValue` and sets `HistoryOverflowed`. Nothing throws and no operation fails because of it. Gauges stay exact, so do not compute them by subtracting totals.

## When to capture

Capture at a maintenance point, not for every lease. Some owners take a lock or scan their storage to answer. See [Statistics and trimming](getting-started.md#statistics-and-trimming).

Process-wide counters come from `NativeMemoryDiagnostics.Snapshot()`; see [Production accounting](production-accounting-contract.md).
