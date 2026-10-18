using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolMetadataTests
{
    [Fact]
    public void SingleSlabNeedsNoClassTableWhenTheLogicalLengthChanges()
    {
        using NativePool<int> pool = new(8, NativeMemoryReturn.ToNativeMemory);
        Assert.Equal(0, HeadCount(pool));
        using (Pooled<int> first = pool.Rent(8, 7))
        {
            Assert.Equal(7, first.Read(static view => view[7]));
        }
        using (Pooled<int> smaller = pool.Rent(3, 11))
        {
            Assert.Equal(8, smaller.Capacity);
            Assert.Equal(11, smaller.Read(static view => view[2]));
        }
        Assert.Equal(0, HeadCount(pool));
        Assert.Equal(1, pool.GetStatistics().FreshSegmentAllocationCount);
    }

    [Fact]
    public void EmptyAndTrimmedSingleSlotOwnersDoNotAcquireAClassTable()
    {
        using NativePool<int> pool = new(0, NativeMemoryReturn.ToNativeMemory);
        Assert.Equal(0, HeadCount(pool));
        using (Pooled<int> first = pool.Rent(8, 7)) { }
        Assert.Equal((nuint)0, pool.TrimRetainedMemory(0));
        Assert.Equal(0, HeadCount(pool));
        Assert.Equal(NativeAlignedAllocation.GetBackingByteLength(32), pool.TrimRetainedMemory());
        using (Pooled<int> second = pool.Rent(8, 11))
        {
            Assert.Equal(11, second.Read(static view => view[7]));
        }
        Assert.Equal(0, HeadCount(pool));
        Assert.Equal(2, pool.GetStatistics().FreshSegmentAllocationCount);
    }

    [Fact]
    public void SimultaneousSlabsPrepareTheTableBeforeEitherLeaseReturns()
    {
        using NativePool<int> pool = new(4, NativeMemoryReturn.ToNativeMemory);
        using (Pooled<int> first = pool.Rent(4, 7))
        {
            Assert.Equal(0, HeadCount(pool));
            using Pooled<int> second = pool.Rent(8, 11);
            Assert.Equal(32, HeadCount(pool));
            Assert.Equal(7, first.Read(static view => view[3]));
            Assert.Equal(11, second.Read(static view => view[7]));
        }
        using Pooled<int> large = pool.Rent(7, 13);
        using Pooled<int> small = pool.Rent(3, 17);
        Assert.Equal(8, large.Capacity);
        Assert.Equal(4, small.Capacity);
        Assert.Equal(2, pool.GetStatistics().FreshSegmentAllocationCount);
    }

    [Fact]
    public void FailedLargerGrowthKeepsTheOnlyAvailableSlabReachable()
    {
        using NativePool<int> pool = new(8, NativeMemoryReturn.ToNativeMemory);
        try
        {
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => pool.Rent(9, 7));
        }
        finally { NativeMemoryTestHooks.Reset(); }
        Assert.Equal(1, pool.GetStatistics().AvailableSegmentCount);
        using Pooled<int> survivor = pool.Rent(7, 11);
        Assert.Equal(8, survivor.Capacity);
        Assert.Equal(11, survivor.Read(static view => view[6]));
        Assert.Equal(1, pool.GetStatistics().FreshSegmentAllocationCount);
    }

    [Fact]
    public void RejectedLargerGrowthDoesNotAcquireTheTableOrLoseTheCachedSlab()
    {
        long bytes = checked((long)NativeAlignedAllocation.GetBackingByteLength(32));
        NativeMemoryBudget budget = new(bytes);
        using NativePool<int> pool = new(budget, 8, preAllocateBytes: 0, NativeMemoryReturn.ToNativeMemory);
        Assert.Throws<NativeMemoryBudgetExceededException>(() => pool.Rent(9, 7));
        Assert.Equal(0, HeadCount(pool));
        Assert.Equal(bytes, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        using Pooled<int> survivor = pool.Rent(7, 11);
        Assert.Equal(8, survivor.Capacity);
        Assert.Equal(1, pool.GetStatistics().FreshSegmentAllocationCount);
    }

    [Fact]
    public void TypedAndRawReservationsKeepTheExistingCapacitySelection()
    {
        using NativePool<int> pool = new(4, 24, NativeMemoryReturn.ToNativeMemory);
        Assert.Equal(32, HeadCount(pool));
        using Pooled<int> typed = pool.Rent(4, 7);
        using Pooled<int> raw = pool.Rent(5, 11);
        Assert.Equal(4, typed.Capacity);
        Assert.Equal(6, raw.Capacity);
        Assert.Equal(2, pool.GetStatistics().FreshSegmentAllocationCount);
    }

    [Fact]
    public void EmptyAndBackedLeasesRemainDistinctOccupiedRecords()
    {
        using NativePool<int> pool = new(0, NativeMemoryReturn.ToNativeMemory);
        using Pooled<int> empty = pool.Rent(0, 7);
        Assert.Equal(0, HeadCount(pool));
        using Pooled<int> backed = pool.Rent(1, 11);
        Assert.Equal(32, HeadCount(pool));
        Assert.Equal(2, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(1, pool.GetStatistics().FreshSegmentAllocationCount);
        Assert.Equal(0, empty.Length);
        Assert.Equal(11, backed.Read(static view => view[0]));
    }

    private static int HeadCount(NativePool<int> pool)
    {
        FieldInfo? field = typeof(NativePool<int>).GetField("_freeHeads", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<int[]>(field.GetValue(pool)).Length;
    }
}
