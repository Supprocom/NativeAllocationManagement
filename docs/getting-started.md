# Getting started

`Supprocom.NativeAllocationManagement` combines a native-memory runtime with a
bundled Roslyn analyzer. Each allocator uses only the state checks that its contract
needs. The analyzer checks ownership, bounded callbacks, destructive moves, and
scoped-recycling completion in consuming source.

The package targets .NET 10. The fast `NativePool<T>`, `NativeRegion`, and
`NativeArena` allocators accept unmanaged values. `NativeBuilder<T>`,
`NativeWorkspace<T>`, and `NativeTransfer<T>` also require unmanaged values.

`NativeConcurrentPool<T>` and `NativeConcurrentArena` retain the synchronized
generation model for concurrent ownership. Their ordinary leases can store managed
references through a separate root-aware path. Transferable leases remain unmanaged.

Install version `0.2.0` with the .NET CLI:

```powershell
dotnet add package Supprocom.NativeAllocationManagement --version 0.2.0
```

The equivalent project file entry is:

```xml
<ItemGroup>
  <PackageReference Include="Supprocom.NativeAllocationManagement" Version="0.2.0" />
</ItemGroup>
```

The package contains the runtime assembly, the ownership analyzer, and its
`buildTransitive` analyzer-presence check. Keep analyzer assets enabled in consuming
projects.

## Build a growable native sequence

`NativeBuilder<T>` owns one unpublished unmanaged sequence. The builder supports one
writer and remains in one method-local variable.

`NativeBuilder<T>` owns its native block directly. It does not require a pool or arena.

```csharp
using Supprocom.NativeAllocationManagement;

using NativeBuilder<uint> builder = new(
    preLease: 4_096);

builder.Borrow(EmitPackedWords);

NativeTransfer<uint> output = builder.Complete();
try
{
    output.Access(static view => Upload(view.AsSpan()));
}
finally
{
    output.Dispose();
}

static void EmitPackedWords(
    scoped ref NativeBuilderBorrow<uint> borrow) =>
    EmitPackedWordsNested(ref borrow);

static void EmitPackedWordsNested(
    scoped ref NativeBuilderBorrow<uint> borrow)
{
    borrow.Write(
        maximumAdditionalCount: 4_096,
        static writer => WriteWords(ref writer));
}

static void WriteWords(
    scoped ref NativeBuilderWriter<uint> writer)
{
    Span<uint> values = writer.AsSpan();
    for (int index = 0; index < values.Length; index++)
    {
        values[index] = checked((uint)index);
    }

    writer.Commit(values.Length);
}
```

`preLease` reserves builder capacity in units of `T`.

`Append(T)` writes one value directly. `Append(ReadOnlySpan<T>)` copies one batch directly
into the unused native range.

`Write` reserves its maximum range with one capacity check. Its callback writes directly
into that native range and commits the initialized prefix.

The callback must call `Commit` exactly once. A missing, repeated, negative, or oversized
commit aborts the complete active builder.

`Borrow` gives one exclusive callback a temporary builder view. Pass that view to nested
helpers only through `scoped ref NativeBuilderBorrow<T>` parameters.

The borrow supports `Append` and `Write`. It cannot complete, dispose, move, store, return,
box, or capture the builder owner.

The owner rejects all operations while its borrow is active. The runtime also rejects a
stale borrow after callback completion.

Growth resizes the native block geometrically. It preserves only the initialized native
prefix and releases the old block.

`Complete` changes the current allocation from unpublished initialization to active
transfer ownership. It does not copy elements into another final buffer.

The published transfer has the exact initialized `Length`. Its `Capacity` can remain
larger because geometric growth retains the current allocation.

Completion invalidates all builder operations. The automatic disposal from a `using`
declaration is a valid no-op after completion.

Cancellation before or after an append aborts the complete unpublished builder. An
allocation failure uses the same cleanup path.

Explicit disposal is idempotent. It frees the unpublished native block only once.

The builder has no allocator owner. Completion moves its block directly into one transfer.

An abandoned active builder has an emergency finalizer. The finalizer returns storage but
does not provide prompt reuse.

The analyzer requires direct constructor initialization into one local. It rejects
fields, properties, parameters, returns, arguments, aggregates, conversions, and closures.

`NAM1028` rejects ownership copies. `NAM1029` rejects use after completion or disposal.
`NAM1030` rejects double completion.

`NAM1031` requires completion or disposal on each exit. `NAM1032` requires direct local
acquisition, and `NAM1033` rejects builder parameters.

`NAM1034` requires `Complete` to publish directly to an exact `NativeTransfer<T>`
destination. Existing typed return, owned receiver, field, and bounded-channel rules apply.

`NAM1041` rejects direct-write view escape. `NAM1042` permits writer forwarding only through
a source-visible `scoped ref NativeBuilderWriter<T>` parameter.

