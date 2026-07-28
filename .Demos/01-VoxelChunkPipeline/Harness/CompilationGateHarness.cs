using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.Harness;

internal static class CompilationGateHarness
{
    internal static async Task<int> RunAsync(string[] args)
    {
        Options options = Options.Parse(args);
        DateTime startedUtc = DateTime.UtcNow;
        string commit = (await RunCommandAsync(
            "git",
            ["-C", options.RepositoryRoot, "rev-parse", "HEAD"],
            TimeSpan.FromSeconds(10))).StandardOutput.Trim();
        string sdk = (await RunCommandAsync(
            "dotnet",
            ["--version"],
            TimeSpan.FromSeconds(10))).StandardOutput.Trim();

        string safeProject = Path.Combine(
            options.RepositoryRoot,
            ".Demos",
            "01-VoxelChunkPipeline",
            "SafeCSharp",
            "SafeCSharp.csproj");
        string namProject = Path.Combine(
            options.RepositoryRoot,
            ".Demos",
            "01-VoxelChunkPipeline",
            "NAM",
            "NAM.csproj");

        List<CompilationSample> warmups = [];
        List<CompilationSample> samples = [];
        for (int pair = 0; pair < options.WarmupPairs; pair++)
        {
            await CompilePairAsync(
                options,
                pair,
                CompilationGatePolicy.SafeRunsFirst(pair),
                safeProject,
                namProject,
                warmups);
        }

        bool warmupGatePassed =
            CompilationGatePolicy.HasValidWarmups(
                warmups,
                options.WarmupPairs);
        if (!CompilationGatePolicy.CanStartMeasuredBuilds(
                warmupGatePassed))
        {
            CompilationGateSummary warmupFailure =
                CreateSummary(options, warmups, samples);
            await WriteReportAsync(
                options,
                commit,
                startedUtc,
                sdk,
                warmups,
                samples,
                warmupFailure);
            return 3;
        }

        for (int pair = 0; pair < options.MeasuredPairs; pair++)
        {
            await CompilePairAsync(
                options,
                pair,
                CompilationGatePolicy.SafeRunsFirst(pair),
                safeProject,
                namProject,
                samples);
        }

        CompilationGateSummary summary =
            CreateSummary(options, warmups, samples);
        await WriteReportAsync(
            options,
            commit,
            startedUtc,
            sdk,
            warmups,
            samples,
            summary);
        return summary.GatePassed ? 0 : 3;
    }

    private static CompilationGateSummary CreateSummary(
        Options options,
        IReadOnlyList<CompilationSample> warmups,
        IReadOnlyList<CompilationSample> samples)
    {
        bool warmupGatePassed =
            CompilationGatePolicy.HasValidWarmups(
                warmups,
                options.WarmupPairs);
        bool measuredOrderGate =
            CompilationGatePolicy.HasBalancedMeasuredOrder(
                samples,
                options.MeasuredPairs);
        bool measuredCompleted =
            CompilationGatePolicy.HasCompletedMeasuredCompilations(
                samples,
                options.MeasuredPairs);
        CompilationSample[] safeSamples = samples
            .Where(sample => sample.Implementation == "SafeCSharp")
            .ToArray();
        CompilationSample[] namSamples = samples
            .Where(sample => sample.Implementation == "NAM")
            .ToArray();
        bool allCompleted =
            CompilationGatePolicy.AllCompilationsCompleted(
                warmupGatePassed,
                measuredCompleted);
        double? safeMean = measuredCompleted
            ? safeSamples.Average(
                sample => sample.CompilerElapsedMilliseconds!.Value)
            : null;
        double? namMean = measuredCompleted
            ? namSamples.Average(
                sample => sample.CompilerElapsedMilliseconds!.Value)
            : null;
        double? safeWallMean = measuredCompleted
            ? safeSamples.Average(sample => sample.WallElapsedMilliseconds)
            : null;
        double? namWallMean = measuredCompleted
            ? namSamples.Average(sample => sample.WallElapsedMilliseconds)
            : null;
        double? ratio = measuredCompleted && safeMean > 0
            ? namMean / safeMean
            : null;
        double? wallRatio = measuredCompleted && safeWallMean > 0
            ? namWallMean / safeWallMean
            : null;
        CompilationCompilerDiagnostics diagnostics = measuredCompleted
            ? new CompilationCompilerDiagnostics(
                SampleStandardDeviation(safeSamples),
                SampleStandardDeviation(namSamples),
                MeanForPosition(safeSamples, 0),
                MeanForPosition(safeSamples, 1),
                MeanForPosition(namSamples, 0),
                MeanForPosition(namSamples, 1))
            : default;
        bool childConfigurationGate =
            CompilationGatePolicy.HasRequiredChildConfiguration(
                warmups,
                samples,
                options.WarmupPairs,
                options.MeasuredPairs);
        bool compilerGate = measuredCompleted
            && ratio.HasValue
            && CompilationGatePolicy.IsWithinRatioLimit(ratio.Value);
        bool wallGate = measuredCompleted
            && wallRatio.HasValue
            && CompilationGatePolicy.IsWithinRatioLimit(wallRatio.Value);
        return new CompilationGateSummary(
            options.MeasuredPairs,
            CompilationGatePolicy.MaximumNamToSafeRatio,
            safeMean,
            namMean,
            safeWallMean,
            namWallMean,
            ratio,
            wallRatio,
            diagnostics,
            warmupGatePassed,
            allCompleted,
            childConfigurationGate,
            measuredOrderGate,
            compilerGate,
            wallGate,
            CompilationGatePolicy.AllGatesPassed(
                warmupGatePassed,
                childConfigurationGate,
                measuredOrderGate,
                compilerGate,
                wallGate));
    }

