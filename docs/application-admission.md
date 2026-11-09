# Application admission

Use admission when a job that cannot fit would waste work already done: reserve the bytes first, produce afterwards. `TryReserve` checks a [budget](memory-budgets.md) before anything is allocated. It is not a scheduler or a process-RSS limit.

```csharp
using Supprocom.NativeAllocationManagement;

NativeMemoryBudget budget = new(4096);

if (!budget.TryReserve<int>(1024, out NativeMemoryReservation<int>? permission,
        out NativeMemoryAdmissionExhaustionReason reason))
{
    return; // reason is NativeByteCapacity: nothing was allocated
}

try
{
    permission.Value.PrepareBacking(); // optional: acquire the memory before producing
    using NativeTransfer<int> owner = NativeMemoryReservation<int>.Activate(
        ref permission, static writer => writer.Fill(42));
    int first = owner.Read(static values => values[0]);
}
finally
{
    permission?.Dispose();
}
```

The steps are: reserve, prepare (optional), activate, use, dispose.

## 1. Reserve

`budget.TryReserve<T>(length, out permission, out reason)` returns `false` only when the bytes do not fit. `permission` is then `null`. A length below zero throws `ArgumentOutOfRangeException`. A length of zero is valid.

A *permission* (`NativeMemoryReservation<T>`) is the right to produce `length` elements of `T`. It is a struct, held in a nullable local, and `Activate` and `Move` take it by `ref`.

```csharp
// WRONG, does not compile: NAM1050 (the result is not checked before use), NAM1025 (permission never disposed)
budget.TryReserve<int>(4, out NativeMemoryReservation<int>? ignored, out _);
ignored!.Value.PrepareBacking();
```

## 2. Prepare

`PrepareBacking(cancellationToken)` allocates the native memory now, so that producing later allocates nothing. If it throws (allocator failure or a cancelled token), you still hold the permission: retry it or dispose it.

## 3. Activate

`NativeMemoryReservation<T>.Activate(ref permission, initializer, cancellationToken)` consumes the permission, runs your initializer over exactly the declared range, and returns a `NativeTransfer<T>`: the single owner of the data. `permission` becomes `null`.

If the initializer throws or the token is cancelled, no transfer is returned and the reserved bytes are given back to the budget.

## 4. Use and dispose

Use the transfer as described in [Getting started](getting-started.md#build-a-growable-native-sequence). Dispose it when you are done. Dispose a permission you did not activate: `permission?.Dispose()` in a `finally` does it and does nothing once `Activate` has run.

```csharp
// WRONG, does not compile: NAM1025 (permission not disposed on every path)
if (budget.TryReserve<int>(4, out NativeMemoryReservation<int>? forgotten, out _))
{
    forgotten.Value.PrepareBacking();
}
```

After `Activate` or `Move`, `permission` is `null` and `permission.Value` throws. Take a copy first if you need the snapshot:

```csharp
NativeMemoryReservationStatistics before = permission.Value.CaptureSnapshot();
```

## Moving a permission

`NativeMemoryReservation<T>.Move(ref source)` hands the permission to a new variable and sets `source` to `null`. Nothing is allocated and nothing is charged twice.

A permission that failed its final cleanup stays charged. `permission.Value.TryCompletePayloadReturn()` retries that cleanup. `Dispose()` on an unused permission counts in `CancelledReservationCount`.

Analyzer errors in this page:

| ID | What it catches | How to fix |
|---|---|---|
| [NAM1050](diagnostics/NAM1050.md) | Using a permission without checking that `TryReserve` returned `true` | Use it only inside `if (TryReserve(...))` |
| [NAM1025](diagnostics/NAM1025.md) | A permission not disposed or activated on every path | `permission?.Dispose()` in a `finally` |

## Statistics

`budget.CaptureAdmissionStatistics()` counts reservations. Read it when no admission is in progress.

| Field | Meaning |
|---|---|
| `PendingBytes` | Reserved, no memory acquired yet. |
| `PreparedUnpublishedBytes` | Memory acquired by `PrepareBacking`, not yet activated. |
| `OutstandingReservationCount` | Permissions not yet activated or disposed. |
| `AdmittedReservationCount`, `RejectedReservationCount` | Reservations that fit, and those that did not. |
| `ActivationCount` | Successful activations. |
| `CancelledReservationCount`, `InitializationFailureCount` | Reservations ended by cancellation, or by a failing initializer. |
| `HistoryOverflowed` | A count reached `long.MaxValue`; it is a lower bound. |

`permission.Value.CaptureSnapshot()` describes one permission, also after it was used up: its outcome (`Pending`, `Prepared`, `Activated`, `Cancelled`, ...), declared length and bytes.

When you benchmark admission, include the reservation and the cleanup, not only the production loop. Compare against the same byte limit, pooling and retention.

## Terms

| Term | Meaning |
|---|---|
| Permission | The right to produce one declared range, returned by `TryReserve`. |
| Admission | Checking a request against a budget before allocating. |
| Activation | Turning a permission into a `NativeTransfer<T>` by running the initializer. |
| Transfer | The single owner of finished native data; see [Getting started](getting-started.md#build-a-growable-native-sequence). |