`NAM1043` rejects borrow escape. `NAM1044` permits borrow forwarding only through a
source-visible `scoped ref NativeBuilderBorrow<T>` parameter.

A field destination requires an `IDisposable` containing type. Its verified `Dispose`
method must release that exact field. A property cannot receive builder completion.

Do not use the builder as cross-method ownership. Complete it and move or store the
resulting transfer instead.

The builder benchmark creates identical opaque and transparent packed-word streams.
The managed path uses two `List<uint>` values, `ToArray`, and `Buffer.BlockCopy`.

The native path completes two builders and moves both transfers through a bounded
channel. The receiver reads GL-compatible byte spans and disposes both transfers.

Run the Release benchmark with this command:

```powershell
dotnet run --project Supprocom.NativeAllocationManagement.Performance -c Release -- --native-builder --samples 10 --prelease 1024 --output native-builder.json
```

The focused direct-write regression uses 24 independent builders. It compares millions of
two-word `Append` calls with one nested-helper `Write` call per builder.

```powershell
$env:DOTNET_TieredCompilation='0'
$env:DOTNET_TieredPGO='0'
dotnet run `
    --project Supprocom.NativeAllocationManagement.Performance `
    -c Release `
    -- `
    --native-builder-write `
    --samples 6 `
    --records-per-builder 100000 `
    --enforce `
    --output native-builder-write.json
```

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
using NativeConcurrentPool<uint> pool = new(preLease: 256);

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

`NativeConcurrentArena.ScratchTransferable<T>` supports the same move contract for
heterogeneous concurrent storage. A transfer can use external storage that the
concurrent arena accepted through `ReserveExternalMemory`.

The same arena accepts concurrent `ScratchTransferable<T>` calls. The arena reserves a
disjoint range before each initializer runs. The callback writes directly into its range
without holding the arena lock. Failure returns only that range.

```csharp
using NativeConcurrentArena arena = new(
    preAllocateBytes: 24u * 25_600u * sizeof(float));

Parallel.For(0, 24, index =>
{
    NativeTransfer<float>? map = arena.ScratchTransferable<float>(
        25_600,
        writer => writer.Fill(index));
    try
    {
        ProcessMap(NativeTransfer<float>.Move(ref map));
    }
    finally
    {
        map?.Dispose();
    }
});

static void ProcessMap(NativeTransfer<float> map)
{
    try
    {
        float first = map.Read(static view => view[0]);
        Console.WriteLine(first);
    }
    finally
    {
        map.Dispose();
    }
}
```

Each `ProcessMap` call owns its transfer. It must dispose or move that ownership on every
exit. No worker-local arena wrapper, queue, or semaphore is necessary.

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

`NativePool<T>` owns reusable typed slabs. `Rent` returns a token-bound `Pooled<T>`
value. The using declarations below return the slab and then dispose the owner in the
normal C# order.

```csharp
using Supprocom.NativeAllocationManagement;

using NativePool<int> pool = new(preLease: 1_024);
using Pooled<int> values = pool.Rent(
    128,
    static writer => writer.Fill(default));

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

`preLease` reserves storage in units of `T`. `preAllocateBytes` reserves one
independent raw byte segment. These values are additive and do not use the same unit.

```csharp
using NativePool<int> pool = new(
    preLease: 1_024,
    preAllocateBytes: 64 * 1_024);
```

Only complete `T` values fit in the raw reservation. NAM retains any final partial
element bytes until trimming or owner cleanup.

`Access` and `Read` pass a scoped `NativeLeaseView<T>` only for the synchronous
callback. Each bounded operation validates the owner and lease token once. The span
performs element bounds checks inside the callback.

`Pooled<T>.Dispose()` validates the token and returns the slab to its capacity class.
The next rent must initialize all logical elements before NAM publishes the slab. A
zero-length lease still has a slab token and exactly-once return authority.

## Borrow a local pool in a helper

A source-visible synchronous helper can borrow a `NativePool<T>` parameter by value. The
caller keeps ownership when the helper returns.

```csharp
using Supprocom.NativeAllocationManagement;

using NativePool<int> pool = new();
RunBatch(pool, 8);

static void RunBatch<T>(NativePool<T> pool, int count)
    where T : unmanaged
{
    for (int index = 0; index < count; index++)
    {
        NativeTransfer<T> transfer = pool.RentTransferable(
            128,
            static writer => writer.Fill(default));
        try
        {
            transfer.Access(static values => values[0] = default);
        }
        finally
        {
            transfer.Dispose();
        }
    }
}
```

The analyzer reads the helper source. It permits `Rent`, `RentTransferable`,
`GetStatistics`, and a call to another verified helper.

The produced lease, transfer, or builder must end on every path. The helper cannot store,
return, box, convert, capture, or forward the pool to an unknown call.

An async method, iterator, `ref` parameter, or `out` parameter fails the proof. A local
pool therefore cannot cross a suspension or enter retained state through this contract.

## Rent from a persistent worker pool

A long-running worker can keep one pool in a readonly instance field. The declaring type
must provide a verified disposal path for that exact field.

```csharp
using Supprocom.NativeAllocationManagement;

