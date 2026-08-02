namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public enum NativeBuilderBenchmarkImplementation
{
    ManagedList,
    NativeBuilder
}

public sealed record NativeBuilderBenchmarkOptions(
    int ElementCount,
    int PreLease,
    int BatchSize,
    int Iterations,
    int WarmupIterations,
    int SampleCount,
    int Seed);

public sealed record NativeBuilderPhaseEvidence(
    double AllocationMilliseconds,
    double InitializationMilliseconds,
    double PublicationMilliseconds,
    double HandoffMilliseconds,
    double AccessMilliseconds,
    double DisposalMilliseconds,
    double TotalMilliseconds);

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
    bool ExactParity);

public sealed record NativeBuilderPairEvidence(
    int SampleIndex,
    NativeBuilderBenchmarkImplementation FirstImplementation,
    NativeBuilderWorkerEvidence Managed,
    NativeBuilderWorkerEvidence Native,
    double ManagedToNativeSpeedup);

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
