using System.Reflection;
using System.Runtime.InteropServices;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class RuntimeLifecycleTests
{
    [Fact]
    public void EmptyAndPreallocatedPoolsExposeTypedLeases()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> lazy = new();
        Pooled<int> empty = lazy.Rent(0);
        Assert.Equal(0, empty.Length);
        Assert.Equal(0, empty.Capacity);
        empty.Dispose();
        lazy.Dispose();

        NativePool<int> preallocated = new(initialCapacity: 8, returnOnDispose: NativeReturn.ToNativeMemory);
        Pooled<int> lease = preallocated.Rent(3);
        Assert.Equal(3, lease.Length);
        Assert.Equal(8, lease.Capacity);
        lease.Dispose();
        preallocated.Dispose();

        NativeMemoryTestMetrics metrics = NativeMemoryTestHooks.Snapshot();
        Assert.Equal(1, metrics.AllocationCount);
        Assert.Equal(1, metrics.ZeroedAllocationCount);
        Assert.Equal(1, metrics.FreeCount);
    }

    [Fact]
    public void PoolGrowthAndSmallestSlabReuseWorkWithoutManagedBackingArrays()
    {
        NativePool<int> pool = new(initialCapacity: 4);
        Pooled<int> first = pool.Rent(4);
        Pooled<int> second = pool.Rent(9);
        Assert.Equal(4, first.Capacity);
        Assert.True(second.Capacity >= 9);
        Assert.Equal(0, second[0]);

        first.Access(static span => span.Fill(17));
        first.Dispose();
        second.Dispose();

        Pooled<int> reused = pool.Rent(3);
        Assert.Equal(4, reused.Capacity);
        Assert.Equal(0, reused.Read(static span => span[0]));
        reused.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void ReusedSlabsAreZeroedForShortAndLongLogicalRanges()
    {
        NativePool<int> pool = new(initialCapacity: 8);
        Pooled<int> longLease = pool.Rent(8);
        longLease.Access(static span => span.Fill(99));
        longLease.Dispose();

        Pooled<int> shortLease = pool.Rent(2);
        Assert.Equal(0, shortLease.Read(static span => span[0]));
        Assert.Equal(0, shortLease.Read(static span => span[1]));
        shortLease.Dispose();

        Pooled<int> longAgain = pool.Rent(8);
        Assert.Equal(0, longAgain.Read(static span =>
        {
            for (int index = 0; index < span.Length; index++)
            {
                if (span[index] != 0)
                {
                    return 1;
                }
            }

            return 0;
        }));
        longAgain.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void NativeReturnInvalidatesOldHandlesFreesImmediatelyAndAllowsNewGeneration()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(initialCapacity: 4, returnOnDispose: NativeReturn.ToNativeMemory);
        Pooled<int> oldLease = pool.Rent(1);
        oldLease[0] = 12;

        pool.ReturnToNativeMemory();
        long freeAfterReturn = NativeMemoryTestHooks.Snapshot().FreeCount;
        Assert.True(freeAfterReturn >= 1);
        NativeAllocationReturnedException exception = CaptureReturned(oldLease);
        Assert.Contains("generation", exception.Message, StringComparison.OrdinalIgnoreCase);
        oldLease.Dispose();

        pool.LeaseFromMemory();
        Pooled<int> newLease = pool.Rent(1);
        Assert.Equal(0, newLease[0]);
        newLease.Dispose();
        pool.Dispose();
        Assert.True(NativeMemoryTestHooks.Snapshot().FreeCount >= freeAfterReturn + 1);
    }

    [Fact]
    public void GarbageCollectorReturnDetachesPhysicalCleanupUntilGenerationOwnerFinalizes()
    {
        NativeMemoryTestHooks.Reset();
        DetachOneGeneration();

        NativeMemoryTestMetrics detached = NativeMemoryTestHooks.Snapshot();
        Assert.Equal(0, detached.FreeCount);
        Assert.Equal(1, detached.DetachedGenerationCount);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Assert.True(NativeMemoryTestHooks.Snapshot().FreeCount >= 1);
    }

    [Fact]
    public void LeaseFromMemoryDoesNotReviveOldValuesAndDisposeIsPermanent()
    {
        NativePool<int> pool = new();
        Pooled<int> oldLease = pool.Rent(1);
        pool.ReturnToGarbageCollector();
        pool.LeaseFromMemory();
        Pooled<int> newLease = pool.Rent(1);
        Assert.Equal(0, newLease[0]);
        Assert.IsType<NativeAllocationReturnedException>(CaptureReturned(oldLease));
        newLease.Dispose();

        pool.Dispose();
        Assert.Throws<NativeAllocationDisposedException>(pool.LeaseFromMemory);
        oldLease.Dispose();
    }

    [Fact]
    public void ConcurrentReLeasePublishesExactlyOneGeneration()
    {
        NativePool<int> pool = new(initialCapacity: 2);
        pool.ReturnToNativeMemory();
        int successes = 0;
        int stateFailures = 0;

        Parallel.For(0, 16, _ =>
        {
            try
            {
                pool.LeaseFromMemory();
                Interlocked.Increment(ref successes);
            }
            catch (NativeAllocationStateException)
            {
                Interlocked.Increment(ref stateFailures);
            }
        });

        Assert.Equal(1, successes);
        Assert.Equal(15, stateFailures);
        pool.Dispose();
    }

    [Fact]
    public void RegionAllocatesMixedTypesAndReclaimsAllSegmentsTogether()
    {
        NativeMemoryTestHooks.Reset();
        using NativeRegion region = new(preAllocateBytes: 16, returnOnDispose: NativeReturn.ToNativeMemory);
        Local<byte> bytes = region.Allocate<byte>(3);
        Local<long> longs = region.Allocate<long>(2);
        bytes.Access(static span =>
        {
            span[0] = 3;
            span[1] = 4;
            span[2] = 5;
        });
        Assert.Equal(12, bytes.Read(static span => span[0] + span[1] + span[2]));
        longs[0] = 40;
        longs[1] = 2;
        Assert.Equal(42, longs.Read(static span => span[0] + span[1]));
        region.ReturnToNativeMemory();
        Assert.True(NativeMemoryTestHooks.Snapshot().FreeCount >= 1);
        Assert.IsType<NativeAllocationReturnedException>(CaptureReturned(bytes));
    }

    [Fact]
    public void RegionGrowthAndGarbageCollectorReturnUseTheSameLifecycleGate()
    {
        NativeMemoryTestHooks.Reset();
        NativeRegion region = new(preAllocateBytes: 1, returnOnDispose: NativeReturn.ToGarbageCollector);
        Local<long> first = region.Allocate<long>(32);
        first[0] = 8;
        region.ReturnToGarbageCollector();
        Assert.IsType<NativeAllocationReturnedException>(CaptureReturned(first));
        Assert.Equal(1, NativeMemoryTestHooks.Snapshot().DetachedGenerationCount);
        region.Dispose();
    }

    [Fact]
    public void RegionUsingCleanupRunsOnFallthroughReturnAndException()
    {
        NativeMemoryTestHooks.Reset();
        long freeBefore = NativeMemoryTestHooks.Snapshot().FreeCount;
        RegionFallthrough();
        RegionReturn();
        RegionGoto();
        RegionLoop();
        try
        {
            RegionThrow();
        }
        catch (InvalidOperationException)
        {
        }

        Assert.True(NativeMemoryTestHooks.Snapshot().FreeCount >= freeBefore + 5);
    }

    [Fact]
    public void DetachedOldGenerationIsReclaimedWhileAReLeasedGenerationRemainsActive()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        NativeMemoryTestHooks.Reset();
        NativePool<long> pool = new(initialCapacity: 4);
        Pooled<long> oldLease = pool.Rent(4);
        oldLease.Access(static span => span.Fill(41));
        pool.ReturnToGarbageCollector();
        long detachedAfterReturn = NativeMemoryTestHooks.Snapshot().DetachedNativeBytes;
        pool.LeaseFromMemory();
        Pooled<long> currentLease = pool.Rent(4);
        Assert.Equal(0, currentLease[0]);

        oldLease.Dispose();
        oldLease = default;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        Assert.True(NativeMemoryTestHooks.Snapshot().DetachedNativeBytes < detachedAfterReturn);
        Assert.Equal(0, currentLease[0]);
        currentLease.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void CallbackExceptionsReleaseTheOperationToken()
    {
        NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(2);
        Exception? callbackException = null;
        try
        {
            lease.Access(static _ => throw new InvalidOperationException("callback"));
        }
        catch (Exception exception)
        {
            callbackException = exception;
        }

        Assert.IsType<InvalidOperationException>(callbackException);
        pool.ReturnToNativeMemory();
        lease.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void ArgumentValidationOccursBeforeCopyOperationEntry()
    {
        NativePool<int> pool = new();
        Pooled<int> lease = pool.Rent(2);
        Assert.IsType<ArgumentException>(CaptureArgumentFailure(lease, 0));
        Assert.IsType<ArgumentException>(CaptureArgumentFailure(lease, 1));
        Assert.IsType<ArgumentOutOfRangeException>(CaptureArgumentFailure(lease, 2));
        lease.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void ZeroLengthLeasesStillCarryGenerationIdentity()
    {
        NativePool<int> pool = new();
        Pooled<int> lease = pool.Rent(0);
        Assert.Equal(0, lease.Read(static span => span.Length));
        pool.ReturnToNativeMemory();
        Assert.IsType<NativeAllocationReturnedException>(CaptureReturned(lease));
        lease.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void InvalidPoliciesAndLengthsDoNotCreateOwners()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativePool<int>(returnOnDispose: (NativeReturn)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeRegion((NativeReturn)99));

        NativePool<int> pool = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(-1));
        pool.Dispose();

        NativeRegion region = new();
        Exception? regionException = null;
        try
        {
            region.Allocate<int>(-1);
        }
        catch (Exception exception)
        {
            regionException = exception;
        }

        Assert.IsType<ArgumentOutOfRangeException>(regionException);
        region.Dispose();
    }

    [Fact]
    public void DefaultOwnersAndHandlesFailOnEveryMeaningfulMember()
    {
        AssertUninitialized(ReadDefaultPooled);
        AssertUninitialized(ReadDefaultPooledCapacity);
        AssertUninitialized(ReadDefaultPooledIndexer);
        AssertUninitialized(ClearDefaultPooled);
        AssertUninitialized(CopyFromDefaultPooled);
        AssertUninitialized(CopyToDefaultPooled);
        AssertUninitialized(AccessDefaultPooled);
        AssertUninitialized(ReadCallbackDefaultPooled);
        AssertUninitialized(DisposeDefaultPooled);
        AssertUninitialized(ReadDefaultLocal);
        AssertUninitialized(ReadDefaultLocalCapacity);
        AssertUninitialized(ReadDefaultLocalIndexer);
        AssertUninitialized(ClearDefaultLocal);
        AssertUninitialized(CopyFromDefaultLocal);
        AssertUninitialized(CopyToDefaultLocal);
        AssertUninitialized(AccessDefaultLocal);
        AssertUninitialized(ReadCallbackDefaultLocal);
        AssertUninitialized(AllocateDefaultRegion);
        AssertUninitialized(ReturnNativeDefaultRegion);
        AssertUninitialized(ReturnGarbageDefaultRegion);
        AssertUninitialized(DisposeDefaultRegion);
    }

    [Fact]
    public void PublicHandlesDoNotExposeCachedNativeViews()
    {
        PropertyInfo[] pooledProperties = typeof(Pooled<int>).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo[] localProperties = typeof(Local<int>).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.Contains(pooledProperties, property => property.Name == "Length" && property.PropertyType == typeof(int));
        Assert.Contains(pooledProperties, property => property.Name == "Capacity" && property.PropertyType == typeof(int));
        Assert.DoesNotContain(pooledProperties, property => property.PropertyType == typeof(Span<int>));
        Assert.DoesNotContain(localProperties, property => property.PropertyType == typeof(Span<int>));
        Assert.Null(typeof(Pooled<int>).GetProperty("DangerousPointer", BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void AllocationFailureLeavesEarlierPoolStateValid()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(initialCapacity: 2);
        Pooled<int> existing = pool.Rent(2);
        NativeMemoryTestHooks.FailNextAllocation();
        NativeAllocationFailedException failure = Assert.Throws<NativeAllocationFailedException>(() => pool.Rent(100));
        Assert.Equal(NativeOwnerLifecycle.Active, failure.CurrentLifecycle);
        existing[0] = 7;
        Assert.Equal(7, existing[0]);
        existing.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void ReLeaseAllocationFailureDoesNotPublishAPartialGeneration()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(initialCapacity: 2);
        pool.ReturnToNativeMemory();
        NativeMemoryTestHooks.FailNextAllocation();

        NativeAllocationFailedException failure = Assert.Throws<NativeAllocationFailedException>(pool.LeaseFromMemory);
        Assert.Equal(NativeOwnerLifecycle.Returned, failure.CurrentLifecycle);
        Assert.Throws<NativeAllocationReturnedException>(() => pool.Rent(1));

        pool.LeaseFromMemory();
        Pooled<int> lease = pool.Rent(1);
        Assert.Equal(0, lease[0]);
        lease.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void AllocationFailuresReportInitialActiveGrowthAndReturnedReLeaseLifecycle()
    {
        NativeMemoryTestHooks.Reset();
        NativeMemoryTestHooks.FailNextAllocation();
        NativeAllocationFailedException initial = Assert.Throws<NativeAllocationFailedException>(
            () => new NativePool<int>(initialCapacity: 2));
        Assert.Equal(NativeOwnerLifecycle.Active, initial.CurrentLifecycle);

        NativeMemoryTestHooks.FailNextAllocation();
        NativeAllocationFailedException initialRegion = Assert.Throws<NativeAllocationFailedException>(
            () => new NativeRegion(preAllocateBytes: 8));
        Assert.Equal(NativeOwnerLifecycle.Active, initialRegion.CurrentLifecycle);

        NativeRegion region = new(preAllocateBytes: 1);
        NativeMemoryTestHooks.FailNextAllocation();
        NativeAllocationFailedException regionGrowth;
        try
        {
            _ = region.Allocate<long>(32);
            throw new Xunit.Sdk.XunitException("Expected a native region growth allocation failure.");
        }
        catch (NativeAllocationFailedException exception)
        {
            regionGrowth = exception;
        }
        Assert.Equal(NativeOwnerLifecycle.Active, regionGrowth.CurrentLifecycle);
        region.Dispose();

        NativePool<int> pool = new(initialCapacity: 2);
        NativeMemoryTestHooks.FailNextAllocation();
        NativeAllocationFailedException growth = Assert.Throws<NativeAllocationFailedException>(() => pool.Rent(4));
        Assert.Equal(NativeOwnerLifecycle.Active, growth.CurrentLifecycle);

        pool.ReturnToNativeMemory();
        NativeMemoryTestHooks.FailNextAllocation();
        NativeAllocationFailedException reLease = Assert.Throws<NativeAllocationFailedException>(pool.LeaseFromMemory);
        Assert.Equal(NativeOwnerLifecycle.Returned, reLease.CurrentLifecycle);

        pool.LeaseFromMemory();
        pool.Dispose();
    }

    [Fact]
    public void FailedIndividualLeaseReturnRestoresActiveStateWithoutRequeueing()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(initialCapacity: 4, returnOnDispose: NativeReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(4);
        lease[0] = 23;
        NativeMemoryTestHooks.FailNextClear();

        Exception? failure = null;
        try
        {
            lease.Dispose();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(23, lease[0]);

        lease.Dispose();
        Pooled<int> reused = pool.Rent(2);
        Assert.Equal(0, reused[0]);
        reused.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void ReturnPoliciesCoverPoolsAndRegionsWithAndWithoutInitialReservation()
    {
        NativeMemoryTestHooks.Reset();
        foreach (NativeReturn policy in Enum.GetValues<NativeReturn>())
        {
            foreach (int initialCapacity in new[] { 0, 4 })
            {
                NativeMemoryTestMetrics poolBefore = NativeMemoryTestHooks.Snapshot();
                NativePool<int> pool = new(initialCapacity, policy);
                Pooled<int> lease = pool.Rent(initialCapacity == 0 ? 1 : 3);
                lease.Dispose();
                pool.Dispose();
                NativeMemoryTestMetrics poolAfter = NativeMemoryTestHooks.Snapshot();
                Assert.True(poolAfter.AllocationCount > poolBefore.AllocationCount);
                if (policy == NativeReturn.ToNativeMemory)
                {
                    Assert.True(poolAfter.FreeCount > poolBefore.FreeCount);
                }
                else
                {
                    Assert.True(poolAfter.DetachedGenerationCount > poolBefore.DetachedGenerationCount);
                }

                NativeMemoryTestMetrics regionBefore = NativeMemoryTestHooks.Snapshot();
                NativeRegion region = new((nuint)(initialCapacity * sizeof(long)), policy);
                Local<long> local = region.Allocate<long>(2);
                local[0] = 8;

                region.Dispose();
                NativeMemoryTestMetrics regionAfter = NativeMemoryTestHooks.Snapshot();
                Assert.True(regionAfter.AllocationCount > regionBefore.AllocationCount);
                if (policy == NativeReturn.ToNativeMemory)
                {
                    Assert.True(regionAfter.FreeCount > regionBefore.FreeCount);
                }
                else
                {
                    Assert.True(regionAfter.DetachedGenerationCount > regionBefore.DetachedGenerationCount);
                }
            }
        }
    }

    [Fact]
    public void DoubleReturnAndPostDisposeExposeStructuredLifecycleFacts()
    {
        foreach (NativeReturn policy in Enum.GetValues<NativeReturn>())
        {
            NativePool<int> pool = new(returnOnDispose: policy);
            Pooled<int> stale = pool.Rent(1);
            if (policy == NativeReturn.ToNativeMemory)
            {
                pool.ReturnToNativeMemory();
            }
            else
            {
                pool.ReturnToGarbageCollector();
            }

            NativeAllocationReturnedException returned = CaptureReturned(stale);
            Assert.Equal(NativeOwnerLifecycle.Returned, returned.CurrentLifecycle);
            Assert.True(returned.CurrentGeneration > returned.Generation);
            Assert.DoesNotContain("0x", returned.Message, StringComparison.OrdinalIgnoreCase);
            if (policy == NativeReturn.ToNativeMemory)
            {
                Assert.Throws<NativeAllocationReturnedException>(pool.ReturnToNativeMemory);
            }
            else
            {
                Assert.Throws<NativeAllocationReturnedException>(pool.ReturnToGarbageCollector);
            }

            stale.Dispose();
            pool.LeaseFromMemory();
            pool.Dispose();
            NativeAllocationDisposedException disposed = Assert.Throws<NativeAllocationDisposedException>(() => pool.Rent(1));
            Assert.Equal(NativeOwnerLifecycle.Disposed, disposed.CurrentLifecycle);
            Assert.Contains("lifecycle", disposed.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void StateExceptionsReportTheObservedActiveLifecycle()
    {
        NativePool<int> pool = new();
        NativeAllocationStateException state = Assert.Throws<NativeAllocationStateException>(pool.LeaseFromMemory);
        Assert.Equal(NativeOwnerLifecycle.Active, state.CurrentLifecycle);
        pool.Dispose();
    }

    [Fact]
    public void InvalidCopyArgumentsDoNotChangeNativeOrManagedState()
    {
        NativePool<int> pool = new();
        Pooled<int> lease = pool.Rent(2);
        lease[0] = 11;
        lease[1] = 12;
        int[] source = [31];
        int[] destination = [41];

        Assert.IsType<ArgumentException>(CaptureArgumentFailure(lease, 0));
        Assert.IsType<ArgumentException>(CaptureArgumentFailure(lease, 1));
        Assert.Equal(new[] { 31 }, source);
        Assert.Equal(new[] { 41 }, destination);
        Assert.Equal(11, lease[0]);
        Assert.Equal(12, lease[1]);

        lease.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void NullCallbacksFailBeforeNativeStateChanges()
    {
        NativePool<int> pool = new();
        Pooled<int> pooled = pool.Rent(1);
        NativeMemoryTestMetrics before = NativeMemoryTestHooks.Snapshot();
        Assert.IsType<ArgumentNullException>(CaptureNullPooledCallback(pooled, read: false));
        Assert.IsType<ArgumentNullException>(CaptureNullPooledCallback(pooled, read: true));
        NativeMemoryTestMetrics afterPooled = NativeMemoryTestHooks.Snapshot();
        Assert.Equal(before.OutstandingNativeBytes, afterPooled.OutstandingNativeBytes);
        pooled.Dispose();
        pool.Dispose();

        NativeRegion region = new();
        Local<int> local = region.Allocate<int>(1);
        NativeMemoryTestMetrics beforeLocal = NativeMemoryTestHooks.Snapshot();
        Assert.IsType<ArgumentNullException>(CaptureNullLocalCallback(local, read: false));
        Assert.IsType<ArgumentNullException>(CaptureNullLocalCallback(local, read: true));
        NativeMemoryTestMetrics afterLocal = NativeMemoryTestHooks.Snapshot();
        Assert.Equal(beforeLocal.OutstandingNativeBytes, afterLocal.OutstandingNativeBytes);
        region.Dispose();
    }

    [Fact]
    public void DetachedNativeBytesRemainAccountedUntilFinalization()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<long> pool = new(initialCapacity: 4);
        Pooled<long> lease = pool.Rent(4);
        long allocatedBytes = NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes;
        pool.ReturnToGarbageCollector();
        NativeMemoryTestMetrics detached = NativeMemoryTestHooks.Snapshot();
        Assert.True(allocatedBytes > 0);
        Assert.Equal(allocatedBytes, detached.DetachedNativeBytes);
        Assert.Equal(allocatedBytes, detached.OutstandingNativeBytes);
        lease.Dispose();
        pool = null!;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    private static void DetachOneGeneration()
    {
        NativePool<int> pool = new(initialCapacity: 4);
        Pooled<int> lease = pool.Rent(4);
        lease.Access(static span => span.Fill(1));
        pool.ReturnToGarbageCollector();
        lease.Dispose();
    }

    private static void RegionFallthrough()
    {
        using NativeRegion region = new(16, NativeReturn.ToNativeMemory);
        Local<int> local = region.Allocate<int>(1);
        local[0] = 1;
    }

    private static int RegionReturn()
    {
        using NativeRegion region = new(16, NativeReturn.ToNativeMemory);
        Local<int> local = region.Allocate<int>(1);
        local[0] = 2;
        return local[0];
    }

    private static void RegionGoto()
    {
        using NativeRegion region = new(16, NativeReturn.ToNativeMemory);
        Local<int> local = region.Allocate<int>(1);
        if (local.Length == 1)
        {
            goto End;
        }

        local[0] = 4;
    End:
        _ = local.Length;
    }

    private static void RegionLoop()
    {
        using NativeRegion region = new(16, NativeReturn.ToNativeMemory);
        Local<int> local = region.Allocate<int>(1);
        for (int index = 0; index < 2; index++)
        {
            if (index == 0)
            {
                continue;
            }

            local[0] = index;
            break;
        }
    }

    private static void RegionThrow()
    {
        using NativeRegion region = new(16, NativeReturn.ToNativeMemory);
        Local<int> local = region.Allocate<int>(1);
        local[0] = 3;
        throw new InvalidOperationException("region exit");
    }

    private static void ReadFirst(Pooled<int> lease)
    {
        _ = lease[0];
    }

    private static void ReadLocal(Local<byte> local)
    {
        _ = local[0];
    }

    private static void AssertUninitialized(Action operation)
    {
        NativeAllocationUninitializedException exception = Assert.Throws<NativeAllocationUninitializedException>(operation);
        Assert.Equal(NativeOwnerLifecycle.Uninitialized, exception.CurrentLifecycle);
    }

    private static void ReadDefaultPooled()
    {
        Pooled<int> value = default;
        _ = value.Length;
    }

    private static void DisposeDefaultPooled()
    {
        Pooled<int> value = default;
        value.Dispose();
    }

    private static void ReadDefaultPooledCapacity()
    {
        Pooled<int> value = default;
        _ = value.Capacity;
    }

    private static void ReadDefaultPooledIndexer()
    {
        Pooled<int> value = default;
        _ = value[0];
    }

    private static void ClearDefaultPooled()
    {
        Pooled<int> value = default;
        value.Clear();
    }

    private static void CopyFromDefaultPooled()
    {
        Pooled<int> value = default;
        value.CopyFrom(ReadOnlySpan<int>.Empty);
    }

    private static void CopyToDefaultPooled()
    {
        Pooled<int> value = default;
        value.CopyTo(Span<int>.Empty);
    }

    private static void AccessDefaultPooled()
    {
        Pooled<int> value = default;
        value.Access(static _ => { });
    }

    private static void ReadCallbackDefaultPooled()
    {
        Pooled<int> value = default;
        _ = value.Read(static _ => 0);
    }

    private static void ReadDefaultLocal()
    {
        Local<int> value = default;
        _ = value.Length;
    }

    private static void ReadDefaultLocalCapacity()
    {
        Local<int> value = default;
        _ = value.Capacity;
    }

    private static void ReadDefaultLocalIndexer()
    {
        Local<int> value = default;
        _ = value[0];
    }

    private static void ClearDefaultLocal()
    {
        Local<int> value = default;
        value.Clear();
    }

    private static void CopyFromDefaultLocal()
    {
        Local<int> value = default;
        value.CopyFrom(ReadOnlySpan<int>.Empty);
    }

    private static void CopyToDefaultLocal()
    {
        Local<int> value = default;
        value.CopyTo(Span<int>.Empty);
    }

    private static void AccessDefaultLocal()
    {
        Local<int> value = default;
        value.Access(static _ => { });
    }

    private static void ReadCallbackDefaultLocal()
    {
        Local<int> value = default;
        _ = value.Read(static _ => 0);
    }

    private static void AllocateDefaultRegion()
    {
        NativeRegion value = default;
        _ = value.Allocate<int>(1);
    }

    private static void ReturnNativeDefaultRegion()
    {
        NativeRegion value = default;
        value.ReturnToNativeMemory();
    }

    private static void ReturnGarbageDefaultRegion()
    {
        NativeRegion value = default;
        value.ReturnToGarbageCollector();
    }

    private static void DisposeDefaultRegion()
    {
        NativeRegion value = default;
        value.Dispose();
    }

    private static Exception? CaptureArgumentFailure(Pooled<int> lease, int operation)
    {
        try
        {
            switch (operation)
            {
                case 0:
                    lease.CopyFrom(new int[1]);
                    break;
                case 1:
                    lease.CopyTo(new int[1]);
                    break;
                default:
                    _ = lease[2];
                    break;
            }
        }
        catch (Exception exception)
        {
            return exception;
        }

        return null;
    }

    private static Exception? CaptureNullPooledCallback(Pooled<int> pooled, bool read)
    {
        try
        {
            if (read)
            {
                _ = pooled.Read<int>(null!);
            }
            else
            {
                pooled.Access(null!);
            }
        }
        catch (Exception exception)
        {
            return exception;
        }

        return null;
    }

    private static Exception? CaptureNullLocalCallback(Local<int> local, bool read)
    {
        try
        {
            if (read)
            {
                _ = local.Read<int>(null!);
            }
            else
            {
                local.Access(null!);
            }
        }
        catch (Exception exception)
        {
            return exception;
        }

        return null;
    }

    private static NativeAllocationReturnedException CaptureReturned(Pooled<int> lease)
    {
        try
        {
            ReadFirst(lease);
        }
        catch (NativeAllocationReturnedException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected NativeAllocationReturnedException.");
    }

    private static NativeAllocationReturnedException CaptureReturned(Local<byte> local)
    {
        try
        {
            ReadLocal(local);
        }
        catch (NativeAllocationReturnedException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected NativeAllocationReturnedException.");
    }

    private static NativeAllocationReturnedException CaptureReturned(Local<long> local)
    {
        try
        {
            _ = local[0];
        }
        catch (NativeAllocationReturnedException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected NativeAllocationReturnedException.");
    }
}
