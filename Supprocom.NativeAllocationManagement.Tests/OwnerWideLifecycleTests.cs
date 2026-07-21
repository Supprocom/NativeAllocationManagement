using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class OwnerWideLifecycleTests
{
    [Fact]
    public async Task PoolStrictReturnRejectsRetiredOperationsAndThenReleasesEveryGeneration()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        BusyGenerationSet busy = StartBusyPoolGenerations(pool, 2);
        long generationBeforeReturn = pool.GenerationCounterForTest;

        try
        {
            WaitForEntry(busy);
            {
                Pooled<int> current = pool.Rent(1);
                current[0] = 7;

                NativeAllocationInUseException rejection =
                    Assert.Throws<NativeAllocationInUseException>(pool.ReturnMemoryToNativeMemory);
                Assert.Equal(2, rejection.ActiveOperationCount);
                Assert.Equal(2, rejection.OwnerWideBusyGenerationCount);
                Assert.Equal(0, rejection.OwnerWideLeaseReturnCount);
                Assert.Equal(NativeOwnerLifecycle.Active, pool.CurrentLifecycle);
                Assert.Equal(2, pool.RetiredGenerationCountForTest);
                Assert.Equal(generationBeforeReturn, pool.GenerationCounterForTest);

            }
            await ReleaseBusyAsync(busy, [1, 0]);
            Assert.Equal(0, pool.RetiredGenerationCountForTest);
            long freeBeforeReturn = NativeMemoryTestHooks.Snapshot().FreeCount;
            pool.ReturnMemoryToNativeMemory();
            Assert.Equal(NativeOwnerLifecycle.Returned, pool.CurrentLifecycle);
            Assert.True(NativeMemoryTestHooks.Snapshot().FreeCount > freeBeforeReturn);
            pool.Dispose();
        }
        finally
        {
            await FinishBusyAsync(busy);
            NativeMemoryTestHooks.Reset();
            pool.Dispose();
        }
    }

    [Fact]
    public async Task ArenaStrictReturnRejectsRetiredOperationsAndThenReleasesEveryGeneration()
    {
        NativeMemoryTestHooks.Reset();
        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        BusyGenerationSet busy = StartBusyArenaGenerations(arena, 2);

        try
        {
            WaitForEntry(busy);
            {
                ArenaLease<int> current = arena.Scratch<int>(1);
                current[0] = 7;

                NativeAllocationInUseException rejection =
                    Assert.Throws<NativeAllocationInUseException>(arena.ReturnMemoryToNativeMemory);
                Assert.Equal(2, rejection.ActiveOperationCount);
                Assert.Equal(2, rejection.OwnerWideBusyGenerationCount);
                Assert.Equal(NativeOwnerLifecycle.Active, arena.CurrentLifecycle);
                Assert.Equal(2, arena.RetiredGenerationCountForTest);

            }
            await ReleaseBusyAsync(busy, [1, 0]);
            Assert.Equal(0, arena.RetiredGenerationCountForTest);
            long freeBeforeReturn = NativeMemoryTestHooks.Snapshot().FreeCount;
            arena.ReturnMemoryToNativeMemory();
            Assert.Equal(NativeOwnerLifecycle.Returned, arena.CurrentLifecycle);
            Assert.True(NativeMemoryTestHooks.Snapshot().FreeCount > freeBeforeReturn);
            arena.Dispose();
        }
        finally
        {
            await FinishBusyAsync(busy);
            NativeMemoryTestHooks.Reset();
            arena.Dispose();
        }
    }

    [Fact]
    public async Task PoolStrictDisposeUsesOwnerWideAdmissionAndReleasesRetiredStorage()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        BusyGenerationSet busy = StartBusyPoolGenerations(pool, 2);
        long generationBeforeDispose = pool.GenerationCounterForTest;

        try
        {
            WaitForEntry(busy);

            NativeAllocationInUseException rejection = Assert.Throws<NativeAllocationInUseException>(pool.Dispose);
            Assert.Equal(2, rejection.ActiveOperationCount);
            Assert.Equal(2, rejection.OwnerWideBusyGenerationCount);
            Assert.Equal(NativeOwnerLifecycle.Active, pool.CurrentLifecycle);
            Assert.Equal(generationBeforeDispose, pool.GenerationCounterForTest);

            await ReleaseBusyAsync(busy, [0, 1]);
            long freeBeforeDispose = NativeMemoryTestHooks.Snapshot().FreeCount;
            pool.Dispose();
            Assert.Equal(NativeOwnerLifecycle.Disposed, pool.CurrentLifecycle);
            Assert.True(NativeMemoryTestHooks.Snapshot().FreeCount > freeBeforeDispose);
        }
        finally
        {
            await FinishBusyAsync(busy);
            NativeMemoryTestHooks.Reset();
            pool.Dispose();
        }
    }

    [Fact]
    public async Task ArenaStrictDisposeUsesOwnerWideAdmissionAndReleasesRetiredStorage()
    {
        NativeMemoryTestHooks.Reset();
        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        BusyGenerationSet busy = StartBusyArenaGenerations(arena, 2);

        try
        {
            WaitForEntry(busy);

            NativeAllocationInUseException rejection = Assert.Throws<NativeAllocationInUseException>(arena.Dispose);
            Assert.Equal(2, rejection.ActiveOperationCount);
            Assert.Equal(2, rejection.OwnerWideBusyGenerationCount);
            Assert.Equal(NativeOwnerLifecycle.Active, arena.CurrentLifecycle);

            await ReleaseBusyAsync(busy, [0, 1]);
            long freeBeforeDispose = NativeMemoryTestHooks.Snapshot().FreeCount;
            arena.Dispose();
            Assert.Equal(NativeOwnerLifecycle.Disposed, arena.CurrentLifecycle);
            Assert.True(NativeMemoryTestHooks.Snapshot().FreeCount > freeBeforeDispose);
        }
        finally
        {
            await FinishBusyAsync(busy);
            NativeMemoryTestHooks.Reset();
            arena.Dispose();
        }
    }

    [Fact]
    public async Task PoolGarbageCollectorReturnDetachesRetiredGenerationsAndNeverReusesTheirSegments()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        BusyGenerationSet busy = StartBusyPoolGenerations(pool, 2);

        try
        {
            WaitForEntry(busy);
            long freeBeforeDrain = 0;
            {
                Pooled<int> current = pool.Rent(1);
                current[0] = 8;
                long freeBeforeReturn = NativeMemoryTestHooks.Snapshot().FreeCount;

                pool.ReturnMemoryToGarbageCollector();
                Assert.Equal(NativeOwnerLifecycle.Returned, pool.CurrentLifecycle);
                Assert.Equal(freeBeforeReturn, NativeMemoryTestHooks.Snapshot().FreeCount);
                pool.LeaseFromMemory();
                Pooled<int> fresh = pool.Rent(1);
                Assert.Equal([4L], pool.CurrentSegmentOrdinalsForTest);
                Assert.Equal(0, fresh[0]);
                freeBeforeDrain = NativeMemoryTestHooks.Snapshot().FreeCount;
            }
            await ReleaseBusyAsync(busy, [1, 0]);
            Assert.Equal(freeBeforeDrain, NativeMemoryTestHooks.Snapshot().FreeCount);
            pool.Dispose();
            ForceFinalizationUntilDetachedStorageIsGone();
            Assert.Equal(0, NativeMemoryTestHooks.Snapshot().DetachedNativeBytes);
        }
        finally
        {
            await FinishBusyAsync(busy);
            NativeMemoryTestHooks.Reset();
            pool.Dispose();
        }
    }

    [Fact]
    public async Task ArenaGarbageCollectorReturnDetachesRetiredGenerationsAndNeverReusesTheirSegments()
    {
        NativeMemoryTestHooks.Reset();
        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        BusyGenerationSet busy = StartBusyArenaGenerations(arena, 2);

        try
        {
            WaitForEntry(busy);
            long freeBeforeDrain = 0;
            {
                ArenaLease<int> current = arena.Scratch<int>(1);
                current[0] = 8;
                long freeBeforeReturn = NativeMemoryTestHooks.Snapshot().FreeCount;

                arena.ReturnMemoryToGarbageCollector();
                Assert.Equal(NativeOwnerLifecycle.Returned, arena.CurrentLifecycle);
                Assert.Equal(freeBeforeReturn, NativeMemoryTestHooks.Snapshot().FreeCount);
                arena.LeaseFromMemory();
                ArenaLease<int> fresh = arena.Scratch<int>(1);
                Assert.Equal([4L], arena.CurrentSegmentOrdinalsForTest);
                Assert.Equal(0, fresh[0]);
                freeBeforeDrain = NativeMemoryTestHooks.Snapshot().FreeCount;
            }
            await ReleaseBusyAsync(busy, [1, 0]);
            Assert.Equal(freeBeforeDrain, NativeMemoryTestHooks.Snapshot().FreeCount);
            arena.Dispose();
            ForceFinalizationUntilDetachedStorageIsGone();
            Assert.Equal(0, NativeMemoryTestHooks.Snapshot().DetachedNativeBytes);
        }
        finally
        {
            await FinishBusyAsync(busy);
            NativeMemoryTestHooks.Reset();
            arena.Dispose();
        }
    }

    [Fact]
    public async Task PoolGarbageCollectorDisposalUsesOwnerWideAdmissionAndNeverFreesDetachedStorage()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToGarbageCollector);
        BusyGenerationSet busy = StartBusyPoolGenerations(pool, 2);

        try
        {
            WaitForEntry(busy);
            pool.ReturnMemoryToGarbageCollector();

            NativeAllocationInUseException rejection = Assert.Throws<NativeAllocationInUseException>(pool.Dispose);
            Assert.Equal(2, rejection.ActiveOperationCount);
            Assert.Equal(2, rejection.OwnerWideBusyGenerationCount);
            Assert.Equal(NativeOwnerLifecycle.Returned, pool.CurrentLifecycle);

            await ReleaseBusyAsync(busy, [1, 0]);
            long freeBeforeDispose = NativeMemoryTestHooks.Snapshot().FreeCount;
            pool.Dispose();
            Assert.Equal(NativeOwnerLifecycle.Disposed, pool.CurrentLifecycle);
            Assert.Equal(freeBeforeDispose, NativeMemoryTestHooks.Snapshot().FreeCount);
            ForceFinalizationUntilDetachedStorageIsGone();
            Assert.Equal(0, NativeMemoryTestHooks.Snapshot().DetachedNativeBytes);
        }
        finally
        {
            await FinishBusyAsync(busy);
            NativeMemoryTestHooks.Reset();
            pool.Dispose();
        }
    }

    [Fact]
    public async Task ArenaGarbageCollectorDisposalUsesOwnerWideAdmissionAndNeverFreesDetachedStorage()
    {
        NativeMemoryTestHooks.Reset();
        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToGarbageCollector);
        BusyGenerationSet busy = StartBusyArenaGenerations(arena, 2);

        try
        {
            WaitForEntry(busy);
            arena.ReturnMemoryToGarbageCollector();

            NativeAllocationInUseException rejection = Assert.Throws<NativeAllocationInUseException>(arena.Dispose);
            Assert.Equal(2, rejection.ActiveOperationCount);
            Assert.Equal(2, rejection.OwnerWideBusyGenerationCount);
            Assert.Equal(NativeOwnerLifecycle.Returned, arena.CurrentLifecycle);

            await ReleaseBusyAsync(busy, [1, 0]);
            long freeBeforeDispose = NativeMemoryTestHooks.Snapshot().FreeCount;
            arena.Dispose();
            Assert.Equal(NativeOwnerLifecycle.Disposed, arena.CurrentLifecycle);
            Assert.Equal(freeBeforeDispose, NativeMemoryTestHooks.Snapshot().FreeCount);
            ForceFinalizationUntilDetachedStorageIsGone();
            Assert.Equal(0, NativeMemoryTestHooks.Snapshot().DetachedNativeBytes);
        }
        finally
        {
            await FinishBusyAsync(busy);
            NativeMemoryTestHooks.Reset();
            arena.Dispose();
        }
    }

    [Fact]
    public async Task PoolQuarantinedStorageFollowsBothWholeMemoryPolicies()
    {
        foreach (NativeMemoryReturn policy in Enum.GetValues<NativeMemoryReturn>())
        {
            NativeMemoryTestHooks.Reset();
            NativePool<int> pool = new(returnMemoryOnDispose: policy);
            BusyGenerationSet busy = StartBusyPoolGenerations(pool, 1);

            try
            {
                WaitForEntry(busy);
                NativeMemoryTestHooks.FailAfterCommitBoundary(1);
                busy.Allows[0].Set();
                NativeAllocationQuarantinedException failure =
                    Assert.IsType<NativeAllocationQuarantinedException>(await busy.Workers[0]);
                Assert.Equal("clear", failure.Boundary);
                Assert.Equal(1, pool.QuarantinedGenerationCountForTest);
                long freeBeforeReturn = NativeMemoryTestHooks.Snapshot().FreeCount;

                if (policy == NativeMemoryReturn.ToNativeMemory)
                {
                    pool.ReturnMemoryToNativeMemory();
                    Assert.Equal(0, pool.QuarantinedGenerationCountForTest);
                    Assert.True(NativeMemoryTestHooks.Snapshot().FreeCount > freeBeforeReturn);
                }
                else
                {
                    pool.ReturnMemoryToGarbageCollector();
                    Assert.Equal(freeBeforeReturn, NativeMemoryTestHooks.Snapshot().FreeCount);
                    pool.LeaseFromMemory();
                    Pooled<int> fresh = pool.Rent(1);
                    Assert.Equal([2L], pool.CurrentSegmentOrdinalsForTest);
                    fresh.Dispose();
                    pool.Dispose();
                    Assert.Equal(freeBeforeReturn, NativeMemoryTestHooks.Snapshot().FreeCount);
                    ForceFinalizationUntilDetachedStorageIsGone();
                }
            }
            finally
            {
                await FinishBusyAsync(busy);
                NativeMemoryTestHooks.Reset();
                pool.Dispose();
            }
        }
    }

    [Fact]
    public async Task ArenaQuarantinedStorageFollowsBothWholeMemoryPolicies()
    {
        foreach (NativeMemoryReturn policy in Enum.GetValues<NativeMemoryReturn>())
        {
            NativeMemoryTestHooks.Reset();
            NativeArena arena = new(returnMemoryOnDispose: policy);
            BusyGenerationSet busy = StartBusyArenaGenerations(arena, 1);

            try
            {
                WaitForEntry(busy);
                NativeMemoryTestHooks.FailAfterCommitBoundary(1);
                busy.Allows[0].Set();
                NativeAllocationQuarantinedException failure =
                    Assert.IsType<NativeAllocationQuarantinedException>(await busy.Workers[0]);
                Assert.Equal("clear", failure.Boundary);
                Assert.Equal(1, arena.QuarantinedGenerationCountForTest);
                long freeBeforeReturn = NativeMemoryTestHooks.Snapshot().FreeCount;

                if (policy == NativeMemoryReturn.ToNativeMemory)
                {
                    arena.ReturnMemoryToNativeMemory();
                    Assert.Equal(0, arena.QuarantinedGenerationCountForTest);
                    Assert.True(NativeMemoryTestHooks.Snapshot().FreeCount > freeBeforeReturn);
                }
                else
                {
                    arena.ReturnMemoryToGarbageCollector();
                    Assert.Equal(freeBeforeReturn, NativeMemoryTestHooks.Snapshot().FreeCount);
                    arena.LeaseFromMemory();
                    ArenaLease<int> fresh = arena.Scratch<int>(1);
                    Assert.Equal([2L], arena.CurrentSegmentOrdinalsForTest);
                    Assert.Equal(0, fresh[0]);
                    arena.Dispose();
                    Assert.Equal(freeBeforeReturn, NativeMemoryTestHooks.Snapshot().FreeCount);
                    ForceFinalizationUntilDetachedStorageIsGone();
                }
            }
            finally
            {
                await FinishBusyAsync(busy);
                NativeMemoryTestHooks.Reset();
                arena.Dispose();
            }
        }
    }

    private static BusyGenerationSet StartBusyPoolGenerations(NativePool<int> pool, int count)
    {
        ManualResetEventSlim[] allows = new ManualResetEventSlim[count];
        ManualResetEventSlim[] entered = new ManualResetEventSlim[count];
        Task<Exception?>[] workers = new Task<Exception?>[count];
        for (int index = 0; index < count; index++)
        {
            allows[index] = new();
            entered[index] = new();
            int generationIndex = index;
            workers[index] = Task.Run(() => HoldBusyPoolLease(
                pool,
                generationIndex + 1,
                generationIndex + 1,
                allows[generationIndex],
                entered[generationIndex]));
            Assert.True(entered[index].Wait(TimeSpan.FromSeconds(10)));
            pool.ReleaseLeasesToGarbageCollector();
        }

        return new BusyGenerationSet(allows, entered, workers);
    }

    private static BusyGenerationSet StartBusyArenaGenerations(NativeArena arena, int count)
    {
        ManualResetEventSlim[] allows = new ManualResetEventSlim[count];
        ManualResetEventSlim[] entered = new ManualResetEventSlim[count];
        Task<Exception?>[] workers = new Task<Exception?>[count];
        for (int index = 0; index < count; index++)
        {
            allows[index] = new();
            entered[index] = new();
            int generationIndex = index;
            workers[index] = Task.Run(() => HoldBusyArenaLease(
                arena,
                generationIndex + 1,
                generationIndex + 1,
                allows[generationIndex],
                entered[generationIndex]));
            Assert.True(entered[index].Wait(TimeSpan.FromSeconds(10)));
            arena.ReleaseLeasesToGarbageCollector();
        }

        return new BusyGenerationSet(allows, entered, workers);
    }

    private static void WaitForEntry(BusyGenerationSet busy)
    {
        foreach (ManualResetEventSlim entry in busy.Entered)
        {
            Assert.True(entry.Wait(TimeSpan.FromSeconds(10)));
        }
    }

    private static async Task ReleaseBusyAsync(BusyGenerationSet busy, int[] order)
    {
        foreach (int index in order)
        {
            busy.Allows[index].Set();
            Assert.Null(await busy.Workers[index]);
        }
    }

    private static async Task FinishBusyAsync(BusyGenerationSet busy)
    {
        for (int index = 0; index < busy.Allows.Length; index++)
        {
            busy.Allows[index]?.Set();
        }

        foreach (Task<Exception?> worker in busy.Workers)
        {
            if (worker is not null)
            {
                try
                {
                    _ = await worker;
                }
                catch
                {
                }
            }
        }
    }

    private static Exception? HoldBusyPoolLease(
        NativePool<int> pool,
        int length,
        int value,
        ManualResetEventSlim allow,
        ManualResetEventSlim entry)
    {
        try
        {
            Pooled<int> lease = pool.Rent(length);
            lease[0] = value;
            lease.Access(_ =>
            {
                entry.Set();
                allow.Wait(TimeSpan.FromSeconds(10));
            });
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Exception? HoldBusyArenaLease(
        NativeArena arena,
        int length,
        int value,
        ManualResetEventSlim allow,
        ManualResetEventSlim entry)
    {
        try
        {
            ArenaLease<int> lease = arena.Scratch<int>(length);
            lease[0] = value;
            lease.Access(_ =>
            {
                entry.Set();
                allow.Wait(TimeSpan.FromSeconds(10));
            });
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void ForceFinalizationUntilDetachedStorageIsGone()
    {
        for (int attempt = 0;
            attempt < 20 && NativeMemoryTestHooks.Snapshot().DetachedNativeBytes != 0;
            attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }

    private sealed class BusyGenerationSet(
        ManualResetEventSlim[] allows,
        ManualResetEventSlim[] entered,
        Task<Exception?>[] workers)
    {
        internal ManualResetEventSlim[] Allows { get; } = allows;
        internal ManualResetEventSlim[] Entered { get; } = entered;
        internal Task<Exception?>[] Workers { get; } = workers;
    }
}
