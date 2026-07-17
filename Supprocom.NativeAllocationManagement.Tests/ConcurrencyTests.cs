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
            release.Set();
            Assert.Null(await worker);
            Pooled<int> reused = pool.Rent(2);
            Assert.Equal(0, reused[0]);
            reused.Dispose();
            pool.Dispose();
        }
        finally
        {
            release.Set();
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
