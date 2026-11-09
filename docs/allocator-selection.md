# Choose an allocator

Start with managed storage: arrays, spans and `ArrayPool<T>`. Move to NAM when you can show it removes a cost the managed version cannot, for the same output, memory cap and thread use. Zero GC allocations alone is not a reason.

```csharp
using Supprocom.NativeAllocationManagement;

// Managed first:
float[] managed = System.Buffers.ArrayPool<float>.Shared.Rent(1_024);
System.Buffers.ArrayPool<float>.Shared.Return(managed);

// NAM, when the managed version is measurably worse:
using Pooled<float> native = NativePool<float>.Shared.Rent(1_024, 0f);
```

## Pick by need

| You need | Use | Guide |
|---|---|---|
| Scratch ranges of several types inside one block of code | `NativeRegion` | [Group allocations in a region](getting-started.md#group-allocations-in-a-region) |
| Same-type buffers of varying size on one thread | `NativePool<T>` | [Pool buffers of one element type](getting-started.md#pool-buffers-of-one-element-type) |
| Fixed-shape buffers on one thread | `NativePreparedPool<T>` | [Prepared pools](prepared-pools.md) |
| Ranges of several types that are reset together on one thread | `NativeArena` | [Free many allocations at once with an arena](getting-started.md#free-many-allocations-at-once-with-an-arena), [Prepared arenas](prepared-arenas.md) |
| The same, shared by several threads | `NativeConcurrentPool<T>`, `NativeConcurrentArena` | [Share an owner between threads](getting-started.md#share-an-owner-between-threads), [Owner generations](getting-started.md#owner-generations) |
| Output of unknown size as one contiguous result | `NativeBuilder<T>` | [Build a growable native sequence](getting-started.md#build-a-growable-native-sequence) |
| Mutable state that a worker keeps and reuses | `NativeWorkspace<T>` | [Reuse one buffer for every batch](getting-started.md#reuse-one-buffer-for-every-batch) |
| One initialized block that moves between owners or threads | `NativeTransfer<T>` | [Unique ownership](unique-ownership.md) |
| One block read by several independent owners | `NativeShared<T>`, `NativeWeak<T>` | [Immutable sharing](immutable-sharing.md) |
| Several arrays of different types with one lifetime | `NativeLayout` | [Typed layouts](typed-layouts.md) |
| A cap on total native bytes | `NativeMemoryBudget` | [Memory budgets](memory-budgets.md), [Application admission](application-admission.md) |

## Rules of thumb

- One owner is the default. Share only when copying or coordinating costs more than the sharing setup.
- A small slice of a shared block keeps the whole block alive. A layout owner keeps the whole block alive.
- Group fields in one layout only if they live and die together.
- Concurrent owners cost coordination. Choose one for real multi-thread use, not to fill an API gap.
- A budget counts the native bytes NAM requests. It is not process memory (RSS) and not a container limit.

When you compare NAM with managed code, include setup, spare capacity, handoff and cleanup on both sides, and compare against an efficient managed version, not one that copies or allocates needlessly.

Every rule the analyzer enforces has a page in the [diagnostic index](diagnostics/README.md).
