using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolReservationTests
{
    [Fact]
    public void ConstructorsExposePreLeaseAndRawPreallocation()
    {
        Type poolType = typeof(NativePool<int>);
        System.Reflection.ConstructorInfo typed = SingleExpected(
            poolType.GetConstructors(),
            constructor =>
                constructor.GetParameters().Length == 2);
        Assert.Equal("preLease", typed.GetParameters()[0].Name);

        System.Reflection.ConstructorInfo combined = SingleExpected(
            poolType.GetConstructors(),
            constructor =>
                constructor.GetParameters().Length == 3);
        Assert.Equal("preLease", combined.GetParameters()[0].Name);
        Assert.Equal(
            "preAllocateBytes",
            combined.GetParameters()[1].Name);
        Assert.DoesNotContain(
            poolType.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters()),
            parameter => string.Equals(parameter.Name, "initialCapacity", StringComparison.Ordinal));
    }

    [Fact]
    public void RawPreallocationIsConsistentAcrossOwnerFamilies()
    {
        using NativePool<byte> pool = new(
            preLease: 0,
            preAllocateBytes: 30,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena arena = new(
            preAllocateBytes: 30,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Assert.Equal((long)NativeAlignedAllocation.GetBackingByteLength(30), pool.GetStatistics().RetainedBytes);
        Assert.Equal((long)NativeAlignedAllocation.GetBackingByteLength(30 + 64), arena.GetStatistics().RetainedBytes);
        Assert.Equal(30, pool.GetStatistics().UsableCapacityBytes);
        Assert.Equal(30, arena.GetStatistics().UsableCapacityBytes);

        using (NativeRegion region = new(
            preAllocateBytes: 30,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory))
        {
            Assert.Equal((long)NativeAlignedAllocation.GetBackingByteLength(30 + 64), region.GetStatistics().RetainedBytes);
            Assert.Equal(30, region.GetStatistics().UsableCapacityBytes);
        }
    }

    [Fact]
    public void PreLeaseReservesTypedStorageWithoutGrowth()
    {
        using NativePool<int> pool = new(
            preLease: 7,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        NativeOwnerStatistics reserved = pool.GetStatistics();
        Assert.Equal((long)NativeAlignedAllocation.GetBackingByteLength(7 * sizeof(int)), reserved.RetainedBytes);
        Assert.Equal(7 * sizeof(int), reserved.UsableCapacityBytes);
        Assert.Equal(1, reserved.SegmentCount);
        Assert.Equal(1, reserved.FreshSegmentAllocationCount);

        using Pooled<int> lease = pool.Rent(
            7,
            static writer => writer.Fill(3));
        Assert.Equal(7, lease.Capacity);
        Assert.Equal(
            reserved.FreshSegmentAllocationCount,
            pool.GetStatistics().FreshSegmentAllocationCount);
    }

    [Fact]
    public void PreAllocateBytesReservesExactRawStorageWithoutGrowth()
    {
        using NativePool<int> pool = new(
            preLease: 0,
            preAllocateBytes: 30,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        NativeOwnerStatistics reserved = pool.GetStatistics();
        Assert.Equal((long)NativeAlignedAllocation.GetBackingByteLength(30), reserved.RetainedBytes);
        Assert.Equal(7 * sizeof(int), reserved.UsableCapacityBytes);
        Assert.Equal(1, reserved.SegmentCount);
        Assert.Equal(1, reserved.FreshSegmentAllocationCount);

        using Pooled<int> lease = pool.Rent(
            7,
            static writer => writer.Fill(5));
        Assert.Equal(7, lease.Capacity);
        Assert.Equal(
            reserved.FreshSegmentAllocationCount,
            pool.GetStatistics().FreshSegmentAllocationCount);
    }

    [Fact]
    public void TypedAndRawReservationsRemainIndependent()
    {
        using NativePool<int> pool = new(
            preLease: 8,
            preAllocateBytes: 18,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        NativeOwnerStatistics reserved = pool.GetStatistics();
        Assert.Equal((long)(NativeAlignedAllocation.GetBackingByteLength(32) + NativeAlignedAllocation.GetBackingByteLength(18)), reserved.RetainedBytes);
        Assert.Equal(12 * sizeof(int), reserved.UsableCapacityBytes);
        Assert.Equal(2, reserved.SegmentCount);
        Assert.Equal(2, reserved.FreshSegmentAllocationCount);

        using Pooled<int> small = pool.Rent(
            4,
            static writer => writer.Fill(1));
        using Pooled<int> large = pool.Rent(
            8,
            static writer => writer.Fill(2));
        Assert.Equal(4, small.Capacity);
        Assert.Equal(8, large.Capacity);
        Assert.Equal(
            reserved.FreshSegmentAllocationCount,
            pool.GetStatistics().FreshSegmentAllocationCount);
    }

    [Fact]
    public void SameClassSearchSkipsAnUndersizedRawReservation()
    {
        using NativePool<int> pool = new(
            preLease: 8,
            preAllocateBytes: 20,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        long freshSegments =
            pool.GetStatistics().FreshSegmentAllocationCount;

        using Pooled<int> large = pool.Rent(
            7,
            static writer => writer.Fill(1));
        using Pooled<int> exact = pool.Rent(
            5,
            static writer => writer.Fill(2));

        Assert.Equal(8, large.Capacity);
        Assert.Equal(5, exact.Capacity);
        Assert.Equal(
            freshSegments,
            pool.GetStatistics().FreshSegmentAllocationCount);
    }

    [Fact]
    public void NegativePreLeaseFailsBeforeAllocation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            static () => new NativePool<int>(preLease: -1));
    }
}
