# Common-lifetime typed layouts

Use a layout when several unmanaged regions genuinely share allocation, movement
and release. A reusable `NativeLayout` prepares type, length, actual CLR alignment
and checked offsets once. Each owner has one admitted backing block and one
acquisition-time control, not an owner or reference count per field. Unrelated
lifetimes should not be grouped: a surviving field retains the whole block.

```csharp
NativeLayoutBuilder builder = new(2);
NativeLayoutField<int> ids = builder.Add<int>(64);
NativeLayoutField<float> weights = builder.Add<float>(64, alignment: 16);
NativeLayout layout = builder.Build();
NativeMemoryBudget budget = new(layout.BackingBytes);
if (!layout.TryReserve(budget, out NativeLayoutReservation? permission, out _))
    return;
try
{
    permission.Value.PrepareBacking();
    using NativeLayoutOwner owner = NativeLayoutReservation.Activate(
        ref permission, (ids, weights), static (writer, fields) =>
        {
            writer.Region(fields.ids).Fill(1);
            writer.Region(fields.weights).Fill(0.5f);
        });
    float sum = owner.Read(weights, static (view, field) =>
    {
        float total = 0;
        foreach (float value in view.Region(field)) total += value;
        return total;
    });
}
finally { permission?.Dispose(); }
```

The builder prepares at most 64 regions and cannot change after `Build`. Region
tokens are immutable descriptor identities and ordinals, not addresses or owners.
Default, foreign and wrong-type tokens fail before payload access. `Describe`
reports the actual shape without native authority. An empty shape still has real
ownership metadata but no invented native allocation or free.

Backing admission includes inter-region padding and at most `Alignment - 1`
bytes of base-alignment slack. Payload alignment is established inside the
ordinary allocated block; this assumes neither allocator alignment nor a new
free API. Natural alignment comes from the runtime's sequential generic field
offset. Explicit alignment must be a power of two at least that alignment, at
most 64 bytes. Extent and slack arithmetic is checked before shape mutation.
This is a NAM requested-extent bound, not a CLR-header, backend-header or RSS cap.

`PrepareBacking` acquires storage before production. Capacity refusal allocates
nothing; metadata/backend errors throw with distinct real counters. Activation
consumes permission, initializes every region, clears only non-payload ranges,
and checks cancellation before publishing a unique owner over that same control.
Missing initialization, cancellation and failed cleanup cannot expose a partial
owner. Failed consumed cleanup stays charged and can retry without resurrection.
An invalid initializer argument does not consume permission. Emergency cleanup
is a safety net, never timely admission relief.

`Move(ref nullableSource)` consumes its source and invalidates aliases without
allocating. Static explicit-state callbacks enter one owner borrow. A typed field
is checked once before obtaining its span; there is no per-element ownership or
reference-count check. Views and initialization writers must not escape callbacks
or enter unknown unscoped forwarding. A field cannot independently dispose.

`DetachField(field, destinationBudget)` explicitly copies into `NativeTransfer<T>`.
It admits temporary overlap before copying, preserves the source on refusal or
failure, and publishes only initialized destination ownership. It never silently
changes retention. Actual copied bytes include copies followed by a later failed
publication; successful detached-owner count does not.

Snapshots separate logical typed bytes, layout padding, alignment slack, complete
backing, owner/reservation authority and real copy histories. Descriptor/control
field-byte measurements exclude CLR headers, inter-field padding and unrelated
objects. Snapshots are sampled observations, not cross-owner atomic transactions
or permission to access stale ownership. Optional preparation/initialization and
detach traces have real owner/descriptor correlation. Disabled tracing does not
disable accounting or construct event payloads.

This feature must justify its preparation, metadata, padding and coordination
costs against previous NAM and a compact expert-managed layout under the same
output, memory bound, retention and concurrency contract. Fewer handles or zero
GC allocation alone is not a reason to accept native-memory risk.
