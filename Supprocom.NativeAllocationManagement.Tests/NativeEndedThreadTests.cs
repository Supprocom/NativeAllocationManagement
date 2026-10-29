namespace Supprocom.NativeAllocationManagement.Tests;

// What a thread kept for its next owners returns once the thread ended and
// later threads start using NAM, not when a collection finalizes it. Tests
// keep the ended thread's state alive, so only that release can free it.
[Collection(EndedThreadGroup.Name)]
public sealed class NativeEndedThreadTests
{
    [Fact]
    public void APoolAnEndedThreadKeptForReuseIsFreed()
    {
        NativePool<int>? pool = null;
        NativeThreadState state = TestThread.Run(() =>
        {
            pool = new NativePool<int>();
            using (pool.Rent(1000, 1))
            {
            }

            pool.Dispose();
        });

        NativePool<int>.Slab slab = pool!.SlabForTest(0);
        Assert.NotEqual(IntPtr.Zero, slab.Pointer);
        NativeEndedThreads.ReleaseEndedForTest();

        Assert.Equal(IntPtr.Zero, slab.Pointer);
        GC.KeepAlive(state);
    }

    [Fact]
    public void ThreadsStartingLaterReleaseWhatAnEndedThreadKept()
    {
        NativePool<int>? pool = null;
        NativeThreadState state = TestThread.Run(() =>
        {
            pool = new NativePool<int>();
            using (pool.Rent(1000, 1))
            {
            }

            pool.Dispose();
        });

        NativePool<int>.Slab slab = pool!.SlabForTest(0);
        for (int started = 0; started < 1000 && slab.Pointer != IntPtr.Zero; started++)
        {
            TestThread.Run(static () => { });
        }

        Assert.Equal(IntPtr.Zero, slab.Pointer);
        GC.KeepAlive(state);
    }

    [Fact]
    public void ALiveThreadKeepsWhatItKeptForReuse()
    {
        using ManualResetEventSlim disposed = new();
        using ManualResetEventSlim end = new();
        NativePool<int>? pool = null;
        NativeThreadState? state = null;
        Thread thread = new(() =>
        {
            state = NativeThreadState.Current;
            pool = new NativePool<int>();
            using (pool.Rent(1000, 1))
            {
            }

            pool.Dispose();
            disposed.Set();
            end.Wait();
        });
        thread.Start();
        disposed.Wait();

        NativeEndedThreads.ReleaseEndedForTest();
        Assert.NotEqual(IntPtr.Zero, pool!.SlabForTest(0).Pointer);

        end.Set();
        thread.Join();
        NativeEndedThreads.ReleaseEndedForTest();
        Assert.Equal(IntPtr.Zero, pool.SlabForTest(0).Pointer);
        // Reachable until here: a collected state would leave its pool to
        // finalization instead of this release.
        GC.KeepAlive(state);
    }

    [Fact]
    public void AnEndedThreadsSharedPoolIsDisposedAndFreedWithAnUndisposedLease()
    {
        NativePool<long>? shared = null;
        NativeThreadState state = TestThread.Run(() =>
        {
            shared = NativePool<long>.Shared;
            _ = shared.Rent(64, 1);
        });

        NativePool<long>.Slab slab = shared!.SlabForTest(0);
        NativeEndedThreads.ReleaseEndedForTest();

        Assert.Equal(NativeOwnerLifecycle.Disposed, shared.CurrentLifecycle);
        Assert.Equal(IntPtr.Zero, slab.Pointer);
        Assert.Throws<NativeAllocationStateException>(() => shared.GetStatistics());
        GC.KeepAlive(state);
    }

    [Fact]
    public void ARetiredSharedPoolIsLeftToItsCoordinator()
    {
        NativePool<short>? shared = null;
        NativeThreadState state = TestThread.Run(() =>
        {
            shared = NativePool<short>.Shared;
            using (shared.Rent(64, 1))
            {
            }

            shared.Retire();
        });

        NativePool<short>.Slab slab = shared!.SlabForTest(0);
        NativeEndedThreads.ReleaseEndedForTest();
        Assert.NotEqual(IntPtr.Zero, slab.Pointer);

        shared.ReleaseRetiredStorage();
        Assert.Equal(IntPtr.Zero, slab.Pointer);
        GC.KeepAlive(state);
    }

    [Fact]
    public void RegionAndArenaKernelsAnEndedThreadKeptAreReleased()
    {
        NativeArena? arena = null;
        NativeThreadState state = TestThread.Run(() =>
        {
            using (NativeRegion region = new())
            {
                _ = region.Lease<int>(1000, 1);
                using NativeRegion nested = new();
                _ = nested.Lease<int>(1000, 1);
            }

            arena = new NativeArena();
            _ = arena.Scratch<int>(1000, 1);
            using (NativeArena nested = new())
            {
                _ = nested.Scratch<int>(1000, 1);
            }

            arena.Dispose();
        });

        Assert.NotNull(state.ReusableRegion);
        Assert.NotNull(state.ReusableArena);
        NativeEndedThreads.ReleaseEndedForTest();

        Assert.Null(state.ReusableRegion);
        Assert.Null(state.ReusableArena);
        Assert.True(state.MoreReusableRegions?.IsEmpty ?? true);
        Assert.True(state.MoreReusableArenas?.IsEmpty ?? true);
        Assert.Throws<NativeAllocationStateException>(() => arena!.GetStatistics());
        Assert.Throws<NativeAllocationStateException>(() => arena!.CaptureDiagnosticSnapshot());
    }

    [Fact]
    public void ATransferBlockAnEndedThreadParkedIsReleased()
    {
        NativeTransfer<int> transfer = default;
        NativeThreadState state = TestThread.Run(() =>
        {
            transfer = NativeTransfer<int>.Create(1000, 1);
            transfer.Dispose();
        });

        NativeBuilderControl<int> control = Assert.IsType<NativeBuilderControl<int>>(transfer.ControlForTest);
        Assert.True(control.HasSpareForTest);
        NativeEndedThreads.ReleaseEndedForTest();

        Assert.False(control.HasSpareForTest);
        GC.KeepAlive(state);
    }

    [Fact]
    public void BudgetedOwnersAnEndedThreadKeptReturnTheirCharges()
    {
        NativeMemoryBudget budget = new(1 << 20);
        NativeThreadState state = TestThread.Run(() =>
        {
            using (NativeRegion region = new(budget, 0, NativeMemoryReturn.ToGarbageCollector))
            {
                _ = region.LeaseUninitialized<byte>(10_000);
            }

            using (NativeArena arena = new(budget, 0, NativeMemoryReturn.ToGarbageCollector))
            {
                _ = arena.ScratchUninitialized<byte>(10_000);
            }

            using NativePool<byte> pool = new(budget, 0, 0, NativeMemoryReturn.ToGarbageCollector);
            using Pooled<byte> lease = pool.RentUninitialized(10_000);
        });

        Assert.NotEqual(0, budget.CaptureStatistics().CommittedBytes);
        NativeEndedThreads.ReleaseEndedForTest();

        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ActiveAllocationCount);
        Assert.Null(state.KeptBudgetedRegion);
        Assert.Null(state.KeptBudgetedArena);
    }
}
