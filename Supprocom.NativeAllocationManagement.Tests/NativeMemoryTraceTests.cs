using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeMemoryTraceTests
{
    [Fact]
    public void DirectOwnerTraceRecordsActualAdmissionRefusalAndRelease()
    {
        NativeMemoryBudget budget = new(16, traceCapacity: 4);
        NativeBuilder<int> builder = new(budget, 2);
        long ownerId = builder.Id;
        try
        {
            Assert.False(builder.TryEnsureCapacity(3));
        }
        finally
        {
            builder.Dispose();
        }
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[4];
        Assert.Equal(4, budget.CopyTraceTo(events));
        Assert.Equal(NativeMemoryTraceKind.Admitted, events[0].Kind);
        Assert.Equal(NativeMemoryTraceKind.Allocated, events[1].Kind);
        Assert.Equal(NativeMemoryTraceKind.Rejected, events[2].Kind);
        Assert.Equal(NativeMemoryTraceKind.Released, events[3].Kind);
        Assert.Equal((nuint)12, events[2].RequestedBytes);
        Assert.Equal(8, events[1].CommittedBytes);
        Assert.Equal(0, events[3].CommittedBytes);
        long sequence = 0;
        long timestamp = 0;
        foreach (ref readonly NativeMemoryTraceEvent entry in events)
        {
            Assert.Equal(++sequence, entry.Sequence);
            Assert.True(entry.TimestampTicks >= timestamp);
            timestamp = entry.TimestampTicks;
            Assert.Equal(budget.Id, entry.BudgetId);
            Assert.Equal(ownerId, entry.OwnerId);
        }
    }

    [Fact]
    public void BoundedRingOverwritesOldestAndShortCopiesReturnNewestSuffix()
    {
        NativeMemoryBudget budget = new(1, traceCapacity: 4);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            using NativeWorkspace<byte> workspace = new(budget, 1);
        }
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(4, snapshot.TraceCapacity);
        Assert.Equal(4, snapshot.TraceCount);
        Assert.Equal(5, snapshot.DroppedTraceEventCount);
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[4];
        Assert.Equal(4, budget.CopyTraceTo(events));
        Assert.Equal(6, events[0].Sequence);
        Assert.Equal(9, events[3].Sequence);
        Span<NativeMemoryTraceEvent> newest = stackalloc NativeMemoryTraceEvent[2];
        Assert.Equal(2, budget.CopyTraceTo(newest));
        Assert.Equal(8, newest[0].Sequence);
        Assert.Equal(9, newest[1].Sequence);
        Assert.Equal(snapshot, budget.CaptureStatistics());
    }

    [Fact]
    public void DisabledTracingConstructsNoEventsOrManagedExpectedExhaustionState()
    {
        NativeMemoryBudget budget = new(16);
        using NativeBuilder<int> builder = new(budget, 4);
        Assert.False(builder.TryEnsureCapacity(5));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool admitted = false;
        for (int attempt = 0; attempt < 1024; attempt++)
        {
            admitted |= builder.TryEnsureCapacity(5);
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(admitted);
        Assert.Equal(0, allocatedBytes);
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(0, snapshot.TraceCapacity);
        Assert.Equal(0, snapshot.TraceCount);
        Assert.Equal(0, snapshot.DroppedTraceEventCount);
        Assert.Equal(0, budget.CopyTraceTo(Span<NativeMemoryTraceEvent>.Empty));
    }

    [Fact]
    public void ReallocAndTransferMoveKeepTheSameBackingLineage()
    {
        NativeMemoryBudget budget = new(64, traceCapacity: 8);
        using NativeBuilder<int> builder = new(budget, 0);
        long id = builder.Id;
        Assert.True(builder.TryEnsureCapacity(4));
        builder.Append(42);
        NativeTransfer<int>? transfer = builder.Complete();
        NativeTransfer<int> moved = NativeTransfer<int>.Move(ref transfer);
        try
        {
            Assert.Null(transfer);
            Assert.Equal(id, moved.Id);
            Assert.Equal(42, moved.Read(static view => view[0]));
        }
        finally
        {
            moved.Dispose();
        }
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[8];
        int count = budget.CopyTraceTo(events);
        Assert.Equal(5, count);
        Assert.Equal(NativeMemoryTraceKind.Reallocated, events[1].Kind);
        Assert.Equal((nuint)0, events[1].PreviousBytes);
        Assert.Equal(NativeMemoryTraceKind.Moved, events[2].Kind);
        Assert.Equal(NativeMemoryTraceKind.UniqueReturned, events[4].Kind);
        foreach (ref readonly NativeMemoryTraceEvent entry in events[..count])
        {
            Assert.Equal(id, entry.OwnerId);
        }
    }

    [Fact]
    public void EmptyCompletionAndMoveKeepIdentityWithoutFabricatingBackingEvents()
    {
        NativeMemoryBudget budget = new(0, traceCapacity: 1);
        using NativeBuilder<int> builder = new(budget, 0);
        long id = builder.Id;
        NativeTransfer<int>? transfer = builder.Complete();
        Assert.Equal(id, transfer.Value.Id);
        NativeTransfer<int> moved = NativeTransfer<int>.Move(ref transfer);
        try
        {
            Assert.Null(transfer);
            Assert.Equal(id, moved.Id);
            Assert.Equal(0, moved.Length);
            Assert.Equal(0, moved.Read(static view => view.Length));
        }
        finally
        {
            moved.Dispose();
        }
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(0, snapshot.CommittedBytes);
        Assert.Equal(0, snapshot.ReservedBytes);
        Assert.Equal(0, snapshot.AllocationCount);
        Assert.Equal(0, snapshot.FreeCount);
        Assert.Equal(1, snapshot.TraceCount);
        Assert.Equal(1, snapshot.DroppedTraceEventCount);
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[1];
        Assert.Equal(1, budget.CopyTraceTo(events));
        Assert.Equal(NativeMemoryTraceKind.UniqueReturned, events[0].Kind);
        Assert.Equal((nuint)0, events[0].RequestedBytes);
    }

    [Fact]
    public void FastPoolInitializationProbeReportsTheActualActiveState()
    {
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        int observed = 0;
        using Pooled<int> lease = pool.Rent(1, writer =>
        {
            observed = pool.CurrentInitializationCountForTest;
            Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
            writer.Write(42);
        });
        Assert.Equal(1, observed);
        Assert.Equal(0, pool.CurrentInitializationCountForTest);
        lease.Access(_ =>
        {
            Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
            using Pooled<int> nested = pool.Rent(1, writer =>
            {
                Assert.Equal(2, pool.CurrentGenerationActiveOperationsForTest);
                writer.Write(7);
            });
            nested.Access(_ => Assert.Equal(2, pool.CurrentGenerationActiveOperationsForTest));
            Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
        });
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
    }

    [Fact]
    public void OwnerSnapshotsKeepIdentityAcrossGenerationChangesAndDisposal()
    {
        using NativeConcurrentArena first = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentArena second = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        long id = first.Id;
        Assert.True(id > 0);
        Assert.NotEqual(id, second.Id);
        Assert.Equal(id, first.GetStatistics().OwnerId);
        Assert.Equal(id, first.CaptureDiagnosticSnapshot().OwnerId);
        first.ReleaseLeasesToNativeMemory();
        Assert.Equal(id, first.Id);
        Assert.Equal(id, first.CaptureDiagnosticSnapshot().OwnerId);
        first.Dispose();
        Assert.Equal(id, first.CaptureDiagnosticSnapshot().OwnerId);
    }

    [Fact]
    public void EnabledTraceCopyAndRepeatedSnapshotDoNotAllocate()
    {
        NativeMemoryBudget budget = new(1, traceCapacity: 4);
        using NativeWorkspace<byte> workspace = new(budget, 1);
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[4];
        _ = budget.CopyTraceTo(events);
        _ = budget.CaptureStatistics();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1024; iteration++)
        {
            _ = budget.CopyTraceTo(events);
            _ = budget.CaptureStatistics();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void NativeAcquisitionFailureIsTracedSeparatelyFromCeilingRefusal()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeMemoryBudget budget = new(128, traceCapacity: 8);
            using NativeBuilder<int> builder = new(budget, 4);
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => builder.TryEnsureCapacity(8));
            Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[8];
            Assert.Equal(5, budget.CopyTraceTo(events));
            Assert.Equal(NativeMemoryTraceKind.Admitted, events[2].Kind);
            Assert.Equal(NativeMemoryTraceKind.AcquisitionFailed, events[3].Kind);
            Assert.Equal(16, events[3].CommittedBytes);
            Assert.Equal(0, events[3].ReservedBytes);
            Assert.Equal(NativeMemoryTraceKind.Released, events[4].Kind);
            Assert.Equal(0, events[4].CommittedBytes);
            foreach (ref readonly NativeMemoryTraceEvent entry in events[..5])
            {
                Assert.Equal(builder.Id, entry.OwnerId);
            }
            Assert.Equal(1, budget.CaptureStatistics().FailedAllocationCount);
            Assert.Equal(0, budget.CaptureStatistics().RejectedAllocationCount);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void TraceSequenceExhaustionCannotBreakAllocationOrRelease()
    {
        NativeMemoryBudget budget = new(1, traceCapacity: 1);
        SetTraceCounter(budget, "_traceSequence", long.MaxValue);
        using (NativeWorkspace<byte> workspace = new(budget, 1))
        {
            Assert.Equal(1, budget.CaptureStatistics().CommittedBytes);
        }
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.True(snapshot.TraceOverflowed);
        Assert.Equal(3, snapshot.DroppedTraceEventCount);
        Assert.Equal(0, snapshot.TraceCount);
        Assert.Equal(0, snapshot.CommittedBytes);
        Assert.Equal(1, snapshot.FreeCount);
    }

    [Fact]
    public void DroppedEventCounterSaturatesAndDisclosesLostExactness()
    {
        NativeMemoryBudget budget = new(1, traceCapacity: 1);
        SetTraceCounter(budget, "_droppedTraceEventCount", long.MaxValue);
        using (NativeWorkspace<byte> workspace = new(budget, 1))
        {
            Assert.True(budget.CaptureStatistics().TraceOverflowed);
        }
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.True(snapshot.TraceOverflowed);
        Assert.Equal(long.MaxValue, snapshot.DroppedTraceEventCount);
        Assert.Equal(0, snapshot.CommittedBytes);
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[1];
        Assert.Equal(1, budget.CopyTraceTo(events));
        Assert.Equal(3, events[0].Sequence);
        Assert.Equal(NativeMemoryTraceKind.Released, events[0].Kind);
    }

    [Fact]
    public void FastPoolMetadataCapacitiesDoNotPretendToBeUsageCounts()
    {
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        var before = pool.CurrentBankCapacitiesForTest;
        Assert.True(before.Slabs > 0);
        Assert.Equal(before.Slabs, before.AvailableSlabs);
        Assert.Equal(0, pool.GetStatistics().SegmentCount);
        using Pooled<int> lease = pool.Rent(1, static writer => writer.Write(42));
        Assert.Equal(before, pool.CurrentBankCapacitiesForTest);
        Assert.Equal(1, pool.GetStatistics().SegmentCount);
    }

    [Fact]
    public void FastOwnerIdentitiesAreDistinctAndCarriedIntoStorageSnapshots()
    {
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeRegion region = new();
        using NativeWorkspace<int> workspace = new(1);
        Assert.True(pool.Id > 0);
        Assert.True(arena.Id > pool.Id);
        Assert.True(region.Id > arena.Id);
        Assert.True(workspace.Id > region.Id);
        Assert.Equal(pool.Id, pool.GetStatistics().OwnerId);
        Assert.Equal(arena.Id, arena.GetStatistics().OwnerId);
        Assert.Equal(region.Id, region.GetStatistics().OwnerId);
    }

    private static void SetTraceCounter(NativeMemoryBudget budget, string name, long value)
    {
        FieldInfo field = typeof(NativeMemoryBudget).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(budget, value);
    }
}
