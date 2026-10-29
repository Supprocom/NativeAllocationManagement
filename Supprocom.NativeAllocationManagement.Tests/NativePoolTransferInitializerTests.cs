namespace Supprocom.NativeAllocationManagement.Tests;

// A transferable rent with an initializer reuses a returned transferable record
// where it is, on its thread's transfer list, as a filled rent does. A failed
// initializer puts the record back for the next rent, and a fold while the
// initializer runs completes the rent through the generation.
public sealed class NativePoolTransferInitializerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // The hooks that count records fold the cache, which would empty the
    // stash; these tests read the thread's cache lists without folding.
    [Fact]
    public void AnInitializerRentReusesAReturnedTransferInPlace()
    {
        using NativeConcurrentPool<int> pool = new();
        NativeOwnerKernel kernel = pool.KernelForTransfer;
        pool.RentTransferable(4, static writer => writer.Fill(1)).Dispose();
        Assert.Equal((0, 1, 1), kernel.ThreadCacheListsForTest());

        (int Records, int Transfers, int Stashed) during = default;
        using (NativeTransfer<int> transfer = pool.RentTransferable(4, writer =>
        {
            during = kernel.ThreadCacheListsForTest();
            writer.Fill(2);
        }))
        {
            // It stayed on the transfer list and never joined the leased set.
            Assert.Equal((0, 1, 0), during);
            Assert.Equal((0, 1, 0), kernel.ThreadCacheListsForTest());
            Assert.Equal(2, transfer.Read(static view => view[3]));
        }

        Assert.Equal((0, 1, 1), kernel.ThreadCacheListsForTest());
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(0, pool.CurrentInitializationCountForTest);
    }

    [Fact]
    public void AFailedInitializerReturnsTheRecordForTheNextRent()
    {
        using NativeConcurrentPool<int> pool = new();
        NativeOwnerKernel kernel = pool.KernelForTransfer;
        pool.RentTransferable(4, static writer => writer.Fill(1)).Dispose();

        Assert.Equal("initializer failed", Assert.Throws<InvalidOperationException>(
            () => pool.RentTransferable(4, static writer =>
            {
                writer.Write(1);
                throw new InvalidOperationException("initializer failed");
            })).Message);
        Assert.Equal((0, 1, 1), kernel.ThreadCacheListsForTest());
        Assert.Throws<InvalidOperationException>(
            () => pool.RentTransferable(4, static writer => writer.Write(1)));
        Assert.Equal((0, 1, 1), kernel.ThreadCacheListsForTest());

        using (NativeTransfer<int> transfer = pool.RentTransferable(4, static writer => writer.Fill(3)))
        {
            Assert.Equal((0, 1, 0), kernel.ThreadCacheListsForTest());
            Assert.Equal(3, transfer.Read(static view => view[3]));
        }

        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(0, pool.CurrentInitializationCountForTest);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
    }

    [Fact]
    public void AnInitializerRunningDuringAFoldCompletesThroughTheGeneration()
    {
        using NativeConcurrentPool<int> pool = new();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        int seen = 0;
        Exception? failure = null;
        Thread owner = new(() =>
        {
            try
            {
                pool.RentTransferable(4, static writer => writer.Fill(1)).Dispose();
                using NativeTransfer<int> transfer = pool.RentTransferable(4, writer =>
                {
                    entered.Set();
                    if (!release.Wait(Timeout))
                    {
                        throw new TimeoutException("The fold did not finish.");
                    }

                    writer.Fill(6);
                });
                seen = transfer.Read(static view => view[3]);
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
            {
                failure = exception;
            }
        });
        owner.Start();
        Assert.True(entered.Wait(Timeout));

        // Statistics fold every thread cache, this one's initializing record included.
        Assert.Equal(1, pool.CurrentInitializationCountForTest);
        _ = pool.GetStatistics();
        release.Set();
        Assert.True(owner.Join(Timeout));

        Assert.Null(failure);
        Assert.Equal(6, seen);
        Assert.Equal(0, pool.CurrentInitializationCountForTest);
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
        pool.ReleaseLeasesToNativeMemory();
    }
}
