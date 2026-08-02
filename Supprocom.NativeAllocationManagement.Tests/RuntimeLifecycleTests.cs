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
        Pooled<int> empty = lazy.Rent(0, static writer => writer.Fill(default!));
        Assert.Equal(0, empty.Length);
        Assert.Equal(0, empty.Capacity);
        empty.Dispose();
        lazy.Dispose();

        NativePool<int> preallocated = new(preLease: 8, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> lease = preallocated.Rent(3, static writer => writer.Fill(default!));
        Assert.Equal(3, lease.Length);
        Assert.Equal(8, lease.Capacity);
        lease.Dispose();
        preallocated.Dispose();

        NativeMemoryTestMetrics metrics = NativeMemoryTestHooks.Snapshot();
        Assert.Equal(1, metrics.AllocationCount);
        Assert.Equal(0, metrics.ZeroedAllocationCount);
        Assert.Equal(1, metrics.FreeCount);
        Assert.Equal(0, metrics.ReusedNativeSegmentCount);
    }

    [Fact]
    public void PoolPublishesOnlyACompletelyInitializedLease()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Assert.Throws<InvalidOperationException>(
            () => pool.Rent(
                4,
                static writer =>
                {
                    writer.Write(17);
                    writer.Write(19);
                }));
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);

        Pooled<int> lease = pool.Rent(
            4,
            static writer =>
            {
                writer.Write(1);
                writer.Write(2);
                writer.Write(3);
                writer.Write(4);
            });
        Assert.Equal(1, lease[0]);
        Assert.Equal(4, lease[3]);
        Assert.Equal(0, NativeMemoryTestHooks.Snapshot().ZeroedAllocationCount);

        lease.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void SequentialWriterPublishesOnlyItsCompleteValueRange()
    {
        NativePool<int> pool = new(
            preLease: 6,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Pooled<int> lease = pool.Rent(
            6,
            static writer =>
            {
                NativeSequentialLeaseWriter<int> range =
                    writer.BeginSequentialRange(0, 6);
                range.Write(1);
                range.Write(stackalloc int[] { 2, 3, 4 });
                range.Fill(2, 5);
                Assert.Equal(1, range.ReadWritten(0));
                Assert.Equal(5, range.ReadWritten(5));
                range.Complete();
            });

        Assert.Equal(1, lease[0]);
        Assert.Equal(2, lease[1]);
        Assert.Equal(3, lease[2]);
        Assert.Equal(4, lease[3]);
        Assert.Equal(5, lease[4]);
        Assert.Equal(5, lease[5]);
        lease.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void SequentialWriterRejectsIncompleteAndExcessWrites()
    {
        NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Assert.Throws<InvalidOperationException>(
            () => pool.Rent(
                4,
                static writer =>
                {
                    NativeSequentialLeaseWriter<int> range =
                        writer.BeginSequentialRange(0, 4);
                    try
                    {
                        _ = range.ReadWritten(0);
                        throw new InvalidDataException(
                            "The writer exposed an unwritten value.");
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    range.Write(1);
                }));
        Assert.Throws<InvalidOperationException>(
            () => pool.Rent(
                4,
                static writer =>
                {
                    NativeSequentialLeaseWriter<int> range =
                        writer.BeginSequentialRange(0, 2);
                    range.Write(stackalloc int[] { 1, 2 });
                    range.Write(3);
                }));
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);

        pool.Dispose();
    }

    [Fact]
    public void SequentialWriterRejectsGapsAndRewritesInitializedRanges()
    {
        NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Assert.Throws<InvalidOperationException>(
            () => pool.Rent(
                4,
                static writer =>
                {
                    _ = writer.BeginSequentialRange(1, 2);
                    writer.Fill(0);
                }));

        Pooled<int> lease = pool.Rent(
            4,
            static writer =>
            {
                NativeSequentialLeaseWriter<int> initial =
                    writer.BeginSequentialRange(0, 4);
                initial.Fill(7);
                initial.Complete();

                NativeSequentialLeaseWriter<int> replacement =
                    writer.BeginSequentialRange(1, 2);
                replacement.Write(stackalloc int[] { 11, 13 });
                replacement.Complete();
            });

        Assert.Equal(7, lease[0]);
        Assert.Equal(11, lease[1]);
        Assert.Equal(13, lease[2]);
        Assert.Equal(7, lease[3]);
        lease.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void InitializedValueSpanSupportsCheckedBulkRewrite()
    {
        NativePool<int> pool = new(
            preLease: 5,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Pooled<int> lease = pool.Rent(
            5,
            static writer =>
            {
                writer.Fill(3);
                Span<int> initialized =
                    writer.RewriteInitializedSpan(1, 3);
                initialized[0] = 5;
                initialized[1] = 7;
                initialized[2] = 11;
            });

        Assert.Equal(3, lease[0]);
        Assert.Equal(5, lease[1]);
        Assert.Equal(7, lease[2]);
        Assert.Equal(11, lease[3]);
        Assert.Equal(3, lease[4]);
        lease.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void InitializedValueSpanRejectsUnwrittenAndReferenceStorage()
    {
        NativePool<int> values = new(
            preLease: 2,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Assert.Throws<InvalidOperationException>(
            () => values.Rent(
                2,
                static writer =>
                {
                    writer.Write(1);
                    _ = writer.RewriteInitializedSpan(0, 2);
                }));
        Assert.Equal(0, values.CurrentAllocationRecordCountForTest);
        values.Dispose();

        NativePool<object> references = new(
            preLease: 2,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Assert.Throws<NotSupportedException>(
            () => references.Rent(
                2,
                static writer =>
                {
                    writer.Fill(new object());
                    _ = writer.RewriteInitializedSpan(0, 2);
                }));
        Assert.Equal(0, references.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, references.CurrentReferenceRootCountForTest);
        references.Dispose();
    }

    [Fact]
    public void FailedSequentialReferenceWriteRemovesEveryRoot()
    {
        NativePool<object> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        object first = new();
        object second = new();

        Assert.Throws<InvalidOperationException>(
            () => pool.Rent(
                4,
                writer =>
                {
                    NativeSequentialLeaseWriter<object> range =
                        writer.BeginSequentialRange(0, 4);
                    range.Write(first);
                    range.Write(second);
                    throw new InvalidOperationException(
                        "Stop the test initializer.");
                }));

        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, pool.CurrentReferenceRootCountForTest);
        pool.Dispose();
    }

    [Fact]
    public void InitializerReadsAndReplacesOnlyItsInitializedPrefix()
    {
        NativePool<int> pool = new(
            preLease: 6,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Pooled<int> lease = pool.Rent(
            6,
            static writer =>
            {
                writer.WriteAt(0, 3);
                writer.WriteAt(
                    0,
                    writer.ReadInitialized(0) + 4);
                writer.Fill(1, 3, 11);
                writer.WriteAt(
                    2,
                    writer.ReadInitialized(1)
                    + writer.ReadInitialized(2));
                writer.WriteAt(4, 29);
                writer.Fill(5, 1, 31);
            });

        Assert.Equal(7, lease[0]);
        Assert.Equal(11, lease[1]);
        Assert.Equal(22, lease[2]);
        Assert.Equal(11, lease[3]);
        Assert.Equal(29, lease[4]);
        Assert.Equal(31, lease[5]);
        lease.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void InitializerRejectsDirtyReadsAndUninitializedGaps()
    {
        NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> dirty = pool.Rent(
            4,
            static writer => writer.Fill(91));
        dirty.Dispose();

        Assert.Throws<InvalidOperationException>(
            () => pool.Rent(
                4,
                static writer =>
                {
                    _ = writer.ReadInitialized(0);
                    writer.Fill(0);
                }));
        Assert.Throws<InvalidOperationException>(
            () => pool.Rent(
                4,
                static writer =>
                {
                    writer.WriteAt(1, 7);
                    writer.Fill(0);
                }));
        Assert.Throws<InvalidOperationException>(
            () => pool.Rent(
                4,
                static writer =>
                {
                    writer.Fill(1, 1, 7);
                    writer.Fill(0);
                }));
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);

        Pooled<int> valid = pool.Rent(
            4,
            static writer =>
            {
                writer.Write(1);
                writer.Write(2);
                writer.Write(3);
                writer.Write(4);
            });
        Assert.Equal(1, valid[0]);
        Assert.Equal(4, valid[3]);
        valid.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void FailedReferenceReplacementRemovesAllInitializerRoots()
    {
        NativePool<object> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        object first = new();
        object replacement = new();

        Assert.Throws<InvalidOperationException>(
            () => pool.Rent(
                4,
                writer =>
                {
                    writer.Write(first);
                    writer.WriteAt(0, replacement);
                    Assert.Same(
                        replacement,
                        writer.ReadInitialized(0));
                    writer.Fill(1, 2, first);
                    throw new InvalidOperationException(
                        "Stop the test initializer.");
                }));

        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, pool.CurrentReferenceRootCountForTest);
        Pooled<object> valid = pool.Rent(
            4,
            writer =>
            {
                writer.Write(replacement);
                writer.Fill(1, 3, first);
            });
        Assert.Same(replacement, valid[0]);
        Assert.Same(first, valid[3]);
        valid.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void FailedReferenceInitializationRemovesEveryPublishedRoot()
    {
        NativePool<object> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        object marker = new();

        Assert.Throws<InvalidOperationException>(
            () => pool.Rent(
                4,
                writer =>
                {
                    writer.Write(marker);
                    writer.Write(new object());
                    throw new InvalidOperationException(
                        "Stop the test initializer.");
                }));

        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, pool.CurrentReferenceRootCountForTest);

        Pooled<object> lease = pool.Rent(
            4,
            static writer => writer.Fill(null!));
        Assert.Null(lease[0]);
        Assert.Null(lease[3]);
        lease.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void DelayedPoolActivationIsAllocationFreeAndPublishesTheReservedGeneration()
    {
        foreach (NativeMemoryReturn policy in Enum.GetValues<NativeMemoryReturn>())
        {
            foreach (int preLease in new[] { 0, 8 })
            {
                NativeMemoryTestHooks.Reset();
                NativePool<int> pool = new(
                    preLease,
                    policy,
                    doNotLeaseOnDeclaration: true);
                NativeMemoryTestMetrics before = NativeMemoryTestHooks.Snapshot();

                Assert.Equal(NativeOwnerLifecycle.Unleased, pool.CurrentLifecycle);
                NativeAllocationStateException rentBeforeActivation = Assert.Throws<NativeAllocationStateException>(() => pool.Rent(1, static writer => writer.Fill(default!)));
                Assert.Equal(NativeOwnerLifecycle.Unleased, rentBeforeActivation.CurrentLifecycle);
                NativeAllocationStateException nativeReturnBeforeActivation = Assert.Throws<NativeAllocationStateException>(pool.ReturnMemoryToNativeMemory);
                NativeAllocationStateException garbageReturnBeforeActivation = Assert.Throws<NativeAllocationStateException>(pool.ReturnMemoryToGarbageCollector);
                Assert.Equal(NativeOwnerLifecycle.Unleased, nativeReturnBeforeActivation.CurrentLifecycle);
                Assert.Equal(NativeOwnerLifecycle.Unleased, garbageReturnBeforeActivation.CurrentLifecycle);
                Assert.Throws<NativeAllocationStateException>(pool.ReleaseLeasesToNativeMemory);
                Assert.Throws<NativeAllocationStateException>(pool.ReleaseLeasesToGarbageCollector);
                Assert.Throws<NativeAllocationStateException>(pool.RecycleScoped);
                Assert.Equal((nuint)0, pool.TrimRetainedMemory());
                Assert.Equal((nuint)0, pool.TrimRetainedMemoryByBytes(1));
                Assert.Equal((nuint)0, pool.TrimRetainedMemoryByLeaseSize(1));
                Assert.Equal(before.AllocationCount, NativeMemoryTestHooks.Snapshot().AllocationCount);

                pool.LeaseFromMemory();
                Assert.Equal(NativeOwnerLifecycle.Active, pool.CurrentLifecycle);
                Pooled<int> values = pool.Rent(preLease == 0 ? 0 : 3, static writer => writer.Fill(default!));
                Assert.Equal(preLease == 0 ? 0 : preLease, values.Capacity);
                values.Dispose();
                Assert.Throws<NativeAllocationStateException>(pool.LeaseFromMemory);
                if (policy == NativeMemoryReturn.ToNativeMemory)
                {
                    pool.ReturnMemoryToNativeMemory();
                }
                else
                {
                    pool.ReturnMemoryToGarbageCollector();
                }

                Assert.Equal(NativeOwnerLifecycle.Returned, pool.CurrentLifecycle);
                pool.LeaseFromMemory();
                Pooled<int> next = pool.Rent(0, static writer => writer.Fill(default!));
                next.Dispose();
                pool.Dispose();
            }
        }
    }

    [Fact]
    public void DelayedRegionActivationRetainsReservationAndKeepsReturnedRegionsTerminal()
    {
        foreach (NativeMemoryReturn policy in Enum.GetValues<NativeMemoryReturn>())
        {
            foreach (nuint reservation in new nuint[] { 0, 32 })
            {
                NativeMemoryTestHooks.Reset();
                using NativeRegion region = new(
                    reservation,
                    policy,
                    doNotLeaseOnDeclaration: true);
                NativeMemoryTestMetrics before = NativeMemoryTestHooks.Snapshot();

                Assert.Equal(NativeOwnerLifecycle.Unleased, region.CurrentLifecycle);
                NativeAllocationStateException leaseBeforeActivation = CaptureRegionLeaseState(region);
                Assert.Equal(NativeOwnerLifecycle.Unleased, leaseBeforeActivation.CurrentLifecycle);
                NativeAllocationStateException nativeReturnBeforeActivation = CaptureRegionNativeMemoryReturnState(region);
                NativeAllocationStateException garbageReturnBeforeActivation = CaptureRegionGarbageReturnState(region);
                Assert.Equal(NativeOwnerLifecycle.Unleased, nativeReturnBeforeActivation.CurrentLifecycle);
                Assert.Equal(NativeOwnerLifecycle.Unleased, garbageReturnBeforeActivation.CurrentLifecycle);
                Assert.IsType<NativeAllocationStateException>(CaptureRegionRecycleState(region));
                Assert.Equal((nuint)0, CaptureRegionTrimState(region, 0));
                Assert.Equal((nuint)0, CaptureRegionTrimState(region, 1));
                Assert.Equal((nuint)0, CaptureRegionTrimState(region, 2));
                Assert.Equal(before.AllocationCount, NativeMemoryTestHooks.Snapshot().AllocationCount);

                region.LeaseFromMemory();
                Assert.Equal(NativeOwnerLifecycle.Active, region.CurrentLifecycle);
                NativeAllocationStateException duplicateActivation = CaptureRegionActiveActivation(region);
                Assert.Equal(NativeOwnerLifecycle.Active, duplicateActivation.CurrentLifecycle);
                Local<int> values = region.Lease<int>(0, static writer => writer.Fill(default!));
                Assert.Equal(0, values.Length);
                if (policy == NativeMemoryReturn.ToNativeMemory)
                {
                    region.ReturnMemoryToNativeMemory();
                }
                else
                {
                    region.ReturnMemoryToGarbageCollector();
                }

                Assert.Equal(NativeOwnerLifecycle.Returned, region.CurrentLifecycle);
                NativeAllocationStateException reLease = CaptureRegionReLeaseState(region);
                Assert.Equal(NativeOwnerLifecycle.Returned, reLease.CurrentLifecycle);
            }
        }
    }

    [Fact]
    public void DisposingUnleasedOwnersIsTerminalAndAllocationFree()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(preLease: 8, doNotLeaseOnDeclaration: true);
        NativeRegion region = new(preAllocateBytes: 32, doNotLeaseOnDeclaration: true);
        NativeArena arena = new(preAllocateBytes: 64, doNotLeaseOnDeclaration: true);
        NativeMemoryTestMetrics before = NativeMemoryTestHooks.Snapshot();

        pool.Dispose();
        region.Dispose();
        arena.Dispose();

        Assert.Equal(NativeOwnerLifecycle.Disposed, pool.CurrentLifecycle);
        Assert.Equal(NativeOwnerLifecycle.Disposed, region.CurrentLifecycle);
        Assert.Equal(NativeOwnerLifecycle.Disposed, arena.CurrentLifecycle);
        Assert.Equal(before.AllocationCount, NativeMemoryTestHooks.Snapshot().AllocationCount);
        Assert.Throws<NativeAllocationDisposedException>(() => pool.LeaseFromMemory());
        Assert.IsType<NativeAllocationDisposedException>(CaptureRegionDisposedActivation(region));
        Assert.Throws<NativeAllocationDisposedException>(() => arena.LeaseFromMemory());
    }

    [Fact]
    public void DelayedActivationFailuresRemainUnleasedAndRetryAtomically()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(preLease: 4, doNotLeaseOnDeclaration: true);
        NativeMemoryTestHooks.FailNextAllocation();
        NativeAllocationFailedException poolFailure = Assert.Throws<NativeAllocationFailedException>(pool.LeaseFromMemory);
        Assert.Equal(NativeOwnerLifecycle.Unleased, poolFailure.CurrentLifecycle);
        Assert.Equal(NativeOwnerLifecycle.Unleased, pool.CurrentLifecycle);
        pool.LeaseFromMemory();
        Pooled<int> poolLease = pool.Rent(1, static writer => writer.Fill(default!));
        poolLease.Dispose();
        pool.Dispose();

        NativeMemoryTestHooks.Reset();
        using NativeRegion region = new(preAllocateBytes: 32, doNotLeaseOnDeclaration: true);
        NativeMemoryTestHooks.FailNextAllocation();
        NativeAllocationFailedException regionFailure = CaptureRegionActivationFailure(region);
        Assert.Equal(NativeOwnerLifecycle.Unleased, regionFailure.CurrentLifecycle);
        Assert.Equal(NativeOwnerLifecycle.Unleased, region.CurrentLifecycle);
        region.LeaseFromMemory();
        Local<int> local = region.Lease<int>(1, static writer => writer.Fill(default!));
        local[0] = 17;
        Assert.Equal(17, local[0]);
    }

    [Fact]
    public void ConcurrentInitialActivationPublishesExactlyOneGeneration()
    {
        NativePool<int> pool = new(doNotLeaseOnDeclaration: true);
        int poolSuccesses = 0;
        int poolFailures = 0;
        Parallel.For(0, 16, _ =>
        {
            try
            {
                pool.LeaseFromMemory();
                Interlocked.Increment(ref poolSuccesses);
            }
            catch (NativeAllocationStateException)
            {
                Interlocked.Increment(ref poolFailures);
            }
        });

        Assert.Equal(1, poolSuccesses);
        Assert.Equal(15, poolFailures);
        pool.Dispose();

    }

    [Fact]
    public void PoolGrowthAndSmallestSlabReuseWorkWithoutManagedBackingArrays()
    {
        NativePool<int> pool = new(preLease: 4);
        Pooled<int> first = pool.Rent(4, static writer => writer.Fill(default!));
        Pooled<int> second = pool.Rent(9, static writer => writer.Fill(default!));
        Assert.Equal(4, first.Capacity);
        Assert.True(second.Capacity >= 9);
        Assert.Equal(0, second[0]);

        first.Access(static span => span.Fill(17));
        first.Dispose();
        second.Dispose();

        Pooled<int> reused = pool.Rent(3, static writer => writer.Fill(default!));
        Assert.Equal(4, reused.Capacity);
        Assert.Equal(0, reused.Read(static span => span[0]));
        reused.Dispose();
        Assert.True(NativeMemoryDiagnostics.Snapshot().ReusedNativeSegmentCount > 0);
        pool.Dispose();
    }

    [Fact]
    public void ReusedSlabsAreZeroedForShortAndLongLogicalRanges()
    {
        NativePool<int> pool = new(preLease: 8);
        Pooled<int> longLease = pool.Rent(8, static writer => writer.Fill(default!));
        longLease.Access(static span => span.Fill(99));
        longLease.Dispose();

        Pooled<int> shortLease = pool.Rent(2, static writer => writer.Fill(default!));
        Assert.Equal(0, shortLease.Read(static span => span[0]));
        Assert.Equal(0, shortLease.Read(static span => span[1]));
        shortLease.Dispose();

        Pooled<int> longAgain = pool.Rent(8, static writer => writer.Fill(default!));
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
    public void NativeMemoryReturnInvalidatesOldHandlesFreesImmediatelyAndAllowsNewGeneration()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(preLease: 4, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> oldLease = pool.Rent(1, static writer => writer.Fill(default!));
        oldLease[0] = 12;

        pool.ReturnMemoryToNativeMemory();
        long freeAfterReturn = NativeMemoryTestHooks.Snapshot().FreeCount;
        Assert.True(freeAfterReturn >= 1);
        NativeAllocationReturnedException exception = CaptureReturned(oldLease);
        Assert.Contains("generation", exception.Message, StringComparison.OrdinalIgnoreCase);
        oldLease.Dispose();

        pool.LeaseFromMemory();
        Pooled<int> newLease = pool.Rent(1, static writer => writer.Fill(default!));
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
        Pooled<int> oldLease = pool.Rent(1, static writer => writer.Fill(default!));
        pool.ReturnMemoryToGarbageCollector();
        pool.LeaseFromMemory();
        Pooled<int> newLease = pool.Rent(1, static writer => writer.Fill(default!));
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
        NativePool<int> pool = new(preLease: 2);
        pool.ReturnMemoryToNativeMemory();
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
    public void RegionLeasesMixedTypesAndReclaimsAllSegmentsTogether()
    {
        NativeMemoryTestHooks.Reset();
        using NativeRegion region = new(preAllocateBytes: 16, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Local<byte> bytes = region.Lease<byte>(3, static writer => writer.Fill(default!));
        Local<long> longs = region.Lease<long>(2, static writer => writer.Fill(default!));
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
        region.ReturnMemoryToNativeMemory();
        Assert.True(NativeMemoryTestHooks.Snapshot().FreeCount >= 1);
        Assert.IsType<NativeAllocationReturnedException>(CaptureReturned(bytes));
    }

    [Fact]
    public void RegionGrowthAndGarbageCollectorReturnUseTheSameLifecycleGate()
    {
        NativeMemoryTestHooks.Reset();
        NativeRegion region = new(preAllocateBytes: 1, returnMemoryOnDispose: NativeMemoryReturn.ToGarbageCollector);
        Local<long> first = region.Lease<long>(32, static writer => writer.Fill(default!));
        first[0] = 8;
        region.ReturnMemoryToGarbageCollector();
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
        NativePool<long> pool = new(preLease: 4);
        long detachedAfterReturn =
            DetachOldGenerationAndReleaseHandle(pool);
        pool.LeaseFromMemory();
        Pooled<long> currentLease = pool.Rent(4, static writer => writer.Fill(default!));
        Assert.Equal(0, currentLease[0]);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        Assert.True(NativeMemoryTestHooks.Snapshot().DetachedNativeBytes < detachedAfterReturn);
        Assert.Equal(0, currentLease[0]);
        currentLease.Dispose();
        pool.Dispose();
    }

    private static long DetachOldGenerationAndReleaseHandle(
        NativePool<long> pool)
    {
        Pooled<long> lease = pool.Rent(
            4,
            static writer => writer.Fill(default!));
        lease.Access(static span => span.Fill(41));
        pool.ReturnMemoryToGarbageCollector();
        long detachedBytes =
            NativeMemoryTestHooks.Snapshot().DetachedNativeBytes;
        lease.Dispose();
        return detachedBytes;
    }

    [Fact]
    public void CallbackExceptionsReleaseTheOperationToken()
    {
        NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<int> lease = pool.Rent(2, static writer => writer.Fill(default!));
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
        pool.ReturnMemoryToNativeMemory();
        lease.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void ArgumentValidationOccursBeforeCopyOperationEntry()
    {
        NativePool<int> pool = new();
        Pooled<int> lease = pool.Rent(2, static writer => writer.Fill(default!));
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
        Pooled<int> lease = pool.Rent(0, static writer => writer.Fill(default!));
        Assert.Equal(0, lease.Read(static span => span.Length));
        pool.ReturnMemoryToNativeMemory();
        Assert.IsType<NativeAllocationReturnedException>(CaptureReturned(lease));
        lease.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void InvalidPoliciesAndLengthsDoNotCreateOwners()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativePool<int>(returnMemoryOnDispose: (NativeMemoryReturn)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeRegion(returnMemoryOnDispose: (NativeMemoryReturn)99));

        NativePool<int> pool = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(-1, static writer => writer.Fill(default!)));
        pool.Dispose();

        NativeRegion region = new();
        Exception? regionException = null;
        try
        {
            region.Lease<int>(-1, static writer => writer.Fill(default!));
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
        AssertUninitialized(LeaseDefaultRegion);
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
    public void NativeRegionExposesLeaseWithoutTheRemovedOperation()
    {
        MethodInfo[] methods = typeof(NativeRegion).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        Assert.Contains(methods, method => method.Name == "Lease" && method.IsGenericMethodDefinition);
        Assert.DoesNotContain(methods, method => method.Name == "Allocate");
        Assert.Contains(
            methods,
            method => method.Name == "LeaseFromMemory" && method.GetParameters().Length == 0);
    }

    [Fact]
    public void AllocationFailureLeavesEarlierPoolStateValid()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(preLease: 2);
        Pooled<int> existing = pool.Rent(2, static writer => writer.Fill(default!));
        NativeMemoryTestHooks.FailNextAllocation();
        NativeAllocationFailedException failure = Assert.Throws<NativeAllocationFailedException>(() => pool.Rent(100, static writer => writer.Fill(default!)));
        Assert.Equal(NativeOwnerLifecycle.Active, failure.CurrentLifecycle);
        existing[0] = 7;
        Assert.Equal(7, existing[0]);
        existing.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void OwnerStatisticsExposeLiveRequestsRetainedSegmentsAndTerminalRelease()
    {
        NativeMemoryTestHooks.Reset();
        using NativePool<int> pool = new(preLease: 4, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        NativeOwnerStatistics initial = pool.GetStatistics();
        Assert.Equal(NativeOwnerLifecycle.Active, initial.Lifecycle);
        Assert.True(initial.RetainedBytes >= 4 * sizeof(int));
        Assert.Equal(1, initial.SegmentCount);

        {
            scoped Pooled<int> lease = pool.LeaseScoped(4, static writer => writer.Fill(default!));
            NativeOwnerStatistics live = pool.GetStatistics();
            Assert.True(live.RequestedBytes >= 4 * sizeof(int));
            Assert.Equal(initial.RetainedBytes, live.RetainedBytes);
        }
        pool.RecycleScoped();
        NativeOwnerStatistics recycled = pool.GetStatistics();
        Assert.Equal(0, recycled.RequestedBytes);
        Assert.Equal(initial.RetainedBytes, recycled.RetainedBytes);
        Assert.True(recycled.FreshSegmentAllocationCount >= initial.FreshSegmentAllocationCount);

        pool.Dispose();
        NativeOwnerStatistics disposed = pool.GetStatistics();
        Assert.Equal(NativeOwnerLifecycle.Disposed, disposed.Lifecycle);
        Assert.Equal(0, disposed.RetainedBytes);
    }

    [Fact]
    public void ReLeaseAllocationFailureDoesNotPublishAPartialGeneration()
    {
        NativeMemoryTestHooks.Reset();
        NativePool<int> pool = new(preLease: 2);
        pool.ReturnMemoryToNativeMemory();
        NativeMemoryTestHooks.FailNextAllocation();

        NativeAllocationFailedException failure = Assert.Throws<NativeAllocationFailedException>(pool.LeaseFromMemory);
        Assert.Equal(NativeOwnerLifecycle.Returned, failure.CurrentLifecycle);
        Assert.Throws<NativeAllocationReturnedException>(() => pool.Rent(1, static writer => writer.Fill(default!)));

        pool.LeaseFromMemory();
        Pooled<int> lease = pool.Rent(1, static writer => writer.Fill(default!));
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
            () => new NativePool<int>(preLease: 2));
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
            _ = region.Lease<long>(32, static writer => writer.Fill(default!));
            throw new Xunit.Sdk.XunitException("Expected a native region growth allocation failure.");
        }
        catch (NativeAllocationFailedException exception)
        {
            regionGrowth = exception;
        }
        Assert.Equal(NativeOwnerLifecycle.Active, regionGrowth.CurrentLifecycle);
        region.Dispose();

        NativePool<int> pool = new(preLease: 2);
        NativeMemoryTestHooks.FailNextAllocation();
        NativeAllocationFailedException growth = Assert.Throws<NativeAllocationFailedException>(() => pool.Rent(4, static writer => writer.Fill(default!)));
        Assert.Equal(NativeOwnerLifecycle.Active, growth.CurrentLifecycle);

        pool.ReturnMemoryToNativeMemory();
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
        NativePool<object> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Pooled<object> lease = pool.Rent(
            4,
            static writer => writer.Fill(null!));
        object marker = new();
        lease[0] = marker;
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
        Assert.Same(marker, lease[0]);

        lease.Dispose();
        Pooled<object> reused = pool.Rent(
            2,
            static writer => writer.Fill(null!));
        Assert.Null(reused[0]);
        reused.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void ReturnPoliciesCoverPoolsAndRegionsWithAndWithoutInitialReservation()
    {
        NativeMemoryTestHooks.Reset();
        foreach (NativeMemoryReturn policy in Enum.GetValues<NativeMemoryReturn>())
        {
            foreach (int reservationUnits in new[] { 0, 4 })
            {
                NativeMemoryTestMetrics poolBefore = NativeMemoryTestHooks.Snapshot();
                NativePool<int> pool = new(reservationUnits, policy);
                Pooled<int> lease = pool.Rent(reservationUnits == 0 ? 1 : 3, static writer => writer.Fill(default!));
                lease.Dispose();
                pool.Dispose();
                NativeMemoryTestMetrics poolAfter = NativeMemoryTestHooks.Snapshot();
                Assert.True(poolAfter.AllocationCount > poolBefore.AllocationCount);
                if (policy == NativeMemoryReturn.ToNativeMemory)
                {
                    Assert.True(poolAfter.FreeCount > poolBefore.FreeCount);
                }
                else
                {
                    Assert.True(poolAfter.DetachedGenerationCount > poolBefore.DetachedGenerationCount);
                }

                NativeMemoryTestMetrics regionBefore = NativeMemoryTestHooks.Snapshot();
                NativeRegion region = new((nuint)(reservationUnits * sizeof(long)), policy);
                Local<long> local = region.Lease<long>(2, static writer => writer.Fill(default!));
                local[0] = 8;

                region.Dispose();
                NativeMemoryTestMetrics regionAfter = NativeMemoryTestHooks.Snapshot();
                Assert.True(regionAfter.AllocationCount > regionBefore.AllocationCount);
                if (policy == NativeMemoryReturn.ToNativeMemory)
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
        foreach (NativeMemoryReturn policy in Enum.GetValues<NativeMemoryReturn>())
        {
            NativePool<int> pool = new(returnMemoryOnDispose: policy);
            Pooled<int> stale = pool.Rent(1, static writer => writer.Fill(default!));
            if (policy == NativeMemoryReturn.ToNativeMemory)
            {
                pool.ReturnMemoryToNativeMemory();
            }
            else
            {
                pool.ReturnMemoryToGarbageCollector();
            }

            NativeAllocationReturnedException returned = CaptureReturned(stale);
            Assert.Equal(NativeOwnerLifecycle.Returned, returned.CurrentLifecycle);
            Assert.True(returned.CurrentGeneration > returned.Generation);
            Assert.DoesNotContain("0x", returned.Message, StringComparison.OrdinalIgnoreCase);
            if (policy == NativeMemoryReturn.ToNativeMemory)
            {
                Assert.Throws<NativeAllocationReturnedException>(pool.ReturnMemoryToNativeMemory);
            }
            else
            {
                Assert.Throws<NativeAllocationReturnedException>(pool.ReturnMemoryToGarbageCollector);
            }

            stale.Dispose();
            pool.LeaseFromMemory();
            pool.Dispose();
            NativeAllocationDisposedException disposed = Assert.Throws<NativeAllocationDisposedException>(() => pool.Rent(1, static writer => writer.Fill(default!)));
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
        Pooled<int> lease = pool.Rent(2, static writer => writer.Fill(default!));
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
        Pooled<int> pooled = pool.Rent(1, static writer => writer.Fill(default!));
        NativeMemoryTestMetrics before = NativeMemoryTestHooks.Snapshot();
        Assert.IsType<ArgumentNullException>(CaptureNullPooledCallback(pooled, read: false));
        Assert.IsType<ArgumentNullException>(CaptureNullPooledCallback(pooled, read: true));
        NativeMemoryTestMetrics afterPooled = NativeMemoryTestHooks.Snapshot();
        Assert.Equal(before.OutstandingNativeBytes, afterPooled.OutstandingNativeBytes);
        pooled.Dispose();
        pool.Dispose();

        NativeRegion region = new();
        Local<int> local = region.Lease<int>(1, static writer => writer.Fill(default!));
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
        NativePool<long> pool = new(preLease: 4);
        Pooled<long> lease = pool.Rent(4, static writer => writer.Fill(default!));
        long allocatedBytes = NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes;
        pool.ReturnMemoryToGarbageCollector();
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
        NativePool<int> pool = new(preLease: 4);
        Pooled<int> lease = pool.Rent(4, static writer => writer.Fill(default!));
        lease.Access(static span => span.Fill(1));
        pool.ReturnMemoryToGarbageCollector();
        lease.Dispose();
    }

    private static void RegionFallthrough()
    {
        using NativeRegion region = new(16, NativeMemoryReturn.ToNativeMemory);
        Local<int> local = region.Lease<int>(1, static writer => writer.Fill(default!));
        local[0] = 1;
    }

    private static int RegionReturn()
    {
        using NativeRegion region = new(16, NativeMemoryReturn.ToNativeMemory);
        Local<int> local = region.Lease<int>(1, static writer => writer.Fill(default!));
        local[0] = 2;
        return local[0];
    }

    private static void RegionGoto()
    {
        using NativeRegion region = new(16, NativeMemoryReturn.ToNativeMemory);
        Local<int> local = region.Lease<int>(1, static writer => writer.Fill(default!));
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
        using NativeRegion region = new(16, NativeMemoryReturn.ToNativeMemory);
        Local<int> local = region.Lease<int>(1, static writer => writer.Fill(default!));
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
        using NativeRegion region = new(16, NativeMemoryReturn.ToNativeMemory);
        Local<int> local = region.Lease<int>(1, static writer => writer.Fill(default!));
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

    private static NativeAllocationStateException CaptureRegionLeaseState(NativeRegion region)
    {
        try
        {
            _ = region.Lease<int>(1, static writer => writer.Fill(default!));
        }
        catch (NativeAllocationStateException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected a region lease lifecycle failure.");
    }

    private static NativeAllocationStateException CaptureRegionNativeMemoryReturnState(NativeRegion region)
    {
        try
        {
            region.ReturnMemoryToNativeMemory();
        }
        catch (NativeAllocationStateException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected a region native-return lifecycle failure.");
    }

    private static NativeAllocationStateException CaptureRegionGarbageReturnState(NativeRegion region)
    {
        try
        {
            region.ReturnMemoryToGarbageCollector();
        }
        catch (NativeAllocationStateException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected a region garbage-return lifecycle failure.");
    }

    private static NativeAllocationStateException CaptureRegionReLeaseState(NativeRegion region)
    {
        try
        {
            region.LeaseFromMemory();
        }
        catch (NativeAllocationStateException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected a returned region activation failure.");
    }

    private static NativeAllocationStateException CaptureRegionActiveActivation(NativeRegion region)
    {
        try
        {
            region.LeaseFromMemory();
        }
        catch (NativeAllocationStateException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected a duplicate region activation failure.");
    }

    private static NativeAllocationStateException CaptureRegionRecycleState(NativeRegion region)
    {
        try
        {
            region.RecycleScoped();
        }
        catch (NativeAllocationStateException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected an unleased region recycle failure.");
    }

    private static nuint CaptureRegionTrimState(NativeRegion region, int trimKind)
    {
        return trimKind switch
        {
            0 => region.TrimRetainedMemory(),
            1 => region.TrimRetainedMemoryByBytes(1),
            _ => region.TrimRetainedMemoryByLeaseSize<int>(1)
        };
    }

    private static NativeAllocationDisposedException CaptureRegionDisposedActivation(NativeRegion region)
    {
        try
        {
            region.LeaseFromMemory();
        }
        catch (NativeAllocationDisposedException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected a disposed region activation failure.");
    }

    private static NativeAllocationFailedException CaptureRegionActivationFailure(NativeRegion region)
    {
        try
        {
            region.LeaseFromMemory();
        }
        catch (NativeAllocationFailedException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected a delayed region activation allocation failure.");
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

    private static void LeaseDefaultRegion()
    {
        NativeRegion value = default;
        _ = value.Lease<int>(1, static writer => writer.Fill(default!));
    }

    private static void ReturnNativeDefaultRegion()
    {
        NativeRegion value = default;
        value.ReturnMemoryToNativeMemory();
    }

    private static void ReturnGarbageDefaultRegion()
    {
        NativeRegion value = default;
        value.ReturnMemoryToGarbageCollector();
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
