using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Performance;

// Diagnostic attribution only. No branches are added to the timed ownership worker.
internal static class NativeOwnershipAllocationProbe
{
    private static readonly NativeLeaseInitializer<byte> Fill = static writer => writer.Fill(7);
    private static readonly NativeLeaseFunc<byte, long> Read = static view =>
    {
        ReadOnlySpan<byte> values = view.AsSpan();
        if (values.Length != NativeOwnershipFullCostMeasurement.PayloadBytes) throw new InvalidDataException("Payload length differs.");
        long sum = 0;
        foreach (ref readonly byte value in values)
        {
            if (value != 7) throw new InvalidDataException("Payload content differs.");
            sum += value;
        }
        return sum;
    };
    private static readonly string[] StageNames = ["initial process snapshot", "budget creation", "admission", "activation and initialization", "unique moves", "bounded reads", "physical return", "terminal diagnostics"];

    internal static int RunCommand(string[] arguments)
    {
        if (arguments is not ["--ownership-allocation-probe", "--rounds", string roundsText]
            || !int.TryParse(roundsText, NumberStyles.None, CultureInfo.InvariantCulture, out int rounds))
            throw new ArgumentException("Expected --ownership-allocation-probe --rounds <1..32>.", nameof(arguments));
        OwnershipAllocationProbeReport report = Run(rounds);
        Console.WriteLine(JsonSerializer.Serialize(report));
        return report.ExactOutput && report.ExactCleanup && report.ExactNativeAccounting ? 0 : 3;
    }

