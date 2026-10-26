namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeLeaseSpanTests
{
    [Fact]
    public void PooledSpanSharesTheLeaseStorageAndRejectsAStaleLease()
    {
        using NativePool<int> pool = new(preLease: 4);
        Pooled<int> lease = pool.Rent(4, 1);
        Span<int> span = lease.AsSpan();
        Assert.Equal(4, span.Length);
        span[2] = 9;
        Assert.Equal(12, lease.Read(static view => view[0] + view[1] + view[2] + view[3]));
        Pooled<int> moved = Pooled<int>.Move(ref lease);
        Assert.Equal(9, moved.AsSpan()[2]);
        Assert.IsType<NativeAllocationUninitializedException>(CaptureAsSpan(lease));
        Pooled<int> stale = moved;
        moved.Dispose();
        Assert.IsType<NativeAllocationReturnedException>(CaptureAsSpan(stale));
    }

    [Fact]
    public void PreparedPooledSpanSharesTheSlotStorage()
    {
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(1, 8, 1), new NativeMemoryBudget(4096));
        using PreparedPooled<int> lease = pool.Rent(8, 3);
        Span<int> span = lease.AsSpan();
        span[0] = 5;
        Assert.Equal(5 + (7 * 3), lease.Read(static view => view.AsSpan().ToArray().Sum()));
    }

    [Fact]
    public void ArenaSpanEndsWithItsGenerationAndScope()
    {
        using NativeArena arena = new(preAllocateBytes: 1024);
        ArenaLease<long> ordinary = arena.Scratch<long>(3, 4L);
        ArenaLease<long> scoped = arena.ScratchScoped<long>(2, 6L);
        Assert.Equal(12L, Sum(ordinary.AsSpan()));
        Assert.Equal(12L, Sum(scoped.AsSpan()));
        arena.RecycleScoped();
        Assert.IsType<NativeAllocationReturnedException>(CaptureAsSpan(scoped));
        Assert.Equal(12L, Sum(ordinary.AsSpan()));
        arena.Reset();
        Assert.IsType<NativeAllocationReturnedException>(CaptureAsSpan(ordinary));
    }

    [Fact]
    public void RegionSpanEndsWithTheRegionAndItsReusedKernel()
    {
        NativeRegion region = new(preAllocateBytes: 256);
        Local<int> local = region.Lease<int>(4, 2);
        local.AsSpan()[3] = 10;
        Assert.Equal(16, local.Read(static view => view[0] + view[1] + view[2] + view[3]));
        region.Dispose();
        Assert.IsType<NativeAllocationDisposedException>(CaptureAsSpan(local));
        NativeRegion next = new(preAllocateBytes: 256);
        Assert.IsType<NativeAllocationDisposedException>(CaptureAsSpan(local));
        next.Dispose();
    }

    private static long Sum(Span<long> values)
    {
        long sum = 0;
        foreach (ref readonly long value in values)
        {
            sum += value;
        }

        return sum;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The test captures the stale lease failure for assertions.")]
    private static Exception CaptureAsSpan<T>(Pooled<T> lease)
        where T : unmanaged
    {
        try
        {
            _ = lease.AsSpan();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("The stale pooled lease produced a span.");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The test captures the stale lease failure for assertions.")]
    private static Exception CaptureAsSpan<T>(ArenaLease<T> lease)
        where T : unmanaged
    {
        try
        {
            _ = lease.AsSpan();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("The stale arena lease produced a span.");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The test captures the stale lease failure for assertions.")]
    private static Exception CaptureAsSpan<T>(Local<T> lease)
        where T : unmanaged
    {
        try
        {
            _ = lease.AsSpan();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("The stale region local produced a span.");
    }
}
