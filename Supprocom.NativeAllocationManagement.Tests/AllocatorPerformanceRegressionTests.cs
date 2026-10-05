using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Performance;
using Xunit.Abstractions;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class AllocatorPerformanceRegressionTests
{
    private const int ProcessTimeoutMilliseconds = 10_000;
    private const double MinimumSampleMilliseconds = 10d;
    private readonly ITestOutputHelper _output;

    public AllocatorPerformanceRegressionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task NativeRegionBeatsOptimizedTypedArrayPools()
    {
        RegionRegressionReport report = await RunWorkerAsync<RegionRegressionReport>(
            "Region");
        string evidence = JsonSerializer.Serialize(report);

        Assert.True(report.Passed, evidence);
        Assert.Equal("0", report.TieredCompilation);
        Assert.Equal("0", report.TieredPgo);
        Assert.True(report.SampleCount >= 7, evidence);
        Assert.Equal(report.SampleCount, report.Pairs.Length);
        Assert.True(
            report.MedianSpeedup >= report.MinimumSpeedup,
            evidence);
        Assert.True(
            report.AggregateSpeedup >= report.MinimumSpeedup,
            evidence);

        int arrayPoolFirst = report.Pairs.Count(
            pair => string.Equals(pair.Order, "ArrayPool-Region", StringComparison.Ordinal));
        int regionFirst = report.Pairs.Count(
            pair => string.Equals(pair.Order, "Region-ArrayPool", StringComparison.Ordinal));
        Assert.InRange(
            Math.Abs(arrayPoolFirst - regionFirst),
            0,
            1);
        foreach (RegionPairEvidence pair in report.Pairs)
        {
            Assert.Equal(
                pair.ArrayPool.Checksum,
                pair.Region.Checksum);
            Assert.Equal(
                pair.ArrayPool.LogicalBytes,
                pair.Region.LogicalBytes);
            Assert.True(pair.Speedup > 0d, evidence);
            Assert.True(
                pair.ArrayPool.ElapsedMilliseconds
                    >= MinimumSampleMilliseconds,
                evidence);
            Assert.True(
                pair.Region.ElapsedMilliseconds
                    >= MinimumSampleMilliseconds,
                evidence);
            Assert.Equal(0, pair.ArrayPool.Gen0Collections);
            Assert.Equal(0, pair.ArrayPool.Gen1Collections);
            Assert.Equal(0, pair.ArrayPool.Gen2Collections);
            Assert.Equal(0, pair.Region.Gen0Collections);
            Assert.Equal(0, pair.Region.Gen1Collections);
            Assert.Equal(0, pair.Region.Gen2Collections);
            Assert.Equal(0, pair.Region.ManagedAllocatedBytes);
            Assert.Equal(0, pair.Region.FreshSegmentCount);
            Assert.InRange(pair.ArrayPool.Attempt, 1, 3);
            Assert.InRange(pair.Region.Attempt, 1, 3);
        }
    }

    [Fact]
    public async Task NativeArenaBeatsOptimizedTypedArrayPools()
    {
        ArenaRegressionReport report = await RunWorkerAsync<ArenaRegressionReport>(
            "Arena");
        string evidence = JsonSerializer.Serialize(report);

        Assert.True(report.Passed, evidence);
        Assert.Equal("0", report.TieredCompilation);
        Assert.Equal("0", report.TieredPgo);
        Assert.True(report.SampleCount >= 7, evidence);
        Assert.Equal(report.SampleCount, report.Pairs.Length);
        Assert.True(
            report.MedianSpeedup >= report.MinimumSpeedup,
            evidence);
        Assert.True(
            report.AggregateSpeedup >= report.MinimumSpeedup,
            evidence);

        int arrayPoolFirst = report.Pairs.Count(
            pair => string.Equals(pair.Order, "ArrayPool-Arena", StringComparison.Ordinal));
        int arenaFirst = report.Pairs.Count(
            pair => string.Equals(pair.Order, "Arena-ArrayPool", StringComparison.Ordinal));
        Assert.InRange(
            Math.Abs(arrayPoolFirst - arenaFirst),
            0,
            1);
        foreach (ArenaPairEvidence pair in report.Pairs)
        {
            Assert.Equal(
                pair.ArrayPool.Checksum,
                pair.Arena.Checksum);
            Assert.Equal(
                pair.ArrayPool.LogicalBytes,
                pair.Arena.LogicalBytes);
            Assert.True(pair.Speedup > 0d, evidence);
            Assert.True(
                pair.ArrayPool.ElapsedMilliseconds
                    >= MinimumSampleMilliseconds,
                evidence);
            Assert.True(
                pair.Arena.ElapsedMilliseconds
                    >= MinimumSampleMilliseconds,
                evidence);
            Assert.True(pair.ArrayPool.Accepted, evidence);
            Assert.True(pair.Arena.Accepted, evidence);
            Assert.Equal(0, pair.Arena.FreshSegmentCount);
            Assert.InRange(pair.ArrayPool.Attempt, 1, 3);
            Assert.InRange(pair.Arena.Attempt, 1, 3);
        }

        ArenaScopedRegressionReport scopedReport =
            await RunWorkerAsync<ArenaScopedRegressionReport>(
                "ArenaScoped");
        AssertArenaScopedReport(scopedReport);
    }

    private static void AssertArenaScopedReport(
        ArenaScopedRegressionReport report)
    {
        string evidence = JsonSerializer.Serialize(report);

        Assert.True(report.Passed, evidence);
        Assert.Equal("0", report.TieredCompilation);
        Assert.Equal("0", report.TieredPgo);
        Assert.True(report.SampleCount >= 7, evidence);
        Assert.Equal(report.SampleCount, report.Pairs.Length);
        Assert.True(
            report.MedianSpeedup >= report.MinimumSpeedup,
            evidence);
        Assert.True(
            report.AggregateSpeedup >= report.MinimumSpeedup,
            evidence);

        int arrayPoolFirst = report.Pairs.Count(
            pair => string.Equals(pair.Order, "ArrayPool-ArenaScoped", StringComparison.Ordinal));
        int arenaFirst = report.Pairs.Count(
            pair => string.Equals(pair.Order, "ArenaScoped-ArrayPool", StringComparison.Ordinal));
        Assert.InRange(
            Math.Abs(arrayPoolFirst - arenaFirst),
            0,
            1);
        foreach (ArenaScopedPairEvidence pair in report.Pairs)
        {
            Assert.Equal(
                pair.ArrayPool.Checksum,
                pair.Arena.Checksum);
            Assert.Equal(
                pair.ArrayPool.LogicalBytes,
                pair.Arena.LogicalBytes);
            Assert.True(pair.Speedup > 0d, evidence);
            Assert.True(
                pair.ArrayPool.ElapsedMilliseconds
                    >= MinimumSampleMilliseconds,
                evidence);
            Assert.True(
                pair.Arena.ElapsedMilliseconds
                    >= MinimumSampleMilliseconds,
                evidence);
            Assert.True(pair.ArrayPool.Accepted, evidence);
            Assert.True(pair.Arena.Accepted, evidence);
            Assert.Equal(0, pair.Arena.ManagedAllocatedBytes);
            Assert.Equal(0, pair.Arena.FreshSegmentCount);
            Assert.InRange(pair.ArrayPool.Attempt, 1, 3);
            Assert.InRange(pair.Arena.Attempt, 1, 3);
        }
    }

    private async Task<TReport> RunWorkerAsync<TReport>(
        string kind)
    {
        string dotnet = Environment.GetEnvironmentVariable(
            "DOTNET_HOST_PATH") ?? "dotnet";
        string worker = typeof(AllocatorPerformanceRegression)
            .Assembly.Location;
        ProcessStartInfo start = new(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(worker);
        start.ArgumentList.Add("--allocator-regression-worker");
        start.ArgumentList.Add("--kind");
        start.ArgumentList.Add(kind);
        start.Environment["DOTNET_TieredCompilation"] = "0";
        start.Environment["DOTNET_TieredPGO"] = "0";

        WorkerResult result = await RunWorkerProcessAsync(start, worker, kind,
            PackageFixtureEvidence.IsEnabled(Environment.GetEnvironmentVariable("NAM_RETAIN_PACKAGE_EVIDENCE")),
            ProcessTimeoutMilliseconds,
            directory => _output.WriteLine($"allocatorWorkerEvidence={directory}")).ConfigureAwait(true);
        string evidence = result.StandardOutput + Environment.NewLine + result.StandardError;
        Assert.True(result.ExitCode == 0, evidence);
        Assert.DoesNotContain("Unhandled exception", result.StandardError, StringComparison.Ordinal);
        return JsonSerializer.Deserialize<TReport>(result.StandardOutput)
            ?? throw new InvalidDataException($"The {kind} regression report was empty.");
    }

    internal static async Task<WorkerResult> RunWorkerProcessAsync(ProcessStartInfo start, string worker, string kind,
        bool retain, int timeoutMilliseconds, Action<string> evidenceCreated)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);
        string workingDirectory = string.IsNullOrEmpty(start.WorkingDirectory)
            ? Environment.CurrentDirectory : start.WorkingDirectory;
        string? directory = PackageFixtureEvidence.Begin(workingDirectory, retain);
        if (directory is not null)
        {
            evidenceCreated(directory);
            string identity = JsonSerializer.Serialize(new
            {
                Kind = kind,
                Worker = FileIdentity(worker),
                CandidateRuntime = FileIdentity(typeof(NativeRegion).Assembly.Location),
                Arguments = start.ArgumentList.ToArray(),
                CompilationEnvironment = start.Environment.Where(static item =>
                    item.Key.StartsWith("DOTNET_", StringComparison.Ordinal)
                        || item.Key.StartsWith("COMPlus_", StringComparison.Ordinal))
                    .Where(static item => item.Key.Contains("Tiered", StringComparison.Ordinal)
                        || item.Key.Contains("Jit", StringComparison.Ordinal)
                        || item.Key.EndsWith("ReadyToRun", StringComparison.Ordinal))
                    .OrderBy(static item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal)
            });
            await File.WriteAllTextAsync(Path.Combine(directory, "allocator-worker.json"), identity).ConfigureAwait(true);
        }

        string arguments = JsonSerializer.Serialize(start.ArgumentList.ToArray());
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        using Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"The {kind} regression process did not start.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            await PackageFixtureEvidence.CompleteAsync(directory, start.FileName, arguments, workingDirectory, startedAt,
                exitCode: null, timedOut: false, string.Empty, exception.ToString(), timeoutMilliseconds / 1000d).ConfigureAwait(true);
            throw;
        }

        Task<string> outputTask =
            process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask =
            process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(
            timeoutMilliseconds);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException exception)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync().ConfigureAwait(true);
            await PackageFixtureEvidence.CompleteAsync(directory, start.FileName, arguments, workingDirectory, startedAt,
                process.ExitCode, timedOut: true, await outputTask.ConfigureAwait(true), await errorTask.ConfigureAwait(true),
                timeoutMilliseconds / 1000d).ConfigureAwait(true);
            throw new TimeoutException(
                $"The {kind} regression process exceeded {timeoutMilliseconds / 1000d} seconds.",
                exception);
        }

        string output = await outputTask.ConfigureAwait(true);
        string error = await errorTask.ConfigureAwait(true);
        await PackageFixtureEvidence.CompleteAsync(directory, start.FileName, arguments, workingDirectory, startedAt,
            process.ExitCode, timedOut: false, output, error, timeoutMilliseconds / 1000d).ConfigureAwait(true);
        return new WorkerResult(process.ExitCode, output, error);
    }

    private static object FileIdentity(string path)
    {
        using FileStream file = File.OpenRead(path);
        return new { Path = path, Length = file.Length, SHA256 = Convert.ToHexString(SHA256.HashData(file)) };
    }

    internal readonly record struct WorkerResult(int ExitCode, string StandardOutput, string StandardError);

    [Theory]
    [InlineData(1.50d, 1.49d)]
    [InlineData(2.00d, 0.75d)]
    public void QuickGatesRejectPassingMedianWithFailingAggregate(
        double medianSpeedup,
        double aggregateSpeedup)
    {
        Assert.True(medianSpeedup >= 1.50d);
        Assert.False(
            AllocatorPerformanceAcceptance.MeetsMinimumSpeedup(
                medianSpeedup,
                aggregateSpeedup,
                1.50d));
    }
}
