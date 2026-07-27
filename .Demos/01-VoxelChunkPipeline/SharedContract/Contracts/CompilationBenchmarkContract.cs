namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public enum CompilationOutcome
{
    Completed,
    DeadlineExceeded,
    BuildFailure,
    HarnessFailure
}

public readonly record struct CompilationSample(
    int Pair,
    int Position,
    string Implementation,
    DateTime StartedUtc,
    double WallElapsedMilliseconds,
    double? CompilerElapsedMilliseconds,
    CompilationOutcome Outcome,
    int? ExitCode,
    string Command,
    string StandardOutputTail,
    string StandardErrorTail);

public readonly record struct CompilationGateSummary(
    int PairCount,
    double MaximumNamToSafeRatio,
    double? SafeMeanCompilerMilliseconds,
    double? NamMeanCompilerMilliseconds,
    double? SafeMeanWallMilliseconds,
    double? NamMeanWallMilliseconds,
    double? NamToSafeRatio,
    double? NamToSafeWallRatio,
    bool AllCompilationsCompleted,
    bool CompilerGatePassed,
    bool WallGatePassed,
    bool GatePassed);

public readonly record struct CompilationGateReport(
    string GitCommit,
    DateTime StartedUtc,
    DateTime CompletedUtc,
    string DotnetSdk,
    string Configuration,
    string Measurement,
    int WarmupPairs,
    int MeasuredPairs,
    double PerCompilationDeadlineMilliseconds,
    IReadOnlyList<CompilationSample> Warmups,
    IReadOnlyList<CompilationSample> Samples,
    CompilationGateSummary Summary);
