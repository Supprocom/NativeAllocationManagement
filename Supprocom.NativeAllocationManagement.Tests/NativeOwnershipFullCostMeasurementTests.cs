using System.Diagnostics;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeOwnershipFullCostMeasurementTests
{
    [Theory]
    [InlineData(0, 0, 917_504)]
    [InlineData(1, 0, 917_504)]
    [InlineData(0, 1, 917_616)]
    [InlineData(1, 1, 917_616)]
    [InlineData(0, 2, 1_146_992)]
    [InlineData(1, 2, 1_146_992)]
    public void EveryImplementationExecutesTheSameIndependentOwnershipOracle(int implementationValue, int contractValue, long oneCycleChecksum)
    {
        OwnershipFullCostReport report = NativeOwnershipFullCostMeasurement.Run((OwnershipFullCostImplementation)implementationValue,
            (OwnershipFullCostContract)contractValue, 2);
        Assert.True(report.ExactOutput);
        Assert.True(report.ExactCleanup);
        Assert.True(report.ExactNativeAccounting);
        Assert.Equal(2, report.SchemaVersion);
        Assert.Equal(2, report.Cycles);
        Assert.Equal(32, report.WarmupCycles);
        Assert.Equal(4_096, report.PayloadBytes);
        Assert.Equal(16, report.SliceBytes);
        Assert.Equal(16, report.MovesPerCycle);
        Assert.Equal(32, report.ReadsPerCycle);
        Assert.Equal(contractValue == 2 ? 8 : 0, report.UpgradesPerCycle);
        Assert.Equal(oneCycleChecksum * 2, report.Checksum);
        Assert.Equal(oneCycleChecksum, report.FirstUseChecksum);
        Assert.Equal(report.FirstUseChecksum, report.ExpectedFirstUseChecksum);
        Assert.Equal(oneCycleChecksum * 32, report.WarmupChecksum);
        Assert.Equal(report.Checksum, report.ExpectedChecksum);
        Assert.Equal(report.WarmupChecksum, report.ExpectedWarmupChecksum);
        Assert.Equal(2, report.CycleTicks.Length);
        Assert.All(report.CycleTicks, ticks => Assert.True(ticks > 0));
        Assert.Equal(32, report.WarmupCycleTicks.Length);
        Assert.All(report.WarmupCycleTicks, ticks => Assert.True(ticks > 0));
        Assert.Equal(["preparation", "first-use full lifecycle", "counted warm-up full lifecycle", "post-warm-up full lifecycle", "terminal diagnostics"],
            report.Phases.Select(phase => phase.Name), StringComparer.Ordinal);
        Assert.Equal([1, 1, 32, 2, 1], report.Phases.Select(phase => phase.Operations));
        Assert.Equal([0, oneCycleChecksum, oneCycleChecksum * 32, oneCycleChecksum * 2, 0], report.Phases.Select(phase => phase.Checksum));
        Assert.All(report.Phases, phase =>
        {
            Assert.True(double.IsFinite(phase.WallMilliseconds) && phase.WallMilliseconds >= 0);
            Assert.True(double.IsFinite(phase.CpuMilliseconds) && phase.CpuMilliseconds >= 0);
            Assert.True(phase.ManagedAllocatedBytes >= 0);
            Assert.True(phase.Gen0Collections >= 0 && phase.Gen1Collections >= 0 && phase.Gen2Collections >= 0);
        });
    }

    [Theory]
    [InlineData(0, 35, 4_096, 16)]
    [InlineData(1, 70, 4_112, 17)]
    [InlineData(2, 70, 4_112, 17)]
    public void NativeHistoriesProveActualMovesFullRetentionAndPhysicalCleanup(int contractValue, long acquisitions, long peakBytes, long moves)
    {
        OwnershipFullCostReport report = NativeOwnershipFullCostMeasurement.Run(OwnershipFullCostImplementation.Native,
            (OwnershipFullCostContract)contractValue, 2);
        NativeMemoryBudgetStatistics budget = Assert.IsType<NativeMemoryBudgetStatistics>(report.Budget);
        Assert.Equal(acquisitions, report.NativeBackingAcquisitions);
        Assert.Equal(acquisitions, budget.AllocationCount);
        Assert.Equal(acquisitions, budget.FreeCount);
        Assert.Equal(peakBytes, report.PeakLiveBackingBytes);
        Assert.Equal(peakBytes, budget.PeakAdmittedBytes);
        Assert.Equal(0, budget.CommittedBytes);
        Assert.Equal(0, budget.ReservedBytes);
        NativeTransferStatistics unique = Assert.IsType<NativeTransferStatistics>(report.TerminalUnique);
        Assert.Equal(moves, unique.MoveCount);
        Assert.Equal(0, unique.OwnedBackingBytes);
        Assert.False(unique.HasReturnObligation);
        if (contractValue == 0)
        {
            Assert.Null(report.TerminalShared);
            Assert.Null(report.TerminalDetached);
            Assert.Equal(0, report.CopiedBytesPerCycle);
            return;
        }
        NativeSharingStatistics shared = Assert.IsType<NativeSharingStatistics>(report.TerminalShared);
        Assert.Equal(33, shared.ShareCount);
        Assert.Equal(1, shared.DetachCount);
        Assert.Equal(1, shared.PayloadReturnCount);
        Assert.Equal(2, shared.PeakStrongBindingCount);
        Assert.Equal(contractValue == 2 ? 1 : 0, shared.WeakCreationCount);
        Assert.Equal(contractValue == 2 ? 8 : 0, shared.SuccessfulUpgradeCount);
        Assert.Equal(contractValue == 2 ? 1 : 0, shared.ExpiredUpgradeCount);
        Assert.Equal(0, shared.StrongBindingCount);
        Assert.Equal(0, shared.WeakBindingCount);
        Assert.Equal(0, shared.ActiveReadCount);
        Assert.Equal(0, shared.ManagedBankBytes);
        Assert.Equal(0, shared.OwnedBackingBytes);
        Assert.True(shared.Expired);
        Assert.True(shared.PayloadReleased);
        Assert.Equal(16, report.CopiedBytesPerCycle);
        Assert.Equal(0, Assert.IsType<NativeTransferStatistics>(report.TerminalDetached).OwnedBackingBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ManagedObservationLeavesEveryUnsupportedNativeDomainUnavailable(int contractValue)
    {
        OwnershipFullCostReport report = NativeOwnershipFullCostMeasurement.Run(OwnershipFullCostImplementation.Managed,
            (OwnershipFullCostContract)contractValue, 2);
        Assert.Null(report.NativeBackingAcquisitions);
        Assert.Null(report.Budget);
        Assert.Null(report.TerminalUnique);
        Assert.Null(report.TerminalShared);
        Assert.Null(report.TerminalDetached);
        Assert.Contains("not physical freeing", report.MemoryDomain, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(report));
        foreach (string field in new[] { "NativeBackingAcquisitions", "Budget", "TerminalUnique", "TerminalShared", "TerminalDetached" })
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty(field).ValueKind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void InvalidCountsFailBeforeBackingOrProducer(int cycles)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeOwnershipFullCostMeasurement.Run(OwnershipFullCostImplementation.Native,
            OwnershipFullCostContract.Unique, cycles));
    }

    [Fact]
    public void InvalidEnumsAndExactCommandNamesNeverSelectADifferentContract()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeOwnershipFullCostMeasurement.Run((OwnershipFullCostImplementation)999,
            OwnershipFullCostContract.Unique, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeOwnershipFullCostMeasurement.Run(OwnershipFullCostImplementation.Native,
            (OwnershipFullCostContract)999, 1));
        foreach (string name in new[] { "0", "native", "999" })
            Assert.Throws<ArgumentException>(() => NativeOwnershipFullCostMeasurement.RunCommand(["--ownership-full-cost-worker",
                "--implementation", name, "--contract", "Unique", "--cycles", "1"]));
        foreach (string name in new[] { "0", "sharedweak", "999" })
            Assert.Throws<ArgumentException>(() => NativeOwnershipFullCostMeasurement.RunCommand(["--ownership-full-cost-worker",
                "--implementation", "Native", "--contract", name, "--cycles", "1"]));
        Assert.Throws<ArgumentException>(() => NativeOwnershipFullCostMeasurement.RunCommand(["--ownership-full-cost-worker"]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void CountedWarmupIsConfigurableWithoutHidingFirstUseOrInventingStabilization(int warmupCycles)
    {
        OwnershipFullCostReport report = NativeOwnershipFullCostMeasurement.Run(OwnershipFullCostImplementation.Native,
            OwnershipFullCostContract.SharedWeak, 2, warmupCycles);
        Assert.True(report.ExactOutput && report.ExactCleanup && report.ExactNativeAccounting);
        Assert.Equal(warmupCycles, report.WarmupCycleTicks.Length);
        Assert.Equal(warmupCycles * 1_146_992L, report.WarmupChecksum);
        Assert.Equal(2 * (1 + warmupCycles + 2), report.NativeBackingAcquisitions);
        Assert.Contains("does not establish Tier1/OSR stabilization", report.MeasurementScope, StringComparison.Ordinal);
        Assert.Contains("not independent samples", report.MeasurementScope, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void InvalidWarmupCountsFailBeforeWork(int warmupCycles) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeOwnershipFullCostMeasurement.Run(OwnershipFullCostImplementation.Native,
            OwnershipFullCostContract.Unique, 1, warmupCycles));

    [Fact]
    public void ObservationsRetainActualSourcesAndUnavailableFilesRemainNull()
    {
        OwnershipHostObservation observation = OwnershipHostObservation.Capture();
        Assert.Equal(Environment.ProcessId, observation.ProcessId);
        Assert.True(observation.CompletedAtUtc >= observation.BeganAtUtc);
        Assert.Equal(10, observation.LinuxFiles.Count);
        Assert.Contains("/proc/self/cgroup", observation.LinuxFiles.Keys, StringComparer.Ordinal);
        Assert.Contains("/proc/self/mountinfo", observation.LinuxFiles.Keys, StringComparer.Ordinal);
        Assert.Contains("/proc/self/status", observation.LinuxFiles.Keys, StringComparer.Ordinal);
        if (!OperatingSystem.IsLinux()) Assert.All(observation.LinuxFiles.Values, Assert.Null);
        Assert.Null(OwnershipHostObservation.ReadOptionalText(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent")));
        OwnershipRuntimeConfiguration configuration = OwnershipRuntimeConfiguration.Capture();
        Assert.Equal(Environment.ProcessorCount, configuration.ProcessorCount);
        Assert.Equal(System.Runtime.GCSettings.IsServerGC, configuration.ServerGc);
        Assert.Equal(System.Runtime.GCSettings.LatencyMode.ToString(), configuration.GcLatencyMode);
        Assert.All(configuration.EnvironmentSettings, pair => Assert.Equal(Environment.GetEnvironmentVariable(pair.Key), pair.Value));
        Assert.DoesNotContain("HOME", configuration.EnvironmentSettings.Keys, StringComparer.Ordinal);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(observation));
        Assert.Equal(10, json.RootElement.GetProperty("LinuxFiles").EnumerateObject().Count());
    }

    [Fact]
    public async Task MaintainedRunnerExercisesIndependentScheduleStatisticsAndReaderFixtures()
    {
        string script = Path.Combine(RepositoryTestPaths.Root, ".github", "ci", "test-ownership-measurement-contract.ps1");
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("POWERSHELL_HOST_PATH") ?? "pwsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        string? evidence = null;
        AllocatorPerformanceRegressionTests.WorkerResult result = await AllocatorPerformanceRegressionTests.RunWorkerProcessAsync(
            start, script, "OwnershipMeasurementContract", retain: true, 20_000, path => evidence = path);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.NotNull(evidence);
        using JsonDocument json = JsonDocument.Parse(result.StandardOutput);
        Assert.True(json.RootElement.GetProperty("Passed").GetBoolean());
        Assert.Equal(30, json.RootElement.GetProperty("Cases").GetArrayLength());
        Assert.Contains("upper-bound-does-not-prove-noninferiority", result.StandardOutput, StringComparison.Ordinal);
    }
}
