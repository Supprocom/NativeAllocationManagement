using System.Diagnostics;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Conformance;

// Declared operations determine extents, charges and ordering. Only immutable
// public identities are observed. Clock bounds are sampled outside production.
internal static class NativeTraceDiagnosticOracle
{
    internal static void RunDirect(int traceCapacity)
    {
        NativeMemoryBudget budget = new(32, traceCapacity);
        long before = Stopwatch.GetTimestamp();
        using NativeBuilder<int> builder = new(budget, 2);
        long owner = builder.Id;
        builder.Append(42);
        if (!builder.TryEnsureCapacity(4) || builder.TryEnsureCapacity(17))
            throw new InvalidOperationException("Declared growth/refusal differs.");
        NativeTransfer<int>? source = builder.Complete();
        try
        {
            NativeTransfer<int> moved = NativeTransfer<int>.Move(ref source);
            try
            {
                if (moved.Read(static view => view[0]) != 42)
                    throw new InvalidOperationException("Growth or move changed initialized output.");
            }
            finally { moved.Dispose(); }
        }
        finally { source?.Dispose(); }
        Expected[] expected =
        [
            Event(1, before, budget.Id, owner, NativeMemoryTraceKind.Admitted, 8, 0, 8),
            Event(2, before, budget.Id, owner, NativeMemoryTraceKind.Allocated, 8, 8, 0, ordinal: 1),
            Event(3, before, budget.Id, owner, NativeMemoryTraceKind.Admitted, 16, 8, 16),
            Event(4, before, budget.Id, owner, NativeMemoryTraceKind.Reallocated, 16, 16, 0, previous: 8, ordinal: 1),
            Event(5, before, budget.Id, owner, NativeMemoryTraceKind.Rejected, 68, 16, 0),
            Event(6, before, budget.Id, owner, NativeMemoryTraceKind.Moved, 16, 16, 0, ordinal: 1, correlation: owner),
            Event(7, before, budget.Id, owner, NativeMemoryTraceKind.Released, 16, 0, 0, ordinal: 1),
            Event(8, before, budget.Id, owner, NativeMemoryTraceKind.UniqueReturned, 16, 0, 0, ordinal: 1, correlation: owner)
        ];
        VerifyHistory(budget, expected, Stopwatch.GetTimestamp());
    }

    internal static void RunReservation(int traceCapacity)
    {
        NativeMemoryBudget budget = new(8, traceCapacity);
        long before = Stopwatch.GetTimestamp();
        if (!budget.TryReserve<int>(2, out NativeMemoryReservation<int>? permission, out _))
            throw new InvalidOperationException("Declared reservation was refused.");
        long owner = permission.Value.Id;
        try { permission.Value.PrepareBacking(); }
        finally { permission.Value.Dispose(); }
        Expected[] expected =
        [
            Event(1, before, budget.Id, owner, NativeMemoryTraceKind.ReservationAdmitted, 8, 0, 8, correlation: owner),
            Event(2, before, budget.Id, owner, NativeMemoryTraceKind.Allocated, 8, 8, 0, ordinal: 1),
            Event(3, before, budget.Id, owner, NativeMemoryTraceKind.ReservationBackingPrepared, 8, 8, 0, ordinal: 1, correlation: owner),
            Event(4, before, budget.Id, owner, NativeMemoryTraceKind.Released, 8, 0, 0, ordinal: 1),
            Event(5, before, budget.Id, owner, NativeMemoryTraceKind.ReservationCancelled, 8, 0, 0, ordinal: 1, correlation: owner)
        ];
        VerifyHistory(budget, expected, Stopwatch.GetTimestamp());
    }

    internal static void RunOwnerlessRefusal()
    {
        NativeMemoryBudget budget = new(0, traceCapacity: 1);
        long before = Stopwatch.GetTimestamp();
        if (budget.TryReserve<int>(1, out NativeMemoryReservation<int>? unexpected, out NativeMemoryAdmissionExhaustionReason reason))
        {
            unexpected.Value.Dispose();
            throw new InvalidOperationException("Zero-capacity domain admitted native bytes.");
        }
        if (reason != NativeMemoryAdmissionExhaustionReason.NativeByteCapacity)
            throw new InvalidOperationException("Expected byte refusal was misclassified.");
        VerifyHistory(budget, [Event(1, before, budget.Id, null, NativeMemoryTraceKind.Rejected, 4, 0, 0)], Stopwatch.GetTimestamp());
    }

