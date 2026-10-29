namespace Supprocom.NativeAllocationManagement.Tests;

// Nested scopes each reuse a disposed owner's storage: the reuse slot has
// more owners behind it.
public sealed class NativeNestedReuseTests
{
    [Fact]
    public void NestedRegionsBothReuseStorageOnTheNextPass()
    {
        RunOnFreshThread(static () =>
        {
            for (int pass = 0; pass < 3; pass++)
            {
                NativeRegion outer = new();
                NativeRegion inner = new();
                Assert.Equal(pass, outer.Lease<int>(4, pass).AsSpan()[3]);
                Assert.Equal(pass + 1, inner.Lease<int>(4, pass + 1).AsSpan()[3]);
                long fresh = outer.GetStatistics().FreshSegmentAllocationCount + inner.GetStatistics().FreshSegmentAllocationCount;
                Assert.Equal(pass == 0 ? 2 : 0, fresh);
                inner.Dispose();
                outer.Dispose();
            }
        });
    }

    [Fact]
    public void NestedArenasBothReuseStorageOnTheNextPass()
    {
        RunOnFreshThread(static () =>
        {
            for (int pass = 0; pass < 3; pass++)
            {
                NativeArena outer = new();
                NativeArena inner = new();
                Assert.Equal(pass, outer.Scratch<int>(4, pass).AsSpan()[3]);
                Assert.Equal(pass + 1, inner.Scratch<int>(4, pass + 1).AsSpan()[3]);
                long fresh = outer.GetStatistics().FreshSegmentAllocationCount + inner.GetStatistics().FreshSegmentAllocationCount;
                Assert.Equal(pass == 0 ? 2 : 0, fresh);
                inner.Dispose();
                outer.Dispose();
            }
        });
    }

    [Fact]
    public void NestedPoolsBothReuseStorageOnTheNextPass()
    {
        RunOnFreshThread(static () =>
        {
            for (int pass = 0; pass < 3; pass++)
            {
                NativePool<int> outer = new();
                NativePool<int> inner = new();
                using (Pooled<int> first = outer.Rent(4, pass))
                using (Pooled<int> second = inner.Rent(4, pass + 1))
                {
                    Assert.Equal(pass, first.AsSpan()[3]);
                    Assert.Equal(pass + 1, second.AsSpan()[3]);
                }

                long fresh = outer.GetStatistics().FreshSegmentAllocationCount + inner.GetStatistics().FreshSegmentAllocationCount;
                Assert.Equal(pass == 0 ? 2 : 0, fresh);
                inner.Dispose();
                outer.Dispose();
            }
        });
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The worker failure is rethrown on the test thread.")]
    private static void RunOnFreshThread(Action action)
    {
        // A new thread starts with empty reuse slots.
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
