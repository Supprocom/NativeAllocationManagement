# Memory budgets

A `NativeMemoryBudget` caps the native memory that NAM owners request. Give several owners the same budget and they draw from one limit.

A budget does not limit process memory (RSS), CLR object headers, the allocator's own bookkeeping, or storage that someone else owns. Memory lent to an owner by someone else (borrowed storage) and managed-heap metadata are not counted. Limit those separately.

```csharp
using Supprocom.NativeAllocationManagement;

NativeMemoryBudget budget = new(capacityBytes: 64 * 1024);
using NativeBuilder<int> builder = new(budget, preLease: 256);

if (!builder.TryEnsureCapacity(10_000))
{
    return; // over budget: nothing was allocated and what you wrote is intact
}

builder.Append(1);
```

The capacity is fixed when the budget is created.

## Owners that take a budget

Budgeted constructors take every argument; none is optional.

| Owner | Constructor |
|---|---|
| `NativeBuilder<T>` | `new(budget, preLease)` |
| `NativeWorkspace<T>` | `new(budget, preLease)` |
| `NativePool<T>` | `new(budget, preLease, preAllocateBytes, returnMemoryOnDispose)` |
| `NativeConcurrentPool<T>` | the same, plus `doNotLeaseOnDeclaration` |
| `NativeArena`, `NativeRegion` | `new(budget, preAllocateBytes, returnMemoryOnDispose)` |
| `NativeConcurrentArena` | the same, plus `doNotLeaseOnDeclaration` |
| `NativePreparedPool<T>` | [prepared pools](prepared-pools.md) |
| `NativeArena` with exact lane bounds | [prepared arenas](prepared-arenas.md) |
| `NativeArena` that grows and trims | [arena retention policy](arena-retention-policy.md) |
| Sharing a budgeted transfer | [immutable sharing](immutable-sharing.md) |
| Declared payloads, admitted before you produce them | [application admission](application-admission.md) |
| Several typed fields in one backing block | [typed layouts](typed-layouts.md) |

## When the budget is full

An allocation that does not fit throws `NativeMemoryBudgetExceededException` (it derives from `InvalidOperationException` and reports `RequestedBytes` and `AvailableBytes`). If the failing call was an `Append` or `Write` on a builder, the builder is disposed like after any other failure.

```csharp
// WRONG: throws NativeMemoryBudgetExceededException, and the builder is disposed
builder.Append(new int[1_000_000]);
```

When full is a normal outcome, ask first. `TryEnsureCapacity(total)` takes the **total** element capacity you need, not the amount to add. It returns `false` without allocating and keeps what you wrote. Invalid arguments throw.

## What is charged

| Rule | Detail |
|---|---|
| What counts | The full extent NAM requests from the allocator, as in the table below. |
| Growth | The whole new extent is admitted while the old one is still charged, so growing needs room for both. |
| Complete and move | `Complete()` and moving a transfer do not release the charge. Dispose does. |
| Growth near the ceiling | If the preferred larger size does not fit, growth falls back to the exact capacity needed. `TryEnsureCapacity` returns `false` only if even that does not fit. |
| Kept storage | An owner that keeps storage for reuse (`NativeMemoryReturn.Reuse`) stays charged after disposal. Before refusing, the budget takes that storage back. |
| Tracing | A budget created with a trace capacity keeps no storage: owners free it on disposal. |
| Not charged | Ranges provided by someone else (`BorrowedBytes`) and unknown allocator bookkeeping. |

Examples of what an owner is charged (on Unix, where sizes round up to 64 bytes):

| Owner | Charge |
|---|---|
| `NativeBuilder<int>` or `NativeWorkspace<int>`, 100 elements | 400 bytes: the elements only |
| `NativePreparedPool<int>`, 10 slots of 10 | 400 bytes: the slots only |
| `NativePool<int>`, 100 elements | 448 bytes |
| `NativeArena` or `NativeRegion`, 100 usable bytes | 192 bytes: a 64-byte header plus the usable bytes, rounded up |

Read the real numbers for your owners from `CaptureStatistics()`.

## Read the budget

`CaptureStatistics()` returns a value copy, taken under the budget's lock. It resets nothing.

```csharp
NativeMemoryBudgetStatistics stats = budget.CaptureStatistics();
Console.WriteLine($"{stats.CommittedBytes} of {stats.CapacityBytes} bytes in use");
```

