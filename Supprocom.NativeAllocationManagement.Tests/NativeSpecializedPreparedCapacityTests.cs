using Supprocom.NativeAllocationManagement.Conformance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeSpecializedPreparedCapacityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void PublicPreparedTransitionsMatchEveryField(int traceCapacity) => RunPool(traceCapacity);

    private static void RunPool(int traceCapacity)
    {
        NativeMemoryBudget budget = new(128, traceCapacity);
        NativePreparedPool<int> pool = new(new NativePoolPreparation(2, 4, 2), budget);
        NativeCapacityDiagnosticOracle.Pool expected = new()
        {
            OwnerId = pool.Id,
            Lifecycle = NativeOwnerLifecycle.Active,
            Preparation = new(2, 4, 2),
            RetainedPageCount = 1,
            RetainedSlotCount = 2,
            AvailableSlotCount = 2,
            RetainedBytes = 32,
            PeakRetainedBytes = 32,
            ManagedBankBytes = 2 * 24 + 40,
            UnusedSlotBytes = 32
        };
        try
        {
            NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected, "pool-prepared");
            bool failed = false;
            try { using PreparedPooled<int> incomplete = pool.Rent(1, static _ => { }); }
            catch (InvalidOperationException) { failed = true; }
            if (!failed) throw new InvalidOperationException("Incomplete initialization was published.");
            expected = expected with { PeakOccupiedSlotCount = 1, InitializerFailureCount = 1 };
            NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected, "pool-incomplete-rolled-back");
            using (PreparedPooled<int> first = pool.Rent(4, static writer => writer.Fill(7)))
            {
                expected = expected with { OccupiedSlotCount = 1, AvailableSlotCount = 1, SuccessfulRentCount = 1, UnusedSlotBytes = 16 };
                NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected, "pool-sparse-page-pinned");
                if (pool.TrimRetainedMemory() != 0) throw new InvalidOperationException("Live slot's page was freed.");
                NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected, "pool-sparse-trim-cannot-free-live-page");
                using (PreparedPooled<int> second = pool.Rent(2, static writer => writer.Fill(11)))
                {
                    expected = expected with
                    { OccupiedSlotCount = 2, PeakOccupiedSlotCount = 2, AvailableSlotCount = 0, SuccessfulRentCount = 2, UnusedSlotBytes = 0 };
                    NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected, "pool-dense");
                    if (pool.TryRent(5, static writer => writer.Fill(0), out PreparedPooled<int> oversized, out NativePoolExhaustionReason shape))
                    {
                        oversized.Dispose();
                        throw new InvalidOperationException("Oversized shape was accepted.");
                    }
                    if (shape != NativePoolExhaustionReason.ShapeExceeded) throw new InvalidOperationException("Shape refusal was misclassified.");
                    expected = expected with { RejectedShapeCount = 1 };
                    NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected, "pool-shape-refused");
                    if (pool.TryRent(1, static writer => writer.Fill(0), out PreparedPooled<int> full, out NativePoolExhaustionReason reason))
                    {
                        full.Dispose();
                        throw new InvalidOperationException("Declared slot capacity was exceeded.");
                    }
                    if (reason != NativePoolExhaustionReason.NoAvailableSlot) throw new InvalidOperationException("Full refusal was misclassified.");
                    expected = expected with { RejectedFullCount = 1 };
                    NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected, "pool-full-refused");
                    if (first.Read(static view => view[3]) != 7 || second.Read(static view => view[1]) != 11)
                        throw new InvalidOperationException("Capacity refusal changed published output.");
                }
                expected = expected with { OccupiedSlotCount = 1, AvailableSlotCount = 1, UnusedSlotBytes = 16 };
                NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected, "pool-second-returned-no-physical-free");
            }
            expected = expected with { OccupiedSlotCount = 0, AvailableSlotCount = 2, UnusedSlotBytes = 32 };
            NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected, "pool-page-idle");
            if (budget.CaptureStatistics().CommittedBytes != 32) throw new InvalidOperationException("Idle backing was prematurely uncharged.");
            if (pool.TrimRetainedMemory() != 32) throw new InvalidOperationException("Idle page trim extent differs.");
            expected = expected with { RetainedPageCount = 0, RetainedSlotCount = 0, AvailableSlotCount = 0, RetainedBytes = 0, UnusedSlotBytes = 0 };
            NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected, "pool-page-physically-trimmed");
            if (pool.TryRent(1, static writer => writer.Fill(0), out PreparedPooled<int> regrown, out _))
            {
                regrown.Dispose();
                throw new InvalidOperationException("Trimmed prepared pool regrew backing.");
            }
            expected = expected with { RejectedFullCount = 2 };
            NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected, "pool-no-growth-after-trim");
        }
        finally { pool.Dispose(); }
        NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected with { Lifecycle = NativeOwnerLifecycle.Disposed }, "pool-disposed-numeric-history");
        if (budget.CaptureStatistics().CommittedBytes != 0 || budget.CaptureStatistics().AllocationCount != 1 || budget.CaptureStatistics().FreeCount != 1)
            throw new InvalidOperationException("Pool backing acquisition/return does not reconcile.");
    }
}
