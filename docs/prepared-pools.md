# Prepared fixed-shape pools

Use `NativePreparedPool<T>` with `NativePoolPreparation` when a workload genuinely needs a known number of
simultaneously live unmanaged buffers of one bounded shape. It groups those slots
into pages and prepares both native backing and the complete slot/page metadata banks and one embedded-link free list. It is not a general heap or automatic sizing policy.

```csharp
NativeMemoryBudget budget = new(64 * 4096);
using NativePreparedPool<byte> pool = new(new NativePoolPreparation(64, 4096, 16), budget);
if (pool.TryRent(4096, static writer => writer.Fill(42),
    out PreparedPooled<byte> lease, out NativePoolExhaustionReason reason))
{
    try
    {
        // Bounded access validates the same owner/thread/token as ordinary pooling.
        _ = lease.Read(static view => view[0]);
    }
    finally
    {
        lease.Dispose();
    }
}
else
{
    // No producer ran and no lease was published. Handle reason at application level.
}
```

This preparation makes four 64 KiB backing acquisitions, not 64 independent
4 KiB acquisitions. All page extents are admitted before metadata or backing
acquisition; a partial failure frees acquired pages and cancels the uncommitted
remainder. Each slot has exactly the declared element capacity, packed at the CLR element
stride without cache-line padding or an additional SIMD-address alignment guarantee. A shorter final page contains only the remaining declared slots.
The admitted page extent is the exact sum of its slots.

Within retained capacity, NAM rent/complete initialization, destructive lexical
movement, bounded access, return, snapshots and expected exhaustion allocate no managed storage or native
backing and do not grow metadata. Consumer callbacks can still allocate. There
is no implicit refill, OS residency guarantee, zero-page-fault guarantee, or
guarantee about unrelated runtime work.

`PreparedPooled<T>.Move(ref source)` reuses the slot's existing identity metadata. It
clears the source on success and invalidates its previous aliases without copying
the payload. The destination retains the source's cleanup obligation and stays
on the pool's construction thread. The capability remains a lexical ref structure,
not a heap-storable or cross-thread pointer. Use `NativeTransfer<T>` for those
different ownership requirements.

```csharp
PreparedPooled<byte> source = pool.Rent(4096, static writer => writer.Fill(42));
using PreparedPooled<byte> destination = PreparedPooled<byte>.Move(ref source);
_ = destination.Read(static view => view[0]);
// source is now default; do not access or dispose it.
```

Movement validates before consumption. A stale/default source, wrong thread,
entered borrow or exhausted monotonic identity is an error; failure leaves the
source unchanged so its actual owner can still return it. This differs from
`NativeTransfer<T>.Move`, whose source is consumed on failure. Using bindings
are read-only and cannot be passed by ref; keep a movable source in an ordinary
local and put its final destination in a using or proven finally cleanup.

`TryRent` returns false only for `ShapeExceeded` or `NoAvailableSlot`. A negative
length, null initializer, wrong thread, closed owner, exhausted
token identity, throwing initializer or incomplete initialization is an error,
not ordinary capacity exhaustion. Failure returns a slot without exposing the
partial payload. The failure output capability is default and cannot be borrowed
or disposed as an owner. The bundled analyzer recognizes direct positive and
negated acquisition guards, including early-return guards; unguarded capability
use remains unproven. Returning only a boolean and later testing an unrelated or
reassigned value does not create ownership proof.

`Rent` also works on prepared pages, but throws for expected exhaustion;
use `TryRent` when fullness is normal. Ordinary variable-shape pool constructors
retain their existing behavior. A prepared owner deterministically releases its
backing on Dispose after all leases return; emergency finalization is fallback,
not timely capacity relief. The existing retirement/coordinator cleanup contract
also applies. Invalid or stale aliases cannot return or access a reused slot.

## Pages and retention

Slot return is reuse permission, not a native free. One live or initializing slot
retains its whole page, and a borrow prevents premature return. Trim releases
only wholly idle pages. It can release more than the requested byte amount to
respect whole-page boundaries. Trim lowers current retained capacity without
changing original preparation bounds or refilling backing. Construct a new
explicitly prepared owner to increase capacity; never change a live slot shape.

Storage/structural segment counts describe physical pages for this mode. Active
records describe occupied slot metadata; available segments are completely idle
pages, not free slots on partially occupied pages. Use `CapturePreparedSnapshot`
for actual slot availability. Small logical ranges still retain the full slot
and, indirectly, its whole page; dense and sparse retention must be measured.

## Prepared snapshot inventory

All fields are derived or recorded by `NativePreparedPool.GetPreparedStatistics`
on the construction thread. The value retains no authority, can observe closure,
does not allocate or reset history, and remains owner-local for the full lifetime.
Counts are integers; byte fields are bytes, not RSS. Tests in
`NativePreparedPoolTests` cover alternate states, failure, sparse trim and cleanup.

| Field | Definition and source |
| --- | --- |
| `OwnerId`, `Lifecycle` | Actual stable owner identity and lifecycle. |
| `Preparation` | Original checked slot count, exact element capacity and page grouping. A caller-created default is not a valid preparation. |
| `RetainedPageCount`, `RetainedSlotCount` | Physically present pages and their actual slot sum; reduced by trim or release. |
| `OccupiedSlotCount` | Actual initializing/published live slot metadata, not entered-borrow count. |
| `PeakOccupiedSlotCount` | Recorded maximum occupied slots, including failed initialization; retained after return/closure. |
| `AvailableSlotCount` | Retained slots minus occupied slots; trimmed slots are not available. |
| `RetainedBytes`, `PeakRetainedBytes` | Complete known owned page extents and recorded high-water extent; exclude CLR metadata and opaque allocator bookkeeping. |
| `SuccessfulRentCount` | Actual complete initialization/publication events, not attempts. |
| `RejectedShapeCount`, `RejectedFullCount` | Actual shape/full refusals before production; distinct from byte-ceiling refusal. |
| `InitializerFailureCount` | Initializers that throw or do not write the required range; no publication or native free is invented. |
| `ManagedBankBytes` | Allocated slot/page array element storage, including record padding; excludes CLR headers and outer owner size. Full managed allocation measurements must include those omitted costs. |
| `UnusedSlotBytes` | Exposed element capacity of reusable retained slots; excludes stride padding and does not represent additional backing. |
| `HistoryOverflowed` | An event count reached Int64 saturation; saturated values are lower bounds. Overflow cannot invalidate successful storage/slot transitions. |

Optional budget tracing emits full admission, actual `PageAcquired`, complete
`Prepared`, physical `Trimmed` and `Released` events. Per-page transitions carry
the real owner-local backing ordinal; aggregate preparation has no single backing
ordinal. Disabled tracing constructs no payload, and warmed reuse emits no
redundant budget event or budget-lock operation. Existing bounded/drop semantics
apply. The internal ordinal probe now reports actual physical acquisition order,
including ordinary independent slabs, rather than an always-empty array.
Lexical movement emits `Moved` only after publishing its new slot token. Its
correlation identity is that never-reused token, backing ordinal is the actual
page, and extent is the exact slot stride. It does not claim a physical allocation, free or budget transfer.
Disabled movement tracing performs no budget locking or event construction.

Fewer backend calls and zero warmed allocation are structural facts, not general
performance superiority. Required release measurements include preparation,
metadata, padding, dense/sparse occupancy, unequal lifetimes, diagnostics and
cleanup against equivalent optimized managed storage and the preserved NAM pool.
