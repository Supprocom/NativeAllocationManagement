using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;
using NativeSession =
    Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.NAM.NativePressureSession;
using SafeSession =
    Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SafeCSharp.SafePressureSession;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class VoxelPressureResetTests
{
    [Fact]
    public void SafeSessionResetsBeforeTheFirstMeasuredRequest()
    {
        using SafeSession session = new();
        VerifyResetSequence(session);
    }

    [Fact]
    public void NativeSessionResetsBeforeTheFirstMeasuredRequest()
    {
        using NativeSession session = new();
        VerifyResetSequence(session);
    }

    [Fact]
    public void WorkerLocalSessionsPreserveResetStateAndAllocationPlans()
    {
        using WorkerLocalPressureSession safe = new(
            "SafeCSharp",
            static () => new SafeSession(),
            maximumWorkerCount: 1);
        VerifyResetSequence(safe);

        using WorkerLocalPressureSession native = new(
            "NAM",
            static () => new NativeSession(),
            maximumWorkerCount: 1);
        VerifyResetSequence(native);
    }

    [Fact]
    public void SafeDiagnosticRecordsWorkerAndPhaseState()
    {
        using WorkerLocalPressureSession session = new(
            "SafeCSharp",
            static () => new SafeSession(),
            maximumWorkerCount: 1);

        VerifyDiagnosticState(
            session,
            expectAllocatorState: false);
    }

    [Fact]
    public void NativeDiagnosticRecordsWorkerAndAllocatorState()
    {
        using WorkerLocalPressureSession session = new(
            "NAM",
            static () => new NativeSession(),
            maximumWorkerCount: 1);

        VerifyDiagnosticState(
            session,
            expectAllocatorState: true);
    }

    private static void VerifyDiagnosticState(
        IPressureProfileSession session,
        bool expectAllocatorState)
    {
        const long capBytes = 64L * 1024 * 1024;
        PressureProfileRequest request = new(
                1,
                capBytes,
                1,
                20_000,
                0x4E414D,
                1,
                int.MaxValue,
                ExecutionMode:
                    PressureExecutionMode.Measurement,
                RequestOrdinal: 1,
                Diagnostic:
                    new PressureDiagnosticRequest(
                        VerifyExactOutput: true));
        PressureProfileResult warmup = session.Run(
            request with
            {
                Warmup = true
            },
            IgnoreProgress);
        PressureProfileResult result = session.Run(
            request with
            {
                RequestOrdinal = 2
            },
            IgnoreProgress);

        Assert.Equal(
            PressureProfileOutcome.Completed,
            warmup.Outcome);
        Assert.Equal(
            PressureProfileOutcome.Completed,
            result.Outcome);
        Assert.True(result.CorrectnessPassed);
        Assert.Equal(
            result.CompletedChunks,
            result.ChunkEvidence.Count);
        PressureRequestDiagnostics diagnostics =
            Assert.IsType<PressureRequestDiagnostics>(
                result.Diagnostics);
        PressureWorkerDiagnostic worker =
            Assert.Single(diagnostics.Workers);

        Assert.Equal(0, worker.WorkerIndex);
        Assert.Equal(
            result.CompletedChunks,
            worker.PartitionChunkCount);
        Assert.Equal(
            result.RealizedCumulativeDemandBytes,
            worker.PartitionLogicalBytes);
        Assert.NotEqual(default, worker.StartedUtc);
        Assert.True(
            worker.ProcessingCompletedUtc
                >= worker.StartedUtc);
        Assert.True(
            worker.FinishedUtc
                >= worker.ProcessingCompletedUtc);
        Assert.True(worker.ProcessingMilliseconds > 0);
        Assert.True(worker.FinishLatencyMilliseconds >= 0);
        Assert.True(worker.Phases.BuildMilliseconds > 0);
        Assert.True(
            worker.Phases.SectionPreparationMilliseconds
                > 0);
        Assert.True(
            worker.Phases.FaceGenerationMilliseconds > 0);
        Assert.True(worker.Phases.PackingMilliseconds > 0);
        Assert.True(
            worker.Phases.OutputConsumptionMilliseconds
                > 0);
        Assert.Equal(
            expectAllocatorState,
            worker.AllocatorBefore.Available);
        Assert.Equal(
            expectAllocatorState,
            worker.AllocatorAfterProcessing.Available);
        Assert.Equal(
            expectAllocatorState,
            worker.AllocatorAfterReset.Available);
        if (expectAllocatorState)
        {
            PressureRequestDiagnostics warmupDiagnostics =
                Assert.IsType<PressureRequestDiagnostics>(
                    warmup.Diagnostics);
            PressureWorkerDiagnostic warmupWorker =
                Assert.Single(warmupDiagnostics.Workers);

            Assert.True(
                worker.AllocatorAfterReset.ActiveRecords > 0);
            Assert.Equal(
                warmupWorker.AllocatorAfterReset.ActiveRecords,
                worker.AllocatorAfterReset.ActiveRecords);
            Assert.Equal(
                0,
                worker.AllocatorAfterReset.ScopedRecords);
            Assert.Equal(
                0,
                worker.AllocatorAfterReset.ReferenceRoots);
        }
    }

    private static void VerifyResetSequence(
        IPressureProfileSession session)
    {
        const long capBytes = 64L * 1024 * 1024;
        PressureProfileRequest request = new(
            1,
            capBytes,
            1,
            20_000,
            0x4E414D,
            1,
            int.MaxValue,
            ExecutionMode: PressureExecutionMode.Measurement);
        PressureProfileResult warmup = session.Run(
            request with
            {
                Warmup = true,
                RequestOrdinal = 1
            },
            IgnoreProgress);
        PressureProfileResult preparation = session.Run(
            request with
            {
                RequestOrdinal = 2
            },
            IgnoreProgress);
        PressureProfileResult first = session.Run(
            request with
            {
                ExecutionMode = PressureExecutionMode.Verification,
                RequestOrdinal = 3
            },
            IgnoreProgress);
        PressureProfileResult later = session.Run(
            request with
            {
                ExecutionMode = PressureExecutionMode.Verification,
                RequestOrdinal = 4
            },
            IgnoreProgress);

        AssertReset(warmup, 1);
        AssertReset(preparation, 2);
        AssertReset(first, 3);
        AssertReset(later, 4);
        Assert.Equal(
            preparation.StateAfterReset!.Value.AllocationPlanFingerprint,
            first.StateAfterReset!.Value.AllocationPlanFingerprint);
        Assert.Equal(
            first.StateAfterReset!.Value.AllocationPlanFingerprint,
            later.StateAfterReset!.Value.AllocationPlanFingerprint);
        Assert.Equal(
            first.StateAfterReset.Value.RetainedCapacityBytes,
            later.StateAfterReset.Value.RetainedCapacityBytes);
        Assert.Equal(
            first.StateAfterReset.Value.PersistentAllocationBytes,
            later.StateAfterReset.Value.PersistentAllocationBytes);
        Assert.Equal(
            first.RealizedCumulativeDemandBytes,
            later.RealizedCumulativeDemandBytes);
        Assert.Equal(first.CompletedChunks, later.CompletedChunks);
        Assert.Equal(
            first.CompletedLogicalBytes,
            later.CompletedLogicalBytes);
        Assert.Equal(
            first.CanonicalEvidenceHash,
            later.CanonicalEvidenceHash);
        Assert.Equal(
            first.ChunkEvidence.ToArray(),
            later.ChunkEvidence.ToArray());
    }

    private static void AssertReset(
        PressureProfileResult result,
        long expectedOrdinal)
    {
        Assert.Equal(PressureProfileOutcome.Completed, result.Outcome);
        Assert.True(result.CorrectnessPassed);
        Assert.True(result.StateAfterReset.HasValue);
        PressureSessionState state =
            result.StateAfterReset.Value;
        Assert.Equal(expectedOrdinal, state.RequestOrdinal);
        Assert.Equal(expectedOrdinal, state.CompletedRequestCount);
        Assert.True(state.LogicalResetPassed);
        Assert.True(state.AllocationPlanFingerprint > 0);
    }

    private static void IgnoreProgress(PressureProgress progress)
    {
    }
}
