# Getting started

`Supprocom.NativeAllocationManagement` provides bounded access to contiguous unmanaged
storage without giving application code a pointer or a span that can outlive its owner.
The NuGet package includes both the runtime library and a Roslyn analyzer. The analyzer
enforces the ownership rules at build time, while the runtime validates the owner,
generation, allocation identity, and active-operation gate whenever native storage is
touched.

The package targets .NET 10 and supports unmanaged element types. Add the package to a
project with a normal package reference. Analyzer assets must remain enabled because the
build-transitive package target rejects installations that remove the bundled analyzer.

```xml
<ItemGroup>
  <PackageReference Include="Supprocom.NativeAllocationManagement" Version="0.1.2" />
</ItemGroup>
```

## Renting typed pool storage

`NativePool<T>` owns reusable slabs for one unmanaged element type. `Rent` creates a
generation-bound `Pooled<T>` value. A using declaration is the simplest ownership form
because generated cleanup returns the lease before it disposes the pool.

```csharp
using Supprocom.NativeAllocationManagement;

using NativePool<int> pool = new(initialCapacity: 1_024);
using Pooled<int> values = pool.Rent(128);

values.Access(static span =>
{
    for (int index = 0; index < span.Length; index++)
    {
        span[index] = index * 2;
    }
});

int total = values.Read(static span =>
{
    int result = 0;
    foreach (int value in span)
    {
        result += value;
    }

    return result;
});
```

`Access` exposes a scoped mutable span only for the synchronous callback. `Read` exposes
a scoped read-only span and returns an independently managed result. Indexing, `Clear`,
`CopyFrom`, and `CopyTo` use the same runtime operation gate. A callback must not return
or dispose its owner because the callback holds an active native borrow until it exits.

When a pooled lease is disposed, its logical range is cleared before its slab becomes
available to a later renter. The pool chooses the smallest available slab whose capacity
can satisfy the next request. A zero-length rent still has generation and allocation
identity, so it follows the same stale-value rules even though it owns no native bytes.

## Leasing heterogeneous region storage

`NativeRegion` is a heterogeneous lexical allocator. It must be the direct resource of
an explicit braced using statement. Using declarations, ordinary locals, aliases,
factory-returned regions, and nested active regions are rejected by the analyzer because
they do not establish the required lexical boundary.

```csharp
using Supprocom.NativeAllocationManagement;

using (NativeRegion region = new(
    preAllocateBytes: 4_096,
    returnOnDispose: NativeReturn.ToNativeMemory))
{
    Local<int> identifiers = region.Lease<int>(64);
    Local<double> weights = region.Lease<double>(64);

    identifiers.Access(static span =>
    {
        for (int index = 0; index < span.Length; index++)
        {
            span[index] = index + 1;
        }
    });

    weights.Access(static span => span.Fill(0.5));

    double firstWeight = weights.Read(static span => span[0]);
    identifiers[0] = checked((int)(firstWeight * 100));
}
```

A region may lease several unmanaged types because it aligns each allocation inside a
shared chain of native segments. `Local<T>` values do not have individual disposal. They
remain valid only inside the region body, and leaving that body invalidates every local
allocation together.

## Deferring the first native allocation

An owner normally publishes an active generation during construction. Set
`doNotLeaseOnDeclaration` when construction and native allocation must occur at different
points. The owner begins in the `Unleased` lifecycle, and `Rent`, region `Lease`, and both
whole-generation return operations fail until `LeaseFromMemory` succeeds.

```csharp
using Supprocom.NativeAllocationManagement;

using NativePool<byte> pool = new(
    initialCapacity: 4_096,
    doNotLeaseOnDeclaration: true);

pool.LeaseFromMemory();
using Pooled<byte> buffer = pool.Rent(4_096);
buffer.Access(static span => span.Fill(0x2A));
```

The initial reservation is applied as part of the activation transaction. If allocation
fails, no partial generation is published and the owner remains unleased so activation
can be retried. Disposing an owner that was never activated is valid, terminal, and does
not allocate native memory.

