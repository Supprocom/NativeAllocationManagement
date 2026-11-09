# Immutable sharing

`NativeShared<T>` (`T` must be unmanaged) lets several owners read one block of native data without copying it. The data is read-only. Use `NativeTransfer<T>` ([Unique ownership](unique-ownership.md)) unless several owners really need the same block at once.

```csharp
using Supprocom.NativeAllocationManagement;

NativeTransfer<int>? source = NativeTransfer<int>.Create(new[] { 1, 2, 3, 4 });
using NativeShared<int> first = NativeShared<int>.Create(
    ref source, new NativeSharingPreparation(strongBindingCount: 2, weakBindingCount: 1));

if (first.TryShare(out NativeShared<int> second, out NativeSharingExhaustionReason reason))
{
    try { Console.WriteLine(second.Read(static view => view.AsSpan().Length)); }
    finally { second.Dispose(); }
}
```

The steps are always the same: prepare and convert, share, read, dispose. The examples below continue with `first` and `source` from this one.

## 1. Prepare and convert

`NativeShared<T>.Create(ref source, preparation)` consumes a `NativeTransfer<T>` and returns the first owner. `NativeSharingPreparation` fixes, once, how many owners and observers can exist at the same time:

| Argument | Meaning |
|---|---|
| `strongBindingCount` | most owners at once, including the first (at least 1) |
| `weakBindingCount` | most weak observers at once (0 or more) |

The counts never grow. If `Create` fails before it consumes the source (for example `default` preparation throws `ArgumentOutOfRangeException`), you still own the source, so keep a `finally { source?.Dispose(); }` around it. If it fails after, the data is freed.

## 2. Share

Assignment never creates an owner. Ask for one:

| Call | Gives you |
|---|---|
| `TryShare(out share, out reason)` | another owner of the whole block |
| `TrySlice(offset, length, out slice, out reason)` | an owner of a sub-range |
| `TryDowngrade(out weak, out reason)` | a weak observer (`NativeWeak<T>`) |
| `weak.TryUpgrade(out strong, out reason)` | an owner again, if one still exists |

Each `Try` call returns `false` when it has nothing to give, and `reason` says why:

| `NativeSharingExhaustionReason` | Meaning |
|---|---|
| `NoStrongBinding` | all prepared owner slots are in use |
| `NoWeakBinding` | all prepared observer slots are in use |
| `ExpiredPayload` | the last owner is gone, so a weak observer cannot upgrade |

Use an output only inside the branch where the call returned `true`. Every owner you get needs its own `Dispose()`.

```csharp
// WRONG, does not compile: NAM1050 (output used without checking the result) and NAM1025
first.TryShare(out NativeShared<int> extra, out _);
extra.Dispose();

// WRONG, does not compile: NAM1025 (slice is never disposed)
if (first.TrySlice(0, 2, out NativeShared<int> slice, out _)) { }
```

An invalid offset or length throws `ArgumentOutOfRangeException`.

## 3. Read

`Read` and `Access` run a callback with a read-only view. The view is valid only inside the callback, and the callback's result cannot be a `Span`.

```csharp
if (first.TrySlice(offset: 1, length: 2, out NativeShared<int> middle, out _))
{
    try { Console.WriteLine(middle.Read(static view => view.AsSpan()[0])); }   // 2
    finally { middle.Dispose(); }
}
```

```csharp
// WRONG, does not compile: NAM1024 (the span leaves the callback)
first.Access(static view => Keep(view.AsSpan()));

static void Keep(ReadOnlySpan<int> values) { }
```

A small slice still keeps the whole block alive. Its `Length` is not what it costs.

## 4. Observe without keeping alive

A weak observer holds no data. It lets you ask "is it still there?" later.

```csharp
if (first.TryDowngrade(out NativeWeak<int> weak, out _))
{
    try
    {
        if (weak.TryUpgrade(out NativeShared<int> strong, out _))
        {
            try { Console.WriteLine(strong.Read(static view => view.AsSpan().Length)); }
            finally { strong.Dispose(); }
        }
    }
    finally { weak.Dispose(); }
}
```

After the last owner is disposed, `TryUpgrade` returns `false` with `ExpiredPayload` and `weak.IsExpired` is `true`. The last owner to leave frees the data, unless a read is still running: then the read finishes first.

## 5. Dispose

Dispose every owner and observer once. A second `Dispose` throws `InvalidOperationException`. `TryCompletePayloadReturn()` on an owner or observer retries a failed cleanup.

## Copy out of a share

`TryDetach(budget, out copy)` copies a share into a new, independent `NativeTransfer<T>`. Use it when a small slice should not keep the whole block alive.

```csharp
NativeMemoryBudget budget = new(capacityBytes: 1_024);

if (first.TrySlice(0, 2, out NativeShared<int> head, out _))
{
    try
    {
        if (head.TryDetach(budget, out NativeTransfer<int> copy))
        {
            try { Console.WriteLine(copy.Length); }   // 2
            finally { copy.Dispose(); }
        }
    }
    finally { head.Dispose(); }
}
```

It returns `false` when `budget` cannot hold the copy, and copies nothing. Other failures throw, and the share stays valid. If both are charged to one budget, it needs room for both while copying.

## Look at its state

`CaptureSnapshot()` returns a `NativeSharingStatistics` and works on a released value too. Fields are read one at a time.

| Field | Meaning |
|---|---|
| `StrongBindingCount`, `WeakBindingCount` | owners and observers now |
| `PeakStrongBindingCount`, `PeakWeakBindingCount` | the most at once |
| `ActiveReadCount` | reads running now |
| `Expired`, `PayloadReleased` | the last owner left; the data is given back |
| `RejectedStrongCount`, `RejectedWeakCount`, `ExpiredUpgradeCount`, `RejectedDetachCount` | refused calls |
| `OwnedBackingBytes` | bytes of native storage behind the block |
| `ManagedBankBytes` | managed memory used by the slot arrays |

`OwnedBackingBytes` is the full block, once per block. Do not add it up across blocks that came from the same pool page; read totals from the [`NativeMemoryBudget`](memory-budgets.md).

## Diagnostics

| ID | What it catches | How to fix |
|---|---|---|
| [NAM1021](diagnostics/NAM1021.md) | copying an owner by assignment | call `TryShare`, `TrySlice` or `TryUpgrade` |
| [NAM1022](diagnostics/NAM1022.md) | using a source after `Create` consumed it, or an owner after `Dispose` | stop using it |
| [NAM1024](diagnostics/NAM1024.md) | a read view escaping its callback | copy the data out |
| [NAM1025](diagnostics/NAM1025.md) | an owner or observer that is never disposed | `Dispose()` on every path |
| [NAM1050](diagnostics/NAM1050.md) | using a `Try` output without checking the result | use it only in the `true` branch |

## Terms

- **Owner (strong binding)**: a `NativeShared<T>`. The data lives while at least one exists.
- **Observer (weak binding)**: a `NativeWeak<T>`. It does not keep the data alive.
- **Slot**: one prepared place for an owner or observer.
- **Slice**: an owner that sees only a sub-range of the block.
