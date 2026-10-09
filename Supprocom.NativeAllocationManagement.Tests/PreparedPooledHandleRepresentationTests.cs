using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class PreparedPooledHandleRepresentationTests
{
    [Fact]
    public void ClosedHandlesKeepAuthorityWithoutAnUnusedPayloadPointer()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(32, Unsafe.SizeOf<PreparedPooled<byte>>());
        Assert.Equal(32, Unsafe.SizeOf<PreparedPooled<int>>());
        Assert.Equal(16, Unsafe.SizeOf<Span<byte>>());
        Assert.Equal(16, Unsafe.SizeOf<Span<int>>());
        string[] fields = typeof(PreparedPooled<int>).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(static field => field.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["_capacity", "_kernel", "_length", "_slabIndex", "_token"], fields);
    }

    [Fact]
    public void ReusedSlotStillRejectsOldAuthorityAndResolvesActualEnteredStorage()
    {
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(1, 2, 1), budget: null);
        PreparedPooled<int> initial = pool.Rent(2, static writer => writer.Fill(7));
        PreparedPooled<int> stale = initial;
        initial.Dispose();
        using PreparedPooled<int> current = pool.Rent(2, static writer => writer.Fill(31));
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
        PreparedPooled<int> missing = default;
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
            Assert.Equal(nameof(PreparedPooled<int>), exception.OwnerKind);
            Assert.Equal(dispose ? nameof(PreparedPooled<int>.Dispose) : nameof(PreparedPooled<int>.Read), exception.Operation);
        }
    }

    private static bool IsReturned(scoped PreparedPooled<int> value, bool dispose)
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