    internal static OwnershipAllocationProbeReport Run(int rounds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rounds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rounds, 32);
        _ = Fill; _ = Read; _ = StageNames;
        OwnershipAllocationStage[] stages = new OwnershipAllocationStage[3 + rounds * 5];
        int index = 0;
        long overallBefore = GC.GetAllocatedBytesForCurrentThread();
        long before = overallBefore;
        NativeMemoryStatistics processBefore = NativeMemoryDiagnostics.Snapshot();
        Record(stages, ref index, stage: 0, round: -1, before);
        before = GC.GetAllocatedBytesForCurrentThread();
        NativeMemoryBudget budget = new(NativeOwnershipFullCostMeasurement.PayloadBytes);
        Record(stages, ref index, stage: 1, round: -1, before);
        long checksum = 0;
        NativeTransfer<byte> last = default;
        for (int round = 0; round < rounds; round++)
        {
            NativeMemoryReservation<byte>? permission = null;
            NativeTransfer<byte>? moving = null;
            try
            {
                before = GC.GetAllocatedBytesForCurrentThread();
                if (!budget.TryReserve<byte>(NativeOwnershipFullCostMeasurement.PayloadBytes, out permission, out _))
                    throw new InvalidOperationException("Declared ownership admission was refused.");
                Record(stages, ref index, stage: 2, round, before);
                before = GC.GetAllocatedBytesForCurrentThread();
                moving = NativeMemoryReservation<byte>.Activate(ref permission, Fill);
                Record(stages, ref index, stage: 3, round, before);
                before = GC.GetAllocatedBytesForCurrentThread();
                for (int move = 0; move < NativeOwnershipFullCostMeasurement.Moves; move++)
                    moving = NativeTransfer<byte>.Move(ref moving);
                Record(stages, ref index, stage: 4, round, before);
                before = GC.GetAllocatedBytesForCurrentThread();
                for (int read = 0; read < NativeOwnershipFullCostMeasurement.Reads; read++)
                    checksum += moving.Value.Read(Read);
                Record(stages, ref index, stage: 5, round, before);
                before = GC.GetAllocatedBytesForCurrentThread();
                last = moving.Value;
                moving.Value.Dispose();
                moving = null;
                Record(stages, ref index, stage: 6, round, before);
            }
            // Destructive ref APIs consume permissions/bindings, also on failure.
#pragma warning disable CA1508
            finally { moving?.Dispose(); permission?.Dispose(); }
#pragma warning restore CA1508
        }
        before = GC.GetAllocatedBytesForCurrentThread();
        NativeMemoryBudgetStatistics terminalBudget = budget.CaptureStatistics();
        NativeMemoryAdmissionStatistics terminalAdmission = budget.CaptureAdmissionStatistics();
        NativeTransferStatistics terminalUnique = last.CaptureSnapshot();
        NativeMemoryStatistics processAfter = NativeMemoryDiagnostics.Snapshot();
        Record(stages, ref index, stage: 7, round: -1, before);
        long overallAfter = GC.GetAllocatedBytesForCurrentThread();
        long attributed = 0;
        foreach (ref readonly OwnershipAllocationStage stage in stages.AsSpan()) attributed = checked(attributed + stage.ManagedAllocatedBytes);
        bool cleanup = terminalBudget is { ReservedBytes: 0, CommittedBytes: 0, ActiveAllocationCount: 0 }
            && terminalAdmission is { OutstandingReservationCount: 0, PendingBytes: 0, PreparedUnpublishedBytes: 0 }
            && terminalUnique is { HasReturnObligation: false, OwnedBackingBytes: 0, ActiveBorrowCount: 0 };
        bool accounting = terminalBudget.AllocationCount == rounds && terminalBudget.FreeCount == rounds
            && terminalBudget.AcquiredBackingBytes == (long)rounds * NativeOwnershipFullCostMeasurement.PayloadBytes
            && terminalBudget.PeakAdmittedBytes == NativeOwnershipFullCostMeasurement.PayloadBytes
            && terminalBudget.RejectedAllocationCount == 0 && terminalBudget.FailedAllocationCount == 0
            && terminalAdmission.AdmittedReservationCount == rounds && terminalAdmission.ActivationCount == rounds
            && terminalUnique.MoveCount == NativeOwnershipFullCostMeasurement.Moves && terminalUnique.PayloadReturnCount == 1
            && processBefore.MetricsEpoch == processAfter.MetricsEpoch
            && processAfter.OutstandingNativeBytes == processBefore.OutstandingNativeBytes;
        return new(1, rounds, stages, overallBefore, overallAfter, attributed,
            checked(overallAfter - overallBefore - attributed), checksum,
            checksum == NativeOwnershipFullCostMeasurement.ExpectedChecksum(OwnershipFullCostContract.Unique, rounds),
            cleanup, accounting, terminalBudget, terminalAdmission, terminalUnique,
            Environment.Version.ToString(), RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessId,
            Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
            "Diagnostic-only current-thread managed allocation deltas, including initial NAM snapshot and budget preparation. Native extents remain separate in the actual budget. Instrumentation array, cached delegates/stage names and report serialization are outside the overall window. Between-stage bytes are measured, not subtracted as noise. Additional counter boundaries perturb execution; neither timing nor allocation equality with the uninstrumented worker is assumed.");
    }

    private static void Record(OwnershipAllocationStage[] stages, ref int index, int stage, int round, long before)
    {
        long after = GC.GetAllocatedBytesForCurrentThread();
        stages[index++] = new(StageNames[stage], round, before, after);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct OwnershipAllocationStage(string Stage, int Round, long BeforeBytes, long AfterBytes)
{
    public long ManagedAllocatedBytes => checked(AfterBytes - BeforeBytes);
}

internal sealed record OwnershipAllocationProbeReport(int SchemaVersion, int Rounds, OwnershipAllocationStage[] Stages,
    long OverallBeforeBytes, long OverallAfterBytes, long AttributedManagedBytes, long BetweenStageManagedBytes, long Checksum,
    bool ExactOutput, bool ExactCleanup, bool ExactNativeAccounting, NativeMemoryBudgetStatistics Budget,
    NativeMemoryAdmissionStatistics Admission, NativeTransferStatistics TerminalUnique, string Runtime, string Architecture,
    int ProcessId, string? TieredCompilation, string? TieredPgo, string Scope);
