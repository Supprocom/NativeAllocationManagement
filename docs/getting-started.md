# Getting started

Native Allocation Management (NAM) keeps buffers in native memory, outside the managed heap. The package also bundles a Roslyn analyzer that turns ownership mistakes into compile errors.

Install the released `0.3.2` package (it targets .NET 10):

```powershell
dotnet add package Supprocom.NativeAllocationManagement --version 0.3.2
```

Or add it to the project file:

```xml
<ItemGroup>
  <PackageReference Include="Supprocom.NativeAllocationManagement" Version="0.3.2" />
</ItemGroup>
```

Keep the analyzer enabled. If the build excludes it, NAM reports [NAM9001](diagnostics/NAM9001.md).

All examples use `using Supprocom.NativeAllocationManagement;` and implicit usings.

## Choose an allocator

An *owner* holds native memory. A *lease* is a handle to one block of it. Every lease is a `ref struct`, so the compiler keeps it in the method that took it.

| You need | Use | Section |
|---|---|---|
| A temporary buffer of one element type | `NativePool<T>` | [Pool](#pool-buffers-of-one-element-type) |
| One buffer, reused for every batch | `NativeWorkspace<T>` | [Workspace](#reuse-one-buffer-for-every-batch) |
| Several element types that end together | `NativeRegion` | [Region](#group-allocations-in-a-region) |
| Several element types that you free together, again and again | `NativeArena` | [Arena](#free-many-allocations-at-once-with-an-arena) |
| Output of unknown size | `NativeBuilder<T>` | [Builder](#build-a-growable-native-sequence) |
| Data that moves to another thread | `NativeTransfer<T>` | [Transfer](#transfer-ownership-across-threads) |
| One owner used by several threads | `NativeConcurrentPool<T>`, `NativeConcurrentArena` | [Concurrent owners](#share-an-owner-between-threads) |
| A known number of fixed-size buffers on one thread | `NativePreparedPool<T>` | [prepared pools](prepared-pools.md) |
| A native-byte cap over several owners | `NativeMemoryBudget` | [memory budgets](memory-budgets.md) |
| Several regions with one lifetime | `NativeLayout` | [typed layouts](typed-layouts.md) |
| Immutable data with several owners | `NativeShared<T>`, `NativeWeak<T>` | [immutable sharing](immutable-sharing.md) |

`NativePool<T>`, `NativeArena` and `NativeWorkspace<T>` belong to the thread that created them. `NativeRegion` is a `ref struct`.

Use arrays, spans and `ArrayPool<T>` when they are enough; see [allocator selection](allocator-selection.md). For size caps, fixed-shape pools and layouts, see [memory budgets](memory-budgets.md), [prepared pools](prepared-pools.md) and [typed layouts](typed-layouts.md). Unique and shared ownership are covered in [unique ownership](unique-ownership.md) and [immutable sharing](immutable-sharing.md).

## Pool buffers of one element type

`NativePool<T>` (`T` must be unmanaged) hands out buffers and takes them back for reuse.

```csharp
using Supprocom.NativeAllocationManagement;

using NativePool<int> pool = new(preLease: 1_024);
using Pooled<int> values = pool.Rent(128, 0);

Span<int> span = values.AsSpan();
for (int index = 0; index < span.Length; index++)
{
    span[index] = index * 2;
}

int total = values.Read(static view =>
{
    int sum = 0;
    foreach (int value in view.AsSpan())
    {
        sum += value;
    }

    return sum;
});
```

`Rent(length, value)` sets every element to `value` before you see it. To compute each element, pass a callback instead; it must write all `length` elements, in order.

```csharp
using NativePool<int> pool = new();
using Pooled<int> squares = pool.Rent(8, static writer =>
{
    for (int index = 0; index < writer.Length; index++)
    {
        writer.Write(index * index);
    }
});
```

`preLease` is the starting capacity in elements of `T`. `new NativePool<T>(returnMemoryOnDispose: NativeMemoryReturn.Free)` frees its native memory on `Dispose`; the default, `Reuse`, keeps it for the thread's next owner of the same kind.

| Call | Use it to |
|---|---|
| `AsSpan()` | get a `Span<T>` over the lease |
| `Access(static view => ...)` | write through the callback's *view* |
| `Read(static view => ...)` | read through the view and return a result |
| `CopyFrom(span)`, `CopyTo(span)`, `Clear()` | copy managed data in or out (`CopyFrom` needs exactly `Length` elements) |

A span from `AsSpan()` carries no ownership. Use it only while its lease is alive.

```csharp
// WRONG, does not compile: NAM1051 (the span outlives its lease)
using NativePool<int> pool = new();
Span<int> kept;
using (Pooled<int> temp = pool.Rent(8, 0))
{
    kept = temp.AsSpan();
}

kept[0] = 1;
```

The compiler also checks the span against its lease. A local that starts as `default` has the widest scope, so declare it `scoped` to assign a span later.

```csharp
// WRONG, does not compile: CS8168
using NativePool<int> pool = new();
using Pooled<int> lease = pool.Rent(8, 0);
Span<int> s = default;
s = lease.AsSpan();

// RIGHT
scoped Span<int> t = default;
t = lease.AsSpan();
```

A method that owns the lease cannot return its span (`using Pooled<int> p = ...; return p.AsSpan();` is CS8168). A helper that takes the lease by value cannot return the span either (CS8166); pass the span itself.

```csharp
// WRONG, does not compile: CS8166
static Span<int> Head(Pooled<int> lease) => lease.AsSpan();

// RIGHT
static Span<int> HeadOf(Span<int> span) => span.Slice(0, 1);
```

### Rent for one call

`pool.Process` rents, runs your callback and returns the buffer, even if the callback throws. Pass what the callback needs as state, and make the callback `static`.

```csharp
using Supprocom.NativeAllocationManagement;

using NativePool<int> pool = new();

int result = pool.Process(128, 0, 42, static (values, input) =>
{
    values[0] = input;
    return values[0] + values.Length;
});
```

The arguments are `length`, the fill value, your state, then the callback.

### Lease rules

| Rule | If you break it |
|---|---|
| Dispose each lease exactly once | A second `Dispose` throws `NativeAllocationReturnedException`, as does any use after `Dispose`; the analyzer reports NAM1004. |
| End every lease on every path | A lease that is never disposed is NAM1003. |
| Use a lease you got from `Rent` or `Scratch` | A `Pooled` or `ArenaLease` is checked at runtime (a disposed or reset lease throws) and a discarded result is NAM1013. |
| Dispose all leases before the pool | `pool.Dispose()` with a live lease throws `InvalidOperationException`. |
| Use the pool on the thread that created it | Another thread gets `NativeAllocationStateException`. |
| Do not hold a lease across `await` | Compile error CS4007, and NAM1011. |

All `NativeAllocation*Exception` types derive from `NativeAllocationException`, which derives from `InvalidOperationException`.

```csharp
// WRONG, does not compile: NAM1013 (the lease leaves its owner's method)
static Pooled<int> Make(NativePool<int> pool) => pool.Rent(8, 0);
```

### Pass a pool to a helper

A helper in your own source can take the pool by value. The caller keeps ownership.

```csharp
using Supprocom.NativeAllocationManagement;

using NativePool<int> pool = new();
RunBatch(pool, 8);

static void RunBatch<T>(NativePool<T> pool, int count)
    where T : unmanaged
{
    for (int index = 0; index < count; index++)
    {
        using Pooled<T> values = pool.Rent(128, default(T));
        values.Access(static view => view[0] = default);
    }
}
```

The analyzer reads the helper. It may call `Rent`, `Process`, `GetStatistics` and other helpers that pass the same check. It may not store, return, box, capture or forward the pool to a call the analyzer cannot read. An `async` method, an iterator, or a `ref` or `out` parameter fails the check.

```csharp
// WRONG, does not compile: NAM1001 (the pool is stored)
static class Registry
{
    private static NativePool<int>? s_kept;

    public static void Keep(NativePool<int> pool) => s_kept = pool;
}
```

### Keep a pool in a field

A long-lived worker can own a pool in a `readonly` field. Always dispose it in the class's `Dispose`; the analyzer reports a missing disposal (NAM1015) only for pools that use `NativeMemoryReturn.Free`.

```csharp
using Supprocom.NativeAllocationManagement;

public sealed class Worker : IDisposable
{
    private readonly NativePool<int> _pool = new(
        preLease: 1_024,
        returnMemoryOnDispose: NativeMemoryReturn.Free);

    public void Run(int count)
    {
        for (int index = 0; index < count; index++)
        {
            using Pooled<int> lease = _pool.Rent(128, 0);
            lease.Access(static view => view[0] = 1);
        }
    }

    public void Dispose() => _pool.Dispose();
}
```

```csharp
// WRONG, does not compile: NAM1015 (no Dispose releases the field)
public sealed class Leaky
{
    private readonly NativePool<int> _pool = new(
        preLease: 1_024,
        returnMemoryOnDispose: NativeMemoryReturn.Free);

    public void Run()
    {
        using Pooled<int> lease = _pool.Rent(128, 0);
    }
}
```

## Reuse one buffer for every batch

`NativeWorkspace<T>` allocates one fixed block of `preLease` elements when you create it. It belongs to one thread.

```csharp
using Supprocom.NativeAllocationManagement;

using NativeWorkspace<float> workspace = new(preLease: 25_600);

ulong checksum = 0;
for (int batch = 0; batch < 729; batch++)
{
    checksum = workspace.Process(
        25_600,
        in checksum,
        static (values, previous) =>
        {
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = index;
            }

            ulong result = previous;
            foreach (float value in values)
            {
                result = unchecked(result * 31 + (uint)value);
            }

            return result;
        });
}
```

`Process(length, in state, callback)` gives the callback a span of `length` elements. A `length` above the capacity throws `ArgumentOutOfRangeException`. The block is not cleared between calls, so write each element before you read it.

To keep a range between calls, publish it, then read it with `Access` and `Read`. `Process` throws `InvalidOperationException` until you call `Reset()`.

```csharp
using NativeWorkspace<int> workspace = new(preLease: 64);

workspace.Initialize(64, static writer => writer.Fill(7));
int first = workspace.Read(static view => view[0]);

workspace.Reset();
```

| Rule | If you break it |
|---|---|
| Use it on the thread that created it | `InvalidOperationException` |
| Do not start a second operation inside a callback | `InvalidOperationException` |
| `Access` and `Read` need a published range | `InvalidOperationException` |

```csharp
// WRONG, does not compile: NAM1036, NAM1038 (a second owner that is never disposed)
using NativeWorkspace<int> workspace = new(preLease: 8);
NativeWorkspace<int> alias = workspace;
```

To share it with a helper, pass it as `scoped in NativeWorkspace<T>`.

```csharp
static int Peek(scoped in NativeWorkspace<int> workspace) =>
    workspace.Read(static view => view[0]);
```

```csharp
// WRONG, does not compile: NAM1040 (by-value parameter)
static void Fill(NativeWorkspace<int> workspace) { }
```

## Group allocations in a region

`NativeRegion` ends every lease when its `using` scope ends; storage is kept for reuse unless you pass `NativeMemoryReturn.Free`. Use it for several allocations, of different types, that live and die together.

```csharp
using Supprocom.NativeAllocationManagement;

using NativeRegion region = new(preAllocateBytes: 4_096);

Local<int> ids = region.Lease<int>(64, 0);
Local<double> weights = region.Lease<double>(64, 0.5);

Span<int> idSpan = ids.AsSpan();
for (int index = 0; index < idSpan.Length; index++)
{
    idSpan[index] = index + 1;
}

double firstWeight = weights.Read(static view => view[0]);
```

The compiler binds each `Local<T>` and its span to the region's scope, so returning one is a compile error (CS8352, CS8168). `preAllocateBytes` reserves bytes up front. A `Local<T>` has no `Dispose`; it ends with the region. It offers `AsSpan`, `Access`, `Read`, `Process`, `CopyFrom`, `CopyTo` and `Clear`.

| Rule | If you break it |
|---|---|
| Create the region as the resource of a `using` | NAM1006 |
| Keep every `Local<T>` and span inside the region's scope | NAM1012, NAM1051 |
| Avoid managed allocations inside the scope | warning NAM1035 |

```csharp
// WRONG, does not compile: NAM1006 (not a using resource)
NativeRegion region = new();
Local<int> values = region.Lease<int>(4, 0);
```

```csharp
// WRONG, does not compile: NAM1012 (the lease leaves the region)
static Local<int> Make()
{
    using NativeRegion region = new();
    return region.Lease<int>(4, 0);
}
```

## Free many allocations at once with an arena

`NativeArena` hands out leases of any unmanaged type. They have no individual `Dispose`; `Reset()` ends them all and keeps the memory for the next round.

```csharp
using Supprocom.NativeAllocationManagement;

using NativeArena arena = new(preAllocateBytes: 64 * 1024);

for (int frame = 0; frame < 3; frame++)
{
    {
        ArenaLease<int> coordinates = arena.Scratch<int>(1_024, 0);
        ArenaLease<double> weights = arena.Scratch<double>(32, 0.5);

        coordinates.Access(static view => view[0] = 7);
        double first = weights.Read(static view => view[0]);
    }

    arena.Reset();
}
```

Leases must be out of scope when you call `Reset()`. Using one afterwards throws `NativeAllocationReturnedException`.

```csharp
// WRONG, does not compile: NAM1007 (a lease is live at Reset), NAM1004 (and used after it)
using NativeArena arena = new();
ArenaLease<int> values = arena.Scratch<int>(1, 0);
arena.Reset();
values.Access(static view => view[0] = 1);
```

```csharp
// WRONG, does not compile: NAM1051 (the span outlives Reset)
using NativeArena arena = new();
Span<int> kept = arena.Scratch<int>(4, 0).AsSpan();
arena.Reset();
kept[0] = 1;
```

### Recycle only the scratch part of a frame

`ScratchScoped` leases come from a separate lane. `RecycleScoped()` ends only those, so leases from `Scratch` stay valid. Put `RecycleScoped()` in a `finally` block.

```csharp
using Supprocom.NativeAllocationManagement;

using NativeArena arena = new();

for (int frame = 0; frame < 3; frame++)
{
    try
    {
        scoped ArenaLease<int> scratch = arena.ScratchScoped<int>(4_096, 0);
        int length = scratch.Read(static view => view.Length);
    }
    finally
    {
        arena.RecycleScoped();
    }
}
```

```csharp
// WRONG, does not compile: NAM1018 (a scoped lease must initialize a scoped local)
static ArenaLease<int> Make(NativeArena arena) => arena.ScratchScoped<int>(4, 0);
```

Two warnings cover the rest: [NAM1019](diagnostics/NAM1019.md) for `Scratch` into a `scoped` local, and [NAM1020](diagnostics/NAM1020.md) for a path that skips `RecycleScoped()`.

## Build a growable native sequence

Use `NativeBuilder<T>` (`T` must be unmanaged) when you produce output of unknown size and then hand it on. It writes straight into native memory; `Complete` passes that memory on as a `NativeTransfer<T>` without copying.

```csharp
using Supprocom.NativeAllocationManagement;

using NativeBuilder<uint> builder = new(preLease: 1_024);

builder.Append(1u);
builder.Append(new uint[] { 2, 3, 4 });

using NativeTransfer<uint> output = builder.Complete();
output.Access(static view => Upload(view.AsSpan()));

static void Upload(scoped ReadOnlySpan<uint> words) { /* send to the GPU, a socket, ... */ }
```

The steps are always the same: create, fill, complete, use, dispose.

### 1. Create

`new NativeBuilder<T>()` starts empty. `new NativeBuilder<T>(preLease: n)` reserves room for `n` elements up front (a negative `n` throws `ArgumentOutOfRangeException`).

Declare the builder as a local and initialize it in the same statement. Keep it in that method.

```csharp
// WRONG, does not compile: NAM1032 (stored in a field), NAM1033 (passed as a parameter)
static NativeBuilder<uint> shared = new();
static void Fill(NativeBuilder<uint> builder) { }
```

### 2. Fill

A builder takes one operation at a time. Starting another operation while one is running throws `InvalidOperationException`. Each call below also takes an optional `CancellationToken` as its last argument.

| Call | Use it to |
|---|---|
| `Append(T)` | add one value |
| `Append(ReadOnlySpan<T>)` | copy a batch |
| `Write(maximumAdditionalCount, callback)` | fill up to that many new elements in place |
| `Borrow(callback)` | let several helper methods append and write |

The builder grows as needed and keeps what you already wrote.

#### Write in place

`Write` hands your callback a *writer*: a span of `maximumAdditionalCount` uninitialized native elements. Fill a prefix of it, then call `Commit(count)` once. Only the committed prefix is added.

```csharp
builder.Write(maximumAdditionalCount: 8, static writer =>
{
    Span<uint> values = writer.AsSpan();
    for (int index = 0; index < 5; index++)
    {
        values[index] = (uint)index;
    }

    writer.Commit(5);
});
```

The span is valid only inside the callback, because the builder can move or free its storage afterwards. To split the work into helpers, pass the writer as `scoped ref NativeBuilderWriter<T>`.

```csharp
builder.Write(4_096, static writer => WriteWords(ref writer));

static void WriteWords(scoped ref NativeBuilderWriter<uint> writer)
{
    Span<uint> values = writer.AsSpan();
    values.Fill(7);
    writer.Commit(values.Length);
}
```

#### Borrow for helpers

`Borrow` lends the builder to one callback as a *borrow*, a temporary handle that supports `Append`, `Write`, `Count` and `Capacity`. Pass it to helpers as `scoped ref NativeBuilderBorrow<T>`. While the callback runs, use the borrow, not the builder.

```csharp
builder.Borrow(static (scoped ref NativeBuilderBorrow<uint> borrow) =>
    AppendHeader(ref borrow));

static void AppendHeader(scoped ref NativeBuilderBorrow<uint> borrow)
{
    borrow.Append(0xCAFEu);
    borrow.Append(2u);
}
```

#### When a fill goes wrong

Any failure inside an operation frees the whole builder: a cancelled token (`OperationCanceledException`), a failed allocation, an exception thrown by your callback, or a bad commit. Later calls then throw `ObjectDisposedException`. Pass the callback straight to `Write` or `Borrow`, not through a stored delegate.

| Mistake | You get |
|---|---|
| Callback never calls `Commit` | `InvalidOperationException` |
| `Commit` called twice | `InvalidOperationException` |
| `Commit` count negative or above `maximumAdditionalCount` | `ArgumentOutOfRangeException` |
| Negative `maximumAdditionalCount` | `ArgumentOutOfRangeException` |
| `builder.Append(...)` or `builder.Complete()` inside a `Borrow` callback | `InvalidOperationException` (and NAM1044) |

```csharp
// WRONG, does not compile: NAM1041 (the span leaves the callback)
builder.Write(2, static writer => { Keep(writer.AsSpan()); writer.Commit(2); });

// WRONG, does not compile: NAM1042 (a second handle to the writer)
builder.Write(2, static writer => { NativeBuilderWriter<uint> copy = writer; copy.Commit(0); });

// WRONG, does not compile: NAM1044 (borrow passed by value)
static void AppendHeader(NativeBuilderBorrow<uint> borrow) { }
```

### 3. Complete

`Complete()` ends the builder and returns the data as a `NativeTransfer<T>`: the single owner of the finished sequence. You can return it, or move it into a field or to another thread: assign `Complete()` to a `NativeTransfer<T>?` local first, because `Move` takes `ref NativeTransfer<T>?`. `Length` is the number of elements you wrote. `Capacity` can be larger, because the builder keeps the block it grew into.

Assign the result straight to a `NativeTransfer<T>` local, or return it. After `Complete` the builder is finished: using it or completing it again throws `InvalidOperationException`. Disposing it is allowed and does nothing.

```csharp
// WRONG, does not compile
builder.Complete();                       // NAM1034: result discarded
builder.Append(5u);                       // NAM1029: builder already completed
using NativeTransfer<uint> again = builder.Complete(); // NAM1030: second completion
```

### 4. Use

Read the data through the transfer. `Access` and `Read` give you a view that is valid only inside the callback.

```csharp
int count = output.Read(static view => view.AsSpan().Length);
```

A helper that receives `view.AsSpan()` must declare the parameter `scoped` (as `Upload` does above); otherwise the analyzer reports NAM1024. To store the transfer in a field or send it to another thread, assign `Complete()` to a `NativeTransfer<T>?` local and move it with `Move(ref ...)`; see [Transfer ownership across threads](#transfer-ownership-across-threads).

### 5. Dispose

Every path must end in `Complete()` or `Dispose()` on the builder, and then in `Dispose()` on the transfer. A `using` declaration does both. Disposing a builder twice is safe. Disposing a transfer twice throws `ObjectDisposedException`.

A builder that is never completed or disposed is freed by a finalizer, but only when the GC gets to it. The analyzer reports it instead (NAM1031).

```csharp
// WRONG, does not compile: NAM1031 (no Complete or Dispose on exit)
NativeBuilder<uint> leaked = new();
leaked.Append(1u);
```

To pass the data to another method, complete the builder first and pass the transfer, not the builder.

### Diagnostics for builders

| ID | What it catches | How to fix |
|---|---|---|
| [NAM1028](diagnostics/NAM1028.md) | Builder copied, boxed or stored | Keep one local variable |
| [NAM1029](diagnostics/NAM1029.md) | Use after `Complete` or `Dispose` | Do all appends before `Complete` |
| [NAM1030](diagnostics/NAM1030.md) | `Complete` on a builder that is already completed or disposed | Complete once |
| [NAM1031](diagnostics/NAM1031.md) | Exit path without `Complete` or `Dispose` | Use a `using` declaration |
| [NAM1032](diagnostics/NAM1032.md) | Builder not created directly into a local (field, return, discarded) | `using NativeBuilder<T> b = new();` |
| [NAM1033](diagnostics/NAM1033.md) | Builder passed as a parameter | Complete it and pass the `NativeTransfer<T>` |
| [NAM1034](diagnostics/NAM1034.md) | `Complete` result discarded, widened, or stored in a field or property | Assign to a `NativeTransfer<T>` local or return it |
| [NAM1041](diagnostics/NAM1041.md) | Write span passed out of the callback | Use the span inside the callback |
| [NAM1042](diagnostics/NAM1042.md) | Writer copied, or callback passed indirectly | Pass the lambda to `Write`; forward as `scoped ref` |
| [NAM1043](diagnostics/NAM1043.md) | Borrow returned, stored or captured | Use it only inside the `Borrow` callback |
| [NAM1044](diagnostics/NAM1044.md) | Borrow parameter not `scoped ref`, or builder used during a borrow | Declare `scoped ref NativeBuilderBorrow<T>`; use the borrow |
| [NAM1024](diagnostics/NAM1024.md) | Transfer view span passed to an unscoped parameter | Declare the parameter `scoped` |

### Terms

- **Builder**: the `NativeBuilder<T>` object that owns the unfinished sequence.
- **preLease**: the starting capacity of a builder, counted in elements of `T`.
- **Writer**: the `NativeBuilderWriter<T>` that `Write` gives your callback.
- **Commit**: tells the writer how many elements of its span are initialized.
- **Borrow**: the temporary `NativeBuilderBorrow<T>` handle that `Borrow` gives your callback.
- **Complete**: ends the builder and returns its data as a transfer.
- **Transfer**: a `NativeTransfer<T>`, the single owner of finished native data; it must be disposed or moved on.

## Transfer ownership across threads

`NativeTransfer<T>` is the single owner of a block of initialized native memory. Unlike a lease it is an ordinary struct: you can store it, send it through a channel and read it on another thread. Ownership moves with `Move`, and the last owner disposes it.

```csharp
using System.Threading.Channels;
using Supprocom.NativeAllocationManagement;

Channel<NativeTransfer<uint>> channel =
    Channel.CreateBounded<NativeTransfer<uint>>(1);

NativeTransfer<uint>? source = NativeTransfer<uint>.Create(new uint[] { 1, 2, 3 });
await channel.Writer.WriteAsync(NativeTransfer<uint>.Move(ref source));

NativeTransfer<uint> receiver = await channel.Reader.ReadAsync();
try
{
    uint sum = receiver.Read(static view =>
    {
        uint total = 0;
        foreach (uint value in view.AsSpan())
        {
            total += value;
        }

        return total;
    });
}
finally
{
    receiver.Dispose();
}
```

`Move(ref source)` returns the next owner and sets `source` to `null`. A copy of the old value then throws `InvalidOperationException` when used.

### Where transfers come from

| Source | Result |
|---|---|
| `NativeTransfer<T>.Create(span)` | a copy of `span` |
| `NativeTransfer<T>.Create(length, value)` | `length` elements set to `value` |
| `NativeBuilder<T>.Complete()` | the builder's data, without a copy |
| `pool.RentTransferable(length, value)` on a `NativeConcurrentPool<T>` | pooled memory; `Dispose` gives it back to the pool |

Assign the result to a local in the same statement (`NativeTransfer<T>? source = ...`).

### Where ownership can go

Pass it on with `Move(ref source)` to one of these:

- a by-value `NativeTransfer<T>` parameter; the callee disposes it or moves it on
- a `NativeTransfer<T>` field
- a `return`
- `WriteAsync` on a `Channel.CreateBounded<NativeTransfer<T>>` local

```csharp
using Supprocom.NativeAllocationManagement;

NativeTransfer<uint>? source = NativeTransfer<uint>.Create(new uint[] { 5, 6 });
uint length = Consume(NativeTransfer<uint>.Move(ref source));

static uint Consume(NativeTransfer<uint> data)
{
    try
    {
        return data.Read(static view => (uint)view.Length);
    }
    finally
    {
        data.Dispose();
    }
}
```

### Rules

| Step | Rule | If you break it |
|---|---|---|
| Create | Assign into a local | NAM1026 |
| Hand over | Move, never copy | NAM1021, NAM1023 |
| Use | The view from `Access` or `Read` stays in the callback | NAM1024 |
| End | Every path disposes the transfer or moves it on | NAM1025 |
| End | Do not use a transfer after `Dispose` or `Move` | NAM1022 |

```csharp
// WRONG, does not compile: NAM1021 (a copy is not a second owner)
NativeTransfer<uint>? source = NativeTransfer<uint>.Create(new uint[] { 1 });
NativeTransfer<uint> owner = NativeTransfer<uint>.Move(ref source);
NativeTransfer<uint> copy = owner;
copy.Dispose();
```

```csharp
// WRONG, does not compile: NAM1023 (source was already moved)
NativeTransfer<uint>? source = NativeTransfer<uint>.Create(new uint[] { 1 });
NativeTransfer<uint> first = NativeTransfer<uint>.Move(ref source);
NativeTransfer<uint> second = NativeTransfer<uint>.Move(ref source);
first.Dispose();
second.Dispose();
```

```csharp
// WRONG, does not compile: NAM1022 (used after Dispose)
NativeTransfer<uint> data = NativeTransfer<uint>.Create(new uint[] { 1 });
data.Dispose();
int length = data.Length;
```

```csharp
// WRONG, does not compile: NAM1024 (the span leaves the callback)
using NativeTransfer<uint> data = NativeTransfer<uint>.Create(new uint[] { 1 });
data.Access(static view => Keep(view.AsSpan()));

static void Keep(Span<uint> values) { }
```

```csharp
// WRONG, does not compile: NAM1027 (ref parameter)
static void Receive(ref NativeTransfer<uint> transfer) { }
```

```csharp
// WRONG, does not compile: NAM1025 (the unbounded channel is not accepted as a hand-over)
Channel<NativeTransfer<uint>> channel = Channel.CreateUnbounded<NativeTransfer<uint>>();
NativeTransfer<uint>? source = NativeTransfer<uint>.Create(new uint[] { 1 });
await channel.Writer.WriteAsync(NativeTransfer<uint>.Move(ref source));
```

A transfer rented from a `NativeConcurrentPool<T>` belongs to that pool. Dispose the transfer before the pool.

```csharp
// WRONG: throws NativeAllocationDisposedException (the pool was disposed first)
NativeConcurrentPool<uint> pool = new();
NativeTransfer<uint> block = pool.RentTransferable(4, 0u);
try
{
    pool.Dispose();
    int length = block.Read(static view => view.Length);
}
finally
{
    block.Dispose();
}
```

If the owner is disposed while a transfer is idle, the transfer is invalid but its receiver must still dispose it. `default(NativeTransfer<T>)` rejects every use, including `Dispose` (`NativeAllocationUninitializedException`).

## Share an owner between threads

`NativeConcurrentPool<T>` and `NativeConcurrentArena` can be used by many threads at once. Each thread rents, uses and disposes its own leases. Unlike `NativePool<T>`, the pool also accepts element types that hold managed references.

```csharp
using Supprocom.NativeAllocationManagement;

using NativeConcurrentPool<int> pool = new(preLease: 256);

Parallel.For(0, 100, _ =>
{
    using ConcurrentPooled<int> buffer = pool.Rent(256, 0);
    buffer.Access(static view => view.Fill(42));
});
```

`ConcurrentPooled<T>.Process(length, in state, callback)` runs a callback with a span. Put the data the callback needs in `state` and make the callback `static`; otherwise the analyzer reports [NAM1047](diagnostics/NAM1047.md), [NAM1048](diagnostics/NAM1048.md) or [NAM1049](diagnostics/NAM1049.md).

```csharp
using NativeConcurrentPool<int> pool = new();
using ConcurrentPooled<int> buffer = pool.Rent(4, 3);
int offset = 5;
int sum = buffer.Process(4, in offset, static (values, add) => values[0] + add);
```

### Owner generations

A concurrent owner works in *generations*. These calls end the current one:

| Call | Effect |
|---|---|
| `ReleaseLeasesToNativeMemory()` | Ends all leases, keeps the memory, and the owner stays usable |
| `ReturnMemoryToNativeMemory()` | Ends the generation and frees its memory |
| `ReturnMemoryToGarbageCollector()` | Ends the generation and leaves its memory to finalization |
| `LeaseFromMemory()` | Starts the next generation |

Old leases throw `NativeAllocationReturnedException` once their generation has ended. With `doNotLeaseOnDeclaration: true` the owner starts without a generation, and renting throws `NativeAllocationStateException` until `LeaseFromMemory()`.

```csharp
using Supprocom.NativeAllocationManagement;

NativeConcurrentPool<int> pool = new(
    preLease: 256,
    returnMemoryOnDispose: NativeMemoryReturn.Free);

try
{
    using (ConcurrentPooled<int> first = pool.Rent(64, 1))
    {
        first.Access(static view => view.Fill(2));
    }

    pool.ReleaseLeasesToNativeMemory();
    pool.ReturnMemoryToNativeMemory();
    pool.LeaseFromMemory();

    using ConcurrentPooled<int> second = pool.Rent(64, 3);
}
finally
{
    pool.Dispose();
}
```

On an owner declared with `using`, `ReturnMemoryTo*`, `LeaseFromMemory()` and an explicit `Dispose()` are NAM1005; use `try`/`finally` as above. `ReleaseLeasesTo*` has no such restriction.

```csharp
// WRONG, does not compile: NAM1007 (a lease is live), NAM1004 (and used afterwards)
NativeConcurrentPool<int> pool = new();
ConcurrentPooled<int> lease = pool.Rent(4, 0);
pool.ReturnMemoryToNativeMemory();
lease.Access(static view => view[0] = 1);
```

`ReturnMemoryToGarbageCollector()` and `ReleaseLeasesToGarbageCollector()` with a live lease are warning [NAM1017](diagnostics/NAM1017.md) instead of an error. The lease is stale either way.

## Statistics and trimming

`GetStatistics()` on a pool, region, arena, workspace or concurrent owner returns a snapshot.

```csharp
using Supprocom.NativeAllocationManagement;

using NativePool<int> pool = new(preLease: 4_096);
using Pooled<int> values = pool.Rent(1_024, 0);

NativeOwnerStatistics snapshot = pool.GetStatistics();
Console.WriteLine(
    $"requested={snapshot.RequestedBytes}, "
    + $"retained={snapshot.RetainedBytes}, "
    + $"segments={snapshot.SegmentCount}");

nuint freed = pool.TrimRetainedMemory();

NativeMemoryStatistics process = NativeMemoryDiagnostics.Snapshot();
Console.WriteLine($"outstanding={process.OutstandingNativeBytes}");
```

Take the snapshot at a maintenance point, not inside a hot loop; some owners scan their storage or take a lock. `NativeMemoryDiagnostics.Snapshot()` counts the native memory NAM allocated across all owners; buffers owned by a provider are excluded.

Memory the owner holds but no live lease uses is *retained* for later rents. `TrimRetainedMemory()` on a pool, arena or concurrent owner frees the idle part and returns the number of bytes freed. Live leases stay valid. A region has no trim; it ends every lease when its scope ends and keeps the storage for reuse unless `NativeMemoryReturn.Free` was passed.

## Diagnostics

Builder diagnostics are listed in the builder section. Every ID links to its page.

| ID | What it catches | How to fix |
|---|---|---|
| [NAM1001](diagnostics/NAM1001.md) | Pool, arena or region assigned to another variable, stored, or passed to a call the analyzer cannot read | Keep one owner variable; pass it only to helpers in your source |
| [NAM1002](diagnostics/NAM1002.md) | Lease copied | Use the original lease |
| [NAM1003](diagnostics/NAM1003.md) | Lease not disposed on some exit | Use a `using` declaration |
| [NAM1004](diagnostics/NAM1004.md) | Lease used after `Dispose`, `Reset` or a generation return | Rent again |
| [NAM1005](diagnostics/NAM1005.md) | Lifecycle call on an owner declared with `using` | Dispose in `try`/`finally` instead |
| [NAM1006](diagnostics/NAM1006.md) | Region not created as a `using` resource | `using NativeRegion region = new();` |
| [NAM1007](diagnostics/NAM1007.md) | `Reset` or memory return while a lease is live | End every lease first |
| [NAM1009](diagnostics/NAM1009.md) | Call the owner's current state does not allow, such as `Rent` after `ReturnMemoryToNativeMemory()` | Call `LeaseFromMemory()` first |
| [NAM1011](diagnostics/NAM1011.md) | Lease held across `await` or `yield` | Finish the work before the `await` |
| [NAM1012](diagnostics/NAM1012.md) | Region lease leaves its region | Keep it inside the `using` scope, or copy the data out |
| [NAM1013](diagnostics/NAM1013.md) | Pooled lease returned or stored | Do the work in the method that rented it |
| [NAM1015](diagnostics/NAM1015.md) | Field pool created with `NativeMemoryReturn.Free` and no `Dispose` for it | Dispose the field in the class's `Dispose` |
| [NAM1016](diagnostics/NAM1016.md) | Lease passed to a call the analyzer cannot read | Use `Access`, `Read` or `Process` |
| [NAM1017](diagnostics/NAM1017.md) | Warning: garbage-collector return or release with a live lease | End leases first |
| [NAM1018](diagnostics/NAM1018.md) | `ScratchScoped` result not assigned to a `scoped` local | `scoped ArenaLease<T> x = arena.ScratchScoped<T>(...)` |
| [NAM1019](diagnostics/NAM1019.md) | Warning: `Scratch` result assigned to a `scoped` local | Use `ScratchScoped` |
| [NAM1020](diagnostics/NAM1020.md) | Warning: scoped lease without `RecycleScoped()` on every exit | Call it in `finally` |
| [NAM1021](diagnostics/NAM1021.md) | Transfer copied | `Move(ref source)` |
| [NAM1022](diagnostics/NAM1022.md) | Transfer used after `Dispose` or `Move` | Use the new owner |
| [NAM1023](diagnostics/NAM1023.md) | `Move` of a transfer that was already moved or disposed | Move once |
| [NAM1024](diagnostics/NAM1024.md) | Transfer view or span leaves its callback | Use it inside the callback; declare helper parameters `scoped` |
| [NAM1025](diagnostics/NAM1025.md) | Transfer not disposed or moved on every path | `try`/`finally` with `Dispose` |
| [NAM1026](diagnostics/NAM1026.md) | Transfer not assigned to a local | Assign to a local, then `Move` |
| [NAM1027](diagnostics/NAM1027.md) | Transfer parameter declared `in`, `ref` or `out` | Take it by value |
| [NAM1035](diagnostics/NAM1035.md) | Warning: managed allocation inside a region's scope | Allocate before the region |
| [NAM1036](diagnostics/NAM1036.md) | Workspace copied | Keep one owner |
| [NAM1037](diagnostics/NAM1037.md) | Workspace used after `Dispose` | Create a new one |
| [NAM1038](diagnostics/NAM1038.md) | Workspace not disposed on every exit | Use a `using` declaration |
| [NAM1039](diagnostics/NAM1039.md) | Workspace not created directly into a local | `using NativeWorkspace<T> w = new(...);` |
| [NAM1040](diagnostics/NAM1040.md) | Workspace helper parameter not `scoped in` | `scoped in NativeWorkspace<T>` |
| [NAM1047](diagnostics/NAM1047.md) | `ConcurrentPooled<T>.Process` callback captures variables or is not passed directly | Pass a `static` lambda; put its data in `state` |
| [NAM1048](diagnostics/NAM1048.md) | Native owner inside the `Process` state | Keep owners out of `state` |
| [NAM1049](diagnostics/NAM1049.md) | `Process` state stored, boxed or returned | Use it inside the callback |
| [NAM1051](diagnostics/NAM1051.md) | Span used after its lease ended | Take a new span from a live lease |
| [NAM9001](diagnostics/NAM9001.md) | The package's analyzer is missing | Restore the analyzer from the package |

## Terms

- **Owner**: an object that holds native memory (pool, region, arena, workspace, builder).
- **Lease**: a handle to one block of an owner's memory. Pool, region, arena and concurrent leases are `ref struct`s.
- **View**: the bounded handle that `Access` and `Read` give a callback; it is valid only inside the callback.
- **Published range**: the elements of a workspace that `Initialize` made readable by `Access` and `Read`.
- **Scoped lease**: an arena lease from `ScratchScoped`; `RecycleScoped()` ends it.
- **Generation**: the period between `LeaseFromMemory()` and the call that returns the owner's memory; leases do not outlive it.
- **Retained memory**: memory an owner keeps for later rents; `TrimRetainedMemory()` frees it.
