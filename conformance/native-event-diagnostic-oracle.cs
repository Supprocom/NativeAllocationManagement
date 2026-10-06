using System.Diagnostics;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Conformance;

// Full ordered histories from declared operations, not copied event values.
internal static class NativeEventDiagnosticOracle
{
    internal static NativeMemoryTraceKind[] RunOrdinary(int traceCapacity)
    {
        NativeMemoryBudget budget = new(32, traceCapacity);
        long before = Stopwatch.GetTimestamp();
        NativeBuilder<int> builder = new(budget, 2);
        long owner = builder.Id;
        try { builder.Append(42); if (!builder.TryEnsureCapacity(4) || builder.TryEnsureCapacity(17)) throw new InvalidOperationException("Declared growth differs."); }
        finally { builder.Dispose(); }
        return Verify(budget, before,
        [
            Item(NativeMemoryTraceKind.Admitted, 8, 0, 8, owner),
            Item(NativeMemoryTraceKind.Allocated, 8, 8, 0, owner, ordinal: 1),
            Item(NativeMemoryTraceKind.Admitted, 16, 8, 16, owner),
            Item(NativeMemoryTraceKind.Reallocated, 16, 16, 0, owner, ordinal: 1, previous: 8),
            Item(NativeMemoryTraceKind.Rejected, 68, 16, 0, owner),
            Item(NativeMemoryTraceKind.Released, 16, 0, 0, owner, ordinal: 1)
        ]);
    }

    internal static NativeMemoryTraceKind[] RunPermission(int traceCapacity)
    {
        NativeMemoryBudget budget = new(8, traceCapacity);
        long before = Stopwatch.GetTimestamp();
        if (!budget.TryReserve<int>(2, out NativeMemoryReservation<int>? source, out _))
            throw new InvalidOperationException("Declared permission was refused.");
        long owner = source.Value.Id;
        NativeMemoryReservation<int>? moved = NativeMemoryReservation<int>.Move(ref source);
        try
        {
            moved.Value.PrepareBacking();
            NativeTransfer<int> unique = NativeMemoryReservation<int>.Activate(ref moved, static writer => writer.Fill(42));
            try { if (unique.Read(static view => view[0] + view[1]) != 84) throw new InvalidOperationException("Declared output differs."); }
            finally { unique.Dispose(); }
        }
        finally { moved?.Dispose(); source?.Dispose(); }
        return Verify(budget, before,
        [
            Item(NativeMemoryTraceKind.ReservationAdmitted, 8, 0, 8, owner, correlation: owner),
            Item(NativeMemoryTraceKind.ReservationMoved, 8, 0, 8, owner, correlation: owner),
            Item(NativeMemoryTraceKind.Allocated, 8, 8, 0, owner, ordinal: 1),
            Item(NativeMemoryTraceKind.ReservationBackingPrepared, 8, 8, 0, owner, ordinal: 1, correlation: owner),
            Item(NativeMemoryTraceKind.ReservationActivated, 8, 8, 0, owner, ordinal: 1, correlation: owner),
            Item(NativeMemoryTraceKind.Released, 8, 0, 0, owner, ordinal: 1),
            Item(NativeMemoryTraceKind.UniqueReturned, 8, 0, 0, owner, ordinal: 1, correlation: owner)
        ]);
    }

    internal static NativeMemoryTraceKind[] RunCancelled(int traceCapacity, bool prepared)
    {
        NativeMemoryBudget budget = new(8, traceCapacity);
        long before = Stopwatch.GetTimestamp();
        if (!budget.TryReserve<int>(2, out NativeMemoryReservation<int>? permission, out _))
            throw new InvalidOperationException("Declared permission was refused.");
        long owner = permission.Value.Id;
        try { if (prepared) permission.Value.PrepareBacking(); }
        finally { permission.Value.Dispose(); }
        return Verify(budget, before, prepared ?
        [
            Item(NativeMemoryTraceKind.ReservationAdmitted, 8, 0, 8, owner, correlation: owner),
            Item(NativeMemoryTraceKind.Allocated, 8, 8, 0, owner, ordinal: 1),
            Item(NativeMemoryTraceKind.ReservationBackingPrepared, 8, 8, 0, owner, ordinal: 1, correlation: owner),
            Item(NativeMemoryTraceKind.Released, 8, 0, 0, owner, ordinal: 1),
            Item(NativeMemoryTraceKind.ReservationCancelled, 8, 0, 0, owner, ordinal: 1, correlation: owner)
        ] :
        [
            Item(NativeMemoryTraceKind.ReservationAdmitted, 8, 0, 8, owner, correlation: owner),
            Item(NativeMemoryTraceKind.ReservationCancelled, 8, 0, 0, owner, correlation: owner)
        ]);
    }

