using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeTerminalBudgetCustodyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 128)]
    [InlineData(1, 0)]
    [InlineData(1, 128)]
    [InlineData(2, 0)]
    [InlineData(2, 128)]
    [InlineData(3, 0)]
    [InlineData(3, 128)]
    [InlineData(4, 0)]
    [InlineData(4, 128)]
    [InlineData(5, 0)]
    [InlineData(5, 128)]
    [InlineData(6, 0)]
    [InlineData(6, 128)]
    [InlineData(7, 0)]
    [InlineData(7, 128)]
    [InlineData(8, 0)]
    [InlineData(8, 128)]
    [InlineData(9, 0)]
    [InlineData(9, 128)]
    [InlineData(10, 0)]
    [InlineData(10, 128)]
    [InlineData(11, 0)]
    [InlineData(11, 128)]
    public void ClosedClassOwnersDoNotKeepReturnedDomainsOrTraceBuffers(int model, int traceCapacity)
    {
        using IDisposable owner = CreateClosedClass(model, traceCapacity,
            out WeakReference<NativeMemoryBudget> budget, out NativeMemoryBudgetStatistics terminal);
        AssertReturned(terminal);
        Assert.Equal(model switch { 8 => 8, 9 => 2, 1 or 3 or 5 or 7 => 1, _ => 0 }, terminal.AllocationCount);
        Assert.Equal(traceCapacity, terminal.TraceCapacity);
        if (traceCapacity == 0) Assert.Equal(0, terminal.TraceCount);
        Collect();
        Assert.False(IsAlive(budget));
        Assert.Equal(NativeOwnerLifecycle.Disposed, Snapshot(owner).Lifecycle);
        Assert.Equal(0, Snapshot(owner).OutstandingNativeBytes);
        GC.KeepAlive(owner);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 128)]
    [InlineData(true, 0)]
    [InlineData(true, 128)]
    public void ClosedLexicalKernelDoesNotKeepItsReturnedDomain(bool empty, int traceCapacity)
    {
        using NativeRegion owner = CreateClosedRegion(empty, traceCapacity,
            out WeakReference<NativeMemoryBudget> budget, out NativeMemoryBudgetStatistics terminal);
        AssertReturned(terminal);
        Assert.Equal(empty ? 0 : 1, terminal.AllocationCount);
        Collect();
        Assert.False(IsAlive(budget));
        Assert.Equal(NativeOwnerLifecycle.Disposed, RegionSnapshot(in owner).Lifecycle);
        Assert.Equal(0, RegionSnapshot(in owner).OutstandingNativeBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void EmptyActiveOwnersKeepTheirDomainForFutureGrowth(int model)
    {
        using IDisposable owner = CreateClassWithOnlyWeakDomain(model, NativeMemoryReturn.ToNativeMemory,
            out WeakReference<NativeMemoryBudget> budget);
        Collect();
        Assert.True(IsAlive(budget));
        Assert.Equal(0, Observe(budget).CommittedBytes);
        Grow(owner);
        Assert.True(Observe(budget).CommittedBytes > 0);
        AssertReturned(DisposeAndObserve(owner, budget));
        Collect();
        Assert.False(IsAlive(budget));
        GC.KeepAlive(owner);
    }

    [Fact]
    public void FailedPoolClosureDoesNotLoseItsLiveDomain()
    {
        using NativePool<int> owner = (NativePool<int>)CreateClassWithOnlyWeakDomain(0,
            NativeMemoryReturn.ToNativeMemory, out WeakReference<NativeMemoryBudget> budget);
        using (Pooled<int> lease = owner.Rent(1, static writer => writer.Write(42)))
        {
            Assert.Throws<InvalidOperationException>(owner.Dispose);
            Collect();
            Assert.True(IsAlive(budget));
            Assert.True(Observe(budget).CommittedBytes > 0);
            Assert.Equal(42, lease.Read(static view => view[0]));
        }
        AssertReturned(DisposeAndObserve(owner, budget));
        Collect();
        Assert.False(IsAlive(budget));
        GC.KeepAlive(owner);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void DetachedFastStorageKeepsTheDomainUntilActualFinalizerReturn(int model)
    {
        AbandonHeldDetachedOwner(model, out WeakReference<NativeMemoryBudget> budget, out WeakReference<IDisposable> owner);
        Collect();
        Assert.False(IsAlive(budget));
        Assert.False(owner.TryGetTarget(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasedGenerationAndStaleSegmentDoNotKeepTheirDomain(bool detach)
    {
        NativeGenerationOwner owner = CreateReleasedGeneration(detach,
            out NativeSegment staleSegment, out WeakReference<NativeMemoryBudget> budget,
            out NativeMemoryBudgetStatistics terminal);
        AssertReturned(terminal);
        Assert.Equal(1, terminal.AllocationCount);
        Assert.Equal(detach ? 5 : 4, terminal.TraceCount);
        Collect();
        Assert.False(IsAlive(budget));
        Assert.Equal(IntPtr.Zero, staleSegment.Pointer);
        owner.ReleaseToNative();
        staleSegment.FreeNow();
        GC.KeepAlive(owner);
        GC.KeepAlive(staleSegment);
    }

    [Fact]
    public void PendingUniqueReturnKeepsItsTraceDomainAfterAllocatorClosure()
    {
        NativeTransfer<int> transfer = CreatePendingLateReturn(out NativeConcurrentPool<int> owner,
            out WeakReference<NativeMemoryBudget> budget);
        NativeTransfer<int> stale = transfer;
        try
        {
            Collect();
            Assert.True(IsAlive(budget));
            Assert.Equal(0, Observe(budget).CommittedBytes);
            Assert.Equal(NativeTransferLifecycle.Invalidated, transfer.CaptureSnapshot().Lifecycle);
        }
        finally { AssertReturned(ReturnUniqueAndObserve(transfer, budget)); }
        Collect();
        Assert.False(IsAlive(budget));
        Assert.Equal(1, stale.CaptureSnapshot().PayloadReturnCount);
        Assert.Equal(0, stale.CaptureSnapshot().OwnedBackingBytes);
        Assert.False(stale.CaptureSnapshot().HasReturnObligation);
        GC.KeepAlive(stale);
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task DetachAndReleasePreserveEveryCommittedTraceWithoutNewSynchronization()
    {
        for (int iteration = 0; iteration < 32; iteration++)
        {
            NativeMemoryTestHooks.Reset();
            NativeMemoryBudget budget = new(64, 16);
            NativeGenerationOwner owner = CreateGeneration(budget, out NativeSegment segment);
            using ManualResetEventSlim start = new(false);
            Task detach = Task.Run(() => { start.Wait(); owner.Detach(); });
            Task release = Task.Run(() => { start.Wait(); owner.ReleaseToNative(); });
            try
            {
                start.Set();
                await Task.WhenAll(detach, release);
                AssertReturned(budget.CaptureStatistics());
                Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
                Assert.Equal(1, CountTrace(budget, NativeMemoryTraceKind.GenerationReleased));
                Assert.Equal(NativeMemoryDiagnostics.Snapshot().DetachedGenerationCount,
                    CountTrace(budget, NativeMemoryTraceKind.GenerationDetached));
                Assert.Equal(IntPtr.Zero, segment.Pointer);
                Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes);
            }
            finally
            {
                owner.ReleaseToNative();
                segment.FreeNow();
                NativeMemoryTestHooks.Reset();
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDisposable CreateClosedClass(int model, int traceCapacity,
        out WeakReference<NativeMemoryBudget> observed, out NativeMemoryBudgetStatistics terminal)
    {
        NativeMemoryBudget budget = new(1_048_576, traceCapacity);
        observed = new(budget);
        IDisposable owner = CreateClass(model, budget, NativeMemoryReturn.ToNativeMemory);
        using (owner) { }
        terminal = budget.CaptureStatistics();
        GC.KeepAlive(budget);
        return owner;
    }

    private static IDisposable CreateClass(int model, NativeMemoryBudget budget, NativeMemoryReturn policy) => model switch
    {
        0 or 1 => new NativePool<int>(budget, model == 0 ? 0 : 4, 0, policy),
        2 or 3 => new NativeArena(budget, model == 2 ? 0U : 64U, policy),
        4 or 5 => new NativeConcurrentPool<int>(budget, model == 4 ? 0 : 4, 0, policy, false),
        6 or 7 => new NativeConcurrentArena(budget, model == 6 ? 0U : 64U, policy, false),
        8 => new NativePool<int>(new NativePoolPreparation(16, 4, 2), budget),
        9 => new NativeArena(new NativeArenaPreparation(64, 64), budget),
        10 => new NativeConcurrentPool<int>(budget, 0, 0, policy, true),
        11 => new NativeConcurrentArena(budget, 0, policy, true),
        _ => throw new ArgumentOutOfRangeException(nameof(model))
    };

    private static NativeOwnerDiagnosticSnapshot Snapshot(IDisposable owner) => owner switch
    {
        NativePool<int> pool => pool.CaptureDiagnosticSnapshot(),
        NativeArena arena => arena.CaptureDiagnosticSnapshot(),
        NativeConcurrentPool<int> pool => pool.CaptureDiagnosticSnapshot(),
        NativeConcurrentArena arena => arena.CaptureDiagnosticSnapshot(),
        _ => throw new ArgumentException("Unknown owner model.", nameof(owner))
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeRegion CreateClosedRegion(bool empty, int traceCapacity,
        out WeakReference<NativeMemoryBudget> observed, out NativeMemoryBudgetStatistics terminal)
    {
        NativeMemoryBudget budget = new(1_048_576, traceCapacity);
        observed = new(budget);
        using NativeRegion owner = new(budget, empty ? 0U : 64U, NativeMemoryReturn.ToNativeMemory);
        owner.Dispose();
        terminal = budget.CaptureStatistics();
        GC.KeepAlive(budget);
        return owner;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDisposable CreateClassWithOnlyWeakDomain(int model, NativeMemoryReturn policy,
        out WeakReference<NativeMemoryBudget> observed)
    {
        NativeMemoryBudget budget = new(1_048_576, 128);
        observed = new(budget);
        return CreateClass(model, budget, policy);
    }

    private static void Grow(IDisposable owner)
    {
        switch (owner)
        {
            case NativePool<int> pool:
                using (Pooled<int> lease = pool.Rent(1, static writer => writer.Write(42)))
                    Assert.Equal(42, lease.Read(static view => view[0]));
                break;
            case NativeArena arena:
                Assert.Equal(42, arena.Scratch<int>(1, static writer => writer.Write(42)).Read(static view => view[0]));
                break;
            case NativeConcurrentPool<int> pool:
                using (ConcurrentPooled<int> lease = pool.Rent(1, static writer => writer.Write(42)))
                    Assert.Equal(42, lease.Read(static view => view[0]));
                break;
            case NativeConcurrentArena arena:
                Assert.Equal(42, arena.Scratch<int>(1, static writer => writer.Write(42)).Read(static view => view[0]));
                break;
            default:
                throw new ArgumentException("Unknown owner model.", nameof(owner));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonHeldDetachedOwner(int model, out WeakReference<NativeMemoryBudget> budget,
        out WeakReference<IDisposable> observedOwner)
    {
        using IDisposable owner = CreateClassWithOnlyWeakDomain(model, NativeMemoryReturn.ToGarbageCollector, out budget);
        owner.Dispose();
        observedOwner = new(owner);
        Collect();
        Assert.True(IsAlive(budget));
        Assert.True(Observe(budget).CommittedBytes > 0);
        Assert.Equal(0, Observe(budget).FreeCount);
        Assert.Equal(NativeOwnerLifecycle.Disposed, Snapshot(owner).Lifecycle);
        GC.KeepAlive(owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeGenerationOwner CreateReleasedGeneration(bool detach, out NativeSegment segment,
        out WeakReference<NativeMemoryBudget> observed, out NativeMemoryBudgetStatistics terminal)
    {
        NativeMemoryBudget budget = new(64, 16);
        observed = new(budget);
        NativeGenerationOwner owner = CreateGeneration(budget, out segment);
        try
        {
            if (detach) owner.Detach();
            Assert.Equal(64, budget.CaptureStatistics().CommittedBytes);
        }
        finally { owner.ReleaseToNative(); }
        terminal = budget.CaptureStatistics();
        Assert.Equal(1, CountTrace(budget, NativeMemoryTraceKind.GenerationReleased));
        Assert.Equal(detach ? 1 : 0, CountTrace(budget, NativeMemoryTraceKind.GenerationDetached));
        GC.KeepAlive(budget);
        return owner;
    }

    private static NativeGenerationOwner CreateGeneration(NativeMemoryBudget budget, out NativeSegment segment)
    {
        NativeGenerationOwner owner = new(7, budget, 42);
        segment = NativeSegment.Allocate(64, "TerminalCustodyTest", 7, "prepare", NativeOwnerLifecycle.Active, false, budget);
        try { owner.AddSegment(segment); }
        catch { segment.FreeNow(); owner.ReleaseToNative(); throw; }
        return owner;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeTransfer<int> CreatePendingLateReturn(out NativeConcurrentPool<int> owner,
        out WeakReference<NativeMemoryBudget> observed)
    {
        NativeMemoryBudget budget = new(64, 128);
        observed = new(budget);
        using (owner = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false))
        {
            NativeTransfer<int> transfer = owner.RentTransferable(1, static writer => writer.Write(42));
            Assert.Equal(42, transfer.Read(static view => view[0]));
            return transfer;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeMemoryBudgetStatistics ReturnUniqueAndObserve(NativeTransfer<int> transfer,
        WeakReference<NativeMemoryBudget> observed)
    {
        Assert.True(observed.TryGetTarget(out NativeMemoryBudget? budget));
        transfer.Dispose();
        Assert.Equal(1, CountTrace(budget, NativeMemoryTraceKind.UniqueReturned));
        return budget.CaptureStatistics();
    }

    private static int CountTrace(NativeMemoryBudget budget, NativeMemoryTraceKind kind)
    {
        Span<NativeMemoryTraceEvent> entries = stackalloc NativeMemoryTraceEvent[128];
        int count = budget.CopyTraceTo(entries);
        int matching = 0;
        foreach (ref readonly NativeMemoryTraceEvent entry in entries[..count])
            if (entry.Kind == kind) matching++;
        return matching;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeMemoryBudgetStatistics DisposeAndObserve(IDisposable owner, WeakReference<NativeMemoryBudget> observed)
    {
        Assert.True(observed.TryGetTarget(out NativeMemoryBudget? budget));
        owner.Dispose();
        return budget.CaptureStatistics();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeMemoryBudgetStatistics Observe(WeakReference<NativeMemoryBudget> observed)
    {
        Assert.True(observed.TryGetTarget(out NativeMemoryBudget? budget));
        return budget.CaptureStatistics();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<NativeMemoryBudget> observed) => observed.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeOwnerDiagnosticSnapshot RegionSnapshot(in NativeRegion owner) => owner.CaptureDiagnosticSnapshot();

    private static void AssertReturned(NativeMemoryBudgetStatistics terminal)
    {
        Assert.Equal(0, terminal.CommittedBytes);
        Assert.Equal(0, terminal.ReservedBytes);
        Assert.Equal(0, terminal.ActiveAllocationCount);
        Assert.Equal(terminal.AllocationCount, terminal.FreeCount);
        Assert.Equal(0, terminal.RejectedAllocationCount);
        Assert.Equal(0, terminal.FailedAllocationCount);
        Assert.False(terminal.TraceOverflowed);
    }

    private static void Collect()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
