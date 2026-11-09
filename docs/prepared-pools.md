# Prepared fixed-shape pools

Use `NativePreparedPool<T>` when you know how many buffers of one bounded size are live at the same time. The pool allocates all of them up front, so within retained capacity, renting and returning never allocates. It is not a general heap and does not size itself.

```csharp
using Supprocom.NativeAllocationManagement;

NativeMemoryBudget budget = new(64 * 4096);
using NativePreparedPool<byte> pool = new(new NativePoolPreparation(64, 4096, 16), budget);

if (pool.TryRent(4096, static writer => writer.Fill(42),
        out PreparedPooled<byte> lease, out NativePoolExhaustionReason reason))
{
    try
    {
        byte first = lease.Read(static view => view[0]);
    }
    finally
    {
        lease.Dispose();
    }
}
else
{
    // No lease was published and the initializer did not run. Handle `reason` here.
}
```

The steps are: prepare, rent, use, return, dispose the pool.

Analyzer errors in this page:

| ID | What it catches | How to fix |
|---|---|---|
| [NAM1050](diagnostics/NAM1050.md) | Using a lease without checking that `TryRent` returned `true` | Use it only inside `if (TryRent(...))` or after an early-return guard |
| [NAM1003](diagnostics/NAM1003.md) | A lease that is not disposed on every path | `Dispose()` in a `finally` or use `using` |
| [NAM1004](diagnostics/NAM1004.md) | Using a lease after `Move` or after it was returned | Use the destination, not the source |


## 1. Prepare

`new NativePoolPreparation(slotCount, slotCapacity, slotsPerPage)` describes the pool:

| Argument | Meaning |
|---|---|
| `slotCount` | How many leases can be live at once. |
| `slotCapacity` | Elements per slot. |
| `slotsPerPage` | Slots per native allocation. |

The example above is 64 slots of 4096 bytes in 4 pages. Each page is one native allocation. A shorter last page holds the remaining slots. Slots are packed at the CLR element stride, with no cache-line padding and no SIMD-alignment guarantee. The pool asks the budget for all pages before it allocates any, and frees what it got if one allocation fails. Its charge is exactly the slot bytes. If the budget cannot hold them, the constructor throws `NativeMemoryBudgetExceededException` and the budget's committed and reserved bytes stay at 0. Pass `null` as the budget to run without a limit.

The pool belongs to the thread that created it. Using it from another thread throws `NativeAllocationStateException`.

## 2. Rent

`TryRent(length, initializer, out lease, out reason)` returns `false` for exactly two reasons:

| `NativePoolExhaustionReason` | Meaning |
|---|---|
| `ShapeExceeded` | `length` is larger than `slotCapacity`. |
| `NoAvailableSlot` | Every slot is in use. |

Nothing runs on `false`: your initializer is not called and no lease exists. Everything else is a bug and throws: a negative length, a `null` initializer, the wrong thread, a closed pool, an initializer that throws, or one that does not write the whole range. A slot is returned to the pool if the initializer fails.

`Rent` does the same and throws `InvalidOperationException` when the pool is full. Use `TryRent` when full is normal. The initializer can be a value instead of a callback: `pool.Rent(4096, (byte)42)`.

Only a `true` result gives you a lease. The analyzer enforces that:

```csharp
// WRONG, does not compile: NAM1050 (lease used without checking the result), NAM1003 (never returned on the failed path)
pool.TryRent(4, static writer => writer.Fill(42), out PreparedPooled<byte> lease, out _);
lease.Dispose();
```

A direct `if (!pool.TryRent(...)) return;` guard is also accepted. A boolean you store and test later is not.

## 3. Use and return

`PreparedPooled<T>` is a `ref struct`: keep it in a local of the method that rented it. `Read`, `Access` and `AsSpan` work as for an ordinary pooled lease. `Dispose()` returns the slot.

```csharp
// WRONG, does not compile: NAM1003 (slot never returned)
PreparedPooled<byte> leaked = pool.Rent(4, (byte)42);
```

Dispose the pool only after every lease has been returned. Disposing it with a live lease throws `InvalidOperationException`. Disposing the pool frees its memory on the spot.

## Move a lease

`PreparedPooled<T>.Move(ref source)` gives the slot to a new variable without copying. `source` becomes unusable. The destination stays on the pool's thread. To send the data to another thread, use a [`NativeTransfer<T>`](getting-started.md#transfer-ownership-across-threads).

```csharp
PreparedPooled<byte> source = pool.Rent(4096, (byte)42);
using PreparedPooled<byte> destination = PreparedPooled<byte>.Move(ref source);
byte moved = destination.Read(static view => view[0]);
```

```csharp
// WRONG, does not compile: NAM1004 (source was moved)
_ = source.Read(static view => view[0]);
```

If `Move` fails (the source is stale or default, the thread is wrong, or a borrow is active), it throws and `source` is left unchanged, so you can still return it. A `using` variable is read-only and cannot be passed by `ref`; move from an ordinary local and put the destination in the `using`.

## Pages and trimming

Returning a slot makes it available again; it does not free memory. One live slot keeps its whole page allocated.

`TrimRetainedMemory()` and `TrimRetainedMemoryByBytes(n)` free only pages that have no live slot. They can free more than you asked for, because a page goes whole. A trimmed page never comes back. To get capacity again, create a new pool.

Small leases still use a whole slot, and a slot keeps its page. If most leases are small or short-lived, measure how many pages stay allocated.

## Prepared snapshot inventory

`pool.CapturePreparedSnapshot()` returns a value copy. Call it on the pool's thread. It allocates nothing and resets nothing.

| Field | Meaning |
|---|---|
| `OwnerId`, `Lifecycle` | Identity and state of the pool. |
| `Preparation` | The `NativePoolPreparation` you passed. |
| `RetainedPageCount`, `RetainedSlotCount` | Pages and slots still allocated. Trim lowers them. |
| `OccupiedSlotCount` | Slots in use, including one being initialized. |
| `PeakOccupiedSlotCount` | Highest `OccupiedSlotCount`, including failed initializations. |
| `AvailableSlotCount` | Retained slots minus occupied ones. |
| `RetainedBytes`, `PeakRetainedBytes` | Native bytes held now, and the highest so far. Not RSS. |
| `SuccessfulRentCount` | Rents that returned a lease. |
| `RejectedShapeCount`, `RejectedFullCount` | `ShapeExceeded` and `NoAvailableSlot` refusals. |
| `InitializerFailureCount` | Initializers that threw or did not write the range. |
| `ManagedBankBytes` | Managed memory used by the pool's slot and page tables. |
| `UnusedSlotBytes` | Payload capacity of free retained slots. |
| `HistoryOverflowed` | A count reached `long.MaxValue`; counts are lower bounds. |

With a budget that has [tracing](memory-budgets.md#owner-identities-and-optional-tracing) enabled, the pool emits `PageAcquired`, `Prepared`, `Trimmed` and `Released` events, and `Moved` for each `Move`.

Renting, moving, reading, returning, snapshots and refusals allocate no managed or native memory while the pool keeps its pages. Your callbacks can still allocate. There is no refill, and no promise about page faults or other runtime work.

## Terms

| Term | Meaning |
|---|---|
| Slot | One fixed-size buffer in the pool. |
| Page | A group of slots allocated together. |
| Lease | The right to use one rented slot, until you dispose it. |
| Trim | Freeing pages that have no live slot. |
