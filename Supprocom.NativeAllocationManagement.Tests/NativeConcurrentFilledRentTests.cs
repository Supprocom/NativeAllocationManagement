namespace Supprocom.NativeAllocationManagement.Tests;

// Filled and uninitialized rents from a synchronized pool publish without an
// initializer callback; a small cached rent completes in one busy window.
public sealed class NativeConcurrentFilledRentTests
{
    [Fact]
    public void AFilledRentHoldsTheValueAndReturnsToThePool()
    {
        using NativeConcurrentPool<int> pool = new(preLease: 16);
        for (int round = 0; round < 64; round++)
        {
            using ConcurrentPooled<int> lease = pool.Rent(16, round);
            Assert.Equal(16, lease.Length);
            Assert.Equal(16 * round, lease.Read(static view => Sum(view.AsSpan())));
            Assert.Equal(64, pool.GetStatistics().RequestedBytes);
        }

        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(-1, 0));
    }

    [Fact]
    public void AnUninitializedRentIsWrittenThroughItsLease()
    {
        using NativeConcurrentPool<long> pool = new(preLease: 8);
        using (ConcurrentPooled<long> lease = pool.RentUninitialized(8))
        {
            lease.Access(static view => view.AsSpan().Fill(3));
            Assert.Equal(24, lease.Read(static view => view[0] * view.Length));
        }

        using ConcurrentPooled<long> again = pool.RentUninitialized(8);
        Assert.Equal(8, again.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.RentUninitialized(-1));
    }

    [Fact]
    public void ReferenceElementsStartAsTheValueOrTheirDefault()
    {
        using NativeConcurrentPool<string> pool = new(preLease: 4);
        using (ConcurrentPooled<string> filled = pool.Rent(4, "x"))
        {
            Assert.Equal("xxxx", filled.Read(static view => view[0] + view[1] + view[2] + view[3]));
        }

        using ConcurrentPooled<string> uninitialized = pool.RentUninitialized(4);
        Assert.True(uninitialized.Read(static view => view[0] is null && view[1] is null && view[2] is null && view[3] is null));
    }

    [Fact]
    public void ALargeFillAndAnInjectedFaultTakeTheInitializerPath()
    {
        using NativeConcurrentPool<int> pool = new(preLease: 4);
        using (ConcurrentPooled<int> large = pool.Rent(8192, 7))
        {
            Assert.Equal(7 * 8192, large.Read(static view => Sum(view.AsSpan())));
        }

        try
        {
            NativeMemoryTestHooks.FailNextAllocation();
            NativeMemoryTestHooks.Reset();
            using ConcurrentPooled<int> lease = pool.Rent(4, 2);
            Assert.Equal(8, lease.Read(static view => Sum(view.AsSpan())));
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void AFilledTransferableRentMovesToAnotherThread()
    {
        using NativeConcurrentPool<int> pool = new(preLease: 4);
        NativeTransfer<int>? transfer = pool.RentTransferable(4, 9);
        Assert.Equal(36, transfer.Value.Read(static view => view[0] + view[1] + view[2] + view[3]));
        NativeTransfer<int> moved = NativeTransfer<int>.Move(ref transfer);
        int read = 0;
        Thread consumer = new(() =>
        {
            read = moved.Read(static view => view[3]);
            moved.Dispose();
        });
        consumer.Start();
        consumer.Join();
        Assert.Equal(9, read);
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
    }

    private static int Sum(Span<int> values)
    {
        int sum = 0;
        foreach (ref readonly int value in values)
        {
            sum += value;
        }

        return sum;
    }
}
