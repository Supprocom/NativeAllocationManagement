using System.Text.Json;
using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeAdmissionFullCostMeasurementTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FourImplementationsProduceTheIndependentVariableSizeOracle(int implementationValue)
    {
        AdmissionFullCostReport report = NativeAdmissionFullCostMeasurement.Run((AdmissionFullCostImplementation)implementationValue, 2);
        Assert.True(report.ExactOutput);
        Assert.True(report.ExactCleanup);
        Assert.True(report.ExactAccounting);
        Assert.Equal(2, report.Cycles);
        Assert.Equal(32, report.WarmupCycles);
        Assert.Equal(65_536, report.CapacityBytes);
        Assert.Equal(54_272, report.OriginalBytes);
        Assert.Equal(11_264, report.ProbeBytes);
        Assert.Equal(1_677_312, report.Checksum);
        Assert.Equal(26_836_992, report.WarmupChecksum);
        Assert.Equal(report.Checksum, report.ExpectedChecksum);
        Assert.Equal(report.WarmupChecksum, report.ExpectedWarmupChecksum);
        Assert.Equal(2, report.CycleTicks.Length);
        Assert.All(report.CycleTicks, ticks => Assert.True(ticks > 0));
        Assert.Equal(["preparation", "warm-up full lifecycle", "complete admission lifecycle", "terminal cleanup and diagnostics"],
            report.Phases.Select(phase => phase.Name), StringComparer.Ordinal);
        Assert.Equal([1, 32, 2, 1], report.Phases.Select(phase => phase.Operations));
        Assert.All(report.Phases, phase =>
        {
            Assert.True(double.IsFinite(phase.WallMilliseconds) && phase.WallMilliseconds >= 0);
            Assert.True(double.IsFinite(phase.CpuMilliseconds) && phase.CpuMilliseconds >= 0);
            Assert.True(phase.ManagedAllocatedBytes >= 0);
            Assert.True(phase.Gen0Collections >= 0 && phase.Gen1Collections >= 0 && phase.Gen2Collections >= 0);
        });
        using JsonDocument json = JsonDocument.Parse(NativeAdmissionFullCostMeasurement.Serialize(report));
        Assert.Equal(1_677_312, json.RootElement.GetProperty("Checksum").GetInt64());
        JsonElement trace = json.RootElement.GetProperty("Trace");
        if (implementationValue == 1)
        {
            Assert.Equal(64, trace.GetArrayLength());
            Assert.All(trace.EnumerateArray(), observation =>
            {
                Assert.Equal(JsonValueKind.Number, observation.GetProperty("RequestedBytes").ValueKind);
                Assert.Equal(JsonValueKind.Number, observation.GetProperty("PreviousBytes").ValueKind);
            });
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ActualNativeHistoriesProveRefusalCancellationOverlapAndPhysicalRelease(int implementationValue)
    {
        AdmissionFullCostReport report = NativeAdmissionFullCostMeasurement.Run((AdmissionFullCostImplementation)implementationValue, 2);
        NativeMemoryBudgetStatistics budget = Assert.IsType<NativeMemoryBudgetStatistics>(report.Budget);
        Assert.Equal(65_536, budget.PeakAdmittedBytes);
        Assert.Equal(65_536, budget.PeakCommittedBytes);
        Assert.Equal(204, budget.AllocationCount);
        Assert.Equal(204, budget.FreeCount);
        Assert.Equal(102, budget.RejectedAllocationCount);
        Assert.Equal(0, budget.FailedAllocationCount);
        Assert.Equal(0, budget.ReallocationCount);
        Assert.Equal(0, budget.CommittedBytes);
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Equal(0, budget.ActiveAllocationCount);
        NativeMemoryAdmissionStatistics admission = Assert.IsType<NativeMemoryAdmissionStatistics>(report.Admission);
        Assert.Equal(238, admission.AdmittedReservationCount);
        Assert.Equal(102, admission.RejectedReservationCount);
        Assert.Equal(204, admission.BackingPreparationCount);
        Assert.Equal(170, admission.ActivationCount);
        Assert.Equal(68, admission.CancelledReservationCount);
        Assert.Equal(5, admission.PeakOutstandingReservationCount);
        Assert.Equal(65_536, admission.PeakPendingBytes);
        Assert.Equal(65_536, admission.PeakPreparedUnpublishedBytes);
        Assert.Equal(0, admission.OutstandingReservationCount);
        Assert.Equal(0, admission.PendingBytes);
        Assert.Equal(0, admission.PreparedUnpublishedBytes);
        Assert.False(admission.HistoryOverflowed);
        NativeMemoryReservationStatistics permission = Assert.IsType<NativeMemoryReservationStatistics>(report.MovedPermission);
        Assert.Equal(1, permission.ReservationMoveCount);
        Assert.Equal(NativeMemoryReservationOutcome.Activated, permission.Outcome);
        Assert.False(permission.HasReservationReturnObligation);
        Assert.Equal(0, permission.OwnedBackingBytes);
        NativeTransferStatistics owner = Assert.IsType<NativeTransferStatistics>(report.MovedOwner);
        Assert.Equal(1, owner.MoveCount);
        Assert.Equal(0, owner.OwnedBackingBytes);
        Assert.False(owner.HasReturnObligation);
        Assert.Null(report.Managed);
        Assert.Null(report.ManagedBeforeCleanup);
    }

    [Fact]
    public void OptionalTraceIsRealBoundedChronologicalAndAccountsForDroppedEvents()
    {
        AdmissionFullCostReport report = NativeAdmissionFullCostMeasurement.Run(AdmissionFullCostImplementation.NativeTrace, 2);
        NativeMemoryBudgetStatistics budget = Assert.IsType<NativeMemoryBudgetStatistics>(report.Budget);
        NativeMemoryTraceEvent[] trace = Assert.IsType<NativeMemoryTraceEvent[]>(report.Trace);
        Assert.Equal(64, budget.TraceCapacity);
        Assert.Equal(64, budget.TraceCount);
        Assert.Equal(64, trace.Length);
        Assert.True(budget.DroppedTraceEventCount > 0);
        Assert.False(budget.TraceOverflowed);
        Assert.Equal(budget.DroppedTraceEventCount + 1, trace[0].Sequence);
        Assert.Equal(budget.DroppedTraceEventCount + 64, trace[^1].Sequence);
        Assert.All(trace, observation =>
        {
            Assert.Equal(budget.Id, observation.BudgetId);
            Assert.InRange(observation.CommittedBytes + observation.ReservedBytes, 0, 65_536);
            Assert.True(observation.TimestampTicks > 0);
        });
        for (int index = 1; index < trace.Length; index++)
        {
            Assert.Equal(trace[index - 1].Sequence + 1, trace[index].Sequence);
            Assert.True(trace[index].TimestampTicks >= trace[index - 1].TimestampTicks);
        }
        Assert.Contains(trace, observation => observation.Kind == NativeMemoryTraceKind.ReservationMoved);
        Assert.True(trace.Count(observation => observation.Kind == NativeMemoryTraceKind.Moved) >= 5);
        Assert.Contains(trace, observation => observation.Kind == NativeMemoryTraceKind.Rejected && observation.CommittedBytes == 65_536);
        Assert.Contains(trace, observation => observation.Kind == NativeMemoryTraceKind.ReservationCancelled && observation.RequestedBytes == 11_264);
        Assert.Equal(0, trace[^1].CommittedBytes);
        Assert.Equal(0, trace[^1].ReservedBytes);
    }

    [Fact]
    public void DisabledNativeTracingReportsActualEmptyRingNotUnavailable()
    {
        AdmissionFullCostReport report = NativeAdmissionFullCostMeasurement.Run(AdmissionFullCostImplementation.Native, 2);
        NativeMemoryBudgetStatistics budget = Assert.IsType<NativeMemoryBudgetStatistics>(report.Budget);
        Assert.Equal(0, budget.TraceCapacity);
        Assert.Equal(0, budget.TraceCount);
        Assert.Equal(0, budget.DroppedTraceEventCount);
        Assert.Empty(Assert.IsType<NativeMemoryTraceEvent[]>(report.Trace));
    }

    [Theory]
    [InlineData(2, 204, 0)]
    [InlineData(3, 1, 65_536)]
    public void ManagedBaselineDoesNotInventPhysicalFreeOrHideRetainedCapacity(int implementationValue, long acquisitions, long retained)
    {
        AdmissionFullCostReport report = NativeAdmissionFullCostMeasurement.Run((AdmissionFullCostImplementation)implementationValue, 2);
        ManagedAdmissionObservation before = Assert.IsType<ManagedAdmissionObservation>(report.ManagedBeforeCleanup);
        ManagedAdmissionObservation after = Assert.IsType<ManagedAdmissionObservation>(report.Managed);
        Assert.Equal(0, before.AdmittedBytes);
        Assert.Equal(retained, before.LiveBackingBytes);
        Assert.Equal(65_536, before.PeakLiveBackingBytes);
        Assert.Equal(65_536, before.PeakAdmittedBytes);
        Assert.Equal(0, after.LiveBackingBytes);
        Assert.Equal(0, after.AdmittedBytes);
        Assert.Equal(acquisitions, after.BackingAcquisitionCount);
        Assert.Equal(238, after.AdmissionCount);
        Assert.Equal(102, after.RefusalCount);
        Assert.Equal(34, after.PendingCancellationCount);
        Assert.Equal(34, after.PreparedCancellationCount);
        Assert.Equal(204, after.PreparationCount);
        Assert.Equal(170, after.ProducerCount);
        Assert.Equal(34, after.ReservationMoveCount);
        Assert.Equal(170, after.UniqueMoveCount);
        Assert.Equal(0, before.ConsumedPermissionBytes);
        Assert.Equal(16_384, before.MovedPermissionBytes);
        Assert.Equal(before.ConsumedPermissionBytes, after.ConsumedPermissionBytes);
        Assert.Equal(before.MovedPermissionBytes, after.MovedPermissionBytes);
        Assert.Contains("not observed physical freeing", report.MemoryDomain, StringComparison.Ordinal);
        Assert.Contains("NativeTrace compares optional tracing cost", report.MeasurementScope, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(NativeAdmissionFullCostMeasurement.Serialize(report));
        foreach (string field in new[] { "Budget", "Admission", "Trace", "MovedPermission", "MovedOwner" })
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty(field).ValueKind);
    }

    [Fact]
    public void NativeExtentOutputPreservesUnsignedWidthWithoutRemovingUnavailableIdentities()
    {
        // Explicit serializer fixture, not claimed to be an observed allocation.
        AdmissionFullCostReport observed = NativeAdmissionFullCostMeasurement.Run(AdmissionFullCostImplementation.Native, 1);
        nuint maximum = nuint.MaxValue;
        NativeMemoryTraceEvent fixture = new(1, 1, 1, null, NativeMemoryTraceKind.Rejected, maximum, maximum, 0, 0);
        using JsonDocument json = JsonDocument.Parse(NativeAdmissionFullCostMeasurement.Serialize(observed with { Trace = [fixture] }));
        JsonElement trace = json.RootElement.GetProperty("Trace")[0];
        Assert.Equal((ulong)maximum, trace.GetProperty("RequestedBytes").GetUInt64());
        Assert.Equal((ulong)maximum, trace.GetProperty("PreviousBytes").GetUInt64());
        Assert.Equal(JsonValueKind.Null, trace.GetProperty("OwnerId").ValueKind);
        Assert.Equal(JsonValueKind.Null, trace.GetProperty("AllocationOrdinal").ValueKind);
        Assert.Equal(JsonValueKind.Null, trace.GetProperty("CorrelationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, trace.GetProperty("Generation").ValueKind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void InvalidCycleCountFailsBeforePreparation(int cycles) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        NativeAdmissionFullCostMeasurement.Run(AdmissionFullCostImplementation.Native, cycles));

    [Fact]
    public void InvalidEnumsAndCommandNamesDoNotSilentlySelectAnotherWorkload()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeAdmissionFullCostMeasurement.Run((AdmissionFullCostImplementation)999, 1));
        foreach (string name in new[] { "0", "native", "999" })
            Assert.Throws<ArgumentException>(() => NativeAdmissionFullCostMeasurement.RunCommand(["--admission-full-cost-worker",
                "--implementation", name, "--cycles", "1"]));
        Assert.Throws<ArgumentException>(() => NativeAdmissionFullCostMeasurement.RunCommand(["--admission-full-cost-worker"]));
    }
}
