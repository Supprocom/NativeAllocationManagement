# Supprocom.NativeAllocationManagement

`Supprocom.NativeAllocationManagement` provides safe C# owners for contiguous unmanaged
native storage. `NativePool<T>` reuses typed native slabs across leases, while
`NativeRegion` provides heterogeneous bump allocation. `Pooled<T>` and `Local<T>` are
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

The default disposal policy defers physical native cleanup to a finalizable generation
owner. `new NativePool<T>(returnOnDispose: NativeReturn.ToNativeMemory)` selects
deterministic release. A manually managed pool can end one generation and start a later
one with `ReturnToNativeMemory()` or `ReturnToGarbageCollector()` followed by
`LeaseFromMemory()`; old `Pooled<T>` values remain stale and are never revived.

`NativeRegion` is a lexical owner and must be directly declared through `using`. It
supports mixed unmanaged element types and releases all of its segments together. The
package targets .NET 10 and the first release intentionally supports `unmanaged` element
types only.

This project is licensed under the GNU Affero General Public License, version 3 only.