| Field | Meaning |
|---|---|
| `CapacityBytes` | The ceiling you passed in. |
| `CommittedBytes` | Extents acquired and not yet released. |
| `ReservedBytes` | Extents admitted but not yet acquired. |
| `PeakCommittedBytes` | Highest `CommittedBytes` so far. |
| `PeakAdmittedBytes` | Highest committed plus reserved so far, including the old-plus-new overlap while growing. |
| `ActiveAllocationCount` | Allocations currently charged. |
| `AllocationCount` | Successful acquisitions, including the first `realloc` from null. |
| `ReallocationCount` | Successful `realloc` calls. |
| `FreeCount` | Physical releases. |
| `AcquiredBackingBytes` | Total bytes of every successful acquisition. Releases do not subtract. |
| `ReplacementBackingBytes` | Total bytes of every successful resize of non-empty backing. |
| `RejectedAllocationCount` | Requests that did not fit and never reached the allocator. |
| `FailedAllocationCount` | Admitted requests that the allocator then failed. |
| `TraceCapacity`, `TraceCount` | Size of the trace ring, and events currently in it. Zero capacity means tracing is off. |
| `DroppedTraceEventCount` | Events overwritten, or omitted after the sequence ran out. |
| `TraceOverflowed` | Event numbers ran out or the dropped count stopped being exact. |
| `Id` | Process-local identity of this budget. |
| `HistoryOverflowed` | A lifetime count or byte total reached `long.MaxValue`; those values are lower bounds. Current gauges stay exact. |

The byte totals measure what NAM requested. They do not include allocator overhead, the physical peak of a `realloc`, or RSS.

### Owner statistics

`GetStatistics()` exists on `NativeBuilder<T>`, `NativeWorkspace<T>`, the pools, the arenas and `NativeRegion`. `NativeTransfer<T>`, `NativeShared<T>` and `NativeWeak<T>` have `CaptureSnapshot()` instead. `PreparedPooled<T>` has neither. Capture it when the owner is idle, so the numbers add up.

| Field | Meaning |
|---|---|
| `RetainedBytes`, `RetiredBytes`, `TrimmedBytes` | Complete owned extents, header and padding included. |
| `UsableCapacityBytes` | Payload capacity you can use. Pools count typed capacity, arenas and regions count bump capacity. |
| `BorrowedBytes`, `RetiredBorrowedBytes` | Ranges that someone else provided. NAM did not acquire them and does not charge them. |
| `RequestedBytes` | Live demand. For arenas it includes alignment between ranges. For typed pools it is element bytes. |

## Owner identities and optional tracing

Every owner gets a positive, process-local `OwnerId` that is never reused. Statistics carry it. A budget has its own `Id`.

Tracing is off by default. Give the budget a trace capacity and it keeps that many of the newest events in a ring.

```csharp
NativeMemoryBudget traced = new(1 << 20, traceCapacity: 64);
using (NativeBuilder<int> builder2 = new(traced, preLease: 16))
{
    builder2.TryEnsureCapacity(1_000);
}

Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[64];
int count = traced.CopyTraceTo(events);
for (int index = 0; index < count; index++)
{
    NativeMemoryTraceEvent e = events[index];
    Console.WriteLine($"{e.Sequence} {e.Kind} {e.RequestedBytes} -> committed {e.CommittedBytes}");
}
```

`CopyTraceTo` allocates nothing and resets nothing. Events come oldest first. If the destination is shorter than the ring, you get the newest events.

| Event field | Meaning |
|---|---|
| `Sequence` | Event number. |
| `TimestampTicks` | `Stopwatch` ticks; divide by `Stopwatch.Frequency`. Not UTC. |
| `Kind` | What happened: `Admitted`, `Allocated`, `Reallocated`, `Released`, `Rejected`, `AcquisitionFailed`, plus sharing, reservation, page and generation kinds. |
| `RequestedBytes` | The size the event is about. Backing and admission events use the full native block; layout and copy events use the data size. |
| `PreviousBytes` | The extent before a `Reallocated` event; zero for every other kind. |
| `CommittedBytes`, `ReservedBytes` | Budget totals after the transition. |
| `BudgetId`, `OwnerId` | Where it happened. `OwnerId` is `null` for internal operations that have no owner. |
| `AllocationOrdinal` | The owner's n-th backing block, or `null` if the event has none. |
| `Generation` | Generation number for generation events, otherwise `null`. |
| `CorrelationId` | An id that links related events. What it identifies depends on `Kind`. |

Tracing records admissions, refusals, acquisitions, resizes, releases, prepared page events and generation events. It does not record each element, callback or reuse of warm storage. When the ring is full, the oldest events are overwritten and counted in `DroppedTraceEventCount`.

Process-wide counters are a separate tool: see [production accounting](production-accounting-contract.md).

## Terms

| Term | Meaning |
|---|---|
| Budget | A shared limit on native bytes requested by NAM. |
| Admission | The check that a request fits, made before any native allocation. |
| Committed | Bytes in extents NAM has acquired. |
| Reserved | Bytes admitted for an acquisition still in progress. |
| Extent | The full block NAM requests: header, padding and payload capacity. |
| Borrowed | Memory owned by another provider that an owner uses but does not allocate or charge. |
