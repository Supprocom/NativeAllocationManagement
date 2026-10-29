namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolInitializerGrowthTests
{
    [Fact]
    public void SuccessfulInitializerPublishesInTheCurrentMetadataBank()
    {
        using NativePool<int> pool = new(2, NativeMemoryReturn.Free);
        using (Pooled<int> outer = pool.Rent(2, writer =>
        {
            using Pooled<int> first = pool.Rent(2, 11);
            using Pooled<int> second = pool.Rent(2, 13);
            using Pooled<int> third = pool.Rent(2, 17);
            using Pooled<int> fourth = pool.Rent(2, 19);
            Assert.True(pool.CurrentBankCapacitiesForTest.Slabs > 4);
            Assert.Equal(1, pool.CurrentInitializationCountForTest);
            Assert.Equal(5, pool.CurrentAllocationRecordCountForTest);
            Assert.Equal(32, pool.GetStatistics().RequestedBytes);
            writer.Fill(7);
        }))
        {
            Assert.Equal(14, outer.Read(static values => values[0] + values[1]));
            Assert.Equal(0, pool.CurrentInitializationCountForTest);
            Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
            Assert.Equal(8, pool.GetStatistics().RequestedBytes);
        }
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        Assert.Equal(32, pool.GetStatistics().PeakInitializedPayloadBytes);
        using Pooled<int> reused = pool.Rent(2, static writer => writer.Fill(23));
        Assert.Equal(46, reused.Read(static values => values[0] + values[1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteAndThrowingInitializersRollBackTheCurrentBank(bool throws)
    {
        using NativePool<int> pool = new(2, NativeMemoryReturn.Free);
        OperationCanceledException expected = new("nested initialization failure");
        Exception? actual = Record.Exception(() =>
        {
            using Pooled<int> unpublished = pool.Rent(2, writer =>
            {
                using Pooled<int> first = pool.Rent(2, 11);
                using Pooled<int> second = pool.Rent(2, 13);
                using Pooled<int> third = pool.Rent(2, 17);
                using Pooled<int> fourth = pool.Rent(2, 19);
                Assert.True(pool.CurrentBankCapacitiesForTest.Slabs > 4);
                writer.Write(7);
                if (throws) throw expected;
            });
        });
        if (throws) Assert.Same(expected, actual);
        else Assert.IsType<InvalidOperationException>(actual);
        Assert.Equal(0, pool.CurrentInitializationCountForTest);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        Assert.Equal(32, pool.GetStatistics().PeakInitializedPayloadBytes);
        using Pooled<int> reused = pool.Rent(2, static writer => writer.Fill(23));
        Assert.Equal(46, reused.Read(static values => values[0] + values[1]));
    }
}
