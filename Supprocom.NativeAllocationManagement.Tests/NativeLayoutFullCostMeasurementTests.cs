using System.Text.Json;
using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeLayoutFullCostMeasurementTests
{
    [Theory]
    [InlineData((int)LayoutFullCostImplementation.NativeLayout)]
    [InlineData((int)LayoutFullCostImplementation.ManagedContiguous)]
    [InlineData((int)LayoutFullCostImplementation.ManagedSeparate)]
    public void CompleteLifecycleUsesTheSameIndependentOutputOracle(int implementationValue)
    {
        LayoutFullCostImplementation implementation = (LayoutFullCostImplementation)implementationValue;
        LayoutFullCostReport report = NativeLayoutFullCostMeasurement.Run(implementation, 3);
        Assert.True(report.ExactOutput);
        Assert.True(report.ExactCleanup);
        Assert.True(report.ExactNativeAccounting);
        Assert.Equal(3, report.Cycles);
        Assert.Equal(32, report.WarmupCycles);
        Assert.Equal(16, report.MovesPerCycle);
        Assert.Equal(8, report.ReadsPerCycle);
        Assert.Equal(1_024, report.ByteCount);
        Assert.Equal(64, report.IntegerCount);
        Assert.Equal(128, report.LongCount);
        Assert.Equal(64, report.DetachedIntegerCount);
        Assert.Equal(2_304, report.LogicalPayloadBytes);
        Assert.Equal(256, report.CopiedBytesPerCycle);
        // (1024*3 + 64*7 + 128*11)*8 + 64*7 = 39872 per cycle.
        Assert.Equal(39_872L * 3, report.Checksum);
        Assert.Equal(39_872L * 32, report.WarmupChecksum);
        Assert.Equal(report.Checksum, report.ExpectedChecksum);
        Assert.Equal(report.WarmupChecksum, report.ExpectedWarmupChecksum);
        Assert.Equal(3, report.CycleTicks.Length);
        Assert.All(report.CycleTicks, ticks => Assert.True(ticks > 0));
        Assert.Equal(["preparation", "warm-up full lifecycle", "complete ownership lifecycle", "terminal diagnostics"], report.Phases.Select(phase => phase.Name), StringComparer.Ordinal);
        Assert.Equal([1, 32, 3, 1], report.Phases.Select(phase => phase.Operations));
        Assert.All(report.Phases, phase =>
        {
            Assert.True(double.IsFinite(phase.WallMilliseconds) && phase.WallMilliseconds >= 0);
            Assert.True(double.IsFinite(phase.CpuMilliseconds) && phase.CpuMilliseconds >= 0);
            Assert.True(phase.ManagedAllocatedBytes >= 0);
            Assert.True(phase.Gen0Collections >= 0 && phase.Gen1Collections >= 0 && phase.Gen2Collections >= 0);
        });
    }

    [Fact]
    public void NativeEvidenceIncludesExactOverlapCopiesMovesAndPhysicalCleanup()
    {
        LayoutFullCostReport report = NativeLayoutFullCostMeasurement.Run(LayoutFullCostImplementation.NativeLayout, 2);
        Assert.Equal(2_311, report.SourceBackingBytes);
        Assert.Equal(2_567, report.PeakLiveBackingBytes);
        Assert.Equal(1, report.SourceBackingAcquisitionsPerCycle);
        Assert.Equal(68, report.NativeBackingAcquisitions);
        Assert.True(report.DescriptorFieldBytes > 0);
        NativeMemoryBudgetStatistics budget = Assert.IsType<NativeMemoryBudgetStatistics>(report.Budget);
        Assert.Equal(0, budget.CommittedBytes);
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Equal(2_567, budget.PeakAdmittedBytes);
        Assert.Equal(68, budget.AllocationCount);
        Assert.Equal(68, budget.FreeCount);
        NativeLayoutStatistics owner = Assert.IsType<NativeLayoutStatistics>(report.TerminalLayout);
        Assert.Equal(16, owner.Ownership.MoveCount);
        Assert.Equal(256, owner.CopiedBytes);
        Assert.Equal(1, owner.DetachedOwnerCount);
        Assert.Equal(0, owner.LogicalInitializedBytes);
        Assert.Equal(0, owner.InitializedRegionCount);
        Assert.Equal(0, owner.Ownership.OwnedBackingBytes);
        Assert.Equal(0, Assert.IsType<NativeTransferStatistics>(report.TerminalDetached).OwnedBackingBytes);
    }

    [Theory]
    [InlineData((int)LayoutFullCostImplementation.ManagedContiguous, 1)]
    [InlineData((int)LayoutFullCostImplementation.ManagedSeparate, 3)]
    public void ManagedEvidenceDoesNotInventNativeCountersOrPhysicalFreeing(int implementationValue, int acquisitions)
    {
        LayoutFullCostImplementation implementation = (LayoutFullCostImplementation)implementationValue;
        LayoutFullCostReport report = NativeLayoutFullCostMeasurement.Run(implementation, 2);
        Assert.Equal(2_304, report.SourceBackingBytes);
        Assert.Equal(2_560, report.PeakLiveBackingBytes);
        Assert.Equal(acquisitions, report.SourceBackingAcquisitionsPerCycle);
        Assert.Null(report.NativeBackingAcquisitions);
        Assert.Null(report.DescriptorFieldBytes);
        Assert.Null(report.Budget);
        Assert.Null(report.TerminalLayout);
        Assert.Null(report.TerminalDetached);
        Assert.Contains("not physical freeing", report.MemoryDomain, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(report));
        foreach (string field in new[] { "NativeBackingAcquisitions", "DescriptorFieldBytes", "Budget", "TerminalLayout", "TerminalDetached" })
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty(field).ValueKind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void InvalidCycleCountsFailBeforeProduction(int cycles)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeLayoutFullCostMeasurement.Run(LayoutFullCostImplementation.NativeLayout, cycles));
    }

    [Fact]
    public void InvalidImplementationAndCommandShapesDoNotSilentlySelectABaseline()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeLayoutFullCostMeasurement.Run((LayoutFullCostImplementation)999, 1));
        Assert.Throws<ArgumentException>(() => NativeLayoutFullCostMeasurement.RunCommand(["--layout-full-cost-worker", "--implementation", "999", "--cycles", "1"]));
        Assert.Throws<ArgumentException>(() => NativeLayoutFullCostMeasurement.RunCommand(["--layout-full-cost-worker", "--implementation", "0", "--cycles", "1"]));
        Assert.Throws<ArgumentException>(() => NativeLayoutFullCostMeasurement.RunCommand(["--layout-full-cost-worker", "--implementation", "managedcontiguous", "--cycles", "1"]));
        Assert.Throws<ArgumentException>(() => NativeLayoutFullCostMeasurement.RunCommand(["--layout-full-cost-worker", "--implementation", "ManagedContiguous", "--cycles", "bad"]));
        Assert.Throws<ArgumentException>(() => NativeLayoutFullCostMeasurement.RunCommand(["--layout-full-cost-worker"]));
    }
}
