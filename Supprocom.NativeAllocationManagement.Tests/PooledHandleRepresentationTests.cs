using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class PooledHandleRepresentationTests
{
    [Fact]
    public void ClosedHandlesKeepAuthorityWithoutAnUnusedPayloadPointer()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(32, Unsafe.SizeOf<Pooled<byte>>());
        Assert.Equal(32, Unsafe.SizeOf<Pooled<int>>());
        Assert.Equal(16, Unsafe.SizeOf<Span<byte>>());
        Assert.Equal(16, Unsafe.SizeOf<Span<int>>());
        string[] fields = typeof(Pooled<int>).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(static field => field.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["_capacity", "_kernel", "_length", "_slab", "_token"], fields);
    }

    [Fact]
    public void StaleLeaseStaysReturnedAfterItsSlotHoldsANewSlab()
    {
        using NativePool<int> pool = new(preLease: 2, returnMemoryOnDispose: NativeMemoryReturn.Free);
        Pooled<int> initial = pool.Rent(2, 7);
        Pooled<int> stale = initial;
        initial.Dispose();
        Assert.NotEqual((nuint)0, pool.TrimRetainedMemory());
        using Pooled<int> current = pool.Rent(2, 31);
        Assert.True(IsReturned(stale, dispose: false));
        Assert.True(IsReturned(stale, dispose: true));
        Assert.Equal(31, current.AsSpan()[1]);
    }

    [Fact]
    public void ReusedSlotStillRejectsOldAuthorityAndResolvesActualEnteredStorage()
    {
        using NativePool<int> pool = new(preLease: 2, returnMemoryOnDispose: NativeMemoryReturn.Free);
        Pooled<int> initial = pool.Rent(2, static writer => writer.Fill(7));
        Pooled<int> stale = initial;
        initial.Dispose();
        using Pooled<int> current = pool.Rent(2, static writer => writer.Fill(31));
        Assert.Equal(2, current.Length);
        Assert.Equal(2, current.Capacity);
        Assert.True(IsReturned(stale, dispose: false));
        Assert.True(IsReturned(stale, dispose: true));
        Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        current.Access(view =>
        {
            Assert.Equal(31, view[0]);
            Assert.Equal(31, view[1]);
            Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
            Assert.Throws<InvalidOperationException>(pool.Dispose);
            view[0] = 13;
        });
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(32, current.Process(1, 19, static (values, extra) => values[0] + extra));
        Assert.Equal(31, current.Read(static view => view[1]));
        Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(1, pool.GetStatistics().FreshSegmentAllocationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultHandleHasOnlyMetadataAndNeverGainsPayloadOrReturnAuthority(bool dispose)
    {
        Pooled<int> missing = default;
        Assert.Equal(0, missing.Length);
        Assert.Equal(0, missing.Capacity);
        try
        {
            if (dispose) missing.Dispose();
            else _ = missing.Read(static view => view[0]);
            Assert.Fail("Default handles must not gain access or return authority.");
        }
        catch (NativeAllocationUninitializedException exception)
        {
            Assert.Equal(nameof(Pooled<int>), exception.OwnerKind);
            Assert.Equal(dispose ? nameof(Pooled<int>.Dispose) : nameof(Pooled<int>.Read), exception.Operation);
        }
    }

    [Fact]
    public void ProcessCannotExposeUninitializedCapacityBeyondThePublishedLogicalLength()
    {
        using NativePool<int> pool = new(preLease: 4, returnMemoryOnDispose: NativeMemoryReturn.Free);
        using Pooled<int> lease = pool.Rent(1, static writer => writer.Write(42));
        int calls = 0;
        bool rejected = false;
        try
        {
            _ = lease.Process(2, 0, (values, _) => { calls++; return values.Length; });
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }
        Assert.True(rejected);
        Assert.Equal(0, calls);
        Assert.Equal(4, lease.Capacity);
        Assert.Equal(1, lease.Length);
        Assert.Equal(42, lease.Read(static view => view[0]));
    }

    private static bool IsReturned(scoped Pooled<int> value, bool dispose)
    {
        try
        {
            if (dispose) value.Dispose();
            else _ = value.Read(static view => view[0]);
            return false;
        }
        catch (NativeAllocationReturnedException)
        {
            return true;
        }
    }
}
