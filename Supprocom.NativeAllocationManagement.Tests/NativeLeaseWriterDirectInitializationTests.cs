namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeLeaseWriterDirectInitializationTests
{
    [Fact]
    public void DirectCallbackPublishesOnlyAfterTheCompleteSpanReturns()
    {
        using NativePool<int> pool = new();
        using Pooled<int> lease = pool.Rent(
            8,
            writer =>
            {
                writer.Write(31);
                writer.InitializeRemaining(
                    static values =>
                    {
                        for (int index = 0; index < values.Length; index++)
                        {
                            values[index] = index + 32;
                        }
                    });
            });

        lease.Read(
            static view =>
            {
                Assert.Equal(8, view.Length);
                for (int index = 0; index < view.Length; index++)
                {
                    Assert.Equal(index + 31, view[index]);
                }

                return 0;
            });
    }

    [Fact]
    public void FailedDirectCallbackAbortsTheReservation()
    {
        using NativeArena arena = new(
            preAllocateBytes: 16 * sizeof(int),
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(
            () => arena.ScratchTransferable<int>(
                16,
                writer => writer.InitializeRemaining(
                    static values =>
                    {
                        values[0] = 41;
                        throw new InvalidOperationException("failure");
                    })));

        Assert.Equal("failure", exception.Message);
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, arena.CurrentConcurrentReservationCountForTest);

        using NativeTransfer<int> replacement =
            arena.ScratchTransferable<int>(
                16,
                static writer => writer.Fill(43));
        Assert.Equal(43, replacement.Read(static view => view[0]));
    }

    [Fact]
    public void CanceledDirectCallbackAbortsTheReservation()
    {
        using NativeArena arena = new(
            preAllocateBytes: 16 * sizeof(int),
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => arena.ScratchTransferable<int>(
                16,
                writer => writer.InitializeRemaining(
                    values =>
                    {
                        values[0] = 47;
                        cancellation.Token.ThrowIfCancellationRequested();
                    })));

        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, arena.CurrentConcurrentReservationCountForTest);
    }

    [Fact]
    public void EmptyDirectCallbackPublishesAnEmptyTransfer()
    {
        bool invoked = false;
        using NativeArena arena = new();
        using NativeTransfer<int> transfer =
            arena.ScratchTransferable<int>(
                0,
                writer => writer.InitializeRemaining(
                    values =>
                    {
                        invoked = true;
                        Assert.Equal(0, values.Length);
                    }));

        Assert.True(invoked);
        Assert.Equal(0, transfer.Length);
    }

    [Fact]
    public void DirectCallbackRejectsStorageWithManagedReferences()
    {
        using NativePool<string> pool = new();

        Assert.Throws<NotSupportedException>(
            () => pool.Rent(
                2,
                writer => writer.InitializeRemaining(
                    static values => values.Fill("value"))));
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, pool.CurrentReferenceRootCountForTest);
    }
}
