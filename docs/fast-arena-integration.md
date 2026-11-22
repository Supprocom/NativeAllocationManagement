# Mapped arenas and grouped operations

Use a *mapped arena* when the memory comes from somewhere else, such as a memory-mapped file. NAM carves ranges out of the buffer you provide and never allocates the data itself. *Grouped operations* then create or read several ranges in one call.

An arena has two fixed areas, called *lanes*: the ordinary lane (ranges end at `Reset()`) and the scoped lane (ranges end at `RecycleScoped()`). See [prepared arenas](prepared-arenas.md).

```csharp
using System.Runtime.InteropServices;
using Supprocom.NativeAllocationManagement;

using AlignedBuffer buffer = new(256);
NativeMemoryBudget budget = new(256);
using NativeArena arena = new(buffer, byteOffset: 0,
    new NativeArenaPreparation(ordinaryBytes: 64, scopedBytes: 128), budget);

if (arena.TryScratch<int>(1, static writer => writer.Fill(17), out ArenaLease<int> source))
{
    try
    {
        if (NativeLeaseOperations.TryInitializeScoped<int, int, long, byte, int>(
                source, arena, 2, 2, 4, 1,
                static (input, a, b, c, d) =>
                {
                    a.Fill(input[0]);
                    b.Fill(2);
                    c.Fill(3);
                    d.Fill(4);
                },
                out ArenaLease<int> first, out ArenaLease<long> second,
                out ArenaLease<byte> third, out ArenaLease<int> fourth))
        {
            NativeLeaseOperations.Access(source, first, second, third, fourth,
                static (s, a, b, c, d) => Console.WriteLine($"{s[0]} {a[1]} {b[0]} {c[3]} {d[0]}"));
        }
    }
    finally
    {
        arena.RecycleScoped();
    }
}

// Any SafeBuffer works. This one wraps 64-byte aligned native memory.
sealed unsafe class AlignedBuffer : SafeBuffer
{
    public AlignedBuffer(nuint byteLength) : base(ownsHandle: true)
    {
        SetHandle((nint)NativeMemory.AlignedAlloc(byteLength, 64));
        Initialize((ulong)byteLength);
    }

    protected override bool ReleaseHandle()
    {
        NativeMemory.AlignedFree((void*)handle);
        return true;
    }
}
```

The example needs `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` in the project file because of the `SafeBuffer` subclass.

Everything else about the arena works as in [prepared arenas](prepared-arenas.md): the same `TryScratch`, `Reset`, `RecycleScoped` and snapshot, on the thread that created it.

## 1. Create over a SafeBuffer

`new NativeArena(buffer, byteOffset, preparation, budget)` takes a `SafeBuffer` that covers `ordinaryBytes + scopedBytes` bytes from `byteOffset`.

| Requirement | Otherwise |
|---|---|
| The buffer covers the whole range. | `ArgumentOutOfRangeException` |
| The start address is 64-byte aligned. | `ArgumentException` |
| If the scoped lane is not empty, `ordinaryBytes` is a multiple of 64. | `ArgumentException` |

Both lanes are fixed: they cannot grow or map more memory. NAM writes only to the ranges you ask for.

NAM allocates one small header block of its own for bookkeeping, outside your buffer (128 bytes for two lanes). The budget charges that block, not your buffer. If you cap the application's memory, count your own buffer yourself.

NAM does not check that your `SafeBuffer` honours its own contract.

## 2. Dispose

Disposing the arena releases the header block and NAM's hold on your buffer. Disposing the buffer first does not free memory that the arena still holds.

## Group several ranges

`NativeLeaseOperations.InitializeScoped` and `TryInitializeScoped` create four or eight scoped ranges in one call and run one initializer. `source` is a lease from the same arena that the initializer can read.

| Call | When the scoped lane is full |
|---|---|
| `InitializeScoped(source, arena, lengths..., initializer, out leases...)` | throws |
| `TryInitializeScoped(source, arena, lengths..., initializer, out leases...)` | returns `false`, before the initializer runs |

Returning from the initializer means every span is completely written. If the initializer throws, or the lane is full, all the new ranges are rolled back and the outputs are default. Ranges you created earlier stay valid.

`TryInitializeScoped` returns `false` only for lack of space. These throw instead: a stale, default or foreign `source`, a negative length, an arena that is not prepared, and a failing initializer.

Use the outputs only when `TryInitializeScoped` returned `true`:

```csharp
// WRONG, does not compile: NAM1050 (outputs used without checking the result)
NativeLeaseOperations.TryInitializeScoped<int, int, int, int, int>(
    source, arena, 1, 1, 1, 1, static (_, a, b, c, d) => { },
    out ArenaLease<int> a1, out ArenaLease<int> b1, out ArenaLease<int> c1, out ArenaLease<int> d1);
_ = a1.Read(static view => view[0]);
```

## Read several ranges

`NativeLeaseOperations.Access` has overloads for 2, 3, 4, 5, 7 and 8 leases of the same arena. One callback sees all of their views.

Every lease is checked before the callback starts, so a stale, default or foreign lease fails without exposing any view. `Reset`, `RecycleScoped`, trim and disposal cannot run while the callback does.

## Recycling a batch

A scoped batch ends with `RecycleScoped()` on every path, including exceptions. Put it in a `finally`, as in the first example. The analyzer warns when a batch is never recycled ([NAM1020](diagnostics/NAM1020.md)):

```csharp
// WRONG: warning NAM1020 (the scoped batch is never recycled)
NativeLeaseOperations.InitializeScoped<int, int, int, int, int>(
    source, arena, 1, 1, 1, 1, static (_, a, b, c, d) => { },
    out ArenaLease<int> a2, out ArenaLease<int> b2, out ArenaLease<int> c2, out ArenaLease<int> d2);
_ = a2.Read(static view => view[0]);
```

## Snapshot

`arena.CapturePreparedSnapshot()` and `arena.GetStatistics()` report the lanes. For a mapped arena:

| Field | Meaning |
|---|---|
| `RetainedBytes` | NAM's own header bytes. |
| `ActiveBorrowedBytes` | Addressable lane capacity of your buffer. |
| `RetainedBorrowedBytes` | Ordinary plus scoped bytes from `byteOffset` (192 for the 64 + 128 example). |
| `BorrowedBytes` (statistics) | Same retained range, never charged to the budget. |
| `SegmentCount` (statistics) | Lanes, not allocations. |

Trimming frees a lane only when no range in it is live. The header block is freed when the last lane goes.

## Terms

| Term | Meaning |
|---|---|
| Mapped arena | An arena whose memory comes from a `SafeBuffer` you provide. |
| Header block | NAM's small bookkeeping block, allocated outside your buffer. |
| Lane | One fixed area of the arena: ordinary or scoped. |
| Scoped batch | The ranges created since the last `RecycleScoped()`. |
