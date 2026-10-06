namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeLeaseWriterRootedLengthTests
{
    [Fact]
    public void RootedWriterLengthRemainsDeclaredAcrossNestedIndependentInitialization()
    {
        using NativeConcurrentPool<string> outer = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentPool<string> inner = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using (ConcurrentPooled<string> lease = outer.Rent(7, writer =>
        {
            writer.Write("first");
            Assert.Equal(7, writer.Length);
            Assert.Equal(6, writer.Remaining);
            using ConcurrentPooled<string> nested = inner.Rent(3, static other => other.Fill("nested"));
            Assert.Equal(3, nested.Read(static view => view.Length));
            Assert.Equal(7, writer.Length);
            writer.Fill("remaining");
            Assert.Equal(0, writer.Remaining);
        }))
        {
            Assert.Equal("first", lease.Read(static view => view[0]));
            Assert.Equal("remaining", lease.Read(static view => view[6]));
            Assert.Equal(7, outer.CurrentReferenceRootCountForTest);
        }
        Assert.Equal(0, outer.CurrentReferenceRootCountForTest);
        Assert.Equal(0, inner.CurrentReferenceRootCountForTest);
    }
}
