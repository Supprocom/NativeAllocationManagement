# Production accounting

`NativeMemoryDiagnostics.Snapshot()` reports the native memory NAM holds in the whole process. It needs no setup and no tracing.

```csharp
using Supprocom.NativeAllocationManagement;

using NativeBuilder<int> builder = new(preLease: 1_024);
builder.Append(1);

NativeMemoryStatistics stats = NativeMemoryDiagnostics.Snapshot();
Console.WriteLine($"outstanding {stats.OutstandingNativeBytes}, peak {stats.PeakOutstandingNativeBytes}");
```

The snapshot allocates nothing. For a per-budget view see [memory budgets](memory-budgets.md).

## Gauges and event counters

| Group | Fields | Available |
|---|---|---|
| Byte gauges | `OutstandingNativeBytes`, `PeakOutstandingNativeBytes`, `DetachedNativeBytes`, `RetiredNativeBytes`, `RetainedNativeBytes` | Always |
| Event counters | `AllocationCount`, `ReallocationCount`, `FreeCount`, `ZeroedAllocationCount`, `DetachedGenerationCount`, `ReusedNativeSegmentCount`, `ReclaimedRangeReuseCount`, `ReclaimedRangeReuseBytes`, `BumpTraversalVisitCount`, `StorageClearCount`, `StorageClearBytes`, `WrittenClearBytes`, `CopiedBytes` | Only in detailed mode, otherwise `0` |

Detailed mode costs cycles on hot paths, so it is off by default. Turn it on before the process starts. `NativeMemoryDiagnostics.DetailedCountersEnabled` tells you which mode is active.

```xml
<!-- in your .csproj -->
<ItemGroup>
  <RuntimeHostConfigurationOption Include="Supprocom.NativeAllocationManagement.DetailedCounters" Value="true" />
</ItemGroup>
```

```bash
# or, without rebuilding
NAM_DETAILED_COUNTERS=1 dotnet run
```

`NAM_DETAILED_COUNTERS` is `1` or `true` to enable; when it is set it wins over the switch.

## What the fields mean

| Field | Meaning |
|---|---|
| `OutstandingNativeBytes` | Extents NAM owns now, header and alignment included. This includes storage that disposed owners kept for reuse. Provider-owned ranges and allocator overhead are not counted. |
| `PeakOutstandingNativeBytes` | Highest outstanding value, including growth in between. |
| `DetachedNativeBytes`, `RetiredNativeBytes` | Subsets of outstanding bytes. Do not add them to it. |
| `RetainedNativeBytes` | Outstanding minus detached. |
| `AllocationCount` | Successful acquisitions, including the first `realloc` from null. |
| `ReallocationCount` | Successful `realloc` calls. |
| `FreeCount` | Physical releases. |
| `ZeroedAllocationCount` | Acquisitions that were zeroed. |
| `DetachedGenerationCount` | Generation detach events, whatever their size. |
| `ReusedNativeSegmentCount` | Times existing backing was reused instead of acquired. |
| `ReclaimedRangeReuseCount`, `ReclaimedRangeReuseBytes` | Reclaimed ranges handed out again, and their bytes. |
| `BumpTraversalVisitCount` | Segments visited while searching for space. Element access does not count. |
| `StorageClearCount`, `StorageClearBytes` | Clears NAM performed on non-empty ranges, and the bytes they visited. |
| `WrittenClearBytes` | Bytes actually zeroed. Can be lower than `StorageClearBytes` because empty reference slots are skipped. |
| `CopiedBytes` | Bytes copied by NAM bulk operations. |
| `MetricsEpoch` | Measurement epoch. Only internal test resets advance it. |
| `HistoryOverflowed` | An event or byte total reached `long.MaxValue`. Gauges stay exact. |

Clears and copies that you do through `AsSpan()` are not counted.

## Reading the numbers

- Read at a point where no other thread allocates or frees. Each thread keeps its own running change, and a snapshot adds them, so the result is exact only then.
- The peak is exact while one thread allocates. When several allocate at once it can miss up to 256 KiB per other thread.
- Counters saturate at `long.MaxValue` and set `HistoryOverflowed`; they never wrap or throw.
- The snapshot does not include allocator-internal copying or RSS.
