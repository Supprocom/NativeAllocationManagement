namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedInitializationFrameTests
{
    [Fact]
    public void NestedPublicationAndOuterFailureKeepDistinctOccupancyAndHistory()
    {
        NativeMemoryBudget budget = new(192);
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(3, 2, 3), budget);
        OperationCanceledException expected = new("outer initialization cancelled");
        OperationCanceledException actual = Assert.Throws<OperationCanceledException>(() =>
            pool.TryRent(2, writer =>
            {
                Assert.Equal(1, pool.CurrentInitializationCountForTest);
                Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
                Assert.Equal(1, pool.CapturePreparedSnapshot().OccupiedSlotCount);
                writer.Write(11);
                using (PreparedPooled<int> nested = pool.Rent(2, nestedWriter =>
                {
                    Assert.Equal(2, pool.CurrentInitializationCountForTest);
                    Assert.Equal(2, pool.CurrentGenerationActiveOperationsForTest);
                    NativePreparedPoolStatistics pending = pool.CapturePreparedSnapshot();
                    Assert.Equal(2, pending.OccupiedSlotCount);
                    Assert.Equal(2, pending.PeakOccupiedSlotCount);
                    Assert.Equal(0, pending.SuccessfulRentCount);
                    Assert.Equal(0, pending.InitializerFailureCount);
                    nestedWriter.Fill(7);
                }))
                {
                    Assert.Equal(1, pool.CurrentInitializationCountForTest);
                    Assert.Equal(1, pool.CapturePreparedSnapshot().SuccessfulRentCount);
                    nested.Access(view =>
                    {
                        Assert.Equal(7, view[0]);
                        Assert.Equal(7, view[1]);
                        Assert.Equal(2, pool.CurrentGenerationActiveOperationsForTest);
                        Assert.Equal(0, pool.CapturePreparedSnapshot().InitializerFailureCount);
                    });
                }
                Assert.Equal(1, pool.CapturePreparedSnapshot().OccupiedSlotCount);
                throw expected;
            }, out _, out _));
        Assert.Same(expected, actual);
        NativePreparedPoolStatistics failed = pool.CapturePreparedSnapshot();
        Assert.Equal(1, failed.SuccessfulRentCount);
        Assert.Equal(1, failed.InitializerFailureCount);
        Assert.Equal(2, failed.PeakOccupiedSlotCount);
        Assert.Equal(0, failed.OccupiedSlotCount);
        Assert.Equal(3, failed.AvailableSlotCount);
        Assert.Equal(0, pool.CurrentInitializationCountForTest);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(24, budget.CaptureStatistics().CommittedBytes);
        if (pool.TryRent(2, static writer => writer.Fill(19), out PreparedPooled<int> reused, out _))
        {
            using (reused)
            {
                Assert.Equal(19, reused.Read(static view => view[1]));
            }
        }
        else
        {
            Assert.Fail("Failed outer initialization must return its slot.");
        }
        Assert.Equal(2, pool.CapturePreparedSnapshot().SuccessfulRentCount);
        Assert.Equal(1, pool.CapturePreparedSnapshot().InitializerFailureCount);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
    }

    [Fact]
    public void EmptyPreparedPublicationStillOccupiesAndReturnsOneRealSlot()
    {
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(1, 2, 1), budget: null);
        using (PreparedPooled<int> empty = pool.Rent(0, writer =>
        {
            Assert.Equal(0, writer.Length);
            Assert.Equal(0, writer.Remaining);
            Assert.Equal(1, pool.CurrentInitializationCountForTest);
            Assert.Equal(1, pool.CapturePreparedSnapshot().OccupiedSlotCount);
            Assert.Equal(0, pool.CapturePreparedSnapshot().SuccessfulRentCount);
        }))
        {
            Assert.Equal(0, empty.Length);
            Assert.Equal(2, empty.Capacity);
            Assert.Equal(1, pool.CapturePreparedSnapshot().SuccessfulRentCount);
            Assert.False(pool.TryRent(0, static _ => { }, out _, out NativePoolExhaustionReason reason));
            Assert.Equal(NativePoolExhaustionReason.NoAvailableSlot, reason);
            Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        }
        NativePreparedPoolStatistics returned = pool.CapturePreparedSnapshot();
        Assert.Equal(1, returned.SuccessfulRentCount);
        Assert.Equal(1, returned.RejectedFullCount);
        Assert.Equal(0, returned.InitializerFailureCount);
        Assert.Equal(1, returned.PeakOccupiedSlotCount);
        Assert.Equal(0, returned.OccupiedSlotCount);
        Assert.Equal(1, returned.AvailableSlotCount);
    }
}
