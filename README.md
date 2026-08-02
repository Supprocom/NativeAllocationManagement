# Supprocom.NativeAllocationManagement

`Supprocom.NativeAllocationManagement` gives C# code explicit, generation-safe
ownership of native storage. `NativePool<T>` reuses typed slabs and individual
`Pooled<T>` leases. `NativeRegion` packs heterogeneous values into one lexical
boundary. `NativeArena` reuses heterogeneous generations when several stages share
one lifetime. The runtime validates owner state, generation identity, allocation
identity, and active operation gates; the bundled Roslyn analyzer proves the source
ownership rules before a consumer can build.

The public model is generic and supports both unmanaged values and reference-containing
values. A bounded `Access` or `Read` callback receives a scoped `NativeLeaseView<T>`;
the view cannot escape the callback, and the runtime operation token retains the exact
generation until the callback exits.

## Measured performance

The included voxel benchmark estimates a 50 to 85 percent performance
improvement over expert safe C# in non-memory-constrained control profiles.

In very memory-constrained profiles, the estimate ranges from 90 to 130
percent, with selected higher-turnover runs above 150 percent.

Both implementations process equal inputs and outputs. The safe C#
implementation uses pooling, exact sizing, bounded retention, and proactive
memory admission.

The matrix records constrained-memory qualification as an informational result.
The result confirms the equal binary cap, no swap, and cumulative demand.
It does not require a garbage collection or a resident-memory threshold.

These estimates are specific to the included workload and test system. The
[voxel pipeline guide][voxel-guide] gives the
method, commands, and current evidence.

[voxel-guide]: https://github.com/Supprocom/NativeAllocationManagement/blob/main/.Demos/01-VoxelChunkPipeline/README.md

Install the package with a normal package reference. Version `0.1.2` adds a growable
native builder that publishes one transferable lease.

```xml
<PackageReference Include="Supprocom.NativeAllocationManagement" Version="0.1.2" />
```

The package contains the runtime assembly, the ownership analyzer, and its
`buildTransitive` analyzer-presence check. Keep analyzer assets enabled in consuming
projects.

## Build growable native output

`NativeBuilder<T>` builds one unmanaged sequence directly in native storage. Create it
from a typed pool or a heterogeneous arena.

Append one value or one `ReadOnlySpan<T>`. Geometric growth copies the initialized prefix
between native allocations without a managed element array.

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
    uint last = output.Read(static view => view[^1]);
    Console.WriteLine(last);
}
finally
{
    output.Dispose();
}
```

`Complete` publishes the current allocation without a final element copy. The transfer
view has the exact initialized length, while its retained capacity can be larger.

Completion invalidates the builder. A later append, count read, capacity read, or second
completion fails closed.

Cancellation or an append failure aborts the unpublished builder. Disposal returns its
current storage, and repeated disposal does not return storage twice.

Owner disposal rejects a live builder under both memory-return policies. Complete or
dispose the builder before owner disposal.

An abandoned active builder has an emergency finalizer. This fallback prevents permanent
retention, but deterministic disposal remains the normal cleanup method.

Keep a builder in one local variable. Do not pass it, return it, store it, copy it, or
capture it.

Publish a `NativeTransfer<T>` when ownership must enter a field, return value, receiver,
or proven bounded channel. Diagnostics `NAM1028` through `NAM1034` enforce these rules.

An arena builder can use storage supplied through `ReserveExternalMemory`. Growth beyond
that mapped range follows the arena's normal segment policy.

The included builder benchmark compares the same batch generator in both paths. The
managed path uses `List<uint>` growth and `ToArray`.

The native path uses pool-backed builder growth and zero-copy completion. The harness
records paired confidence, allocations, external peak working set, throughput, and exact
output parity.

```powershell
dotnet run --project Supprocom.NativeAllocationManagement.Performance -c Release -- --native-builder --samples 10 --output native-builder.json
```

## Transferable cross-thread leases

`NativeTransfer<T>` is a heap-storable lease for unmanaged values. A pool initializes
the complete range before it publishes the lease.

`Move(ref source)` transfers ownership and sets `source` to `null`. Store only the
returned destination in an object field or a bounded channel.

```csharp
using System.Threading.Channels;
using Supprocom.NativeAllocationManagement;

