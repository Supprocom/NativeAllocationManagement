namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public enum VoxelHandoffImplementation
{
    Managed,
    Native
}

public sealed record VoxelHandoffBenchmarkOptions(
    int WordCount,
    int Iterations,
    int WarmupIterations,
    int SampleCount,
    int Seed);

public sealed record VoxelHandoffWorkerEvidence(
    VoxelHandoffImplementation Implementation,
    int WordCount,
    int Iterations,
    int WarmupIterations,
    int Seed,
    long LogicalBytes,
    double SetupMilliseconds,
    double WarmupMilliseconds,
    double ElapsedMilliseconds,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
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
    bool ExactParity);

public sealed record VoxelHandoffPairEvidence(
    int SampleIndex,
    VoxelHandoffImplementation FirstImplementation,
    VoxelHandoffWorkerEvidence Managed,
    VoxelHandoffWorkerEvidence Native,
    double ManagedToNativeSpeedup);

public sealed record VoxelHandoffBenchmarkReport(
    VoxelHandoffBenchmarkOptions Options,
    VoxelHandoffPairEvidence[] Pairs,
    double ManagedMeanMilliseconds,
    double NativeMeanMilliseconds,
    double MeanPairedSpeedup,
    double PairedSpeedupConfidenceLower95,
    double ManagedMeanLogicalGigabytesPerSecond,
    double NativeMeanLogicalGigabytesPerSecond,
    bool ExactParity,
    bool BalancedOrder,
    bool PerformanceAdvantage,
    double TotalElapsedMilliseconds,
    DateTimeOffset CreatedUtc);
