using System.Diagnostics;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class AllocatorPerformanceRegressionTests
{
    private const int ProcessTimeoutMilliseconds = 10_000;
    private const double MinimumSampleMilliseconds = 10d;

    [Fact]
    public async Task NativeRegionBeatsOptimizedTypedArrayPools()
    {
        RegionRegressionReport report = await RunWorker<RegionRegressionReport>(
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
        Assert.True(report.AggregateSpeedup > 0d, evidence);

        int arrayPoolFirst = report.Pairs.Count(
            pair => pair.Order == "ArrayPool-Region");
        int regionFirst = report.Pairs.Count(
            pair => pair.Order == "Region-ArrayPool");
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
        ArenaRegressionReport report = await RunWorker<ArenaRegressionReport>(
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
        Assert.True(report.AggregateSpeedup > 0d, evidence);

        int arrayPoolFirst = report.Pairs.Count(
            pair => pair.Order == "ArrayPool-Arena");
        int arenaFirst = report.Pairs.Count(
            pair => pair.Order == "Arena-ArrayPool");
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
    }

    private static async Task<TReport> RunWorker<TReport>(
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

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                $"The {kind} regression process did not start.");
        Task<string> outputTask =
            process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask =
            process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(
            ProcessTimeoutMilliseconds);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"The {kind} regression process exceeded ten seconds.");
        }

        string output = await outputTask;
        string error = await errorTask;
        TReport report =
            JsonSerializer.Deserialize<TReport>(
                output)
            ?? throw new InvalidDataException(
                $"The {kind} regression report was empty.");
        string evidence = output + Environment.NewLine + error;

        Assert.True(process.ExitCode == 0, evidence);
        Assert.DoesNotContain("Unhandled exception", error);
        return report;
    }
}
