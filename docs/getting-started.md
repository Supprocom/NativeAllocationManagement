# Getting started

`Supprocom.NativeAllocationManagement` combines a native-memory runtime with a
bundled Roslyn analyzer. The runtime checks owner state, generation identity,
allocation identity, and the active-operation gate whenever storage is touched. The
analyzer proves lexical ownership, generation transitions, bounded callbacks, and
scoped-recycling completion in the consuming source.

The package targets .NET 10 and supports unmanaged values, reference values, and value
types that contain references through the same generic owner and handle model. Add a
normal package reference and keep its analyzer asset enabled.

```xml
<ItemGroup>
  <PackageReference Include="Supprocom.NativeAllocationManagement" Version="0.1.2" />
</ItemGroup>
```

## Build a growable native sequence

`NativeBuilder<T>` owns one unpublished unmanaged sequence. The builder supports one
writer and remains in one method-local variable.

Create the builder from `NativePool<T>` when output has one known element type. Create it
from `NativeArena` when the output belongs to a heterogeneous arena lifecycle.

```csharp
using Supprocom.NativeAllocationManagement;

using NativePool<uint> pool = new(initialCapacity: 1_024);
using NativeBuilder<uint> builder = pool.CreateBuilder(
    initialCapacity: 64);
Span<uint> batch = stackalloc uint[64];

for (int offset = 0; offset < 4_096; offset += batch.Length)
{
    for (int index = 0; index < batch.Length; index++)
    {
        batch[index] = checked((uint)(offset + index));
    }

    builder.Append(batch);
}

NativeTransfer<uint> output = builder.Complete();
try
{
    output.Access(static view => Upload(view.AsSpan()));
}
finally
{
    output.Dispose();
}
```

`Append(T)` writes one value directly. `Append(ReadOnlySpan<T>)` copies one batch directly
into the unused native range.

Growth reserves a geometric native allocation. It copies only the initialized native
prefix and returns the prior pool slab for reuse.

An arena cannot reclaim an earlier bump range during growth. The arena lifecycle reclaims
that range with its containing segment.

`Complete` changes the current allocation from unpublished initialization to active
transfer ownership. It does not copy elements into another final buffer.

The published transfer has the exact initialized `Length`. Its `Capacity` can remain
larger because geometric growth retains the current allocation.

Completion invalidates all builder operations. The automatic disposal from a `using`
declaration is a valid no-op after completion.

Cancellation before or after an append aborts the complete unpublished builder. An
allocation failure uses the same cleanup path.

Explicit disposal is idempotent. The session returns its current allocation and exits its
generation protection only once.

A live builder blocks owner disposal under both return policies. This rule prevents an
owner transition from invalidating unpublished initialization.

An abandoned active builder has an emergency finalizer. The finalizer returns storage but
does not provide prompt reuse.

The analyzer requires direct `CreateBuilder` initialization into one local. It rejects
fields, properties, parameters, returns, arguments, aggregates, conversions, and closures.

`NAM1028` rejects ownership copies. `NAM1029` rejects use after completion or disposal.
`NAM1030` rejects double completion.

`NAM1031` requires completion or disposal on each exit. `NAM1032` requires direct local
acquisition, and `NAM1033` rejects builder parameters.

`NAM1034` requires `Complete` to publish directly to an exact `NativeTransfer<T>`
destination. Existing typed return, owned receiver, field, and bounded-channel rules apply.

Do not use the builder as cross-method ownership. Complete it and move or store the
resulting transfer instead.

## Transfer ownership across threads

`NativeTransfer<T>` stores one initialized unmanaged lease on the managed heap. The
object can cross a thread boundary without exposing an unbounded pointer.

Acquire the lease into a local variable. Then move that local into its next ownership
location.

