using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Conformance;

// Independent declared shapes and operations, not a copy of captured gauges.
// Comparison is test-only; none of this reflection runs in the allocator.
internal static class NativeCapacityDiagnosticOracle
{
    internal const int ArenaHeaderBytes = 64;
    internal const int PoolSlotBytes = 24;
    internal const int PoolPageBytes = 40;

    internal static long ArenaExtent(int capacity) => capacity == 0 ? 0
        : OperatingSystem.IsWindows() ? checked(ArenaHeaderBytes + capacity)
        : checked((ArenaHeaderBytes + capacity + 63L) / 64 * 64);

    internal static void RunPool(int traceCapacity)
    {
        NativeMemoryBudget budget = new(128, traceCapacity);
        NativePreparedPool<int> pool = new(new NativePoolPreparation(2, 4, 2), budget);
        Pool expected = new()
        {
            OwnerId = pool.Id,
            Lifecycle = NativeOwnerLifecycle.Active,
            Preparation = new(2, 4, 2),
            RetainedPageCount = 1,
            RetainedSlotCount = 2,
            AvailableSlotCount = 2,
            RetainedBytes = 32,
            PeakRetainedBytes = 32,
            ManagedBankBytes = 2 * PoolSlotBytes + PoolPageBytes,
            UnusedSlotBytes = 32
        };
        try
        {
            Verify(pool.CapturePreparedSnapshot(), expected, "pool-prepared");
            bool failed = false;
            try { using PreparedPooled<int> incomplete = pool.Rent(1, static _ => { }); }
            catch (InvalidOperationException) { failed = true; }
            if (!failed) throw new InvalidOperationException("Incomplete initialization was published.");
            expected = expected with { PeakOccupiedSlotCount = 1, InitializerFailureCount = 1 };
            Verify(pool.CapturePreparedSnapshot(), expected, "pool-incomplete-rolled-back");
            using (PreparedPooled<int> first = pool.Rent(4, static writer => writer.Fill(7)))
            {
                expected = expected with { OccupiedSlotCount = 1, AvailableSlotCount = 1, SuccessfulRentCount = 1, UnusedSlotBytes = 16 };
                Verify(pool.CapturePreparedSnapshot(), expected, "pool-sparse-page-pinned");
                if (pool.TrimRetainedMemory() != 0) throw new InvalidOperationException("Live slot's page was freed.");
                Verify(pool.CapturePreparedSnapshot(), expected, "pool-sparse-trim-cannot-free-live-page");
                using (PreparedPooled<int> second = pool.Rent(2, static writer => writer.Fill(11)))
                {
                    expected = expected with
                    { OccupiedSlotCount = 2, PeakOccupiedSlotCount = 2, AvailableSlotCount = 0, SuccessfulRentCount = 2, UnusedSlotBytes = 0 };
                    Verify(pool.CapturePreparedSnapshot(), expected, "pool-dense");
                    if (pool.TryRent(5, static writer => writer.Fill(0), out PreparedPooled<int> oversized, out NativePoolExhaustionReason shape))
                    {
                        oversized.Dispose();
                        throw new InvalidOperationException("Oversized shape was accepted.");
                    }
                    if (shape != NativePoolExhaustionReason.ShapeExceeded) throw new InvalidOperationException("Shape refusal was misclassified.");
                    expected = expected with { RejectedShapeCount = 1 };
                    Verify(pool.CapturePreparedSnapshot(), expected, "pool-shape-refused");
                    if (pool.TryRent(1, static writer => writer.Fill(0), out PreparedPooled<int> full, out NativePoolExhaustionReason reason))
                    {
                        full.Dispose();
                        throw new InvalidOperationException("Declared slot capacity was exceeded.");
                    }
                    if (reason != NativePoolExhaustionReason.NoAvailableSlot) throw new InvalidOperationException("Full refusal was misclassified.");
                    expected = expected with { RejectedFullCount = 1 };
                    Verify(pool.CapturePreparedSnapshot(), expected, "pool-full-refused");
                    if (first.Read(static view => view[3]) != 7 || second.Read(static view => view[1]) != 11)
                        throw new InvalidOperationException("Capacity refusal changed published output.");
                }
                expected = expected with { OccupiedSlotCount = 1, AvailableSlotCount = 1, UnusedSlotBytes = 16 };
                Verify(pool.CapturePreparedSnapshot(), expected, "pool-second-returned-no-physical-free");
            }
            expected = expected with { OccupiedSlotCount = 0, AvailableSlotCount = 2, UnusedSlotBytes = 32 };
            Verify(pool.CapturePreparedSnapshot(), expected, "pool-page-idle");
            if (budget.CaptureStatistics().CommittedBytes != 32) throw new InvalidOperationException("Idle backing was prematurely uncharged.");
            if (pool.TrimRetainedMemory() != 32) throw new InvalidOperationException("Idle page trim extent differs.");
            expected = expected with { RetainedPageCount = 0, RetainedSlotCount = 0, AvailableSlotCount = 0, RetainedBytes = 0, UnusedSlotBytes = 0 };
            Verify(pool.CapturePreparedSnapshot(), expected, "pool-page-physically-trimmed");
            if (pool.TryRent(1, static writer => writer.Fill(0), out PreparedPooled<int> regrown, out _))
            {
                regrown.Dispose();
                throw new InvalidOperationException("Trimmed prepared pool regrew backing.");
            }
            expected = expected with { RejectedFullCount = 2 };
            Verify(pool.CapturePreparedSnapshot(), expected, "pool-no-growth-after-trim");
        }
        finally { pool.Dispose(); }
        Verify(pool.CapturePreparedSnapshot(), expected with { Lifecycle = NativeOwnerLifecycle.Disposed }, "pool-disposed-numeric-history");
        if (budget.CaptureStatistics().CommittedBytes != 0 || budget.CaptureStatistics().AllocationCount != 1 || budget.CaptureStatistics().FreeCount != 1)
            throw new InvalidOperationException("Pool backing acquisition/return does not reconcile.");
    }

