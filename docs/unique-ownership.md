# Unique native ownership (0.3.0 work in progress)

`NativeTransfer<T>` is NAM's unique smart-pointer family for an initialized
unmanaged range. It is not a wrapper around another published smart-pointer
family. Acquire one acquisition-time control, then use the two-field value
capability for destructive movement without another allocation. Assignment does
not acquire ownership. Old value aliases retain their version and cannot borrow,
move or dispose a newer destination.

Prefer ordinary managed ownership unless the complete workload demonstrates a
material memory or performance advantage under the same cap and output contract.
Zero allocations and the presence of a native pointer are insufficient. NAM
requires deterministic cleanup, runtime guards and explicit lifetime reasoning.

## Movement, borrowing and cleanup

`Move(ref NativeTransfer<T>? source)` clears the nullable source before attempting
movement. Failure also consumes that source. A move during an entered borrow
retires the source and returns its payload after the last entered borrow, without
invalidating that borrow. An ordinary `Dispose` during a borrow rejects before
returning storage and preserves the current binding. Bounded access is synchronous;
an escaping span or native pointer has no ownership authority.

A storage-return failure does not receive successful-return credit or free its
budget charge. Failed ordinary disposal restores the current binding so its
receiver can call `Dispose` again. A failed consumed retirement remains terminal
and cannot borrow again; `TryCompletePayloadReturn` can retry that cleanup through
any surviving non-default control observation without reopening ownership.
False means it is not an idle consumed terminal source or another attempt owns
cleanup. It is not a way to consume a still-active or allocator-invalidated value:
the current binding must dispose that value normally. True means the return
obligation has genuinely ended. A cleanup failure throws.

An abandoned control has emergency finalization. Failure preserves its cleanup
obligation for a later collection, never a tight retry loop or timely admission
relief. Once cleanup succeeds, the control clears references to the old allocator,
generation, allocation record and direct block. A stale alias can still observe
scalar lineage/history but need not retain detached payload storage. A diagnostic
failure after successful return cannot reopen native access or free storage twice.

Allocator-wide return can independently invalidate a pooled unique handle. That
handle's CLR control may still require disposal even though no live payload
authority remains. GC-owned detached backing can stay physically charged until
actual finalization. An entered borrow survives the supported GC-detach boundary;
it does not establish fresh authority for another read.

## Real observations

`CaptureSnapshot` works on constructed stale/released values and allocates
nothing. A default capability throws. Fields are sampled during concurrent
transitions, not an atomic transaction across the control and its allocator.

`OwnerId` plus `AllocationId` identifies the acquisition lineage, never a raw
address. Binding and authority versions distinguish a stale alias from the
current unique value. `BindingIsActive` checks actual allocator authority as well
as control/version state. `LiveUniqueOwnerCount` is zero or one for the published
live unique binding, not the number of copies. `HasReturnObligation` separately
reports incomplete control cleanup, including invalidated or retiring payloads.

Active borrows come from real operation admission. Peak borrows are recorded from
the existing atomic entry result, not reconstructed from a later snapshot.
`MoveCount` is exactly the authority-publication version minus its initial value:
that non-wrapping version changes only on a published move. Initial logical
length and associated backing extent are immutable after acquisition and supply
the actual lifetime peaks. A pooled acquisition can share a larger physical
segment with other acquisitions; do not sum their full-backing observations as
independent domain allocations. Borrowed provider backing is separate.

`PayloadReturnCount` is zero or one after completed control return, including
empty payloads and reusable pooled storage. It is not backend free count.
`PayloadReturnFailureCount` counts failed real attempts, excluding invalid use
rejected before an attempt and diagnostic failures after successful return. Its
history saturates with `HistoryOverflowed`; current authority/borrow gauges remain
exact. Native budget and allocator snapshots establish physical charge and free.

`ControlFieldBytes` sums actual declared field representations, including inline
block storage and reference slots. It excludes CLR headers, padding between
fields and referenced objects. It is not a fabricated heap-size or total-process
memory measurement. A structural test reconciles it with every actual field.

Optional bounded events distinguish `Moved`, `Retired` and `UniqueReturned` from
physical allocation/free and shared-control events. Unique `CorrelationId` is
the allocation lineage paired with `OwnerId`; it is not a raw address. Disabled
tracing constructs no event payload. Required observations remain available.

The bundled analyzer retains constructed-control proof separately from current
authority. Post-cleanup observation therefore does not grant read/move/dispose
authority. Default assignment, an unsuccessful Try acquisition and a consumed
nullable ref source do not inherit that proof. Runtime guards remain mandatory
when analyzer warnings are suppressed.