```csharp
using System.Threading.Channels;
using Supprocom.NativeAllocationManagement;

Channel<NativeTransfer<uint>> channel =
    Channel.CreateBounded<NativeTransfer<uint>>(1);
using NativePool<uint> pool = new(initialCapacity: 256);

NativeTransfer<uint>? source = pool.RentTransferable(
    256,
    static writer =>
    {
        for (int index = 0; index < writer.Length; index++)
        {
            writer.Write(checked((uint)(index * 3)));
        }
    });

await channel.Writer.WriteAsync(
    NativeTransfer<uint>.Move(ref source));

NativeTransfer<uint> receiver = await channel.Reader.ReadAsync();
try
{
    uint sum = receiver.Read(
        static view =>
        {
            uint total = 0;
            foreach (uint value in view.AsSpan())
            {
                total = unchecked(total + value);
            }

            return total;
        });

    Console.WriteLine(sum);
}
finally
{
    receiver.Dispose();
}
```

The initializer must write all logical elements in order. NAM returns the unpublished
storage if initialization fails or remains incomplete.

`Move(ref source)` sets the source variable to `null` before it publishes the
destination. Old aliases fail at runtime even when analyzer diagnostics are disabled.

Only one concurrent move can publish a destination. A move that finds an active
callback consumes the source and returns its storage after that callback exits.

Run cancellation checks before the move when possible. After the move, the destination
owns cleanup and must be disposed if the next operation rejects it.

If no code retains the rejected destination, its finalizer returns the lease later.
Finalization prevents a permanent leak, but it does not give prompt reuse.

An exception from `Access` or `Read` releases the operation token. The destination stays
active and still requires disposal.

An owner can dispose while a live transfer is idle. This action invalidates the
transfer, and the receiver must still dispose its transfer object.

An entered receiver callback blocks strict owner disposal. Retry owner disposal after
the callback exits.

`NativeArena.ScratchTransferable<T>` supports the same move contract for heterogeneous
arena storage. A transfer can use external storage that the arena accepted through
`ReserveExternalMemory`.

`NAM1021` rejects ownership copies. `NAM1022` rejects inactive use and double disposal.
`NAM1023` rejects invalid moves.

`NAM1024` prevents a callback view from escaping. `NAM1025` requires disposal or a move
on each exit. `NAM1026` requires direct acquisition into a local source. `NAM1027`
rejects transfer parameters with `in`, `ref`, or `out`.

An ordinary `NativeTransfer<T>` parameter is an owned receiver. Each receiver path must
dispose the transfer or move it to the next owner. Call the receiver with
`NativeTransfer<T>.Move(ref source)`. The immediate destination must have the exact
`NativeTransfer<T>` type. A typed field, proven bounded typed channel, or direct typed
return can receive ownership. An `object`, `dynamic`, generic `T`, task, tuple, array, or
other aggregate cannot.

Use `WriteAsync` on a direct `Channel.CreateBounded` result or an unreassigned local from
that factory. The analyzer rejects `CreateUnbounded` and any channel local that is
reassigned. This proof keeps the generic `ChannelWriter<T>` parameter from becoming broad
ownership authority. Do not move directly into `TryWrite`. A failed call keeps ownership
with its caller.

Do not use application `in`, `ref`, or `out NativeTransfer<T>` parameters. Borrow only
inside an `Access` or `Read` callback. Use `ref` only in the package move operation.

## Typed pool leases

`NativePool<T>` owns reusable typed slabs. `Rent` returns a generation-bound
`Pooled<T>` value, and the using declarations below return the lease and then dispose
the owner in the normal C# order.

```csharp
using Supprocom.NativeAllocationManagement;

using NativePool<int> pool = new(initialCapacity: 1_024);
using Pooled<int> values = pool.Rent(128);

values.Access(view =>
{
    for (int index = 0; index < view.Length; index++)
    {
        view[index] = index * 2;
    }
});

int total = values.Read(view =>
{
    int result = 0;
    for (int index = 0; index < view.Length; index++)
    {
        result += view[index];
    }

    return result;
});
```

`Access` and `Read` pass a scoped `NativeLeaseView<T>` only for the synchronous
callback. Indexing, `Clear`, `Fill`, `CopyFrom`, and `CopyTo` use the same runtime
operation gate. `Pooled<T>.Dispose()` clears one logical lease before returning its slab
to the idle bank. A zero-length lease has generation and allocation identity even though
it owns no native bytes.

## Heterogeneous regions

