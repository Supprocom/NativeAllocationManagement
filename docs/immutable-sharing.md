# Immutable native sharing (0.3.0 work in progress)

NAM remains an optimization tool with explicit memory and lifetime risks, not a
replacement for ordinary managed ownership. Use `NativeTransfer<T>` as the
default heap-storable owner. Immutable sharing is justified only when an actual
fan-out workload avoids more copying or lifetime coordination than its prepared
metadata, boundary synchronization, and cleanup cost. Correctness and zero
managed allocation alone do not establish that justification. The complete
release requires equivalent managed and previous-NAM end-to-end evidence.

## Preparation and ownership

`NativeShared<T>.Create(ref NativeTransfer<T>? source, preparation)` first
allocates fixed strong/weak binding banks, control, and payload metadata, then
destructively consumes the source. The initial owner occupies one strong slot. With zero weak capacity, no payload
weak-reference wrapper or private weak bank is allocated.
Invalid preparation or metadata failure before the move preserves the source.
Failure during or after destructive movement consumes it; initialization and
publication failures return its storage. If publication and cleanup both fail,
an aggregate exception preserves both causes. The ordinary unique path acquires
no reference-counting bank or shared payload wrapper.

After conversion, the original transfer lineage reports `Lifecycle.Shared`, no
active unique binding and `LiveUniqueOwnerCount == 0`. Its remaining return
obligation describes private shared cleanup custody, not access authority. The
shared payload stores that custody directly rather than retaining a public unique
capability. Its allocator generation/allocation pin still protects backing until
the last strong owner and entered shared reader finish. Preparing this pin does
not invent a unique borrow or increase the unique borrow peak; earlier genuine
unique borrows keep their recorded peak. Lineage backing/initialization fields and
shared snapshots describe the same storage and must not be added together.

If storage return completes before an observation/trace step throws, report the
actual completed return and zero remaining associated backing, preserve the
exception, and do not schedule another physical free. A failed return before
completion retains shared cleanup custody and can be retried without reopening
unique authority. These are correctness contracts, not measured performance
claims. Full end-to-end comparisons still include preparation, metadata, boundary
synchronization, cleanup and diagnostics against optimized managed C#.

Keep the nullable source in a `try/finally` with `source?.Dispose()` around the
conversion and its result. That cleanup is a no-op after a consumed move, but
returns the still-owned source if preparation fails before consumption. A using
builder does not retain return authority after `Complete`.

Every independent owner must be obtained explicitly through `TryShare`,
`TrySlice`, or a weak `TryUpgrade`. Assignment only aliases one versioned release
identity. An alias cannot release a binding twice or affect a reused slot.
`TryDowngrade` acquires an independent observer. Both banks are fixed for the
payload lifetime: reuse increments a non-wrapping version, and identity exhaustion
throws before changing a slot. Valid slot exhaustion returns false, a default
output, and a specific `NativeSharingExhaustionReason`; it does not allocate or
silently grow. Invalid/stale bindings throw instead of masquerading as capacity
exhaustion. Prepare enough slots for all simultaneous owners and observers.

No implicit sharing, automatic copy-on-write, mutable shared span, global owner
registry, or cycle collector is introduced. `T` must be unmanaged. Read callbacks
are synchronous and receive only `NativeReadOnlyLeaseView<T>` and its read-only
span. Admission is at callback entry/exit, not at each element. A result cannot be
a ref struct. The analyzer also rejects supported source-visible view escapes;
unsafe consumers still have explicit obligations and are not memory-safe merely
because a view is read-only.

## Payload and weak-state lifetimes

The last strong release closes upgrades permanently, even while an already
entered read finishes. It does not free storage underneath that reader. Last
strong/last read arbitrate one payload return. A pooled transfer holds actual
allocation/generation admission until return, so pool disposal or generation
reuse cannot invalidate a live shared read. Returning the payload releases that
admission and returns the allocation's actual slot. A direct transfer physically
frees its backing. The original budget charge is carried, never charged again by
sharing.

