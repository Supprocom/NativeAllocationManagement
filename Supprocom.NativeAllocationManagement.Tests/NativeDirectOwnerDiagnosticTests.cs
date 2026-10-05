namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeDirectOwnerDiagnosticTests
{
    [Fact]
    public void EmptyBuilderHasRealZeroHistoriesAndReservedCapacityHasNoInitializedDemand()
    {
        using NativeBuilder<int> empty = new();
        AssertDirect(empty.GetStatistics(), empty.Id, NativeOwnerModel.SingleWriterBuilder, 0, 0, 0, 0);
        Assert.Equal(0, empty.GetStatistics().FreshSegmentAllocationCount);
        using NativeBuilder<int> reserved = new(4);
        AssertDirect(reserved.GetStatistics(), reserved.Id, NativeOwnerModel.SingleWriterBuilder, 0, 0, 16, 16);
        Assert.Equal(1, reserved.GetStatistics().FreshSegmentAllocationCount);
        Assert.Equal(1, reserved.CaptureDiagnosticSnapshot().ActiveRecords);
        Assert.Equal(1, reserved.GetStatistics().AvailableSegmentCount);
        reserved.Dispose();
        AssertDirect(reserved.GetStatistics(), reserved.Id, NativeOwnerModel.SingleWriterBuilder, 0, 0, 0, 16);
        Assert.Equal(0, reserved.CaptureDiagnosticSnapshot().ActiveRecords);
    }

    [Fact]
    public void BuilderGrowthAndCompletionPreserveNumericHistoryWithoutRetainingBackingAuthority()
    {
        NativeMemoryBudget budget = new(64);
        using NativeBuilder<int> builder = new(budget, 2);
        builder.Append([11, 22, 33, 44]);
        AssertDirect(builder.GetStatistics(), builder.Id, NativeOwnerModel.SingleWriterBuilder, 16, 16, 16, 16);
        NativeTransfer<int> transfer = builder.Complete();
        try
        {
            Assert.Equal(builder.Id, transfer.Id);
            Assert.Equal(44, transfer.Read(static values => values[3]));
            AssertDirect(builder.GetStatistics(), builder.Id, NativeOwnerModel.SingleWriterBuilder, 0, 16, 0, 16);
            NativeOwnerDiagnosticSnapshot terminal = builder.CaptureDiagnosticSnapshot();
            Assert.Equal(NativeOwnerLifecycle.Returned, terminal.Lifecycle);
            Assert.Equal(0, terminal.OutstandingNativeBytes);
            Assert.Equal(16, terminal.PeakOutstandingNativeBytes);
            Assert.Equal(16, terminal.PeakInitializedPayloadBytes);
            Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
            Assert.Throws<InvalidOperationException>(() => builder.Append(55));
        }
        finally { transfer.Dispose(); }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(16, builder.GetStatistics().PeakOutstandingNativeBytes);
    }

    [Fact]
    public void BuilderBudgetRefusalPreservesHistoryAndExactFallbackGrowthIsMeasured()
    {
        NativeMemoryBudget budget = new(28);
        using NativeBuilder<int> builder = new(budget, 3);
        builder.Append(42);
        Assert.False(builder.TryEnsureCapacity(5));
        AssertDirect(builder.GetStatistics(), builder.Id, NativeOwnerModel.SingleWriterBuilder, 4, 4, 12, 12);
        Assert.True(builder.TryEnsureCapacity(4));
        AssertDirect(builder.GetStatistics(), builder.Id, NativeOwnerModel.SingleWriterBuilder, 4, 4, 16, 16);
        Assert.Equal(1, budget.CaptureStatistics().RejectedAllocationCount);
        Assert.Equal(1, budget.CaptureStatistics().ReallocationCount);
        builder.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        AssertDirect(builder.GetStatistics(), builder.Id, NativeOwnerModel.SingleWriterBuilder, 0, 4, 0, 16);
    }

    [Fact]
    public void FailedBuilderGrowthReleasesBackingButDoesNotInventARejectedExtentPeak()
    {
        NativeMemoryBudget budget = new(64);
        using NativeBuilder<int> builder = new(budget, 2);
        builder.Append([17, 19]);
        try
        {
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => builder.Append(23));
            AssertDirect(builder.GetStatistics(), builder.Id, NativeOwnerModel.SingleWriterBuilder, 0, 8, 0, 8);
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void FailedBuilderProducerKeepsOnlyThePreviouslyPublishedPrefixPeak()
    {
        using NativeBuilder<int> builder = new(4);
        builder.Append(42);
        Assert.Throws<InvalidOperationException>(() => builder.Write(3, static writer =>
        {
            writer.AsSpan().Fill(17);
            throw new InvalidOperationException("Uncommitted producer.");
        }));
        AssertDirect(builder.GetStatistics(), builder.Id, NativeOwnerModel.SingleWriterBuilder, 0, 4, 0, 16);
        Assert.Equal(NativeOwnerLifecycle.Disposed, builder.CaptureDiagnosticSnapshot().Lifecycle);
    }

    [Fact]
    public void BuilderObservationRejectsAnEnteredWriterWithoutPoisoningIt()
    {
        using NativeBuilder<int> builder = new(4);
        builder.Write(2, writer =>
        {
            Assert.Throws<InvalidOperationException>(() => builder.GetStatistics());
            Assert.Throws<InvalidOperationException>(() => builder.CaptureDiagnosticSnapshot());
            writer.AsSpan().Fill(42);
            writer.Commit(2);
        });
        builder.Append(17);
        AssertDirect(builder.GetStatistics(), builder.Id, NativeOwnerModel.SingleWriterBuilder, 12, 12, 16, 16);
    }

    [Fact]
    public void CancelledBuilderOperationReturnsStorageAndRetainsPriorHistories()
    {
        using NativeBuilder<int> builder = new(4);
        builder.Append(42);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => builder.Append(17, cancellation.Token));
        AssertDirect(builder.GetStatistics(), builder.Id, NativeOwnerModel.SingleWriterBuilder, 0, 4, 0, 16);
    }

    [Fact]
    public void WorkspaceProcessesPreviouslyInitializedStorageWithoutPublishingPersistentDemand()
    {
        using NativeWorkspace<int> workspace = new(8);
        AssertDirect(workspace.GetStatistics(), workspace.Id, NativeOwnerModel.ThreadConfinedWorkspace, 0, 0, 32, 32);
        int result = workspace.Process(4, values =>
        {
            AssertDirect(workspace.GetStatistics(), workspace.Id, NativeOwnerModel.ThreadConfinedWorkspace, 16, 16, 32, 32);
            Assert.Equal(0, values[3]);
            values.Fill(42);
        }, values =>
        {
            Assert.Equal(16, workspace.CaptureDiagnosticSnapshot().InitializedPayloadBytes);
            Assert.Throws<InvalidOperationException>(workspace.Reset);
            Assert.Throws<InvalidOperationException>(workspace.Dispose);
            Assert.Throws<InvalidOperationException>(() => workspace.Process(0, 0, static (_, state) => state));
            return values[3];
        });
        Assert.Equal(42, result);
        Assert.Equal(0, workspace.Length);
        AssertDirect(workspace.GetStatistics(), workspace.Id, NativeOwnerModel.ThreadConfinedWorkspace, 0, 16, 32, 32);
        Assert.Throws<InvalidOperationException>(() => workspace.Read(static view => view[0]));
    }

    [Fact]
    public void WorkspaceStateProcessAndZeroLengthKeepTheSameEnteredUseGuards()
    {
        using NativeWorkspace<int> workspace = new(8);
        int result = workspace.Process(6, workspace, static (values, owner) =>
        {
            Assert.Equal(24, owner.GetStatistics().InitializedPayloadBytes);
            Assert.Throws<InvalidOperationException>(owner.Dispose);
            values.Fill(7);
            return values[5];
        });
        Assert.Equal(7, result);
        Assert.Equal(0, workspace.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(24, workspace.GetStatistics().PeakInitializedPayloadBytes);
        Assert.Equal(17, workspace.Process(0, workspace, static (values, owner) =>
        {
            Assert.Equal(0, values.Length);
            Assert.Equal(0, owner.GetStatistics().InitializedPayloadBytes);
            Assert.Throws<InvalidOperationException>(owner.Dispose);
            return 17;
        }));
        workspace.Reset();
        Assert.Equal(24, workspace.GetStatistics().PeakInitializedPayloadBytes);
    }

    [Fact]
    public void WorkspaceCheckedInitializationExcludesIncompleteProducersAndPreservesPeaksAfterReset()
    {
        using NativeWorkspace<int> workspace = new(8);
        Assert.Throws<InvalidOperationException>(() => workspace.Initialize(4, writer =>
        {
            writer.Write(42);
            Assert.Equal(0, workspace.GetStatistics().InitializedPayloadBytes);
            Assert.Equal(0, workspace.GetStatistics().PeakInitializedPayloadBytes);
        }));
        workspace.Initialize(3, writer =>
        {
            writer.Fill(17);
            Assert.Equal(0, workspace.CaptureDiagnosticSnapshot().InitializedPayloadBytes);
        });
        AssertDirect(workspace.GetStatistics(), workspace.Id, NativeOwnerModel.ThreadConfinedWorkspace, 12, 12, 32, 32);
        Assert.Equal(17, workspace.Read(static view => view[2]));
        workspace.Reset();
        AssertDirect(workspace.GetStatistics(), workspace.Id, NativeOwnerModel.ThreadConfinedWorkspace, 0, 12, 32, 32);
        workspace.Dispose();
        AssertDirect(workspace.GetStatistics(), workspace.Id, NativeOwnerModel.ThreadConfinedWorkspace, 0, 12, 0, 32);
        Assert.Equal(NativeOwnerLifecycle.Disposed, workspace.CaptureDiagnosticSnapshot().Lifecycle);
    }

    [Fact]
    public void InvalidAndPreCancelledWorkspaceRequestsDoNotInventEnteredDemand()
    {
        using NativeWorkspace<int> workspace = new(8);
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.Process(9, 0, static (_, state) => state));
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.Process(-1, 0, static (_, state) => state));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => workspace.Process(8, 0, static (_, state) => state, cancellation.Token));
        AssertDirect(workspace.GetStatistics(), workspace.Id, NativeOwnerModel.ThreadConfinedWorkspace, 0, 0, 32, 32);
        Assert.Throws<InvalidOperationException>(() => workspace.Process<int, int>(5, 0, static (_, _) => throw new InvalidOperationException("Entered failure.")));
        AssertDirect(workspace.GetStatistics(), workspace.Id, NativeOwnerModel.ThreadConfinedWorkspace, 0, 20, 32, 32);
    }

    [Fact]
    public void WorkspaceObserversRemainThreadConfinedAfterRelease()
    {
        NativeMemoryBudget budget = new(32);
        using NativeWorkspace<int> workspace = new(budget, 8);
        workspace.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Exception? failure = null;
        Thread worker = new(() =>
        {
            try { _ = workspace.GetStatistics(); }
            catch (InvalidOperationException exception) { failure = exception; }
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(32, workspace.CaptureDiagnosticSnapshot().PeakOutstandingNativeBytes);
    }

    [Fact]
    public void DirectCapturesAllocateNothingAndMeasurementResetDoesNotChangeOwnerHistories()
    {
        using NativeBuilder<int> builder = new(4);
        using NativeWorkspace<int> workspace = new(8);
        builder.Append(42);
        workspace.Initialize(3, static writer => writer.Fill(17));
        Assert.Equal(4, builder.CaptureDiagnosticSnapshot().InitializedPayloadBytes);
        Assert.Equal(12, workspace.CaptureDiagnosticSnapshot().InitializedPayloadBytes);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long observed = 0;
        for (int index = 0; index < 1_024; index++)
        {
            observed += builder.GetStatistics().PeakOutstandingNativeBytes;
            observed += builder.CaptureDiagnosticSnapshot().PeakInitializedPayloadBytes;
            observed += workspace.GetStatistics().PeakOutstandingNativeBytes;
            observed += workspace.CaptureDiagnosticSnapshot().PeakInitializedPayloadBytes;
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(65_536, observed);
        try
        {
            NativeMemoryTestHooks.Reset();
            builder.Dispose(); workspace.Dispose();
            Assert.Equal(16, builder.CaptureDiagnosticSnapshot().PeakOutstandingNativeBytes);
            Assert.Equal(4, builder.CaptureDiagnosticSnapshot().PeakInitializedPayloadBytes);
            Assert.Equal(32, workspace.CaptureDiagnosticSnapshot().PeakOutstandingNativeBytes);
            Assert.Equal(12, workspace.CaptureDiagnosticSnapshot().PeakInitializedPayloadBytes);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    private static void AssertDirect(NativeOwnerStatistics actual, long id, NativeOwnerModel model,
        long initialized, long initializedPeak, long backing, long backingPeak)
    {
        Assert.Equal(id, actual.OwnerId);
        Assert.Equal(model, actual.Model);
        Assert.Equal(initialized, actual.InitializedPayloadBytes);
        Assert.Equal(initializedPeak, actual.PeakInitializedPayloadBytes);
        Assert.Equal(backing, actual.OutstandingNativeBytes);
        Assert.Equal(backingPeak, actual.PeakOutstandingNativeBytes);
        Assert.Equal(0, actual.DetachedNativeBytes);
        Assert.Equal(0, actual.BorrowedBytes);
        Assert.Equal(0, actual.RetiredBytes);
        Assert.False(actual.HistoryOverflowed);
    }
}
