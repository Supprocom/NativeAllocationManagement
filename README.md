# Supprocom.NativeAllocationManagement

`Supprocom.NativeAllocationManagement` provides safe C# owners for contiguous unmanaged
native storage. `NativePool<T>` reuses typed native slabs across leases, while
`NativeRegion` provides heterogeneous lexical storage. `Pooled<T>` and `Local<T>` are
readonly ref-struct handles that retain owner and generation identity without exposing a
revocable pointer or an unbounded native-backed span.

The package includes its Roslyn ownership analyzer and build-transitive enforcement in the
same installation. Normal access is bounded to synchronous callbacks:

```csharp
using NativePool<int> pool = new(initialCapacity: 1024);
using Pooled<int> values = pool.Rent(128);

values.Access(static span =>
{
    for (int index = 0; index < span.Length; index++)
    {
        span[index] = index;
    }
});
```

Declaration leasing can be deferred when a pool or region must be constructed before its
first generation is published. The pool remains a normal using declaration. A region must
be the direct resource of a braced using statement, and its typed values are obtained with
`Lease<T>`:

```csharp
using NativePool<int> pool = new(doNotLeaseOnDeclaration: true);
pool.LeaseFromMemory();
using Pooled<int> values = pool.Rent(128);

using (NativeRegion region = new(doNotLeaseOnDeclaration: true))
{
    region.LeaseFromMemory();
    Local<int> local = region.Lease<int>(128);
    local.Access(static span => span.Clear());
}
```

The default constructor behavior is active immediately. With
`doNotLeaseOnDeclaration: true`, construction is allocation-free until
`LeaseFromMemory()` succeeds; `Rent`, `Lease`, and both whole-generation return policies
reject use before activation. Disposing an unleased owner is valid and terminal.

The default disposal policy defers physical native cleanup to a finalizable generation
owner. `new NativePool<T>(returnOnDispose: NativeReturn.ToNativeMemory)` selects
deterministic release. A manually managed pool can end one generation and start a later
one with `ReturnToNativeMemory()` or `ReturnToGarbageCollector()` followed by
`LeaseFromMemory()`; old `Pooled<T>` values remain stale and are never revived.

`NativeRegion` is a lexical owner and must be the direct resource of an explicit braced
using statement. It supports mixed unmanaged element types and releases all of its
segments together. The package targets .NET 10 and the first release intentionally
supports `unmanaged` element types only.

This project is licensed under the GNU Affero General Public License, version 3 only.
