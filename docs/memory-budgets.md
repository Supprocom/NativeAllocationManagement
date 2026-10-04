# Native backing budgets

`NativeMemoryBudget` is a shared admission domain for complete NAM-requested
native backing extents. It does not claim to bound process RSS, CLR object
headers, external storage owned by another provider, or opaque native allocator
bookkeeping. Borrowed storage and managed metadata require separate accounting
and preparation limits.

The current integration covers direct builders/workspaces/completed transfers,
thread-confined pools/arenas/regions, and synchronized pools/arenas. Storage keeps
its domain through retention, retirement, quarantine and finalizable detachment.
Prepared execution, page slots, shared-pointer controls and tracing are still
required before the complete 0.3.0 boundary is eligible for release.

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
| `FreeCount` | Physical release calls; realloc is not an invented free event. |
| `ActiveAllocationCount` | Derived exactly from successful acquisitions minus physical releases. No redundant mutation counter. |
| `RejectedAllocationCount` | Requests whose minimum complete extent did not fit, before native allocation. A failed preference with a successful exact fallback is not a rejection. |
| `FailedAllocationCount` | Admitted acquisition attempts cancelled after failure. Ceiling refusal never acquired a reservation and is not counted here. |

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
