# Arena retention policy

A `NativeArena` created with a retention policy grows in segments when it runs out of room, and gives idle segments back when you reset it or ask it to. Use it for arenas whose peak is much larger than their typical load.

```csharp
using Supprocom.NativeAllocationManagement;

NativeMemoryBudget budget = new(1 << 20);
using NativeArena arena = new(
    budget,
    new NativeArenaRetentionPolicy(ordinarySegmentCeilingBytes: 4096, idleRetentionBytes: 8192),
    preAllocateBytes: 1024,
    NativeMemoryReturn.Free);

for (int index = 0; index < 6; index++)
{
    ArenaLease<byte> chunk = arena.Scratch<byte>(3000, (byte)1);
    byte first = chunk.Read(static view => view[0]);
}

{
    // An outlier: a request this big gets a segment of its own.
    ArenaLease<byte> big = arena.Scratch<byte>(100_000, (byte)1);
    byte last = big.Read(static view => view[99_999]);
}

arena.Reset();                          // the outlier is released, ordinary segments are kept
nuint released = arena.MaintainRetention();
```

Every argument of this constructor is required. Other arena constructors do not grow this way or trim on their own.

## Policy

`preAllocateBytes` is a constructor argument of the arena, not of the policy: the usable bytes reserved at creation. It does not change the growth threshold. A policy has two values:

| Argument | Meaning |
|---|---|
| `ordinarySegmentCeilingBytes` | Largest segment the arena picks for ordinary growth. |
| `idleRetentionBytes` | How many bytes of idle segments to keep, shared by both lanes, headers and alignment included. Zero is valid. |

A ceiling below 64 throws `ArgumentOutOfRangeException`. `MaintainRetention()` on an arena without a policy throws `InvalidOperationException`.

A request larger than the ceiling gets a segment of exactly the size it needs. That segment is an *outlier*: it never becomes the size basis for the next ordinary segment. The ceiling limits segment size, not how much you can have live.

If the budget is nearly full, the arena can pick a smaller segment than the ceiling when a request would otherwise be refused.

## What happens when

| Call | Effect |
|---|---|
| `Reset()` | Releases idle outliers, then keeps ordinary idle segments up to `idleRetentionBytes`. |
| `RecycleScoped()` | The same, after emptying the scoped lane. |
| `MaintainRetention()` | The same, on demand. Returns the bytes released. |

A segment that is partly used is not idle: all of it stays charged until its generation or scope ends. Live scratch ranges are never evicted to reach the target. `MaintainRetention()` leaves live ranges valid, but throws while a callback or an initializer is running, before it releases anything.

Nothing runs on a timer. The arena looks at its segments only when it grows or when one of these calls runs.

## Snapshot

`arena.CaptureRetentionSnapshot()` returns a value copy. Call it on the arena's thread. It allocates nothing and resets nothing.

| Field | Meaning |
|---|---|
| `Enabled` | `false` for an arena without a policy; its other policy fields are zero. |
| `Policy` | The policy you passed. |
| `RetainedBytes` | All native bytes the arena holds now. |
| `IdleBytes` | Whole owned segments that are free to reuse. |
| `OversizedBytes`, `PeakOversizedBytes` | Outlier bytes now, and the highest so far. |
| `ReleasedBytes` | Bytes freed by `Reset`, `RecycleScoped` and `MaintainRetention`. Trim and disposal are not counted. |
| `MaintenanceCount` | How often those three ran, even when they released nothing. |
| `HistoryOverflowed` | A count reached `long.MaxValue`; counts are lower bounds. |

Releasing outliers lowers the bytes NAM holds. It does not prove lower RSS. When you compare it with a managed buffer policy, include the maintenance calls, creation and teardown.

## Terms

| Term | Meaning |
|---|---|
| Segment | One native block the arena owns. |
| Idle segment | A segment with no live range in it. |
| Outlier | A segment bigger than the ordinary ceiling, made for one large request. |
