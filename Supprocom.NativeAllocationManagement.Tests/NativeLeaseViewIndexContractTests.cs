namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeLeaseViewIndexContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void DirectPoolViewPreservesLogicalBoundsAndValues(int length)
    {
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using Pooled<int> lease = pool.Rent(length, static writer => writer.Fill(17));
        lease.Read(view =>
        {
            VerifyBounds(view, length, 23);
            for (int index = 0; index < length; index++)
            {
                Assert.Equal(17, view[index]);
                view[index] = index + 23;
            }

            for (int index = 0; index < length; index++) Assert.Equal(index + 23, view[index]);
            VerifyBounds(view, length, 99);
            for (int index = 0; index < length; index++) Assert.Equal(index + 23, view[index]);
            return 0;
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void DirectRegionViewPreservesLogicalBoundsAndValues(int length)
    {
        using NativeRegion region = new(64, NativeMemoryReturn.ToNativeMemory);
        Local<long> lease = region.Lease<long>(length, static writer => writer.Fill(71));
        lease.Read(view =>
        {
            VerifyBounds(view, length, 73L);
            for (int index = 0; index < length; index++)
            {
                Assert.Equal(71L, view[index]);
                view[index] = index + 73L;
            }

            for (int index = 0; index < length; index++) Assert.Equal(index + 73L, view[index]);
            VerifyBounds(view, length, 99L);
            return 0;
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void ExplicitRootedPrefixDoesNotExposeOrModifyTheBackingSuffix(int length)
    {
        using NativeConcurrentPool<string> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using ConcurrentPooled<string> lease = pool.Rent(4, static writer => writer.Fill("original"));
        Assert.Equal(4, pool.CurrentReferenceRootCountForTest);
        int processed = lease.Process(length, length, static (view, expectedLength) =>
        {
            Assert.Equal(expectedLength, view.Capacity);
            VerifyBounds(view, expectedLength, "out of range");
            for (int index = 0; index < view.Length; index++)
            {
                Assert.Equal("original", view[index]);
                view[index] = "replacement";
            }

            VerifyBounds(view, expectedLength, "out of range");
            return view.Length;
        });
        Assert.Equal(length, processed);
        Assert.Equal(4, pool.CurrentReferenceRootCountForTest);
        lease.Read(view =>
        {
            VerifyBounds(view, 4, "out of range");
            for (int index = 0; index < 4; index++)
                Assert.Equal(index < length ? "replacement" : "original", view[index]);
            return 0;
        });
        lease.Clear();
        Assert.Equal(0, pool.CurrentReferenceRootCountForTest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void ExplicitDirectPrefixDoesNotExposeOrModifyTheBackingSuffix(int length)
    {
        using NativeConcurrentPool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using ConcurrentPooled<int> lease = pool.Rent(4, static writer => writer.Fill(17));
        _ = lease.Process(length, length, static (view, expectedLength) =>
        {
            Assert.Equal(expectedLength, view.Capacity);
            VerifyBounds(view, expectedLength, 99);
            for (int index = 0; index < view.Length; index++) view[index] = 23;
            VerifyBounds(view, expectedLength, 99);
            return 0;
        });
        lease.Read(view =>
        {
            for (int index = 0; index < 4; index++) Assert.Equal(index < length ? 23 : 17, view[index]);
            return 0;
        });
    }

    [Fact]
    public void DirectMultifieldValuesRoundTripWithoutChangingTheirRepresentation()
    {
        using NativePool<decimal> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using Pooled<decimal> lease = pool.Rent(2, static writer => writer.Fill(17.25m));
        lease.Read(static view =>
        {
            VerifyBounds(view, 2, 99.75m);
            Assert.Equal(17.25m, view[0]);
            Assert.Equal(17.25m, view[1]);
            view[0] = decimal.MaxValue;
            view[1] = decimal.MinValue;
            Assert.Equal(decimal.MaxValue, view[0]);
            Assert.Equal(decimal.MinValue, view[1]);
            return 0;
        });
    }

    [Fact]
    public void DefaultDirectAndReferenceViewsRefuseEveryIndex()
    {
        NativeLeaseView<int> direct = default;
        NativeLeaseView<string> rooted = default;
        VerifyBounds(direct, 0, 17);
        VerifyBounds(rooted, 0, "not stored");
    }

    [Fact]
    public void RawReferenceViewRetainsSpanTypeGuardAndBoundsExceptionPrecedence()
    {
        NativeLeaseView<string> view = new(IntPtr.Zero, 1);
        VerifyBounds(view, 1, "not stored");
        try
        {
            _ = view[0];
            Assert.Fail("Raw reference storage must not become a direct span.");
        }
        catch (ArgumentException failure)
        {
            Assert.IsNotType<ArgumentOutOfRangeException>(failure);
        }

        try
        {
            view[0] = "not stored";
            Assert.Fail("Raw reference storage must not bypass the span type guard.");
        }
        catch (ArgumentException failure)
        {
            Assert.IsNotType<ArgumentOutOfRangeException>(failure);
        }
    }

    private static void VerifyBounds<T>(scoped NativeLeaseView<T> view, int length, T value)
    {
        Assert.Equal(length, view.Length);
        foreach (int index in new[] { int.MinValue, -1, length, int.MaxValue })
        {
            ArgumentOutOfRangeException read = ReadFailure(view, index);
            ArgumentOutOfRangeException write = WriteFailure(view, index, value);
            Assert.Equal("index", read.ParamName);
            Assert.Equal(index, read.ActualValue);
            Assert.Equal("index", write.ParamName);
            Assert.Equal(index, write.ActualValue);
            Assert.Equal(read.Message, write.Message);
            if (index >= 0) Assert.StartsWith("The index is outside the logical lease range.", read.Message, StringComparison.Ordinal);
        }
    }

    private static ArgumentOutOfRangeException ReadFailure<T>(scoped NativeLeaseView<T> view, int index)
    {
        try
        {
            _ = view[index];
        }
        catch (ArgumentOutOfRangeException failure)
        {
            return failure;
        }

        Assert.Fail("Reading outside the logical range must fail.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }

    private static ArgumentOutOfRangeException WriteFailure<T>(scoped NativeLeaseView<T> view, int index, T value)
    {
        try
        {
            view[index] = value;
        }
        catch (ArgumentOutOfRangeException failure)
        {
            return failure;
        }

        Assert.Fail("Writing outside the logical range must fail.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }
}
