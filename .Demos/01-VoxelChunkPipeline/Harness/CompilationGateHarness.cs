using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.Harness;

internal static class CompilationGateHarness
{
    private const double MaximumRatio = 1.10;

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
        int totalPairs = checked(
            options.WarmupPairs + options.MeasuredPairs);
        for (int ordinal = 0; ordinal < totalPairs; ordinal++)
        {
            bool measured = ordinal >= options.WarmupPairs;
            int displayPair = measured
                ? ordinal - options.WarmupPairs
                : ordinal;
            bool safeFirst = (ordinal & 1) == 0;
            (string Name, string Project)[] order = safeFirst
                ? [("SafeCSharp", safeProject), ("NAM", namProject)]
                : [("NAM", namProject), ("SafeCSharp", safeProject)];
            for (int position = 0; position < order.Length; position++)
            {
                CompilationSample sample = await CompileAsync(
                    options,
                    displayPair,
                    position,
                    order[position].Name,
                    order[position].Project);
                (measured ? samples : warmups).Add(sample);
            }
        }

        CompilationSample[] safeSamples = samples
            .Where(sample => sample.Implementation == "SafeCSharp")
            .ToArray();
        CompilationSample[] namSamples = samples
            .Where(sample => sample.Implementation == "NAM")
            .ToArray();
        bool completed = samples.Count == checked(options.MeasuredPairs * 2)
            && samples.All(sample => sample.Outcome == CompilationOutcome.Completed)
            && safeSamples.Length == options.MeasuredPairs
            && namSamples.Length == options.MeasuredPairs;
        double? safeMean = completed
            ? safeSamples.Average(
                sample => sample.CompilerElapsedMilliseconds!.Value)
            : null;
        double? namMean = completed
            ? namSamples.Average(
                sample => sample.CompilerElapsedMilliseconds!.Value)
            : null;
        double? safeWallMean = completed
            ? safeSamples.Average(sample => sample.WallElapsedMilliseconds)
            : null;
        double? namWallMean = completed
            ? namSamples.Average(sample => sample.WallElapsedMilliseconds)
            : null;
        double? ratio = completed && safeMean > 0
            ? namMean / safeMean
            : null;
        double? wallRatio = completed && safeWallMean > 0
            ? namWallMean / safeWallMean
            : null;
        bool compilerGate = completed
            && ratio.HasValue
            && ratio.Value <= MaximumRatio;
        bool wallGate = completed
            && wallRatio.HasValue
            && wallRatio.Value <= MaximumRatio;
        CompilationGateSummary summary = new(
            options.MeasuredPairs,
            MaximumRatio,
            safeMean,
            namMean,
            safeWallMean,
            namWallMean,
            ratio,
            wallRatio,
            completed,
            compilerGate,
            wallGate,
            compilerGate && wallGate);
        CompilationGateReport report = new(
            commit,
            startedUtc,
            DateTime.UtcNow,
            sdk,
            "Release",
            "Alternating single-project Rebuild commands do not restore or rebuild dependencies. Compiler sharing and build-server reuse are disabled. NAM uses the built runtime and analyzer files. This configuration measures package-consumer compilation without evaluation of their source projects. The Csc task ratio and command wall-time ratio must not exceed 1.10.",
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
        return summary.GatePassed ? 0 : 3;
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
        string command = FormatCommand("dotnet", arguments);
        Stopwatch stopwatch = Stopwatch.StartNew();
        CommandResult result;
        try
        {
            result = await RunCommandAsync(
                "dotnet",
                arguments,
                options.PerCompilationTimeout);
        }
        catch (TimeoutException exception)
        {
            stopwatch.Stop();
            return new CompilationSample(
                pair,
                position,
                implementation,
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
        TimeSpan timeout)
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
                : 1;
            int pairs = values.TryGetValue("--pairs", out string? pairCount)
                ? int.Parse(pairCount, CultureInfo.InvariantCulture)
                : 5;
            int timeoutMilliseconds = values.TryGetValue(
                "--compile-timeout-ms",
                out string? timeout)
                ? int.Parse(timeout, CultureInfo.InvariantCulture)
                : 30_000;
            if (warmups < 0 || pairs < 3 || timeoutMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(args),
                    "Compilation warmups must be nonnegative, measured pairs at least three, and the timeout positive.");
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
