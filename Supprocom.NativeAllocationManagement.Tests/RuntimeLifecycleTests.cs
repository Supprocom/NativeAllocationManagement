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
        NativePool<int> pool = new(initialCapacity: 2);
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
        NativePool<int> pool = new();
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
        Assert.Throws<NativeAllocationUninitializedException>(ReadDefaultPooled);
        Assert.Throws<NativeAllocationUninitializedException>(DisposeDefaultPooled);
        Assert.Throws<NativeAllocationUninitializedException>(ReadDefaultLocal);
        Assert.Throws<NativeAllocationUninitializedException>(DisposeDefaultRegion);
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
        Assert.Throws<NativeAllocationFailedException>(() => pool.Rent(100));
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

        Assert.Throws<NativeAllocationFailedException>(pool.LeaseFromMemory);
        Assert.Throws<NativeAllocationReturnedException>(() => pool.Rent(1));

        pool.LeaseFromMemory();
        Pooled<int> lease = pool.Rent(1);
        Assert.Equal(0, lease[0]);
        lease.Dispose();
        pool.Dispose();
    }

    private static void DetachOneGeneration()
    {
        NativePool<int> pool = new(initialCapacity: 4);
        Pooled<int> lease = pool.Rent(4);
        lease.Access(static span => span.Fill(1));
        pool.ReturnToGarbageCollector();
        lease.Dispose();
    }

    private static void ReadFirst(Pooled<int> lease)
    {
        _ = lease[0];
    }

    private static void ReadLocal(Local<byte> local)
    {
        _ = local[0];
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

    private static void ReadDefaultLocal()
    {
        Local<int> value = default;
        _ = value.Length;
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
