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
    PressureEffectiveIsolation Isolation,
    double EffectiveCpuCores = 0,
    long PageFaultsDelta = 0,
    long MajorPageFaultsDelta = 0);

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

public static class PressureSamplePolicy
{
    public static bool SafeRunsFirst(int sampleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleIndex);
        return (sampleIndex & 1) == 0;
    }

    public static bool HasBalancedOrder(
        IReadOnlyList<PressurePairedObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        return observations.Count > 0
            && (observations.Count & 1) == 0
            && observations.Count(
                static observation => observation.SafeRanFirst)
                == observations.Count / 2;
    }

    public static bool StressConfidencePassed(
        int sampleCount,
        int requiredSampleCount,
        double confidenceLower95) =>
        sampleCount == requiredSampleCount
        && requiredSampleCount > 0
        && (requiredSampleCount & 1) == 0
        && confidenceLower95 > 1.00;
}

public static class PressureProfileOrderPolicy
{
    public static bool FollowsCanonicalOrder(
        IReadOnlyList<int> requestedProfiles,
        IReadOnlyList<int> canonicalProfiles)
    {
        ArgumentNullException.ThrowIfNull(requestedProfiles);
        ArgumentNullException.ThrowIfNull(canonicalProfiles);
        if (requestedProfiles.Count == 0
            || canonicalProfiles.Count == 0)
        {
            return false;
        }

        int canonicalIndex = 0;
        foreach (int requestedProfile in requestedProfiles)
        {
            while (canonicalIndex < canonicalProfiles.Count
                && canonicalProfiles[canonicalIndex] != requestedProfile)
            {
                canonicalIndex++;
            }

            if (canonicalIndex >= canonicalProfiles.Count)
            {
                return false;
            }

            canonicalIndex++;
        }

        return true;
    }
}

public readonly record struct PressurePreparationAssessment(
    int ObservationCount,
    int FluctuationCount,
    bool MinimumReached,
    bool MaximumReached,
    bool FluctuationTargetReached,
    bool Accepted)
{
    public bool Exhausted => MaximumReached && !FluctuationTargetReached;

    public bool ShouldContinue => !Accepted && !MaximumReached;
}

public static class PressurePreparationPolicy
{
    public static PressurePreparationAssessment Evaluate(
        IReadOnlyList<double> elapsedMilliseconds,
        int minimumObservationCount,
        int maximumObservationCount,
        int requiredFluctuationCount)
    {
        ArgumentNullException.ThrowIfNull(elapsedMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            minimumObservationCount);
        if (maximumObservationCount < minimumObservationCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumObservationCount));
        }

        if (elapsedMilliseconds.Count > maximumObservationCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elapsedMilliseconds));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            requiredFluctuationCount);
        if (elapsedMilliseconds.Any(
                static value => !double.IsFinite(value) || value <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(elapsedMilliseconds));
        }

        int direction = -1;
        int fluctuationCount = 0;
        for (int index = 1;
            index < elapsedMilliseconds.Count;
            index++)
        {
            int nextDirection = Math.Sign(
                elapsedMilliseconds[index]
                    - elapsedMilliseconds[index - 1]);
            if (nextDirection != direction || nextDirection == 0)
            {
                direction = nextDirection;
                fluctuationCount++;
            }
        }

        bool minimumReached =
            elapsedMilliseconds.Count >= minimumObservationCount;
        bool maximumReached =
            elapsedMilliseconds.Count >= maximumObservationCount;
        bool fluctuationTargetReached =
            fluctuationCount >= requiredFluctuationCount;
        return new PressurePreparationAssessment(
            elapsedMilliseconds.Count,
            fluctuationCount,
            minimumReached,
            maximumReached,
            fluctuationTargetReached,
            minimumReached && fluctuationTargetReached);
    }
}

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
    bool LogicalResetPassed,
    IReadOnlyList<PressureImplementationObservation>? SafeAttempts = null,
    IReadOnlyList<PressureImplementationObservation>? NamAttempts = null,
    PressurePreparationAssessment SafeAssessment = default,
    PressurePreparationAssessment NamAssessment = default,
    int MinimumAttemptCount = 1,
    int MaximumAttemptCount = 1,
    int RequiredFluctuationCount = 0,
    bool SafeTimedRequestStarted = true,
    bool NamTimedRequestStarted = true,
    string? FailureMessage = null);

public readonly record struct PressurePreparationFailure(
    string Implementation,
    int ProfilePercent,
    int SampleIndex,
    string Message);

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
    PressurePairedStatistics Statistics,
    IReadOnlyList<PressureProfileInitialization>? SampleInitializations = null);

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

public readonly record struct PressureMatrixFailureReport(
    string GitCommit,
    string ImageId,
    IReadOnlyList<PressureBinaryIdentity> BinaryIdentities,
    long CgroupCapBytes,
    double DeadlineMilliseconds,
    int Seed,
    IReadOnlyList<int> PredeclaredProfilePercents,
    IReadOnlyList<PressureProfilePair> CompletedProfiles,
    PressureProfilePair FailedProfile,
    PressurePreparationFailure Failure,
    IReadOnlyList<string> Commands,
    IReadOnlyList<PressureWorkerLifecycle> WorkerLifecycles,
    DateTime StartedUtc,
    DateTime CompletedUtc,
    double TotalEndToEndElapsedMilliseconds);