    internal static void RunArena(int traceCapacity)
    {
        VerifyIncompleteArena(traceCapacity);
        long ordinaryExtent = ArenaExtent(16);
        long scopedExtent = ArenaExtent(8);
        NativeMemoryBudget budget = new(ordinaryExtent + scopedExtent, traceCapacity);
        NativeArena arena = new(new NativeArenaPreparation(16, 8), budget);
        Arena expected = new()
        {
            OwnerId = arena.Id,
            Lifecycle = NativeOwnerLifecycle.Active,
            Preparation = new(16, 8),
            OrdinaryAvailableBytes = 16,
            ScopedAvailableBytes = 8,
            RetainedBytes = ordinaryExtent + scopedExtent,
            PeakRetainedBytes = ordinaryExtent + scopedExtent
        };
        try
        {
            Verify(arena.CapturePreparedSnapshot(), expected, "arena-prepared");
            {
                if (!arena.TryScratch<int>(4, static writer => writer.Fill(7), out ArenaLease<int> ordinary))
                    throw new InvalidOperationException("Declared ordinary capacity was refused.");
                expected = expected with { OrdinaryUsedBytes = 16, OrdinaryAvailableBytes = 0, PeakOrdinaryUsedBytes = 16, SuccessfulScratchCount = 1 };
                Verify(arena.CapturePreparedSnapshot(), expected, "arena-ordinary-full");
                try
                {
                    if (!arena.TryScratchScoped<long>(1, static writer => writer.Write(11), out ArenaLease<long> scoped))
                        throw new InvalidOperationException("Declared scoped capacity was refused.");
                    expected = expected with { ScopedUsedBytes = 8, ScopedAvailableBytes = 0, PeakScopedUsedBytes = 8, SuccessfulScratchCount = 2 };
                    Verify(arena.CapturePreparedSnapshot(), expected, "arena-both-lanes-full");
                    if (arena.TryScratch<byte>(1, static writer => writer.Write(0), out ArenaLease<byte> unexpectedOrdinary))
                    {
                        unexpectedOrdinary.Clear();
                        throw new InvalidOperationException("Ordinary lane grew beyond preparation.");
                    }
                    expected = expected with { RejectedCapacityCount = 1 };
                    Verify(arena.CapturePreparedSnapshot(), expected, "arena-ordinary-refused");
                    if (arena.TryScratchScoped<byte>(1, static writer => writer.Write(0), out ArenaLease<byte> unexpectedScoped))
                    {
                        unexpectedScoped.Clear();
                        throw new InvalidOperationException("Scoped lane grew beyond preparation.");
                    }
                    expected = expected with { RejectedCapacityCount = 2 };
                    Verify(arena.CapturePreparedSnapshot(), expected, "arena-scoped-refused");
                    if (ordinary.Read(static view => view[3]) != 7 || scoped.Read(static view => view[0]) != 11)
                        throw new InvalidOperationException("Prepared arena output differs.");
                }
                finally { arena.RecycleScoped(); }
                expected = expected with { ScopedUsedBytes = 0, ScopedAvailableBytes = 8 };
                Verify(arena.CapturePreparedSnapshot(), expected, "arena-scoped-recycled");
                if (ordinary.Read(static view => view[0]) != 7) throw new InvalidOperationException("Scoped recycle changed ordinary output.");
            }
            arena.Reset();
            expected = expected with { OrdinaryUsedBytes = 0, OrdinaryAvailableBytes = 16 };
            Verify(arena.CapturePreparedSnapshot(), expected, "arena-reset-peaks-preserved");
            if (arena.TrimRetainedMemory() != (nuint)(ordinaryExtent + scopedExtent)) throw new InvalidOperationException("Prepared arena trim extent differs.");
            expected = expected with { OrdinaryAvailableBytes = 0, ScopedAvailableBytes = 0, RetainedBytes = 0 };
            Verify(arena.CapturePreparedSnapshot(), expected, "arena-physically-trimmed");
            if (arena.TryScratch<int>(1, static writer => writer.Write(0), out ArenaLease<int> regrown))
            {
                regrown.Clear();
                throw new InvalidOperationException("Prepared arena regrew trimmed storage.");
            }
            expected = expected with { RejectedCapacityCount = 3 };
            Verify(arena.CapturePreparedSnapshot(), expected, "arena-no-growth-after-trim");
        }
        finally { arena.Dispose(); }
        Verify(arena.CapturePreparedSnapshot(), expected with { Lifecycle = NativeOwnerLifecycle.Disposed }, "arena-disposed");
        if (budget.CaptureStatistics().CommittedBytes != 0 || budget.CaptureStatistics().AllocationCount != 2 || budget.CaptureStatistics().FreeCount != 2)
            throw new InvalidOperationException("Prepared arena backing does not reconcile.");
    }

