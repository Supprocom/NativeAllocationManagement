# Owner statistics and snapshots

Every NAM owner can report what it holds. These calls return plain values that grant no access to the memory:

| Call | Returns | Use it to |
|---|---|---|
| `GetStatistics()` | `NativeOwnerStatistics` | see how many bytes the owner holds and how much is in use |
| `CaptureDiagnosticSnapshot()` | `NativeOwnerDiagnosticSnapshot` | see the owner's internal structure: segments, records, retired storage |
| `NativeMemoryDiagnostics.Snapshot()` | `NativeMemoryStatistics` | see native storage for the whole process |

`NativePool<T>`, `NativeRegion`, `NativeArena`, `NativeConcurrentPool<T>`, `NativeConcurrentArena`, `NativeBuilder<T>`, `NativeWorkspace<T>` and `NativePreparedPool<T>` have both owner calls.

```csharp
using Supprocom.NativeAllocationManagement;

NativeOwnerStatistics stats = MeasureRegion();
Console.WriteLine($"{stats.Lifecycle}: retained {stats.RetainedBytes} B, peak {stats.PeakOutstandingNativeBytes} B");

NativeOwnerDiagnosticSnapshot after = MeasureBuilder();
Console.WriteLine($"{after.Model}: {after.Lifecycle}, {after.RetainedSegmentCount} segments");

NativeMemoryStatistics process = NativeMemoryDiagnostics.Snapshot();
Console.WriteLine($"process-wide outstanding: {process.OutstandingNativeBytes} B");

static NativeOwnerStatistics MeasureRegion()
{
    using NativeRegion region = new();
    region.Lease<int>(1_024, 0).AsSpan()[0] = 1;
    return region.GetStatistics();
}

static NativeOwnerDiagnosticSnapshot MeasureBuilder()
{
    using NativeBuilder<uint> builder = new(preLease: 16);
    builder.Append(1u);
    using NativeTransfer<uint> output = builder.Complete();
    return builder.CaptureDiagnosticSnapshot();   // still works after Complete
}
```

Rules:

- Both calls are observations. Keeping the value keeps no native memory alive.
- They work after the owner is completed or disposed and then report numbers only. A builder that was completed shows `Lifecycle` `Returned`, no current backing, and its peaks.
- A thread-confined owner (`NativePool<T>`, `NativeRegion`, `NativeArena`, `NativeWorkspace<T>`) must be read on the thread that created it; another thread gets `NativeAllocationStateException` (pool, arena) or `InvalidOperationException` (workspace).
- A builder cannot be read while another operation on it runs, for example inside its own `Write` callback: that throws `InvalidOperationException`.
- Reading never resets a counter. Capture is not for per-element code.
- Fields are read one after another, not as one atomic instant. To compare fields with each other, read while no other thread is using the owner.
- Bytes are the extents NAM acquired, including headers and alignment padding. They are not process resident memory (RSS) and do not include the allocator's own bookkeeping.

## NativeOwnerStatistics

