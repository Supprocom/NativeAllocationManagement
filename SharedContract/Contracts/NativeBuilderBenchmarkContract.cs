using System.Text.Json.Serialization;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public enum NativeBuilderBenchmarkImplementation
{
    ManagedList,
    NativeBuilder,
    ManagedListPrefix,
    ManagedExactArray
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

/// <summary>Absolute counters at one worker-phase boundary; GC heap size is the last collection's observation.</summary>
public readonly record struct NativeBuilderWorkerObservation(
    [property: JsonRequired] long Timestamp,
    [property: JsonRequired] long ProcessCpuTicks,
    [property: JsonRequired] long ManagedAllocatedBytes,
    [property: JsonRequired] int Gen0Collections,
    [property: JsonRequired] int Gen1Collections,
    [property: JsonRequired] int Gen2Collections,
    [property: JsonRequired] long ManagedHeapBytes,
    [property: JsonRequired] NativeBuilderNativeObservation Native);

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
