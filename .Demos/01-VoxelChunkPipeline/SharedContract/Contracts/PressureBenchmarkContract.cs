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
    IReadOnlyDictionary<string, string> DockerInspect);

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
    bool StructuralParityPassed);

public readonly record struct PressureProfilePair(
    int ProfilePercent,
    long CgroupCapBytes,
    long RequestedCumulativeDemandBytes,
    IReadOnlyList<PressurePairedObservation> Observations,
    PressurePairedStatistics Statistics);

public readonly record struct PressureVerificationPair(
    int ProfilePercent,
    long RequestedCumulativeDemandBytes,
    PressureImplementationObservation Safe,
    PressureImplementationObservation Nam,
    bool ExactParityPassed);

public readonly record struct PressureMatrixSummary(
    DateTime StartedUtc,
    DateTime CompletedUtc,
    double TotalMatrixMeasuredElapsedMilliseconds,
    double TotalEndToEndElapsedMilliseconds,
    int CompletedProfiles,
    int FailedProfiles,
    bool ExactParityPassed,
    bool DeadlineGatePassed,
    bool PressureQualificationPassed,
    bool NamScalingGatePassed,
    bool PerformanceGatePassed,
    bool GatePassed);

public readonly record struct PressureMatrixReport(
    string GitCommit,
    string ImageId,
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
    IReadOnlyList<string> Limitations);