    internal static NativeMemoryTraceKind[] RunLayout(int traceCapacity)
    {
        NativeLayoutBuilder shape = new(2);
        NativeLayoutField<byte> first = shape.Add<byte>(3);
        NativeLayoutField<byte> second = shape.Add<byte>(2);
        NativeLayout layout = shape.Build();
        NativeMemoryBudget budget = new(8, traceCapacity);
        long before = Stopwatch.GetTimestamp();
        if (!layout.TryReserve(budget, out NativeLayoutReservation? permission, out _))
            throw new InvalidOperationException("Declared layout was refused.");
        long owner = permission.Value.Id;
        long copiedOwner = 0;
        NativeLayoutOwner unique = NativeLayoutReservation.Activate(ref permission, (first, second), static (writer, fields) =>
        { writer.Region(fields.first).Fill(17); writer.Region(fields.second).Fill(19); });
        try
        {
            NativeTransfer<byte> copy = unique.DetachField(first, budget);
            copiedOwner = copy.Id;
            try { if (copy.Read(static view => view[0] + view[2]) != 34) throw new InvalidOperationException("Declared copy differs."); }
            finally { copy.Dispose(); }
        }
        finally { unique.Dispose(); }
        return Verify(budget, before,
        [
            Item(NativeMemoryTraceKind.ReservationAdmitted, 5, 0, 5, owner, correlation: owner),
            Item(NativeMemoryTraceKind.Allocated, 5, 5, 0, owner, ordinal: 1),
            Item(NativeMemoryTraceKind.ReservationBackingPrepared, 5, 5, 0, owner, ordinal: 1, correlation: owner),
            Item(NativeMemoryTraceKind.LayoutPrepared, 5, 5, 0, owner, ordinal: 1, correlation: layout.Id),
            Item(NativeMemoryTraceKind.LayoutInitialized, 5, 5, 0, owner, ordinal: 1, correlation: layout.Id),
            Item(NativeMemoryTraceKind.ReservationActivated, 5, 5, 0, owner, ordinal: 1, correlation: owner),
            Item(NativeMemoryTraceKind.ReservationAdmitted, 3, 5, 3, copiedOwner, correlation: copiedOwner),
            Item(NativeMemoryTraceKind.Allocated, 3, 8, 0, copiedOwner, ordinal: 1),
            Item(NativeMemoryTraceKind.ReservationBackingPrepared, 3, 8, 0, copiedOwner, ordinal: 1, correlation: copiedOwner),
            Item(NativeMemoryTraceKind.ReservationActivated, 3, 8, 0, copiedOwner, ordinal: 1, correlation: copiedOwner),
            Item(NativeMemoryTraceKind.Detached, 3, 8, 0, owner, ordinal: 1, correlation: copiedOwner),
            Item(NativeMemoryTraceKind.Released, 3, 5, 0, copiedOwner, ordinal: 1),
            Item(NativeMemoryTraceKind.UniqueReturned, 3, 5, 0, copiedOwner, ordinal: 1, correlation: copiedOwner),
            Item(NativeMemoryTraceKind.Released, 5, 0, 0, owner, ordinal: 1),
            Item(NativeMemoryTraceKind.UniqueReturned, 5, 0, 0, owner, ordinal: 1, correlation: owner)
        ]);
    }

