using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class ArenaLifecycleTests
{
    [Fact]
    public void ArenaSharesOneHeterogeneousGenerationAndInvalidatesEveryLeaseOnRelease()
    {
        NativeMemoryTestHooks.Reset();
        NativeArena arena = new(preAllocateBytes: 128, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> numbers = arena.Scratch<int>(4);
        ArenaLease<string> names = arena.Scratch<string>(2);
        numbers[0] = 17;
        names[0] = "temporary";

        arena.ReleaseLeasesToNativeMemory();

        Exception? staleNumbers = null;
        try { _ = numbers[0]; } catch (Exception exception) { staleNumbers = exception; }
        Exception? staleNames = null;
        try { _ = names[0]; } catch (Exception exception) { staleNames = exception; }
        Assert.IsType<NativeAllocationReturnedException>(staleNumbers);
        Assert.IsType<NativeAllocationReturnedException>(staleNames);

        ArenaLease<int> freshNumbers = arena.Scratch<int>(4);
        ArenaLease<string> freshNames = arena.Scratch<string>(2);
        Assert.Equal(0, freshNumbers[0]);
        Assert.Null(freshNames[0]);
        freshNumbers[0] = 23;
        freshNames[0] = "fresh";
        Assert.Equal(23, freshNumbers[0]);
        Assert.Equal("fresh", freshNames[0]);
        arena.Dispose();
    }

    [Fact]
    public void DelayedActivationIsAllocationFreeAndFailureAtomicForAllOwners()
    {
        NativeMemoryTestHooks.Reset();
        NativeMemoryTestMetrics before = NativeMemoryTestHooks.Snapshot();
        NativePool<string> pool = new(initialCapacity: 4, doNotLeaseOnDeclaration: true);
        NativeArena arena = new(preAllocateBytes: 64, doNotLeaseOnDeclaration: true);
        NativeRegion region = new(preAllocateBytes: 64, doNotLeaseOnDeclaration: true);
        NativeMemoryTestMetrics afterConstruction = NativeMemoryTestHooks.Snapshot();

        Assert.Equal(before.AllocationCount, afterConstruction.AllocationCount);
        Assert.Equal(NativeOwnerLifecycle.Unleased, pool.CurrentLifecycle);
        Assert.Equal(NativeOwnerLifecycle.Unleased, arena.CurrentLifecycle);
        Assert.Equal(NativeOwnerLifecycle.Unleased, region.CurrentLifecycle);

        Assert.IsType<NativeAllocationStateException>(Record(() => pool.Rent(1)));
        Assert.IsType<NativeAllocationStateException>(Record(() => arena.Scratch<int>(1)));
        Assert.IsType<NativeAllocationStateException>(RecordRegionLease(ref region));
        Assert.IsType<NativeAllocationStateException>(Record(pool.ReturnMemoryToNativeMemory));
        Assert.IsType<NativeAllocationStateException>(Record(arena.ReleaseLeasesToNativeMemory));
        Assert.IsType<NativeAllocationStateException>(RecordRegionReturn(ref region));

        NativeMemoryTestHooks.FailNextAllocation();
        NativeAllocationFailedException failure = Assert.Throws<NativeAllocationFailedException>(pool.LeaseFromMemory);
        Assert.Equal(NativeOwnerLifecycle.Unleased, failure.CurrentLifecycle);
        Assert.Equal(NativeOwnerLifecycle.Unleased, pool.CurrentLifecycle);

        pool.LeaseFromMemory();
        arena.LeaseFromMemory();
        region.LeaseFromMemory();
        Assert.Equal(NativeOwnerLifecycle.Active, pool.CurrentLifecycle);
        Assert.Equal(NativeOwnerLifecycle.Active, arena.CurrentLifecycle);
        Assert.Equal(NativeOwnerLifecycle.Active, region.CurrentLifecycle);

        pool.Dispose();
        arena.Dispose();
        region.Dispose();
    }

    [Fact]
    public void FailedScopedGrowthDoesNotPublishAStorageUnitOrPendingBaseline()
    {
        NativeMemoryTestHooks.Reset();
        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeMemoryTestHooks.FailNextAllocation();

        Assert.Throws<NativeAllocationFailedException>(() => arena.ScratchScoped<int>(1));
        Assert.Equal(0, NativeMemoryTestHooks.Snapshot().AllocationCount);

        ArenaLease<int> ordinary = arena.Scratch<int>(1);
        long afterOrdinary = NativeMemoryTestHooks.Snapshot().AllocationCount;
        scoped ArenaLease<int> scopedLease = arena.ScratchScoped<int>(1);
        Assert.Equal(afterOrdinary, NativeMemoryTestHooks.Snapshot().AllocationCount);
        scopedLease[0] = 7;
        arena.RecycleScoped();
        Assert.Equal(0, ordinary[0]);
        arena.Dispose();
    }

    [Fact]
    public void DelayedArenaActivationRejectsEveryPreActivationOperationForBothPolicies()
    {
        foreach (NativeMemoryReturn policy in Enum.GetValues<NativeMemoryReturn>())
        {
            foreach (nuint reservation in new nuint[] { 0, 128 })
            {
                NativeMemoryTestHooks.Reset();
                NativeArena arena = new(reservation, policy, doNotLeaseOnDeclaration: true);
                NativeMemoryTestMetrics before = NativeMemoryTestHooks.Snapshot();

                Assert.Throws<NativeAllocationStateException>(() => arena.Scratch<int>(1));
                Assert.Throws<NativeAllocationStateException>(() => arena.ScratchScoped<int>(1));
                Assert.Throws<NativeAllocationStateException>(arena.ReturnMemoryToNativeMemory);
                Assert.Throws<NativeAllocationStateException>(arena.ReturnMemoryToGarbageCollector);
                Assert.Throws<NativeAllocationStateException>(arena.ReleaseLeasesToNativeMemory);
                Assert.Throws<NativeAllocationStateException>(arena.ReleaseLeasesToGarbageCollector);
                Assert.Throws<NativeAllocationStateException>(arena.RecycleScoped);
                Assert.Throws<NativeAllocationStateException>(() => { _ = arena.TrimRetainedMemory(); });
                Assert.Throws<NativeAllocationStateException>(() => arena.TrimRetainedMemoryByBytes(1));
                Assert.Throws<NativeAllocationStateException>(() => arena.TrimRetainedMemoryByLeaseSize<int>(1));
                Assert.Equal(before.AllocationCount, NativeMemoryTestHooks.Snapshot().AllocationCount);

                arena.LeaseFromMemory();
                ArenaLease<int> value = arena.Scratch<int>(1);
                Assert.Equal(0, value[0]);
                arena.Dispose();
            }
        }
    }

    [Fact]
    public void ScopedRecycleClearsReferenceRootsAndKeepsArenaStorageReusable()
    {
        NativeMemoryTestHooks.Reset();
        WeakReference weak = CreateRecycledReference();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Assert.False(weak.IsAlive);
        NativePool<object> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<object> reused = pool.LeaseScoped(1);
        Assert.Null(reused[0]);
        pool.RecycleScoped();
        pool.Dispose();

        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> ordinary = arena.Scratch<int>(1);
        ordinary[0] = 9;
        ArenaLease<int> scoped = arena.ScratchScoped<int>(4);
        scoped[0] = 42;
        arena.RecycleScoped();
        Assert.Equal(9, ordinary[0]);
        ArenaLease<int> recycled = arena.Scratch<int>(4);
        Assert.Equal(0, recycled[0]);
        arena.Dispose();
    }

    [Fact]
    public void MemoryReturnClearsReferenceRootsForEveryOwnerAndPolicy()
    {
        foreach (NativeMemoryReturn policy in Enum.GetValues<NativeMemoryReturn>())
        {
            WeakReference poolRoot = ReturnPoolReference(policy);
            WeakReference regionRoot = ReturnRegionReference(policy);
            WeakReference arenaRoot = ReturnArenaReference(policy);

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();

            Assert.False(poolRoot.IsAlive);
            Assert.False(regionRoot.IsAlive);
            Assert.False(arenaRoot.IsAlive);
        }
    }

    private static WeakReference CreateRecycledReference()
    {
        NativePool<object> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<object> pooled = pool.LeaseScoped(1);
        object held = new();
        WeakReference weak = new(held);
        pooled[0] = held;
        pool.RecycleScoped();
        held = null!;
        pool.Dispose();
        return weak;
    }

    private static WeakReference ReturnPoolReference(NativeMemoryReturn policy)
    {
        NativePool<object> pool = new(returnMemoryOnDispose: policy);
        Pooled<object> lease = pool.Rent(1);
        object held = new();
        WeakReference weak = new(held);
        lease[0] = held;
        if (policy == NativeMemoryReturn.ToNativeMemory)
        {
            pool.ReturnMemoryToNativeMemory();
        }
        else
        {
            pool.ReturnMemoryToGarbageCollector();
        }

        pool.Dispose();
        held = null!;
        return weak;
    }

    private static WeakReference ReturnRegionReference(NativeMemoryReturn policy)
    {
        NativeRegion region = new(returnMemoryOnDispose: policy);
        Local<object> lease = region.Lease<object>(1);
        object held = new();
        WeakReference weak = new(held);
        lease[0] = held;
        if (policy == NativeMemoryReturn.ToNativeMemory)
        {
            region.ReturnMemoryToNativeMemory();
        }
        else
        {
            region.ReturnMemoryToGarbageCollector();
        }

        region.Dispose();
        held = null!;
        return weak;
    }

    private static WeakReference ReturnArenaReference(NativeMemoryReturn policy)
    {
        NativeArena arena = new(returnMemoryOnDispose: policy);
        ArenaLease<object> lease = arena.Scratch<object>(1);
        object held = new();
        WeakReference weak = new(held);
        lease[0] = held;
        if (policy == NativeMemoryReturn.ToNativeMemory)
        {
            arena.ReturnMemoryToNativeMemory();
        }
        else
        {
            arena.ReturnMemoryToGarbageCollector();
        }

        arena.Dispose();
        held = null!;
        return weak;
    }

    [Fact]
    public void TrimReleasesOnlyIdleRetainedArenaSegmentsAndGrowsAgainOnDemand()
    {
        NativeMemoryTestHooks.Reset();
        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<byte> large = arena.Scratch<byte>(16_384);
        large[0] = 5;
        arena.ReleaseLeasesToNativeMemory();
        long beforeTrim = NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes;
        nuint released = arena.TrimRetainedMemoryByBytes(1);
        Assert.True(released > 0);
        Assert.True(NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes < beforeTrim);

        ArenaLease<byte> grown = arena.Scratch<byte>(16_384);
        Assert.Equal(0, grown[0]);
        arena.Dispose();
    }

    [Fact]
    public void EveryOwnerTrimFormFreesWholeIdleUnitsAndAllowsOnDemandGrowth()
    {
        Func<NativePool<int>, nuint>[] poolTrims =
        [
            static pool => pool.TrimRetainedMemory(),
            static pool => pool.TrimRetainedMemoryByBytes(1),
            static pool => pool.TrimRetainedMemoryByLeaseSize(1)
        ];

        foreach (Func<NativePool<int>, nuint> trim in poolTrims)
        {
            NativePool<int> pool = new(initialCapacity: 8, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
            nuint released = trim(pool);
            Assert.True(released >= (nuint)(8 * sizeof(int)));
            Pooled<int> lease = pool.Rent(1);
            Assert.Equal(0, lease[0]);
            lease.Dispose();
            pool.Dispose();
        }

        for (int trimKind = 0; trimKind < 3; trimKind++)
        {
            NativeRegion region = new(4096, NativeMemoryReturn.ToNativeMemory);
            nuint released = trimKind switch
            {
                0 => region.TrimRetainedMemory(),
                1 => region.TrimRetainedMemoryByBytes(1),
                _ => region.TrimRetainedMemoryByLeaseSize<int>(1)
            };
            Assert.True(released >= 4096);
            Local<int> lease = region.Lease<int>(1);
            Assert.Equal(0, lease[0]);
            region.Dispose();
        }

        Func<NativeArena, nuint>[] arenaTrims =
        [
            static arena => arena.TrimRetainedMemory(),
            static arena => arena.TrimRetainedMemoryByBytes(1),
            static arena => arena.TrimRetainedMemoryByLeaseSize<int>(1)
        ];

        foreach (Func<NativeArena, nuint> trim in arenaTrims)
        {
            NativeArena arena = new(4096, NativeMemoryReturn.ToNativeMemory);
            ArenaLease<int> lease = arena.Scratch<int>(1);
            lease[0] = 9;
            arena.ReleaseLeasesToNativeMemory();
            nuint released = trim(arena);
            Assert.True(released >= 4096);
            ArenaLease<int> fresh = arena.Scratch<int>(1);
            Assert.Equal(0, fresh[0]);
            arena.Dispose();
        }
    }

    [Fact]
    public void RegionRecyclesAScopedTailBeforeTrimmingWithoutMovingOrdinaryStorage()
    {
        NativeMemoryTestHooks.Reset();
        NativeRegion region = new(4096, NativeMemoryReturn.ToNativeMemory);
        Local<int> ordinary = region.Lease<int>(1);
        ordinary[0] = 31;
        scoped Local<byte> scopedTail = region.LeaseScoped<byte>(5_000);
        scopedTail[0] = 42;

        region.RecycleScoped();
        nuint released = region.TrimRetainedMemoryByBytes(1);

        Assert.True(released >= 4096);
        Assert.Equal(31, ordinary[0]);
        region.Dispose();
    }

    [Fact]
    public void RegionTrimDoesNotFreeAnActiveSegmentJustBecauseItsTailIsUnused()
    {
        NativeMemoryTestHooks.Reset();
        NativeRegion region = new(4096, NativeMemoryReturn.ToNativeMemory);
        Local<int> ordinary = region.Lease<int>(1);
        ordinary[0] = 17;

        nuint released = region.TrimRetainedMemory();

        Assert.Equal((nuint)0, released);
        Assert.Equal(17, ordinary[0]);
        region.Dispose();
    }

    [Fact]
    public void ScopedRecycleRewindsAHighEndSegmentWithoutMovingItsOrdinaryPrefix()
    {
        NativeMemoryTestHooks.Reset();
        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<byte> ordinaryPrefix = arena.Scratch<byte>(4_096);
        scoped ArenaLease<byte> scopedTail = arena.ScratchScoped<byte>(2_048);
        Assert.Equal(2, NativeMemoryTestHooks.Snapshot().AllocationCount);
        ArenaLease<byte> ordinaryInTailSegment = arena.Scratch<byte>(1_024);
        Assert.Equal(2, NativeMemoryTestHooks.Snapshot().AllocationCount);
        ordinaryPrefix[0] = 7;
        ordinaryInTailSegment[0] = 8;

        arena.RecycleScoped();
        long allocationsBeforeReuse = NativeMemoryTestHooks.Snapshot().AllocationCount;
        ArenaLease<byte> reusedTail = arena.Scratch<byte>(3_000);

        Assert.Equal(allocationsBeforeReuse, NativeMemoryTestHooks.Snapshot().AllocationCount);
        Assert.Equal(7, ordinaryPrefix[0]);
        Assert.Equal(8, ordinaryInTailSegment[0]);
        Assert.Equal(0, reusedTail[0]);
        arena.Dispose();
    }

    [Fact]
    public void ScopedPendingStorageIsNotRecycledByALaterAcquisitionOrTrim()
    {
        NativeMemoryTestHooks.Reset();
        (NativeArena arena, WeakReference weak) = CreatePendingArenaReference();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Assert.True(weak.IsAlive);

        arena.RecycleScoped();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Assert.False(weak.IsAlive);
        arena.Dispose();
    }

    private static (NativeArena Arena, WeakReference Weak) CreatePendingArenaReference()
    {
        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        scoped ArenaLease<object> pending = arena.ScratchScoped<object>(1);
        object held = new();
        WeakReference weak = new(held);
        pending[0] = held;
        held = null!;
        _ = arena.Scratch<byte>(1);
        _ = arena.TrimRetainedMemory();
        _ = arena.TrimRetainedMemoryByBytes(1);
        _ = arena.TrimRetainedMemoryByLeaseSize<byte>(1);
        return (arena, weak);
    }

    [Fact]
    public void TolerantArenaLeaseReleaseRetiresBusyStorageUntilTheEnteredOperationExits()
    {
        NativeMemoryTestHooks.Reset();
        NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ManualResetEventSlim entered = new();
        ManualResetEventSlim release = new();
        NativeMemoryTestHooks.SetOperationEntered(operation =>
        {
            if (operation == nameof(ArenaLease<int>.Access))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });

        Thread worker = new(() =>
        {
            ArenaLease<int> lease = arena.Scratch<int>(64);
            lease.Access(view => view[0] = 11);
        });

        try
        {
            worker.Start();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            arena.ReleaseLeasesToGarbageCollector();
            Assert.True(NativeMemoryTestHooks.Snapshot().RetiredNativeBytes > 0);
            release.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, NativeMemoryTestHooks.Snapshot().RetiredNativeBytes);
        }
        finally
        {
            release.Set();
            if (worker.IsAlive)
            {
                worker.Join(TimeSpan.FromSeconds(10));
            }

            NativeMemoryTestHooks.Reset();
            arena.Dispose();
        }
    }

    [Fact]
    public void DefaultArenaLeaseReportsUninitializedForEveryPublicOperation()
    {
        for (int operation = 0; operation < 8; operation++)
        {
            NativeAllocationUninitializedException exception = Assert.Throws<NativeAllocationUninitializedException>(
                () => CaptureDefaultArenaLeaseOperation(operation));
            Assert.Equal(NativeOwnerLifecycle.Uninitialized, exception.CurrentLifecycle);
        }
    }

    private static void CaptureDefaultArenaLeaseOperation(int operation)
    {
        ArenaLease<int> value = default;
        switch (operation)
        {
            case 0:
                _ = value.Length;
                break;
            case 1:
                _ = value.Capacity;
                break;
            case 2:
                _ = value[0];
                break;
            case 3:
                value.Clear();
                break;
            case 4:
                value.CopyFrom(ReadOnlySpan<int>.Empty);
                break;
            case 5:
                value.CopyTo(Span<int>.Empty);
                break;
            case 6:
                value.Access(static _ => { });
                break;
            default:
                _ = value.Read(static _ => 0);
                break;
        }
    }

    private static Exception Record(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected an exception.");
    }

    private static Exception RecordRegionLease(ref NativeRegion region)
    {
        try
        {
            region.Lease<int>(1);
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected an exception.");
    }

    private static Exception RecordRegionReturn(ref NativeRegion region)
    {
        try
        {
            region.ReturnMemoryToNativeMemory();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected an exception.");
    }
}
