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
        using (NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.Free))
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
        using (NativeArena arena = new(budget, 0, NativeMemoryReturn.Reuse))
        {
            address = Address(arena.ScratchUninitialized<byte>(Bytes).AsSpan());
        }

        Assert.False(NativeLargeBlocks.HoldsForTest((void*)address));
    }

    [Fact]
    public void ATakenBlockIsAtLeastTheLengthAndAtMostTwice()
    {
        void* block = Allocate(Huge, out long metricsEpoch);
        Assert.True(NativeLargeBlocks.TryKeep(block, Huge, metricsEpoch));
        void* small = NativeLargeBlocks.TryTake((Huge / 2) - 64, out nuint smallBytes, out long smallEpoch);
        Assert.True(small != block);
        FreeIfTaken(small, smallBytes, smallEpoch);
        void* fitting = NativeLargeBlocks.TryTake(Huge - 64, out nuint fittingBytes, out long fittingEpoch);
        Assert.True(fitting == block);
        Assert.Equal(Huge, fittingBytes);
        Assert.Equal(metricsEpoch, fittingEpoch);
        Free(fitting, fittingBytes, fittingEpoch);
    }

    // A kept block stays outstanding: keeping and taking it record nothing,
    // and the cache records the free when it frees it.
    [Fact]
    public void AKeptBlockStaysOutstandingUntilTheCacheFreesIt()
    {
        void* block = Allocate(Huge, out long metricsEpoch);
        NativeMemoryStatistics allocated = NativeMemoryDiagnostics.Snapshot();
        Assert.True(NativeLargeBlocks.TryKeep(block, Huge, metricsEpoch));
        NativeMemoryStatistics kept = NativeMemoryDiagnostics.Snapshot();
        Assert.Equal(allocated.OutstandingNativeBytes, kept.OutstandingNativeBytes);
        Assert.Equal(allocated.FreeCount, kept.FreeCount);
        NativeLargeBlocks.Trim(highLoad: true);
        NativeMemoryStatistics freed = NativeMemoryDiagnostics.Snapshot();
        Assert.False(NativeLargeBlocks.HoldsForTest(block));
        Assert.True(freed.FreeCount > kept.FreeCount);
        Assert.True(freed.OutstandingNativeBytes <= kept.OutstandingNativeBytes - (long)Huge);
    }

    // A block from an earlier measurement epoch would hide its next owner
    // from the current one: it waits to be freed instead.
    [Fact]
    public void ABlockFromAnEarlierMeasurementEpochIsNotTaken()
    {
        // Never counted in the current epoch, so freeing it records nothing.
        void* stale = NativeMemory.AlignedAlloc(Huge, NativeSegment.Alignment);
        Assert.True(NativeLargeBlocks.TryKeep(stale, Huge, NativeMemoryAccounting.CurrentMetricsEpoch - 1));
        void* taken = NativeLargeBlocks.TryTake(Huge, out nuint takenBytes, out long takenEpoch);
        Assert.True(taken != stale);
        FreeIfTaken(taken, takenBytes, takenEpoch);
        Assert.True(NativeLargeBlocks.HoldsForTest(stale));
        NativeLargeBlocks.Trim(highLoad: true);
        Assert.False(NativeLargeBlocks.HoldsForTest(stale));
    }

    [Fact]
    public void AFullCacheFreesItsOldestBlock()
    {
        void* first = Allocate(Huge, out long firstEpoch);
        void* second = Allocate(Huge, out long secondEpoch);
        Assert.True(NativeLargeBlocks.TryKeep(first, Huge, firstEpoch));
        Assert.True(NativeLargeBlocks.TryKeep(second, Huge, secondEpoch));
        Assert.False(NativeLargeBlocks.HoldsForTest(first));
        Assert.True(NativeLargeBlocks.HoldsForTest(second));
        FreeIfTaken(NativeLargeBlocks.TryTake(Huge, out nuint takenBytes, out long takenEpoch), takenBytes, takenEpoch);
    }

    [Fact]
    public void ABlockNotTakenBetweenTwoTrimsIsFreed()
    {
        void* block = Allocate(Huge, out long metricsEpoch);
        int collections = GC.CollectionCount(2);
        Assert.True(NativeLargeBlocks.TryKeep(block, Huge, metricsEpoch));
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
        void* block = Allocate(Huge, out long metricsEpoch);
        Assert.True(NativeLargeBlocks.TryKeep(block, Huge, metricsEpoch));
        NativeLargeBlocks.Trim(highLoad: true);
        Assert.False(NativeLargeBlocks.HoldsForTest(block));
    }

    [Fact]
    public void SmallBlocksAreNotKept()
    {
        void* block = NativeMemory.AlignedAlloc(NativeLargeBlocks.MinimumBytes - 64, NativeSegment.Alignment);
        Assert.False(NativeLargeBlocks.TryKeep(block, NativeLargeBlocks.MinimumBytes - 64, NativeMemoryAccounting.CurrentMetricsEpoch));
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

    // A block as an owner hands it over: allocated and counted.
    private static void* Allocate(nuint bytes, out long metricsEpoch)
    {
        void* block = NativeMemory.AlignedAlloc(bytes, NativeSegment.Alignment);
        metricsEpoch = NativeMemoryAccounting.RecordAllocation(bytes, zeroed: false);
        return block;
    }

    private static void Free(void* block, nuint bytes, long metricsEpoch)
    {
        NativeMemory.AlignedFree(block);
        NativeMemoryAccounting.RecordFree(bytes, detached: false, metricsEpoch);
    }

    private static void FreeIfTaken(void* block, nuint bytes, long metricsEpoch)
    {
        if (block != null)
        {
            Free(block, bytes, metricsEpoch);
        }
    }

    private struct AddressOf : INativeSpanProcessor<byte, int, nint>
    {
        public static nint Invoke(scoped Span<byte> values, scoped in int state) => Address(values);
    }
}
