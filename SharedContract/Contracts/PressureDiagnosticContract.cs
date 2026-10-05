using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public readonly record struct PressureDiagnosticRequest(
    bool VerifyExactOutput,
    int WorkerIndex = -1,
    int PartitionChunkCount = 0,
    long PartitionLogicalBytes = 0);

public enum PressureDiagnosticPhase
{
    Build,
    SectionPreparation,
    FaceGeneration,
    FirstRecycle,
    Packing,
    OutputConsumption,
    SecondRecycle,
    Reset
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct PressurePhaseTimings(
    double BuildMilliseconds,
    double SectionPreparationMilliseconds,
    double FaceGenerationMilliseconds,
    double FirstRecycleMilliseconds,
    double PackingMilliseconds,
    double OutputConsumptionMilliseconds,
    double SecondRecycleMilliseconds,
    double ResetMilliseconds);

public sealed class PressurePhaseRecorder
{
    private readonly long[] _ticks = new long[
        Enum.GetValues<PressureDiagnosticPhase>().Length];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "The instance recorder API pairs Start with Record for each phase.")]
    public long Start() => Stopwatch.GetTimestamp();

    public void Record(
        PressureDiagnosticPhase phase,
        long startedTimestamp)
    {
        long elapsed = Stopwatch.GetTimestamp() - startedTimestamp;
        _ticks[(int)phase] = checked(
            _ticks[(int)phase] + Math.Max(0, elapsed));
    }

    public PressurePhaseTimings Capture() =>
        new(
            GetMilliseconds(PressureDiagnosticPhase.Build),
            GetMilliseconds(PressureDiagnosticPhase.SectionPreparation),
            GetMilliseconds(PressureDiagnosticPhase.FaceGeneration),
            GetMilliseconds(PressureDiagnosticPhase.FirstRecycle),
            GetMilliseconds(PressureDiagnosticPhase.Packing),
            GetMilliseconds(PressureDiagnosticPhase.OutputConsumption),
            GetMilliseconds(PressureDiagnosticPhase.SecondRecycle),
            GetMilliseconds(PressureDiagnosticPhase.Reset));

    private double GetMilliseconds(PressureDiagnosticPhase phase) =>
        _ticks[(int)phase] * 1000.0 / Stopwatch.Frequency;
}

public readonly record struct PressureAllocatorDiagnosticSnapshot(
    bool Available,
    string? Lifecycle,
    long? Generation,
    long? ScopeEpoch,
    long? MetricsEpoch,
    int? ActiveRecords,
    int? ScopedRecords,
    int? ReferenceRoots,
    int? OrdinaryTraversalIndex,
    int? ScopedTraversalIndex,
    int? RetainedSegmentCount,
    int? AvailableSegmentCount,
    int? RetiredGenerationCount,
    int? RetiredSegmentCount,
    long? RetiredBytes,
    int? QuarantinedGenerationCount,
    int? QuarantinedSegmentCount,
    bool? CurrentGenerationQuarantined)
{
    public long? OwnerId { get; init; }
    public string? Model { get; init; }
    public bool? HistoryOverflowed { get; init; }
    public long? OutstandingNativeBytes { get; init; }
    public long? DetachedNativeBytes { get; init; }
    public long? PeakOutstandingNativeBytes { get; init; }
    public long? InitializedPayloadBytes { get; init; }
    public long? PeakInitializedPayloadBytes { get; init; }
}

public readonly record struct PressureWorkerDiagnostic(
    int WorkerIndex,
    int PartitionChunkCount,
    long PartitionLogicalBytes,
    DateTime StartedUtc,
    DateTime ProcessingCompletedUtc,
    DateTime FinishedUtc,
    double ProcessingMilliseconds,
    double FinishLatencyMilliseconds,
    PressurePhaseTimings Phases,
    PressureAllocatorDiagnosticSnapshot AllocatorBefore,
    PressureAllocatorDiagnosticSnapshot AllocatorAfterProcessing,
    PressureAllocatorDiagnosticSnapshot AllocatorAfterReset);

public readonly record struct PressureRequestDiagnostics(
    IReadOnlyList<PressureWorkerDiagnostic> Workers);

public readonly record struct PressureExternalProcessSnapshot(
    DateTime Utc,
    CgroupMemorySnapshot Cgroup,
    int? ThreadCount,
    long? VoluntaryContextSwitches,
    long? NonvoluntaryContextSwitches,
    long? ProcessUserTicks,
    long? ProcessSystemTicks,
    long? ClockTicksPerSecond,
    double? ProcessCpuMilliseconds,
    long? WorkingSetBytes)
{
    public bool Available => ThreadCount is > 0
        && VoluntaryContextSwitches.HasValue && NonvoluntaryContextSwitches.HasValue
        && ProcessUserTicks.HasValue && ProcessSystemTicks.HasValue
        && ClockTicksPerSecond is > 0 && ProcessCpuMilliseconds.HasValue
        && WorkingSetBytes.HasValue;

    public static PressureExternalProcessSnapshot FromCommand(
        DateTime utc, CgroupMemorySnapshot cgroup, int exitCode, string text)
    {
        IReadOnlyDictionary<string, long?> counters = ExternalObservation.ParseCounters(exitCode == 0 ? text : null);
        long? threads = counters.GetValueOrDefault("threads");
        long? user = counters.GetValueOrDefault("user_ticks");
        long? system = counters.GetValueOrDefault("system_ticks");
        long? frequency = counters.GetValueOrDefault("clock_ticks");
        long? workingSetKiB = counters.GetValueOrDefault("working_set_kib");
        return new(utc, cgroup,
            threads is >= 0 and <= int.MaxValue ? (int)threads : null,
            counters.GetValueOrDefault("voluntary"), counters.GetValueOrDefault("nonvoluntary"),
            user, system, frequency, ExternalObservation.CpuMilliseconds(user, system, frequency),
            workingSetKiB is >= 0 && workingSetKiB <= long.MaxValue / 1024 ? workingSetKiB * 1024 : null);
    }
}