    private static async Task WriteReportAsync(
        Options options,
        string commit,
        DateTime startedUtc,
        string sdk,
        IReadOnlyList<CompilationSample> warmups,
        IReadOnlyList<CompilationSample> samples,
        CompilationGateSummary summary)
    {
        CompilationGateReport report = new(
            commit,
            startedUtc,
            DateTime.UtcNow,
            sdk,
            "Release",
            "Each command performs a single-project Rebuild without dependency builds. Build-server reuse and compiler sharing are disabled. NAM uses the built runtime and analyzer files. Each child process disables tiered compilation and tiered PGO. Both warmup builds must complete before measured builds start. Six measured pairs give each implementation three first positions. The Csc task ratio and command wall-time ratio must not exceed 1.10.",
            options.WarmupPairs,
            options.MeasuredPairs,
            options.PerCompilationTimeout.TotalMilliseconds,
            warmups,
            samples,
            summary);

        string? outputDirectory = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        JsonSerializerOptions json = new(VoxelJson.Options)
        {
            WriteIndented = true
        };
        await File.WriteAllTextAsync(
            options.OutputPath,
            JsonSerializer.Serialize(report, json));
        Console.WriteLine(JsonSerializer.Serialize(summary, VoxelJson.Options));
        Console.WriteLine(options.OutputPath);
    }

    private static async Task CompilePairAsync(
        Options options,
        int pair,
        bool safeFirst,
        string safeProject,
        string namProject,
        ICollection<CompilationSample> destination)
    {
        (string Name, string Project)[] order = safeFirst
            ? [("SafeCSharp", safeProject), ("NAM", namProject)]
            : [("NAM", namProject), ("SafeCSharp", safeProject)];
        for (int position = 0; position < order.Length; position++)
        {
            destination.Add(
                await CompileAsync(
                    options,
                    pair,
                    position,
                    order[position].Name,
                    order[position].Project));
        }
    }

    private static async Task<CompilationSample> CompileAsync(
        Options options,
        int pair,
        int position,
        string implementation,
        string project)
    {
        DateTime startedUtc = DateTime.UtcNow;
        string[] arguments =
        [
            "build",
            project,
            "--configuration",
            "Release",
            "--no-restore",
            "--no-dependencies",
            "--disable-build-servers",
            "--nologo",
            "--verbosity",
            "quiet",
            "-consoleLoggerParameters:PerformanceSummary",
            "-target:Rebuild",
            "-property:UseSharedCompilation=false",
            "-property:CompilationBenchmark=true",
            "-property:GenerateDependencyFile=false",
            "-property:GenerateRuntimeConfigurationFiles=false",
            "-property:UseAppHost=false",
            "-property:OutputPath=" + Path.Combine(
                options.RepositoryRoot,
                "artifacts",
                "compilation-gate",
                implementation)
        ];
        PressureCompilationConfiguration childConfiguration =
            CompilationGatePolicy.RequiredChildCompilationConfiguration;
        string command = FormatCommand("dotnet", arguments);
        Stopwatch stopwatch = Stopwatch.StartNew();
        CommandResult result;
        try
        {
            result = await RunCommandAsync(
                "dotnet",
                arguments,
                options.PerCompilationTimeout,
                childConfiguration);
        }
        catch (TimeoutException exception)
        {
            stopwatch.Stop();
            return new CompilationSample(
                pair,
                position,
                implementation,
                childConfiguration,
                startedUtc,
                stopwatch.Elapsed.TotalMilliseconds,
                null,
                CompilationOutcome.DeadlineExceeded,
                null,
                command,
                string.Empty,
                Tail(exception.Message));
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return new CompilationSample(
                pair,
                position,
                implementation,
                childConfiguration,
                startedUtc,
                stopwatch.Elapsed.TotalMilliseconds,
                null,
                CompilationOutcome.HarnessFailure,
                null,
                command,
                string.Empty,
                Tail(exception.ToString()));
        }

        stopwatch.Stop();
        Match compilerTiming = Regex.Match(
            result.StandardOutput,
            @"(?m)^\s*(\d+)\s+ms\s+Csc\s+1\s+calls?\s*$",
            RegexOptions.CultureInvariant);
        double? compilerElapsed = compilerTiming.Success
            ? double.Parse(
                compilerTiming.Groups[1].Value,
                CultureInfo.InvariantCulture)
            : null;
        CompilationOutcome outcome = result.ExitCode != 0
            ? CompilationOutcome.BuildFailure
            : compilerElapsed.HasValue
                ? CompilationOutcome.Completed
                : CompilationOutcome.HarnessFailure;
        return new CompilationSample(
            pair,
            position,
            implementation,
            childConfiguration,
            startedUtc,
            stopwatch.Elapsed.TotalMilliseconds,
            compilerElapsed,
            outcome,
            result.ExitCode,
            command,
            Tail(result.StandardOutput),
            Tail(result.StandardError));
    }

