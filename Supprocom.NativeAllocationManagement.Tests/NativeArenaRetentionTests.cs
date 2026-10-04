using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeArenaRetentionTests
{
    [Fact]
    public void BothIdleLanesUseOneCeilingAndKeepTheFittingOrdinaryPrefix()
    {
        NativeMemoryBudget budget = new(16_384);
        using NativeArena arena = new(budget, new NativeArenaRetentionPolicy(4_096, 4_160), 4_096, NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> ordinary = arena.Scratch<int>(1, static writer => writer.Write(17));
        ArenaLease<int> scoped = arena.ScratchScoped<int>(1, static writer => writer.Write(23));
        Assert.Equal(17, ordinary.Read(static view => view[0]));
        Assert.Equal(23, scoped.Read(static view => view[0]));
        Assert.Equal(8_320, arena.CaptureRetentionSnapshot().RetainedBytes);
        arena.Reset();
        NativeArenaRetentionStatistics snapshot = arena.CaptureRetentionSnapshot();
        Assert.Equal(4_160, snapshot.IdleBytes);
        Assert.Equal(4_160, snapshot.RetainedBytes);
        Assert.Equal(4_160, snapshot.ReleasedBytes);
        Assert.Equal(0, snapshot.OversizedBytes);
        Assert.Equal(0, snapshot.PeakOversizedBytes);
        ArenaLease<int> reused = arena.Scratch<int>(1, static writer => writer.Write(29));
        Assert.Equal(29, reused.Read(static view => view[0]));
        Assert.Equal(2, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void MaintenanceHistoryOverflowCannotWrapOrPreventARealRelease()
    {
        NativeMemoryBudget budget = new(512);
        using NativeArena arena = new(budget, new NativeArenaRetentionPolicy(256, 0), 256, NativeMemoryReturn.ToNativeMemory);
        object kernel = typeof(NativeArena).GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(arena)!;
        kernel.GetType().GetField("_retentionMaintenanceCount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(kernel, long.MaxValue);
        Assert.False(arena.CaptureRetentionSnapshot().HistoryOverflowed);
        arena.Reset();
        NativeArenaRetentionStatistics snapshot = arena.CaptureRetentionSnapshot();
        Assert.Equal(long.MaxValue, snapshot.MaintenanceCount);
        Assert.Equal(320, snapshot.ReleasedBytes);
        Assert.True(snapshot.HistoryOverflowed);
        Assert.Equal(0, snapshot.RetainedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void NormalOutlierNormalPreservesTheFittingNormalWorkingSet()
    {
        NativeMemoryBudget budget = new(2_000_000);
        using NativeArena arena = new(budget, new NativeArenaRetentionPolicy(4_096, 4_160), 0, NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> normal = arena.Scratch<int>(1, static writer => writer.Write(17));
        ArenaLease<byte> outlier = arena.Scratch<byte>(65_536, static writer => writer.Fill(23));
        Assert.Equal(17, normal.Read(static view => view[0]));
        Assert.Equal(23, outlier.Read(static view => view[65_535]));
        NativeArenaRetentionStatistics live = arena.CaptureRetentionSnapshot();
        Assert.Equal(65_600, live.OversizedBytes);
        Assert.Equal(65_600, live.PeakOversizedBytes);
        Assert.Equal(0, live.IdleBytes);
        arena.Reset();
        NativeArenaRetentionStatistics reset = arena.CaptureRetentionSnapshot();
        Assert.Equal(4_160, reset.RetainedBytes);
        Assert.Equal(4_160, reset.IdleBytes);
        Assert.Equal(0, reset.OversizedBytes);
        Assert.Equal(65_600, reset.ReleasedBytes);
        Assert.Equal(1, reset.MaintenanceCount);
        long acquired = budget.CaptureStatistics().AllocationCount;
        ArenaLease<int> next = arena.Scratch<int>(1, static writer => writer.Write(29));
        Assert.Equal(29, next.Read(static view => view[0]));
        Assert.Equal(acquired, budget.CaptureStatistics().AllocationCount);
        bool stale = false;
        try { normal.Clear(); }
        catch (NativeAllocationReturnedException) { stale = true; }
        Assert.True(stale);
    }

    [Fact]
    public void OutlierDoesNotBecomeTheNextOrdinaryGrowthBasis()
    {
        NativeMemoryBudget budget = new(2_000_000);
        using NativeArena arena = new(budget, new NativeArenaRetentionPolicy(4_096, 8_320), 0, NativeMemoryReturn.ToNativeMemory);
        ArenaLease<byte> outlier = arena.Scratch<byte>(65_536, static writer => writer.Fill(1));
        ArenaLease<int> normal = arena.Scratch<int>(1, static writer => writer.Write(42));
        Assert.Equal(42, normal.Read(static view => view[0]));
        Assert.Equal(69_760, arena.CaptureRetentionSnapshot().RetainedBytes);
        Assert.Equal(65_600, arena.CaptureRetentionSnapshot().OversizedBytes);
        Assert.Equal(1, outlier.Read(static view => view[65_535]));
        arena.Reset();
        Assert.Equal(4_160, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, arena.CaptureRetentionSnapshot().OversizedBytes);
    }

    [Fact]
    public void ScopedMaintenanceCannotEvictOccupiedOrdinaryStorage()
    {
        NativeMemoryBudget budget = new(2_000_000);
        using NativeArena arena = new(budget, new NativeArenaRetentionPolicy(4_096, 0), 0, NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> ordinary = arena.Scratch<int>(1, static writer => writer.Write(42));
        ArenaLease<byte> scoped = arena.ScratchScoped<byte>(65_536, static writer => writer.Fill(7));
        Assert.Equal(7, scoped.Read(static view => view[0]));
        arena.RecycleScoped();
        Assert.Equal(42, ordinary.Read(static view => view[0]));
        Assert.Equal(4_160, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, arena.CaptureRetentionSnapshot().IdleBytes);
        arena.Reset();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, arena.CaptureRetentionSnapshot().RetainedBytes);
        Assert.Equal(2, arena.CaptureRetentionSnapshot().MaintenanceCount);
    }

    [Fact]
    public void ExactReservationAndTighterGrowthReconcileAtTheNativeCap()
    {
        NativeMemoryBudget budget = new(512);
        using NativeArena arena = new(budget, new NativeArenaRetentionPolicy(4_096, 0), 0, NativeMemoryReturn.ToNativeMemory);
        ArenaLease<byte> exact = arena.Scratch<byte>(448, static writer => writer.Fill(42));
        Assert.Equal(42, exact.Read(static view => view[447]));
        Assert.Equal(512, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().RejectedAllocationCount);
        Assert.Throws<NativeMemoryBudgetExceededException>(() => arena.Scratch<int>(1, static writer => writer.Write(1)));
        Assert.Equal(42, exact.Read(static view => view[447]));
        arena.Reset();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(512, arena.CaptureRetentionSnapshot().ReleasedBytes);
    }

    [Fact]
    public void IncompleteOutlierRemainsChargedUntilActualIdleMaintenance()
    {
        NativeMemoryBudget budget = new(2_000_000);
        using NativeArena arena = new(budget, new NativeArenaRetentionPolicy(4_096, 4_160), 0, NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> ordinary = arena.Scratch<int>(1, static writer => writer.Write(17));
        Assert.Throws<InvalidOperationException>(() => arena.Scratch<byte>(65_536, static writer => writer.Write(1)));
        Assert.Equal(4, arena.GetStatistics().RequestedBytes);
        Assert.Equal(65_600, arena.CaptureRetentionSnapshot().OversizedBytes);
        Assert.Equal((nuint)65_600, arena.MaintainRetention());
        Assert.Equal(17, ordinary.Read(static view => view[0]));
        Assert.Equal(4_160, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(65_600, arena.GetStatistics().TrimmedBytes);
    }

    [Fact]
    public void EnteredBorrowRejectsMaintenanceBeforePhysicalRelease()
    {
        NativeMemoryBudget budget = new(512);
        using NativeArena arena = new(budget, new NativeArenaRetentionPolicy(256, 0), 256, NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> value = arena.Scratch<int>(1, static writer => writer.Write(42));
        value.Access(view =>
        {
            Assert.Throws<NativeAllocationInUseException>(() => arena.MaintainRetention());
            Assert.Throws<NativeAllocationInUseException>(arena.Reset);
            Assert.Equal(42, view[0]);
            Assert.Equal(0, arena.CaptureRetentionSnapshot().MaintenanceCount);
        });
        arena.Reset();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void SustainedOutliersKeepBoundedRetentionWithoutNormalChurn()
    {
        NativeMemoryBudget budget = new(2_000_000);
        using NativeArena arena = new(budget, new NativeArenaRetentionPolicy(4_096, 4_160), 4_096, NativeMemoryReturn.ToNativeMemory);
        for (int iteration = 0; iteration < 20; iteration++)
        {
            ArenaLease<int> normal = arena.Scratch<int>(1, static writer => writer.Write(17));
            ArenaLease<byte> outlier = arena.Scratch<byte>(65_536, static writer => writer.Fill(23));
            Assert.Equal(17, normal.Read(static view => view[0]));
            Assert.Equal(23, outlier.Read(static view => view[65_535]));
            arena.Reset();
            Assert.Equal(4_160, arena.CaptureRetentionSnapshot().IdleBytes);
        }
        Assert.Equal(21, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(20, budget.CaptureStatistics().FreeCount);
        Assert.Equal(1_312_000, arena.CaptureRetentionSnapshot().ReleasedBytes);
    }

    [Fact]
    public void PolicySnapshotAndWarmResetRequireNoManagedAllocation()
    {
        NativeMemoryBudget budget = new(512);
        using NativeArena arena = new(budget, new NativeArenaRetentionPolicy(256, 320), 256, NativeMemoryReturn.ToNativeMemory);
        NativeLeaseInitializer<int> initialize = static writer => writer.Fill(42);
        ArenaLease<int> warm = arena.Scratch<int>(1, initialize);
        Assert.Equal(42, warm.Read(static view => view[0]));
        arena.Reset();
        _ = arena.CaptureRetentionSnapshot();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            _ = arena.Scratch<int>(1, initialize);
            arena.Reset();
            _ = arena.CaptureRetentionSnapshot();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(1_001, arena.CaptureRetentionSnapshot().MaintenanceCount);
    }

    [Fact]
    public void ReleasedHistorySaturatesButActualChargesAndPeaksStayExact()
    {
        NativeMemoryBudget budget = new(512);
        using NativeArena arena = new(budget, new NativeArenaRetentionPolicy(256, 0), 256, NativeMemoryReturn.ToNativeMemory);
        object kernel = typeof(NativeArena).GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(arena)!;
        kernel.GetType().GetField("_retentionReleasedBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(kernel, long.MaxValue);
        arena.Reset();
        NativeArenaRetentionStatistics snapshot = arena.CaptureRetentionSnapshot();
        Assert.Equal(long.MaxValue, snapshot.ReleasedBytes);
        Assert.True(snapshot.HistoryOverflowed);
        Assert.Equal(0, snapshot.RetainedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(320, arena.GetStatistics().TrimmedBytes);
    }

    [Fact]
    public void DisabledPolicyHasRealInvariantHistoryAndCannotBeApplied()
    {
        using NativeArena arena = new(256, NativeMemoryReturn.ToNativeMemory);
        NativeArenaRetentionStatistics initial = arena.CaptureRetentionSnapshot();
        Assert.False(initial.Enabled);
        Assert.Equal(default, initial.Policy);
        Assert.Equal(320, initial.IdleBytes);
        arena.Reset();
        Assert.Throws<InvalidOperationException>(() => arena.MaintainRetention());
        NativeArenaRetentionStatistics snapshot = arena.CaptureRetentionSnapshot();
        Assert.Equal(0, snapshot.MaintenanceCount);
        Assert.Equal(0, snapshot.PeakOversizedBytes);
        Assert.Equal(0, snapshot.ReleasedBytes);
    }

    [Fact]
    public void FinalCleanupReleasesCurrentGaugesWithoutInventingPolicyFreeCredit()
    {
        NativeMemoryBudget budget = new(2_000_000);
        NativeArena arena = new(budget, new NativeArenaRetentionPolicy(4_096, 0), 65_536, NativeMemoryReturn.ToNativeMemory);
        Assert.Equal(65_600, arena.CaptureRetentionSnapshot().OversizedBytes);
        arena.Dispose();
        NativeArenaRetentionStatistics closed = arena.CaptureRetentionSnapshot();
        Assert.Equal(NativeOwnerLifecycle.Disposed, closed.Lifecycle);
        Assert.Equal(0, closed.RetainedBytes);
        Assert.Equal(0, closed.IdleBytes);
        Assert.Equal(0, closed.OversizedBytes);
        Assert.Equal(65_600, closed.PeakOversizedBytes);
        Assert.Equal(0, closed.ReleasedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void PolicyTrimTraceRecordsTheRealExtentOwnerAndBackingIdentity()
    {
        NativeMemoryBudget budget = new(2_000_000, 8);
        using NativeArena arena = new(budget, new NativeArenaRetentionPolicy(4_096, 4_160), 4_096, NativeMemoryReturn.ToNativeMemory);
        _ = arena.Scratch<byte>(65_536, static writer => writer.Fill(23));
        arena.Reset();
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[8];
        int count = budget.CopyTraceTo(events);
        Assert.Equal(5, count);
        NativeMemoryTraceEvent actual = events[count - 1];
        Assert.Equal(NativeMemoryTraceKind.Trimmed, actual.Kind);
        Assert.Equal(arena.Id, actual.OwnerId);
        Assert.Equal((nuint)65_600, actual.RequestedBytes);
        Assert.Equal(2, actual.AllocationOrdinal);
    }

    [Fact]
    public void InvalidPolicyCannotAcquireBackingAndThreadConfinementStillApplies()
    {
        NativeMemoryBudget budget = new(512);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeArenaRetentionPolicy(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeArena(budget, default(NativeArenaRetentionPolicy), 256, NativeMemoryReturn.ToNativeMemory));
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        using NativeArena arena = new(budget, new NativeArenaRetentionPolicy(256, 320), 256, NativeMemoryReturn.ToNativeMemory);
        Exception? snapshotFailure = null;
        Exception? maintenanceFailure = null;
        Thread other = new(() =>
        {
            snapshotFailure = Record.Exception(() => arena.CaptureRetentionSnapshot());
            maintenanceFailure = Record.Exception(() => arena.MaintainRetention());
        });
        other.Start();
        Assert.True(other.Join(TimeSpan.FromSeconds(10)));
        Assert.IsType<NativeAllocationStateException>(snapshotFailure);
        Assert.IsType<NativeAllocationStateException>(maintenanceFailure);
        Assert.Equal(0, arena.CaptureRetentionSnapshot().MaintenanceCount);
        Assert.Equal(320, budget.CaptureStatistics().CommittedBytes);
    }
}