public readonly record struct PressureHostProcessorSample(
    DateTime Utc,
    double ProcessorPerformancePercent,
    double TotalCpuPercent,
    double ProcessorQueueLength);

public readonly record struct PressureHostStateGate(
    IReadOnlyList<PressureHostProcessorSample> Samples,
    bool Passed,
    string FailureReason);

public static class PressureHostStabilityPolicy
{
    public const int RequiredConsecutiveSamples = 3;

    public static double MedianProcessorPerformance(
        IReadOnlyList<PressureHostProcessorSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException(
                "The host baseline requires one or more samples.",
                nameof(samples));
        }

        double[] values = samples
            .Select(
                static sample =>
                    sample.ProcessorPerformancePercent)
            .Order()
            .ToArray();
        int middle = values.Length / 2;
        return (values.Length & 1) == 0
            ? (values[middle - 1] + values[middle]) / 2
            : values[middle];
    }

    public static bool IsStable(
        PressureHostProcessorSample sample,
        double initialPerformanceMedian,
        double maximumCpuPercent,
        double maximumQueueLength,
        double maximumPerformanceDelta) =>
        sample.TotalCpuPercent <= maximumCpuPercent
        && sample.ProcessorQueueLength <= maximumQueueLength
        && Math.Abs(
            sample.ProcessorPerformancePercent
            - initialPerformanceMedian)
            <= maximumPerformanceDelta;

    public static bool HasStableTail(
        IReadOnlyList<PressureHostProcessorSample> samples,
        double initialPerformanceMedian,
        double maximumCpuPercent,
        double maximumQueueLength,
        double maximumPerformanceDelta)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < RequiredConsecutiveSamples)
        {
            return false;
        }

        int start = samples.Count - RequiredConsecutiveSamples;
        for (int index = start; index < samples.Count; index++)
        {
            if (!IsStable(
                    samples[index],
                    initialPerformanceMedian,
                    maximumCpuPercent,
                    maximumQueueLength,
                    maximumPerformanceDelta))
            {
                return false;
            }
        }

        return true;
    }
}

public readonly record struct PressureSustainedDiagnosticOptionsSnapshot(
    string RepositoryRoot,
    string Image,
    string OutputPath,
    string CpuSet,
    long CgroupCapBytes,
    double DeadlineMilliseconds,
    int RetentionDepth,
    int Seed,
    int PidsLimit,
    int GcHeapHardLimitPercent,
    int ProfilePercent,
    int WarmupPassCount,
    int PreparationPassCount,
    int HostGateTimeoutSeconds,
    double MaximumHostCpuPercent,
    double MaximumProcessorQueueLength,
    double MaximumProcessorPerformanceDelta);

public readonly record struct PressureSustainedDiagnosticTrace(
    string Label,
    string Implementation,
    int RequestedWorkerCount,
    PressureHostStateGate HostGate,
    PressureHostProcessorSample HostAfter,
    IReadOnlyList<PressureImplementationObservation> Warmups,
    IReadOnlyList<PressureImplementationObservation> Preparations,
    PressureImplementationObservation Timed,
    IReadOnlyList<bool> ExactParityByRequestOrdinal,
    bool AllRequestsVerified,
    PressureWorkerLifecycle Lifecycle);

public readonly record struct PressureSustainedDiagnosticCleanup(
    int ExpectedLifecycleCount,
    int RecordedLifecycleCount,
    bool EveryDisposalCompleted,
    bool EveryContainerAbsent,
    int ActiveContainerCount);

public readonly record struct PressureSustainedDiagnosticHostGateFailure(
    string TraceLabel,
    PressureHostStateGate HostGate);

public readonly record struct PressureSustainedDiagnosticReport(
    string GitCommit,
    string WorkingTreeSourceSha256,
    string ImageId,
    IReadOnlyList<PressureBinaryIdentity> BinaryIdentities,
    PressureSustainedDiagnosticOptionsSnapshot Options,
    IReadOnlyList<string> Commands,
    double InitialProcessorPerformanceMedian,
    IReadOnlyList<PressureHostProcessorSample> InitialHostSamples,
    IReadOnlyList<PressureSustainedDiagnosticTrace> Traces,
    PressureSustainedDiagnosticCleanup Cleanup,
    PressureSustainedDiagnosticHostGateFailure? HostGateFailure,
    string? Failure,
    DateTime StartedUtc,
    DateTime CompletedUtc);
