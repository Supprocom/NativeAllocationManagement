using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task ReturnFailsAfterAnOperationTokenWinsAndLeavesGenerationUsable()
    {
        NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
        ManualResetEventSlim entered = new();
        ManualResetEventSlim release = new();
        NativeMemoryTestHooks.SetOperationEntered(operation =>
        {
            if (operation == nameof(Pooled<int>.Access))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });

        try
        {
            Task worker = Task.Run(() =>
            {
                Pooled<int> lease = pool.Rent(1);
                lease.Access(static span => span[0] = 42);
                lease.Dispose();
            });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            NativeAllocationInUseException exception = Assert.Throws<NativeAllocationInUseException>(pool.ReturnToNativeMemory);
            Assert.Contains("No lease was invalidated", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(NativeOwnerLifecycle.Active, exception.CurrentLifecycle);
            release.Set();
            await worker;

            Pooled<int> usable = pool.Rent(1);
            Assert.Equal(0, usable[0]);
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

        NativePool<int> pool = new(
            initialCapacity: 1,
            returnOnDispose: NativeReturn.ToNativeMemory);
        ManualResetEventSlim entered = new();
        ManualResetEventSlim release = new();
        NativeMemoryTestHooks.SetOperationEntered(operation =>
        {
            if (operation == nameof(Pooled<int>.Access))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });

        try
        {
            Task worker = Task.Run(() =>
            {
                Pooled<int> lease = pool.Rent(1);
                lease.Access(static span => span[0] = 42);
                lease.Dispose();
            });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            NativeMemoryTestMetrics beforeReturn = NativeMemoryTestHooks.Snapshot();
            pool.ReturnToGarbageCollector();
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
            Pooled<int> current = pool.Rent(1);
            current[0] = 7;
            Assert.Equal(7, current[0]);
            current.Dispose();

            release.Set();
            await worker;

            for (int attempt = 0;
                attempt < 3 && NativeMemoryTestHooks.Snapshot().DetachedNativeBytes != 0;
                attempt++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }

            Assert.Equal(0, NativeMemoryTestHooks.Snapshot().DetachedNativeBytes);
            Pooled<int> verified = pool.Rent(1);
            Assert.Equal(0, verified[0]);
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
        NativePool<int> pool = new();
        Pooled<int> lease = pool.Rent(1);
        pool.ReturnToNativeMemory();
        NativeAllocationReturnedException exception = CaptureReturned(lease);
        Assert.Contains("returned", exception.Message, StringComparison.OrdinalIgnoreCase);
        lease.Dispose();
        pool.Dispose();
    }

    [Fact]
    public async Task IndividualLeaseDisposalDoesNotReuseAStagingSlabDuringActiveAccess()
    {
        NativePool<int> pool = new();
        ManualResetEventSlim entered = new();
        NativeAllocationInUseException? returnException = null;
        NativeMemoryTestHooks.SetOperationEnteredWithAllocation((operation, kernel, generation, allocationId) =>
        {
            if (operation == nameof(Pooled<int>.Access))
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
                Pooled<int> lease = pool.Rent(2);
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
            Assert.Null(await worker);
            Assert.NotNull(returnException);
            Pooled<int> reused = pool.Rent(2);
            Assert.Equal(0, reused[0]);
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
        NativePool<int> pool = new();
        Parallel.For(0, 16, index =>
        {
            Pooled<int> lease = pool.Rent(1);
            lease[0] = index;
            Assert.Equal(index, lease[0]);
            lease.Dispose();
        });
        pool.Dispose();
    }

    private static void Read(Pooled<int> lease)
    {
        _ = lease[0];
    }

    private static NativeAllocationReturnedException CaptureReturned(Pooled<int> lease)
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
