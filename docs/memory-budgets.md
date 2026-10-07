# Native backing budgets

This page describes [unreleased 0.3.0 development](version-status.md), not the
released 0.2.3 API or an accepted performance/compatibility claim.

`NativeMemoryBudget` is a shared admission domain for complete NAM-requested
native backing extents. It does not claim to bound process RSS, CLR object
headers, external storage owned by another provider, or opaque native allocator
bookkeeping. Borrowed storage and managed metadata require separate accounting
and preparation limits.

The current integration covers direct builders/workspaces/completed transfers,
thread-confined pools/arenas/regions, and synchronized pools/arenas. Storage keeps
its domain through retention, retirement, quarantine and finalizable detachment.
[Prepared fixed-shape page slots](prepared-pools.md) additionally reserve all
pages before metadata acquisition and retain whole-page charges through slot
reuse. [Prepared arenas](prepared-arenas.md) establish exact ordinary/scoped
lane bounds; [immutable sharing](immutable-sharing.md) prepares fixed strong/weak
binding banks. [Application admission](application-admission.md) admits declared
payloads before production, and [typed layouts](typed-layouts.md) share one backing
owner. These are implemented contracts, not accepted full-cost improvements.
All essential diagnostic definitions, equivalent managed/previous-NAM performance
and final source/package/target gates remain mandatory before release.

Budgeted owner constructors require all arguments explicitly. Existing optional
owner signatures and defaults are preserved. Their narrow RS0027 exceptions
are confined to those existing constructors: each new budget-first overload is
fully required and has strictly greater arity than every previously supported
call, so it cannot change existing overload resolution. This avoids replacing
published defaults with an expanded family of forwarding overloads.

