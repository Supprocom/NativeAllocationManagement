using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

// Arenas, pools and region kernels are not finalizable themselves: each takes
// a finalization guard from its thread, and a disposed owner hands it back for
// the next one. An arena's or region's guard belongs to its kernel, which a
// default owner's thread reuses.
public sealed class NativeFinalizationGuardTests
{
    [Fact]
    public void ADisposedOwnersGuardServesTheNextOwnerOnItsThread()
    {
        RunOnFreshThread(static () =>
        {
            NativeArena first = new(64, NativeMemoryReturn.ToNativeMemory);
            NativeFinalizationGuard guard = GuardOf(first);
            first.Dispose();
            Assert.Null(GuardOf(first, allowNull: true));
            Assert.Null(guard.Target);

            NativePool<int> pool = new(16, NativeMemoryReturn.ToNativeMemory);
            Assert.Same(guard, GuardOf(pool));
            Assert.Same(pool, guard.Target);
            pool.Dispose();

            using NativeArena second = new(64, NativeMemoryReturn.ToNativeMemory);
            Assert.Same(guard, GuardOf(second));
            Assert.Same(StateOf(second), guard.Target);
        });
    }

    [Fact]
    public void AnArenaThatReusesARetainedKernelKeepsItsGuard()
    {
        RunOnFreshThread(static () =>
        {
            NativeArena first = new();
            _ = first.ScratchUninitialized<int>(16);
            object kernel = StateOf(first);
            NativeFinalizationGuard guard = GuardOf(first);
            first.Dispose();
            Assert.Same(kernel, guard.Target);

            using NativeArena second = new();
            Assert.Equal(0, second.GetStatistics().FreshSegmentAllocationCount);
            Assert.Same(kernel, StateOf(second));
            Assert.Same(guard, GuardOf(second));
            Assert.Same(kernel, guard.Target);
        });
    }

    [Fact]
    public void ARegionKernelTakesItsThreadsGuardAndHandsItBack()
    {
        Assert.Equal(typeof(object), typeof(NativeRegionKernel).GetMethod("Finalize", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        RunOnFreshThread(static () =>
        {
            NativeArena arena = new(64, NativeMemoryReturn.ToNativeMemory);
            NativeFinalizationGuard guard = GuardOf(arena);
            arena.Dispose();

            NativeRegionKernel region = new(64, NativeMemoryReturn.ToNativeMemory);
            Assert.Same(guard, GuardOf(region));
            Assert.Same(region, guard.Target);
            region.Dispose();
            Assert.Null(GuardOf(region, allowNull: true));
            Assert.Null(guard.Target);

            using NativePool<int> pool = new(16, NativeMemoryReturn.ToNativeMemory);
            Assert.Same(guard, GuardOf(pool));
        });
    }

    // A budgeted region disposed with the garbage-collector policy detaches
    // its storage, which stays charged until its guard's finalizer frees it.
    [Fact]
    public void ADetachedRegionIsFreedByItsGuardsFinalizer()
    {
        NativeMemoryBudget budget = new(1 << 20);
        DisposeDetachedRegion(budget);
        for (int attempt = 0; attempt < 20 && budget.CaptureStatistics().CommittedBytes != 0; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DisposeDetachedRegion(NativeMemoryBudget budget)
    {
        NativeRegionKernel region = new(256, NativeMemoryReturn.ToGarbageCollector, budget);
        NativeFinalizationGuard guard = GuardOf(region);
        region.Dispose();
        Assert.Same(region, guard.Target);
        Assert.NotEqual(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void LeakedOwnersStillReturnTheirStorageThroughTheirGuards()
    {
        NativeMemoryBudget budget = new(1 << 20);
        LeakArenaPoolAndRegion(budget);
        for (int attempt = 0; attempt < 20 && budget.CaptureStatistics().CommittedBytes != 0; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The owners are leaked on purpose: their guards must return the storage.")]
    private static void LeakArenaPoolAndRegion(NativeMemoryBudget budget)
    {
        NativeArena arena = new(budget, 256, NativeMemoryReturn.ToNativeMemory);
        _ = arena.ScratchUninitialized<int>(16);
        NativePool<int> pool = new(budget, 16, 0, NativeMemoryReturn.ToNativeMemory);
        using (pool.RentUninitialized(16))
        {
        }

        _ = new NativeRegionKernel(256, NativeMemoryReturn.ToNativeMemory, budget);

        Assert.NotEqual(0, budget.CaptureStatistics().CommittedBytes);
    }

    // The object holding an owner's state: an arena's kernel, or the owner.
    private static object StateOf(object owner) =>
        owner is NativeArena
            ? typeof(NativeArena).GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!
            : owner;

    private static NativeFinalizationGuard GuardOf(object owner, bool allowNull = false)
    {
        object state = StateOf(owner);
        object? guard = state.GetType().GetField("_guard", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state);
        if (!allowNull)
        {
            Assert.NotNull(guard);
        }

        return (NativeFinalizationGuard)guard!;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The worker failure is rethrown on the test thread.")]
    private static void RunOnFreshThread(Action action)
    {
        // A new thread starts with no spare guard and empty reuse slots.
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