`NativeRegion` is a one-shot heterogeneous lexical owner. Its only accepted ownership
shape is an explicit braced using statement whose direct resource is the region. The
analyzer rejects using declarations, ordinary locals, factories, aliases, parameters,
fields, unbraced forms, and nested active regions.

```csharp
using Supprocom.NativeAllocationManagement;

using (NativeRegion region = new(
    preAllocateBytes: 4_096,
    returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory))
{
    Local<int> identifiers = region.Lease<int>(64);
    Local<double> weights = region.Lease<double>(64);

    identifiers.Access(view =>
    {
        for (int index = 0; index < view.Length; index++)
        {
            view[index] = index + 1;
        }
    });

    weights.Access(view => view.Fill(0.5));
    double firstWeight = weights.Read(view => view[0]);
    identifiers[0] = checked((int)(firstWeight * 100));
}
```

Region locals have no individual physical return. Leaving the braced body invalidates
the complete generation, so a `Local<T>` cannot be returned, stored, or passed to an
unknown retaining call.

## Reusable heterogeneous arenas

`NativeArena` is a reusable heterogeneous owner for values that should become stale at
one explicit generation boundary. It may be a local, a using-owned object, or a field.
`Scratch<T>` and `ScratchScoped<T>` are its only acquisition methods; `ArenaLease<T>`
has no individual disposal because arena storage is reclaimed as a group.

```csharp
using Supprocom.NativeAllocationManagement;

using NativeArena arena = new(preAllocateBytes: 64 * 1024);
ArenaLease<int> coordinates = arena.Scratch<int>(1_024);
ArenaLease<string> labels = arena.Scratch<string>(32);

coordinates[0] = 7;
labels[0] = "ready";
arena.ReleaseLeasesToNativeMemory();
```

The arena has one two-ended segment bank. Ordinary scratch values grow from the low end
and scoped scratch values grow from the high end. `ReleaseLeasesToNativeMemory()` or
`ReleaseLeasesToGarbageCollector()` invalidates every current lease, advances the
generation once, and leaves the arena active. Idle segments are reused by the next
generation; an entered operation on an old generation keeps its retired segment alive
until the operation exits.

Typed pools are preferred when repeated element types and lease shapes are known, and a
region is preferred when heterogeneous values share one braced lexical lifetime. Use an
arena only for a genuinely heterogeneous reusable bulk lifetime. Its one shared budget
and operation gate mean that a capacity spike in one type can retain space for every
type. Interior fragmentation cannot be compacted or combined, and NAM does not infer
managed reachability, move live values, or provide size classes. The developer remains
responsible for explicit scratch-recycle, generation-release, trim, growth, and final
return boundaries; an arena is not a reachability-based replacement for managed
allocation or a predictable typed pool.

## Delayed activation

Construction normally publishes an active generation. Passing
`doNotLeaseOnDeclaration: true` makes construction allocation-free and publishes the
`Unleased` lifecycle instead. The configured initial capacity remains private until
`LeaseFromMemory()` succeeds.

```csharp
using Supprocom.NativeAllocationManagement;

using NativePool<byte> pool = new(
    initialCapacity: 4_096,
    doNotLeaseOnDeclaration: true);

pool.LeaseFromMemory();
using Pooled<byte> buffer = pool.Rent(4_096);
buffer.Access(view => view.Fill(0x2A));
```

The same form applies to an arena and to the required braced region statement.
`Rent`, `Lease`, `Scratch`, both memory-return operations, both lease-release operations,
and `RecycleScoped` reject an unleased owner. Disposal before activation is valid,
terminal, and allocation-free. Activation prepares any initial reservation privately;
if it fails, the owner remains unleased and no partial generation is published.

## Generations and cleanup policies

Memory return ends the current generation and leaves the owner returned. A later
`LeaseFromMemory()` creates the next pool or arena generation; a region remains terminal
after a memory return because its lexical owner is one-shot. Lease release is different:
it invalidates all current pool or arena leases, retains reusable storage, advances the
generation, and leaves the owner active.

