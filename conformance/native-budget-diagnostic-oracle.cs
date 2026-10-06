using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Conformance;

// Test-only independent expectations. This exact public-API-only source also
// compiles in a consumer of the packaged library, without project references.
internal static class NativeBudgetDiagnosticOracle
{
    internal static void Run(int traceCapacity)
    {
        RunBuilder(traceCapacity);
        RunPendingReservation(traceCapacity);
        RunPreparedReservation(traceCapacity);
    }

    private static void RunBuilder(int traceCapacity)
    {
        NativeMemoryBudget budget = new(64, traceCapacity);
        Expected expected = Expected.Empty(budget.Id, 64, traceCapacity);
        Verify(budget, expected, "builder:initial");
        using (NativeBuilder<int> builder = new(budget, 2))
        {
            expected = expected with
            {
                CommittedBytes = 8,
                PeakCommittedBytes = 8,
                PeakAdmittedBytes = 8,
                AllocationCount = 1,
                ActiveAllocationCount = 1
            };
            Verify(budget, expected.WithEvents(2), "builder:allocated");
            builder.Append(42);
            if (!builder.TryEnsureCapacity(3) || builder.Capacity != 4 || builder.Count != 1)
                throw new InvalidOperationException("The declared builder growth did not occur.");
            expected = expected with
            {
                CommittedBytes = 16,
                PeakCommittedBytes = 16,
                PeakAdmittedBytes = 24,
                ReallocationCount = 1
            };
            Verify(budget, expected.WithEvents(4), "builder:reallocated");
            if (builder.TryEnsureCapacity(17) || builder.Capacity != 4 || builder.Count != 1)
                throw new InvalidOperationException("Capacity refusal did not preserve the builder prefix.");
            expected = expected with { RejectedAllocationCount = 1 };
            Verify(budget, expected.WithEvents(5), "builder:refused");
        }
        expected = expected with { CommittedBytes = 0, ActiveAllocationCount = 0, FreeCount = 1 };
        Verify(budget, expected.WithEvents(6), "builder:released");
        VerifyTraceCopy(budget, expected.WithEvents(6), eventCount: 6);
    }

    private static void RunPendingReservation(int traceCapacity)
    {
        NativeMemoryBudget budget = new(16, traceCapacity);
        Expected expected = Expected.Empty(budget.Id, 16, traceCapacity);
        Verify(budget, expected, "pending:initial");
        if (!budget.TryReserve<int>(4, out NativeMemoryReservation<int>? permission, out NativeMemoryAdmissionExhaustionReason reason))
            throw new InvalidOperationException("Pending permission was not admitted.");
        using (NativeMemoryReservation<int> reservation = NativeMemoryReservation<int>.Move(ref permission))
        {
            if (reason != NativeMemoryAdmissionExhaustionReason.None)
                throw new InvalidOperationException("Successful permission reported a refusal reason.");
            expected = expected with { ReservedBytes = 16, PeakAdmittedBytes = 16 };
            Verify(budget, expected.WithEvents(2), "pending:admitted-and-moved");
        }
        expected = expected with { ReservedBytes = 0 };
        Verify(budget, expected.WithEvents(3), "pending:cancelled-without-acquisition");
        VerifyTraceCopy(budget, expected.WithEvents(3), eventCount: 3);
    }

    private static void RunPreparedReservation(int traceCapacity)
    {
        NativeMemoryBudget budget = new(16, traceCapacity);
        Expected expected = Expected.Empty(budget.Id, 16, traceCapacity);
        if (!budget.TryReserve<int>(4, out NativeMemoryReservation<int>? permission, out _))
            throw new InvalidOperationException("Prepared permission was not admitted.");
        using (NativeMemoryReservation<int> reservation = NativeMemoryReservation<int>.Move(ref permission))
        {
            expected = expected with { ReservedBytes = 16, PeakAdmittedBytes = 16 };
            Verify(budget, expected.WithEvents(2), "prepared:admitted-and-moved");
            if (budget.TryReserve<byte>(1, out NativeMemoryReservation<byte>? refused, out NativeMemoryAdmissionExhaustionReason reason))
            {
                using NativeMemoryReservation<byte> unexpected = NativeMemoryReservation<byte>.Move(ref refused);
                throw new InvalidOperationException("A pending reservation did not constrain admission.");
            }
            if (reason != NativeMemoryAdmissionExhaustionReason.NativeByteCapacity)
                throw new InvalidOperationException("Capacity refusal reported a different reason.");
            expected = expected with { RejectedAllocationCount = 1 };
            Verify(budget, expected.WithEvents(3), "prepared:refused");
            reservation.PrepareBacking();
            expected = expected with
            {
                ReservedBytes = 0,
                CommittedBytes = 16,
                PeakCommittedBytes = 16,
                AllocationCount = 1,
                ActiveAllocationCount = 1
            };
            Verify(budget, expected.WithEvents(5), "prepared:acquired-unpublished");
        }
        expected = expected with { CommittedBytes = 0, ActiveAllocationCount = 0, FreeCount = 1 };
        Verify(budget, expected.WithEvents(7), "prepared:cancelled-and-released");
        VerifyTraceCopy(budget, expected.WithEvents(7), eventCount: 7);
    }

