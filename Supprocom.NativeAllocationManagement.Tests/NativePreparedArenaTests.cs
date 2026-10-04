using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

// Prepared execution is checked independently of timing or GC heuristics.
public sealed class NativePreparedArenaTests
{
    [Fact]
    public void BothLanesStayWithinPreparedBackingAndResetWithoutAllocation()
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeArena arena = new(new NativeArenaPreparation(32, 32), budget);
        NativeLeaseInitializer<int> fillInts = static writer => writer.Fill(42);
        NativeLeaseInitializer<long> fillLongs = static writer => writer.Fill(7);
        Assert.True(arena.TryScratch(8, fillInts, out ArenaLease<int> ordinary));
        Assert.True(arena.TryScratchScoped(4, fillLongs, out ArenaLease<long> scoped));
        Assert.Equal(42, ordinary.Read(static view => view[7]));
        Assert.Equal(7, scoped.Read(static view => view[3]));
        arena.Reset();
        NativeMemoryBudgetStatistics backing = budget.CaptureStatistics();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            if (!arena.TryScratch(8, fillInts, out _)
                || !arena.TryScratchScoped(4, fillLongs, out _))
            {
                throw new InvalidOperationException("Prepared execution unexpectedly exhausted.");
            }
            arena.Reset();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
        Assert.Equal(backing.AllocationCount, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(backing.CommittedBytes, budget.CaptureStatistics().CommittedBytes);
        NativePreparedArenaStatistics snapshot = arena.CapturePreparedSnapshot();
        Assert.Equal(2_002, snapshot.SuccessfulScratchCount);
        Assert.Equal(32, snapshot.PeakOrdinaryUsedBytes);
        Assert.Equal(32, snapshot.PeakScopedUsedBytes);
        Assert.Equal(0, snapshot.OrdinaryUsedBytes);
        Assert.Equal(32, snapshot.OrdinaryAvailableBytes);
        Assert.Equal(32, snapshot.ScopedAvailableBytes);
    }

    [Fact]
    public void ExpectedCapacityExhaustionDoesNotRunTheProducerOrAllocate()
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeArena arena = new(new NativeArenaPreparation(8, 0), budget);
        bool invoked = false;
        NativeLeaseInitializer<int> producer = writer =>
        {
            invoked = true;
            writer.Fill(42);
        };
        Assert.False(arena.TryScratch(3, producer, out _));
        NativeMemoryBudgetStatistics before = budget.CaptureStatistics();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            _ = arena.TryScratch(3, producer, out _);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
        Assert.False(invoked);
        Assert.Equal(before.AllocationCount, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(before.CommittedBytes, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1_001, arena.CapturePreparedSnapshot().RejectedCapacityCount);
        Assert.Equal(0, arena.CapturePreparedSnapshot().OrdinaryUsedBytes);
    }