    internal static void RunRetention(int traceCapacity)
    {
        long normalExtent = ArenaExtent(64);
        long outlierExtent = ArenaExtent(128);
        NativeMemoryBudget budget = new(normalExtent + outlierExtent, traceCapacity);
        NativeArena arena = new(budget, new NativeArenaRetentionPolicy(64, (nuint)normalExtent), 64, NativeMemoryReturn.ToNativeMemory);
        Retention expected = new()
        {
            OwnerId = arena.Id,
            Lifecycle = NativeOwnerLifecycle.Active,
            Enabled = true,
            Policy = new(64, (nuint)normalExtent),
            RetainedBytes = normalExtent,
            IdleBytes = normalExtent
        };
        try
        {
            Verify(arena.CaptureRetentionSnapshot(), expected, "retention-idle-normal");
            {
                ArenaLease<byte> normal = arena.Scratch<byte>(64, static writer => writer.Fill(7));
                expected = expected with { IdleBytes = 0 };
                Verify(arena.CaptureRetentionSnapshot(), expected, "retention-normal-live");
                ArenaLease<byte> outlier = arena.Scratch<byte>(128, static writer => writer.Fill(11));
                expected = expected with { RetainedBytes = normalExtent + outlierExtent, OversizedBytes = outlierExtent, PeakOversizedBytes = outlierExtent };
                Verify(arena.CaptureRetentionSnapshot(), expected, "retention-outlier-live");
                if (arena.MaintainRetention() != 0) throw new InvalidOperationException("Maintenance freed occupied backing.");
                expected = expected with { MaintenanceCount = 1 };
                Verify(arena.CaptureRetentionSnapshot(), expected, "retention-live-maintenance");
                if (normal.Read(static view => view[63]) != 7 || outlier.Read(static view => view[127]) != 11)
                    throw new InvalidOperationException("Retention maintenance changed initialized output.");
            }
            arena.Reset();
            expected = expected with { RetainedBytes = normalExtent, IdleBytes = normalExtent, OversizedBytes = 0, MaintenanceCount = 2, ReleasedBytes = outlierExtent };
            Verify(arena.CaptureRetentionSnapshot(), expected, "retention-reset-isolates-outlier");
            if (budget.CaptureStatistics().CommittedBytes != normalExtent) throw new InvalidOperationException("Released outlier remains charged.");
            {
                ArenaLease<int> next = arena.Scratch<int>(1, static writer => writer.Write(29));
                expected = expected with { IdleBytes = 0 };
                Verify(arena.CaptureRetentionSnapshot(), expected, "retention-normal-reuse-no-growth");
                if (next.Read(static view => view[0]) != 29 || budget.CaptureStatistics().AllocationCount != 2)
                    throw new InvalidOperationException("Normal reuse grew after the outlier.");
            }
        }
        finally { arena.Dispose(); }
        Verify(arena.CaptureRetentionSnapshot(), expected with
        { Lifecycle = NativeOwnerLifecycle.Disposed, RetainedBytes = 0 }, "retention-disposal-is-not-policy-release");
        if (budget.CaptureStatistics().CommittedBytes != 0 || budget.CaptureStatistics().FreeCount != 2)
            throw new InvalidOperationException("Retention terminal backing does not reconcile.");
    }