    internal static void RunGenerationZero()
    {
        // Unix's aligned backend rounds the four-byte payload to 64 bytes;
        // Windows passes the original length. Neither figure includes RSS.
        nuint extent = OperatingSystem.IsWindows() ? 4u : 64u;
        NativeMemoryBudget budget = new(64, traceCapacity: 16);
        long before = Stopwatch.GetTimestamp();
        NativeConcurrentPool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToNativeMemory, false);
        long owner = pool.Id;
        try { pool.ReturnMemoryToNativeMemory(); }
        finally { pool.Dispose(); }
        Expected[] expected =
        [
            Event(1, before, budget.Id, owner, NativeMemoryTraceKind.Admitted, extent, 0, (long)extent),
            Event(2, before, budget.Id, owner, NativeMemoryTraceKind.Allocated, extent, (long)extent, 0, ordinal: 1),
            Event(3, before, budget.Id, owner, NativeMemoryTraceKind.Released, extent, 0, 0, ordinal: 1),
            Event(4, before, budget.Id, owner, NativeMemoryTraceKind.GenerationReleased, extent, 0, 0, correlation: 0, generation: 0)
        ];
        VerifyHistory(budget, expected, Stopwatch.GetTimestamp());
    }

    internal static Expected Event(long sequence, long lowerClockBound, long budget, long? owner,
        NativeMemoryTraceKind kind, nuint bytes, long committed, long reserved,
        nuint previous = 0, long? ordinal = null, long? correlation = null, long? generation = null) => new()
        {
            Sequence = sequence,
            TimestampTicks = lowerClockBound,
            BudgetId = budget,
            OwnerId = owner,
            Kind = kind,
            RequestedBytes = bytes,
            PreviousBytes = previous,
            CommittedBytes = committed,
            ReservedBytes = reserved,
            AllocationOrdinal = ordinal,
            CorrelationId = correlation,
            Generation = generation
        };

    internal static void Verify(NativeMemoryTraceEvent actual, Expected expected, long upperClockBound)
    {
        Check(actual.Sequence == expected.Sequence, nameof(actual.Sequence));
        Check(actual.TimestampTicks >= expected.TimestampTicks && actual.TimestampTicks <= upperClockBound, nameof(actual.TimestampTicks));
        Check(actual.BudgetId == expected.BudgetId, nameof(actual.BudgetId));
        Check(actual.OwnerId == expected.OwnerId, nameof(actual.OwnerId));
        Check(actual.Kind == expected.Kind, nameof(actual.Kind));
        Check(actual.RequestedBytes == expected.RequestedBytes, nameof(actual.RequestedBytes));
        Check(actual.PreviousBytes == expected.PreviousBytes, nameof(actual.PreviousBytes));
        Check(actual.CommittedBytes == expected.CommittedBytes, nameof(actual.CommittedBytes));
        Check(actual.ReservedBytes == expected.ReservedBytes, nameof(actual.ReservedBytes));
        Check(actual.AllocationOrdinal == expected.AllocationOrdinal, nameof(actual.AllocationOrdinal));
        Check(actual.CorrelationId == expected.CorrelationId, nameof(actual.CorrelationId));
        Check(actual.Generation == expected.Generation, nameof(actual.Generation));
    }

    internal static void VerifyHistory(NativeMemoryBudget budget, Expected[] expected, long upperClockBound)
    {
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        int count = Math.Min(snapshot.TraceCapacity, expected.Length);
        Check(snapshot.CommittedBytes == 0 && snapshot.ReservedBytes == 0, "terminal charges");
        Check(snapshot.TraceCount == count, "retained count");
        Check(snapshot.DroppedTraceEventCount == (snapshot.TraceCapacity == 0 ? 0 : expected.Length - count), "dropped count");
        NativeMemoryTraceEvent[] actual = new NativeMemoryTraceEvent[count];
        Check(budget.CopyTraceTo(actual) == count, "complete copy count");
        long prior = long.MinValue;
        for (int index = 0; index < count; index++)
        {
            Verify(actual[index], expected[expected.Length - count + index], upperClockBound);
            Check(actual[index].TimestampTicks >= prior, "monotonic clock ordering");
            prior = actual[index].TimestampTicks;
        }
        NativeMemoryTraceEvent[] repeated = new NativeMemoryTraceEvent[count];
        Check(budget.CopyTraceTo(repeated) == count && actual.AsSpan().SequenceEqual(repeated), "non-consuming copy");
        Span<NativeMemoryTraceEvent> newest = stackalloc NativeMemoryTraceEvent[1];
        Check(budget.CopyTraceTo(newest) == Math.Min(1, count), "short newest suffix count");
        if (count != 0) Check(newest[0] == actual[^1], "short newest suffix value");
        Check(budget.CopyTraceTo(Span<NativeMemoryTraceEvent>.Empty) == 0, "empty copy count");
        Check(snapshot == budget.CaptureStatistics(), "copy cannot mutate domain counters");
    }

    private static void Check(bool condition, string field)
    {
        if (!condition) throw new InvalidOperationException("Independent trace contract differs: " + field);
    }

    // TimestampTicks stores an independent LOWER clock bound, not a copied
    // producer value. Verify also requires the independent upper bound.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal readonly record struct Expected
    {
        public long Sequence { get; init; }
        public long TimestampTicks { get; init; }
        public long BudgetId { get; init; }
        public long? OwnerId { get; init; }
        public NativeMemoryTraceKind Kind { get; init; }
        public nuint RequestedBytes { get; init; }
        public nuint PreviousBytes { get; init; }
        public long CommittedBytes { get; init; }
        public long ReservedBytes { get; init; }
        public long? AllocationOrdinal { get; init; }
        public long? CorrelationId { get; init; }
        public long? Generation { get; init; }
    }
}
