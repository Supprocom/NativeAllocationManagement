using System.Reflection;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class MechanismRegressionTests
{
    [Fact]
    public void ReferenceContainingLeasesUseNativeHandleSlotsAndOneOwnerRootTable()
    {
        Assert.Null(typeof(NativeAllocation).GetProperty("ManagedValues", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(typeof(NativeSlab).GetProperty("ManagedValues", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.NotNull(typeof(NativeAllocation).GetProperty("ReferenceRoots", BindingFlags.Instance | BindingFlags.NonPublic));

        NativeMemoryTestHooks.Reset();
        NativePool<string> pool = new(initialCapacity: 2, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        try
        {
            Pooled<string> first = pool.Rent(2);
            first[0] = "first";
            first[1] = "second";
            Assert.Equal("first", first[0]);
            Assert.Equal("second", first[1]);
            Assert.Equal(2, pool.CurrentReferenceRootCountForTest);
            first.Dispose();
            Assert.Equal(0, pool.CurrentReferenceRootCountForTest);
            Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);

            Pooled<string> reused = pool.Rent(2);
            Assert.Null(reused[0]);
            Assert.Null(reused[1]);
            reused.Dispose();
        }
        finally
        {
            pool.Dispose();
        }
    }

    [Fact]
    public void ReferenceContainingStructsWorkForPoolRegionAndArenaGenerations()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<ReferenceCell> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        try
        {
            Pooled<ReferenceCell> pooled = pool.Rent(1);
            pooled[0] = new ReferenceCell("pool", 7);
            Assert.Equal(new ReferenceCell("pool", 7), pooled[0]);
            pooled.Dispose();

            ArenaLease<ReferenceCell> arenaLease = arena.Scratch<ReferenceCell>(1);
            arenaLease[0] = new ReferenceCell("arena", 9);
            Assert.Equal(new ReferenceCell("arena", 9), arenaLease[0]);
            arena.ReleaseLeasesToNativeMemory();
            Assert.Equal(0, arena.CurrentReferenceRootCountForTest);
            ArenaLease<ReferenceCell> freshArenaLease = arena.Scratch<ReferenceCell>(1);
            Assert.Equal(default, freshArenaLease[0]);

            using (NativeRegion region = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory))
            {
                Local<ReferenceCell> local = region.Lease<ReferenceCell>(1);
                local[0] = new ReferenceCell("region", 11);
                Assert.Equal(new ReferenceCell("region", 11), local[0]);
                Assert.Equal(1, region.CurrentReferenceRootCountForTest);
            }
        }
        finally
        {
            arena.Dispose();
            pool.Dispose();
        }
    }

    [Fact]
    public void ScopedReferenceRootsAreClearedBeforePoolAndArenaReuse()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<string> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        try
        {
            scoped Pooled<string> pooled = pool.LeaseScoped(1);
            pooled[0] = "pool scoped";
            pool.RecycleScoped();
            Assert.Equal(0, pool.CurrentReferenceRootCountForTest);
            Pooled<string> pooledReuse = pool.Rent(1);
            Assert.Null(pooledReuse[0]);
            pooledReuse.Dispose();

            scoped ArenaLease<string> scratch = arena.ScratchScoped<string>(1);
            scratch[0] = "arena scoped";
            arena.RecycleScoped();
            Assert.Equal(0, arena.CurrentReferenceRootCountForTest);
            ArenaLease<string> scratchReuse = arena.Scratch<string>(1);
            Assert.Null(scratchReuse[0]);
        }
        finally
        {
            arena.Dispose();
            pool.Dispose();
        }
    }

    [Fact]
    public void IndividualPoolReturnsRetireRecordsForLongAndZeroLengthCycles()
    {
        NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        try
        {
            for (int index = 0; index < 2_000; index++)
            {
                Pooled<int> lease = pool.Rent(index % 2 == 0 ? 0 : 1);
                lease.Dispose();
            }

            Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
            pool.ReleaseLeasesToNativeMemory();
            Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        }
        finally
        {
            pool.Dispose();
        }
    }

    [Fact]
    public void RolloverCommitBoundaryFailuresPreserveTheActiveGenerationAndNativeValues()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<string> pool = new(initialCapacity: 1, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        try
        {
            Pooled<string> first = pool.Rent(1);
            first[0] = "first";
            Pooled<string> second = pool.Rent(2);
            second[0] = "second-0";
            second[1] = "second-1";

            NativeMemoryTestHooks.FailAtCommitBoundary(1);
            Assert.Throws<InvalidOperationException>(() => pool.ReleaseLeasesToNativeMemory());
            Assert.Equal(NativeOwnerLifecycle.Active, pool.CurrentLifecycle);
            Assert.Equal("first", first[0]);
            Assert.Equal("second-0", second[0]);
            Assert.Equal("second-1", second[1]);
            Assert.Equal(3, pool.CurrentReferenceRootCountForTest);

            NativeMemoryTestHooks.FailAtCommitBoundary(3);
            Assert.Throws<InvalidOperationException>(() => pool.ReleaseLeasesToNativeMemory());
            Assert.Equal(NativeOwnerLifecycle.Active, pool.CurrentLifecycle);
            Assert.Equal("first", first[0]);
            Assert.Equal("second-0", second[0]);
            Assert.Equal("second-1", second[1]);
            Assert.Equal(3, pool.CurrentReferenceRootCountForTest);

            pool.ReleaseLeasesToNativeMemory();
            Assert.Equal(NativeOwnerLifecycle.Active, pool.CurrentLifecycle);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
            pool.Dispose();
        }
    }

    [Fact]
    public void ReturnAndScopedRecycleCommitBoundaryFailuresLeaveUsableState()
    {
        NativePool<int> returnPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativePool<int> scopedPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        try
        {
            Pooled<int> returned = returnPool.Rent(1);
            returned[0] = 41;
            NativeMemoryTestHooks.FailAtCommitBoundary(1);
            Assert.Throws<InvalidOperationException>(() => returnPool.ReturnMemoryToNativeMemory());
            Assert.Equal(NativeOwnerLifecycle.Active, returnPool.CurrentLifecycle);
            Assert.Equal(41, returned[0]);
            returnPool.ReturnMemoryToNativeMemory();

            scoped Pooled<int> scoped = scopedPool.LeaseScoped(1);
            scoped[0] = 73;
            NativeMemoryTestHooks.FailAtCommitBoundary(1);
            Assert.Throws<InvalidOperationException>(() => scopedPool.RecycleScoped());
            Assert.Equal(NativeOwnerLifecycle.Active, scopedPool.CurrentLifecycle);
            Assert.Equal(73, scoped[0]);
            scopedPool.RecycleScoped();
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
            returnPool.Dispose();
            scopedPool.Dispose();
        }
    }

    [Fact]
    public async Task TolerantLeaseReleaseDoesNotClearAReusedSlabUnderAnEnteredOperation()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(initialCapacity: 1, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ManualResetEventSlim entered = new();
        ManualResetEventSlim allowCallback = new();
        NativeMemoryTestHooks.SetOperationEntered(operation =>
        {
            if (operation == nameof(Pooled<int>.Access))
            {
                entered.Set();
            }
        });

        try
        {
            Task<int> worker = Task.Run(() =>
            {
                Pooled<int> lease = pool.Rent(1);
                lease[0] = 42;
                int observed = 0;
                lease.Access(view =>
                {
                    allowCallback.Wait(TimeSpan.FromSeconds(10));
                    observed = view[0];
                });
                return observed;
            });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            pool.ReleaseLeasesToGarbageCollector();
            allowCallback.Set();
            Assert.Equal(42, await worker);
            pool.Dispose();
        }
        finally
        {
            allowCallback.Set();
            NativeMemoryTestHooks.Reset();
            pool.Dispose();
        }
    }

    [Fact]
    public async Task TolerantMemoryReturnKeepsEnteredDataUntilTheOperationExits()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(initialCapacity: 1, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ManualResetEventSlim entered = new();
        ManualResetEventSlim allowCallback = new();
        NativeMemoryTestHooks.SetOperationEntered(operation =>
        {
            if (operation == nameof(Pooled<int>.Access))
            {
                entered.Set();
            }
        });

        try
        {
            Task<int> worker = Task.Run(() =>
            {
                Pooled<int> lease = pool.Rent(1);
                lease[0] = 73;
                int observed = 0;
                lease.Access(view =>
                {
                    allowCallback.Wait(TimeSpan.FromSeconds(10));
                    observed = view[0];
                });
                return observed;
            });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            pool.ReturnMemoryToGarbageCollector();
            Assert.Equal(NativeOwnerLifecycle.Returned, pool.CurrentLifecycle);
            allowCallback.Set();
            Assert.Equal(73, await worker);
            pool.Dispose();
        }
        finally
        {
            allowCallback.Set();
            NativeMemoryTestHooks.Reset();
            pool.Dispose();
        }
    }

    [Fact]
    public async Task TolerantMemoryReturnDefersDetachedSegmentFreeUntilTheGenerationOwnerFinalizes()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(initialCapacity: 1, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ManualResetEventSlim entered = new();
        ManualResetEventSlim allowCallback = new();
        NativeGenerationOwner? retainedOwner = null;
        NativeMemoryTestHooks.SetOperationEnteredWithGenerationOwner((operation, owner) =>
        {
            if (operation == nameof(Pooled<int>.Access))
            {
                retainedOwner = owner;
                entered.Set();
            }
        });

        try
        {
            Task worker = Task.Run(() =>
            {
                Pooled<int> lease = pool.Rent(1);
                lease.Access(_ => allowCallback.Wait(TimeSpan.FromSeconds(10)));
            });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            pool.ReturnMemoryToGarbageCollector();
            long freeAfterReturn = NativeMemoryTestHooks.Snapshot().FreeCount;
            allowCallback.Set();
            await worker;
            Assert.Equal(freeAfterReturn, NativeMemoryTestHooks.Snapshot().FreeCount);

            WeakReference weakOwner = ReleaseStrongOwner(ref retainedOwner);
            NativeMemoryTestHooks.SetOperationEnteredWithGenerationOwner(null);
            ForceFinalization(weakOwner);
            Assert.False(weakOwner.IsAlive);
            Assert.True(NativeMemoryTestHooks.Snapshot().FreeCount > freeAfterReturn);
            pool.Dispose();
        }
        finally
        {
            allowCallback.Set();
            retainedOwner = null;
            NativeMemoryTestHooks.Reset();
            pool.Dispose();
        }
    }

    [Fact]
    public async Task RetiredArenaRejoinRestoresPhysicalOrderAndTheScopedTraversalFrontier()
    {
        NativeMemoryTestHooks.Reset();
        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ManualResetEventSlim entered = new();
        ManualResetEventSlim allowCallback = new();
        NativeMemoryTestHooks.SetOperationEntered(operation =>
        {
            if (operation == nameof(ArenaLease<byte>.Access))
            {
                entered.Set();
            }
        });

        try
        {
            Task worker = Task.Run(() =>
            {
                ArenaLease<byte> first = arena.Scratch<byte>(4_096);
                first.Access(_ => allowCallback.Wait(TimeSpan.FromSeconds(10)));
            });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            _ = arena.Scratch<byte>(4_096);
            arena.ReleaseLeasesToGarbageCollector();
            _ = arena.Scratch<byte>(8_192);
            allowCallback.Set();
            await worker;

            Assert.Equal([1L, 2L], arena.CurrentSegmentOrdinalsForTest);
            ArenaLease<byte> scoped = arena.ScratchScoped<byte>(1);
            Assert.Equal(2, arena.CurrentBumpTraversalForTest.SegmentCount);
            arena.RecycleScoped();
        }
        finally
        {
            allowCallback.Set();
            NativeMemoryTestHooks.Reset();
            arena.Dispose();
        }
    }

    [Fact]
    public async Task RetiredPoolRejoinPreservesReversePhysicalTrimOrder()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(initialCapacity: 1, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ManualResetEventSlim entered = new();
        ManualResetEventSlim allowCallback = new();
        NativeMemoryTestHooks.SetOperationEntered(operation =>
        {
            if (operation == nameof(Pooled<int>.Access))
            {
                entered.Set();
            }
        });

        try
        {
            Task worker = Task.Run(() =>
            {
                Pooled<int> first = pool.Rent(1);
                first.Access(_ => allowCallback.Wait(TimeSpan.FromSeconds(10)));
            });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            _ = pool.Rent(2);
            pool.ReleaseLeasesToGarbageCollector();
            allowCallback.Set();
            await worker;

            Assert.Equal([1L, 2L], pool.CurrentSegmentOrdinalsForTest);
            Assert.Equal((nuint)(2 * sizeof(int)), pool.TrimRetainedMemoryByBytes(1));
            Assert.Equal([1L], pool.CurrentSegmentOrdinalsForTest);
            Assert.Equal((nuint)sizeof(int), pool.TrimRetainedMemory());
        }
        finally
        {
            allowCallback.Set();
            NativeMemoryTestHooks.Reset();
            pool.Dispose();
        }
    }

    [Fact]
    public async Task RetiredDrainClearFailureQuarantinesStorageInsteadOfReusingIt()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(initialCapacity: 1, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ManualResetEventSlim entered = new();
        ManualResetEventSlim allowCallback = new();
        NativeMemoryTestHooks.SetOperationEntered(operation =>
        {
            if (operation == nameof(Pooled<int>.Access))
            {
                entered.Set();
            }
        });

        try
        {
            Task<Exception?> worker = Task.Run(() =>
            {
                try
                {
                    Pooled<int> lease = pool.Rent(1);
                    lease.Access(_ => allowCallback.Wait(TimeSpan.FromSeconds(10)));
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            pool.ReleaseLeasesToGarbageCollector();
            NativeMemoryTestHooks.FailAfterCommitBoundary(1);
            allowCallback.Set();
            NativeAllocationQuarantinedException failure = Assert.IsType<NativeAllocationQuarantinedException>(await worker);
            Assert.Equal("clear", failure.Boundary);
            Assert.Equal(1, failure.SegmentOrdinal);
            Assert.Equal(1, pool.QuarantinedSegmentCountForTest);
            Assert.Empty(pool.CurrentSegmentOrdinalsForTest);
            long freeBeforeFreshRent = NativeMemoryTestHooks.Snapshot().FreeCount;

            Pooled<int> fresh = pool.Rent(1);
            Assert.Equal([2L], pool.CurrentSegmentOrdinalsForTest);
            fresh[0] = 9;
            fresh.Dispose();
            Assert.Equal(freeBeforeFreshRent, NativeMemoryTestHooks.Snapshot().FreeCount);
        }
        finally
        {
            allowCallback.Set();
            NativeMemoryTestHooks.Reset();
            pool.Dispose();
        }
    }

    [Fact]
    public async Task RetiredDrainTransferFailureQuarantinesTheTransferredSegment()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(initialCapacity: 1, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ManualResetEventSlim entered = new();
        ManualResetEventSlim allowCallback = new();
        NativeMemoryTestHooks.SetOperationEntered(operation =>
        {
            if (operation == nameof(Pooled<int>.Access))
            {
                entered.Set();
            }
        });

        try
        {
            Task<Exception?> worker = Task.Run(() =>
            {
                try
                {
                    Pooled<int> lease = pool.Rent(1);
                    lease.Access(_ => allowCallback.Wait(TimeSpan.FromSeconds(10)));
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            pool.ReleaseLeasesToGarbageCollector();
            NativeMemoryTestHooks.FailAfterCommitBoundary(2);
            allowCallback.Set();
            NativeAllocationQuarantinedException failure = Assert.IsType<NativeAllocationQuarantinedException>(await worker);
            Assert.Equal("slab transfer", failure.Boundary);
            Assert.Equal(1, failure.SegmentOrdinal);
            Assert.Equal(1, pool.QuarantinedSegmentCountForTest);
            Assert.Empty(pool.CurrentSegmentOrdinalsForTest);

            Pooled<int> fresh = pool.Rent(1);
            Assert.Equal([2L], pool.CurrentSegmentOrdinalsForTest);
            fresh.Dispose();
        }
        finally
        {
            allowCallback.Set();
            NativeMemoryTestHooks.Reset();
            pool.Dispose();
        }
    }

    [Fact]
    public void TrimUsesLifecycleNoOpsExactPhysicalUnitsAndAllocationOrder()
    {
        NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory, initialCapacity: 4);
        Pooled<int> initial = pool.Rent(4);
        Pooled<int> growth = pool.Rent(8);
        growth.Dispose();
        initial.Dispose();
        nuint firstRelease = pool.TrimRetainedMemoryByBytes(1);
        Assert.Equal((nuint)(8 * sizeof(int)), firstRelease);
        nuint secondRelease = pool.TrimRetainedMemory();
        Assert.Equal((nuint)(4 * sizeof(int)), secondRelease);
        pool.Dispose();

        NativePool<string> referencePool = new(initialCapacity: 2, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<string> referenceLease = referencePool.Rent(2);
        referenceLease[0] = "root";
        referenceLease.Dispose();
        Assert.Equal((nuint)(2 * IntPtr.Size), referencePool.TrimRetainedMemoryByLeaseSize(1));
        referencePool.Dispose();
    }

    [Fact]
    public void TrimLeaseSizingIncludesAlignmentAndGeometricArenaGrowth()
    {
        NativeMemoryTestHooks.Reset();
        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        try
        {
            ArenaLease<byte> first = arena.Scratch<byte>(4_096);
            ArenaLease<byte> second = arena.Scratch<byte>(4_096);
            Assert.Equal(2, arena.CurrentBumpTraversalForTest.SegmentCount);
            Assert.True(NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes >= 12_288);
            arena.ReleaseLeasesToNativeMemory();
            Assert.Equal(0, arena.CurrentBumpTraversalForTest.Ordinary);
            Assert.Equal(1, arena.CurrentBumpTraversalForTest.Scoped);
            nuint released = arena.TrimRetainedMemoryByLeaseSize<long>(1);
            Assert.True(released >= 12_288);
            Assert.Equal(0, NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes);
        }
        finally
        {
            arena.Dispose();
        }
    }

    [Fact]
    public void ArenaTraversalAdvancesFromTheCurrentSegmentInsteadOfRescanningTheBank()
    {
        NativeMemoryTestHooks.Reset();
        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        try
        {
            for (int index = 0; index < 128; index++)
            {
                _ = arena.Scratch<byte>(4_096);
            }

            NativeMemoryTestMetrics metrics = NativeMemoryTestHooks.Snapshot();
            Assert.True(arena.CurrentBumpTraversalForTest.SegmentCount > 1);
            Assert.True(metrics.BumpTraversalVisitCount <= 128, metrics.ToString());

            arena.ReleaseLeasesToNativeMemory();
            Assert.Equal(0, arena.CurrentBumpTraversalForTest.Ordinary);
            Assert.True(arena.CurrentBumpTraversalForTest.SegmentCount > 1);
        }
        finally
        {
            arena.Dispose();
        }
    }

    [Fact]
    public void TrimOnUnleasedAndReturnedOwnersIsAZeroNoOpButDisposedOwnersThrow()
    {
        NativePool<int> unleasedPool = new(doNotLeaseOnDeclaration: true);
        Assert.Equal((nuint)0, unleasedPool.TrimRetainedMemory());
        Assert.Equal((nuint)0, unleasedPool.TrimRetainedMemoryByBytes(1));
        Assert.Equal((nuint)0, unleasedPool.TrimRetainedMemoryByLeaseSize(1));
        unleasedPool.Dispose();
        Assert.Throws<NativeAllocationDisposedException>(() => unleasedPool.TrimRetainedMemory());

        NativePool<int> returnedPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        returnedPool.ReturnMemoryToNativeMemory();
        Assert.Equal((nuint)0, returnedPool.TrimRetainedMemory());
        Assert.Equal((nuint)0, returnedPool.TrimRetainedMemoryByBytes(1));
        Assert.Equal((nuint)0, returnedPool.TrimRetainedMemoryByLeaseSize(1));
        returnedPool.Dispose();
        Assert.Throws<NativeAllocationDisposedException>(() => returnedPool.TrimRetainedMemory());

        NativeRegion unleasedRegion = new(doNotLeaseOnDeclaration: true);
        Assert.Equal((nuint)0, unleasedRegion.TrimRetainedMemory());
        Assert.Equal((nuint)0, unleasedRegion.TrimRetainedMemoryByBytes(1));
        Assert.Equal((nuint)0, unleasedRegion.TrimRetainedMemoryByLeaseSize<int>(1));
        unleasedRegion.Dispose();

        NativeRegion returnedRegion = new();
        returnedRegion.ReturnMemoryToNativeMemory();
        Assert.Equal((nuint)0, returnedRegion.TrimRetainedMemory());
        Assert.Equal((nuint)0, returnedRegion.TrimRetainedMemoryByBytes(1));
        Assert.Equal((nuint)0, returnedRegion.TrimRetainedMemoryByLeaseSize<int>(1));
        returnedRegion.Dispose();

        NativeRegion region = new();
        region.Dispose();
        try
        {
            _ = region.TrimRetainedMemory();
            Assert.Fail("A disposed region trim must throw.");
        }
        catch (NativeAllocationDisposedException)
        {
        }

        NativeArena arena = new();
        arena.ReturnMemoryToNativeMemory();
        Assert.Equal((nuint)0, arena.TrimRetainedMemory());
        Assert.Equal((nuint)0, arena.TrimRetainedMemoryByBytes(1));
        Assert.Equal((nuint)0, arena.TrimRetainedMemoryByLeaseSize<int>(1));
        arena.Dispose();
        Assert.Throws<NativeAllocationDisposedException>(() => arena.TrimRetainedMemory());
    }

    private static void ForceFinalization(WeakReference owner)
    {
        for (int attempt = 0; attempt < 20 && owner.IsAlive; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }

    private static WeakReference ReleaseStrongOwner(ref NativeGenerationOwner? owner)
    {
        WeakReference weakOwner = new(owner!);
        owner = null;
        return weakOwner;
    }

    private readonly record struct ReferenceCell(string? Text, int Number);
}
