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
scoped borrow behind a lifetime diagnostic. `Pooled<T>` and `Local<T>` expose storage only
through bounded operations, so direct control does not require persistent pointers,
unbounded spans, or garbage-collection superstition.

Install version `0.1.2` with a normal package reference. The package supplies both the
runtime assembly and its required ownership analyzer.

```xml
<PackageReference Include="Supprocom.NativeAllocationManagement" Version="0.1.2" />
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

`ReturnToGarbageCollector()` closes the current pool generation even when a bounded
operation has already entered. That operation token keeps the detached segments alive
until the operation exits, while every later operation through an old `Pooled<T>` value
fails as stale. Both whole-generation return policies use one analyzer liveness query.
It reports each live root/reference, active callback, alias, escape, or unknown-retention
path as error `NAM1007` for `ReturnToNativeMemory()` and ordinary warning `NAM1017` for
`ReturnToGarbageCollector()`, with equivalent provenance. A plain stale root does not
retain detached storage; the entered operation token is what retains the detached owner
until its callback exits. A consumer's normal warning policy applies, so
`TreatWarningsAsErrors` promotes `NAM1017` to an error.

## Ownership diagnostics

The analyzer uses errors for ownership violations that cannot execute safely, including
escaping handles and every shared liveness finding at immediate native return. The
deferred-release case remains an ordinary warning because its policy can detach the
generation while an entered operation token retains the native owner until the callback
finishes. `ReturnToNativeMemory()` and `Dispose()` remain hard runtime operation gates:
they reject an entered operation rather than freeing storage beneath it.

`NativeRegion` is a lexical owner and must be the direct resource of an explicit braced
using statement. It supports mixed unmanaged element types and releases all of its
segments together. The package targets .NET 10 and supports `unmanaged` element types
only.

This project is licensed under the GNU Affero General Public License, version 3 only.
The complete terms and project-specific source offer are in [LICENSE.md](LICENSE.md).

[getting-started]:
  https://github.com/Supprocom/NativeAllocationManagement/blob/main/docs/getting-started.md
