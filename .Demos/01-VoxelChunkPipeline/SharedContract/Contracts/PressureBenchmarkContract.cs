namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public enum PressureFailureAttribution
{
    None,
    SafeCSharp,
    NAM,
    BothImplementations,
    HarnessInfrastructure
}

public readonly record struct PressureHostProgress(
    PressureProgress Progress,
    double HostElapsedMilliseconds);

public readonly record struct PressureHostSample(
    DateTime Utc,
    double HostElapsedMilliseconds,
    long CgroupMemoryBytes,
    long CgroupMemoryLimitBytes,
    double CpuPercent,
    int Pids,
    long NetworkInputBytes,
    long NetworkOutputBytes,
    long BlockReadBytes,
    long BlockWriteBytes);

public readonly record struct PressureEffectiveIsolation(
    string ContainerName,
    string ImageId,
    long MemoryLimitBytes,
    long MemorySwapLimitBytes,
    int MemorySwappiness,
    string RequestedCpuSet,
    string EffectiveCpuSet,
    string CpuMax,
    int PidsLimit,
    int LogicalProcessorCount,
    IReadOnlyDictionary<string, string> GcConfiguration,
    IReadOnlyDictionary<string, string> DockerInspect,
    string ContainerId = "",
    int ContainerProcessId = 0,
    string CgroupIdentity = "",
    PressureRuntimeSnapshot StartupRuntime = default);

public readonly record struct PressureImplementationObservation(
    string Implementation,
    int ProfilePercent,
    PressureProfileOutcome Outcome,
    PressureFailureAttribution FailureAttribution,
    long CgroupCapBytes,
    long RequestedCumulativeDemandBytes,
    long RealizedCumulativeDemandBytes,
    double DeadlineMilliseconds,
    double? ProfileElapsedMilliseconds,
    double ElapsedLowerBoundMilliseconds,
    double SetupMilliseconds,
    double ResultTransferMilliseconds,
    int CompletedChunks,
    long CompletedLogicalBytes,
    VoxelPipelineStage LastCompletedStage,
    int LastCompletedChunkId,
    bool CorrectnessPassed,
    int? ExitCode,
    string? ExceptionType,
    string? ExceptionMessage,
    PressureProfileResult? ChildResult,
    long ManagedAllocatedSinceWorkerStart,
    int Gen2CollectionsSinceWorkerStart,
    double CpuMillisecondsSinceWorkerStart,
    IReadOnlyList<PressureHostProgress> Progress,
    IReadOnlyList<PressureHostSample> HostSamples,
    CgroupMemorySnapshot InitialCgroup,
    CgroupMemorySnapshot FinalCgroup,
    bool CgroupPeakReset,
    long ExternalCgroupPeakBytes,
    double ExternalCpuPercentMean,
    double ExternalCpuPercentPeak,
    PressureEffectiveIsolation Isolation);

public readonly record struct PressurePairedStatistics(
    int SampleCount,
    double? MeanSpeedup,
    double? ConfidenceLower95,
    double? ConfidenceUpper95,
    double? SafeMeanMillisecondsPerGiB,
    double? NamMeanMillisecondsPerGiB,
    double? SafeP95MillisecondsPerGiB,
    double? SafeP99MillisecondsPerGiB,
    double? NamP95MillisecondsPerGiB,
    double? NamP99MillisecondsPerGiB,
    bool PressureQualified,
    bool DeadlineGatePassed,
    bool CorrectnessGatePassed,
    bool PerformanceGatePassed,
    bool GatePassed,
    string Interpretation);

public readonly record struct PressurePairedObservation(
    int SampleIndex,
    PressureImplementationObservation Safe,
    PressureImplementationObservation Nam,
    bool StructuralParityPassed,
    bool SafeRanFirst = false);

public readonly record struct PressureOutcomeDecision(
    bool SafeCompleted,
    bool NamCompleted,
    bool PairedParityPassed,
    bool CompletedPairOutputMismatch,
    bool SafeOutputIncorrect,
    bool NamOutputIncorrect,
    bool SafeResourceFailure,
    bool DecisiveNam,
    bool DeadlineGatePassed,
    bool CorrectnessGatePassed);

public static class PressureOutcomePolicy
{
    public static bool AllSamplesCompletedWithinDeadline(
        IReadOnlyList<PressureImplementationObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        return observations.Count > 0
            && observations.All(
                static observation =>
                    observation.Outcome == PressureProfileOutcome.Completed
                    && observation.CorrectnessPassed
                    && observation.DeadlineMilliseconds > 0
                    && observation.ProfileElapsedMilliseconds is > 0
                    && observation.ProfileElapsedMilliseconds
                        <= observation.DeadlineMilliseconds);
    }

