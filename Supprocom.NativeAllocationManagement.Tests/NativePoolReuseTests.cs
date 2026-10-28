namespace Supprocom.NativeAllocationManagement.Tests;

// A disposed default-policy pool keeps its slabs for the next pool its thread
// creates instead of detaching them for its finalizer.
public sealed class NativePoolReuseTests
{
    [Fact]
    public void ADisposedPoolsSlabsServeTheNextPoolOnTheThread()
    {
        RunOnFreshThread(static () =>
        {
            NativePool<int> first = new();
            using (Pooled<int> lease = first.Rent(16, 7))
            {
                Assert.Equal(7, lease.AsSpan()[15]);
            }

            Assert.Equal(1, first.GetStatistics().FreshSegmentAllocationCount);
            first.Dispose();

            using NativePool<int> second = new();
            NativeOwnerStatistics adopted = second.GetStatistics();
            Assert.Equal(1, adopted.SegmentCount);
            Assert.Equal(0, adopted.FreshSegmentAllocationCount);
            using Pooled<int> reused = second.Rent(16, 3);
            Assert.Equal(3, reused.AsSpan()[15]);
            Assert.Equal(0, second.GetStatistics().FreshSegmentAllocationCount);
        });
    }

    [Fact]
    public void AStaleLeaseOfTheDisposedPoolCannotReachTheAdoptedSlab()
    {
        RunOnFreshThread(static () =>
        {
            NativePool<int> first = new();
            Pooled<int> lease = first.Rent(4, 1);
            Pooled<int> stale = lease;
            lease.Dispose();
            first.Dispose();

            using NativePool<int> second = new();
            using Pooled<int> current = second.Rent(4, 2);
            bool refused = false;
            try
            {
                _ = stale.AsSpan()[0];
            }
            catch (NativeAllocationException)
            {
                refused = true;
            }

            Assert.True(refused);
            Assert.Equal(2, current.AsSpan()[0]);
        });
    }

    [Fact]
    public void PoolsThatCannotBeReusedLeaveTheSlotEmpty()
    {
        RunOnFreshThread(static () =>
        {
            NativePool<int> freed = new(16, NativeMemoryReturn.ToNativeMemory);
            freed.Dispose();
            AssertNextPoolIsFresh();

            NativePool<int> budgeted = new(new NativeMemoryBudget(65_536), 16, 0, NativeMemoryReturn.ToGarbageCollector);
            budgeted.Dispose();
            AssertNextPoolIsFresh();

            NativePool<int> large = new();
            using (large.RentUninitialized(512 * 1024))
            {
            }

            large.Dispose();
            AssertNextPoolIsFresh();
        });
    }

    [Fact]
    public void APoolFromAnOlderReuseEpochIsNotReused()
    {
        RunOnFreshThread(static () =>
        {
            NativePool<int> first = new(16);
            first.Dispose();
            NativeStorageReuse.InvalidateCurrentThreadForTest();
            AssertNextPoolIsFresh();
        });
    }

    private static void AssertNextPoolIsFresh()
    {
        using NativePool<int> next = new();
        Assert.Equal(0, next.GetStatistics().SegmentCount);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The worker failure is rethrown on the test thread.")]
    private static void RunOnFreshThread(Action action)
    {
        // A new thread starts with an empty pool reuse slot.
        Exception? failure = null;
        Thread worker = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
