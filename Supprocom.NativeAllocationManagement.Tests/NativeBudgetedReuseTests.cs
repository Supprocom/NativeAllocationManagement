namespace Supprocom.NativeAllocationManagement.Tests;

// Budgeted owners with the garbage-collector policy keep their storage for
// the next owner with the same budget on their thread, and a budget that
// refuses an admission first takes back what every thread kept for it. Each
// test uses its own budget, so other tests' threads cannot change its charges.
public sealed class NativeBudgetedReuseTests
{
    private const int Lease = 40_000;

    [Fact]
    public void BudgetedOwnersMadePerScopeReuseTheirStorageWithinTheBudget()
    {
        RunOnFreshThread(static () =>
        {
            // Room for about two leases: owners made per scope must reuse.
            NativeMemoryBudget budget = new(2 * (Lease + 4096));
            for (int index = 0; index < 1_000; index++)
            {
                using NativeRegion region = new(budget, 0, NativeMemoryReturn.Reuse);
                region.LeaseUninitialized<byte>(Lease).AsSpan()[^1] = 1;
                if (index != 0)
                {
                    Assert.Equal(0, region.GetStatistics().FreshSegmentAllocationCount);
                }
            }

            for (int index = 0; index < 1_000; index++)
            {
                using NativeArena arena = new(budget, 0, NativeMemoryReturn.Reuse);
                arena.ScratchUninitialized<byte>(Lease).AsSpan()[^1] = 1;
                if (index != 0)
                {
                    Assert.Equal(0, arena.GetStatistics().FreshSegmentAllocationCount);
                }
            }

            for (int index = 0; index < 1_000; index++)
            {
                using NativePool<byte> pool = new(budget, 0, 0, NativeMemoryReturn.Reuse);
                using Pooled<byte> lease = pool.RentUninitialized(Lease);
                lease.AsSpan()[^1] = 1;
                if (index != 0)
                {
                    Assert.Equal(0, pool.GetStatistics().FreshSegmentAllocationCount);
                }
            }

            Assert.True(budget.CaptureStatistics().CommittedBytes <= 2 * (Lease + 4096));
        });
    }

