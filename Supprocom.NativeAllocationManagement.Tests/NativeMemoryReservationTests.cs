using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeMemoryReservationTests
{
    [Fact]
    public void FirstCapacityRefusalPrecedesMetadataAndAllocatesNothing()
    {
        NativeMemoryBudget full = new(0);
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(6);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = full.TryReserve<int>(1, out NativeMemoryReservation<int>? refused, out NativeMemoryAdmissionExhaustionReason reason);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(accepted);
        Assert.Null(refused);
        Assert.Equal(NativeMemoryAdmissionExhaustionReason.NativeByteCapacity, reason);
        Assert.Equal(0, allocated);
        Assert.Equal(0, full.CaptureAdmissionStatistics().LedgerFieldBytes);
        Assert.Equal(1, full.CaptureAdmissionStatistics().RejectedReservationCount);
        Assert.Equal(0, full.CaptureStatistics().ReservedBytes);
        NativeMemoryBudget available = new(4);
        Assert.Throws<InvalidOperationException>(() => available.TryReserve<int>(1, out _, out _));
        Assert.Equal(1, available.CaptureAdmissionStatistics().ControlPreparationFailureCount);
        Assert.Equal(0, available.CaptureAdmissionStatistics().OutstandingReservationCount);
        Assert.Equal(0, available.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, available.CaptureStatistics().AllocationCount);
    }

    [Fact]
    public void VariableSizedPermissionsCompeteWithoutAcquiringPayload()
    {
        NativeMemoryBudget budget = new(20);
        Assert.True(budget.TryReserve<int>(3, out NativeMemoryReservation<int>? first, out _));
        Assert.True(budget.TryReserve<byte>(8, out NativeMemoryReservation<byte>? second, out _));
        Assert.False(budget.TryReserve<byte>(1, out NativeMemoryReservation<byte>? refused, out _));
        Assert.Null(refused);
        Assert.Equal(20, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(2, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
        first.Value.Dispose();
        second.Value.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, budget.CaptureStatistics().FreeCount);
        Assert.Equal(2, budget.CaptureAdmissionStatistics().CancelledReservationCount);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
    }

    [Fact]
    public void PendingPreparedAndObligationPeaksSurviveFailureAndTerminalCleanup()
    {
        NativeMemoryBudget budget = new(20);
        Assert.True(budget.TryReserve<int>(3, out NativeMemoryReservation<int>? first, out _));
        Assert.True(budget.TryReserve<byte>(8, out NativeMemoryReservation<byte>? second, out _));
        try
        {
            Assert.Equal(3, first.Value.CaptureSnapshot().DeclaredLength);
            Assert.Equal(8, second.Value.CaptureSnapshot().DeclaredLength);
            NativeMemoryAdmissionStatistics pending = budget.CaptureAdmissionStatistics();
            Assert.Equal(2, pending.OutstandingReservationCount);
            Assert.Equal(2, pending.PeakOutstandingReservationCount);
            Assert.Equal(20, pending.PendingBytes);
            Assert.Equal(20, pending.PeakPendingBytes);
            Assert.Equal(0, pending.PreparedUnpublishedBytes);
            Assert.Equal(0, pending.PeakPreparedUnpublishedBytes);

            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => first.Value.PrepareBacking());
            NativeMemoryAdmissionStatistics failed = budget.CaptureAdmissionStatistics();
            Assert.Equal(20, failed.PendingBytes);
            Assert.Equal(20, failed.PeakPendingBytes);
            Assert.Equal(0, failed.PeakPreparedUnpublishedBytes);
            Assert.Equal(2, failed.PeakOutstandingReservationCount);
            first.Value.PrepareBacking();
            NativeMemoryAdmissionStatistics partial = budget.CaptureAdmissionStatistics();
            Assert.Equal(8, partial.PendingBytes);
            Assert.Equal(12, partial.PreparedUnpublishedBytes);
            Assert.Equal(12, partial.PeakPreparedUnpublishedBytes);
            second.Value.PrepareBacking();
            Assert.Equal(0, budget.CaptureAdmissionStatistics().PendingBytes);
            Assert.Equal(20, budget.CaptureAdmissionStatistics().PreparedUnpublishedBytes);
            Assert.Equal(20, budget.CaptureAdmissionStatistics().PeakPreparedUnpublishedBytes);
        }
        finally
        {
            first.Value.Dispose();
            second.Value.Dispose();
            NativeMemoryTestHooks.Reset();
        }

        NativeMemoryAdmissionStatistics returned = budget.CaptureAdmissionStatistics();
        Assert.Equal(0, returned.OutstandingReservationCount);
        Assert.Equal(0, returned.PendingBytes);
        Assert.Equal(0, returned.PreparedUnpublishedBytes);
        Assert.Equal(2, returned.PeakOutstandingReservationCount);
        Assert.Equal(20, returned.PeakPendingBytes);
        Assert.Equal(20, returned.PeakPreparedUnpublishedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void MovementAndPreparedActivationReuseOneControlWithoutDoubleAdmission()
    {
        NativeMemoryBudget budget = new(16);
        Assert.True(budget.TryReserve<int>(4, out NativeMemoryReservation<int>? source, out _));
        NativeMemoryReservation<int> stale = source.Value;
        object control = stale.ControlForTest!;
        NativeMemoryReservation<int> current = NativeMemoryReservation<int>.Move(ref source);
        Assert.Null(source);
        Assert.False(stale.CaptureSnapshot().BindingIsActive);
        Assert.Throws<InvalidOperationException>(stale.Dispose);
        current.PrepareBacking();
        Assert.True(current.CaptureSnapshot().BackingIsPrepared);
        Assert.Equal(16, current.CaptureSnapshot().OwnedBackingBytes);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(16, budget.CaptureAdmissionStatistics().PreparedUnpublishedBytes);
        NativeMemoryReservation<int>? activating = current;
        NativeTransfer<int> unique = NativeMemoryReservation<int>.Activate(ref activating, static writer => writer.Fill(42));
        try
        {
            Assert.Null(activating);
            Assert.Same(control, unique.ControlForTest);
            Assert.Equal(0, unique.CaptureSnapshot().MoveCount);
            Assert.Equal(2, unique.CaptureSnapshot().AuthorityVersion);
            Assert.Equal(1, stale.CaptureSnapshot().ReservationMoveCount);
            Assert.Equal(NativeMemoryReservationOutcome.Activated, stale.CaptureSnapshot().Outcome);
            Assert.False(stale.CaptureSnapshot().HasReservationReturnObligation);
            Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
            Assert.Equal(1, budget.CaptureAdmissionStatistics().ActivationCount);
            Assert.Equal(0, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
            Assert.Equal(0, budget.CaptureAdmissionStatistics().PreparedUnpublishedBytes);
            Assert.Equal(42, unique.Read(static view => view[3]));
        }
        finally { unique.Dispose(); }
        Assert.Equal(0, stale.CaptureSnapshot().OwnedBackingBytes);
        Assert.Equal(16, stale.CaptureSnapshot().PeakOwnedBackingBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void WarmReservationMovesAndPreparedActivationAllocateNoSecondControl()
    {
        NativeMemoryBudget budget = new(4);
        Assert.True(budget.TryReserve<int>(1, out NativeMemoryReservation<int>? source, out _));
        NativeMemoryReservation<int> current = NativeMemoryReservation<int>.Move(ref source);
        source = current;
        object control = current.ControlForTest!;
        _ = MeasureReservationMoves(ref source);
        long moveAllocated = MeasureReservationMoves(ref source);
        Assert.NotNull(source);
        current = source.Value;
        Assert.Equal(0, moveAllocated);
        Assert.Equal(20_001, current.CaptureSnapshot().ReservationMoveCount);
        current.PrepareBacking();
        NativeLeaseInitializer<int> initializer = static writer => writer.Write(17);
        NativeTransfer<int> warm = WarmActivation();
        warm.Dispose();
        long before = GC.GetAllocatedBytesForCurrentThread();
        NativeTransfer<int> unique = NativeMemoryReservation<int>.Activate(ref source, initializer);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        try
        {
            Assert.Equal(0, allocated);
            Assert.Same(control, unique.ControlForTest);
            Assert.Equal(17, unique.Read(static view => view[0]));
            Assert.Equal(0, unique.CaptureSnapshot().MoveCount);
        }
        finally { unique.Dispose(); }
    }

    [Fact]
    public void FailedBackingPreparationPreservesPermissionAndItsExactPendingCharge()
    {
        NativeMemoryBudget budget = new(8);
        Assert.True(budget.TryReserve<int>(2, out NativeMemoryReservation<int>? source, out _));
        NativeMemoryTestHooks.FailNextAllocation();
        Assert.Throws<NativeAllocationFailedException>(() => source.Value.PrepareBacking());
        Assert.True(source.Value.CaptureSnapshot().BindingIsActive);
        Assert.Equal(8, source.Value.CaptureSnapshot().ReservedBytes);
        Assert.Equal(0, source.Value.CaptureSnapshot().OwnedBackingBytes);
        Assert.Equal(1, source.Value.CaptureSnapshot().BackingPreparationFailureCount);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().BackendAllocationFailureCount);
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
        source.Value.PrepareBacking();
        source.Value.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void FailedPreparedObservationDoesNotEraseActualAcquisitionOrItsCharge()
    {
        NativeMemoryBudget budget = new(4);
        Assert.True(budget.TryReserve<int>(1, out NativeMemoryReservation<int>? source, out _));
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(7);
        Assert.Throws<InvalidOperationException>(() => source.Value.PrepareBacking());
        Assert.Equal(before.OutstandingNativeBytes + 4, NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes);
        Assert.True(source.Value.CaptureSnapshot().BindingIsActive);
        Assert.True(source.Value.CaptureSnapshot().BackingIsPrepared);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(4, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().BackendAllocationFailureCount);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().BackingPreparationFailureCount);
        source.Value.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void IncompleteInitializationConsumesPermissionAndPublishesNoUniqueOwner()
    {
        NativeMemoryBudget budget = new(8);
        Assert.True(budget.TryReserve<int>(2, out NativeMemoryReservation<int>? source, out _));
        NativeMemoryReservation<int> alias = source.Value;
        Assert.Throws<InvalidOperationException>(() => NativeMemoryReservation<int>.Activate(ref source, static writer => writer.Write(17)));
        Assert.Null(source);
        Assert.Equal(NativeMemoryReservationOutcome.InitializationFailed, alias.CaptureSnapshot().Outcome);
        Assert.False(alias.CaptureSnapshot().HasReservationReturnObligation);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().ActivationCount);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().InitializationFailureCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationConsumesPendingOrPreparedActivationWithoutRunningProducer(bool prepared)
    {
        NativeMemoryBudget budget = new(4);
        Assert.True(budget.TryReserve<int>(1, out NativeMemoryReservation<int>? source, out _));
        if (prepared) source.Value.PrepareBacking();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        bool producerEntered = false;
        Assert.Throws<OperationCanceledException>(() => NativeMemoryReservation<int>.Activate(ref source,
            writer => { producerEntered = true; writer.Write(42); }, cancellation.Token));
        Assert.Null(source);
        Assert.False(producerEntered);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().CancelledReservationCount);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().InitializationFailureCount);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(prepared ? 1 : 0, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void FailedConsumedCleanupStaysChargedAndCanRetryWithoutResurrection()
    {
        NativeMemoryBudget budget = new(4);
        Assert.True(budget.TryReserve<int>(1, out NativeMemoryReservation<int>? source, out _));
        NativeMemoryReservation<int> alias = source.Value;
        AggregateException failure = Assert.Throws<AggregateException>(() => NativeMemoryReservation<int>.Activate(ref source, static _ =>
        {
            NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
            throw new InvalidOperationException("producer failed");
        }));
        Assert.Equal("producer failed", failure.InnerExceptions[0].Message);
        Assert.Null(source);
        Assert.False(alias.CaptureSnapshot().BindingIsActive);
        Assert.True(alias.CaptureSnapshot().HasReservationReturnObligation);
        Assert.Equal(1, alias.CaptureSnapshot().ReturnFailureCount);
        Assert.Equal(4, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().ReturnFailureCount);
        Assert.True(alias.TryCompletePayloadReturn());
        Assert.False(alias.CaptureSnapshot().HasReservationReturnObligation);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
        Assert.Throws<InvalidOperationException>(alias.Dispose);
    }

    [Fact]
    public void ZeroLengthPermissionHasRealMetadataButNoInventedBackingEvents()
    {
        NativeMemoryBudget budget = new(0);
        Assert.True(budget.TryReserve<int>(0, out NativeMemoryReservation<int>? source, out _));
        Assert.Equal(1, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
        source.Value.PrepareBacking();
        Assert.True(source.Value.CaptureSnapshot().BackingIsPrepared);
        using NativeTransfer<int> unique = NativeMemoryReservation<int>.Activate(ref source, static _ => { });
        Assert.Equal(0, unique.Length);
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().FreeCount);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().BackingPreparationCount);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().ActivationCount);
    }

    [Fact]
    public void BudgetDoesNotRootAbandonedPendingOrPreparedControls()
    {
        NativeMemoryBudget budget = new(8);
        WeakReference pending = Abandon(budget, prepared: false);
        WeakReference prepared = Abandon(budget, prepared: true);
        for (int index = 0; index < 8 && (pending.IsAlive || prepared.IsAlive); index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(pending.IsAlive);
        Assert.False(prepared.IsAlive);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
        Assert.Equal(2, budget.CaptureAdmissionStatistics().AbandonedReservationCount);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
        GC.KeepAlive(budget);
    }

    [Fact]
    public void OrdinaryUniqueControlsHaveNoReservationSpecificFields()
    {
        string[] expected = ["_kernel", "_generationState", "_allocationState", "_ownerId", "_backingBytes",
            "_borrowedBacking", "_generation", "_allocationId", "_block", "_length", "_capacity", "_state",
            "_operationAdmission", "_authorityVersion", "_peakBorrows", "_payloadReturned", "_returnFailures", "_historyOverflowed"];
        Assert.Equal(expected.Order(StringComparer.Ordinal),
            typeof(NativeTransferControl<int>).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(static field => field.Name).Order(StringComparer.Ordinal));
        NativeMemoryReservation<int> value = default;
        Assert.Throws<NativeAllocationUninitializedException>(value.Dispose);
        Assert.Throws<NativeAllocationUninitializedException>(() => value.CaptureSnapshot());
    }

    [Fact]
    public async Task ConcurrentGrantsCannotOversubscribeTheCap()
    {
        NativeMemoryBudget budget = new(8);
        NativeMemoryReservation<int>?[] bindings = new NativeMemoryReservation<int>?[8];
        try
        {
            bool[] outcomes = await Task.WhenAll(Enumerable.Range(0, bindings.Length).Select(index => Task.Run(() =>
                budget.TryReserve<int>(1, out bindings[index], out _))));
            Assert.Equal(2, outcomes.Count(static value => value));
            Assert.Equal(8, budget.CaptureStatistics().ReservedBytes);
            Assert.Equal(2, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
            Assert.Equal(6, budget.CaptureAdmissionStatistics().RejectedReservationCount);
        }
        finally
        {
            foreach (NativeMemoryReservation<int>? binding in bindings) binding?.Dispose();
        }
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
    }

    [Fact]
    public void CancellationAfterProducerWritesPublishesNothingAndDoesNotInventInitializationFailure()
    {
        NativeMemoryBudget budget = new(4);
        Assert.True(budget.TryReserve<int>(1, out NativeMemoryReservation<int>? source, out _));
        using CancellationTokenSource cancellation = new();
        Assert.Throws<OperationCanceledException>(() => NativeMemoryReservation<int>.Activate(ref source, writer =>
        {
            writer.Write(42);
            cancellation.Cancel();
        }, cancellation.Token));
        Assert.Null(source);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().ActivationCount);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().InitializationFailureCount);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().CancelledReservationCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void EnteredProducerCannotReenterOrDisposeItsUnpublishedControl()
    {
        NativeMemoryBudget budget = new(4);
        Assert.True(budget.TryReserve<int>(1, out NativeMemoryReservation<int>? source, out _));
        NativeMemoryReservation<int> alias = source.Value;
        using NativeTransfer<int> owner = NativeMemoryReservation<int>.Activate(ref source, writer =>
        {
            Assert.Throws<InvalidOperationException>(alias.Dispose);
            Assert.Throws<InvalidOperationException>(() => alias.PrepareBacking());
            Assert.False(alias.TryCompletePayloadReturn());
            Assert.Equal(1, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
            writer.Write(17);
        });
        Assert.Equal(17, owner.Read(static view => view[0]));
        Assert.Equal(0, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
    }

    [Fact]
    public void FailedOrdinaryPermissionReturnPreservesAuthorityAndPostReturnFailureCannotReviveIt()
    {
        NativeMemoryBudget budget = new(4);
        Assert.True(budget.TryReserve<int>(1, out NativeMemoryReservation<int>? source, out _));
        source.Value.PrepareBacking();
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
        Assert.Throws<InvalidOperationException>(() => source.Value.Dispose());
        Assert.True(source.Value.CaptureSnapshot().BindingIsActive);
        Assert.Equal(NativeMemoryReservationOutcome.Prepared, source.Value.CaptureSnapshot().Outcome);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().CancelledReservationCount);
        Assert.Equal(4, budget.CaptureStatistics().CommittedBytes);
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(5);
        Assert.Throws<InvalidOperationException>(() => source.Value.Dispose());
        Assert.False(source.Value.CaptureSnapshot().BindingIsActive);
        Assert.False(source.Value.CaptureSnapshot().HasReservationReturnObligation);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().CancelledReservationCount);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().ReturnFailureCount);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
        Assert.True(source.Value.TryCompletePayloadReturn());
        Assert.Throws<InvalidOperationException>(() => source.Value.Dispose());
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void TraceCorrelatesActualPermissionPreparationMovementAndPhysicalReturn()
    {
        NativeMemoryBudget budget = new(4, 16);
        Assert.True(budget.TryReserve<int>(1, out NativeMemoryReservation<int>? source, out _));
        long ownerId = source.Value.Id;
        NativeMemoryReservation<int> moved = NativeMemoryReservation<int>.Move(ref source);
        moved.PrepareBacking();
        moved.Dispose();
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[16];
        int count = budget.CopyTraceTo(events);
        NativeMemoryTraceKind[] expected = [NativeMemoryTraceKind.ReservationAdmitted, NativeMemoryTraceKind.ReservationMoved,
            NativeMemoryTraceKind.Allocated, NativeMemoryTraceKind.ReservationBackingPrepared,
            NativeMemoryTraceKind.Released, NativeMemoryTraceKind.ReservationCancelled];
        Assert.Equal(expected.Length, count);
        for (int index = 0; index < count; index++)
        {
            Assert.Equal(expected[index], events[index].Kind);
            Assert.Equal(ownerId, events[index].OwnerId);
            Assert.Equal(4u, events[index].RequestedBytes);
            if (events[index].Kind is not (NativeMemoryTraceKind.Allocated or NativeMemoryTraceKind.Released))
                Assert.Equal(ownerId, events[index].CorrelationId);
        }
        Assert.Equal(4, events[0].ReservedBytes);
        Assert.Equal(0, events[count - 1].ReservedBytes);
        Assert.Equal(0, events[count - 1].CommittedBytes);
    }

    [Fact]
    public void HistoriesSaturateAndAuthorityExhaustionConsumesWithoutWrapping()
    {
        NativeMemoryBudget full = new(0);
        typeof(NativeMemoryBudget).GetField("_applicationRejectedCount", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(full, long.MaxValue);
        Assert.False(full.TryReserve<int>(1, out _, out _));
        Assert.Equal(long.MaxValue, full.CaptureAdmissionStatistics().RejectedReservationCount);
        Assert.True(full.CaptureAdmissionStatistics().HistoryOverflowed);
        Assert.Equal(0, full.CaptureAdmissionStatistics().OutstandingReservationCount);

        NativeMemoryBudget budget = new(4);
        Assert.True(budget.TryReserve<int>(1, out NativeMemoryReservation<int>? source, out _));
        var control = (NativeMemoryReservationControl<int>)source.Value.ControlForTest!;
        typeof(NativeTransferControl<int>).GetField("_authorityVersion", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(control, long.MaxValue);
        source = new NativeMemoryReservation<int>(control, long.MaxValue);
        NativeMemoryReservation<int> alias = source.Value;
        Assert.Throws<OverflowException>(() => NativeMemoryReservation<int>.Move(ref source));
        Assert.Null(source);
        Assert.Equal(NativeMemoryReservationOutcome.AuthorityExhausted, alias.CaptureSnapshot().Outcome);
        Assert.Equal(long.MaxValue, alias.CaptureSnapshot().AuthorityVersion);
        Assert.False(alias.CaptureSnapshot().HasReservationReturnObligation);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
    }

    [Fact]
    public void FieldMeasurementsSumOnlyActualRetainedDeclaredRepresentations()
    {
        NativeMemoryBudget budget = new(4);
        Assert.True(budget.TryReserve<int>(1, out NativeMemoryReservation<int>? source, out _));
        try
        {
            object control = source.Value.ControlForTest!;
            long bytes = 0;
            for (Type? type = control.GetType(); type is not null && type != typeof(object); type = type.BaseType)
                bytes += type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Sum(static field => FieldBytes(field.FieldType));
            Assert.Equal(bytes, source.Value.CaptureSnapshot().ControlFieldBytes);
            Type ledger = typeof(NativeMemoryBudget).GetNestedType("NativeAdmissionLedger", BindingFlags.NonPublic)!;
            Assert.Equal(ledger.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Sum(static field => FieldBytes(field.FieldType)), budget.CaptureAdmissionStatistics().LedgerFieldBytes);
            Assert.Equal(IntPtr.Size + 2L * sizeof(long), budget.CaptureAdmissionStatistics().BudgetAdmissionFieldBytes);
        }
        finally { source.Value.Dispose(); }
    }

    [Fact]
    public void InvalidDimensionsAndInitializerDoNotMutateOrConsumeAdmission()
    {
        NativeMemoryBudget budget = new(4);
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.TryReserve<int>(-1, out _, out _));
        Assert.Equal(0, budget.CaptureAdmissionStatistics().LedgerFieldBytes);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().RejectedReservationCount);
        Assert.True(budget.TryReserve<int>(1, out NativeMemoryReservation<int>? source, out _));
        Assert.Throws<ArgumentNullException>(() => NativeMemoryReservation<int>.Activate(ref source, null!));
        Assert.True(source.Value.CaptureSnapshot().BindingIsActive);
        Assert.Equal(4, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().InitializationFailureCount);
        source.Value.Dispose();
    }

    [Fact]
    public void ActivationBackendFailureConsumesPermissionWithoutInventingAllocationOrInitialization()
    {
        NativeMemoryBudget budget = new(4);
        Assert.True(budget.TryReserve<int>(1, out NativeMemoryReservation<int>? source, out _));
        NativeMemoryReservation<int> observed = source.Value;
        NativeMemoryTestHooks.FailNextAllocation();
        Assert.Throws<NativeAllocationFailedException>(() => NativeMemoryReservation<int>.Activate(ref source,
            static writer => writer.Write(42)));
        Assert.Null(source);
        Assert.Equal(NativeMemoryReservationOutcome.PreparationFailed, observed.CaptureSnapshot().Outcome);
        Assert.False(observed.CaptureSnapshot().HasReservationReturnObligation);
        NativeMemoryAdmissionStatistics actual = budget.CaptureAdmissionStatistics();
        Assert.Equal(1, actual.BackingPreparationFailureCount);
        Assert.Equal(1, actual.BackendAllocationFailureCount);
        Assert.Equal(0, actual.InitializationFailureCount);
        Assert.Equal(0, actual.ActivationCount);
        Assert.Equal(0, actual.OutstandingReservationCount);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void CancelledExplicitPreparationPreservesPermissionWithoutAcquiringBacking()
    {
        NativeMemoryBudget budget = new(4);
        Assert.True(budget.TryReserve<int>(1, out NativeMemoryReservation<int>? source, out _));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => source.Value.PrepareBacking(cancellation.Token));
        Assert.True(source.Value.CaptureSnapshot().BindingIsActive);
        Assert.Equal(NativeMemoryReservationOutcome.Pending, source.Value.CaptureSnapshot().Outcome);
        Assert.Equal(4, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().BackingPreparationFailureCount);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().CancelledReservationCount);
        source.Value.Dispose();
    }

    private static long FieldBytes(Type type) => !type.IsValueType ? IntPtr.Size
        : type == typeof(long) ? sizeof(long)
        : type == typeof(int) || type.IsEnum ? sizeof(int)
        : type == typeof(bool) ? sizeof(bool)
        : type == typeof(NativeBlock) ? Unsafe.SizeOf<NativeBlock>()
        : throw new InvalidOperationException($"Unaccounted field representation: {type}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon(NativeMemoryBudget budget, bool prepared)
    {
        Assert.True(budget.TryReserve<int>(1, out NativeMemoryReservation<int>? source, out _));
        if (prepared) source.Value.PrepareBacking();
        return new WeakReference(source.Value.ControlForTest!);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureReservationMoves(ref NativeMemoryReservation<int>? source)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            NativeMemoryReservation<int> current = NativeMemoryReservation<int>.Move(ref source);
            source = current;
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeTransfer<int> WarmActivation()
    {
        NativeMemoryBudget budget = new(4);
        Assert.True(budget.TryReserve<int>(1, out NativeMemoryReservation<int>? source, out _));
        source.Value.PrepareBacking();
        return NativeMemoryReservation<int>.Activate(ref source, static writer => writer.Write(1));
    }
}
