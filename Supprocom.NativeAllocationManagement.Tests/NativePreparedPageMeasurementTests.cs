using System.Text.Json;
using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedPageMeasurementTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryImplementationVerifiesAllBytesAndTrueSparseRetention(int implementationValue)
    {
        PreparedPageImplementation implementation = (PreparedPageImplementation)implementationValue;
        PreparedPageReport report = NativePreparedPageMeasurement.Run(implementation, 4);
        Assert.True(report.ExactOutput);
        Assert.True(report.ExactCleanup);
        // Independent workload oracle, not the worker's checksum helper.
        Assert.Equal(7L * 64 * 4_096 * 4, report.Checksum);
        Assert.Equal(64 * 4_096, report.RetainedBeforeTrim);
        Assert.Equal(implementation == PreparedPageImplementation.OrdinaryNativePool ? 4_096 : 16 * 4_096,
            report.RetainedAfterTrim);
        Assert.Equal(0, report.RetainedAfterReturn);
        Assert.Equal(64, report.SlotCount);
        Assert.Equal(4_096, report.SlotBytes);
        Assert.Equal(16, report.SlotsPerPage);
        Assert.Equal(["preparation", "warm-up", "prepared reuse", "sparse maintenance", "cleanup"],
            report.Phases.Select(static phase => phase.Name), StringComparer.Ordinal);
        Assert.Equal(0, report.Phases[2].ManagedAllocatedBytes);
        Assert.Equal(4, report.RoundTicks.Length);
        Assert.All(report.RoundTicks, static ticks => Assert.True(ticks > 0));
        Assert.True(report.TimestampFrequency > 0);
        Assert.False(string.IsNullOrWhiteSpace(report.Rid));
        Assert.False(string.IsNullOrWhiteSpace(report.Runtime));
        Assert.Contains("No benefit verdict", report.MeasurementScope, StringComparison.Ordinal);

        if (implementation == PreparedPageImplementation.ManagedExactPages)
        {
            Assert.Null(report.OwnedBackingAcquisitions);
            Assert.Null(report.Prepared);
            Assert.Null(report.Budget);
            Assert.Equal(4L * IntPtr.Size, report.MetadataElementBytes);
            Assert.Contains("NOT an observed physical free", report.MemoryDomain, StringComparison.Ordinal);
        }
        else if (implementation == PreparedPageImplementation.OrdinaryNativePool)
        {
            Assert.Equal(64, report.OwnedBackingAcquisitions);
            Assert.Null(report.Prepared);
            Assert.Null(report.MetadataElementBytes);
            Assert.Null(report.Budget);
        }
        else
        {
            Assert.Equal(4, report.OwnedBackingAcquisitions);
            Assert.True(report.MetadataElementBytes > 0);
            Assert.NotNull(report.Prepared);
            NativePreparedPoolStatistics prepared = report.Prepared.Value;
            Assert.Equal(NativeOwnerLifecycle.Disposed, prepared.Lifecycle);
            Assert.Equal(64, prepared.PeakOccupiedSlotCount);
            Assert.Equal(0, prepared.RetainedBytes);
            Assert.Equal(0, prepared.RetainedPageCount);
            Assert.Equal(64 * (64 + 4) + 1, prepared.SuccessfulRentCount);
        }

        if (implementation is PreparedPageImplementation.PreparedBudget or PreparedPageImplementation.PreparedTrace)
        {
            Assert.NotNull(report.Budget);
            NativeMemoryBudgetStatistics budget = report.Budget.Value;
            Assert.Equal(4, budget.AllocationCount);
            Assert.Equal(4, budget.FreeCount);
            Assert.Equal(64 * 4_096, budget.PeakCommittedBytes);
            Assert.Equal(0, budget.CommittedBytes);
            Assert.Equal(0, budget.ReservedBytes);
            Assert.Equal(implementation == PreparedPageImplementation.PreparedTrace ? 64 : 0, budget.TraceCapacity);
            if (implementation == PreparedPageImplementation.PreparedTrace) Assert.True(budget.TraceCount > 0);
            else Assert.Equal(0, budget.TraceCount);
        }
        Assert.Equal(0, report.ReuseProcessAllocations);
        Assert.Equal(0, report.ReuseProcessFrees);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void InvalidRoundsAreNotMeasured(int rounds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NativePreparedPageMeasurement.Run(PreparedPageImplementation.PreparedBudget, rounds));

    [Fact]
    public void UnknownImplementationIsNotAccepted() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NativePreparedPageMeasurement.Run((PreparedPageImplementation)99, 1));

    [Theory]
    [InlineData("preparedbudget", "1")]
    [InlineData("PreparedBudget", "-1")]
    [InlineData("PreparedBudget", "1x")]
    [InlineData("99", "1")]
    public void MalformedWorkerArgumentsDoNotProduceAnAcceptedReport(string implementation, string rounds) =>
        Assert.Throws<ArgumentException>(() => NativePreparedPageMeasurement.RunCommand(
            ["--prepared-page-worker", "--implementation", implementation, "--rounds", rounds]));

    [Fact]
    public void CorruptionIsDetectedEvenWhenMostBytesAreCorrect()
    {
        byte[] payload = new byte[4_096];
        payload.AsSpan().Fill(7);
        payload[2_048] = 8;
        Assert.Throws<InvalidDataException>(() => NativePreparedPageMeasurement.Verify(payload));
        Assert.Equal(7L * 64 * 4_096 * 1_024, NativePreparedPageMeasurement.ExpectedChecksum(1_024));
    }

    [Fact]
    public void ActualBackingFailureDoesNotProduceASuccessfulReportOrLeakStorage()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => NativePreparedPageMeasurement.Run(PreparedPageImplementation.PreparedBudget, 1));
            NativeMemoryTestMetrics metrics = NativeMemoryTestHooks.Snapshot();
            Assert.Equal(metrics.AllocationCount, metrics.FreeCount);
            Assert.Equal(0, metrics.OutstandingNativeBytes);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void SerializedReportPreservesEveryActualTerminalSnapshotField()
    {
        PreparedPageReport report = NativePreparedPageMeasurement.Run(PreparedPageImplementation.PreparedTrace, 2);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(report));
        JsonElement root = document.RootElement;
        Assert.Equal(report.Checksum, root.GetProperty("Checksum").GetInt64());
        Assert.Equal(report.RoundTicks, root.GetProperty("RoundTicks").EnumerateArray().Select(static ticks => ticks.GetInt64()));
        Assert.Equal(JsonSerializer.Serialize(report.Budget), root.GetProperty("Budget").GetRawText());
        Assert.Equal(JsonSerializer.Serialize(report.Prepared), root.GetProperty("Prepared").GetRawText());
        Assert.Equal(JsonSerializer.Serialize(report.Phases), root.GetProperty("Phases").GetRawText());
        Assert.Equal(JsonSerializer.Serialize(report.ProcessBefore), root.GetProperty("ProcessBefore").GetRawText());
        Assert.Equal(JsonSerializer.Serialize(report.ProcessAfter), root.GetProperty("ProcessAfter").GetRawText());
        Assert.Equal(0, root.GetProperty("RetainedAfterReturn").GetInt64());
        Assert.True(root.GetProperty("ExactCleanup").GetBoolean());
        PreparedPageReport ordinary = NativePreparedPageMeasurement.Run(PreparedPageImplementation.OrdinaryNativePool, 1);
        using JsonDocument unavailable = JsonDocument.Parse(JsonSerializer.Serialize(ordinary));
        Assert.Equal(JsonValueKind.Null, unavailable.RootElement.GetProperty("MetadataElementBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, unavailable.RootElement.GetProperty("Prepared").ValueKind);
    }
}