Channel<NativeTransfer<uint>> uploads =
    Channel.CreateBounded<NativeTransfer<uint>>(1);
using NativePool<uint> pool = new(initialCapacity: 1_024);

NativeTransfer<uint>? source = pool.RentTransferable(
    1_024,
    static writer =>
    {
        for (int index = 0; index < writer.Length; index++)
        {
            writer.Write(checked((uint)(index + 1)));
        }
    });

await uploads.Writer.WriteAsync(
    NativeTransfer<uint>.Move(ref source));

NativeTransfer<uint> received = await uploads.Reader.ReadAsync();
try
{
    received.Access(static view => Consume(view.AsSpan()));
}
finally
{
    received.Dispose();
}
```

The receiver can use the lease on a different thread. `Access` and `Read` expose the
same bounded `NativeLeaseView<T>` as other NAM handles.

The source and all old aliases are invalid after a successful move. A second move,
source reuse, access after disposal, and double disposal fail closed.

Wait for channel capacity and process cancellation before the move when possible. The
destination owns cleanup after the move, including when later channel work fails.

A callback exception releases its operation token and leaves the destination active.
Dispose that destination in the normal cleanup path.

An abandoned destination has a finalizer that returns its lease. This finalizer is a
safety fallback and is not a deterministic cleanup method.

Owner disposal invalidates an idle live transfer. An active receiver callback blocks
owner disposal until that callback exits.

`NativeArena.ScratchTransferable<T>` gives the same ownership model to heterogeneous
arena storage. It also works with storage supplied through `ReserveExternalMemory`.

The bundled analyzer rejects copied ownership, inactive use, double moves, escaping
views, unfinished lifetimes, and direct acquisition into storage. These rules are
`NAM1021` through `NAM1027`.

An ordinary `NativeTransfer<T>` parameter receives ownership. The receiver must dispose
or move that ownership on every method exit. Pass only a move expression to the receiver.
The move destination must have the exact `NativeTransfer<T>` type. This rule permits a
typed field, a proven bounded typed channel, or a direct typed return. It rejects `object`,
`dynamic`, generic `T`, tasks, tuples, arrays, and other aggregate storage.

The analyzer accepts `WriteAsync` from a direct `Channel.CreateBounded` result. It also
accepts an unreassigned local that was initialized by `Channel.CreateBounded`. An unbounded
channel or reassigned channel cannot receive transfer ownership. `TryWrite` is rejected
because a failed call keeps ownership with its caller.

Application `in`, `ref`, and `out NativeTransfer<T>` parameters are invalid. Borrow only
inside an `Access` or `Read` callback. Keep `ref` for `NativeTransfer<T>.Move(ref source)`.

## Typed pools

Pool declarations are ordinary C# using declarations. The default constructor publishes
an active generation immediately. `doNotLeaseOnDeclaration: true` makes construction
allocation-free and requires an explicit `LeaseFromMemory()` before `Rent` or
`LeaseScoped` can succeed.

```csharp
using Supprocom.NativeAllocationManagement;

using NativePool<int> pool = new(initialCapacity: 1024);
using Pooled<int> values = pool.Rent(128);

values.Access(view =>
{
    for (int index = 0; index < view.Length; index++)
    {
        view[index] = index;
    }
});
```

`Pooled<T>.Dispose()` clears and returns one typed lease to the pool. A pool can also
end or roll its complete generation with `ReturnMemoryToNativeMemory()`,
`ReturnMemoryToGarbageCollector()`, `ReleaseLeasesToNativeMemory()`, or
`ReleaseLeasesToGarbageCollector()`. Memory return leaves the owner returned and
requires a later `LeaseFromMemory()`; lease release keeps the owner active and advances
the generation. A handle from an earlier generation is permanently stale.

## Explicit regions and reusable arenas

`NativeRegion` is accepted only as the direct resource of an explicit braced using
statement. Its `Lease<T>` values share one heterogeneous lexical generation and are
invalid after the body exits. Using declarations, ordinary locals, factories, fields,
parameters, aliases, unbraced forms, and nested active regions are rejected by the
bundled analyzer.

```csharp
using (NativeRegion region = new(doNotLeaseOnDeclaration: true))
{
    region.LeaseFromMemory();
    Local<int> values = region.Lease<int>(128);
    values.Access(view => view.Fill(42));
}
```

`NativeArena` is the reusable heterogeneous owner for values that should become stale
together at an explicit generation boundary. It may be a local, using-owned object, or
field. `Scratch<T>` is the ordinary acquisition and `ReleaseLeasesToNativeMemory()` is
the strict reuse boundary.

```csharp
using NativeArena arena = new(preAllocateBytes: 64 * 1024);

