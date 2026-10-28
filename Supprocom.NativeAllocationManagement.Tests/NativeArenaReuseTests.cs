namespace Supprocom.NativeAllocationManagement.Tests;

// A disposed default-policy arena keeps its only segment for the next arena on
// its thread instead of detaching it for its finalizer.
public sealed class NativeArenaReuseTests
{
    [Fact]
    public void ADisposedArenasSegmentServesTheNextArenaOnTheThread()
    {
        RunOnFreshThread(static () =>
        {
            NativeArena first = new();
            first.Scratch<int>(256, 7);
            Assert.Equal(1, first.GetStatistics().FreshSegmentAllocationCount);
            first.Dispose();

            using NativeArena second = new();
            NativeOwnerStatistics adopted = second.GetStatistics();
            Assert.Equal(1, adopted.SegmentCount);
            Assert.Equal(0, adopted.FreshSegmentAllocationCount);
            ArenaLease<int> lease = second.Scratch<int>(256, 3);
            Assert.Equal(3, lease.AsSpan()[255]);
            Assert.Equal(0, second.GetStatistics().FreshSegmentAllocationCount);
        });
    }

    [Fact]
    public void ArenasThatCannotBeReusedLeaveTheSlotEmpty()
    {
        RunOnFreshThread(static () =>
        {
            NativeArena freed = new(4096, NativeMemoryReturn.ToNativeMemory);
            freed.Dispose();
            AssertNextArenaIsFresh();

            NativeArena budgeted = new(new NativeMemoryBudget(65_536), 4096, NativeMemoryReturn.ToGarbageCollector);
            budgeted.Dispose();
            AssertNextArenaIsFresh();

            NativeArena grown = new();
            grown.ScratchUninitialized<byte>(3000);
            grown.ScratchUninitialized<byte>(8000);
            Assert.Equal(2, grown.GetStatistics().SegmentCount);
            grown.Dispose();
            AssertNextArenaIsFresh();

            NativeArena large = new(2 * 1024 * 1024);
            large.Dispose();
            AssertNextArenaIsFresh();
        });
    }

    [Fact]
    public void AnArenaFromAnOlderReuseEpochIsNotReused()
    {
        RunOnFreshThread(static () =>
        {
            NativeArena first = new(4096);
            first.Dispose();
            NativeStorageReuse.InvalidateCurrentThreadForTest();
            AssertNextArenaIsFresh();
        });
    }

    private static void AssertNextArenaIsFresh()
    {
        using NativeArena next = new();
        Assert.Equal(0, next.GetStatistics().SegmentCount);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The worker failure is rethrown on the test thread.")]
    private static void RunOnFreshThread(Action action)
    {
        // A new thread starts with an empty arena reuse slot.
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