Weak values contain only bounded control state, including weak references; they
contain no strong payload, transfer, budget, or allocator reference. Lingering
observers cannot prevent payload cleanup or pooled-slot reuse. The expired strong bank is dropped immediately, rather than retaining its
prepared capacity for weak observation. The weak bank is dropped when its final
observer releases after expiration. Remaining control metadata
becomes collectible when all actual CLR references end; disposing a value does
not erase copies of that value held by its caller. Stale strong values retain no
native return obligation after successful cleanup. Finalizable payload cleanup
assumes the consumed unique control's emergency-finalization obligation, avoiding
competing finalizers. Failed emergency return is retried at a later collection,
not spun or treated as timely memory-budget relief.

A failed return does not receive success credit. Ownership remains expired and
storage remains charged. `TryCompletePayloadReturn` on a surviving shared or weak
control retries cleanup without acquiring or reopening ownership. It returns true
when return has completed and false while a strong binding, entered reader, or
another return attempt prevents completion. Cleanup errors throw.
If an abandoned payload's short weak reference has already been cleared, the
weak observer also returns false while emergency finalization owns that payload;
it does not resurrect or strongly retain the payload merely to retry it.
A callback and last-return failure preserve both causes. Emergency cleanup never replaces
required deterministic disposal.

## Slices and explicit detach

A small shared slice still retains the entire backing extent. Its logical
`Length` is not its memory charge. `TryDetach(destinationBudget, out transfer)`
admits the initialized slice's complete destination extent before native
acquisition or copying, including the temporary overlap if both owners use one
budget. False is budget rejection before acquisition/copy. Other failures throw,
cancel admission or return any destination, and preserve the source. Only a fully
initialized independent unique owner is published. Completed copying is counted
once. Empty detach has no fabricated backend allocation or copied-byte event.

## Actual diagnostics

`CaptureSnapshot` is one locked control observation, with no native authority.
It remains available after release on a non-default value. `Id` identifies the
non-reusable payload control; `OwnerId` identifies its original backing lineage.
Current strong/weak/read counts come from real bank occupancy/admission. Peaks
come from successful publication. Refusals distinguish strong slots, weak slots,
expiration, and detach admission. Histories count only completed transitions and
saturate with `HistoryOverflowed`; current gauges stay exact.

`OwnedBackingBytes`/`BorrowedBackingBytes` describe the full pinned extent once
per payload control, not per slice. Distinct payloads sharing a pooled page may
report the same backing extent: summing these observations is not a domain byte
total. Use the original `NativeMemoryBudget` for aggregate charge.
`InitializedPayloadBytes` is the logical initialized range counted once.
`ManagedBankBytes` is the actual binding-array element storage, explicitly
excluding CLR headers and control/allocator objects; it is not a fabricated
measurement of the complete managed heap. Release zeros retained payload gauges,
not history or surviving observer occupancy.

`PayloadReturnCount` records completed payload-authority returns, including
reusable pooled slots and empty payloads; it is not physical backend free count.
`PayloadReturnFailureCount` records failed attempts without crediting success.
The source budget's actual free/active/committed counters remain authoritative
for physical extents. Optional bounded trace events distinguish share, weak
creation, upgrade/rejection, binding release, payload return, and detach. Their
`CorrelationId` is the shared control, not a physical allocation ordinal. Disabled
tracing creates no trace event or budget weak-reference wrapper.

Every output of a Try acquisition must be used only on a branch proving success,
and every successful independent binding needs its own deterministic cleanup.
The bundled analyzer authenticates types against the runtime assembly, rejects
assignment/boxing/storage aliases, tracks destructive conversion, and checks
guarded outputs and read-only escape boundaries. Runtime version and lifetime
guards remain effective when analyzer diagnostics are suppressed.

The maintained [sharing field contract](../conformance/native-sharing-diagnostic-contracts.json)
defines all 26 properties, their units, scope, mutation sources, lifetime and
overflow behavior. The public-only independent oracle compares every field
through share/slice/observer acquisition and exhaustion, weak
upgrade, rejected and successful detach, payload return, expiration and final
bank release. It also runs in an isolated package consumer with bundled ownership
analysis enabled, in traced and untraced configurations. Its callbacks are static;
it does not capture owning values to inspect an entered read. Independent local
fault, nested-read admission/drain and real provider-storage cases verify complete
snapshots at those boundaries without weakening the package's capture rule.
These proofs do not constitute performance acceptance or a complete release inventory.
