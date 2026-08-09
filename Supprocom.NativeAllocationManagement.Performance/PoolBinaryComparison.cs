using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class PoolBinaryComparison
{
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
        return report.ValidEvidence ? 0 : 3;
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

        using var baseline = new ProbeAssemblyHost(baselinePath);
        using var candidate = new ProbeAssemblyHost(candidatePath);
        PoolBinaryVariant[] preparationOrder =
        [
            PoolBinaryVariant.Baseline,
            PoolBinaryVariant.Candidate,
            PoolBinaryVariant.Candidate,
            PoolBinaryVariant.Baseline
        ];
        var preparations = new PoolBinaryPreparationEvidence[
            preparationOrder.Length];
        for (int index = 0; index < preparationOrder.Length; index++)
        {
            PoolBinaryVariant variant = preparationOrder[index];
            PoolExactHeadProbeReport result = variant
                == PoolBinaryVariant.Baseline
                    ? baseline.Run(warmupIterations, measuredIterations)
                    : candidate.Run(warmupIterations, measuredIterations);
            preparations[index] = new PoolBinaryPreparationEvidence(
                index,
                variant,
                result);
        }

        var pairs = new PoolBinaryPairEvidence[sampleCount];
        for (int sampleIndex = 0;
            sampleIndex < sampleCount;
            sampleIndex++)
        {
            PoolBinaryVariant first = GetFirstVariant(sampleIndex);
            PoolExactHeadProbeReport firstResult = first
                == PoolBinaryVariant.Baseline
                    ? baseline.Run(warmupIterations, measuredIterations)
                    : candidate.Run(warmupIterations, measuredIterations);
            PoolExactHeadProbeReport secondResult = first
                == PoolBinaryVariant.Baseline
                    ? candidate.Run(warmupIterations, measuredIterations)
                    : baseline.Run(warmupIterations, measuredIterations);
            PoolExactHeadProbeReport baselineResult = first
                == PoolBinaryVariant.Baseline
                    ? firstResult
                    : secondResult;
            PoolExactHeadProbeReport candidateResult = first
                == PoolBinaryVariant.Candidate
                    ? firstResult
                    : secondResult;
            bool exactParity = HasExactParity(
                baselineResult,
                candidateResult);
            pairs[sampleIndex] = new PoolBinaryPairEvidence(
                sampleIndex,
                first,
                baselineResult,
                candidateResult,
                exactParity,
                baselineResult.ElapsedMilliseconds
                    / candidateResult.ElapsedMilliseconds,
                baselineResult.ProcessorMilliseconds
                    / candidateResult.ProcessorMilliseconds);
        }

        double[] elapsedRatios = pairs
            .Select(static pair => pair.BaselineToCandidateSpeedup)
            .ToArray();
        double[] processorRatios = pairs
            .Select(static pair => pair.BaselineToCandidateProcessorSpeedup)
            .ToArray();
        bool balanced = pairs.Count(static pair =>
                pair.FirstVariant == PoolBinaryVariant.Baseline)
            == sampleCount / 2
            && pairs.Count(static pair =>
                pair.FirstVariant == PoolBinaryVariant.Candidate)
                == sampleCount / 2;
        bool exactOutput = pairs.All(static pair => pair.ExactParity)
            && preparations.All(static item =>
                item.Result.WarmupChecksum
                    == item.Result.WarmupIterations
                && item.Result.MeasuredChecksum
                    == item.Result.MeasuredIterations);
        bool zeroManagedAllocation = pairs.All(static pair =>
                pair.Baseline.ManagedAllocatedBytes == 0
                && pair.Candidate.ManagedAllocatedBytes == 0)
            && preparations.All(static item =>
                item.Result.ManagedAllocatedBytes == 0);
        bool zeroFreshSegments = pairs.All(static pair =>
                pair.Baseline.FreshSegmentAllocationDelta == 0
                && pair.Candidate.FreshSegmentAllocationDelta == 0)
            && preparations.All(static item =>
                item.Result.FreshSegmentAllocationDelta == 0);
        bool processorMeasurements = pairs.All(static pair =>
                IsPositiveFinite(pair.Baseline.ProcessorMilliseconds)
                && IsPositiveFinite(
                    pair.Candidate.ProcessorMilliseconds))
            && preparations.All(static item =>
                IsPositiveFinite(item.Result.ProcessorMilliseconds));
        bool runtimeConfiguration =
            IsDisabled("DOTNET_TieredCompilation")
            && IsDisabled("DOTNET_TieredPGO");
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
            preparations,
            pairs,
            pairs.Average(static pair =>
                pair.Baseline.ElapsedMilliseconds),
            pairs.Average(static pair =>
                pair.Candidate.ElapsedMilliseconds),
            elapsedRatios.Average(),
            pairs.Sum(static pair =>
                    pair.Baseline.ElapsedMilliseconds)
                / pairs.Sum(static pair =>
                    pair.Candidate.ElapsedMilliseconds),
            PairedBenchmarkStatistics.ConfidenceLower95(elapsedRatios),
            pairs.Average(static pair =>
                pair.Baseline.ProcessorMilliseconds),
            pairs.Average(static pair =>
                pair.Candidate.ProcessorMilliseconds),
            processorRatios.Average(),
            pairs.Sum(static pair =>
                    pair.Baseline.ProcessorMilliseconds)
                / pairs.Sum(static pair =>
                    pair.Candidate.ProcessorMilliseconds),
            PairedBenchmarkStatistics.ConfidenceLower95(processorRatios),
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Baseline,
                firstPosition: true),
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Baseline,
                firstPosition: false),
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Candidate,
                firstPosition: true),
            MeanForPosition(
                pairs,
                PoolBinaryVariant.Candidate,
                firstPosition: false),
            exactOutput,
            balanced,
            zeroManagedAllocation,
            zeroFreshSegments,
            processorMeasurements,
            runtimeConfiguration,
            exactOutput
                && balanced
                && zeroManagedAllocation
                && zeroFreshSegments
                && processorMeasurements
                && runtimeConfiguration,
            DateTimeOffset.UtcNow);
    }

    internal static PoolBinaryVariant GetFirstVariant(int sampleIndex) =>
        (sampleIndex & 1) == 0
            ? PoolBinaryVariant.Baseline
            : PoolBinaryVariant.Candidate;

    internal static void ValidateSampleCount(int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        if ((sampleCount & 1) != 0)
        {
            throw new ArgumentException(
                "The sample count must be positive and even.",
                nameof(sampleCount));
        }
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

    private static double MeanForPosition(
        IReadOnlyCollection<PoolBinaryPairEvidence> pairs,
        PoolBinaryVariant variant,
        bool firstPosition) =>
        pairs.Where(pair =>
                (pair.FirstVariant == variant) == firstPosition)
            .Average(pair => variant == PoolBinaryVariant.Baseline
                ? pair.Baseline.ElapsedMilliseconds
                : pair.Candidate.ElapsedMilliseconds);

    private static bool IsDisabled(string name) =>
        string.Equals(
            Environment.GetEnvironmentVariable(name),
            "0",
            StringComparison.Ordinal);

    private static bool IsPositiveFinite(double value) =>
        value > 0d && double.IsFinite(value);

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
    PoolBinaryVariant Variant,
    PoolExactHeadProbeReport Result);

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
    IReadOnlyList<PoolBinaryPreparationEvidence> Preparations,
    IReadOnlyList<PoolBinaryPairEvidence> Pairs,
    double BaselineMeanMilliseconds,
    double CandidateMeanMilliseconds,
    double PairedMeanSpeedup,
    double AggregateSpeedup,
    double ConfidenceLower95,
    double BaselineMeanProcessorMilliseconds,
    double CandidateMeanProcessorMilliseconds,
    double PairedMeanProcessorSpeedup,
    double AggregateProcessorSpeedup,
    double ProcessorConfidenceLower95,
    double BaselineFirstPositionMeanMilliseconds,
    double BaselineSecondPositionMeanMilliseconds,
    double CandidateFirstPositionMeanMilliseconds,
    double CandidateSecondPositionMeanMilliseconds,
    bool ExactParity,
    bool BalancedOrder,
    bool ZeroManagedAllocation,
    bool ZeroFreshSegments,
    bool ProcessorMeasurementsPassed,
    bool RuntimeConfigurationPassed,
    bool ValidEvidence,
    DateTimeOffset RecordedAtUtc);