    internal static NativeMemoryTraceKind[] RunSharing(int traceCapacity)
    {
        NativeMemoryBudget budget = new(8, traceCapacity);
        long before = Stopwatch.GetTimestamp();
        using NativeBuilder<int> builder = new(budget, 2);
        long owner = builder.Id;
        builder.Append([17, 19]);
        NativeTransfer<int>? source = builder.Complete();
        NativeShared<int> shared = NativeShared<int>.Create(ref source, new(2, 1));
        long control = shared.CaptureSnapshot().Id;
        if (!shared.TryDowngrade(out NativeWeak<int> weak, out _))
        {
            shared.Dispose();
            throw new InvalidOperationException("Declared weak observation was refused.");
        }
        try
        {
            try
            {
                if (!shared.TryShare(out NativeShared<int> clone, out _)) throw new InvalidOperationException("Declared share was refused.");
                try
                {
                    if (weak.TryUpgrade(out NativeShared<int> unexpected, out _))
                    { unexpected.Dispose(); throw new InvalidOperationException("Full strong table admitted an upgrade."); }
                }
                finally { clone.Dispose(); }
                if (!weak.TryUpgrade(out NativeShared<int> upgrade, out _)) throw new InvalidOperationException("Live weak upgrade was refused.");
                try { if (upgrade.Read(static view => view[0] + view[1]) != 36) throw new InvalidOperationException("Shared output differs."); }
                finally { upgrade.Dispose(); }
            }
            finally { shared.Dispose(); }
            if (weak.TryUpgrade(out NativeShared<int> revived, out _))
            { revived.Dispose(); throw new InvalidOperationException("Expired payload was resurrected."); }
        }
        finally { weak.Dispose(); source?.Dispose(); }
        return Verify(budget, before,
        [
            Item(NativeMemoryTraceKind.Admitted, 8, 0, 8, owner),
            Item(NativeMemoryTraceKind.Allocated, 8, 8, 0, owner, ordinal: 1),
            Item(NativeMemoryTraceKind.Moved, 8, 8, 0, owner, ordinal: 1, correlation: owner),
            Item(NativeMemoryTraceKind.Shared, 8, 8, 0, owner, ordinal: 1, correlation: control),
            Item(NativeMemoryTraceKind.WeakCreated, 8, 8, 0, owner, ordinal: 1, correlation: control),
            Item(NativeMemoryTraceKind.Shared, 8, 8, 0, owner, ordinal: 1, correlation: control),
            Item(NativeMemoryTraceKind.UpgradeRejected, 8, 8, 0, owner, ordinal: 1, correlation: control),
            Item(NativeMemoryTraceKind.OwnershipReleased, 8, 8, 0, owner, ordinal: 1, correlation: control),
            Item(NativeMemoryTraceKind.WeakUpgraded, 8, 8, 0, owner, ordinal: 1, correlation: control),
            Item(NativeMemoryTraceKind.OwnershipReleased, 8, 8, 0, owner, ordinal: 1, correlation: control),
            Item(NativeMemoryTraceKind.OwnershipReleased, 8, 8, 0, owner, ordinal: 1, correlation: control),
            Item(NativeMemoryTraceKind.Released, 8, 0, 0, owner, ordinal: 1),
            Item(NativeMemoryTraceKind.UniqueReturned, 8, 0, 0, owner, ordinal: 1, correlation: owner),
            Item(NativeMemoryTraceKind.PayloadReturned, 8, 0, 0, owner, ordinal: 1, correlation: control),
            Item(NativeMemoryTraceKind.UpgradeRejected, 0, 0, 0, owner, ordinal: 1, correlation: control),
            Item(NativeMemoryTraceKind.OwnershipReleased, 0, 0, 0, owner, ordinal: 1, correlation: control)
        ]);
    }

    internal static NativeMemoryTraceKind[] RunPreparedPages(int traceCapacity)
    {
        NativeMemoryBudget budget = new(128, traceCapacity);
        long before = Stopwatch.GetTimestamp();
        NativePool<int> pool = new(new NativePoolPreparation(2, 4, 2), budget);
        long owner = pool.Id;
        try
        {
            using (Pooled<int> value = pool.Rent(4, static writer => writer.Fill(42)))
            {
                if (value.Read(static view => view[3]) != 42 || pool.TrimRetainedMemory() != 0)
                    throw new InvalidOperationException("A live page was altered or freed.");
            }
            if (pool.TrimRetainedMemory() != 128) throw new InvalidOperationException("Idle page extent differs.");
        }
        finally { pool.Dispose(); }
        return Verify(budget, before,
        [
            Item(NativeMemoryTraceKind.Admitted, 128, 0, 128, owner),
            Item(NativeMemoryTraceKind.PageAcquired, 128, 128, 0, owner, ordinal: 1),
            Item(NativeMemoryTraceKind.Prepared, 128, 128, 0, owner),
            Item(NativeMemoryTraceKind.Trimmed, 128, 0, 0, owner, ordinal: 1)
        ]);
    }

    internal static NativeTraceDiagnosticOracle.Expected Item(NativeMemoryTraceKind kind, nuint bytes, long committed, long reserved,
        long? owner, long? ordinal = null, long? correlation = null, nuint previous = 0, long? generation = null) =>
        new()
        {
            Kind = kind,
            RequestedBytes = bytes,
            PreviousBytes = previous,
            CommittedBytes = committed,
            ReservedBytes = reserved,
            OwnerId = owner,
            AllocationOrdinal = ordinal,
            CorrelationId = correlation,
            Generation = generation
        };

    internal static NativeMemoryTraceKind[] Verify(NativeMemoryBudget budget, long before, NativeTraceDiagnosticOracle.Expected[] expected)
    {
        long after = Stopwatch.GetTimestamp();
        for (int index = 0; index < expected.Length; index++)
            expected[index] = expected[index] with { Sequence = index + 1, TimestampTicks = before, BudgetId = budget.Id };
        NativeTraceDiagnosticOracle.VerifyHistory(budget, expected, after);
        NativeMemoryTraceEvent[] actual = new NativeMemoryTraceEvent[budget.CaptureStatistics().TraceCount];
        if (budget.CopyTraceTo(actual) != actual.Length) throw new InvalidOperationException("Trace copy count differs.");
        NativeMemoryTraceKind[] kinds = new NativeMemoryTraceKind[actual.Length];
        for (int index = 0; index < actual.Length; index++) kinds[index] = actual[index].Kind;
        if (budget.CaptureStatistics().CommittedBytes != 0 || budget.CaptureStatistics().ReservedBytes != 0)
            throw new InvalidOperationException("Event scenario left physical or pending storage charged.");
        return kinds;
    }
}