For aligned backing, admission includes the native header and known backend
size rounding. The [.NET Unix backend](https://github.com/dotnet/dotnet/blob/b0f34d51fccc69fd334253924abd8d6853fad7aa/src/runtime/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/NativeMemory.Unix.cs)
rounds size to alignment before its native call; the
[Windows backend](https://github.com/dotnet/dotnet/blob/b0f34d51fccc69fd334253924abd8d6853fad7aa/src/runtime/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/NativeMemory.Windows.cs)
passes the requested size to its aligned allocator. The charge therefore reflects the selected
backend, not a fabricated platform-independent physical measurement. Neither
includes unknown allocator-internal bookkeeping or RSS. Admission and physical
release use the same complete known extent. The exposed payload capacity is
not enlarged merely because the backend pads its request.

`NativeOwnerStatistics.RetainedBytes`, `RetiredBytes` and `TrimmedBytes` now
describe complete known owned extents. `UsableCapacityBytes` reports exposed
owned payload capacity: typed capacity for pools and bump payload capacity for
arenas/regions, excluding native headers, unusable tails and borrowed ranges.
`BorrowedBytes` and `RetiredBorrowedBytes` report provider-owned ranges separately;
they do not pretend those ranges were acquired by NAM or charged to its domain.
Requested bytes retain their existing owner-specific meaning: fast arenas
include occupied inter-range alignment, while typed pool demand is logical
element bytes. Capture these owner snapshots at quiescence for reconciliation.

```csharp
NativeMemoryBudget budget = new(64 * 1024);
using NativeBuilder<int> builder = new(budget, preLease: 256);
if (!builder.TryEnsureCapacity(requiredTotalElements))
{
    // No producer ran, no new backing was acquired, and the prefix is intact.
    return;
}

// Produce directly into the admitted capacity; ordinary reuse takes no budget lock.
```

The ceiling is immutable. Admission reserves the entire replacement extent
while the old extent remains charged, before calling native realloc. A growth
preference can fall back atomically to the exact required capacity. A successful
fallback is not counted as a rejected request. Completion and move do not
uncharge backing or replace its full capacity with logical length. Disposal or
emergency physical release removes its charge exactly once; the budget does not
retain the owner.

`TryEnsureCapacity` takes a **total** element capacity. A ceiling refusal returns
false without allocation or losing the initialized prefix. Invalid arguments
throw. Actual native allocation failure retains the builder's existing terminal
failure/cleanup policy. Ordinary append/write also retains that policy when it
throws; use preflight when budget pressure is an expected, recoverable outcome.

The old integer builder constructor remains binary-compatible. Its optional
default is replaced by an explicit parameterless constructor, preserving ordinary
source calls without introducing ambiguous optional overloads. Budgeted creation
uses the explicit two-argument constructor.

## Snapshot contract

`CaptureStatistics()` returns one value-only observation under the domain's
admission lock. It does not reset history or grant borrowing authority. Sizes
are bytes; counts are whole events; identities are stable process-local integers,
not addresses. Budget state is never reset by internal process-measurement resets.

| Field | Actual definition/update |
| --- | --- |
| `Id` | Assigned once on domain construction with checked, non-reusing identity advancement. |
| `CapacityBytes` | Immutable native admission ceiling supplied at construction. |
| `CommittedBytes` | Full successfully acquired extents, removed only on physical release or replaced after successful realloc. |
| `ReservedBytes` | In-flight admitted acquisition demand; moved to committed extent on success or cancelled on failure. |
| `PeakCommittedBytes` | Lifetime maximum committed extents, not an assertion about opaque realloc internals. |
| `PeakAdmittedBytes` | Lifetime maximum committed-plus-reserved admission demand, including conservative old-plus-new overlap. |
| `AllocationCount` | Successful fresh backing acquisitions, including realloc from null. |
| `ReallocationCount` | Successful realloc calls, including realloc from null; it overlaps that acquisition case. |
| `AcquiredBackingBytes` | Lifetime sum of full successfully acquired nonempty NAM-requested extents, including realloc from null and application-prepared backing. Releases do not subtract history. No charged reuse or failed/refused attempt is counted. |
| `ReplacementBackingBytes` | Lifetime sum of full successful realloc targets for previously nonempty backing. Not net growth; excludes realloc from null, already counted in acquisition bytes. |
| `FreeCount` | Physical release calls; realloc is not an invented free event. |
| `ActiveAllocationCount` | Exact current charged allocation gauge, updated at acquisition and physical release under the existing domain lock. It cannot be derived from two saturated lifetime histories. Each live allocation owns at least one byte, so admission bounds the gauge by the immutable byte ceiling. |
| `RejectedAllocationCount` | Requests whose minimum complete extent did not fit, before native allocation. A failed preference with a successful exact fallback is not a rejection. |
| `FailedAllocationCount` | Admitted acquisition attempts cancelled after failure. Ceiling refusal never acquired a reservation and is not counted here. |
| `TraceCapacity` | Fixed managed event-array capacity chosen at construction; zero disables event construction. This array is managed metadata, not native backing charged to the ceiling. |
| `TraceCount` | Events currently retained in the ring, never larger than its capacity. |
| `DroppedTraceEventCount` | Events overwritten or omitted after sequence exhaustion, not those omitted by a short copy destination. Saturates at `long.MaxValue`; check `TraceOverflowed`. |
| `TraceOverflowed` | True if event identities were exhausted or the dropped count ceased to be exact. This diagnostic condition never interrupts a successful allocation or physical release. |
| `HistoryOverflowed` | A lifetime event or byte history exceeded Int64's representable history. Values at `long.MaxValue` are lower bounds; other unsaturated histories remain exact. Charged allocations, bytes, reservations and peaks remain exact and do not depend on history subtraction. |

The two byte histories partition successful backing requests. They are available
without tracing, survive ring overwrites and owner cleanup, and do not double
count realloc from null. Sum them only with checked/wider arithmetic when exact
histories are required. They measure NAM-requested extents, not bytes newly
allocated inside opaque realloc, private backend overhead, realloc's physical
peak, process RSS or complete allocation volume. They cannot alone establish a
memory-allocation win over C#; managed metadata and backend limits still matter.
Updates run only at existing successful backing transitions under the existing
budget gate: two lifetime fields, no trace buffer or new synchronization. Warmed
reuse, reservations, ordinary refusal and snapshots add no byte-history writes.

Lifetime acquisition/realloc/free/refusal/failure counters saturate with the visible
`HistoryOverflowed` flag, rather than throwing after a successful physical storage
transition. The exact live gauge is independent of that diagnostic history, so
exhausted histories cannot prevent cleanup or make a budget refuse real capacity.
Owner/domain identities still advance with checked, non-reusing authority; they
are not converted into saturated or wrapping lifetime tokens. Budget history and
trace history are independent flags and never reset during ordinary capture.

## Owner identities and optional tracing

Every owner is assigned a positive, checked, process-local identity once, without
a global owner registry or a reference to that owner. Owner statistics and
structural snapshots carry `OwnerId`. A builder's completed transfer and its
subsequent moves keep that backing-lineage identity; identity does not confer
authority to access a lease. Resetting a generation or disposing an owner does
not reuse its identity. A budget has a separate domain identity.

Tracing is opt-in: `new NativeMemoryBudget(capacityBytes, traceCapacity)` allocates
one bounded managed event array during construction. The default constructor
uses no event storage. Disabled tracing returns before constructing an event
or reading the clock. Enabled tracing records native admission, refusal,
acquisition, realloc, acquisition rollback and physical release under the
existing budget lock. Prepared pages also emit actual page acquisition,
preparation completion and physical trim events. Reusing backing does not take that lock or emit an event.
It does not trace every element, callback or warmed lease.

Concurrent-owner generation lifecycle events also record actual detach,
retirement, failed-drain quarantine and successful cleanup. `Generation` is the
real checked generation identity, including the initial zero; non-generation
events report `null`. This view reuses `CorrelationId`, which otherwise identifies
the ownership control on pointer/reservation events. These IDs are values, not
references that keep an owner, generation or payload alive.

For generation events, `RequestedBytes` is the complete known NAM-owned extent
associated with that transition; provider-owned external ranges are excluded.
Detach, retirement and quarantine do not release the backing charge.
`GenerationReleased` follows completed generation cleanup and can carry zero
after all backing was transferred into a successor, or segment emergency
finalizers already freed it. Segment and generation finalizers have no relative
ordering guarantee. It is not a physical-free
counter: individual `Released` events and `FreeCount` record actual native frees.
Failed cleanup emits no successful generation completion. These events use the
same bounded ring, sequence exhaustion and dropped-event rules. Fast nongenerational
owners are not assigned a fabricated generation merely to populate this field.

`CopyTraceTo(Span<NativeMemoryTraceEvent>)` copies without allocating or resetting
history. Events are chronological; a short destination receives the newest
suffix. Each event holds values only: domain and optional backing-owner IDs,
sequence, monotonic `Stopwatch` timestamp ticks, transition kind, complete
requested/previous extents, and post-transition committed/reserved bytes.
Use `Stopwatch.Frequency` to interpret ticks; they are not UTC timestamps.
Production backing transitions supply their real owner ID; ownerless internal
domain operations report `null`, never a fabricated owner zero.
`AllocationOrdinal` is the owner-local backing acquisition ordinal where supplied:
fast-pool slabs/pages, fast-arena segments, lexical-region segments and direct blocks.
A direct builder/workspace lineage has one block, ordinal one, including realloc
from null, later replacement and physical return after completion or movement.
Reallocation does not create a second live block or another owner lineage.
Regions use their checked append count for consecutive acquired segment identities;
initializer rollback does not pretend an acquired, retained segment was freed.
The ordinal survives deterministic or emergency cleanup in existing native-header
padding; the 64-byte header extent is unchanged. Exhaustion is checked before
admission/publication. Empty owners, pending/refused/failed acquisition attempts
have no successfully acquired backing ordinal.
Synchronized owners likewise keep their existing lifetime segment sequence across
generations and reuse. Failed backend/admission attempts do not advance it;
acquired storage that later fails metadata publication consumes its identity and
is traced through its actual physical release. The segment replaces its repeated
owner-ID field with this ordinal; immutable owner identity is shared through the
existing numeric backing-history record. No owner/kernel reference is added.
Borrowed registration can have a segment identity but never earns an owned-byte
charge or a fabricated backend acquisition/free event.

Unique move/retirement/return events and immutable sharing/weak events identify
their backing within the same owner lineage. Unique controls derive the ordinal
only for enabled tracing, before clearing released authority. Shared control keeps
one additional Int64 number after payload return, not a payload/segment/allocator
reference; expired weak events can still be correlated without retaining native
storage. Count its eight represented bytes in sharing metadata cost. Producer
permissions have no acquired-backing ordinal while pending; successful direct
preparation, activation and return use their existing single block. Empty ranges
do not invent a physical identity. Typed layouts use that same direct lineage.

`CorrelationId` has event-specific meaning: pointer/control identity for ordinary
ownership events, layout descriptor identity for layout preparation/initialization,
destination control identity for layout-field detach, source sharing-control
identity for shared-slice detach, and actual generation for generation events.
It is not interchangeable with `AllocationOrdinal` or `OwnerId`.
Aggregate preparation/generation events have no single backing ordinal;
other transitions lacking an acquired backing report unavailable (`null`), not an
invented zero. Identity fields are observations, not lifetime or borrowing authority.

Sequence exhaustion stops new event recording rather than wrapping an identity.
Dropped-count exhaustion saturates and sets `TraceOverflowed`; after that flag,
the count is a lower bound, not a claimed exact measurement. Memory admission
and release continue independently of diagnostic overflow. Copying retains no
owner, pointer or borrowing authority. Native-acquisition failures are distinct
from ceiling refusals and release the admitted reservation before being traced.

The runtime exposes actual acquisition, realloc and free counters through
`NativeMemoryDiagnostics.Snapshot()`. Existing-block realloc now updates the
extent delta rather than manufacturing a free/acquisition pair. These process
fields are read independently, not as one linearizable domain snapshot. Reconcile
them at a quiescent boundary. Internal tests reset measurement epochs; a block
first resized into a new measurement epoch becomes accounted in that epoch.
Those resets are not a production memory-limit mechanism. Neither native snapshot
nor a budget snapshot measures unknown allocator-internal transient copying or RSS.

The required final evidence includes competing real owners, refusal before
production, overlap and rollback, retained/retired/quarantined charge, external
storage, all pointer lifetimes, prepared exhaustion, and equivalent managed work.
Local direct-owner regressions alone cannot establish those remaining obligations
or general performance superiority.
