using System.Text.Json;
using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeOwnershipAllocationProbeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(32)]
    public void ProbeReconcilesEveryStageUsefulWorkAndActualPhysicalReturn(int rounds)
    {
        OwnershipAllocationProbeReport report = NativeOwnershipAllocationProbe.Run(rounds);
        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal(rounds, report.Rounds);
        Assert.True(report.ExactOutput && report.ExactCleanup && report.ExactNativeAccounting);
        Assert.Equal(917_504L * rounds, report.Checksum);
        Assert.Equal(rounds, report.Budget.AllocationCount);
        Assert.Equal(rounds, report.Budget.FreeCount);
        Assert.Equal(4_096L * rounds, report.Budget.AcquiredBackingBytes);
        Assert.Equal(0, report.Budget.CommittedBytes);
        Assert.Equal(0, report.Budget.ReservedBytes);
        Assert.Equal(0, report.Admission.OutstandingReservationCount);
        Assert.Equal(rounds, report.Admission.ActivationCount);
        Assert.Equal(16, report.TerminalUnique.MoveCount);
        Assert.False(report.TerminalUnique.HasReturnObligation);
        Assert.Equal(3 + 5 * rounds, report.Stages.Length);
        Assert.Equal("initial process snapshot", report.Stages[0].Stage);
        Assert.Equal("budget creation", report.Stages[1].Stage);
        Assert.Equal("terminal diagnostics", report.Stages[^1].Stage);
        for (int round = 0; round < rounds; round++)
        {
            OwnershipAllocationStage[] phases = report.Stages.AsSpan(2 + round * 5, 5).ToArray();
            Assert.Equal(["admission", "activation and initialization", "unique moves", "bounded reads", "physical return"],
                phases.Select(phase => phase.Stage), StringComparer.Ordinal);
            Assert.All(phases, phase => Assert.Equal(round, phase.Round));
        }
        Assert.All(report.Stages, phase =>
        {
            Assert.True(phase.AfterBytes >= phase.BeforeBytes);
            Assert.Equal(phase.AfterBytes - phase.BeforeBytes, phase.ManagedAllocatedBytes);
        });
        Assert.Equal(report.Stages.Sum(phase => phase.ManagedAllocatedBytes), report.AttributedManagedBytes);
        Assert.True(report.BetweenStageManagedBytes >= 0);
        Assert.Equal(report.OverallAfterBytes - report.OverallBeforeBytes,
            report.AttributedManagedBytes + report.BetweenStageManagedBytes);
        Assert.Equal(Environment.ProcessId, report.ProcessId);
        Assert.Equal(Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), report.TieredCompilation);
        Assert.Contains("Diagnostic-only", report.Scope, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(report));
        JsonElement first = json.RootElement.GetProperty("Stages")[0];
        Assert.Equal(report.Stages[0].ManagedAllocatedBytes, first.GetProperty("ManagedAllocatedBytes").GetInt64());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(33)]
    public void InvalidRoundCountsFailBeforeAllocation(int rounds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeOwnershipAllocationProbe.Run(rounds));

    [Fact]
    public void CommandRequiresExactDiagnosticNameAndBoundedInteger()
    {
        foreach (string text in new[] { "", "-1", "1.5", "nan", "2147483648" })
            Assert.Throws<ArgumentException>(() => NativeOwnershipAllocationProbe.RunCommand(["--ownership-allocation-probe", "--rounds", text]));
        Assert.Throws<ArgumentException>(() => NativeOwnershipAllocationProbe.RunCommand(["--ownership-full-cost-worker", "--rounds", "2"]));
        Assert.Throws<ArgumentException>(() => NativeOwnershipAllocationProbe.RunCommand(["--ownership-allocation-probe"]));
    }

    [Fact]
    public void BackendFailureCannotEmitSuccessOrLeaveNativeBacking()
    {
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        NativeMemoryTestHooks.FailNextAllocation();
        Assert.Throws<NativeAllocationFailedException>(() => NativeOwnershipAllocationProbe.Run(2));
        NativeMemoryStatistics after = NativeMemoryDiagnostics.Snapshot();
        Assert.Equal(before.OutstandingNativeBytes, after.OutstandingNativeBytes);
        Assert.Equal(before.AllocationCount, after.AllocationCount);
    }

    [Fact]
    public void MetadataFailureCannotRunTheProducerOrEmitSuccess()
    {
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(6);
        Assert.Throws<InvalidOperationException>(() => NativeOwnershipAllocationProbe.Run(2));
        NativeMemoryStatistics after = NativeMemoryDiagnostics.Snapshot();
        Assert.Equal(before.OutstandingNativeBytes, after.OutstandingNativeBytes);
        Assert.Equal(before.AllocationCount, after.AllocationCount);
    }
}