    // Compare every property independently. Do not construct the production
    // snapshot type, call its internal constructor, or derive expected values
    // from another production snapshot.
    internal static void Verify(NativeMemoryBudget budget, Expected expected, string stage)
    {
        NativeMemoryBudgetStatistics actual = budget.CaptureStatistics();
        if (expected.Id <= 0) throw new InvalidOperationException("Budget identity must be positive.");
        Check(nameof(actual.Id), actual.Id, expected.Id, stage);
        Check(nameof(actual.CapacityBytes), actual.CapacityBytes, expected.CapacityBytes, stage);
        Check(nameof(actual.CommittedBytes), actual.CommittedBytes, expected.CommittedBytes, stage);
        Check(nameof(actual.ReservedBytes), actual.ReservedBytes, expected.ReservedBytes, stage);
        Check(nameof(actual.PeakCommittedBytes), actual.PeakCommittedBytes, expected.PeakCommittedBytes, stage);
        Check(nameof(actual.PeakAdmittedBytes), actual.PeakAdmittedBytes, expected.PeakAdmittedBytes, stage);
        Check(nameof(actual.AllocationCount), actual.AllocationCount, expected.AllocationCount, stage);
        Check(nameof(actual.ReallocationCount), actual.ReallocationCount, expected.ReallocationCount, stage);
        Check(nameof(actual.FreeCount), actual.FreeCount, expected.FreeCount, stage);
        Check(nameof(actual.ActiveAllocationCount), actual.ActiveAllocationCount, expected.ActiveAllocationCount, stage);
        Check(nameof(actual.RejectedAllocationCount), actual.RejectedAllocationCount, expected.RejectedAllocationCount, stage);
        Check(nameof(actual.FailedAllocationCount), actual.FailedAllocationCount, expected.FailedAllocationCount, stage);
        Check(nameof(actual.TraceCapacity), actual.TraceCapacity, expected.TraceCapacity, stage);
        Check(nameof(actual.TraceCount), actual.TraceCount, expected.TraceCount, stage);
        Check(nameof(actual.DroppedTraceEventCount), actual.DroppedTraceEventCount, expected.DroppedTraceEventCount, stage);
        Check(nameof(actual.TraceOverflowed), actual.TraceOverflowed, expected.TraceOverflowed, stage);
        Check(nameof(actual.HistoryOverflowed), actual.HistoryOverflowed, expected.HistoryOverflowed, stage);
    }

    private static void VerifyTraceCopy(NativeMemoryBudget budget, Expected expected, int eventCount)
    {
        Span<NativeMemoryTraceEvent> destination = stackalloc NativeMemoryTraceEvent[4];
        int copied = budget.CopyTraceTo(destination);
        Check("copied", copied, expected.TraceCount, "trace-copy");
        for (int index = 0; index < copied; index++)
        {
            Check("sequence", destination[index].Sequence, eventCount - copied + index + 1, "trace-copy");
            Check("budgetId", destination[index].BudgetId, expected.Id, "trace-copy");
        }
        int suffix = budget.CopyTraceTo(destination[..2]);
        Check("suffix", suffix, Math.Min(2, expected.TraceCount), "trace-copy");
        for (int index = 0; index < suffix; index++)
            Check("suffix-sequence", destination[index].Sequence, eventCount - suffix + index + 1, "trace-copy");
        Verify(budget, expected, "trace-copy:does-not-consume-or-reset");
    }

    private static void Check(string field, long actual, long expected, string stage)
    {
        if (actual != expected)
            throw new InvalidOperationException($"{stage}: {field} was {actual}, expected {expected}.");
    }

    private static void Check(string field, bool actual, bool expected, string stage)
    {
        if (actual != expected)
            throw new InvalidOperationException($"{stage}: {field} was {actual}, expected {expected}.");
    }

    internal readonly record struct Expected(
        long Id, long CapacityBytes, long CommittedBytes, long ReservedBytes,
        long PeakCommittedBytes, long PeakAdmittedBytes, long AllocationCount,
        long ReallocationCount, long FreeCount, long ActiveAllocationCount,
        long RejectedAllocationCount, long FailedAllocationCount, int TraceCapacity,
        int TraceCount, long DroppedTraceEventCount, bool TraceOverflowed, bool HistoryOverflowed)
    {
        internal static Expected Empty(long id, long capacityBytes, int traceCapacity) =>
            new(id, capacityBytes, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, traceCapacity, 0, 0, false, false);

        internal Expected WithEvents(int count) => this with
        {
            TraceCount = TraceCapacity == 0 ? 0 : Math.Min(count, TraceCapacity),
            DroppedTraceEventCount = TraceCapacity == 0 ? 0 : Math.Max(0, count - TraceCapacity)
        };
    }
}
