using System.Reflection;
using System.Runtime.CompilerServices;

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

    [Fact]
    public void InitialGenerationZeroRecordsRealCleanupAndDoesNotEnlargeTraceValues()
    {
        NativeMemoryBudget budget = new(128, traceCapacity: 16);
        NativeConcurrentPool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToNativeMemory, false);
        long ownerId = pool.Id;
        long extent = budget.CaptureStatistics().CommittedBytes;
        try { pool.ReturnMemoryToNativeMemory(); }
        finally { pool.Dispose(); }
        NativeMemoryTraceEvent[] events = CaptureTrace(budget);
        NativeMemoryTraceEvent completed = AssertOne(events, entry => entry.Kind == NativeMemoryTraceKind.GenerationReleased);
        Assert.Equal(0, completed.Generation);
        Assert.Equal(0, completed.CorrelationId);
        Assert.Equal(ownerId, completed.OwnerId);
        Assert.Equal((nuint)extent, completed.RequestedBytes);
        Assert.Equal(0, completed.CommittedBytes);
        NativeMemoryTraceEvent freed = AssertOne(events, entry => entry.Kind == NativeMemoryTraceKind.Released);
        Assert.Null(freed.Generation);
        Assert.Null(freed.CorrelationId);
        Assert.True(freed.Sequence < completed.Sequence);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
        // All four supported native matrix cells are 64-bit. The computed
        // generation view reuses the previous nullable correlation storage.
        Assert.Equal(112, Unsafe.SizeOf<NativeMemoryTraceEvent>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<NativeMemoryTraceEvent>());
    }

    [Fact]
    public void SuccessiveGenerationsKeepOwnerLineageAndDoNotReuseGenerationZero()
    {
        NativeMemoryBudget budget = new(128, traceCapacity: 32);
        using NativeConcurrentPool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToNativeMemory, false);
        long ownerId = pool.Id;
        pool.ReturnMemoryToNativeMemory();
        pool.LeaseFromMemory();
        using (ConcurrentPooled<int> lease = pool.Rent(1, static writer => writer.Write(7)))
        {
            Assert.Equal(7, lease.Read(static view => view[0]));
        }
        pool.ReturnMemoryToNativeMemory();
        NativeMemoryTraceEvent[] events = CaptureTrace(budget);
        NativeMemoryTraceEvent first = AssertOne(events, entry =>
            entry.Kind == NativeMemoryTraceKind.GenerationReleased && entry.Generation == 0);
        NativeMemoryTraceEvent second = AssertOne(events, entry =>
            entry.Kind == NativeMemoryTraceKind.GenerationReleased && entry.Generation == 1);
        Assert.Equal(ownerId, first.OwnerId);
        Assert.Equal(ownerId, second.OwnerId);
        Assert.True(first.Sequence < second.Sequence);
        Assert.Equal(0, second.CommittedBytes);
        Assert.Equal(2, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void RetiredGenerationRejoinsWithoutInventingAPhysicalFree()
    {
        NativeMemoryBudget budget = new(128, traceCapacity: 32);
        using NativeConcurrentPool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToNativeMemory, false);
        long extent = budget.CaptureStatistics().CommittedBytes;
        using ConcurrentPooled<int> lease = pool.Rent(1, static writer => writer.Write(42));
        lease.Access(view =>
        {
            pool.ReleaseLeasesToGarbageCollector();
            Assert.Equal(42, view[0]);
            Assert.Equal(extent, pool.CaptureDiagnosticSnapshot().RetiredBytes);
            Assert.Equal(extent, pool.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
            Assert.Equal(extent, pool.CaptureDiagnosticSnapshot().PeakOutstandingNativeBytes);
            Assert.Equal(0, pool.CaptureDiagnosticSnapshot().DetachedNativeBytes);
        });
        NativeMemoryTraceEvent[] events = CaptureTrace(budget);
        NativeMemoryTraceEvent retired = AssertOne(events, entry => entry.Kind == NativeMemoryTraceKind.GenerationRetired);
        NativeMemoryTraceEvent completed = AssertOne(events, entry => entry.Kind == NativeMemoryTraceKind.GenerationReleased);
        Assert.Equal(0, retired.Generation);
        Assert.Equal((nuint)extent, retired.RequestedBytes);
        Assert.Equal(0, completed.Generation);
        Assert.Equal((nuint)0, completed.RequestedBytes);
        Assert.True(retired.Sequence < completed.Sequence);
        Assert.Equal(extent, completed.CommittedBytes);
        Assert.Equal(extent, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().FreeCount);
        Assert.Equal(0, pool.CaptureDiagnosticSnapshot().RetiredBytes);
        Assert.Equal(extent, pool.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
        Assert.Equal(extent, pool.CaptureDiagnosticSnapshot().PeakOutstandingNativeBytes);
        Assert.Equal(0, pool.CaptureDiagnosticSnapshot().DetachedNativeBytes);
        Assert.DoesNotContain(events, entry => entry.Kind == NativeMemoryTraceKind.Released);
    }

    [Fact]
    public void QuarantineRecordsChargedGenerationAndOnlyActualCleanupCompletesIt()
    {
        NativeMemoryTestHooks.Reset();
        NativeMemoryBudget budget = new(128, traceCapacity: 64);
        NativeConcurrentPool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToNativeMemory, false);
        long extent = budget.CaptureStatistics().CommittedBytes;
        try
        {
            using ConcurrentPooled<int> lease = pool.Rent(1, static writer => writer.Write(42));
            NativeAllocationQuarantinedException? failure = null;
            try
            {
                lease.Access(view =>
            {
                pool.ReleaseLeasesToGarbageCollector();
                Assert.Equal(42, view[0]);
                NativeMemoryTestHooks.FailAfterCommitBoundary(1);
            });
            }
            catch (NativeAllocationQuarantinedException exception) { failure = exception; }
            Assert.NotNull(failure);
            NativeMemoryTraceEvent[] events = CaptureTrace(budget);
            NativeMemoryTraceEvent quarantine = AssertOne(events, entry => entry.Kind == NativeMemoryTraceKind.GenerationQuarantined);
            Assert.Equal(0, quarantine.Generation);
            Assert.Equal(pool.Id, quarantine.OwnerId);
            Assert.Equal((nuint)extent, quarantine.RequestedBytes);
            Assert.Equal(extent, quarantine.CommittedBytes);
            Assert.DoesNotContain(events, entry => entry.Kind == NativeMemoryTraceKind.GenerationReleased);
            Assert.Equal(1, pool.CaptureDiagnosticSnapshot().QuarantinedSegmentCount);
            Assert.Equal(extent, pool.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
            Assert.Equal(extent, pool.CaptureDiagnosticSnapshot().PeakOutstandingNativeBytes);
            Assert.Equal(0, pool.CaptureDiagnosticSnapshot().DetachedNativeBytes);
            Assert.Equal(0, budget.CaptureStatistics().FreeCount);
        }
        finally { NativeMemoryTestHooks.Reset(); pool.Dispose(); }
        NativeMemoryTraceEvent completed = AssertOne(CaptureTrace(budget), entry =>
            entry.Kind == NativeMemoryTraceKind.GenerationReleased && entry.Generation == 0);
        Assert.Equal((nuint)extent, completed.RequestedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
        pool.Dispose();
        Assert.Equal(0, pool.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
        Assert.Equal(0, pool.CaptureDiagnosticSnapshot().DetachedNativeBytes);
        Assert.Equal(extent, pool.CaptureDiagnosticSnapshot().PeakOutstandingNativeBytes);
        _ = AssertOne(CaptureTrace(budget), entry =>
            entry.Kind == NativeMemoryTraceKind.GenerationReleased && entry.Generation == 0);
    }

    [Fact]
    public void DetachedGenerationRemainsChargedUntilItsActualFinalizer()
    {
        NativeMemoryBudget budget = new(128, traceCapacity: 32);
        long ownerId = DetachTracedGeneration(budget);
        NativeMemoryTraceEvent detached = AssertOne(CaptureTrace(budget), entry => entry.Kind == NativeMemoryTraceKind.GenerationDetached);
        Assert.Equal(0, detached.Generation);
        Assert.Equal(ownerId, detached.OwnerId);
        Assert.True(detached.RequestedBytes > 0);
        Assert.Equal((long)detached.RequestedBytes, detached.CommittedBytes);
        for (int attempt = 0; attempt < 8 && budget.CaptureStatistics().CommittedBytes != 0; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        NativeMemoryTraceEvent completed = AssertOne(CaptureTrace(budget), entry => entry.Kind == NativeMemoryTraceKind.GenerationReleased);
        Assert.Equal(0, completed.Generation);
        Assert.Equal(ownerId, completed.OwnerId);
        NativeMemoryTraceEvent released = AssertOne(CaptureTrace(budget), entry => entry.Kind == NativeMemoryTraceKind.Released);
        Assert.Equal(detached.RequestedBytes, released.RequestedBytes);
        Assert.Equal(ownerId, released.OwnerId);
        // Segment and generation emergency finalizers have no ordering contract.
        // Generation cleanup observes either its still-owned segment or zero
        // after that segment's independent finalizer already physically freed it.
        Assert.True(completed.RequestedBytes == 0 || completed.RequestedBytes == released.RequestedBytes);
        Assert.True(detached.Sequence < released.Sequence);
        Assert.True(released.Sequence < completed.Sequence);
        Assert.True(detached.Sequence < completed.Sequence);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void DisabledGenerationTracingAddsNeitherPayloadsNorManagedAllocations()
    {
        NativeMemoryBudget budget = new(0);
        budget.RecordGenerationTransition(NativeMemoryTraceKind.GenerationRetired, 1, 0, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_024; iteration++)
        {
            budget.RecordGenerationTransition(NativeMemoryTraceKind.GenerationRetired, 1, 0, 0);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(0, budget.CaptureStatistics().TraceCount);
        Assert.Equal(0, budget.CaptureStatistics().DroppedTraceEventCount);
    }

    [Fact]
    public void GenerationEventsShareTheBoundedRingAndExhaustionCannotBreakCleanup()
    {
        NativeMemoryBudget budget = new(128, traceCapacity: 1);
        using (NativeConcurrentPool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToNativeMemory, false))
        {
            pool.ReturnMemoryToNativeMemory();
        }
        NativeMemoryTraceEvent completed = AssertOne(CaptureTrace(budget));
        Assert.Equal(NativeMemoryTraceKind.GenerationReleased, completed.Kind);
        Assert.Equal(0, completed.Generation);
        Assert.Equal(3, budget.CaptureStatistics().DroppedTraceEventCount);
        SetTraceCounter(budget, "_traceSequence", long.MaxValue);
        using (NativeConcurrentPool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToNativeMemory, false))
        {
            Assert.True(budget.CaptureStatistics().CommittedBytes > 0);
        }
        Assert.True(budget.CaptureStatistics().TraceOverflowed);
        Assert.Equal(7, budget.CaptureStatistics().DroppedTraceEventCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(2, budget.CaptureStatistics().FreeCount);
        Assert.Equal(completed, AssertOne(CaptureTrace(budget)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long DetachTracedGeneration(NativeMemoryBudget budget)
    {
        NativeGenerationOwner? retainedOwner = null;
        NativeMemoryTestHooks.SetOperationEnteredWithGenerationOwner((operation, owner) =>
        {
            if (string.Equals(operation, nameof(ConcurrentPooled<int>.Access), StringComparison.Ordinal))
            {
                retainedOwner = owner;
            }
        });
        try
        {
            using NativeConcurrentPool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToNativeMemory, false);
            using ConcurrentPooled<int> lease = pool.Rent(1, static writer => writer.Write(42));
            lease.Access(static view => Assert.Equal(42, view[0]));
            Assert.NotNull(retainedOwner);
            long id = pool.Id;
            pool.ReturnMemoryToGarbageCollector();
            NativeMemoryTraceEvent detached = AssertOne(CaptureTrace(budget), entry =>
                entry.Kind == NativeMemoryTraceKind.GenerationDetached);
            Assert.Equal((long)detached.RequestedBytes, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(0, budget.CaptureStatistics().FreeCount);
            GC.KeepAlive(retainedOwner);
            return id;
        }
        finally { NativeMemoryTestHooks.SetOperationEnteredWithGenerationOwner(null); }
    }

    private static NativeMemoryTraceEvent[] CaptureTrace(NativeMemoryBudget budget)
    {
        NativeMemoryTraceEvent[] events = new NativeMemoryTraceEvent[64];
        return events[..budget.CopyTraceTo(events)];
    }

    private static NativeMemoryTraceEvent AssertOne(NativeMemoryTraceEvent[] events, Func<NativeMemoryTraceEvent, bool>? predicate = null)
    {
        int matches = 0;
        NativeMemoryTraceEvent result = default;
        foreach (ref readonly NativeMemoryTraceEvent entry in events.AsSpan())
        {
            if (predicate is null || predicate(entry))
            {
                matches++;
                result = entry;
            }
        }
        Assert.Equal(1, matches);
        return result;
    }

    private static void SetTraceCounter(NativeMemoryBudget budget, string name, long value)
    {
        FieldInfo field = typeof(NativeMemoryBudget).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(budget, value);
    }
}
