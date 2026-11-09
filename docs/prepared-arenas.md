# Prepared arenas

A prepared arena has two lanes of a fixed size, the *ordinary* lane and the *scoped* lane. Both are allocated when you create it. Scratch ranges are carved from them without allocating again, and when a lane is full you get `false` instead of growth.

```csharp
using Supprocom.NativeAllocationManagement;

NativeMemoryBudget budget = new(4096);
using NativeArena arena = new(new NativeArenaPreparation(ordinaryBytes: 1024, scopedBytes: 1024), budget);

if (arena.TryScratch<int>(128, static writer => writer.Fill(42), out ArenaLease<int> values))
{
    int last = values.Read(static view => view[127]);
}
else
{
    // The lane is full. Decide what your application does.
}
```

The arena belongs to the thread that created it. Using it from another thread throws `NativeAllocationStateException`.

## 1. Create

`new NativeArenaPreparation(ordinaryBytes, scopedBytes)` sets the usable bytes of each lane. Zero is allowed. Alignment between ranges uses up usable bytes.

The constructor asks the budget for both lanes before it allocates either. The budget is charged the usable bytes plus NAM's headers and allocator padding (2176 bytes for the example above). If the budget cannot hold both, the constructor throws `NativeMemoryBudgetExceededException` and the budget's committed and reserved bytes stay at 0.

## 2. Scratch

| Call | Lane | When the lane is full |
|---|---|---|
| `TryScratch<T>(length, initializer, out lease)` | ordinary | returns `false` |
| `TryScratchScoped<T>(length, initializer, out lease)` | scoped | returns `false` |
| `Scratch<T>(length, initializer)` | ordinary | throws `InvalidOperationException` |
| `ScratchScoped<T>(length, initializer)` | scoped | throws `InvalidOperationException` |

`false` is returned before the initializer runs, with a default lease. The initializer must write the whole range, as on any arena. A bad length, the wrong thread, a closed arena and an incomplete initializer throw. Zero-length ranges are valid.

Use the lease only when `Try...` returned `true`:

```csharp
// WRONG, does not compile: NAM1050 (lease used without checking the result)
arena.TryScratch<int>(4, static writer => writer.Fill(1), out ArenaLease<int> unused);
_ = unused.Read(static view => view[0]);
```

## 3. Reset and recycle

| Call | Frees for reuse | Invalidates |
|---|---|---|
| `Reset()` | both lanes | every lease |
| `RecycleScoped()` | the scoped lane | scoped leases |

Neither frees native memory; the lanes are reused. Both throw if a borrow is active.

```csharp
// WRONG, does not compile: NAM1007 (Reset while `old` is live), NAM1004 (`old` used after Reset)
ArenaLease<int> old = arena.Scratch<int>(4, 1);
arena.Reset();
_ = old.Read(static view => view[0]);
```

Analyzer errors in this page:

| ID | What it catches | How to fix |
|---|---|---|
| [NAM1050](diagnostics/NAM1050.md) | Using a lease without checking that `TryScratch` returned `true` | Use it only inside the `if` |
| [NAM1007](diagnostics/NAM1007.md) | `Reset` while a lease is still live | End the lease's scope before `Reset` |
| [NAM1004](diagnostics/NAM1004.md) | Using a lease after `Reset` or `RecycleScoped` | Make a new lease |

Scoped leases must end before `RecycleScoped()`. Declare the `Try` output inside the batch, and recycle in a `finally`.

```csharp
try
{
    if (arena.TryScratchScoped<int>(64, static writer => writer.Fill(7), out ArenaLease<int> temp))
    {
        int first = temp.Read(static view => view[0]);
    }
}
finally
{
    arena.RecycleScoped();
}
```

## 4. Trim and dispose

`TrimRetainedMemory()` and `TrimRetainedMemoryByBytes(n)` free lanes that hold no live range, and may free more than `n`. A trimmed lane never refills: after trimming, `TryScratch` returns `false` until you create a new arena. `Dispose()` frees the memory on the spot.

## Snapshot

`arena.CapturePreparedSnapshot()` returns a value copy. Call it on the arena's thread. It allocates nothing and resets nothing.

| Field | Meaning |
|---|---|
| `OrdinaryUsedBytes`, `ScopedUsedBytes` | Bytes used per lane, including alignment and a range being initialized. |
| `OrdinaryAvailableBytes`, `ScopedAvailableBytes` | Bytes left per lane. |
| `PeakOrdinaryUsedBytes`, `PeakScopedUsedBytes` | Highest use so far, including ranges that were rolled back. |
| `RetainedBytes`, `PeakRetainedBytes` | Native bytes owned now, and the highest so far. |
| `ActiveBorrowedBytes`, `RetainedBorrowedBytes` | For [mapped arenas](fast-arena-integration.md): usable and total external bytes. |
| `SuccessfulScratchCount` | Ranges published. |
| `RejectedCapacityCount` | `false` results, counted before the initializer runs. |
| `InitializerFailureCount` | Initializers that threw or did not write the range. |
| `HistoryOverflowed` | A count reached `long.MaxValue`; counts are lower bounds. |

A prepared arena has no per-lease table: its managed cost is the owner object and your initializer. Compare it with equivalent prepared managed storage including creation and teardown, not only the allocation count.

## Terms

| Term | Meaning |
|---|---|
| Lane | One fixed block of the arena. |
| Ordinary lane | Ranges here stay valid until `Reset()`. |
| Scoped lane | Ranges here stay valid until `RecycleScoped()` or `Reset()`. |
| Lease | A range inside a lane, valid until it is reset or recycled. |
