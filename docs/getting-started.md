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
  <PackageReference Include="Supprocom.NativeAllocationManagement" Version="0.1.3" />
</ItemGroup>
```

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