    [Fact]
    public void ARefusedAdmissionTakesBackWhatItsOwnThreadKept()
    {
        RunOnFreshThread(static () =>
        {
            NativeMemoryBudget budget = new(3 * Lease);
            using (NativeRegion region = new(budget, 0, NativeMemoryReturn.Reuse))
            {
                _ = region.LeaseUninitialized<byte>(2 * Lease);
            }

            Assert.NotNull(NativeThreadState.Current.KeptBudgetedRegion);
            Assert.NotEqual(0, budget.CaptureStatistics().CommittedBytes);

            // Fits only once the kept region's storage is back in the budget.
            using NativeArena arena = new(budget, 0, NativeMemoryReturn.Reuse);
            _ = arena.ScratchUninitialized<byte>(2 * Lease);
            Assert.Null(NativeThreadState.Current.KeptBudgetedRegion);
        });
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The worker failure is checked on the test thread.")]
    public void ARefusedAdmissionTakesBackWhatALiveThreadKept()
    {
        NativeMemoryBudget budget = new(3 * Lease);
        using ManualResetEventSlim kept = new();
        using ManualResetEventSlim taken = new();
        Exception? failure = null;
        NativeThreadState? keeper = null;
        NativePool<byte>? pool = null;
        Thread thread = new(() =>
        {
            try
            {
                keeper = NativeThreadState.Current;
                // A slab of 65,536 bytes: pools round capacity up to a power of two.
                pool = new NativePool<byte>(budget, 0, 0, NativeMemoryReturn.Reuse);
                using (pool.RentUninitialized(Lease))
                {
                }

                pool.Dispose();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                kept.Set();
            }

            if (failure is not null || !taken.Wait(TimeSpan.FromSeconds(30)))
            {
                return;
            }

            try
            {
                // The disposed pool stays disposed, and the next one starts afresh.
                Assert.Throws<NativeAllocationDisposedException>(() => pool!.GetStatistics());
                using NativePool<byte> next = new(budget, 0, 0, NativeMemoryReturn.Reuse);
                using Pooled<byte> lease = next.RentUninitialized(16);
                Assert.Equal(1, next.GetStatistics().FreshSegmentAllocationCount);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
        Assert.True(kept.Wait(TimeSpan.FromSeconds(30)));
        Assert.Null(failure);

        // Fits only once the other thread's kept slab is back in the budget.
        using (NativeRegion region = new(budget, 0, NativeMemoryReturn.Reuse))
        {
            _ = region.LeaseUninitialized<byte>(2 * Lease);
            Assert.Equal(1, budget.CaptureStatistics().ActiveAllocationCount);
        }

        taken.Set();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(failure);
        GC.KeepAlive(keeper);
    }

    [Fact]
    public void ARefusedAdmissionTakesBackWhatAnEndedThreadKept()
    {
        NativeMemoryBudget budget = new(3 * Lease);
        NativeThreadState? keeper = null;
        RunOnFreshThread(() =>
        {
            keeper = NativeThreadState.Current;
            using NativeArena arena = new(budget, 0, NativeMemoryReturn.Reuse);
            _ = arena.ScratchUninitialized<byte>(2 * Lease);
        });

        using (NativeRegion region = new(budget, 0, NativeMemoryReturn.Free))
        {
            _ = region.LeaseUninitialized<byte>(2 * Lease);
        }

        Assert.Null(keeper!.KeptBudgetedArena);
    }

    [Fact]
    public void ABudgetWithNothingKeptStillRefuses()
    {
        NativeMemoryBudget budget = new(2 * Lease);
        using NativeRegion live = new(budget, 0, NativeMemoryReturn.Reuse);
        _ = live.LeaseUninitialized<byte>(Lease);
        Assert.Throws<NativeMemoryBudgetExceededException>(() =>
        {
            using NativeArena arena = new(budget, 0, NativeMemoryReturn.Reuse);
            _ = arena.ScratchUninitialized<byte>(Lease);
        });
    }

    [Fact]
    public void AKeptBudgetedRegionEndsTheLifetimeOfItsHandles()
    {
        RunOnFreshThread(static () =>
        {
            NativeMemoryBudget budget = new(1 << 20);
            NativeRegion region = new(budget, 0, NativeMemoryReturn.Reuse);
            NativeRegion copy = region;
            _ = region.LeaseUninitialized<byte>(64);
            region.Dispose();
            Assert.NotNull(NativeThreadState.Current.KeptBudgetedRegion);
            NativeAllocationDisposedException? ended = null;
            try
            {
                _ = copy.CaptureDiagnosticSnapshot();
            }
            catch (NativeAllocationDisposedException exception)
            {
                ended = exception;
            }

            Assert.NotNull(ended);
            copy.Dispose();

            NativeArena arena = new(budget, 0, NativeMemoryReturn.Reuse);
            _ = arena.ScratchUninitialized<byte>(64);
            arena.Dispose();
            Assert.NotNull(NativeThreadState.Current.KeptBudgetedArena);
            Assert.Equal(NativeOwnerLifecycle.Disposed, arena.CaptureDiagnosticSnapshot().Lifecycle);
            Assert.Throws<NativeAllocationDisposedException>(() => arena.GetStatistics());
            arena.Dispose();
        });
    }

    // Kept storage would serve owners other than the one a trace recorded, so a
    // budget that traces keeps nothing. Its owners free their storage when
    // disposed instead, so tracing does not change whether the budget runs out.
    [Fact]
    public void ABudgetThatTracesKeepsNothingAndGetsItsBytesBackOnDisposal()
    {
        RunOnFreshThread(static () =>
        {
            NativeMemoryBudget budget = new(1 << 20, traceCapacity: 64);
            using (NativeRegion region = new(budget, 0, NativeMemoryReturn.Reuse))
            {
                _ = region.LeaseUninitialized<byte>(64);
            }

            using (NativeArena arena = new(budget, 0, NativeMemoryReturn.Reuse))
            {
                _ = arena.ScratchUninitialized<byte>(64);
            }

            using (NativePool<byte> pool = new(budget, 0, 0, NativeMemoryReturn.Reuse))
            {
                using Pooled<byte> lease = pool.RentUninitialized(64);
            }

            Assert.Null(NativeThreadState.Current.KeptBudgetedRegion);
            Assert.Null(NativeThreadState.Current.KeptBudgetedArena);
            NativeMemoryBudgetStatistics statistics = budget.CaptureStatistics();
            Assert.Equal(0, statistics.CommittedBytes);
            Assert.NotEqual(0, statistics.AllocationCount);
            Assert.Equal(statistics.AllocationCount, statistics.FreeCount);
        });
    }

    [Fact]
    public void AnOwnerWithAnotherBudgetLeavesTheKeptKernelAndStartsAfresh()
    {
        RunOnFreshThread(static () =>
        {
            NativeMemoryBudget first = new(1 << 20);
            NativeMemoryBudget second = new(1 << 20);
            using (NativeRegion region = new(first, 0, NativeMemoryReturn.Reuse))
            {
                _ = region.LeaseUninitialized<byte>(64);
            }

            using (NativeRegion other = new(second, 0, NativeMemoryReturn.Reuse))
            {
                _ = other.LeaseUninitialized<byte>(64);
                Assert.Equal(1, other.GetStatistics().FreshSegmentAllocationCount);
            }

            // The second region's disposal displaced the first budget's kernel.
            Assert.Equal(0, first.CaptureStatistics().CommittedBytes);
            Assert.NotEqual(0, second.CaptureStatistics().CommittedBytes);
        });
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The worker failure is rethrown on the test thread.")]
    private static void RunOnFreshThread(Action action)
    {
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
        })
        {
            IsBackground = true
        };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
