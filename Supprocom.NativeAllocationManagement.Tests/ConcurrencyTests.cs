using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task ReturnFailsAfterAnOperationTokenWinsAndLeavesGenerationUsable()
    {
        NativeConcurrentPool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        NativeMemoryTestHooks.SetOperationEntered(operation =>
        {
            if (string.Equals(operation, nameof(ConcurrentPooled<int>.Access), StringComparison.Ordinal))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });

        try
        {
            Task worker = Task.Run(() =>
            {
                ConcurrentPooled<int> lease = pool.Rent(1, static writer => writer.Fill(default!));
                lease.Access(static span => span[0] = 42);
                lease.Dispose();
            });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            NativeAllocationInUseException exception = Assert.Throws<NativeAllocationInUseException>(pool.ReturnMemoryToNativeMemory);
            Assert.Contains("No lease was invalidated", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(NativeOwnerLifecycle.Active, exception.CurrentLifecycle);
            release.Set();
            await worker.ConfigureAwait(true);

            ConcurrentPooled<int> usable = pool.Rent(1, static writer => writer.Fill(default!));
            Assert.Equal(0, usable.Read(__namIndexedView => __namIndexedView[0]));
            usable.Dispose();
            pool.Dispose();
        }
        finally
        {
            release.Set();
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public async Task GarbageCollectorReturnDetachesWhileAnEnteredBorrowKeepsTheGenerationAlive()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        NativeMemoryTestHooks.Reset();

        NativeConcurrentPool<int> pool = new(
            preLease: 1,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        NativeMemoryTestHooks.SetOperationEntered(operation =>
        {
            if (string.Equals(operation, nameof(ConcurrentPooled<int>.Access), StringComparison.Ordinal))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });

        try
        {
            Task worker = Task.Run(() =>
            {
                ConcurrentPooled<int> lease = pool.Rent(1, static writer => writer.Fill(default!));
                lease.Access(static span => span[0] = 42);
                lease.Dispose();
            });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            NativeMemoryTestMetrics beforeReturn = NativeMemoryTestHooks.Snapshot();
            pool.ReturnMemoryToGarbageCollector();
            NativeMemoryTestMetrics detached = NativeMemoryTestHooks.Snapshot();
            Assert.Equal(beforeReturn.DetachedGenerationCount + 1, detached.DetachedGenerationCount);
            Assert.Equal(beforeReturn.OutstandingNativeBytes, detached.DetachedNativeBytes);
            Assert.Equal(NativeOwnerLifecycle.Returned, pool.CurrentLifecycle);

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            NativeMemoryTestMetrics whileBorrowed = NativeMemoryTestHooks.Snapshot();
            Assert.Equal(detached.DetachedNativeBytes, whileBorrowed.DetachedNativeBytes);
            Assert.Equal(detached.FreeCount, whileBorrowed.FreeCount);

            NativeMemoryTestHooks.SetOperationEntered(null);
            pool.LeaseFromMemory();
            ConcurrentPooled<int> current = pool.Rent(1, static writer => writer.Fill(default!));
            current.Access(__namIndexedView => __namIndexedView[0] = 7);
            Assert.Equal(7, current.Read(__namIndexedView => __namIndexedView[0]));
            current.Dispose();

            release.Set();
            await worker.ConfigureAwait(true);

            for (int attempt = 0;
                attempt < 3 && NativeMemoryTestHooks.Snapshot().DetachedNativeBytes != 0;
                attempt++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }

            Assert.Equal(0, NativeMemoryTestHooks.Snapshot().DetachedNativeBytes);
            ConcurrentPooled<int> verified = pool.Rent(1, static writer => writer.Fill(default!));
            Assert.Equal(0, verified.Read(__namIndexedView => __namIndexedView[0]));
            verified.Dispose();
            pool.Dispose();
        }
        finally
        {
            release.Set();
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void ReturnWinsBeforeOperationEntryAndOperationFailsBeforeAddressCalculation()
    {
        NativeConcurrentPool<int> pool = new();
        ConcurrentPooled<int> lease = pool.Rent(1, static writer => writer.Fill(default!));
        pool.ReturnMemoryToNativeMemory();
        NativeAllocationReturnedException exception = CaptureReturned(lease);
        Assert.Contains("returned", exception.Message, StringComparison.OrdinalIgnoreCase);
        lease.Dispose();
        pool.Dispose();
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The test intentionally captures arbitrary callback or worker failures for lifecycle assertions.")]
    public async Task IndividualLeaseDisposalDoesNotReuseAStagingSlabDuringActiveAccess()
    {
        NativeConcurrentPool<int> pool = new();
        using ManualResetEventSlim entered = new();
        NativeAllocationInUseException? returnException = null;
        NativeMemoryTestHooks.SetOperationEnteredWithAllocation((operation, kernel, generation, allocationId) =>
        {
            if (string.Equals(operation, nameof(ConcurrentPooled<int>.Access), StringComparison.Ordinal))
            {
                entered.Set();
                try
                {
                    kernel.ReturnLease(generation, allocationId);
                }
                catch (NativeAllocationInUseException exception)
                {
                    returnException = exception;
                }
            }
        });

        try
        {
            Task<Exception?> worker = Task.Run(() =>
            {
                ConcurrentPooled<int> lease = pool.Rent(2, static writer => writer.Fill(default!));
                try
                {
                    lease.Access(static span => span[0] = 1);
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
                finally
                {
                    lease.Dispose();
                }
            });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Null(await worker.ConfigureAwait(true));
            Assert.NotNull(returnException);
            ConcurrentPooled<int> reused = pool.Rent(2, static writer => writer.Fill(default!));
            Assert.Equal(0, reused.Read(__namIndexedView => __namIndexedView[0]));
            reused.Dispose();
            pool.Dispose();
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void ConcurrentRentsProduceIndependentValidLeases()
    {
        NativeConcurrentPool<int> pool = new();
        Parallel.For(0, 16, index =>
        {
            ConcurrentPooled<int> lease = pool.Rent(1, static writer => writer.Fill(default!));
            lease.Access(__namIndexedView => __namIndexedView[0] = index);
            Assert.Equal(index, lease.Read(__namIndexedView => __namIndexedView[0]));
            lease.Dispose();
        });
        pool.Dispose();
    }

    private static void Read(ConcurrentPooled<int> lease)
    {
        _ = lease.Read(__namIndexedView => __namIndexedView[0]);
    }

    private static NativeAllocationReturnedException CaptureReturned(ConcurrentPooled<int> lease)
    {
        try
        {
            Read(lease);
        }
        catch (NativeAllocationReturnedException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("Expected NativeAllocationReturnedException.");
    }
}
