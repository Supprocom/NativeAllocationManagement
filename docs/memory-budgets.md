# Native backing budgets

`NativeMemoryBudget` is a shared admission domain for complete NAM-requested
native backing extents. It does not claim to bound process RSS, CLR object
headers, external storage owned by another provider, or opaque native allocator
bookkeeping. Borrowed storage and managed metadata require separate accounting
and preparation limits.

The current integration covers `NativeBuilder<T>`, `NativeWorkspace<T>` and the
direct backing carried by completed/moved `NativeTransfer<T>` values. Pools,
arenas, regions, page slots and shared-pointer controls must also participate
before the complete 0.3.0 boundary is eligible for release. This document does
not declare those pending integrations implemented.

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