A delayed region uses the same activation rule while retaining its required braced
lexical form.

```csharp
using Supprocom.NativeAllocationManagement;

using (NativeRegion region = new(
    preAllocateBytes: 4_096,
    doNotLeaseOnDeclaration: true))
{
    region.LeaseFromMemory();
    Local<long> counters = region.Lease<long>(32);
    counters.Clear();
}
```

## Reusing pool generations

A manually managed pool can end one generation and later publish another. Every lease
from the old generation remains permanently stale; `LeaseFromMemory` never revives an old
`Pooled<T>` value. Dispose individual leases when their slabs should be reused, or use a
whole-generation return to invalidate every remaining lease from that generation at once.

```csharp
using Supprocom.NativeAllocationManagement;

NativePool<int> pool = new(
    initialCapacity: 256,
    returnOnDispose: NativeReturn.ToNativeMemory);

try
{
    Pooled<int> first = pool.Rent(64);
    first.Access(static span => span.Fill(1));
    first.Dispose();

    pool.ReturnToNativeMemory();
    pool.LeaseFromMemory();

    Pooled<int> second = pool.Rent(64);
    second.Access(static span => span.Fill(2));
    second.Dispose();
}
finally
{
    pool.Dispose();
}
```

`ReturnToNativeMemory` releases the current generation synchronously after the safety
gate succeeds. `ReturnToGarbageCollector` invalidates a pool generation immediately and
detaches its segments to a finalizable generation owner, so physical release occurs later
without forcing a collection. `Dispose` applies the policy selected by `returnOnDispose`.
A region is single-generation and cannot be leased again after either whole-generation
return operation.

An entered pool operation changes the distinction between those policies. Immediate
native release and disposal throw `NativeAllocationInUseException`, restore the active
state, and leave every lease untouched. A garbage-collected pool return succeeds instead:
the entered operation token retains the detached generation owner and may finish, while
the generation becomes returned immediately and rejects every later handle operation.
Regions retain the strict operation gate for both return policies.

The following return therefore produces `NAM1017` while remaining memory-safe. The
callback has already entered, so its span remains valid until it exits. The old
`Pooled<int>` value is stale as soon as the return succeeds.

```csharp
using Supprocom.NativeAllocationManagement;

NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
try
{
    Pooled<int> values = pool.Rent(4);
    values.Access(span =>
    {
        pool.ReturnToGarbageCollector();
        span.Fill(42);
    });

    pool.LeaseFromMemory();
    Pooled<int> current = pool.Rent(4);
    current.Dispose();
}
finally
{
    pool.Dispose();
}
```

The analyzer emits one ordinary `NAM1017` warning for every live `Pooled<T>` value when
the current generation is returned to the garbage collector. If the value is actively
borrowed, the diagnostic ownership path also names the scoped callback that retains the
detached storage. The warning follows the consuming project's normal compiler policy;
`TreatWarningsAsErrors` promotes it to an error without package-specific exceptions.

## Working with the ownership analyzer

The analyzer treats unsafe ownership violations as build errors. A native owner has one
binding, a derived handle cannot be copied to another owner-shaped local, a pooled value
cannot be passed through an unknown retaining call, and a region local cannot cross its
region boundary. Active handles also cannot cross `await` or `yield`. `NAM1017` is the
deliberate warning-level exception because a garbage-collected pool return invalidates
old values without freeing an already entered operation's storage.

A helper can carry a pool lifecycle effect when its source is available and every reachable
path proves the same direct return, lease, or disposal effect on the matching owner
parameter. Ambiguous helpers and precompiled helpers remain unknown. Put ordinary data
processing inside `Access` or `Read`, or copy the required result into managed storage,
instead of passing `Pooled<T>` or `Local<T>` through an API that could retain it.

The runtime still validates lifecycle and identity when diagnostics are suppressed or a
separately compiled caller bypasses analysis. That defensive layer prevents stale handles
from resolving native addresses, but suppressing the analyzer removes the source-level
single-owner and lexical-lifetime guarantees and is not a supported usage mode.
