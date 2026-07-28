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
    PressureCompilationConfiguration ChildCompilationConfiguration,
    DateTime StartedUtc,
    double WallElapsedMilliseconds,
    double? CompilerElapsedMilliseconds,
    CompilationOutcome Outcome,
    int? ExitCode,
    string Command,
    string StandardOutputTail,
    string StandardErrorTail);

public readonly record struct CompilationCompilerDiagnostics(
    double? SafeStandardDeviationMilliseconds,
    double? NamStandardDeviationMilliseconds,
    double? SafeFirstPositionMeanMilliseconds,
    double? SafeSecondPositionMeanMilliseconds,
    double? NamFirstPositionMeanMilliseconds,
    double? NamSecondPositionMeanMilliseconds);

public readonly record struct CompilationGateSummary(
    int PairCount,
    double MaximumNamToSafeRatio,
    double? SafeMeanCompilerMilliseconds,
    double? NamMeanCompilerMilliseconds,
    double? SafeMeanWallMilliseconds,
    double? NamMeanWallMilliseconds,
    double? NamToSafeRatio,
    double? NamToSafeWallRatio,
    CompilationCompilerDiagnostics CompilerDiagnostics,
    bool WarmupGatePassed,
    bool AllCompilationsCompleted,
    bool ChildConfigurationGatePassed,
    bool MeasuredOrderGatePassed,
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

public static class CompilationGatePolicy
{
    public const int DefaultWarmupPairCount = 1;
    public const int DefaultMeasuredPairCount = 6;
    public const double MaximumNamToSafeRatio = 1.10;

    public static PressureCompilationConfiguration
        RequiredChildCompilationConfiguration =>
        new("0", "0");

    public static bool IsValidMeasuredPairCount(int pairCount) =>
        pairCount > 0 && (pairCount & 1) == 0;

    public static bool IsWithinRatioLimit(double ratio) =>
        double.IsFinite(ratio)
        && ratio >= 0
        && ratio <= MaximumNamToSafeRatio;

    public static bool SafeRunsFirst(int measuredPair)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(measuredPair);
        return (measuredPair & 1) == 0;
    }

    public static bool HasValidWarmups(
        IReadOnlyList<CompilationSample> warmups,
        int warmupPairCount)
    {
        ArgumentNullException.ThrowIfNull(warmups);
        return warmupPairCount == DefaultWarmupPairCount
            && warmups.Count == checked(warmupPairCount * 2)
            && HasExpectedPairStructure(warmups, warmupPairCount)
            && warmups.All(IsCompletedCompilation)
            && warmups.All(HasRequiredChildConfiguration);
    }

    public static bool CanStartMeasuredBuilds(bool warmupGatePassed) =>
        warmupGatePassed;

    public static bool HasBalancedMeasuredOrder(
        IReadOnlyList<CompilationSample> samples,
        int measuredPairCount)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!IsValidMeasuredPairCount(measuredPairCount)
            || samples.Count != checked(measuredPairCount * 2))
        {
            return false;
        }

        return HasExpectedPairStructure(samples, measuredPairCount)
            && samples.Count(
            static sample =>
                sample.Position == 0
                && sample.Implementation == "SafeCSharp")
            == measuredPairCount / 2;
    }

    public static bool HasCompletedMeasuredCompilations(
        IReadOnlyList<CompilationSample> samples,
        int measuredPairCount)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return HasBalancedMeasuredOrder(samples, measuredPairCount)
            && samples.All(IsCompletedCompilation);
    }

    public static bool AllCompilationsCompleted(
        bool warmupGatePassed,
        bool measuredCompilationsCompleted) =>
        warmupGatePassed && measuredCompilationsCompleted;

    public static bool HasRequiredChildConfiguration(
        IReadOnlyList<CompilationSample> warmups,
        IReadOnlyList<CompilationSample> samples,
        int warmupPairCount,
        int measuredPairCount)
    {
        ArgumentNullException.ThrowIfNull(warmups);
        ArgumentNullException.ThrowIfNull(samples);
        if (warmupPairCount != DefaultWarmupPairCount
            || !IsValidMeasuredPairCount(measuredPairCount)
            || warmups.Count != checked(warmupPairCount * 2)
            || samples.Count != checked(measuredPairCount * 2))
        {
            return false;
        }

        return warmups
            .Concat(samples)
            .All(HasRequiredChildConfiguration);
    }

    public static bool AllGatesPassed(
        bool warmupGatePassed,
        bool childConfigurationGatePassed,
        bool measuredOrderGatePassed,
        bool compilerGatePassed,
        bool wallGatePassed) =>
        warmupGatePassed
        && childConfigurationGatePassed
        && measuredOrderGatePassed
        && compilerGatePassed
        && wallGatePassed;

    private static bool HasExpectedPairStructure(
        IReadOnlyList<CompilationSample> samples,
        int pairCount)
    {
        for (int pair = 0; pair < pairCount; pair++)
        {
            CompilationSample[] pairSamples = samples
                .Where(sample => sample.Pair == pair)
                .OrderBy(sample => sample.Position)
                .ToArray();
            if (pairSamples.Length != 2
                || pairSamples[0].Position != 0
                || pairSamples[1].Position != 1)
            {
                return false;
            }

            bool safeFirst = SafeRunsFirst(pair);
            if (!string.Equals(
                    pairSamples[0].Implementation,
                    safeFirst ? "SafeCSharp" : "NAM",
                    StringComparison.Ordinal)
                || !string.Equals(
                    pairSamples[1].Implementation,
                    safeFirst ? "NAM" : "SafeCSharp",
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCompletedCompilation(
        CompilationSample sample) =>
        sample.Outcome == CompilationOutcome.Completed
        && sample.CompilerElapsedMilliseconds is > 0
        && sample.WallElapsedMilliseconds > 0
        && sample.ExitCode == 0;

    private static bool HasRequiredChildConfiguration(
        CompilationSample sample)
    {
        PressureCompilationConfiguration required =
            RequiredChildCompilationConfiguration;
        return string.Equals(
                sample.ChildCompilationConfiguration.TieredCompilation,
                required.TieredCompilation,
                StringComparison.Ordinal)
            && string.Equals(
                sample.ChildCompilationConfiguration.TieredPgo,
                required.TieredPgo,
                StringComparison.Ordinal);
    }
}
