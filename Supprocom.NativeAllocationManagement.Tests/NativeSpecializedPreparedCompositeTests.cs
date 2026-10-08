namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeSpecializedPreparedCompositeTests
{
    [Fact]
    public void EveryAccessShapeUnwindsAllEnteredBorrowsWhenTheCallbackThrows()
    {
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(3, 1, 3), budget: null);
        using NativeConcurrentArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using PreparedPooled<int> a = pool.Rent(1, static writer => writer.Write(7));
        using PreparedPooled<int> b = pool.Rent(1, static writer => writer.Write(11));
        using PreparedPooled<int> c = pool.Rent(1, static writer => writer.Write(13));
        ConcurrentArenaLease<int> x = arena.Scratch<int>(1, static writer => writer.Write(17));
        ConcurrentArenaLease<int> y = arena.Scratch<int>(1, static writer => writer.Write(19));
        ConcurrentArenaLease<int> z = arena.Scratch<int>(1, static writer => writer.Write(23));
        ConcurrentArenaLease<int> w = arena.Scratch<int>(1, static writer => writer.Write(29));
        int failures = 0;
        try { NativeLeaseOperations.Access(a, b, static (_, _) => throw new OperationCanceledException()); }
        catch (OperationCanceledException) { failures++; }
        try { NativeLeaseOperations.Access(a, b, c, static (_, _, _) => throw new OperationCanceledException()); }
        catch (OperationCanceledException) { failures++; }
        try { NativeLeaseOperations.Access(a, x, static (_, _) => throw new OperationCanceledException()); }
        catch (OperationCanceledException) { failures++; }
        try { NativeLeaseOperations.Access(a, x, y, static (_, _, _) => throw new OperationCanceledException()); }
        catch (OperationCanceledException) { failures++; }
        try { NativeLeaseOperations.Access(a, x, y, z, w, static (_, _, _, _, _) => throw new OperationCanceledException()); }
        catch (OperationCanceledException) { failures++; }
        try { NativeLeaseOperations.Access(a, b, c, x, static (_, _, _, _) => throw new OperationCanceledException()); }
        catch (OperationCanceledException) { failures++; }
        try { NativeLeaseOperations.Access(a, b, c, x, y, static (_, _, _, _, _) => throw new OperationCanceledException()); }
        catch (OperationCanceledException) { failures++; }
        Assert.Equal(7, failures);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(7, a.Read(static view => view[0]));
        Assert.Equal(11, b.Read(static view => view[0]));
        Assert.Equal(13, c.Read(static view => view[0]));
        arena.ReturnMemoryToNativeMemory();
    }

    [Fact]
    public void ScopedInitializationPublishesFourCompleteRangesAndFailureReturnsTheSourceBorrow()
    {
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(1, 1, 1), budget: null);
        using NativeConcurrentArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using PreparedPooled<int> source = pool.Rent(1, static writer => writer.Write(7));
        NativeLeaseOperations.InitializeScoped<int, int, int, int, int>(source, arena, 1, 1, 1, 1,
            static (input, a, b, c, d) => { a.Fill(input[0]); b.Fill(11); c.Fill(13); d.Fill(17); },
            out ConcurrentArenaLease<int> a, out ConcurrentArenaLease<int> b,
            out ConcurrentArenaLease<int> c, out ConcurrentArenaLease<int> d);
        NativeLeaseOperations.Access(a, b, c, d, static (va, vb, vc, vd) =>
        {
            Assert.Equal(7, va[0]); Assert.Equal(11, vb[0]); Assert.Equal(13, vc[0]); Assert.Equal(17, vd[0]);
        });
        arena.RecycleScoped();
        bool rejected = false;
        try
        {
            NativeLeaseOperations.InitializeScoped<int, int, int, int, int>(source, arena, 1, 1, 1, 1,
                static (_, a, _, _, _) => { a.Fill(31); throw new OperationCanceledException(); },
                out _, out _, out _, out _);
        }
        catch (OperationCanceledException) { rejected = true; }
        Assert.True(rejected);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(7, source.Read(static view => view[0]));
        arena.RecycleScoped();
        arena.ReturnMemoryToNativeMemory();
    }
}