```csharp
using Supprocom.NativeAllocationManagement;

NativePool<int> pool = new(
    initialCapacity: 256,
    returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

try
{
    Pooled<int> first = pool.Rent(64);
    first.Access(view => view.Fill(1));
    first.Dispose();

    pool.ReleaseLeasesToNativeMemory();
    Pooled<int> second = pool.Rent(64);
    second.Access(view => view.Fill(2));
    second.Dispose();

    pool.ReturnMemoryToNativeMemory();
    pool.LeaseFromMemory();
}
finally
{
    pool.Dispose();
}
```

`ReturnMemoryToNativeMemory()` frees the current segments synchronously after the
operation gate succeeds. `ReturnMemoryToGarbageCollector()` detaches the current
generation to a finalizable owner without forcing collection. The old handles are stale
as soon as either operation succeeds, and a later generation never revives them.

The analyzer uses one liveness query for both policies. A live root, active bounded
callback, alias, escape, or unknown-retention path produces `NAM1007` error for native
return or strict lease release and ordinary warning `NAM1017` for garbage-collector
return or tolerant lease release. The finding path is equivalent across the pair. A
plain stale root is a source-liveness fact; an entered operation token is the runtime
object that retains detached native storage.

## Scoped recycling

Scoped recycling uses the C# `scoped` local plus the matching owner acquisition. The
only public completion operation is parameterless `RecycleScoped()`.

```csharp
using Supprocom.NativeAllocationManagement;

using NativeArena arena = new();

while (ShouldContinue())
{
    {
        scoped ArenaLease<int> scratch =
            arena.ScratchScoped<int>(4_096);
        Process(scratch);
    }

    arena.RecycleScoped();
}
```

`LeaseScoped` and `ScratchScoped` must directly initialize a `scoped` local. The
analyzer reports `NAM1018` for an escape, warning `NAM1019` when an ordinary acquisition
is unnecessarily placed in a scoped local, and warning `NAM1020` when a pending scoped
set is not completed on every path. Early return and exception paths put that same
`RecycleScoped()` call in an ordinary C# `finally`. The operation clears reference
roots, advances allocation epochs, and rewinds eligible high-water state while retaining
backing memory. Trimming cannot satisfy a scoped obligation.

## Owner statistics

`NativePool<T>`, `NativeRegion`, and `NativeArena` expose `GetStatistics()` for
diagnostics and capacity policy. The snapshot reports lifecycle and generation,
currently requested bytes, retained and retired physical bytes, current and retired
segment counts, cumulative trimming, and fresh upstream segment allocations.

```csharp
using Supprocom.NativeAllocationManagement;

using NativePool<int> pool = new(initialCapacity: 4_096);
using Pooled<int> values = pool.Rent(1_024);

NativeOwnerStatistics snapshot = pool.GetStatistics();
Console.WriteLine(
    $"requested={snapshot.RequestedBytes}, "
    + $"retained={snapshot.RetainedBytes}, "
    + $"segments={snapshot.SegmentCount}");
```

The operation is a truthful point-in-time diagnostic, not a free hot-path counter. It
takes the owner gate and derives requested and retained totals from the current owner
state. Capture it before or after measured processing, at a maintenance boundary, or
after a rare capacity event. Do not call it for every lease or inside an allocation
benchmark loop.

`NativeMemoryDiagnostics.Snapshot()` provides the corresponding process-wide physical
native counters. It is useful for proving that terminal cleanup returned to a known
baseline, while `GetStatistics()` explains which live owner retained a particular
capacity.

## Trimming and runtime fallback

`TrimRetainedMemory()` releases every idle storage unit. The byte and lease-shape forms
release whole idle units until their request is met, using the same sizing and alignment
rules as the real acquisition path. Trimming does not change lifecycle or generation
identity, and it never invalidates a live handle or discharges scoped storage.

The runtime repeats the critical stale-handle and active-operation checks even when a
consumer suppresses analyzer diagnostics or was compiled separately. Strict native
transitions refuse to free storage beneath an entered operation. Tolerant
garbage-collector transitions permit an entered old operation to drain while all later
old-handle operations fail. Structured exceptions report owner kind, generation,
operation, allocation identity, active-operation count, and observed lifecycle without
exposing addresses or payloads.
