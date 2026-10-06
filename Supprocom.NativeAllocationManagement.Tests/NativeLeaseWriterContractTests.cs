namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeLeaseWriterContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void DirectWriterKeepsLogicalLengthAndSequentialBounds(int length)
    {
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using Pooled<int> lease = pool.Rent(length, writer =>
        {
            Assert.Equal(length, writer.Length);
            Assert.Equal(length, writer.Remaining);
            NativeSequentialLeaseWriter<int> sequential = writer.BeginSequentialRange(0, length);
            for (int index = 0; index < length; index++) sequential.Write(index + 17);
            Assert.Equal(length, writer.Remaining);
            sequential.Complete();
            Assert.Equal(0, writer.Remaining);
            Assert.Equal(length, writer.ReadInitializedSpan(0, length).Length);
            for (int index = 0; index < length; index++) writer.WriteAt(index, writer.ReadInitialized(index) + 1);
            writer.RewriteInitializedSpan(0, length).Fill(42);
            writer.Fill(0, length, 43);
        });
        Assert.Equal(length, lease.Length);
        lease.Read(view =>
        {
            for (int index = 0; index < view.Length; index++) Assert.Equal(43, view[index]);
            return 0;
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void RootedWriterKeepsLogicalLengthWithoutDirectSpanAccess(int length)
    {
        using NativeConcurrentPool<string> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using ConcurrentPooled<string> lease = pool.Rent(length, writer =>
        {
            Assert.Equal(length, writer.Length);
            Assert.Equal(length, writer.Remaining);
            NativeSequentialLeaseWriter<string> sequential = writer.BeginSequentialRange(0, length);
            for (int index = 0; index < length; index++) sequential.Write("initial");
            sequential.Complete();
            Assert.Equal(0, writer.Remaining);
            for (int index = 0; index < length; index++)
            {
                Assert.Equal("initial", writer.ReadInitialized(index));
                writer.WriteAt(index, "replacement");
            }
            writer.Fill(0, length, "final");
            try
            {
                _ = writer.ReadInitializedSpan(0, length);
                Assert.Fail("Rooted storage must not publish a direct span.");
            }
            catch (NotSupportedException)
            {
                Assert.Equal(0, writer.Remaining);
            }
        });
        lease.Read(view =>
        {
            Assert.Equal(length, view.Length);
            for (int index = 0; index < view.Length; index++) Assert.Equal("final", view[index]);
            return 0;
        });
        Assert.Equal(length, pool.CurrentReferenceRootCountForTest);
    }

    [Fact]
    public void DefaultWriterRetainsZeroLogicalLengthForDirectAndRootedTypes()
    {
        NativeLeaseWriter<int> direct = default;
        NativeLeaseWriter<string> rooted = default;
        Assert.Equal(0, direct.Length);
        Assert.Equal(0, rooted.Length);
    }

    [Fact]
    public void DirectSpanCallbackFailureDoesNotPublishItsPartiallyWrittenPrefix()
    {
        NativeMemoryBudget budget = new(512);
        using NativeRegion region = new(budget, 16, NativeMemoryReturn.ToNativeMemory);
        try
        {
            _ = region.Lease<int>(4, static writer =>
            {
                writer.Write(17);
                writer.InitializeRemaining(static values =>
                {
                    values[0] = 99;
                    throw new InvalidOperationException("partial direct callback");
                });
            });
            Assert.Fail("A failed callback must not publish a lease.");
        }
        catch (InvalidOperationException failure)
        {
            Assert.Equal("partial direct callback", failure.Message);
        }
        Assert.Equal(0, region.GetStatistics().RequestedBytes);
        Local<int> complete = region.Lease<int>(4, static writer => writer.Fill(42));
        Assert.Equal(42, complete.Read(static view => view[3]));
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
    }
}
