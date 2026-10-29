namespace Supprocom.NativeAllocationManagement.Tests;

// A returned lease stays in the pool's totals until the next rent takes its
// place or something reads them; every reading must still be exact.
public sealed class NativePoolDeferredReturnTests
{
    [Fact]
    public void ARentOfAnotherSizeReplacesTheReturnedLeaseExactly()
    {
        using NativePool<int> pool = new();
        pool.Rent(16, 1).Dispose();
        using (Pooled<int> larger = pool.Rent(32, 2))
        {
            NativeOwnerStatistics during = pool.GetStatistics();
            Assert.Equal(128, during.InitializedPayloadBytes);
            Assert.Equal(128, during.PeakInitializedPayloadBytes);
            Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
        }

        using (Pooled<int> smaller = pool.Rent(4, 3))
        {
            Assert.Equal(16, pool.GetStatistics().InitializedPayloadBytes);
            Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
        }

        NativeOwnerStatistics after = pool.GetStatistics();
        Assert.Equal(0, after.InitializedPayloadBytes);
        Assert.Equal(128, after.PeakInitializedPayloadBytes);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public void TwoReturnsWithoutARentBetweenLeaveExactTotals()
    {
        using NativePool<int> pool = new();
        Pooled<int> first = pool.Rent(16, 1);
        Pooled<int> second = pool.Rent(8, 2);
        first.Dispose();
        second.Dispose();
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        NativeOwnerStatistics statistics = pool.GetStatistics();
        Assert.Equal(0, statistics.InitializedPayloadBytes);
        Assert.Equal(96, statistics.PeakInitializedPayloadBytes);
        Assert.Equal(0, pool.CaptureDiagnosticSnapshot().InitializedPayloadBytes);
    }

    [Fact]
    public void AOneShotProcessAfterAReturnTakesItsPeakFromExactTotals()
    {
        using NativePool<int> pool = new();
        pool.Rent(64, 1).Dispose();
        int last = pool.Process(4, 5, 0, static (values, _) => values[3]);
        Assert.Equal(5, last);
        NativeOwnerStatistics statistics = pool.GetStatistics();
        Assert.Equal(0, statistics.InitializedPayloadBytes);
        Assert.Equal(256, statistics.PeakInitializedPayloadBytes);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public void AnInitializerRentAfterAReturnOfAnotherSizeCountsItself()
    {
        using NativePool<int> pool = new();
        pool.Rent(64, 1).Dispose();
        using (Pooled<int> lease = pool.Rent(4, static writer => writer.Fill(9)))
        {
            Assert.Equal(16, pool.GetStatistics().InitializedPayloadBytes);
            Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
            Assert.Equal(36, lease.Read(static values => values[0] + values[1] + values[2] + values[3]));
        }

        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(256, pool.GetStatistics().PeakInitializedPayloadBytes);
    }

    [Fact]
    public void AReturnInsideAnInitializerLeavesBeforeThePeakIsTaken()
    {
        using NativePool<int> pool = new();
        using (Pooled<int> outer = pool.Rent(4, writer =>
        {
            pool.Rent(64, 1).Dispose();
            writer.Fill(2);
        }))
        {
            NativeOwnerStatistics statistics = pool.GetStatistics();
            Assert.Equal(16, statistics.InitializedPayloadBytes);
            Assert.Equal(256, statistics.PeakInitializedPayloadBytes);
            Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
        }
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "Retire transfers cleanup to ReleaseRetiredStorage on the coordinator thread.")]
    public void RetireAndDisposeRightAfterAReturnSeeNoLiveLease()
    {
        NativePool<int> retired = new();
        retired.Rent(16, 1).Dispose();
        retired.Retire();
        retired.ReleaseRetiredStorage();

        NativePool<int> disposed = new();
        disposed.Rent(16, 1).Dispose();
        disposed.Dispose();
        Assert.Equal(NativeOwnerLifecycle.Disposed, disposed.CurrentLifecycle);
    }
}