    private static async Task<CommandResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        PressureCompilationConfiguration? childConfiguration = null)
    {
        ProcessStartInfo start = new(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (childConfiguration is { } configuration)
        {
            start.Environment["DOTNET_TieredCompilation"] =
                configuration.TieredCompilation;
            start.Environment["DOTNET_TieredPGO"] =
                configuration.TieredPgo;
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                $"Could not start '{fileName}'.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeoutSource = new(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            await Task.WhenAll(stdoutTask, stderrTask);
            throw new TimeoutException(
                $"'{FormatCommand(fileName, arguments)}' exceeded "
                + $"{timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms.");
        }

        return new CommandResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string FormatCommand(
        string fileName,
        IEnumerable<string> arguments)
    {
        return fileName
            + " "
            + string.Join(
                " ",
                arguments.Select(argument => argument.Any(char.IsWhiteSpace)
                    ? $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
                    : argument));
    }

    private static string Tail(string value)
    {
        const int maximumCharacters = 4_096;
        return value.Length <= maximumCharacters
            ? value
            : value[^maximumCharacters..];
    }

    private static double? SampleStandardDeviation(
        IReadOnlyList<CompilationSample> samples)
    {
        if (samples.Count < 2
            || samples.Any(
                static sample =>
                    !sample.CompilerElapsedMilliseconds.HasValue))
        {
            return null;
        }

        double mean = samples.Average(
            static sample =>
                sample.CompilerElapsedMilliseconds!.Value);
        double sum = samples.Sum(sample =>
        {
            double delta =
                sample.CompilerElapsedMilliseconds!.Value - mean;
            return delta * delta;
        });
        return Math.Sqrt(sum / (samples.Count - 1));
    }

    private static double? MeanForPosition(
        IReadOnlyList<CompilationSample> samples,
        int position)
    {
        CompilationSample[] positionSamples = samples
            .Where(sample => sample.Position == position)
            .ToArray();
        return positionSamples.Length > 0
            && positionSamples.All(
                static sample =>
                    sample.CompilerElapsedMilliseconds.HasValue)
            ? positionSamples.Average(
                static sample =>
                    sample.CompilerElapsedMilliseconds!.Value)
            : null;
    }

    private sealed record Options(
        string RepositoryRoot,
        string OutputPath,
        int WarmupPairs,
        int MeasuredPairs,
        TimeSpan PerCompilationTimeout)
    {
        internal static Options Parse(IReadOnlyList<string> args)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            for (int index = 0; index < args.Count; index++)
            {
                if (args[index] == "--compile-gate")
                {
                    continue;
                }

                if (!args[index].StartsWith("--", StringComparison.Ordinal)
                    || index + 1 >= args.Count)
                {
                    throw new ArgumentException(
                        $"Unknown compilation-gate argument '{args[index]}'.");
                }

                values[args[index]] = args[++index];
            }

            string repositoryRoot = Required(values, "--repo");
            string output = Required(values, "--output");
            int warmups = values.TryGetValue("--warmup-pairs", out string? warmup)
                ? int.Parse(warmup, CultureInfo.InvariantCulture)
                : CompilationGatePolicy.DefaultWarmupPairCount;
            int pairs = values.TryGetValue("--pairs", out string? pairCount)
                ? int.Parse(pairCount, CultureInfo.InvariantCulture)
                : CompilationGatePolicy.DefaultMeasuredPairCount;
            int timeoutMilliseconds = values.TryGetValue(
                "--compile-timeout-ms",
                out string? timeout)
                ? int.Parse(timeout, CultureInfo.InvariantCulture)
                : 30_000;
            if (warmups
                    != CompilationGatePolicy.DefaultWarmupPairCount
                || !CompilationGatePolicy.IsValidMeasuredPairCount(pairs)
                || timeoutMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(args),
                    "The compilation gate requires one warmup pair, a positive even measured count, and a positive timeout.");
            }

            return new Options(
                Path.GetFullPath(repositoryRoot),
                Path.GetFullPath(output),
                warmups,
                pairs,
                TimeSpan.FromMilliseconds(timeoutMilliseconds));
        }

        private static string Required(
            IReadOnlyDictionary<string, string> values,
            string key)
        {
            return values.TryGetValue(key, out string? value)
                && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException(
                    $"The compilation gate requires '{key}'.");
        }
    }

    private readonly record struct CommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
