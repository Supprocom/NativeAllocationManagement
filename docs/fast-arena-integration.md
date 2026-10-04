# Fast mapped arena and grouped operations

Use the thread-confined arena when one construction thread owns a pipeline.
Mapped preparation declares separate ordinary and scoped bounds at once:

```csharp
using NativeArena arena = new(buffer, byteOffset: 0,
    new NativeArenaPreparation(ordinaryBytes: 64, scopedBytes: 128), budget);
```

The `SafeBuffer` must expose the complete declared range. Its start and the
scoped-lane start must be 64-byte aligned; a nonempty scoped lane therefore
requires an ordinary bound divisible by 64. Invalid ranges and native address
overflow fail before publication. Zero total capacity requires no provider hold
or native header allocation. This API does not authenticate an arbitrary
provider's implementation of the SafeBuffer contract.

NAM takes one safe-handle hold and allocates one aligned header block outside
the mapped range. No NAM header overwrites mapped data. The budget charges that
block, not provider-owned mapped payload. Account for the provider's allocation,
alignment and control costs separately when imposing an application memory cap.
Disposing the provider does not free a range still held by the arena. Deterministic
arena disposal releases both headers and the provider hold exactly once.

Both lanes are prepared: scratch cannot grow or append another mapping. Existing
TryScratch and TryScratchScoped return false before running an initializer when
their lane cannot fit. Reset reuses both lanes; scoped recycle reuses only the
scoped lane. Old capabilities retain the existing thread and epoch guards.

The four/eight-region `NativeLeaseOperations.InitializeScoped` overloads reserve
under one owner and invoke one span initializer. Returning from that initializer
declares all spans completely initialized, matching the existing unmanaged span
initializer contract. An exception or capacity failure rewinds the whole scoped
checkpoint and leaves outputs default; earlier ordinary leases remain valid.
Reservation failure is an error on these throwing convenience overloads.
TryInitializeScoped provides the same four/eight arities for prepared owners:
false means capacity exhaustion before invoking the initializer. It rewinds all
reservations and leaves every output default. Invalid authority, invalid lengths,
an unprepared owner and producer failure remain exceptions, not false-full
results. Prove success directly in the conditional before using any output;
negated early return and named/short-circuit guards retain that proof. Scoped
recycling still requires the complete lexical batch to end.

Fast Access overloads cover two, three, four, five, seven and eight arena leases.
All must belong to the same owner. One callback admission validates every epoch
before exposing any view. Stale, default and different-owner inputs fail without
exposing a partial view or leaving an entered borrow. Reset, recycle, trim and
disposal cannot cross that callback; nested initialization cannot cross a group
initializer. End the complete lexical scoped batch before recycling, including
on exceptional paths. The bundled analyzer authenticates the actual runtime
symbols and checks all participating inputs and out-declared group bindings.

`CapturePreparedSnapshot` reports ActiveBorrowedBytes (addressable lane capacity)
and RetainedBorrowedBytes (the full external range still held), separately from
RetainedBytes (NAM-owned native headers). Whole-idle-lane trim never refills.
Trimming one lane can remove authority while freeing zero physical header bytes:
its peer still holds the shared block and full provider range. The last lane
releases both. GetStatistics.BorrowedBytes reports that real retained range;
SegmentCount counts actual lane segments, whereas AllocationCount counts the
single NAM header backend acquisition. No mapped backend event is fabricated.

The real voxel worker uses this constructor and the fast group operations. Its
capacity plan includes the header block and its mapped provider's alignment
overhead. This selection is not a superiority claim: final old-NAM/current-NAM
and optimized managed comparisons must include preparation, mapping, metadata,
retention, output verification and teardown under matched compilation settings.
