namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public enum ConcurrentArenaBenchmarkImplementation
{
    ManagedArrays,
    NativePool,
    ShardedArenas,
    ConcurrentArena
}

public enum ConcurrentArenaValuePattern
{
    Constant,
    Sequential
}

public sealed record ConcurrentArenaBenchmarkOptions(
    int MapCount,
    int ValuesPerMap,
    int WorkerCount,
    int WarmupIterations,
    int Iterations,
    int SampleCount,
    int Seed,
    ConcurrentArenaValuePattern ValuePattern);

public sealed record ConcurrentArenaWorkerEvidence(
    ConcurrentArenaBenchmarkImplementation Implementation,
    ConcurrentArenaValuePattern ValuePattern,
    int MapCount,
    int ValuesPerMap,
    long TotalValues,
    int WorkerCount,
    int WarmupIterations,
    int Iterations,
    int Seed,
    long LogicalBytes,
    double BackingAllocationMilliseconds,
    double VerificationMilliseconds,
    double WarmupMilliseconds,
    double InitializationMilliseconds,
    double PublicationMilliseconds,
    double AccessMilliseconds,
    double DisposalMilliseconds,
    double FullPathMilliseconds,
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
    bool ExactParity);

public sealed record ConcurrentArenaPairEvidence(
    int SampleIndex,
    ConcurrentArenaBenchmarkImplementation[] ImplementationOrder,
    ConcurrentArenaWorkerEvidence ManagedArrays,
    ConcurrentArenaWorkerEvidence NativePool,
    ConcurrentArenaWorkerEvidence ShardedArenas,
    ConcurrentArenaWorkerEvidence ConcurrentArena,
    double ManagedToConcurrentArenaSpeedup,
    double NativePoolToConcurrentArenaSpeedup,
    double ShardedToConcurrentArenaSpeedup);

public sealed record ConcurrentArenaComparisonEvidence(
    ConcurrentArenaBenchmarkImplementation Baseline,
    double BaselineMeanMilliseconds,
    double ConcurrentArenaMeanMilliseconds,
    double MeanPairedSpeedup,
    double PairedSpeedupConfidenceLower95);

public sealed record ConcurrentArenaBenchmarkReport(
    ConcurrentArenaBenchmarkOptions Options,
    ConcurrentArenaPairEvidence[] Pairs,
    ConcurrentArenaComparisonEvidence[] Comparisons,
    double ManagedMeanAllocatedBytes,
    double ConcurrentArenaMeanAllocatedBytes,
    double ManagedMeanPeakWorkingSetBytes,
    double ConcurrentArenaMeanPeakWorkingSetBytes,
    bool ExactParity,
    bool BalancedOrder,
    bool RuntimeSettingsValid,
    bool PerformanceAdvantage,
    double TotalElapsedMilliseconds,
    DateTimeOffset CreatedUtc);
