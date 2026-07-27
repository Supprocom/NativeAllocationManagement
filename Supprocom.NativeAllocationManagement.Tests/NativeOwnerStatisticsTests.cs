using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeOwnerStatisticsTests
{
    [Fact]
    public void PoolStatisticsTrackRequestedRetainedAndTrimmedStorage()
    {
        using NativePool<int> pool = new(
            initialCapacity: 64,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        NativeOwnerStatistics reserved = pool.GetStatistics();
        Assert.Equal(NativeOwnerLifecycle.Active, reserved.Lifecycle);
        Assert.Equal(0, reserved.RequestedBytes);
        Assert.True(reserved.RetainedBytes >= 64 * sizeof(int));
        Assert.True(reserved.SegmentCount >= 1);
        Assert.True(reserved.FreshSegmentAllocationCount >= 1);

        using (Pooled<int> values = pool.Rent(16, static writer => writer.Fill(default!)))
        {
            NativeOwnerStatistics active = pool.GetStatistics();
            Assert.Equal(16 * sizeof(int), active.RequestedBytes);
            Assert.Equal(reserved.RetainedBytes, active.RetainedBytes);
            Assert.Equal(reserved.SegmentCount, active.SegmentCount);
        }

        NativeOwnerStatistics returned = pool.GetStatistics();
        Assert.Equal(0, returned.RequestedBytes);
        nuint released = pool.TrimRetainedMemory();
        NativeOwnerStatistics trimmed = pool.GetStatistics();
        Assert.Equal(checked((long)released), trimmed.TrimmedBytes);
        Assert.Equal(1, trimmed.TrimCallCount);
        Assert.Equal(0, trimmed.RetainedBytes);
        Assert.Equal(0, trimmed.SegmentCount);
    }

    [Fact]
    public void ArenaStatisticsTrackGenerationReuseWithoutLogicalLeaks()
    {
        using NativeArena arena = new(
            preAllocateBytes: 4_096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        long generation;
        {
            ArenaLease<byte> bytes = arena.Scratch<byte>(128, static writer => writer.Fill(default!));
            NativeOwnerStatistics active = arena.GetStatistics();
            generation = active.Generation;
            Assert.Equal(128, active.RequestedBytes);
            Assert.True(active.RetainedBytes >= 4_096);
            Assert.True(active.SegmentCount >= 1);
            bytes.Access(static view => view.Fill(0x2A));
        }

        arena.ReleaseLeasesToNativeMemory();
        NativeOwnerStatistics released = arena.GetStatistics();
        Assert.Equal(NativeOwnerLifecycle.Active, released.Lifecycle);
        Assert.True(released.Generation > generation);
        Assert.Equal(0, released.RequestedBytes);
        Assert.True(released.RetainedBytes >= 4_096);
        Assert.True(released.AvailableSegmentCount >= 1);
    }

    [Fact]
    public void RegionStatisticsReportTheCurrentLexicalAllocation()
    {
        using (NativeRegion region = new(
            preAllocateBytes: 4_096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory))
        {
            Local<long> values = region.Lease<long>(32, static writer => writer.Fill(default!));
            NativeOwnerStatistics active = region.GetStatistics();
            Assert.Equal(NativeOwnerLifecycle.Active, active.Lifecycle);
            Assert.Equal(32 * sizeof(long), active.RequestedBytes);
            Assert.True(active.RetainedBytes >= 4_096);
            Assert.True(active.SegmentCount >= 1);
            values.Access(static view => view.Fill(7));
        }
    }
}
