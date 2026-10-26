namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeFilledBumpTests
{
    [Fact]
    public void FilledScratchPublishesEveryValueAndRecyclesWithItsLane()
    {
        using NativeArena arena = new(preAllocateBytes: 4096);
        ArenaLease<long> ordinary = arena.Scratch<long>(8, 7L);
        ArenaLease<int> scoped = arena.ScratchScoped<int>(5, -3);
        ArenaLease<byte> empty = arena.Scratch<byte>(0, 1);

        Assert.Equal(56L, ordinary.Read(static view => Sum(view.AsSpan())));
        Assert.Equal(-15L, scoped.Read(static view => Sum(view.AsSpan())));
        Assert.Equal(0, empty.Length);
        Assert.Equal((8 * sizeof(long)) + (5 * sizeof(int)), arena.GetStatistics().RequestedBytes);

        arena.RecycleScoped();
        Assert.IsType<NativeAllocationReturnedException>(CaptureRead(scoped));
        Assert.Equal(7L, ordinary.Read(static view => view[0]));
        arena.Reset();
        Assert.IsType<NativeAllocationReturnedException>(CaptureRead(ordinary));
        Assert.Throws<ArgumentOutOfRangeException>(() => arena.Scratch<int>(-1, 0));
    }

    [Fact]
    public void FilledScratchCannotRunInsideAnInitializer()
    {
        using NativeArena arena = new(preAllocateBytes: 4096);
        Assert.Throws<InvalidOperationException>(() => arena.Scratch<int>(2, writer =>
        {
            _ = arena.Scratch<int>(1, 9);
            writer.Fill(0);
        }));
        Assert.Equal(0, arena.GetStatistics().RequestedBytes);
    }

    [Fact]
    public void PreparedFilledScratchCountsSuccessAndRefusesBeyondItsBound()
    {
        using NativeArena arena = new(new NativeArenaPreparation(64, 32), new NativeMemoryBudget(64 * 1024));
        ArenaLease<int> values = arena.Scratch<int>(16, 4);
        Assert.Equal(64L, values.Read(static view => Sum(view.AsSpan())));
        NativePreparedArenaStatistics prepared = arena.CapturePreparedSnapshot();
        Assert.Equal(1, prepared.SuccessfulScratchCount);
        Assert.Equal(0, prepared.RejectedCapacityCount);

        Assert.Throws<InvalidOperationException>(() => arena.Scratch<int>(1, 4));
        prepared = arena.CapturePreparedSnapshot();
        Assert.Equal(1, prepared.SuccessfulScratchCount);
        Assert.Equal(1, prepared.RejectedCapacityCount);
        Assert.Equal(64L, values.Read(static view => Sum(view.AsSpan())));
    }

    [Fact]
    public void FilledRegionLeasePublishesValuesAndEndsWithTheRegion()
    {
        NativeRegion region = new(256, NativeMemoryReturn.ToNativeMemory);
        Local<double> values = region.Lease<double>(4, 0.5);
        Local<int> grown = region.Lease<int>(1_000, 2);
        Assert.Equal(2d, values.Read(static view => view[0] + view[1] + view[2] + view[3]));
        Assert.Equal(2_000L, grown.Read(static view => Sum(view.AsSpan())));
        Assert.Equal((4 * sizeof(double)) + (1_000 * sizeof(int)), region.GetStatistics().RequestedBytes);
        region.Dispose();
        Assert.IsType<NativeAllocationDisposedException>(ReadAfterDispose(values));
    }

    private static long Sum<T>(Span<T> values)
        where T : unmanaged, System.Numerics.INumber<T>
    {
        long sum = 0;
        foreach (ref readonly T value in values)
        {
            sum += long.CreateTruncating(value);
        }

        return sum;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The test captures the stale handle failure for assertions.")]
    private static Exception CaptureRead<T>(ArenaLease<T> lease)
        where T : unmanaged
    {
        try
        {
            _ = lease.Read(static view => view[0]);
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("The stale arena lease remained active.");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The test captures the stale handle failure for assertions.")]
    private static Exception ReadAfterDispose(Local<double> local)
    {
        try
        {
            _ = local.Read(static view => view[0]);
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("The disposed Local remained active.");
    }
}
