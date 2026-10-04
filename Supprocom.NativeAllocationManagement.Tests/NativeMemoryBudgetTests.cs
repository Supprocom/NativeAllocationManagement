using System.Runtime.CompilerServices;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeMemoryBudgetTests
{
    private static readonly int[] ExpectedPrefix = [1, 2, 3, 4, 5];

    [Fact]
    public void KeepingTheBudgetAliveDoesNotPreventAbandonedOwnerCleanup()
    {
        NativeMemoryBudget budget = new(128);
        WeakReference builder = CreateAbandonedOwner(budget, workspace: false);
        WeakReference workspace = CreateAbandonedOwner(budget, workspace: true);
        Assert.Equal(128, budget.CaptureStatistics().CommittedBytes);
        for (int attempt = 0; attempt < 8 && budget.CaptureStatistics().CommittedBytes != 0; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(builder.IsAlive);
        Assert.False(workspace.IsAlive);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ActiveAllocationCount);
        Assert.Equal(2, budget.CaptureStatistics().FreeCount);
        GC.KeepAlive(budget);
    }

    [Fact]
    public void PublishedReallocCountersMatchActualCallsAndExtentChanges()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativeBuilder<int> builder = new(preLease: 1);
            builder.Append(1);
            NativeMemoryStatistics allocated = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(1, allocated.AllocationCount);
            Assert.Equal(0, allocated.ReallocationCount);
            Assert.Equal(0, allocated.FreeCount);
            Assert.Equal(4, allocated.OutstandingNativeBytes);
            builder.Append(2);
            NativeMemoryStatistics resized = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(1, resized.AllocationCount);
            Assert.Equal(1, resized.ReallocationCount);
            Assert.Equal(0, resized.FreeCount);
            Assert.Equal(8, resized.OutstandingNativeBytes);
            Assert.Equal(8, resized.PeakOutstandingNativeBytes);
            NativeTransfer<int> transfer = builder.Complete();
            transfer.Dispose();
            NativeMemoryStatistics freed = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(1, freed.AllocationCount);
            Assert.Equal(1, freed.ReallocationCount);
            Assert.Equal(1, freed.FreeCount);
            Assert.Equal(0, freed.OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "This helper deliberately abandons owners to prove finalizer cleanup while the shared budget remains alive.")]
    private static WeakReference CreateAbandonedOwner(NativeMemoryBudget budget, bool workspace) =>
        workspace
            ? new WeakReference(new NativeWorkspace<int>(budget, preLease: 16))
            : new WeakReference(new NativeBuilder<int>(budget, preLease: 16));
    [Fact]
    public void GrowthPreferenceFallsBackWithoutInventingRejectedAllocations()
    {
        NativeMemoryBudget budget = new(64);
        budget.Reserve(16);
        budget.Commit(16);
        Assert.True(budget.TryReservePreferred(80, 48, out nuint admitted, out long available));
        Assert.Equal((nuint)48, admitted);
        Assert.Equal(48, available);
        Assert.Equal(0, budget.CaptureStatistics().RejectedAllocationCount);
        budget.CommitReallocation(admitted, previousByteLength: 16);
        Assert.Equal(48, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(64, budget.CaptureStatistics().PeakAdmittedBytes);
        budget.Release(48);
    }

    [Fact]
    public void WorkspaceAndBuilderCompeteForBackingNotLogicalLength()
    {
        NativeMemoryBudget budget = new(64);
        using NativeWorkspace<int> workspace = new(budget, preLease: 8);
        using NativeBuilder<int> builder = new(budget, preLease: 4);
        builder.Append([1, 2, 3, 4]);
        Assert.Equal(48, budget.CaptureStatistics().CommittedBytes);
        Assert.False(builder.TryEnsureCapacity(5));
        Assert.Equal(4, builder.Count);
        Assert.Equal(4, builder.Capacity);
        Assert.Equal(48, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        workspace.Dispose();
        Assert.True(builder.TryEnsureCapacity(5));
        builder.Append(5);
        NativeTransfer<int> transfer = builder.Complete();
        Assert.Equal(32, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().ActiveAllocationCount);
        Assert.Equal(ExpectedPrefix, transfer.Read(static view => view.AsSpan().ToArray()));
        transfer.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ActiveAllocationCount);
    }

    [Fact]
    public void TightGrowthCapUsesExactCapacityAndAdmitsOldPlusNew()
    {
        NativeMemoryBudget budget = new(36);
        using NativeBuilder<int> builder = new(budget, preLease: 4);
        builder.Append([1, 2, 3, 4]);
        Assert.True(builder.TryEnsureCapacity(5));
        Assert.Equal(5, builder.Capacity);
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(20, snapshot.CommittedBytes);
        Assert.Equal(36, snapshot.PeakAdmittedBytes);
        Assert.Equal(0, snapshot.RejectedAllocationCount);
        Assert.Equal(1, snapshot.ReallocationCount);
        builder.Append(5);
        NativeTransfer<int> transfer = builder.Complete();
        Assert.Equal(ExpectedPrefix, transfer.Read(static view => view.AsSpan().ToArray()));
        transfer.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void CompletedAndMovedTransferKeepsItsFullBackingCharge()
    {
        NativeMemoryBudget budget = new(64);
        using NativeBuilder<int> builder = new(budget, preLease: 16);
        builder.Append(42);
        NativeTransfer<int>? source = builder.Complete();
        builder.Dispose();
        Assert.Equal(64, budget.CaptureStatistics().CommittedBytes);
        NativeTransfer<int> destination = NativeTransfer<int>.Move(ref source);
        Assert.Null(source);
        Assert.Equal(1, destination.Length);
        Assert.Equal(16, destination.Capacity);
        Assert.Equal(42, destination.Read(static view => view[0]));
        Assert.Equal(64, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        destination.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void ExpectedBuilderExhaustionDoesNotAllocateOrDestroyThePrefix()
    {
        NativeMemoryBudget budget = new(16);
        using NativeBuilder<int> builder = new(budget, preLease: 4);
        builder.Append(42);
        _ = builder.TryEnsureCapacity(5);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int attempt = 0; attempt < 1_024; attempt++)
        {
            Assert.False(builder.TryEnsureCapacity(5));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(1, builder.Count);
        Assert.Equal(4, builder.Capacity);
        Assert.Equal(1_025, budget.CaptureStatistics().RejectedAllocationCount);
        NativeTransfer<int> transfer = builder.Complete();
        Assert.Equal(42, transfer.Read(static view => view[0]));
        transfer.Dispose();
    }

    [Fact]
    public void RefusalHappensBeforeBuilderProducerAndKeepsTerminalFailurePolicy()
    {
        NativeMemoryBudget budget = new(0);
        using NativeBuilder<int> builder = new(budget, preLease: 0);
        bool produced = false;
        Assert.Throws<NativeMemoryBudgetExceededException>(() => builder.Write(1, writer =>
        {
            produced = true;
            writer.AsSpan()[0] = 1;
            writer.Commit(1);
        }));
        Assert.False(produced);
        Assert.Throws<ObjectDisposedException>(() => builder.Append(2));
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().RejectedAllocationCount);
    }

    [Fact]
    public void NativeFailureRollsBackTheReservationAndReleasesTheAbortedBuilder()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeMemoryBudget budget = new(128);
            using NativeBuilder<int> builder = new(budget, preLease: 4);
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => builder.TryEnsureCapacity(8));
            NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
            Assert.Equal(0, snapshot.CommittedBytes);
            Assert.Equal(0, snapshot.ReservedBytes);
            Assert.Equal(0, snapshot.ActiveAllocationCount);
            Assert.Equal(1, snapshot.AllocationCount);
            Assert.Equal(0, snapshot.ReallocationCount);
            Assert.Equal(1, snapshot.FreeCount);
            Assert.Equal(1, snapshot.FailedAllocationCount);
            Assert.Equal(0, NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void WorkspaceRefusalDoesNotConsumeNativeFailureInjection()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeMemoryBudget budget = new(0);
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeMemoryBudgetExceededException>(() => new NativeWorkspace<int>(budget, preLease: 1));
            NativeMemoryBudgetStatistics rejected = budget.CaptureStatistics();
            Assert.Equal(1, rejected.RejectedAllocationCount);
            Assert.Equal(0, rejected.FailedAllocationCount);
            Assert.Equal(0, rejected.AllocationCount);
            Assert.Throws<NativeAllocationFailedException>(() => new NativeWorkspace<int>(preLease: 1));
            Assert.Equal(0, NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void ReservationAdmissionAndPhysicalReleaseReconcileExactly()
    {
        NativeMemoryBudget budget = new(128);
        Assert.True(budget.Id > 0);
        Assert.True(budget.TryReserve(80, out long available));
        Assert.Equal(128, available);
        NativeMemoryBudgetStatistics admitted = budget.CaptureStatistics();
        Assert.Equal(0, admitted.CommittedBytes);
        Assert.Equal(80, admitted.ReservedBytes);
        Assert.Equal(0, admitted.PeakCommittedBytes);
        Assert.Equal(80, admitted.PeakAdmittedBytes);
        budget.Commit(80);
        Assert.False(budget.TryReserve(49, out available));
        Assert.Equal(48, available);
        budget.Reserve(48);
        budget.Commit(48);
        NativeMemoryBudgetStatistics full = budget.CaptureStatistics();
        Assert.Equal(budget.Id, full.Id);
        Assert.Equal(128, full.CapacityBytes);
        Assert.Equal(128, full.CommittedBytes);
        Assert.Equal(0, full.ReservedBytes);
        Assert.Equal(128, full.PeakCommittedBytes);
        Assert.Equal(128, full.PeakAdmittedBytes);
        Assert.Equal(2, full.AllocationCount);
        Assert.Equal(2, full.ActiveAllocationCount);
        Assert.Equal(1, full.RejectedAllocationCount);
        Assert.Equal(0, full.ReallocationCount);
        Assert.Equal(0, full.FreeCount);
        Assert.Equal(0, full.FailedAllocationCount);
        budget.Release(80);
        budget.Release(48);
        NativeMemoryBudgetStatistics released = budget.CaptureStatistics();
        Assert.Equal(0, released.CommittedBytes);
        Assert.Equal(0, released.ActiveAllocationCount);
        Assert.Equal(2, released.FreeCount);
        Assert.Equal(full.PeakCommittedBytes, released.PeakCommittedBytes);
        Assert.Equal(full.PeakAdmittedBytes, released.PeakAdmittedBytes);
        Assert.Equal(full.RejectedAllocationCount, released.RejectedAllocationCount);
    }

    [Fact]
    public void FailedAdmissionIsNonAllocatingAndDoesNotConsumeCapacity()
    {
        NativeMemoryBudget budget = new(0);
        _ = budget.TryReserve(1, out _);
        _ = budget.CaptureStatistics();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int attempt = 0; attempt < 1_024; attempt++)
        {
            Assert.False(budget.TryReserve(1, out _));
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(1_025, snapshot.RejectedAllocationCount);
        Assert.Equal(0, snapshot.CommittedBytes);
        Assert.Equal(0, snapshot.ReservedBytes);
        Assert.Equal(0, snapshot.PeakAdmittedBytes);
        Assert.Equal(0, snapshot.AllocationCount);
        Assert.Equal(0, snapshot.ActiveAllocationCount);
    }

    [Fact]
    public void ThrowingRejectionContainsTheActualAdmissionDecision()
    {
        NativeMemoryBudget budget = new(32);
        budget.Reserve(24);
        NativeMemoryBudgetExceededException exception = Assert.Throws<NativeMemoryBudgetExceededException>(
            () => budget.Reserve(9));
        Assert.Equal(budget.Id, exception.BudgetId);
        Assert.Equal(32, exception.CapacityBytes);
        Assert.Equal((nuint)9, exception.RequestedBytes);
        Assert.Equal(8, exception.AvailableBytes);
        budget.Cancel(24);
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(0, snapshot.ReservedBytes);
        Assert.Equal(0, snapshot.CommittedBytes);
        Assert.Equal(1, snapshot.FailedAllocationCount);
        Assert.Equal(1, snapshot.RejectedAllocationCount);
        Assert.Equal(24, snapshot.PeakAdmittedBytes);
    }

    [Fact]
    public void GrowthAdmitsFullOverlapWithoutInventingReallocPhysicalPeaks()
    {
        NativeMemoryBudget budget = new(96);
        budget.Reserve(32);
        budget.Commit(32);
        Assert.False(budget.TryReserve(65, out _));
        budget.Reserve(64);
        budget.CommitReallocation(64, previousByteLength: 32);
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(64, snapshot.CommittedBytes);
        Assert.Equal(0, snapshot.ReservedBytes);
        Assert.Equal(64, snapshot.PeakCommittedBytes);
        Assert.Equal(96, snapshot.PeakAdmittedBytes);
        Assert.Equal(1, snapshot.AllocationCount);
        Assert.Equal(1, snapshot.ReallocationCount);
        Assert.Equal(1, snapshot.ActiveAllocationCount);
        Assert.Equal(0, snapshot.FreeCount);
        budget.Release(64);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void ReallocationFromNullAndFailedGrowthKeepHonestEventCounts()
    {
        NativeMemoryBudget budget = new(96);
        budget.Reserve(32);
        budget.CommitReallocation(32, previousByteLength: 0);
        budget.Reserve(64);
        budget.Cancel(64);
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(32, snapshot.CommittedBytes);
        Assert.Equal(0, snapshot.ReservedBytes);
        Assert.Equal(1, snapshot.ActiveAllocationCount);
        Assert.Equal(1, snapshot.AllocationCount);
        Assert.Equal(1, snapshot.ReallocationCount);
        Assert.Equal(1, snapshot.FailedAllocationCount);
        budget.Release(32);
    }

    [Fact]
    public void CeilingArithmeticHandlesTheSignedMaximumWithoutOverflow()
    {
        long maximumBytes = IntPtr.Size == 8 ? long.MaxValue : uint.MaxValue;
        NativeMemoryBudget budget = new(maximumBytes);
        nuint maximum = checked((nuint)maximumBytes);
        budget.Reserve(maximum);
        Assert.False(budget.TryReserve(1, out long available));
        Assert.Equal(0, available);
        budget.Commit(maximum);
        Assert.Equal(maximumBytes, budget.CaptureStatistics().CommittedBytes);
        budget.Release(maximum);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void ZeroLengthDoesNotFabricateBackingEvents()
    {
        NativeMemoryBudget budget = new(0);
        budget.Reserve(0);
        budget.Commit(0);
        budget.Cancel(0);
        budget.Release(0);
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(0, snapshot.AllocationCount);
        Assert.Equal(0, snapshot.FreeCount);
        Assert.Equal(0, snapshot.FailedAllocationCount);
        Assert.Equal(0, snapshot.ActiveAllocationCount);
    }

    [Fact]
    public async Task CompetingOwnersCannotOversubscribeTheSharedCeiling()
    {
        NativeMemoryBudget budget = new(3);
        Task[] workers = new Task[8];
        foreach (ref Task worker in workers.AsSpan())
        {
            worker = Task.Run(() =>
            {
                for (int attempt = 0; attempt < 2_048; attempt++)
                {
                    if (!budget.TryReserve(1, out _))
                    {
                        continue;
                    }

                    budget.Commit(1);
                    NativeMemoryBudgetStatistics current = budget.CaptureStatistics();
                    Assert.InRange(current.CommittedBytes + current.ReservedBytes, 1, 3);
                    budget.Release(1);
                }
            });
        }

        await Task.WhenAll(workers);
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(0, snapshot.CommittedBytes);
        Assert.Equal(0, snapshot.ReservedBytes);
        Assert.Equal(0, snapshot.ActiveAllocationCount);
        Assert.Equal(snapshot.AllocationCount, snapshot.FreeCount);
        Assert.InRange(snapshot.PeakAdmittedBytes, 1, 3);
    }
}
