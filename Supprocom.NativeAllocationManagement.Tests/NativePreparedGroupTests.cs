namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedGroupTests
{
    [Fact]
    public void QuadRefusalRollsBackAllRangesBeforeTheProducerAndAllocatesNothing()
    {
        NativeMemoryBudget budget = new(512);
        using NativeArena arena = new(new NativeArenaPreparation(16, 16), budget);
        ArenaLease<int> source = arena.Scratch<int>(1, static writer => writer.Write(17));
        bool invoked = false;
        NativeLeaseSourceQuadSpanInitializer<int, int, int, int, int> producer =
            (_, a, b, c, d) => { invoked = true; a.Fill(1); b.Fill(2); c.Fill(3); d.Fill(4); };
        Assert.False(NativeLeaseOperations.TryInitializeScoped(source, arena, 1, 1, 1, 2,
            producer, out ArenaLease<int> a, out ArenaLease<int> b,
            out ArenaLease<int> c, out ArenaLease<int> d));
        Assert.Equal(0, a.Length);
        Assert.Equal(0, b.Length);
        Assert.Equal(0, c.Length);
        Assert.Equal(0, d.Length);
        AssertUninitialized(a);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            if (NativeLeaseOperations.TryInitializeScoped(source, arena, 1, 1, 1, 2,
                producer, out _, out _, out _, out _))
            {
                throw new InvalidOperationException("Prepared group unexpectedly fit.");
            }
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.False(invoked);
        Assert.Equal(17, source.Read(static view => view[0]));
        Assert.Equal(0, arena.CapturePreparedSnapshot().ScopedUsedBytes);
        Assert.Equal(1_001, arena.CapturePreparedSnapshot().RejectedCapacityCount);
        Assert.Equal(1, arena.CapturePreparedSnapshot().SuccessfulScratchCount);
        Assert.Equal(2, budget.CaptureStatistics().AllocationCount);
    }

    [Fact]
    public void OctetPublishesAllRegionsAndItsLastRangeCanRefuseWithoutGrowth()
    {
        NativeMemoryBudget budget = new(512);
        using NativeArena arena = new(new NativeArenaPreparation(16, 32), budget);
        ArenaLease<int> source = arena.Scratch<int>(1, static writer => writer.Write(17));
        NativeLeaseSourceOctupleSpanInitializer<int, int, int, int, int, int, int, int, int> producer =
            static (input, a, b, c, d, e, f, g, h) =>
            { a.Fill(input[0]); b.Fill(2); c.Fill(3); d.Fill(4); e.Fill(5); f.Fill(6); g.Fill(7); h.Fill(8); };
        Assert.True(NativeLeaseOperations.TryInitializeScoped(source, arena, 1, 1, 1, 1, 1, 1, 1, 1,
            producer, out ArenaLease<int> a, out ArenaLease<int> b, out ArenaLease<int> c, out ArenaLease<int> d,
            out ArenaLease<int> e, out ArenaLease<int> f, out ArenaLease<int> g, out ArenaLease<int> h));
        Assert.Equal(17, a.Read(static view => view[0]));
        Assert.Equal(8, h.Read(static view => view[0]));
        Assert.Equal(32, arena.CapturePreparedSnapshot().ScopedUsedBytes);
        Assert.Equal(9, arena.CapturePreparedSnapshot().SuccessfulScratchCount);
        arena.RecycleScoped();
        Assert.False(NativeLeaseOperations.TryInitializeScoped(source, arena, 1, 1, 1, 1, 1, 1, 1, 2,
            producer, out _, out _, out _, out _, out _, out _, out _, out _));
        Assert.Equal(0, arena.CapturePreparedSnapshot().ScopedUsedBytes);
        Assert.Equal(1, arena.CapturePreparedSnapshot().RejectedCapacityCount);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            if (!NativeLeaseOperations.TryInitializeScoped(source, arena, 1, 1, 1, 1, 1, 1, 1, 1,
                producer, out _, out _, out _, out _, out _, out _, out _, out _))
            {
                throw new InvalidOperationException("Prepared octet unexpectedly exhausted.");
            }
            arena.RecycleScoped();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(2, budget.CaptureStatistics().AllocationCount);
    }

    [Fact]
    public void StaleSourceIsAnErrorEvenWhenTheGroupCannotFit()
    {
        NativeMemoryBudget budget = new(512);
        using NativeArena arena = new(new NativeArenaPreparation(16, 0), budget);
        ArenaLease<int> source = arena.Scratch<int>(1, static writer => writer.Write(17));
        arena.Reset();
        bool rejected = false;
        try
        {
            NativeLeaseOperations.TryInitializeScoped<int, int, int, int, int>(source, arena, 1, 1, 1, 1,
                static (_, _, _, _, _) => { }, out _, out _, out _, out _);
        }
        catch (NativeAllocationReturnedException)
        {
            rejected = true;
        }
        Assert.True(rejected);
        Assert.Equal(0, arena.CapturePreparedSnapshot().RejectedCapacityCount);
        Assert.Equal(0, arena.CapturePreparedSnapshot().ScopedUsedBytes);
        arena.Reset();
    }

    [Fact]
    public void ProducerFailureIsNotRelabeledCapacityRefusal()
    {
        NativeMemoryBudget budget = new(512);
        using NativeArena arena = new(new NativeArenaPreparation(16, 16), budget);
        ArenaLease<int> source = arena.Scratch<int>(1, static writer => writer.Write(17));
        bool rejected = false;
        try
        {
            NativeLeaseOperations.TryInitializeScoped<int, int, int, int, int>(source, arena, 1, 1, 1, 1,
                static (_, a, _, _, _) => { a.Fill(1); throw new InvalidOperationException("Injected prepared failure."); },
                out _, out _, out _, out _);
        }
        catch (InvalidOperationException exception) when (string.Equals(exception.Message, "Injected prepared failure.", StringComparison.Ordinal))
        {
            rejected = true;
        }
        Assert.True(rejected);
        Assert.Equal(0, arena.CapturePreparedSnapshot().RejectedCapacityCount);
        Assert.Equal(1, arena.CapturePreparedSnapshot().InitializerFailureCount);
        Assert.Equal(0, arena.CapturePreparedSnapshot().ScopedUsedBytes);
        arena.Reset();
    }

    private static void AssertUninitialized(scoped ArenaLease<int> lease)
    {
        try { lease.Clear(); }
        catch (NativeAllocationUninitializedException) { return; }
        throw new InvalidOperationException("A refused prepared output acquired authority.");
    }
}
