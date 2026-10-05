# Application admission before production

Use this contract for a declared unmanaged range when producing a job that cannot
fit would waste useful work. It is not a scheduler, a process-RSS ceiling or an OS
allocation guarantee. Prepared pool/arena reuse already has local permission
checks; do not reserve fresh bytes again for an already charged slot.

```csharp
NativeMemoryBudget budget = new(4096);
if (!budget.TryReserve<int>(1024, out NativeMemoryReservation<int>? permission,
        out NativeMemoryAdmissionExhaustionReason reason))
    return; // NativeByteCapacity: no control, backing or producer was allocated.
try
{
    permission.Value.PrepareBacking(); // establish backing before the producer
    using NativeTransfer<int> owner = NativeMemoryReservation<int>.Activate(
        ref permission, static writer => writer.Fill(42));
    int result = owner.Read(static values => values[0]);
}
finally { permission?.Dispose(); }
```

Invalid dimensions and arithmetic overflow throw before admission. Expected
native-byte exhaustion is the only false result; its nullable output is null.
Successful admission checks the cap and prepares the optional domain ledger and
one ownership control before publishing permission. Metadata preparation failure
throws with its own real counter, without an invented byte reservation or native
allocation. Zero-length permission still needs metadata and deterministic cleanup.

The nullable ref binding is the actual source, not an invitation to alias it.
Move consumes that binding, invalidates stale value copies and advances a checked,
non-reusable version. It allocates no wrapper and creates no second byte charge.
Activation returns a unique owner over the very same control. Reservation moves
and unique moves have separate real histories; normal unique owners have no
reservation-specific fields.

PrepareBacking runs exclusively before production. A backend failure preserves
the pending permission for retry/cancellation. A successful native acquisition
remains actually counted and charged even if a later observation throws. A
prepared activation performs no native allocation and creates no second control.
Cancellation checked after preparation can leave prepared backing in a still
owning permission; dispose it or activate only when cancellation permits.

Activation consumes a current permission after validating its initializer
argument. It checks cancellation, prepares any remaining backing, initializes
exactly the declared range, and checks cancellation again before unique
publication. Backend failure, partial initialization, a throwing producer and
cancellation publish no unique owner. Failed terminal cleanup remains charged and
retryable through metadata-only TryCompletePayloadReturn or eventual emergency
finalization. Neither retry nor snapshot reopens permission. Emergency cleanup
is not timely admission relief. Unique cancellation/failed handoff still requires
returning the moved destination, just as ordinary unique ownership does.

CaptureAdmissionStatistics is consistent within the native budget gate. Its
outstanding count includes pending permissions and consumed terminal obligations,
including zero-length metadata. PendingBytes is uncommitted admission;
PreparedUnpublishedBytes is real prepared extent whose obligation has not
activated or completed cleanup. Activation removes only those reservation gauges,
not the unique owner's physical charge. Cancellation of pending bytes is not a
backend failure or physical free. Domain histories separate rejection, control
preparation, backing preparation/backend failure, initialization, activation,
cancellation, abandonment and resource-return failure. Current gauges are exact;
histories saturate with HistoryOverflowed. A never-retained optional ledger has
genuine zero storage and no ledger events; refusal/preparation-failure counters
remain actual even in that case.

CaptureSnapshot is a sampled control observation, not an atomic transaction
across preparation and the domain. Versions, disposition, pending/owned/peak
extents, preparation failures, return obligation and failures remain visible on
stale/released non-default values. Default values reject observation. Field-byte
measurements sum declared representations, excluding CLR headers, inter-field
padding, referenced objects and application queue/scheduling storage.

This additional cold admission, metadata and cleanup work must have a measured
end-to-end justification against previous NAM and an optimized managed byte-quota
and pooling implementation with the same outputs, cap and retention. No benchmark
superiority or release acceptance follows from correctness or zero-allocation
movement alone.