    [Fact]
    public void AlignmentConsumesTheDeclaredUsableBound()
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeArena arena = new(new NativeArenaPreparation(11, 0), budget);
        Assert.True(arena.TryScratch<byte>(1, static writer => writer.Write(1), out _));
        Assert.False(arena.TryScratch<long>(1, static writer => writer.Write(2), out _));
        Assert.Equal(1, arena.CapturePreparedSnapshot().OrdinaryUsedBytes);
        Assert.Equal(10, arena.CapturePreparedSnapshot().OrdinaryAvailableBytes);
        Assert.True(arena.TryScratch<int>(1, static writer => writer.Write(3), out ArenaLease<int> value));
        Assert.Equal(3, value.Read(static view => view[0]));
        Assert.Equal(8, arena.CapturePreparedSnapshot().OrdinaryUsedBytes);
    }

    [Fact]
    public void IncompleteInitializationRollsBackButRetainsAnActualReservedPeak()
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeArena arena = new(new NativeArenaPreparation(16, 0), budget);
        Assert.Throws<InvalidOperationException>(() =>
            arena.TryScratch<int>(4, static writer => writer.Write(1), out _));
        NativePreparedArenaStatistics failed = arena.CapturePreparedSnapshot();
        Assert.Equal(0, failed.OrdinaryUsedBytes);
        Assert.Equal(16, failed.PeakOrdinaryUsedBytes);
        Assert.Equal(1, failed.InitializerFailureCount);
        Assert.Equal(0, failed.SuccessfulScratchCount);
        Assert.True(arena.TryScratch<int>(4, static writer => writer.Fill(42), out ArenaLease<int> lease));
        Assert.Equal(42, lease.Read(static view => view[3]));
    }

    [Fact]
    public void ZeroBytePreparationDoesNotFabricateBacking()
    {
        NativeMemoryBudget budget = new(0);
        using NativeArena arena = new(default(NativeArenaPreparation), budget);
        Assert.True(arena.TryScratch<int>(0, static writer => writer.Fill(0), out ArenaLease<int> empty));
        Assert.Equal(0, empty.Length);
        Assert.False(arena.TryScratch<int>(1, static writer => writer.Fill(1), out _));
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, arena.CapturePreparedSnapshot().RetainedBytes);
    }

    [Fact]
    public void TotalAdmissionPrecedesAnyNativeAcquisition()
    {
        NativeMemoryBudget budget = new(1);
        NativeMemoryTestMetrics before = NativeMemoryTestHooks.Snapshot();
        Assert.Throws<NativeMemoryBudgetExceededException>(() =>
            new NativeArena(new NativeArenaPreparation(32, 32), budget));
        Assert.Equal(before.AllocationCount, NativeMemoryTestHooks.Snapshot().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().RejectedAllocationCount);
    }

    [Fact]
    public void NativeAcquisitionFailureCancelsTheEntirePreparedAdmission()
    {
        NativeMemoryBudget budget = new(1_024);
        NativeMemoryTestHooks.FailNextAllocation();
        Assert.Throws<NativeAllocationFailedException>(() =>
            new NativeArena(new NativeArenaPreparation(32, 32), budget));
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ActiveAllocationCount);
    }

    [Fact]
    public void TrimmingAnIdlePreparedLaneNeverRefillsIt()
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeArena arena = new(new NativeArenaPreparation(32, 32), budget);
        Assert.True(arena.TryScratch<int>(8, static writer => writer.Fill(42), out _));
        long retained = arena.CapturePreparedSnapshot().RetainedBytes;
        Assert.True(arena.TrimRetainedMemory() > 0);
        Assert.Equal(0, arena.CapturePreparedSnapshot().ScopedAvailableBytes);
        Assert.Equal((nuint)32, arena.CapturePreparedSnapshot().Preparation.ScopedBytes);
        Assert.False(arena.TryScratchScoped<int>(1, static writer => writer.Fill(1), out _));
        arena.Reset();
        Assert.True(arena.TrimRetainedMemory() > 0);
        Assert.Equal(0, arena.CapturePreparedSnapshot().RetainedBytes);
        Assert.Equal(retained, arena.CapturePreparedSnapshot().PeakRetainedBytes);
        Assert.False(arena.TryScratch<int>(1, static writer => writer.Fill(1), out _));
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void SecondLaneFailureFreesTheFirstLaneAndCancelsItsRemainingReservation()
    {
        NativeMemoryBudget budget = new(1_024);
        NativeMemoryTestHooks.SetBeforeOperationEntry(operation =>
        {
            if (string.Equals(operation, "NativeArena.PrepareScoped", StringComparison.Ordinal))
            {
                NativeMemoryTestHooks.FailNextAllocation();
            }
        });
        try
        {
            Assert.Throws<NativeAllocationFailedException>(() =>
                new NativeArena(new NativeArenaPreparation(32, 32), budget));
            NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
            Assert.Equal(0, snapshot.ReservedBytes);
            Assert.Equal(0, snapshot.CommittedBytes);
            Assert.Equal(0, snapshot.ActiveAllocationCount);
            Assert.Equal(1, snapshot.AllocationCount);
            Assert.Equal(1, snapshot.FreeCount);
        }
        finally
        {
            NativeMemoryTestHooks.SetBeforeOperationEntry(null);
        }
    }

    [Fact]
    public void ThrowingPreparationHookAlsoCancelsUnattemptedBacking()
    {
        NativeMemoryBudget budget = new(1_024);
        NativeMemoryTestHooks.SetBeforeOperationEntry(operation =>
        {
            if (string.Equals(operation, "NativeArena.PrepareScoped", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected pre-acquisition failure.");
            }
        });
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                new NativeArena(new NativeArenaPreparation(32, 32), budget));
            Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(0, budget.CaptureStatistics().ActiveAllocationCount);
        }
        finally
        {
            NativeMemoryTestHooks.SetBeforeOperationEntry(null);
        }
    }

    [Fact]
    public void PreparedTracingCorrelatesEachAcquisitionAndPhysicalTrim()
    {
        NativeMemoryBudget budget = new(1_024, traceCapacity: 8);
        using NativeArena arena = new(new NativeArenaPreparation(32, 32), budget);
        arena.TrimRetainedMemory();
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[8];
        Assert.Equal(6, budget.CopyTraceTo(events));
        Assert.Equal(NativeMemoryTraceKind.Admitted, events[0].Kind);
        Assert.Null(events[0].AllocationOrdinal);
        Assert.Equal(NativeMemoryTraceKind.Allocated, events[1].Kind);
        Assert.Equal(1, events[1].AllocationOrdinal);
        Assert.Equal(NativeMemoryTraceKind.Allocated, events[2].Kind);
        Assert.Equal(2, events[2].AllocationOrdinal);
        Assert.Equal(NativeMemoryTraceKind.Prepared, events[3].Kind);
        Assert.Null(events[3].AllocationOrdinal);
        Assert.Equal(NativeMemoryTraceKind.Trimmed, events[4].Kind);
        Assert.Equal(events[1].AllocationOrdinal, events[4].AllocationOrdinal);
        Assert.Equal(NativeMemoryTraceKind.Trimmed, events[5].Kind);
        Assert.Equal(events[2].AllocationOrdinal, events[5].AllocationOrdinal);
        Assert.Equal(0, events[5].CommittedBytes);
        foreach (ref readonly NativeMemoryTraceEvent entry in events[..6])
        {
            Assert.Equal(arena.Id, entry.OwnerId);
        }
    }

    [Fact]
    public void SaturatedPreparedHistoryCannotInterruptReuseOrCleanup()
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeArena arena = new(new NativeArenaPreparation(8, 0), budget);
        object kernel = typeof(NativeArena).GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(arena)!;
        kernel.GetType().GetField("_preparedSuccessCount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(kernel, long.MaxValue);
        Assert.True(arena.TryScratch<int>(2, static writer => writer.Fill(42), out _));
        Assert.True(arena.CapturePreparedSnapshot().HistoryOverflowed);
        Assert.Equal(long.MaxValue, arena.CapturePreparedSnapshot().SuccessfulScratchCount);
        arena.Dispose();
        NativePreparedArenaStatistics closed = arena.CapturePreparedSnapshot();
        Assert.Equal(NativeOwnerLifecycle.Disposed, closed.Lifecycle);
        Assert.Equal(0, closed.OrdinaryAvailableBytes);
        Assert.Equal(0, closed.RetainedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void UnpreparedOwnersDoNotPromiseAGrowthFreeTryContract()
    {
        using NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Assert.Throws<InvalidOperationException>(() => arena.TryScratch<int>(1, static writer => writer.Write(42), out _));
        Assert.Throws<InvalidOperationException>(() => arena.CapturePreparedSnapshot());
    }

    [Fact]
    public void ScopedRecyclingInvalidatesOldAuthorityWithoutChangingOrdinaryData()
    {
        using NativeArena arena = new(new NativeArenaPreparation(8, 8), new NativeMemoryBudget(512));
        Assert.True(arena.TryScratch<int>(2, static writer => writer.Fill(42), out ArenaLease<int> ordinary));
        Assert.True(arena.TryScratchScoped<int>(2, static writer => writer.Fill(7), out ArenaLease<int> scoped));
        arena.RecycleScoped();
        AssertStale(scoped);
        Assert.Equal(42, ordinary.Read(static view => view[1]));
        Assert.True(arena.TryScratchScoped<int>(2, static writer => writer.Fill(9), out ArenaLease<int> replacement));
        Assert.Equal(9, replacement.Read(static view => view[1]));
        arena.Reset();
        AssertStale(ordinary);
        AssertStale(replacement);
    }

    [Fact]
    public void ExistingScratchCannotGrowAPreparedArena()
    {
        NativeMemoryBudget budget = new(512);
        using NativeArena arena = new(new NativeArenaPreparation(8, 0), budget);
        ArenaLease<int> full = arena.Scratch<int>(2, static writer => writer.Fill(42));
        long allocations = budget.CaptureStatistics().AllocationCount;
        Assert.Throws<InvalidOperationException>(() => arena.Scratch<int>(1, static writer => writer.Fill(7)));
        Assert.Equal(allocations, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(42, full.Read(static view => view[0]));
        Assert.Equal(1, arena.CapturePreparedSnapshot().RejectedCapacityCount);
    }

    private static void AssertStale(scoped in ArenaLease<int> lease)
    {
        try
        {
            lease.Clear();
            Assert.Fail("A stale prepared arena lease must not access reused backing.");
        }
        catch (NativeAllocationReturnedException)
        {
        }
    }
}
