using System.Text.Json.Serialization;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public enum NativeBuilderBenchmarkImplementation
{
    ManagedList,
    NativeBuilder,
    ManagedListPrefix,
    ManagedExactArray,
    NativeBuilderBudgeted,
    NativeBuilderBudgetedDirect
}

public sealed record NativeBuilderBenchmarkOptions(
    int ElementCount,
    int PreLease,
    int BatchSize,
    int Iterations,
    int WarmupIterations,
    int SampleCount,
    int Seed)
{
    /// <summary>The explicit managed comparison; known output sizing is the default.</summary>
    public NativeBuilderBenchmarkImplementation ManagedBaseline { get; init; } =
        NativeBuilderBenchmarkImplementation.ManagedExactArray;
    /// <summary>The native feature measured by the paired command; the legacy unbudgeted path remains explicit.</summary>
    public NativeBuilderBenchmarkImplementation NativeBaseline { get; init; } =
        NativeBuilderBenchmarkImplementation.NativeBuilderBudgeted;
    /// <summary>The budgeted worker's NAM-requested extent ceiling, not a managed or process-RSS cap.</summary>
    public long NativeBudgetCapacityBytes { get; init; } = long.MaxValue;
}

public sealed record NativeBuilderPhaseEvidence(
    double AllocationMilliseconds,
    double InitializationMilliseconds,
    double PublicationMilliseconds,
    double HandoffMilliseconds,
    double AccessMilliseconds,
    double DisposalMilliseconds,
    double TotalMilliseconds);

/// <summary>A process-wide observation, not allocation-byte volume or an owner budget.</summary>
public readonly record struct NativeBuilderNativeObservation(
    [property: JsonRequired] long MetricsEpoch,
    [property: JsonRequired] long AllocationCount,
    [property: JsonRequired] long ReallocationCount,
    [property: JsonRequired] long FreeCount,
    [property: JsonRequired] long OutstandingBytes,
    [property: JsonRequired] long DetachedBytes,
    [property: JsonRequired] long RetiredBytes,
    [property: JsonRequired] long CopiedBytes,
    [property: JsonRequired] bool HistoryOverflowed);

/// <summary>All implemented budget fields at one quiescent worker boundary; requested histories are not opaque physical allocation volume.</summary>
public readonly record struct NativeBuilderBudgetObservation(
    [property: JsonRequired] long BudgetId,
    [property: JsonRequired] long CapacityBytes,
    [property: JsonRequired] long CommittedBytes,
    [property: JsonRequired] long ReservedBytes,
    [property: JsonRequired] long PeakCommittedBytes,
    [property: JsonRequired] long PeakAdmittedBytes,
    [property: JsonRequired] long AllocationCount,
    [property: JsonRequired] long ReallocationCount,
    [property: JsonRequired] long FreeCount,
    [property: JsonRequired] long ActiveAllocationCount,
    [property: JsonRequired] long RejectedAllocationCount,
    [property: JsonRequired] long FailedAllocationCount,
    [property: JsonRequired] int TraceCapacity,
    [property: JsonRequired] int TraceCount,
    [property: JsonRequired] long DroppedTraceEventCount,
    [property: JsonRequired] bool TraceOverflowed,
    [property: JsonRequired] bool HistoryOverflowed,
    [property: JsonRequired] long AcquiredBackingBytes,
    [property: JsonRequired] long ReplacementBackingBytes);

/// <summary>Absolute counters at one worker-phase boundary; GC heap size is the last collection's observation.</summary>
public readonly record struct NativeBuilderWorkerObservation(
    [property: JsonRequired] long Timestamp,
    [property: JsonRequired] long ProcessCpuTicks,
    [property: JsonRequired] long ManagedAllocatedBytes,
    [property: JsonRequired] int Gen0Collections,
    [property: JsonRequired] int Gen1Collections,
    [property: JsonRequired] int Gen2Collections,
    [property: JsonRequired] long ManagedHeapBytes,
    [property: JsonRequired] NativeBuilderNativeObservation Native)
{
    /// <summary>Real worker-domain snapshot, or null where the domain is genuinely unavailable.</summary>
    [JsonRequired]
    public NativeBuilderBudgetObservation? Budget { get; init; }
}

/// <summary>Complete invocation of a phase, including its channel/task setup where applicable.</summary>
public readonly record struct NativeBuilderLifecyclePhaseEvidence(
    [property: JsonRequired] NativeBuilderWorkerObservation Before,
    [property: JsonRequired] NativeBuilderWorkerObservation After,
    [property: JsonRequired] double ElapsedMilliseconds);

/// <summary>Four separate observations, not a cold-start or forced-reclamation experiment.</summary>
public sealed record NativeBuilderLifecycleEvidence(
    [property: JsonRequired] NativeBuilderLifecyclePhaseEvidence PreparationAndValidation,
    [property: JsonRequired] NativeBuilderLifecyclePhaseEvidence Warmup,
    [property: JsonRequired] NativeBuilderLifecyclePhaseEvidence MeasuredBatch,
    [property: JsonRequired] NativeBuilderLifecyclePhaseEvidence SeparatePhaseProbe);

public sealed record NativeBuilderWorkerEvidence(
    NativeBuilderBenchmarkImplementation Implementation,
    int ElementCount,
    int OpaqueElementCount,
    int TransparentElementCount,
    int PreLease,
    int BatchSize,
    int Iterations,
    int WarmupIterations,
    int Seed,
    long LogicalBytes,
    double SetupMilliseconds,
    double WarmupMilliseconds,
    double ElapsedMilliseconds,
    double LogicalGigabytesPerSecond,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long ManagedHeapBeforeBytes,
    long ManagedHeapAfterBytes,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    long PeakWorkingSetBytes,
    long NativeRetainedBytes,
    long NativeFreshSegmentAllocations,
    long NativeFreshSegmentAllocationDelta,
    long Checksum,
    string ExactOutputSha256,
    string RuntimeInformationalVersion,
    string PerformanceInformationalVersion,
    string TieredCompilation,
    string TieredPgo,
    int ProcessorCount,
    bool ServerGc,
    NativeBuilderPhaseEvidence PhaseEvidence,
    bool ExactParity)
{
    [JsonRequired]
    public required NativeBuilderLifecycleEvidence LifecycleEvidence { get; init; }
}

public sealed record NativeBuilderPairEvidence(
    int SampleIndex,
    NativeBuilderBenchmarkImplementation FirstImplementation,
    NativeBuilderWorkerEvidence Managed,
    NativeBuilderWorkerEvidence Native,
    double ManagedToNativeSpeedup);

// The pair array is the persisted benchmark evidence schema.
#pragma warning disable CA1819
public sealed record NativeBuilderBenchmarkReport(
    NativeBuilderBenchmarkOptions Options,
    NativeBuilderPairEvidence[] Pairs,
    double ManagedMeanMilliseconds,
    double NativeMeanMilliseconds,
    double MeanPairedSpeedup,
    double PairedSpeedupConfidenceLower95,
    double ManagedMeanLogicalGigabytesPerSecond,
    double NativeMeanLogicalGigabytesPerSecond,
    double ManagedMeanAllocatedBytes,
    double NativeMeanAllocatedBytes,
    double ManagedMeanPeakWorkingSetBytes,
    double NativeMeanPeakWorkingSetBytes,
    bool ExactParity,
    bool BalancedOrder,
    bool PerformanceAdvantage,
    double TotalElapsedMilliseconds,
    DateTimeOffset CreatedUtc);
#pragma warning restore CA1819
