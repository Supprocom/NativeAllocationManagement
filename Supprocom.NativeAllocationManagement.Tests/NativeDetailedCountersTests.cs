using System.Diagnostics;
using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeDetailedCountersTests
{
    [Fact]
    public void TestProcessEnablesDetailedCounters() =>
        Assert.True(NativeMemoryDiagnostics.DetailedCountersEnabled);

    [Fact]
    public async Task DisabledProcessKeepsByteGaugesAndSkipsEventCounters()
    {
        string report = await RunProbeAsync("0");
        Assert.Contains("enabled=False", report, StringComparison.Ordinal);
        Assert.Contains("allocations=0 frees=0 clears=0 copied=0", report, StringComparison.Ordinal);
        Assert.Contains("live=256 ", report, StringComparison.Ordinal);
        // The disposed builder's block stays parked for the process's next
        // builder, so it stays outstanding; the pool's slab was freed.
        Assert.EndsWith("kept=256 outstanding=256", report.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnabledProcessRecordsEventCounters()
    {
        string report = await RunProbeAsync("1");
        Assert.Contains("enabled=True", report, StringComparison.Ordinal);
        Assert.DoesNotContain("allocations=0 ", report, StringComparison.Ordinal);
        Assert.DoesNotContain("clears=0 ", report, StringComparison.Ordinal);
        Assert.Contains("copied=32 ", report, StringComparison.Ordinal);
        Assert.EndsWith("kept=256 outstanding=256", report.TrimEnd(), StringComparison.Ordinal);
    }

    private static async Task<string> RunProbeAsync(string detailedCounters)
    {
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(PooledPerformanceRegression).Assembly.Location);
        start.ArgumentList.Add("--detailed-counters-probe");
        start.Environment["NAM_DETAILED_COUNTERS"] = detailedCounters;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The probe did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        string report = await output.ConfigureAwait(false);
        Assert.True(process.ExitCode == 0, report + await error.ConfigureAwait(false));
        return report;
    }
}
