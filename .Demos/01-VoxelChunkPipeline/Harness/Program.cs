using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.Harness;

internal static class Program
{
    private const int DefaultPairs = 30;

    private static int Main(string[] args)
    {
        try
        {
            string root = FindRepositoryRoot();
            HarnessArguments parsed = HarnessArguments.Parse(args);
            RunSafetyScan(root);
            CorrectnessReport correctness = RunCorrectness(root, parsed.Workload);
            Console.WriteLine(JsonSerializer.Serialize(correctness, VoxelJson.Options));
            if (parsed.CorrectnessOnly)
            {
                return correctness.Passed ? 0 : 2;
            }

            BenchmarkReport report = RunBenchmark(root, parsed);
            string outputPath = parsed.OutputPath ?? Path.Combine(
                root,
                "artifacts",
                $"voxel-pipeline-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(report, VoxelJson.Options));
            Console.WriteLine(JsonSerializer.Serialize(report.Summary, VoxelJson.Options));
            if (parsed.Enforce && !report.Summary.GatePassed)
            {
                return 3;
            }

            return report.Summary.CorrectnessPassed ? 0 : 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 2;
        }
    }

    private static CorrectnessReport RunCorrectness(string root, VoxelWorkloadOptions options)
    {
        ChildRunResult safe = RunChild(root, "SafeCSharp", options, "--correctness");
        ChildRunResult nam = RunChild(root, "NAM", options, "--correctness");
        bool parity = SameWork(safe.Result, nam.Result);
        if (!parity)
        {
            throw new InvalidDataException("SafeCSharp and NAM correctness outputs differ.");
        }

        if (nam.Result.FinalNativeBackingBytes != 0)
        {
            throw new InvalidDataException("NAM did not return final native backing to zero.");
        }

        return new CorrectnessReport(
            parity,
            safe.Result,
            nam.Result,
            safe.Result.ManagedPayloadObjectBytes == nam.Result.ManagedPayloadObjectBytes);
    }

    private static BenchmarkReport RunBenchmark(string root, HarnessArguments parsed)
    {
        for (int warmup = 0; warmup < 2; warmup++)
        {
            _ = RunChild(root, "SafeCSharp", parsed.Workload, "--run");
            _ = RunChild(root, "NAM", parsed.Workload, "--run");
        }

        List<PairedSample> samples = new(parsed.Pairs);
        for (int pair = 0; pair < parsed.Pairs; pair++)
        {
            ChildRunResult safe;
            ChildRunResult nam;
            if ((pair & 1) == 0)
            {
                safe = RunChild(root, "SafeCSharp", parsed.Workload, "--run");
                nam = RunChild(root, "NAM", parsed.Workload, "--run");
            }
            else
            {
                nam = RunChild(root, "NAM", parsed.Workload, "--run");
                safe = RunChild(root, "SafeCSharp", parsed.Workload, "--run");
            }

            if (!SameWork(safe.Result, nam.Result))
            {
                throw new InvalidDataException($"Correctness parity failed in measured pair {pair}.");
            }

            samples.Add(new PairedSample(pair, safe, nam));
        }

        BenchmarkSummary summary = Summarize(parsed.Workload, samples);
        return new BenchmarkReport(
            DateTime.UtcNow,
            parsed.Workload,
            parsed.Pairs,
            summary.CorrectnessPassed,
            samples,
            summary);
    }

    private static BenchmarkSummary Summarize(VoxelWorkloadOptions options, IReadOnlyList<PairedSample> samples)
    {
        double work = checked((double)options.ChunkCount * options.Iterations);
        double safeMeanMs = Mean(samples.Select(sample => sample.Safe.ElapsedMilliseconds));
        double namMeanMs = Mean(samples.Select(sample => sample.Nam.ElapsedMilliseconds));
        double safeMeanThroughput = work / (safeMeanMs / 1000.0);
        double namMeanThroughput = work / (namMeanMs / 1000.0);
        double[] speedups = samples.Select(sample =>
            sample.Safe.ElapsedMilliseconds / sample.Nam.ElapsedMilliseconds).OrderBy(value => value).ToArray();
        double speedupMean = speedups.Average();
        double speedupStdDev = StandardDeviation(speedups);
        double confidenceHalfWidth = 1.96 * speedupStdDev / Math.Sqrt(speedups.Length);
        double lowerConfidence = speedupMean - confidenceHalfWidth;
        double safeManaged = Mean(samples.Select(sample => (double)sample.Safe.ManagedAllocatedBytes));
        double namManaged = Mean(samples.Select(sample => (double)sample.Nam.ManagedAllocatedBytes));
        double safeHeap = Mean(samples.Select(sample => (double)sample.Safe.HeapBytesAfterRun));
        double namHeap = Mean(samples.Select(sample => (double)sample.Nam.HeapBytesAfterRun));
        double safeLoh = Mean(samples.Select(sample => (double)sample.Safe.LargeObjectHeapBytesAfterRun));
        double namLoh = Mean(samples.Select(sample => (double)sample.Nam.LargeObjectHeapBytesAfterRun));
        double safeWorkingSet = Mean(samples.Select(sample => (double)sample.Safe.PeakWorkingSetBytes));
        double namWorkingSet = Mean(samples.Select(sample => (double)sample.Nam.PeakWorkingSetBytes));
        double safePause = Mean(samples.Select(sample => (double)sample.Safe.GcPauseMilliseconds));
        double namPause = Mean(samples.Select(sample => (double)sample.Nam.GcPauseMilliseconds));
        bool correctness = samples.All(sample =>
            SameWork(sample.Safe.Result, sample.Nam.Result) &&
            sample.Nam.Result.FinalNativeBackingBytes == 0);
        bool materiallyLessManaged = namManaged <= safeManaged * 0.90;
        bool throughputGate = namMeanThroughput >= safeMeanThroughput * 1.05;
        bool confidenceGate = lowerConfidence > 1.00;
        bool gate = correctness && throughputGate && confidenceGate && materiallyLessManaged;
        return new BenchmarkSummary(
            correctness,
            gate,
            safeMeanMs,
            namMeanMs,
            safeMeanThroughput,
            namMeanThroughput,
            speedupMean,
            speedupStdDev,
            lowerConfidence,
            speedupMean + confidenceHalfWidth,
            safeManaged,
            namManaged,
            materiallyLessManaged,
            throughputGate,
            confidenceGate,
            Percentile(samples.Select(sample => sample.Safe.ElapsedMilliseconds), 0.50),
            Percentile(samples.Select(sample => sample.Safe.ElapsedMilliseconds), 0.95),
            Percentile(samples.Select(sample => sample.Safe.ElapsedMilliseconds), 0.99),
            Percentile(samples.Select(sample => sample.Nam.ElapsedMilliseconds), 0.50),
            Percentile(samples.Select(sample => sample.Nam.ElapsedMilliseconds), 0.95),
            Percentile(samples.Select(sample => sample.Nam.ElapsedMilliseconds), 0.99),
            Mean(samples.Select(sample => (double)sample.Safe.Result.PeakManagedBackingBytes)),
            Mean(samples.Select(sample => (double)sample.Nam.Result.PeakManagedBackingBytes)),
            Mean(samples.Select(sample => (double)sample.Nam.Result.PeakNativeBackingBytes)),
            Mean(samples.Select(sample => (double)sample.Nam.Result.PeakRetainedNativeBackingBytes)),
            Mean(samples.Select(sample => (double)sample.Nam.Result.FinalNativeBackingBytes)),
            Mean(samples.Select(sample => (double)sample.Safe.Result.PeakCoordinateStageBytes)),
            Mean(samples.Select(sample => (double)sample.Safe.Result.PeakFaceStageBytes)),
            Mean(samples.Select(sample => (double)sample.Safe.Result.PeakPackingStageBytes)),
            Mean(samples.Select(sample => (double)sample.Nam.Result.PeakCoordinateStageBytes)),
            Mean(samples.Select(sample => (double)sample.Nam.Result.PeakFaceStageBytes)),
            Mean(samples.Select(sample => (double)sample.Nam.Result.PeakPackingStageBytes)),
            samples.Sum(sample => sample.Nam.Result.RentCount) / (double)samples.Count,
            samples.Sum(sample => sample.Nam.Result.ScopedRecycleCount) / (double)samples.Count,
            samples.Sum(sample => sample.Nam.Result.ClearedBytes) / (double)samples.Count)
        {
            SafeMeanHeapBytes = safeHeap,
            NamMeanHeapBytes = namHeap,
            SafeMeanLargeObjectHeapBytes = safeLoh,
            NamMeanLargeObjectHeapBytes = namLoh,
            SafeMeanPeakWorkingSetBytes = safeWorkingSet,
            NamMeanPeakWorkingSetBytes = namWorkingSet,
            SafeMeanGcPauseMilliseconds = safePause,
            NamMeanGcPauseMilliseconds = namPause
        };
    }

    private static ChildRunResult RunChild(string root, string implementation, VoxelWorkloadOptions options, string mode)
    {
        string projectDirectory = Path.Combine(root, ".Demos", "01-VoxelChunkPipeline", implementation);
        string assemblyName = implementation == "SafeCSharp"
            ? "VoxelChunkPipeline.SafeCSharp"
            : "VoxelChunkPipeline.NAM";
        string assemblyPath = Path.Combine(projectDirectory, "bin", "Release", "net10.0", assemblyName + ".dll");
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"Build the {implementation} child before running the harness.", assemblyPath);
        }

        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add("--seed");
        start.ArgumentList.Add(options.Seed.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--chunks");
        start.ArgumentList.Add(options.ChunkCount.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--workers");
        start.ArgumentList.Add(options.WorkerCount.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--iterations");
        start.ArgumentList.Add(options.Iterations.ToString(CultureInfo.InvariantCulture));
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start child process.");
        if (!process.WaitForExit(180_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{implementation} child exceeded the 180 second bound.");
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{implementation} child failed with exit {process.ExitCode}: {stderr}");
        }

        string? json = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (json is null)
        {
            throw new InvalidDataException($"{implementation} child produced no JSON result. stderr: {stderr}");
        }

        return ChildRunResult.FromJson(json);
    }

    private static void RunSafetyScan(string root)
    {
        string demoRoot = Path.Combine(root, ".Demos", "01-VoxelChunkPipeline");
        string safeProject = File.ReadAllText(Path.Combine(demoRoot, "SafeCSharp", "SafeCSharp.csproj"));
        string namProject = File.ReadAllText(Path.Combine(demoRoot, "NAM", "NAM.csproj"));
        string contractSource = File.ReadAllText(Path.Combine(demoRoot, "SharedContract", "VoxelContract.cs"));
        string namSource = File.ReadAllText(Path.Combine(demoRoot, "NAM", "NativeVoxelPipeline.cs"));
        if (!safeProject.Contains("<AllowUnsafeBlocks>false</AllowUnsafeBlocks>", StringComparison.Ordinal)
            || !namProject.Contains("<AllowUnsafeBlocks>false</AllowUnsafeBlocks>", StringComparison.Ordinal))
        {
            throw new InvalidDataException("SafeCSharp and NAM must compile with unsafe blocks disabled.");
        }

        if (!contractSource.Contains("stackalloc", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The bounded section classifier must retain its stack-based masks and counters.");
        }

        if (!namProject.Contains("OutputItemType=\"Analyzer\"", StringComparison.Ordinal)
            || !namSource.Contains("LeaseScoped", StringComparison.Ordinal)
            || !namSource.Contains("ScratchScoped", StringComparison.Ordinal)
            || !namSource.Contains("RecycleScoped", StringComparison.Ordinal))
        {
            throw new InvalidDataException("NAM must compile with its bundled analyzer and use typed/scoped stage ownership.");
        }

        string[] forbidden = ["unsafe", "NativeMemory", "Marshal.Alloc", "DllImport", "VirtualAlloc", "AllocHGlobal", "UnmanagedCallersOnly"];
        foreach (string sourcePath in Directory.EnumerateFiles(Path.Combine(demoRoot, "SafeCSharp"), "*.cs"))
        {
            string source = File.ReadAllText(sourcePath);
            foreach (string token in forbidden)
            {
                if (source.Contains(token, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"SafeCSharp source contains forbidden token '{token}': {sourcePath}");
                }
            }
        }
    }

    private static bool SameWork(PipelineResult left, PipelineResult right) =>
        left.Digest == right.Digest
        && left.Chunks == right.Chunks
        && left.VisibleFaces == right.VisibleFaces
        && left.Vertices == right.Vertices
        && left.Indices == right.Indices
        && left.StagedBytes == right.StagedBytes
        && left.ManagedPayloadObjectBytes == right.ManagedPayloadObjectBytes
        && left.EmptySections == right.EmptySections
        && left.UniformSections == right.UniformSections
        && left.ExpandedSections == right.ExpandedSections
        && left.PackedSections == right.PackedSections
        && left.MultiPackedSections == right.MultiPackedSections
        && left.TransparentMaskCount == right.TransparentMaskCount
        && left.DominantTransparentSections == right.DominantTransparentSections
        && left.ResidualTransparentSections == right.ResidualTransparentSections;

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Supprocom.NativeAllocationManagement.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the NAM repository root.");
    }

    private static double Mean(IEnumerable<double> values)
    {
        double[] array = values.ToArray();
        return array.Length == 0 ? 0 : array.Average();
    }

    private static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        double mean = values.Average();
        double sum = 0;
        for (int index = 0; index < values.Count; index++)
        {
            double delta = values[index] - mean;
            sum += delta * delta;
        }

        return Math.Sqrt(sum / (values.Count - 1));
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        double position = (sorted.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return lower == upper
            ? sorted[lower]
            : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private readonly record struct HarnessArguments(
        VoxelWorkloadOptions Workload,
        int Pairs,
        bool CorrectnessOnly,
        bool Enforce,
        string? OutputPath)
    {
        internal static HarnessArguments Parse(string[] args)
        {
            List<string> workloadArgs = [];
            int pairs = DefaultPairs;
            bool correctnessOnly = false;
            bool enforce = false;
            string? output = null;
            for (int index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--correctness-only":
                        correctnessOnly = true;
                        break;
                    case "--enforce":
                        enforce = true;
                        break;
                    case "--pairs":
                        pairs = int.Parse(args[++index], CultureInfo.InvariantCulture);
                        break;
                    case "--out":
                        output = args[++index];
                        break;
                    default:
                        workloadArgs.Add(args[index]);
                        if (args[index] is "--seed" or "--chunks" or "--workers" or "--iterations")
                        {
                            workloadArgs.Add(args[++index]);
                        }
                        break;
                }
            }

            if (pairs < DefaultPairs)
            {
                throw new ArgumentOutOfRangeException(nameof(args), $"At least {DefaultPairs} paired measurements are required.");
            }

            return new HarnessArguments(VoxelWorkloadOptions.Parse(workloadArgs), pairs, correctnessOnly, enforce, output);
        }
    }

    private readonly record struct CorrectnessReport(
        bool Passed,
        PipelineResult Safe,
        PipelineResult Nam,
        bool PayloadObjectParity);

    private readonly record struct PairedSample(int Pair, ChildRunResult Safe, ChildRunResult Nam);

    private readonly record struct BenchmarkReport(
        DateTime UtcStarted,
        VoxelWorkloadOptions Workload,
        int PairedRuns,
        bool CorrectnessPassed,
        IReadOnlyList<PairedSample> Samples,
        BenchmarkSummary Summary);

    private readonly record struct BenchmarkSummary(
        bool CorrectnessPassed,
        bool GatePassed,
        double SafeMeanMilliseconds,
        double NamMeanMilliseconds,
        double SafeMeanThroughput,
        double NamMeanThroughput,
        double MeanPairedSpeedup,
        double PairedSpeedupStandardDeviation,
        double PairedSpeedupConfidenceLower95,
        double PairedSpeedupConfidenceUpper95,
        double SafeMeanManagedAllocatedBytes,
        double NamMeanManagedAllocatedBytes,
        bool MaterialManagedAllocationReduction,
        bool ThroughputGate,
        bool ConfidenceGate,
        double SafeP50Milliseconds,
        double SafeP95Milliseconds,
        double SafeP99Milliseconds,
        double NamP50Milliseconds,
        double NamP95Milliseconds,
        double NamP99Milliseconds,
        double SafeMeanPeakManagedBackingBytes,
        double NamMeanPeakManagedBackingBytes,
        double NamMeanPeakNativeBackingBytes,
        double NamMeanPeakRetainedNativeBackingBytes,
        double NamMeanFinalNativeBackingBytes,
        double SafeMeanCoordinateStageBytes,
        double SafeMeanFaceStageBytes,
        double SafeMeanPackingStageBytes,
        double NamMeanCoordinateStageBytes,
        double NamMeanFaceStageBytes,
        double NamMeanPackingStageBytes,
        double NamMeanLeaseCount,
        double NamMeanScopedRecycleCount,
        double NamMeanClearedBytes)
    {
        public double SafeMeanHeapBytes { get; init; }
        public double NamMeanHeapBytes { get; init; }
        public double SafeMeanLargeObjectHeapBytes { get; init; }
        public double NamMeanLargeObjectHeapBytes { get; init; }
        public double SafeMeanPeakWorkingSetBytes { get; init; }
        public double NamMeanPeakWorkingSetBytes { get; init; }
        public double SafeMeanGcPauseMilliseconds { get; init; }
        public double NamMeanGcPauseMilliseconds { get; init; }
    }
}
