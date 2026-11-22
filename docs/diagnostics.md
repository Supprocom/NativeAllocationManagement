# Owner statistics and snapshots

Use these calls to see how much native memory an owner holds and how much of it is in use. They return plain values that grant no access to the memory.

| Call | Returns | Use it to |
|---|---|---|
| `GetStatistics()` | `NativeOwnerStatistics` | see how many bytes the owner holds and how much is in use |
| `CaptureDiagnosticSnapshot()` | `NativeOwnerDiagnosticSnapshot` | track down a problem: segments, bookkeeping records, retired storage |
| `NativeMemoryDiagnostics.Snapshot()` | `NativeMemoryStatistics` | see native storage for the whole process |

Most programs only need `GetStatistics()`. `NativePool<T>`, `NativeRegion`, `NativeArena`, `NativeConcurrentPool<T>`, `NativeConcurrentArena`, `NativeBuilder<T>`, `NativeWorkspace<T>` and `NativePreparedPool<T>` have both owner calls.

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

## Rules

- Both calls only observe. Keeping the value keeps no native memory alive.
- They work after the owner is completed or disposed, and then report numbers only. A completed builder shows `Lifecycle` `Returned`, no current backing, and its peaks.
- Read a thread-confined owner (`NativePool<T>`, `NativeRegion`, `NativeArena`, `NativeWorkspace<T>`) on the thread that created it. Another thread gets `NativeAllocationStateException` (pool, arena) or `InvalidOperationException` (workspace).
- A builder cannot be read while another operation on it runs, for example inside its own `Write` callback. That throws `InvalidOperationException`.
- Reading never resets a counter. Do not call it per element.
- Fields are read one after another, not at one instant. To compare fields with each other, read while no other thread uses the owner.
- Bytes are the blocks NAM acquired, including headers and alignment padding. They are not process resident memory (RSS) and do not include the allocator's own bookkeeping.

## NativeOwnerStatistics

**What the owner is**

| Field | Meaning |
|---|---|
| `Lifecycle` | `Active`, `Returned`, `Disposed`, ... at capture |
| `Model` | Which kind of owner this is (see [Models](#models)) |
| `OwnerId` | Id of this owner within the process; not an address |
| `Generation` | The owner's current generation (the period its memory stays valid before `Reset` or a return ends it); zero for owners without generations |

**Bytes now**

| Field | Meaning |
|---|---|
| `RequestedBytes` | Initialized data currently held. A thread-confined arena reports occupied bytes, including padding |
| `UsableCapacityBytes` | Space exposed to callers, without headers and tail padding |
| `RetainedBytes` | Bytes of backing the owner currently keeps |
| `RetiredBytes` | Bytes in older storage waiting for cleanup |
| `OutstandingNativeBytes` | All bytes NAM currently holds for this owner: retained, retired and detached |
| `DetachedNativeBytes` | The part of outstanding bytes waiting for final release; do not add it to outstanding |
| `BorrowedBytes`, `RetiredBorrowedBytes` | Memory lent by someone else, now and in older storage; not counted as owned |
| `InitializedPayloadBytes` | Initialized data in the current lifetime, without padding |

**Segments**

| Field | Meaning |
|---|---|
| `SegmentCount` | Storage segments (blocks of native memory) kept |
| `AvailableSegmentCount` | Segments that are completely unused (not a count of free bytes) |
| `RetiredSegmentCount` | Segments in older storage |

**Over the owner's life**

| Field | Meaning |
|---|---|
| `PeakOutstandingNativeBytes`, `PeakInitializedPayloadBytes` | Highest values so far |
| `TrimmedBytes`, `TrimCallCount` | Bytes released by explicit trims, and how many trims ran |
| `FreshSegmentAllocationCount` | Segments first acquired from the allocator. Reuse and builder growth are not counted |
| `HistoryOverflowed` | A running total passed `long.MaxValue`; current values stay exact |

## NativeOwnerDiagnosticSnapshot

The snapshot repeats `Lifecycle`, `Model`, `OwnerId`, `Generation`, `HistoryOverflowed`, the outstanding, detached and peak byte fields and the initialized payload fields. It adds details about the owner's bookkeeping:

| Field | Meaning |
|---|---|
| `ScopeEpoch` | Counter of the current generation's scoped lifetime; zero without a current generation |
| `MetricsEpoch` | Which process measurement window the numbers belong to |
| `ActiveRecords` | Live bookkeeping records. Plain ranges in a region or arena have none, so this can be zero while data is live |
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
| `ThreadConfinedRegion` | `NativeRegion` | no scoped lane; generation and scope are zero |
| `ThreadConfinedArena` | `NativeArena` | `Generation` holds an unsigned counter; read it with `unchecked((ulong)value)` |
| `SynchronizedPool`, `SynchronizedArena`, `SynchronizedRegion` | `NativeConcurrentPool<T>`, `NativeConcurrentArena` (`SynchronizedRegion` is used internally) | have generations, retired and quarantined storage |
| `SingleWriterBuilder` | `NativeBuilder<T>` | one block; nothing retired or quarantined |
| `ThreadConfinedWorkspace` | `NativeWorkspace<T>` | one fixed block |
| `Unspecified` | `default` value | not read from an owner |

Fields an owner cannot have are zero (or `-1` for the traversal indexes), not estimates. After `Complete` or `Dispose`, a builder's current backing is zero and its peaks stay. A builder counts its first block once in `FreshSegmentAllocationCount`; growing it is not a new segment.

For budget statistics see [native backing budgets](memory-budgets.md). For prepared pools see [prepared page diagnostics](prepared-pools.md#prepared-snapshot-inventory).

## Process-wide counters

`NativeMemoryDiagnostics.Snapshot()` returns the bytes outstanding, detached, retired and at peak for every NAM owner in the process, plus counters. The per-event counters (allocations, reallocations, frees and so on) cost time on hot paths, so they stay zero by default. To turn them on, start the process with one of these:

- the `Supprocom.NativeAllocationManagement.DetailedCounters` AppContext switch;
- the environment variable `NAM_DETAILED_COUNTERS=1` (`true` also works).

Any other value of the environment variable turns them off, even if the switch is on. `NativeMemoryDiagnostics.DetailedCountersEnabled` tells you which mode is active. See [production accounting](production-accounting-contract.md).