    internal static void Verify<TActual, TExpected>(TActual actual, TExpected expected, string stage) =>
        NativeAdmissionDiagnosticOracle.Verify(actual, expected, stage);

    private static void VerifyIncompleteArena(int traceCapacity)
    {
        long extent = ArenaExtent(8);
        NativeMemoryBudget budget = new(extent, traceCapacity);
        using (NativeArena arena = new(new NativeArenaPreparation(8, 0), budget))
        {
            bool failed = false;
            try
            {
                ArenaLease<int> incomplete = arena.Scratch<int>(2, static writer => writer.Write(1));
                incomplete.Clear();
            }
            catch (InvalidOperationException) { failed = true; }
            if (!failed) throw new InvalidOperationException("Incomplete arena payload was published.");
            Verify(arena.CapturePreparedSnapshot(), new Arena
            {
                OwnerId = arena.Id,
                Lifecycle = NativeOwnerLifecycle.Active,
                Preparation = new(8, 0),
                OrdinaryAvailableBytes = 8,
                PeakOrdinaryUsedBytes = 8,
                RetainedBytes = extent,
                PeakRetainedBytes = extent,
                InitializerFailureCount = 1
            }, "arena-incomplete-rollback-retains-reserved-peak");
        }
        if (budget.CaptureStatistics().CommittedBytes != 0 || budget.CaptureStatistics().FreeCount != 1)
            throw new InvalidOperationException("Failed initializer backing was not released.");
    }

    internal readonly record struct Pool
    {
        public long OwnerId { get; init; }
        public NativeOwnerLifecycle Lifecycle { get; init; }
        public NativePoolPreparation Preparation { get; init; }
        public int RetainedPageCount { get; init; }
        public int RetainedSlotCount { get; init; }
        public int OccupiedSlotCount { get; init; }
        public int PeakOccupiedSlotCount { get; init; }
        public int AvailableSlotCount { get; init; }
        public long RetainedBytes { get; init; }
        public long PeakRetainedBytes { get; init; }
        public long SuccessfulRentCount { get; init; }
        public long RejectedShapeCount { get; init; }
        public long RejectedFullCount { get; init; }
        public long InitializerFailureCount { get; init; }
        public long ManagedBankBytes { get; init; }
        public long UnusedSlotBytes { get; init; }
        public bool HistoryOverflowed { get; init; }
    }

    internal readonly record struct Arena
    {
        public long OwnerId { get; init; }
        public NativeOwnerLifecycle Lifecycle { get; init; }
        public NativeArenaPreparation Preparation { get; init; }
        public long OrdinaryUsedBytes { get; init; }
        public long ScopedUsedBytes { get; init; }
        public long OrdinaryAvailableBytes { get; init; }
        public long ScopedAvailableBytes { get; init; }
        public long PeakOrdinaryUsedBytes { get; init; }
        public long PeakScopedUsedBytes { get; init; }
        public long RetainedBytes { get; init; }
        public long ActiveBorrowedBytes { get; init; }
        public long RetainedBorrowedBytes { get; init; }
        public long PeakRetainedBytes { get; init; }
        public long SuccessfulScratchCount { get; init; }
        public long RejectedCapacityCount { get; init; }
        public long InitializerFailureCount { get; init; }
        public bool HistoryOverflowed { get; init; }
    }

    internal readonly record struct Retention
    {
        public long OwnerId { get; init; }
        public NativeOwnerLifecycle Lifecycle { get; init; }
        public bool Enabled { get; init; }
        public NativeArenaRetentionPolicy Policy { get; init; }
        public long RetainedBytes { get; init; }
        public long IdleBytes { get; init; }
        public long OversizedBytes { get; init; }
        public long PeakOversizedBytes { get; init; }
        public long MaintenanceCount { get; init; }
        public long ReleasedBytes { get; init; }
        public bool HistoryOverflowed { get; init; }
    }
}
