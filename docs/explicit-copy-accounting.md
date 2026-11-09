# Counting copied bytes

`NativeMemoryDiagnostics.Snapshot().CopiedBytes` totals the bytes NAM copied into or out of native storage in bulk. Take a snapshot before and after the code you want to measure and subtract.

```csharp
using Supprocom.NativeAllocationManagement;

long before = NativeMemoryDiagnostics.Snapshot().CopiedBytes;

using NativeTransfer<int> data = NativeTransfer<int>.Create(new int[100]);

long copied = NativeMemoryDiagnostics.Snapshot().CopiedBytes - before;   // 400 with detailed counters on, otherwise 0
```

## Turn it on

The counter stays at zero unless the process starts with detailed counters on:

```powershell
$env:NAM_DETAILED_COUNTERS = "1"
```

or the AppContext switch `Supprocom.NativeAllocationManagement.DetailedCounters`. `NativeMemoryDiagnostics.DetailedCountersEnabled` tells you which mode is active. Details are in [Production accounting](production-accounting-contract.md).

## What counts

| Counted | Not counted |
|---|---|
| `NativeTransfer<T>.Create(span)` | `NativeTransfer<T>.Create(length, value)` and `Fill` |
| `builder.Append(span)` | `builder.Append(value)` and element-by-element `writer.Write` |
| `CopyFrom` and `CopyTo` on `Local<T>`, `Pooled<T>` and views | |
| `NativeShared<T>.TryDetach` and `NativeLayoutOwner.DetachField` | writes you make yourself through a span |
| | empty copies |

The size is the number of elements times the element size, added after the copy succeeds. A copy that happened before a later failure stays counted. Each copy counts once, even when one NAM call forwards to another.

The counter covers NAM's own bulk copies, not all memory traffic in the process.

A `NativeLayoutOwner` also reports its own `CopiedBytes` in `CaptureSnapshot()`; see [Typed layouts](typed-layouts.md).