| Field | Meaning |
|---|---|
| `Lifecycle` | `Active`, `Returned`, `Disposed`, ... at capture |
| `Model` | Which kind of owner this is (see [Models](#models)) |
| `OwnerId` | Stable id of this owner within the process; not an address |
| `Generation` | Current generation of a pool or arena; zero for owners without generations |
| `RequestedBytes` | Initialized data currently held. A thread-confined arena reports occupied bytes, including padding |
| `UsableCapacityBytes` | Payload capacity exposed to callers, without headers and tail padding |
| `RetainedBytes` | Bytes of backing the owner currently keeps |
| `RetiredBytes` | Bytes in older storage waiting for cleanup |
| `SegmentCount` | Storage segments kept |
| `AvailableSegmentCount` | Segments that are completely unused (not a count of free bytes) |
| `RetiredSegmentCount` | Segments in older storage |
| `BorrowedBytes` | Bytes of provider-owned memory the owner currently borrows; not counted as owned backing |
| `RetiredBorrowedBytes` | The same, still held by older storage |
| `OutstandingNativeBytes` | All bytes NAM currently holds for this owner: retained, retired and detached |
| `DetachedNativeBytes` | The part of outstanding bytes waiting for final release; do not add it to outstanding |
| `PeakOutstandingNativeBytes` | Highest outstanding value over the owner's life |
| `InitializedPayloadBytes` | Initialized data in the current lifetime, without padding |
| `PeakInitializedPayloadBytes` | Highest initialized value over the owner's life |
| `TrimmedBytes`, `TrimCallCount` | Bytes released by explicit trims, and how many trims ran |
| `FreshSegmentAllocationCount` | Segments first acquired from the allocator. Reuse and builder growth are not counted |
| `HistoryOverflowed` | A running total passed `long.MaxValue`; current values stay exact |

## NativeOwnerDiagnosticSnapshot

The snapshot repeats `Lifecycle`, `Model`, `OwnerId`, `Generation`, `HistoryOverflowed`, the outstanding, detached and peak byte fields and the initialized payload fields, and adds structure:

| Field | Meaning |
|---|---|
| `ScopeEpoch` | Counter of the current generation's scoped lifetime; zero without a current generation |
| `MetricsEpoch` | Which process measurement window the numbers belong to |
| `ActiveRecords` | Live bookkeeping records. Plain bump ranges in a region or arena have none, so this can be zero while data is live |
| `ScopedRecords` | The scoped part of those records |
| `ReferenceRoots` | Managed references the owner keeps alive for native slots |
| `OrdinaryTraversalIndex`, `ScopedTraversalIndex` | Where the owner will look next for space; `-1` means no candidate |
| `RetainedSegmentCount`, `AvailableSegmentCount` | Segments kept now, and the idle ones |
| `RetiredGenerationCount` | Older generations still draining; quarantined ones are not counted |
| `RetiredSegmentCount`, `RetiredBytes` | Segments and bytes in retired and quarantined storage |
| `QuarantinedGenerationCount` | Generations whose cleanup failed and which cannot be reused |
| `QuarantinedSegmentCount` | Their segments; a subset of `RetiredSegmentCount` |
| `CurrentGenerationQuarantined` | The current generation is itself quarantined |

## Models

`Model` says what the numbers mean:

| `Model` | Owner | Notes |
|---|---|---|
| `ThreadConfinedPool` | `NativePool<T>`, `NativePreparedPool<T>` | no generations or quarantine |
| `ThreadConfinedRegion` | `NativeRegion` | no scoped bank; generation and scope are zero |
| `ThreadConfinedArena` | `NativeArena` | reports its checked epoch in `Generation`; read it with `unchecked((ulong)value)` |
| `SynchronizedPool`, `SynchronizedArena`, `SynchronizedRegion` | `NativeConcurrentPool<T>`, `NativeConcurrentArena` (`SynchronizedRegion` is used internally) | have generations, retired and quarantined storage |
| `SingleWriterBuilder` | `NativeBuilder<T>` | one block; nothing retired or quarantined |
| `ThreadConfinedWorkspace` | `NativeWorkspace<T>` | one fixed block |
| `Unspecified` | `default` value | not read from an owner |

Fields an owner cannot have are zero (or `-1` for the traversal indexes), not estimates. After `Complete` or `Dispose`, a builder's current backing is zero and its peaks stay. A builder counts its first block once in `FreshSegmentAllocationCount`; growing it is not a new segment.

For budget statistics see [native backing budgets](memory-budgets.md). For prepared pools see [prepared page diagnostics](prepared-pools.md#prepared-snapshot-inventory).

## Process-wide counters

`NativeMemoryDiagnostics.Snapshot()` returns the bytes outstanding, detached, retired and at peak for every NAM owner in the process, plus counters. Per-event counters (allocations, reallocations, frees and so on) cost time on hot paths, so they stay zero unless the process starts with the `Supprocom.NativeAllocationManagement.DetailedCounters` AppContext switch or the environment variable `NAM_DETAILED_COUNTERS=1` (`true` also works; any other value turns them off, even over the switch). `NativeMemoryDiagnostics.DetailedCountersEnabled` tells you which mode is active.
