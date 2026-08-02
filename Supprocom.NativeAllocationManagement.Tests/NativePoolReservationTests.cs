using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolReservationTests
{
    [Fact]
    public void ConstructorsExposePreLeaseAndRawPreallocation()
    {
        Type poolType = typeof(NativePool<int>);
        System.Reflection.ConstructorInfo typed = Assert.Single(
            poolType.GetConstructors(),
            constructor =>
                constructor.GetParameters().Length == 3);
        Assert.Equal("preLease", typed.GetParameters()[0].Name);

        System.Reflection.ConstructorInfo combined = Assert.Single(
            poolType.GetConstructors(),
            constructor =>
                constructor.GetParameters().Length == 4);
        Assert.Equal("preLease", combined.GetParameters()[0].Name);
        Assert.Equal(
            "preAllocateBytes",
            combined.GetParameters()[1].Name);
        Assert.DoesNotContain(
            poolType.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.Name == "initialCapacity");
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

        Assert.Equal(30, pool.GetStatistics().RetainedBytes);
        Assert.Equal(30, arena.GetStatistics().RetainedBytes);

        using (NativeRegion region = new(
            preAllocateBytes: 30,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory))
        {
            Assert.Equal(30, region.GetStatistics().RetainedBytes);
        }
    }

    [Fact]
    public void PreLeaseReservesTypedStorageWithoutGrowth()
    {
        using NativePool<int> pool = new(
            preLease: 7,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        NativeOwnerStatistics reserved = pool.GetStatistics();
        Assert.Equal(7 * sizeof(int), reserved.RetainedBytes);
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
        Assert.Equal(30, reserved.RetainedBytes);
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
        Assert.Equal(50, reserved.RetainedBytes);
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
    public void DelayedActivationCreatesBothReservationsOnce()
    {
        using NativePool<long> pool = new(
            preLease: 4,
            preAllocateBytes: 18,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory,
            doNotLeaseOnDeclaration: true);

        NativeOwnerStatistics unleased = pool.GetStatistics();
        Assert.Equal(0, unleased.RetainedBytes);
        Assert.Equal(0, unleased.FreshSegmentAllocationCount);

        pool.LeaseFromMemory();

        NativeOwnerStatistics active = pool.GetStatistics();
        Assert.Equal(50, active.RetainedBytes);
        Assert.Equal(2, active.SegmentCount);
        Assert.Equal(2, active.FreshSegmentAllocationCount);
    }

    [Fact]
    public void NegativePreLeaseFailsBeforeAllocation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            static () => new NativePool<int>(preLease: -1));
    }
}
