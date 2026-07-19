# Supprocom.NativeAllocationManagement

Performance-sensitive C# often turns memory control into ceremony: rent managed buffers,
return them, force a collection, and hope the garbage collector's machine spirit releases
the backing storage at the right time. Those rituals can reduce allocations, but they do
not give the program ownership of the pool or a deterministic boundary for its memory.

`Supprocom.NativeAllocationManagement` replaces that ceremony with direct ownership of
native memory. `NativePool<T>` owns and reuses typed native slabs, while
`NativeRegion` owns heterogeneous storage for one lexical scope. The caller can
invalidate a complete generation and choose immediate physical release or deferred
cleanup. Runtime generation and operation checks prevent stale access, while the bundled
analyzer borrow-checks owners and derived handles and points to the ownership path or
scoped borrow behind a lifetime error. `Pooled<T>` and `Local<T>` expose storage only
through bounded operations, so direct control does not require persistent pointers,
unbounded spans, or garbage-collection superstition.

Install version `0.1.1` with a normal package reference. The package supplies both the
runtime assembly and its required ownership analyzer.

```xml
<PackageReference Include="Supprocom.NativeAllocationManagement" Version="0.1.1" />
```

The [getting-started guide][getting-started] explains pool leases, heterogeneous regions,
delayed activation, generation reuse, and the ownership rules with complete examples.

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
segments together. The package targets .NET 10 and supports `unmanaged` element types
only.

This project is licensed under the GNU Affero General Public License, version 3 only.
The complete terms and project-specific source offer are in [LICENSE.md](LICENSE.md).

[getting-started]:
  https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/getting-started.md