ArenaLease<int> coordinates = arena.Scratch<int>(1024);
ArenaLease<string> labels = arena.Scratch<string>(32);
coordinates[0] = 7;
labels[0] = "ready";
arena.ReleaseLeasesToNativeMemory();
```

An arena uses one two-ended segment bank. Ordinary acquisitions grow from the low end;
scoped acquisitions grow from the high end. The shared kernel, generation gate, stale
handle checks, and reference-root clearing are the same for pools, regions, and arenas.

Choose an arena only when genuinely heterogeneous values share one reusable bulk
lifetime. Prefer typed pools when the element types and lease shapes are predictable,
because an arena gives up type-specific capacity planning and can retain a larger shared
budget after one unusual spike. Prefer a region when all values belong to one explicit
lexical lifetime. Arena interior holes cannot be compacted or combined, the runtime does
not infer managed reachability or size classes, and every type shares one allocation
budget and operation gate. Developers must therefore choose the recycle, release, trim,
growth, and final-return boundaries explicitly; an arena is not a general replacement
for pools, regions, or ordinary managed allocation.

## Delayed activation and scoped recycling

All owners accept `doNotLeaseOnDeclaration: true`. Construction retains the requested
capacity without reserving native storage. `LeaseFromMemory()` applies that reservation
atomically and publishes the first active generation. A failed activation leaves the
owner unleased, and disposal before activation is valid and allocation-free.

Scoped recycling uses the C# `scoped` local together with the owner-specific acquisition.
The only cleanup operation is the parameterless `RecycleScoped()` call. There is no
scope token or public mark object. The call clears the complete analyzer-proven pending
set, rewinds the eligible high-water cursor or returns typed slabs to the idle bank, and
retains the backing memory.

```csharp
using NativeArena arena = new();

while (ShouldContinue())
{
    {
        scoped ArenaLease<int> scratch =
            arena.ScratchScoped<int>(4096);
        Process(scratch);
    }

    arena.RecycleScoped();
}
```

When control can leave early or exceptionally, put the same `RecycleScoped()` call in a
normal C# `finally`. `LeaseScoped` and `ScratchScoped` must directly initialize a
`scoped` local. The analyzer reports `NAM1018` for escapes, `NAM1019` when an ordinary
acquisition is placed in a scoped local, and warning `NAM1020` when completion is not
proven on every exit. Trimming only releases already-idle storage and cannot discharge
a scoped obligation.

## Ownership diagnostics and runtime safety

The analyzer uses one liveness query for both return policies. A shared live root,
active callback, alias, escape, or unknown-retention path is `NAM1007` error for
immediate native return or strict lease release, and ordinary warning `NAM1017` for the
garbage-collector variants. The provenance is equivalent; only the policy severity and
consequence wording differ. A plain stale root does not retain detached storage. An
entered operation token retains its generation owner until the callback exits.

The runtime remains defensive when diagnostics are suppressed or a caller was compiled
separately. Strict transitions refuse to free storage beneath an entered operation.
Garbage-collector transitions detach the old generation while preserving the entered
operation, and every later use of the old handle fails with a structured stale or
lifecycle exception. Exceptions expose owner kind, attempted generation, current
generation, operation, allocation identity, active-operation count, and observed
lifecycle without exposing addresses or payloads.

`TrimRetainedMemory()`, `TrimRetainedMemoryByBytes(...)`, and the lease-shape-specific
`TrimRetainedMemoryByLeaseSize(...)` reduce idle capacity without changing generation
identity. Use a memory return at a phase boundary and lease release when the owner
should remain active for the next generation.

This project is licensed under the GNU Affero General Public License, version 3 only.
The complete terms and project-specific source offer are in [LICENSE.md](LICENSE.md).

The longer examples and analyzer rules are in
[docs/getting-started.md](docs/getting-started.md).