    public static PressureOutcomeDecision Evaluate(
        int profilePercent,
        bool safeCompleted,
        bool namCompleted,
        bool pairedParityPassed,
        bool completedPairOutputMismatch,
        IReadOnlyList<PressureProfileOutcome> safeOutcomes,
        IReadOnlyList<PressureProfileOutcome> namOutcomes)
    {
        ArgumentNullException.ThrowIfNull(safeOutcomes);
        ArgumentNullException.ThrowIfNull(namOutcomes);
        if (safeOutcomes.Count == 0
            || safeOutcomes.Count != namOutcomes.Count)
        {
            throw new ArgumentException(
                "The outcome policy requires equal nonempty outcome sets.");
        }

        bool safeOutputIncorrect = safeOutcomes.Contains(
            PressureProfileOutcome.IncorrectOutput);
        bool namOutputIncorrect = namOutcomes.Contains(
            PressureProfileOutcome.IncorrectOutput);
        bool safeResourceFailure = safeOutcomes.Any(IsResourceFailure)
            && safeOutcomes.All(
                static outcome =>
                    outcome == PressureProfileOutcome.Completed
                    || IsResourceFailure(outcome));
        bool decisiveNam = profilePercent >= 200
            && namCompleted
            && !completedPairOutputMismatch
            && !safeOutputIncorrect
            && !namOutputIncorrect
            && safeResourceFailure;
        bool deadlineGatePassed = namCompleted
            && (safeCompleted || decisiveNam);
        bool correctnessGatePassed =
            !completedPairOutputMismatch
            && !safeOutputIncorrect
            && !namOutputIncorrect
            && (pairedParityPassed || decisiveNam);

        return new PressureOutcomeDecision(
            safeCompleted,
            namCompleted,
            pairedParityPassed,
            completedPairOutputMismatch,
            safeOutputIncorrect,
            namOutputIncorrect,
            safeResourceFailure,
            decisiveNam,
            deadlineGatePassed,
            correctnessGatePassed);
    }

    private static bool IsResourceFailure(PressureProfileOutcome outcome) =>
        outcome is
            PressureProfileOutcome.DeadlineExceeded
            or PressureProfileOutcome.OutOfMemory;
}

public readonly record struct PressureProfileInitialization(
    int SequenceOrdinal,
    DateTime StartedUtc,
    double ElapsedMilliseconds,
    string SafeContainerName,
    string NamContainerName,
    int WarmupPasses,
    int WarmupProfilePercent,
    long WarmupCumulativeDemandBytes,
    PressureMeasurementPreparation? MeasurementPreparation = null);

public readonly record struct PressureMeasurementPreparation(
    int ProfilePercent,
    long RequestedCumulativeDemandBytes,
    double ElapsedMilliseconds,
    PressureImplementationObservation Safe,
    PressureImplementationObservation Nam,
    bool EquivalentMeasuredPathPassed,
    bool LogicalResetPassed);

public readonly record struct PressureWorkerLifecycle(
    string Implementation,
    string ContainerName,
    string ContainerId,
    int ContainerProcessId,
    string CgroupIdentity,
    PressureRuntimeSnapshot StartupRuntime,
    long FirstRequestOrdinal,
    long LastRequestOrdinal,
    int RequestCount,
    bool DisposalCompleted,
    bool ContainerAbsentAfterDisposal,
    DateTime DisposedUtc);

public readonly record struct PressureProfilePair(
    int ProfilePercent,
    long CgroupCapBytes,
    long RequestedCumulativeDemandBytes,
    PressureProfileInitialization Initialization,
    IReadOnlyList<PressurePairedObservation> Observations,
    PressurePairedStatistics Statistics);

public readonly record struct PressureVerificationPair(
    int ProfilePercent,
    long RequestedCumulativeDemandBytes,
    int WarmupProfilePercent,
    long WarmupCumulativeDemandBytes,
    PressureProfileInitialization Initialization,
    PressureImplementationObservation Safe,
    PressureImplementationObservation Nam,
    bool ExactParityPassed);

public readonly record struct PressureMatrixSummary(
    DateTime StartedUtc,
    DateTime CompletedUtc,
    double TotalMatrixMeasuredElapsedMilliseconds,
    double TotalProfileInitializationElapsedMilliseconds,
    double TotalEndToEndElapsedMilliseconds,
    int CompletedProfiles,
    int FailedProfiles,
    bool ProfileIsolationPassed,
    bool ExactParityPassed,
    bool DeadlineGatePassed,
    bool PressureQualificationPassed,
    bool PerformanceGatePassed,
    bool GatePassed);

public readonly record struct PressureBinaryIdentity(
    string Component,
    string RelativePath,
    string Sha256,
    string InformationalVersion,
    string InformationalCommit);

public readonly record struct PressureMatrixReport(
    string GitCommit,
    string ImageId,
    IReadOnlyList<PressureBinaryIdentity> BinaryIdentities,
    long CgroupCapBytes,
    string CgroupCapUnit,
    double DeadlineMilliseconds,
    int RetentionDepth,
    int ProgressEveryChunks,
    int SamplesPerProfile,
    int Seed,
    IReadOnlyList<int> PredeclaredProfilePercents,
    IReadOnlyList<PressureProfilePair> Profiles,
    PressureVerificationPair Verification,
    PressureMatrixSummary Summary,
    IReadOnlyDictionary<string, string> HostConfiguration,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<PressureWorkerLifecycle>? WorkerLifecycles = null);
