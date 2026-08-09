using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class PoolBinaryComparison
{
    internal const int MinimumSampleCount = 8;
    internal const int SubBatchCount = 64;
    internal const double MinimumObservationMilliseconds = 1_000d;
    internal const double MinimumProcessorResidency = 0.95d;
    internal const double MaximumProcessorResidency = 1.05d;
    internal const double MaximumWallProcessorDifference = 1.03d;
    internal const double MaximumPositionBias = 1.03d;
    internal const double MaximumTemporalDrift = 1.05d;
    internal const double MaximumCalibrationCenterBias = 1.05d;
    internal const double MaximumCalibrationUncertainty = 1.10d;

    private const string ProbeTypeName =
        "Supprocom.NativeAllocationManagement.Performance.PoolExactHeadProbe";

    internal static int RunCommand(string[] args)
    {
        string baselinePath = ReadRequiredOption(args, "--baseline");
        string candidatePath = ReadRequiredOption(args, "--candidate");
        int sampleCount = ReadIntOption(args, "--samples", 8);
        int warmupIterations = ReadIntOption(
            args,
            "--warmup-iterations",
            PoolExactHeadProbe.DefaultWarmupIterations);
        int measuredIterations = ReadIntOption(
            args,
            "--measured-iterations",
            PoolExactHeadProbe.DefaultMeasuredIterations);
        PoolBinaryComparisonReport report = Run(
            baselinePath,
            candidatePath,
            sampleCount,
            warmupIterations,
            measuredIterations);
        string json = JsonSerializer.Serialize(
            report,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        string? outputPath = ReadOptionalOption(args, "--output");
        if (outputPath is not null)
        {
            string fullPath = Path.GetFullPath(outputPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, json);
        }

        Console.WriteLine(json);
        if (!report.ValidEvidence)
        {
            return 3;
        }

        return HasOption(args, "--require-improvement")
            && report.Decision != PoolBinaryDecision.Improvement
                ? 4
                : 0;
    }

    internal static PoolBinaryComparisonReport Run(
        string baselinePath,
        string candidatePath,
        int sampleCount,
        int warmupIterations,
        int measuredIterations)
    {
        ValidateSampleCount(sampleCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            warmupIterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            measuredIterations);
        if (warmupIterations % SubBatchCount != 0
            || measuredIterations % SubBatchCount != 0)
        {
            throw new ArgumentException(
                "Iteration counts must be divisible by 64.");
        }

        using var calibrationLeft = new ProbeAssemblyHost(baselinePath);
        using var calibrationRight = new ProbeAssemblyHost(baselinePath);
        using var baseline = new ProbeAssemblyHost(baselinePath);
        using var candidate = new ProbeAssemblyHost(candidatePath);
        bool harnessIdentity = baseline.PerformanceSha256
            == candidate.PerformanceSha256;
        bool runtimeIdentity = calibrationLeft.RuntimeSha256
                == calibrationRight.RuntimeSha256
            && calibrationLeft.RuntimeSha256
                == baseline.RuntimeSha256;

        ProbeAssemblyHost[] preparationOrder =
        [
            calibrationLeft,
            baseline,
            candidate,
            calibrationRight,
            calibrationRight,
            candidate,
            baseline,
            calibrationLeft
        ];
        PoolBinaryHost[] preparationHosts =
        [
            PoolBinaryHost.CalibrationLeft,
            PoolBinaryHost.Baseline,
            PoolBinaryHost.Candidate,
            PoolBinaryHost.CalibrationRight,
            PoolBinaryHost.CalibrationRight,
            PoolBinaryHost.Candidate,
            PoolBinaryHost.Baseline,
            PoolBinaryHost.CalibrationLeft
        ];
        var preparations = new PoolBinaryPreparationEvidence[
            preparationOrder.Length];
        for (int index = 0; index < preparationOrder.Length; index++)
        {
            preparations[index] = new PoolBinaryPreparationEvidence(
                index,
                preparationHosts[index],
                preparationOrder[index].Run(
                    warmupIterations,
                    warmupIterations));
        }

        var calibrationPairs = new PoolBinaryPairEvidence[sampleCount];
        var comparisonPairs = new PoolBinaryPairEvidence[sampleCount];
        for (int sampleIndex = 0;
            sampleIndex < sampleCount;
            sampleIndex++)
        {
            PoolBinaryVariant first = GetFirstVariant(sampleIndex);
            if ((sampleIndex & 1) == 0)
            {
                calibrationPairs[sampleIndex] = MeasurePair(
                    sampleIndex,
                    first,
                    calibrationLeft,
                    calibrationRight,
                    warmupIterations,
                    measuredIterations);
                comparisonPairs[sampleIndex] = MeasurePair(
                    sampleIndex,
                    first,
                    baseline,
                    candidate,
                    warmupIterations,
                    measuredIterations);
            }
            else
            {
                comparisonPairs[sampleIndex] = MeasurePair(
                    sampleIndex,
                    first,
                    baseline,
                    candidate,
                    warmupIterations,
                    measuredIterations);
                calibrationPairs[sampleIndex] = MeasurePair(
                    sampleIndex,
                    first,
                    calibrationLeft,
                    calibrationRight,
                    warmupIterations,
                    measuredIterations);
            }
        }

        PoolBinaryMeasurementEvidence calibration = Summarize(
            calibrationPairs);
        PoolBinaryMeasurementEvidence comparison = Summarize(
            comparisonPairs);
        bool exactOutput = calibration.ExactParity
            && comparison.ExactParity
            && preparations.All(static item =>
                item.Result.WarmupChecksum
                    == item.Result.WarmupIterations
                && item.Result.MeasuredChecksum
                    == item.Result.MeasuredIterations);
        bool zeroManagedAllocation = calibration.ZeroManagedAllocation
            && comparison.ZeroManagedAllocation
            && preparations.All(static item =>
                item.Result.ManagedAllocatedBytes == 0);
        bool zeroFreshSegments = calibration.ZeroFreshSegments
            && comparison.ZeroFreshSegments
            && preparations.All(static item =>
                item.Result.FreshSegmentAllocationDelta == 0);
        bool runtimeConfiguration =
            IsDisabled("DOTNET_TieredCompilation")
            && IsDisabled("DOTNET_TieredPGO");
        bool calibrationEquivalent = IsCalibrationEquivalent(
            calibration);
        bool controlEnvelope = calibrationEquivalent
            && comparison.MinimumDurationPassed;
        bool validEvidence = harnessIdentity
            && runtimeIdentity
            && exactOutput
            && calibration.BalancedOrder
            && comparison.BalancedOrder
            && zeroManagedAllocation
            && zeroFreshSegments
            && runtimeConfiguration
            && controlEnvelope;
        PoolBinaryDecision decision = Decide(
            validEvidence,
            calibration,
            comparison);
        return new PoolBinaryComparisonReport(
            baseline.PerformancePath,
            candidate.PerformancePath,
            baseline.PerformanceSha256,
            candidate.PerformanceSha256,
            baseline.RuntimeSha256,
            candidate.RuntimeSha256,
            sampleCount,
            warmupIterations,
            measuredIterations,
            SubBatchCount,
            preparations,
            calibration,
            comparison,
            harnessIdentity,
            runtimeIdentity,
            exactOutput,
            zeroManagedAllocation,
            zeroFreshSegments,
            runtimeConfiguration,
            calibrationEquivalent,
            controlEnvelope,
            validEvidence,
            decision,
            DateTimeOffset.UtcNow);
    }

    internal static PoolBinaryVariant GetFirstVariant(int sampleIndex) =>
        (sampleIndex & 3) is 0 or 3
            ? PoolBinaryVariant.Baseline
            : PoolBinaryVariant.Candidate;

    internal static void ValidateSampleCount(int sampleCount)
    {
        if (sampleCount < MinimumSampleCount
            || (sampleCount & 7) != 0)
        {
            throw new ArgumentException(
                "The sample count must be a positive multiple of eight.",
                nameof(sampleCount));
        }
    }

    internal static PoolBinaryNoiseAssessment AssessNoise(
        IReadOnlyList<PoolBinaryPairEvidence> pairs)
    {
        bool minimumDuration = pairs.All(static pair =>
            HasMinimumDuration(pair.Baseline)
            && HasMinimumDuration(pair.Candidate));
        bool processorResidency = pairs.All(static pair =>
            HasValidProcessorResidency(pair.Baseline)
            && HasValidProcessorResidency(pair.Candidate));
        bool wallProcessorAgreement = pairs.All(static pair =>
            IsWithinFactor(
                pair.BaselineToCandidateSpeedup,
                pair.BaselineToCandidateProcessorSpeedup,
                MaximumWallProcessorDifference));
        bool positionBias = HasBoundedPositionBias(pairs);
        bool temporalDrift = HasBoundedTemporalDrift(pairs);
        return new PoolBinaryNoiseAssessment(
            minimumDuration,
            processorResidency,
            wallProcessorAgreement,
            positionBias,
            temporalDrift,
            minimumDuration
                && processorResidency
                && wallProcessorAgreement
                && positionBias
                && temporalDrift);
    }

    internal static bool IsCalibrationEquivalent(
        PoolBinaryMeasurementEvidence calibration) =>
        calibration.MinimumDurationPassed
        && calibration.BalancedOrder
        && IsWithinFactor(
            calibration.AggregateSpeedup,
            1d,
            MaximumCalibrationCenterBias)
        && IsWithinFactor(
            calibration.AggregateProcessorSpeedup,
            1d,
            MaximumCalibrationCenterBias)
        && calibration.ConfidenceLower95
            >= 1d / MaximumCalibrationUncertainty
        && calibration.ConfidenceUpper95
            <= MaximumCalibrationUncertainty
        && calibration.ProcessorConfidenceLower95
            >= 1d / MaximumCalibrationUncertainty
        && calibration.ProcessorConfidenceUpper95
            <= MaximumCalibrationUncertainty;

    internal static PoolBinaryDecision Decide(
        bool validEvidence,
        PoolBinaryMeasurementEvidence calibration,
        PoolBinaryMeasurementEvidence comparison)
    {
        if (!validEvidence)
        {
            return PoolBinaryDecision.Invalid;
        }

        double wallNoiseLimit = Math.Max(
            1d,
            calibration.ConfidenceUpper95);
        double processorNoiseLimit = Math.Max(
            1d,
            calibration.ProcessorConfidenceUpper95);
        if (comparison.ConfidenceLower95 > wallNoiseLimit
            && comparison.ProcessorConfidenceLower95
                > processorNoiseLimit)
        {
            return PoolBinaryDecision.Improvement;
        }

        double wallRegressionLimit = Math.Min(
            1d,
            calibration.ConfidenceLower95);
        double processorRegressionLimit = Math.Min(
            1d,
            calibration.ProcessorConfidenceLower95);
        if (comparison.ConfidenceUpper95 < wallRegressionLimit
            && comparison.ProcessorConfidenceUpper95
                < processorRegressionLimit)
        {
            return PoolBinaryDecision.Regression;
        }

        return PoolBinaryDecision.Inconclusive;
    }

    private static PoolBinaryPairEvidence MeasurePair(
        int sampleIndex,
        PoolBinaryVariant first,
        ProbeAssemblyHost baseline,
        ProbeAssemblyHost candidate,
        int warmupIterations,
        int measuredIterations)
    {
        int warmupBatch = warmupIterations / SubBatchCount;
        int measuredBatch = measuredIterations / SubBatchCount;
        var baselineResults = new PoolExactHeadProbeReport[SubBatchCount];
        var candidateResults = new PoolExactHeadProbeReport[SubBatchCount];
        for (int subBatch = 0; subBatch < SubBatchCount; subBatch++)
        {
            PoolBinaryVariant subBatchFirst = GetFirstVariant(subBatch);
            if (first == PoolBinaryVariant.Candidate)
            {
                subBatchFirst = Invert(subBatchFirst);
            }

            if (subBatchFirst == PoolBinaryVariant.Baseline)
            {
                baselineResults[subBatch] = baseline.Run(
                    warmupBatch,
                    measuredBatch);
                candidateResults[subBatch] = candidate.Run(
                    warmupBatch,
                    measuredBatch);
            }
            else
            {
                candidateResults[subBatch] = candidate.Run(
                    warmupBatch,
                    measuredBatch);
                baselineResults[subBatch] = baseline.Run(
                    warmupBatch,
                    measuredBatch);
            }
        }

        PoolExactHeadProbeReport baselineResult = Aggregate(
            baselineResults);
        PoolExactHeadProbeReport candidateResult = Aggregate(
            candidateResults);
        return new PoolBinaryPairEvidence(
            sampleIndex,
            first,
            baselineResult,
            candidateResult,
            HasExactParity(baselineResult, candidateResult),
            baselineResult.ElapsedMilliseconds
                / candidateResult.ElapsedMilliseconds,
            baselineResult.ProcessorMilliseconds
                / candidateResult.ProcessorMilliseconds);
    }

    private static PoolBinaryVariant Invert(PoolBinaryVariant variant) =>
        variant == PoolBinaryVariant.Baseline
            ? PoolBinaryVariant.Candidate
            : PoolBinaryVariant.Baseline;

    private static PoolExactHeadProbeReport Aggregate(
        IReadOnlyList<PoolExactHeadProbeReport> results)
    {
        int warmupIterations = results.Sum(static item =>
            item.WarmupIterations);
        int measuredIterations = results.Sum(static item =>
            item.MeasuredIterations);
        double elapsedMilliseconds = results.Sum(static item =>
            item.ElapsedMilliseconds);
        double processorMilliseconds = results.Sum(static item =>
            item.ProcessorMilliseconds);
        ulong reservedBytes = results[0].ReservedBytes;
        long retainedBytes = results[0].RetainedBytes;
        if (results.Any(item =>
                item.ReservedBytes != reservedBytes
                || item.RetainedBytes != retainedBytes))
        {
            throw new InvalidDataException(
                "A probe sub-batch changed its storage shape.");
        }

        return new PoolExactHeadProbeReport(
            warmupIterations,
            measuredIterations,
            reservedBytes,
            elapsedMilliseconds,
            processorMilliseconds,
            measuredIterations / (elapsedMilliseconds / 1_000d),
            measuredIterations / (processorMilliseconds / 1_000d),
            results.Sum(static item => item.ManagedAllocatedBytes),
            results.Sum(static item => item.FreshSegmentAllocationDelta),
            retainedBytes,
            results.Sum(static item => item.WarmupChecksum),
            results.Sum(static item => item.MeasuredChecksum));
    }

    internal static PoolBinaryMeasurementEvidence Summarize(
        PoolBinaryPairEvidence[] pairs)
    {
        double[] elapsedRatios = pairs
            .Select(static pair => pair.BaselineToCandidateSpeedup)
            .ToArray();
        double[] processorRatios = pairs
            .Select(static pair =>
                pair.BaselineToCandidateProcessorSpeedup)
            .ToArray();
        (double elapsedLower, double elapsedUpper) =
            PairedBenchmarkStatistics.RatioConfidence95(
                elapsedRatios);
        (double processorLower, double processorUpper) =
            PairedBenchmarkStatistics.RatioConfidence95(
                processorRatios);
        bool balanced = pairs.Count(static pair =>
                pair.FirstVariant == PoolBinaryVariant.Baseline)
            == pairs.Length / 2
            && pairs.Count(static pair =>
                pair.FirstVariant == PoolBinaryVariant.Candidate)
                == pairs.Length / 2;
        bool exactParity = pairs.All(static pair => pair.ExactParity);
        bool zeroManagedAllocation = pairs.All(static pair =>
            pair.Baseline.ManagedAllocatedBytes == 0
            && pair.Candidate.ManagedAllocatedBytes == 0);
        bool zeroFreshSegments = pairs.All(static pair =>
            pair.Baseline.FreshSegmentAllocationDelta == 0
            && pair.Candidate.FreshSegmentAllocationDelta == 0);
        PoolBinaryNoiseAssessment noise = AssessNoise(pairs);
        return new PoolBinaryMeasurementEvidence(
            pairs,
            pairs.Average(static pair =>
                pair.Baseline.ElapsedMilliseconds),
            pairs.Average(static pair =>
                pair.Candidate.ElapsedMilliseconds),
            PairedBenchmarkStatistics.GeometricMean(elapsedRatios),
            pairs.Sum(static pair =>
                    pair.Baseline.ElapsedMilliseconds)
                / pairs.Sum(static pair =>
                    pair.Candidate.ElapsedMilliseconds),
            elapsedLower,
            elapsedUpper,
            pairs.Average(static pair =>
                pair.Baseline.ProcessorMilliseconds),
            pairs.Average(static pair =>
                pair.Candidate.ProcessorMilliseconds),
            PairedBenchmarkStatistics.GeometricMean(processorRatios),
            pairs.Sum(static pair =>
                    pair.Baseline.ProcessorMilliseconds)
                / pairs.Sum(static pair =>
                    pair.Candidate.ProcessorMilliseconds),
            processorLower,
            processorUpper,
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Baseline,
                firstPosition: true,
                processor: false),
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Baseline,
                firstPosition: false,
                processor: false),
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Candidate,
                firstPosition: true,
                processor: false),
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Candidate,
                firstPosition: false,
                processor: false),
            exactParity,
            balanced,
            zeroManagedAllocation,
            zeroFreshSegments,
            noise.MinimumDurationPassed,
            noise.ProcessorResidencyPassed,
            noise.WallProcessorAgreementPassed,
            noise.PositionBiasPassed,
            noise.TemporalDriftPassed,
            noise.StrictHostStabilityPassed);
    }

    private static bool HasExactParity(
        PoolExactHeadProbeReport baseline,
        PoolExactHeadProbeReport candidate) =>
        baseline.WarmupIterations == candidate.WarmupIterations
        && baseline.MeasuredIterations == candidate.MeasuredIterations
        && baseline.ReservedBytes == candidate.ReservedBytes
        && baseline.WarmupChecksum == candidate.WarmupChecksum
        && baseline.MeasuredChecksum == candidate.MeasuredChecksum
        && baseline.RetainedBytes == candidate.RetainedBytes;

    private static bool HasMinimumDuration(
        PoolExactHeadProbeReport result) =>
        result.ElapsedMilliseconds >= MinimumObservationMilliseconds
        && result.ProcessorMilliseconds
            >= MinimumObservationMilliseconds;

    private static bool HasValidProcessorResidency(
        PoolExactHeadProbeReport result)
    {
        double residency = result.ProcessorMilliseconds
            / result.ElapsedMilliseconds;
        return residency >= MinimumProcessorResidency
            && residency <= MaximumProcessorResidency;
    }

    private static bool HasBoundedPositionBias(
        IReadOnlyList<PoolBinaryPairEvidence> pairs) =>
        IsWithinFactor(
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Baseline,
                firstPosition: true,
                processor: false),
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Baseline,
                firstPosition: false,
                processor: false),
            MaximumPositionBias)
        && IsWithinFactor(
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Candidate,
                firstPosition: true,
                processor: false),
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Candidate,
                firstPosition: false,
                processor: false),
            MaximumPositionBias)
        && IsWithinFactor(
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Baseline,
                firstPosition: true,
                processor: true),
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Baseline,
                firstPosition: false,
                processor: true),
            MaximumPositionBias)
        && IsWithinFactor(
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Candidate,
                firstPosition: true,
                processor: true),
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Candidate,
                firstPosition: false,
                processor: true),
            MaximumPositionBias);

    private static bool HasBoundedTemporalDrift(
        IReadOnlyList<PoolBinaryPairEvidence> pairs)
    {
        int middle = pairs.Count / 2;
        return HasBoundedTemporalDrift(
                pairs,
                middle,
                PoolBinaryVariant.Baseline,
                processor: false)
            && HasBoundedTemporalDrift(
                pairs,
                middle,
                PoolBinaryVariant.Candidate,
                processor: false)
            && HasBoundedTemporalDrift(
                pairs,
                middle,
                PoolBinaryVariant.Baseline,
                processor: true)
            && HasBoundedTemporalDrift(
                pairs,
                middle,
                PoolBinaryVariant.Candidate,
                processor: true);
    }

    private static bool HasBoundedTemporalDrift(
        IReadOnlyList<PoolBinaryPairEvidence> pairs,
        int middle,
        PoolBinaryVariant variant,
        bool processor)
    {
        double first = MeanForRange(
            pairs,
            0,
            middle,
            variant,
            processor);
        double second = MeanForRange(
            pairs,
            middle,
            pairs.Count,
            variant,
            processor);
        return IsWithinFactor(first, second, MaximumTemporalDrift);
    }

    private static double MeanForPosition(
        IReadOnlyCollection<PoolBinaryPairEvidence> pairs,
        PoolBinaryVariant variant,
        bool firstPosition,
        bool processor) =>
        pairs.Where(pair =>
                (pair.FirstVariant == variant) == firstPosition)
            .Average(pair => ReadTime(pair, variant, processor));

    private static double MeanForRange(
        IReadOnlyList<PoolBinaryPairEvidence> pairs,
        int start,
        int end,
        PoolBinaryVariant variant,
        bool processor)
    {
        double total = 0d;
        for (int index = start; index < end; index++)
        {
            total += ReadTime(pairs[index], variant, processor);
        }

        return total / (end - start);
    }

    private static double ReadTime(
        PoolBinaryPairEvidence pair,
        PoolBinaryVariant variant,
        bool processor)
    {
        PoolExactHeadProbeReport result = variant
            == PoolBinaryVariant.Baseline
                ? pair.Baseline
                : pair.Candidate;
        return processor
            ? result.ProcessorMilliseconds
            : result.ElapsedMilliseconds;
    }

    private static bool IsWithinFactor(
        double first,
        double second,
        double maximumFactor)
    {
        if (!IsPositiveFinite(first) || !IsPositiveFinite(second))
        {
            return false;
        }

        double ratio = first / second;
        return ratio >= 1d / maximumFactor
            && ratio <= maximumFactor;
    }

    private static bool IsDisabled(string name) =>
        string.Equals(
            Environment.GetEnvironmentVariable(name),
            "0",
            StringComparison.Ordinal);

    private static bool IsPositiveFinite(double value) =>
        value > 0d && double.IsFinite(value);

    private static bool HasOption(string[] args, string name) =>
        args.Contains(name, StringComparer.Ordinal);

    private static int ReadIntOption(
        string[] args,
        string name,
        int fallback)
    {
        string? value = ReadOptionalOption(args, name);
        return value is null
            ? fallback
            : int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string ReadRequiredOption(
        string[] args,
        string name) =>
        ReadOptionalOption(args, name)
        ?? throw new ArgumentException(
            $"The required option {name} is missing.",
            nameof(args));

    private static string? ReadOptionalOption(
        string[] args,
        string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == name)
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private sealed class ProbeAssemblyHost : IDisposable
    {
        private readonly ProbeLoadContext _context;
        private readonly MethodInfo _run;
        private readonly PropertyInfo _warmupIterations;
        private readonly PropertyInfo _measuredIterations;
        private readonly PropertyInfo _reservedBytes;
        private readonly PropertyInfo _elapsedMilliseconds;
        private readonly PropertyInfo _processorMilliseconds;
        private readonly PropertyInfo _operationsPerSecond;
        private readonly PropertyInfo _processorOperationsPerSecond;
        private readonly PropertyInfo _managedAllocatedBytes;
        private readonly PropertyInfo _freshSegmentDelta;
        private readonly PropertyInfo _retainedBytes;
        private readonly PropertyInfo _warmupChecksum;
        private readonly PropertyInfo _measuredChecksum;

        internal ProbeAssemblyHost(string performancePath)
        {
            PerformancePath = Path.GetFullPath(performancePath);
            if (!File.Exists(PerformancePath))
            {
                throw new FileNotFoundException(
                    "The performance assembly does not exist.",
                    PerformancePath);
            }

            string directory = Path.GetDirectoryName(PerformancePath)!;
            string runtimePath = Path.Combine(
                directory,
                "Supprocom.NativeAllocationManagement.dll");
            if (!File.Exists(runtimePath))
            {
                throw new FileNotFoundException(
                    "The runtime assembly does not exist.",
                    runtimePath);
            }

            PerformanceSha256 = HashFile(PerformancePath);
            RuntimeSha256 = HashFile(runtimePath);
            _context = new ProbeLoadContext(directory);
            Assembly assembly = _context.LoadFromAssemblyPath(
                PerformancePath);
            Type probeType = assembly.GetType(
                    ProbeTypeName,
                    throwOnError: true)!
                ?? throw new TypeLoadException(
                    "The Pool probe type is missing.");
            _run = probeType.GetMethod(
                    "Run",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    [typeof(int), typeof(int)],
                    modifiers: null)
                ?? throw new MissingMethodException(
                    ProbeTypeName,
                    "Run");
            Type reportType = _run.ReturnType;
            _warmupIterations = GetProperty(
                reportType,
                "WarmupIterations");
            _measuredIterations = GetProperty(
                reportType,
                "MeasuredIterations");
            _reservedBytes = GetProperty(reportType, "ReservedBytes");
            _elapsedMilliseconds = GetProperty(
                reportType,
                "ElapsedMilliseconds");
            _processorMilliseconds = GetProperty(
                reportType,
                "ProcessorMilliseconds");
            _operationsPerSecond = GetProperty(
                reportType,
                "MeasuredOperationsPerSecond");
            _processorOperationsPerSecond = GetProperty(
                reportType,
                "MeasuredProcessorOperationsPerSecond");
            _managedAllocatedBytes = GetProperty(
                reportType,
                "ManagedAllocatedBytes");
            _freshSegmentDelta = GetProperty(
                reportType,
                "FreshSegmentAllocationDelta");
            _retainedBytes = GetProperty(reportType, "RetainedBytes");
            _warmupChecksum = GetProperty(reportType, "WarmupChecksum");
            _measuredChecksum = GetProperty(
                reportType,
                "MeasuredChecksum");
        }

        internal string PerformancePath { get; }

        internal string PerformanceSha256 { get; }

        internal string RuntimeSha256 { get; }

        internal PoolExactHeadProbeReport Run(
            int warmupIterations,
            int measuredIterations)
        {
            object result;
            try
            {
                result = _run.Invoke(
                    null,
                    [warmupIterations, measuredIterations])!;
            }
            catch (TargetInvocationException exception)
                when (exception.InnerException is not null)
            {
                throw new InvalidOperationException(
                    "The loaded Pool probe failed.",
                    exception.InnerException);
            }

            return new PoolExactHeadProbeReport(
                Read<int>(_warmupIterations, result),
                Read<int>(_measuredIterations, result),
                Read<ulong>(_reservedBytes, result),
                Read<double>(_elapsedMilliseconds, result),
                Read<double>(_processorMilliseconds, result),
                Read<double>(_operationsPerSecond, result),
                Read<double>(_processorOperationsPerSecond, result),
                Read<long>(_managedAllocatedBytes, result),
                Read<long>(_freshSegmentDelta, result),
                Read<long>(_retainedBytes, result),
                Read<long>(_warmupChecksum, result),
                Read<long>(_measuredChecksum, result));
        }

        public void Dispose() => _context.Unload();

        private static PropertyInfo GetProperty(Type type, string name) =>
            type.GetProperty(name)
            ?? throw new MissingMemberException(type.FullName, name);

        private static TValue Read<TValue>(
            PropertyInfo property,
            object instance) =>
            (TValue)property.GetValue(instance)!;

        private static string HashFile(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private sealed class ProbeLoadContext : AssemblyLoadContext
    {
        private readonly string _directory;

        internal ProbeLoadContext(string directory)
            : base(isCollectible: true)
        {
            _directory = directory;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string path = Path.Combine(
                _directory,
                $"{assemblyName.Name}.dll");
            return File.Exists(path)
                ? LoadFromAssemblyPath(path)
                : null;
        }
    }
}

internal enum PoolBinaryVariant
{
    Baseline,
    Candidate
}

internal enum PoolBinaryHost
{
    CalibrationLeft,
    CalibrationRight,
    Baseline,
    Candidate
}

internal enum PoolBinaryDecision
{
    Invalid,
    Inconclusive,
    Improvement,
    Regression
}

internal readonly record struct PoolBinaryPairEvidence(
    int SampleIndex,
    PoolBinaryVariant FirstVariant,
    PoolExactHeadProbeReport Baseline,
    PoolExactHeadProbeReport Candidate,
    bool ExactParity,
    double BaselineToCandidateSpeedup,
    double BaselineToCandidateProcessorSpeedup);

internal readonly record struct PoolBinaryPreparationEvidence(
    int Sequence,
    PoolBinaryHost Host,
    PoolExactHeadProbeReport Result);

internal readonly record struct PoolBinaryNoiseAssessment(
    bool MinimumDurationPassed,
    bool ProcessorResidencyPassed,
    bool WallProcessorAgreementPassed,
    bool PositionBiasPassed,
    bool TemporalDriftPassed,
    bool StrictHostStabilityPassed);

internal readonly record struct PoolBinaryMeasurementEvidence(
    IReadOnlyList<PoolBinaryPairEvidence> Pairs,
    double BaselineMeanMilliseconds,
    double CandidateMeanMilliseconds,
    double PairedGeometricMeanSpeedup,
    double AggregateSpeedup,
    double ConfidenceLower95,
    double ConfidenceUpper95,
    double BaselineMeanProcessorMilliseconds,
    double CandidateMeanProcessorMilliseconds,
    double PairedGeometricMeanProcessorSpeedup,
    double AggregateProcessorSpeedup,
    double ProcessorConfidenceLower95,
    double ProcessorConfidenceUpper95,
    double BaselineFirstPositionMeanMilliseconds,
    double BaselineSecondPositionMeanMilliseconds,
    double CandidateFirstPositionMeanMilliseconds,
    double CandidateSecondPositionMeanMilliseconds,
    bool ExactParity,
    bool BalancedOrder,
    bool ZeroManagedAllocation,
    bool ZeroFreshSegments,
    bool MinimumDurationPassed,
    bool ProcessorResidencyPassed,
    bool WallProcessorAgreementPassed,
    bool PositionBiasPassed,
    bool TemporalDriftPassed,
    bool StrictHostStabilityPassed);

internal readonly record struct PoolBinaryComparisonReport(
    string BaselinePerformancePath,
    string CandidatePerformancePath,
    string BaselinePerformanceSha256,
    string CandidatePerformanceSha256,
    string BaselineRuntimeSha256,
    string CandidateRuntimeSha256,
    int SampleCount,
    int WarmupIterations,
    int MeasuredIterations,
    int SubBatchCount,
    IReadOnlyList<PoolBinaryPreparationEvidence> Preparations,
    PoolBinaryMeasurementEvidence Calibration,
    PoolBinaryMeasurementEvidence Comparison,
    bool HarnessIdentityPassed,
    bool RuntimeIdentityPassed,
    bool ExactParity,
    bool ZeroManagedAllocation,
    bool ZeroFreshSegments,
    bool RuntimeConfigurationPassed,
    bool CalibrationEquivalent,
    bool ControlEnvelopePassed,
    bool ValidEvidence,
    PoolBinaryDecision Decision,
    DateTimeOffset RecordedAtUtc);
