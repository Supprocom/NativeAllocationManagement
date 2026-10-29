using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement.Tests;

// Large blocks that unbudgeted owners return wait in a process-wide cache for
// the next owner. Tests run in parallel and may take a kept block first, so
// each reuse check gets a few attempts.
public sealed unsafe class NativeLargeBlockTests
{
    private const int Bytes = (3 * 1024 * 1024) + 4096;
    private const nuint Huge = 40 * 1024 * 1024;

    [Fact]
    public void AnArenaMadePerUseTakesTheLargeSegmentOfTheLastOne() =>
        Assert.True(ReusedWithinAttempts(static () =>
        {
            using NativeArena arena = new();
            return Address(arena.ScratchUninitialized<byte>(Bytes).AsSpan());
        }));

    [Fact]
    public void ARegionMadePerUseTakesTheLargeSegmentOfTheLastOne() =>
        Assert.True(ReusedWithinAttempts(static () =>
        {
            using NativeRegion region = new();
            return Address(region.LeaseUninitialized<byte>(Bytes).AsSpan());
        }));

    [Fact]
    public void APoolMadePerUseTakesTheLargeSlabOfTheLastOne() =>
        Assert.True(ReusedWithinAttempts(static () =>
        {
            using NativePool<byte> pool = new();
            using Pooled<byte> lease = pool.RentUninitialized(Bytes);
            return Address(lease.AsSpan());
        }));

    [Fact]
    public void ALargeTransferTakesTheBlockTheLastOneReturned() =>
        Assert.True(ReusedWithinAttempts(static () =>
        {
            NativeTransfer<byte> transfer = NativeTransfer<byte>.CreateUninitialized(Bytes);
            nint address = transfer.Process<int, nint, AddressOf>(0);
            transfer.Dispose();
            return address;
        }));

    [Fact]
    public void ANativeMemoryArenaFreesItsLargeSegment()
    {
        nint address;
        using (NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory))
        {
            address = Address(arena.ScratchUninitialized<byte>(Bytes).AsSpan());
        }

        Assert.False(NativeLargeBlocks.HoldsForTest((void*)address));
    }

    [Fact]
    public void ABudgetedArenaFreesItsLargeSegment()
    {
        NativeMemoryBudget budget = new(64 * 1024 * 1024);
        nint address;
        using (NativeArena arena = new(budget, 0, NativeMemoryReturn.ToGarbageCollector))
        {
            address = Address(arena.ScratchUninitialized<byte>(Bytes).AsSpan());
        }

        Assert.False(NativeLargeBlocks.HoldsForTest((void*)address));
    }

    [Fact]
    public void ATakenBlockIsAtLeastTheLengthAndAtMostTwice()
    {
        void* block = NativeMemory.AlignedAlloc(Huge, NativeSegment.Alignment);
        Assert.True(NativeLargeBlocks.TryKeep(block, Huge));
        void* small = NativeLargeBlocks.TryTake((Huge / 2) - 64, out nuint smallBytes);
        Assert.True(small != block);
        FreeIfTaken(small);
        void* fitting = NativeLargeBlocks.TryTake(Huge - 64, out nuint fittingBytes);
        Assert.True(fitting == block);
        Assert.Equal(Huge, fittingBytes);
        NativeMemory.AlignedFree(fitting);
        _ = smallBytes;
    }

    [Fact]
    public void AFullCacheFreesItsOldestBlock()
    {
        void* first = NativeMemory.AlignedAlloc(Huge, NativeSegment.Alignment);
        void* second = NativeMemory.AlignedAlloc(Huge, NativeSegment.Alignment);
        Assert.True(NativeLargeBlocks.TryKeep(first, Huge));
        Assert.True(NativeLargeBlocks.TryKeep(second, Huge));
        Assert.False(NativeLargeBlocks.HoldsForTest(first));
        Assert.True(NativeLargeBlocks.HoldsForTest(second));
        FreeIfTaken(NativeLargeBlocks.TryTake(Huge, out _));
    }

    [Fact]
    public void ABlockNotTakenBetweenTwoTrimsIsFreed()
    {
        void* block = NativeMemory.AlignedAlloc(Huge, NativeSegment.Alignment);
        int collections = GC.CollectionCount(2);
        Assert.True(NativeLargeBlocks.TryKeep(block, Huge));
        NativeLargeBlocks.Trim(highLoad: false);
        if (GC.CollectionCount(2) == collections)
        {
            // A collection's own trim may have run in between.
            Assert.True(NativeLargeBlocks.HoldsForTest(block));
        }

        NativeLargeBlocks.Trim(highLoad: false);
        Assert.False(NativeLargeBlocks.HoldsForTest(block));
    }

    [Fact]
    public void HighMemoryLoadFreesEveryKeptBlock()
    {
        void* block = NativeMemory.AlignedAlloc(Huge, NativeSegment.Alignment);
        Assert.True(NativeLargeBlocks.TryKeep(block, Huge));
        NativeLargeBlocks.Trim(highLoad: true);
        Assert.False(NativeLargeBlocks.HoldsForTest(block));
    }

    [Fact]
    public void SmallBlocksAreNotKept()
    {
        void* block = NativeMemory.AlignedAlloc(NativeLargeBlocks.MinimumBytes - 64, NativeSegment.Alignment);
        Assert.False(NativeLargeBlocks.TryKeep(block, NativeLargeBlocks.MinimumBytes - 64));
        NativeMemory.AlignedFree(block);
    }

    // The block one use returned is kept, and the next use takes it.
    private static bool ReusedWithinAttempts(Func<nint> use)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            nint first = use();
            if (NativeLargeBlocks.HoldsForTest((void*)first) && use() == first)
            {
                return true;
            }
        }

        return false;
    }

    private static nint Address(Span<byte> span) => (nint)Unsafe.AsPointer(ref span[0]);

    private static void FreeIfTaken(void* block)
    {
        if (block != null)
        {
            NativeMemory.AlignedFree(block);
        }
    }

    private struct AddressOf : INativeSpanProcessor<byte, int, nint>
    {
        public static nint Invoke(scoped Span<byte> values, scoped in int state) => Address(values);
    }
}
