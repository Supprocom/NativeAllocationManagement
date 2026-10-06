namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedFixedShapeSearchTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(17)]
    public void AlternatingShorterShapesUseEverySingleClassSlotWithoutChangingBacking(int capacity)
    {
        using NativePool<int> pool = new(new NativePoolPreparation(3, capacity, 3), budget: null);
        long backing = pool.GetStatistics().RetainedBytes;
        for (int round = 0; round < 4; round++)
        {
            using Pooled<int> empty = pool.Rent(0, static _ => { });
            using Pooled<int> shorter = pool.Rent(capacity - 1, static writer => writer.Fill(17));
            using Pooled<int> full = pool.Rent(capacity, static writer => writer.Fill(19));
            Assert.Equal(capacity, empty.Capacity);
            Assert.Equal(capacity, shorter.Capacity);
            Assert.Equal(capacity, full.Capacity);
            Assert.Equal(0, pool.CapturePreparedSnapshot().AvailableSlotCount);
            Assert.Equal(3, pool.CapturePreparedSnapshot().OccupiedSlotCount);
            Assert.Equal(19, full.Read(static view => view[0]));
            Assert.Equal(capacity - 1, shorter.Read(static view => view.Length));
            Assert.False(pool.TryRent(0, static _ => { }, out _, out NativePoolExhaustionReason reason));
            Assert.Equal(NativePoolExhaustionReason.NoAvailableSlot, reason);
            Assert.Equal(backing, pool.GetStatistics().RetainedBytes);
        }
        NativePreparedPoolStatistics done = pool.CapturePreparedSnapshot();
        Assert.Equal(12, done.SuccessfulRentCount);
        Assert.Equal(4, done.RejectedFullCount);
        Assert.Equal(3, done.PeakOccupiedSlotCount);
        Assert.Equal(3, done.AvailableSlotCount);
        Assert.Equal(1, pool.GetStatistics().FreshSegmentAllocationCount);
    }

    [Fact]
    public void CachedShortShapeSurvivesOversizeRefusalAndFailedDifferentShape()
    {
        using NativePool<int> pool = new(new NativePoolPreparation(2, 3, 2), budget: null);
        Pooled<int> old = pool.Rent(1, static writer => writer.Write(23));
        Pooled<int> stale = old;
        old.Dispose();
        Assert.Throws<InvalidOperationException>(() => pool.Rent(4, static writer => writer.Fill(0)));
        Assert.Throws<OperationCanceledException>(() => pool.Rent(2, static writer =>
        {
            writer.Write(29);
            throw new OperationCanceledException();
        }));
        using Pooled<int> current = pool.Rent(3, static writer => writer.Fill(31));
        Assert.Equal(31, current.Read(static view => view[2]));
        try
        {
            stale.Dispose();
            Assert.Fail("Returned-slot search must not restore old release authority.");
        }
        catch (NativeAllocationReturnedException)
        {
        }
        NativePreparedPoolStatistics state = pool.CapturePreparedSnapshot();
        Assert.Equal(2, state.SuccessfulRentCount);
        Assert.Equal(1, state.InitializerFailureCount);
        Assert.Equal(1, state.RejectedShapeCount);
        Assert.Equal(0, state.RejectedFullCount);
        Assert.Equal(1, state.OccupiedSlotCount);
        Assert.Equal(1, state.AvailableSlotCount);
    }

    [Fact]
    public void TrimmedClassHasNoRefillOrReusableAuthorityForPhysicallyFreedSlots()
    {
        NativeMemoryBudget budget = new(192);
        using NativePool<int> pool = new(new NativePoolPreparation(3, 3, 1), budget);
        Pooled<int> survivor = pool.Rent(1, static writer => writer.Write(37));
        try
        {
            Assert.Equal((nuint)128, pool.TrimRetainedMemory());
            Assert.Equal(64, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(1, pool.CapturePreparedSnapshot().RetainedSlotCount);
            Assert.False(pool.TryRent(3, static writer => writer.Fill(0), out _, out NativePoolExhaustionReason reason));
            Assert.Equal(NativePoolExhaustionReason.NoAvailableSlot, reason);
            Assert.Equal(37, survivor.Read(static view => view[0]));
        }
        finally { survivor.Dispose(); }
        using (Pooled<int> reused = pool.Rent(3, static writer => writer.Fill(41)))
        {
            Assert.Equal(41, reused.Read(static view => view[2]));
        }
        Assert.Equal((nuint)64, pool.TrimRetainedMemory());
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.False(pool.TryRent(0, static _ => { }, out _, out NativePoolExhaustionReason empty));
        Assert.Equal(NativePoolExhaustionReason.NoAvailableSlot, empty);
        Assert.Equal(3, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(3, budget.CaptureStatistics().FreeCount);
    }
}
