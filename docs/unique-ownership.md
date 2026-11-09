# Unique ownership with NativeTransfer

`NativeTransfer<T>` (`T` must be unmanaged) is the single owner of one block of initialized native elements. Unlike a `Span<T>`, you can keep it in a field, send it through a channel or hand it to another thread.

```csharp
using Supprocom.NativeAllocationManagement;

using NativeTransfer<int> data = NativeTransfer<int>.Create(new[] { 1, 2, 3 });

int sum = data.Read(static view =>
{
    int total = 0;
    foreach (int value in view.AsSpan()) total += value;
    return total;
});
```

The steps are always the same: create, move, use, dispose.

## 1. Create

| Source | Gives you |
|---|---|
| `NativeTransfer<T>.Create(span)` | a copy of `span` |
| `NativeTransfer<T>.Create(length, value)` | `length` elements, all set to `value` |
| `NativeTransfer<T>.CreateUninitialized(length)` | `length` elements with unspecified contents; write each before reading it |
| `builder.Complete()` | a builder's output, see [Build a growable native sequence](getting-started.md#build-a-growable-native-sequence) |
| `NativeConcurrentPool<T>.RentTransferable(...)` | a pool lease that can be moved, see [Transfer ownership across threads](getting-started.md#transfer-ownership-across-threads) |

Assign the result straight to a local.

## 2. Move

Assignment never creates a second owner. To pass ownership on, hold the transfer in a nullable local and call `Move`. It sets the source to `null`, so only the destination can use the data.

```csharp
NativeTransfer<int>? source = NativeTransfer<int>.Create(new[] { 1, 2, 3 });
using NativeTransfer<int> owner = NativeTransfer<int>.Move(ref source);
// source is null from here on.
```

```csharp
// WRONG, does not compile: NAM1023 (source was already moved)
using NativeTransfer<int> again = NativeTransfer<int>.Move(ref source);

// WRONG, does not compile: NAM1021 (assignment is not a move) and NAM1025 (copy is never disposed)
NativeTransfer<int> copy = owner;
```

A failed `Move` still consumes its source. If another thread is inside `Read` or `Access` when you move, the move throws `InvalidOperationException`: the source is consumed, the destination gets nothing, and the callback finishes safely.

Pass a transfer to a method as an ordinary by-value parameter. The method then owns it and must dispose it or move it on. Call it as `Use(NativeTransfer<int>.Move(ref source))`. Do not declare `in`, `ref` or `out` transfer parameters (NAM1027).

## 3. Use

`Read` returns a result and `Access` returns nothing. Both lend you a view that is valid only inside the callback.

```csharp
// WRONG, does not compile: NAM1024 (the span leaves the callback)
owner.Access(static view => Keep(view.AsSpan()));

static void Keep(ReadOnlySpan<int> values) { }
```

A helper that takes `view.AsSpan()` must declare the parameter `scoped`.

## 4. Dispose

End every path with `Dispose()` or a `Move`. A `using` declaration does it.

```csharp
// WRONG, does not compile: NAM1025 (never disposed)
NativeTransfer<int> leaked = NativeTransfer<int>.Create(new[] { 1 });
```

A transfer that nobody disposes is freed later by a finalizer, when the GC gets to it. Do not rely on that to get memory back in time.

## When the analyzer is off

The same mistakes throw at run time.

| Mistake | You get |
|---|---|
| `Dispose()` or `Read` after `Dispose()` | `ObjectDisposedException` |
| any use of an alias whose ownership moved | `InvalidOperationException` |
| Any call on `default(NativeTransfer<T>)` | `NativeAllocationUninitializedException` |
| `NativeTransfer<T>.Move(ref source)` with `source == null` | `ArgumentNullException` |
| `Dispose()` from inside its own `Access` or `Read` callback | `InvalidOperationException`; the transfer stays usable |
| negative `length` | `ArgumentOutOfRangeException` |

If a cleanup error makes `Dispose` throw, the same value can call `Dispose` again. `TryCompletePayloadReturn()` retries a failed cleanup; it returns `true` once the storage is given back.

## Look at its state

`CaptureSnapshot()` allocates nothing and also works on a disposed or moved value (not on `default`). Fields are read one at a time, so while other threads use the transfer they can be slightly out of step.

```csharp
NativeTransferStatistics stats = owner.CaptureSnapshot();
Console.WriteLine($"{stats.Lifecycle} moves={stats.MoveCount} borrows={stats.ActiveBorrowCount}");
```

| Field | Meaning |
|---|---|
| `Lifecycle` | `Active`, `Returned`, `Shared` and so on |
| `BindingIsActive` | whether this value is the current owner |
| `MoveCount` | how often ownership moved |
| `ActiveBorrowCount`, `PeakBorrowCount` | callbacks running now, and the most at once |
| `InitializedPayloadBytes` | bytes of data the transfer holds |
| `OwnedBackingBytes` | bytes of native storage behind it |
| `PayloadReturnCount` | 1 once the storage has been given back |
| `HasReturnObligation` | cleanup is not finished yet |

A pooled transfer can sit in a larger shared segment. Do not add up `OwnedBackingBytes` across transfers; read the total from the [`NativeMemoryBudget`](memory-budgets.md).

## Diagnostics

| ID | What it catches | How to fix |
|---|---|---|
| [NAM1021](diagnostics/NAM1021.md) | copying a transfer by assignment | `NativeTransfer<T>.Move(ref source)` |
| [NAM1022](diagnostics/NAM1022.md) | using a transfer after dispose or move, or disposing twice | stop using the value after `Dispose` or `Move` |
| [NAM1023](diagnostics/NAM1023.md) | `Move` from a source that is already empty | move from a source that still owns |
| [NAM1024](diagnostics/NAM1024.md) | a callback view escaping its callback | copy the data out, or mark the helper parameter `scoped` |
| [NAM1025](diagnostics/NAM1025.md) | a path that neither disposes nor moves | `using`, `Dispose()` or `Move` |
| [NAM1026](diagnostics/NAM1026.md) | a transfer acquired straight into a field, collection or expression | acquire into a local, then move |
| [NAM1027](diagnostics/NAM1027.md) | `in`, `ref` or `out` transfer parameters | take it by value |

## Terms

- **Unmanaged**: a type with no references, so it can live in native memory.
- **View**: the read or write window a callback receives. It is valid only inside that callback.
- **Move**: hand ownership to a new variable and empty the old one.

For sharing one block between several readers, see [Immutable sharing](immutable-sharing.md).