public sealed class Worker : IDisposable
{
    private readonly NativePool<int> _pool = new(preLease: 1_024);

    public void Process(int count)
    {
        for (int index = 0; index < count; index++)
        {
            Pooled<int> lease = _pool.Rent(
                128,
                static writer => writer.Fill(default));
            try
            {
                lease.Access(static values => values[0] = 1);
            }
            finally
            {
                lease.Dispose();
            }
        }
    }

    public void Dispose() => _pool.Dispose();
}
```

The field stays active for the complete worker lifetime. Each lease must still end on
every branch, early return, and exception path.

A return, field store, capture, or async suspension of `Pooled<T>` remains invalid. An
owner return, release, or disposal also invalidates later field operations.

## Reuse one fixed worker workspace

`NativeWorkspace<T>` owns one fixed native block for one worker thread. Use it when each
batch has a known maximum size. The workspace does not rent from a pool or publish an
allocation record.

The explicit-state `Process` overload gives one bounded span to one static callback.
It passes caller state without a closure allocation.

```csharp
using Supprocom.NativeAllocationManagement;

using NativeWorkspace<float> workspace = new(preLease: 51_200);

ulong checksum = 0;
for (int batchIndex = 0; batchIndex < 729; batchIndex++)
{
    var state = new MapState(batchIndex, checksum);
    checksum = workspace.Process(
        25_600,
        in state,
        static (values, current) =>
        {
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = current.BatchIndex + index;
            }

            ulong result = current.Checksum;
            foreach (float value in values)
            {
                result = unchecked(result * 31 + (uint)value);
            }

            return result;
        });
}

readonly record struct MapState(
    int BatchIndex,
    ulong Checksum);
```

Construction allocates the block once. `Reset` removes only its logical publication.
Disposal or emergency finalization frees the block exactly once.

The workspace checks its owner thread before each operation. The callback cannot keep
the scoped span after `Process` returns.

Cancellation is checked before and after the callback. A callback exception or
cancellation leaves the workspace ready for the next `Process` call.

The overload does not publish the temporary range. `Length` remains zero. Call
`Initialize` when later `Access` or `Read` operations must use a published range.

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
    Local<int> identifiers = region.Lease<int>(
        64,
        static writer => writer.Fill(0));
    Local<double> weights = region.Lease<double>(
        64,
        static writer => writer.Fill(0.0));

    identifiers.Access(static view =>
    {
        Span<int> values = view.AsSpan();
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = index + 1;
        }
    });

    weights.Access(static view => view.Fill(0.5));
    double firstWeight = weights.Read(static view => view[0]);
    _ = identifiers.Process(
        checked((int)(firstWeight * 100)),
        static (values, value) =>
        {
            values[0] = value;
            return values[0];
        });
}
```

Region locals have no individual physical return. Leaving the braced body invalidates
the complete Region lifetime, so a `Local<T>` cannot be returned, stored, or passed to an
unknown retaining call.

`Local<T>` does not expose a per-element indexer. Use `Access`, `Read`, or `Process`
to validate the owner once. Call `AsSpan()` once before a hot element loop.

## Reusable heterogeneous arenas

`NativeArena` is a reusable heterogeneous owner for values that should become stale at
one explicit generation boundary. It may be a local, a using-owned object, or a field.
`Scratch<T>` and `ScratchScoped<T>` are its only acquisition methods; `ArenaLease<T>`
has no individual disposal because arena storage is reclaimed as a group.

```csharp
using Supprocom.NativeAllocationManagement;

using NativeArena arena = new(preAllocateBytes: 64 * 1024);
ArenaLease<int> coordinates = arena.Scratch<int>(
    1_024,
    static writer => writer.Fill(0));
ArenaLease<double> weights = arena.ScratchScoped<double>(
    32,
    static writer => writer.Fill(0.5));

coordinates.Access(static view => view.AsSpan()[0] = 7);
double firstWeight = weights.Read(static view => view[0]);

arena.RecycleScoped();
arena.Reset();
```

The arena has separate ordinary and scoped bump lanes. `Reset()` invalidates both lanes
and retains their segments. `RecycleScoped()` invalidates only the scoped lane.

An active bounded callback blocks reset and disposal. A stale generation or scoped epoch
cannot access reused storage.

