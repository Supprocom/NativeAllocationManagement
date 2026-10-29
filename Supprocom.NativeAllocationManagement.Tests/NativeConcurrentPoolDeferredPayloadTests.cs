namespace Supprocom.NativeAllocationManagement.Tests;

// A cached return leaves its payload in the thread cache until the owner's
// next rent takes its place or a fold reads the cache; the pool's statistics
// must still be exact.
public sealed class NativeConcurrentPoolDeferredPayloadTests
{
    [Fact]
    public void RentsOfOtherSizesReplaceTheReturnedLeaseExactly()
    {
        using NativeConcurrentPool<int> pool = new();
        pool.Rent(16, 1).Dispose();
        pool.Rent(32, 2).Dispose();
        pool.Rent(4, 3).Dispose();
        NativeOwnerStatistics statistics = pool.GetStatistics();
        Assert.Equal(0, statistics.InitializedPayloadBytes);
        Assert.Equal(128, statistics.PeakInitializedPayloadBytes);
    }

    [Fact]
    public void TwoReturnsWithoutARentBetweenLeaveExactStatistics()
    {
        using NativeConcurrentPool<int> pool = new();
        ConcurrentPooled<int> first = pool.Rent(16, 1);
        ConcurrentPooled<int> second = pool.Rent(8, 2);
        first.Dispose();
        second.Dispose();
        NativeOwnerStatistics statistics = pool.GetStatistics();
        Assert.Equal(0, statistics.InitializedPayloadBytes);
        Assert.Equal(96, statistics.PeakInitializedPayloadBytes);
    }

    [Fact]
    public void AnInitializerRentAfterAReturnOfAnotherSizeCountsItself()
    {
        using NativeConcurrentPool<int> pool = new();
        pool.Rent(64, 1).Dispose();
        using (ConcurrentPooled<int> lease = pool.Rent(4, static writer => writer.Fill(9)))
        {
            Assert.Equal(36, lease.Read(static view => view[0] + view[1] + view[2] + view[3]));
        }

        NativeOwnerStatistics statistics = pool.GetStatistics();
        Assert.Equal(0, statistics.InitializedPayloadBytes);
        Assert.Equal(256, statistics.PeakInitializedPayloadBytes);
    }

    [Fact]
    public void AOneShotProcessAfterAReturnTakesItsPeakFromExactPayload()
    {
        using NativeConcurrentPool<int> pool = new();
        pool.Rent(64, 1).Dispose();
        Assert.Equal(5, pool.Process(4, 5, 0, static (span, _) => span[3]));
        NativeOwnerStatistics statistics = pool.GetStatistics();
        Assert.Equal(0, statistics.InitializedPayloadBytes);
        Assert.Equal(256, statistics.PeakInitializedPayloadBytes);
    }

    [Fact]
    public void ALockedRentAfterACachedReturnSeesTheReturnedPayloadGone()
    {
        using NativeConcurrentPool<int> pool = new();
        pool.Rent(16, 1).Dispose();
        // A scoped lease takes the locked path, which records this thread's
        // cached peak against the generation's payload first.
        using (ConcurrentPooled<int> scoped = pool.LeaseScoped(2, static writer => writer.Fill(3)))
        {
            NativeOwnerStatistics during = pool.GetStatistics();
            Assert.Equal(8, during.InitializedPayloadBytes);
            Assert.Equal(64, during.PeakInitializedPayloadBytes);
        }
    }
}