Typed pools are preferred when repeated element types and lease shapes are known, and a
region is preferred when heterogeneous values share one braced lexical lifetime. Use an
arena only for a heterogeneous reusable bulk lifetime. The base arena is single-writer
and accepts unmanaged values. It uses bump allocation and does not reclaim individual
ranges. The developer controls scoped recycle, generation reset, trim, and final disposal.

## Concurrent owner generations

`NativeConcurrentPool<T>` and `NativeConcurrentArena` keep the broader synchronized
generation contract. Use these types only when storage needs concurrent access,
transferable ownership, managed-reference roots, or explicit memory-return transitions.

Construction normally publishes an active generation. Passing
`doNotLeaseOnDeclaration: true` defers that generation. The configured reservation
remains private until `LeaseFromMemory()` succeeds.

```csharp
using Supprocom.NativeAllocationManagement;

using NativeConcurrentPool<byte> pool = new(
    preLease: 4_096,
    doNotLeaseOnDeclaration: true);

pool.LeaseFromMemory();
using ConcurrentPooled<byte> buffer = pool.Rent(
    4_096,
    static writer => writer.Fill(default));
buffer.Access(view => view.Fill(0x2A));
```

The same form applies to `NativeConcurrentArena`. Acquisition and lifecycle operations
reject an unleased owner. Disposal before activation is valid and terminal. A failed
activation does not publish a partial generation.

Memory return ends the current concurrent-owner generation. A later
`LeaseFromMemory()` creates the next generation. Lease release invalidates current
leases, retains reusable storage, and leaves the owner active.

```csharp
using Supprocom.NativeAllocationManagement;

NativeConcurrentPool<int> pool = new(
    preLease: 256,
    returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

try
{
    ConcurrentPooled<int> first = pool.Rent(
        64,
        static writer => writer.Fill(default));
    first.Access(view => view.Fill(1));
    first.Dispose();

    pool.ReleaseLeasesToNativeMemory();
    ConcurrentPooled<int> second = pool.Rent(
        64,
        static writer => writer.Fill(default));
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

`ReturnMemoryToNativeMemory()` frees the current segments after its safety gate
succeeds. `ReturnMemoryToGarbageCollector()` detaches the current generation for later
finalization. Old handles become stale when either operation succeeds.

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
        scoped ArenaLease<int> scratch = arena.ScratchScoped<int>(
            4_096,
            static writer => writer.Fill(0));
        Process(scratch);
    }

    arena.RecycleScoped();
}
```

`NativeArena.ScratchScoped` and the concurrent scoped methods must directly initialize
a `scoped` local. The analyzer reports `NAM1018` for an escape. It reports `NAM1019`
when an ordinary acquisition uses a scoped local. It reports `NAM1020` when code does
not recycle a pending scoped set on each path.

Early returns and exceptions require `RecycleScoped()` in a `finally` block. The base
arena resets its scoped bump lane and advances its scoped epoch. Concurrent owners also
clear their root-aware scoped state. Trimming cannot complete a scoped obligation.

## Owner statistics

`NativePool<T>`, `NativeRegion`, `NativeArena`, and the concurrent owners expose
`GetStatistics()` for diagnostics and capacity policy. The snapshot reports lifecycle,
requested bytes, retained bytes, segment counts, trimming, and fresh segment allocations.
Generation and retired fields apply only when the selected owner uses those concepts.

```csharp
using Supprocom.NativeAllocationManagement;

using NativePool<int> pool = new(preLease: 4_096);
using Pooled<int> values = pool.Rent(
    1_024,
    static writer => writer.Fill(default));

NativeOwnerStatistics snapshot = pool.GetStatistics();
Console.WriteLine(
    $"requested={snapshot.RequestedBytes}, "
    + $"retained={snapshot.RetainedBytes}, "
    + $"segments={snapshot.SegmentCount}");
```

The operation is a point-in-time diagnostic, not a free hot-path counter. Some owners
scan retained storage or take a synchronization gate. Capture it at a maintenance
boundary. Do not call it for each lease or inside a benchmark loop.

`NativeMemoryDiagnostics.Snapshot()` provides the corresponding process-wide physical
native counters. It is useful for proving that terminal cleanup returned to a known
baseline, while `GetStatistics()` explains which live owner retained a particular
capacity.

## Trimming and runtime fallback

Pool trimming releases idle slabs. Arena trimming releases unused tail segments. These
operations do not invalidate a live handle or complete scoped storage. `NativeRegion`
has no trim operation because its complete segment chain ends with its lexical lifetime.

The runtime repeats the checks required by each allocator contract when analyzer
diagnostics are unavailable. Pool handles validate owner identity and lease tokens.
Arena handles validate generation or scoped epoch. Region handles validate owner state
and owner thread. Bounded callbacks prevent reset, return, or disposal during access.
