using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedPoolTests
{
    [Fact]
    public void SixtyFourIndependentFourKiBSlotsUseFourPagesWithoutMetadataGrowth()
    {
        NativeMemoryBudget budget = new(64 * 4096);
        using NativePool<byte> pool = new(new NativePoolPreparation(64, 4096, 16), budget);
        NativePreparedPoolStatistics prepared = pool.CapturePreparedSnapshot();
        Assert.Equal(4, prepared.RetainedPageCount);
        Assert.Equal(64, prepared.AvailableSlotCount);
        Assert.Equal(64 * 4096, prepared.RetainedBytes);
        Assert.Equal(4, budget.CaptureStatistics().AllocationCount);
        Assert.True(prepared.ManagedBankBytes > 0);
        var banks = pool.CurrentBankCapacitiesForTest;
        Assert.Equal(64, banks.Slabs);
        Assert.Equal(4, banks.OwnerSegments);
        WithSlots(pool, 64, 4096);
        NativePreparedPoolStatistics returned = pool.CapturePreparedSnapshot();
        Assert.Equal(64, returned.PeakOccupiedSlotCount);
        Assert.Equal(0, returned.OccupiedSlotCount);
        Assert.Equal(64, returned.AvailableSlotCount);
        Assert.Equal(64, returned.SuccessfulRentCount);
        Assert.Equal(64 * 4096, returned.UnusedSlotBytes);
        Assert.Equal(prepared.ManagedBankBytes, returned.ManagedBankBytes);
        Assert.Equal(banks, pool.CurrentBankCapacitiesForTest);
        Assert.Equal(4, pool.GetStatistics().FreshSegmentAllocationCount);
        Assert.Equal([1L, 2L, 3L, 4L], pool.CurrentSegmentOrdinalsForTest);
    }

    private static void WithSlots(NativePool<byte> pool, int remaining, int length)
    {
        using Pooled<byte> lease = pool.Rent(length, static writer => writer.Fill(42));
        Assert.Equal(length, lease.Capacity);
        if (remaining > 1)
        {
            WithSlots(pool, remaining - 1, length);
        }
        else
        {
            Assert.Equal(64, pool.CapturePreparedSnapshot().OccupiedSlotCount);
            Assert.Equal(0, pool.CapturePreparedSnapshot().AvailableSlotCount);
            Assert.Equal(4, pool.GetStatistics().SegmentCount);
            Assert.Equal(0, pool.GetStatistics().AvailableSegmentCount);
        }
        Assert.Equal(42, lease.Read(static view => view[0]));
    }

    [Fact]
    public void ExhaustionDoesNotCallTheProducerOrChangePublishedStorage()
    {
        NativeMemoryBudget budget = new(64);
        using NativePool<int> pool = new(new NativePoolPreparation(1, 4, 1), budget);
        using Pooled<int> existing = pool.Rent(4, static writer => writer.Fill(7));
        int calls = 0;
        Assert.False(pool.TryRent(5, writer => { calls++; writer.Fill(0); }, out _, out NativePoolExhaustionReason shape));
        Assert.Equal(NativePoolExhaustionReason.ShapeExceeded, shape);
        Assert.False(pool.TryRent(1, writer => { calls++; writer.Fill(0); }, out _, out NativePoolExhaustionReason full));
        Assert.Equal(NativePoolExhaustionReason.NoAvailableSlot, full);
        Assert.Equal(0, calls);
        Assert.Equal(7, existing.Read(static view => view[0]));
        NativePreparedPoolStatistics snapshot = pool.CapturePreparedSnapshot();
        Assert.Equal(1, snapshot.RejectedShapeCount);
        Assert.Equal(1, snapshot.RejectedFullCount);
        Assert.Equal(1, snapshot.SuccessfulRentCount);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
    }

    [Fact]
    public void SuccessfulTryRentAndExpectedExhaustionHaveNoManagedOrBackingAllocation()
    {
        NativeMemoryBudget budget = new(64);
        using NativePool<int> pool = new(new NativePoolPreparation(1, 4, 1), budget);
        NativeLeaseInitializer<int> initialize = static writer => writer.Fill(42);
        _ = pool.CapturePreparedSnapshot();
        if (pool.TryRent(4, initialize, out Pooled<int> warm, out _))
        {
            warm.Dispose();
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        int successes = 0;
        int refusals = 0;
        for (int iteration = 0; iteration < 1024; iteration++)
        {
            if (pool.TryRent(4, initialize, out Pooled<int> lease, out _))
            {
                successes++;
                if (!pool.TryRent(4, initialize, out _, out NativePoolExhaustionReason reason)
                    && reason == NativePoolExhaustionReason.NoAvailableSlot)
                {
                    refusals++;
                }
                lease.Dispose();
            }
            _ = pool.CapturePreparedSnapshot();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(1024, successes);
        Assert.Equal(1024, refusals);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().ReallocationCount);
        Assert.Equal(1, pool.CapturePreparedSnapshot().PeakOccupiedSlotCount);
    }

    [Fact]
    public void IncompleteAndThrowingInitializationReturnTheSlotWithoutPublication()
    {
        using NativePool<int> pool = new(new NativePoolPreparation(2, 4, 2), budget: null);
        Assert.Throws<InvalidOperationException>(() => pool.TryRent(4, static writer => writer.Write(1), out _, out _));
        Assert.Throws<OperationCanceledException>(() => pool.TryRent(4,
            static writer => { writer.Write(1); throw new OperationCanceledException(); }, out _, out _));
        NativePreparedPoolStatistics snapshot = pool.CapturePreparedSnapshot();
        Assert.Equal(2, snapshot.InitializerFailureCount);
        Assert.Equal(0, snapshot.SuccessfulRentCount);
        Assert.Equal(1, snapshot.PeakOccupiedSlotCount);
        Assert.Equal(0, snapshot.OccupiedSlotCount);
        Assert.Equal(2, snapshot.AvailableSlotCount);
        if (pool.TryRent(4, static writer => writer.Fill(7), out Pooled<int> lease, out _))
        {
            Assert.Equal(7, lease.Read(static view => view[0]));
            lease.Dispose();
        }
        else
        {
            Assert.Fail("Failed initialization must return its retained slot.");
        }
    }

    [Fact]
    public void SparseTrimReleasesOnlyWholeIdlePagesAndNeverImplicitlyRefills()
    {
        NativeMemoryBudget budget = new(256);
        using NativePool<int> pool = new(new NativePoolPreparation(4, 1, 2), budget);
        Pooled<int> survivor = pool.Rent(1, static writer => writer.Write(42));
        try
        {
            survivor.Access(_ => Assert.Equal((nuint)8, pool.TrimRetainedMemory()));
            NativePreparedPoolStatistics sparse = pool.CapturePreparedSnapshot();
            Assert.Equal(1, sparse.RetainedPageCount);
            Assert.Equal(2, sparse.RetainedSlotCount);
            Assert.Equal(1, sparse.AvailableSlotCount);
            Assert.Equal(8, sparse.RetainedBytes);
            Assert.Equal(16, sparse.PeakRetainedBytes);
            Assert.Equal(4, sparse.UnusedSlotBytes);
            Assert.Equal(42, survivor.Read(static view => view[0]));
            Assert.Equal(8, budget.CaptureStatistics().CommittedBytes);
        }
        finally
        {
            survivor.Dispose();
        }
        Assert.Equal((nuint)8, pool.TrimRetainedMemory());
        Assert.False(pool.TryRent(1, static writer => writer.Write(1), out _, out NativePoolExhaustionReason full));
        Assert.Equal(NativePoolExhaustionReason.NoAvailableSlot, full);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(2, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(2, budget.CaptureStatistics().FreeCount);
        Assert.Equal(0, pool.CapturePreparedSnapshot().RetainedSlotCount);
        Assert.Empty(pool.CurrentSegmentOrdinalsForTest);
    }

    [Fact]
    public void LastPageAndEveryPackedSlotAreChargedAtTheirExactElementExtent()
    {
        NativeMemoryBudget budget = new(15);
        using NativePool<byte> pool = new(new NativePoolPreparation(5, 3, 2), budget);
        NativePreparedPoolStatistics snapshot = pool.CapturePreparedSnapshot();
        Assert.Equal(3, snapshot.RetainedPageCount);
        Assert.Equal(5, snapshot.RetainedSlotCount);
        Assert.Equal(15, snapshot.RetainedBytes);
        Assert.Equal(15, snapshot.UnusedSlotBytes);
        Assert.Equal(15, pool.GetStatistics().UsableCapacityBytes);
        Assert.Equal(3, pool.GetStatistics().FreshSegmentAllocationCount);
        Assert.Equal(15, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void PreparationRefusesTheCompleteExtentBeforeAnyBackendAcquisition()
    {
        NativeMemoryBudget budget = new(15);
        Assert.Throws<NativeMemoryBudgetExceededException>(() => new NativePool<int>(new NativePoolPreparation(4, 1, 2), budget));
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(1, budget.CaptureStatistics().RejectedAllocationCount);
    }

    [Fact]
    public void FailedPagePreparationRollsBackAllAdmission()
    {
        NativeMemoryBudget budget = new(256);
        NativeMemoryTestHooks.FailNextAllocation();
        Assert.Throws<NativeAllocationFailedException>(() => new NativePool<int>(new NativePoolPreparation(4, 1, 2), budget));
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(0, snapshot.CommittedBytes);
        Assert.Equal(0, snapshot.ReservedBytes);
        Assert.Equal(0, snapshot.ActiveAllocationCount);
        Assert.Equal(1, snapshot.FailedAllocationCount);
    }

    [Fact]
    public void TerminalCaptureKeepsHistoryAndClearsActualCapacity()
    {
        NativeMemoryBudget budget = new(64);
        using NativePool<int> pool = new(new NativePoolPreparation(1, 4, 1), budget);
        using (Pooled<int> lease = pool.Rent(4, static writer => writer.Fill(1)))
        {
        }
        pool.Dispose();
        NativePreparedPoolStatistics snapshot = pool.CapturePreparedSnapshot();
        Assert.Equal(pool.Id, snapshot.OwnerId);
        Assert.Equal(NativeOwnerLifecycle.Disposed, snapshot.Lifecycle);
        Assert.Equal(0, snapshot.RetainedPageCount);
        Assert.Equal(0, snapshot.AvailableSlotCount);
        Assert.Equal(1, snapshot.SuccessfulRentCount);
        Assert.Equal(1, snapshot.PeakOccupiedSlotCount);
        Assert.Equal(16, snapshot.PeakRetainedBytes);
        Assert.Equal(0, snapshot.RetainedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void HistoryOverflowCannotBreakSlotPublicationOrExpectedExhaustion()
    {
        using NativePool<int> pool = new(new NativePoolPreparation(1, 1, 1), budget: null);
        object kernel = typeof(NativePool<int>).GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
        kernel.GetType().GetField("_successfulPreparedRents", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(kernel, long.MaxValue);
        using Pooled<int> lease = pool.Rent(1, static writer => writer.Write(42));
        Assert.Equal(42, lease.Read(static view => view[0]));
        Assert.Equal(long.MaxValue, pool.CapturePreparedSnapshot().SuccessfulRentCount);
        Assert.True(pool.CapturePreparedSnapshot().HistoryOverflowed);
    }

    [Fact]
    public void OrdinaryPoolOrdinalProbeReadsActualAcquisitionIdentities()
    {
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Assert.Empty(pool.CurrentSegmentOrdinalsForTest);
        using (Pooled<int> first = pool.Rent(1, static writer => writer.Write(1)))
        {
            Assert.Equal([1L], pool.CurrentSegmentOrdinalsForTest);
        }
        _ = pool.TrimRetainedMemory();
        Assert.Empty(pool.CurrentSegmentOrdinalsForTest);
        using Pooled<int> second = pool.Rent(1, static writer => writer.Write(2));
        Assert.Equal([2L], pool.CurrentSegmentOrdinalsForTest);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    public void PreparationPublicationFailureReleasesCommittedPagesAndRemainingReservation(int boundary, int acquisitions)
    {
        NativeMemoryBudget budget = new(256);
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(boundary);
        Assert.Throws<InvalidOperationException>(() => new NativePool<int>(new NativePoolPreparation(4, 1, 2), budget));
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(acquisitions, snapshot.AllocationCount);
        Assert.Equal(acquisitions, snapshot.FreeCount);
        Assert.Equal(0, snapshot.CommittedBytes);
        Assert.Equal(0, snapshot.ReservedBytes);
        Assert.Equal(0, snapshot.ActiveAllocationCount);
    }

    [Fact]
    public void StaleSlotAliasCannotBorrowOrReturnTheReusedSlot()
    {
        using NativePool<int> pool = new(new NativePoolPreparation(1, 1, 1), budget: null);
        Pooled<int> first = pool.Rent(1, static writer => writer.Write(1));
        Pooled<int> stale = first;
        first.Dispose();
        using Pooled<int> current = pool.Rent(1, static writer => writer.Write(42));
        bool borrowRejected = false;
        bool returnRejected = false;
        try { _ = stale.Read(static view => view[0]); }
        catch (NativeAllocationReturnedException) { borrowRejected = true; }
        try { stale.Dispose(); }
        catch (NativeAllocationReturnedException) { returnRejected = true; }
        Assert.True(borrowRejected);
        Assert.True(returnRejected);
        Assert.Equal(1, pool.CapturePreparedSnapshot().OccupiedSlotCount);
        Assert.Equal(42, current.Read(static view => view[0]));
    }

    [Fact]
    public void PageTracingIsBoundedAndCorrelatesPreparationAcquisitionTrimAndRelease()
    {
        NativeMemoryBudget budget = new(256, traceCapacity: 8);
        using NativePool<int> pool = new(new NativePoolPreparation(4, 1, 2), budget);
        Assert.Equal((nuint)8, pool.TrimRetainedMemoryByBytes(1));
        pool.Dispose();
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[8];
        Assert.Equal(6, budget.CopyTraceTo(events));
        Assert.Equal(NativeMemoryTraceKind.Admitted, events[0].Kind);
        Assert.Equal(NativeMemoryTraceKind.PageAcquired, events[1].Kind);
        Assert.Equal(1, events[1].AllocationOrdinal);
        Assert.Equal(NativeMemoryTraceKind.PageAcquired, events[2].Kind);
        Assert.Equal(2, events[2].AllocationOrdinal);
        Assert.Equal(NativeMemoryTraceKind.Prepared, events[3].Kind);
        Assert.Null(events[3].AllocationOrdinal);
        Assert.Equal(16, events[3].CommittedBytes);
        Assert.Equal(NativeMemoryTraceKind.Trimmed, events[4].Kind);
        Assert.Equal(1, events[4].AllocationOrdinal);
        Assert.Equal(NativeMemoryTraceKind.Released, events[5].Kind);
        Assert.Equal(2, events[5].AllocationOrdinal);
        Assert.Equal(0, events[5].CommittedBytes);
        foreach (ref readonly NativeMemoryTraceEvent entry in events[..6])
        {
            Assert.Equal(pool.Id, entry.OwnerId);
        }
        Assert.Equal(0, budget.CaptureStatistics().DroppedTraceEventCount);
    }

    [Fact]
    public void PreparedReuseWithEnabledBackingTraceEmitsNoRedundantBudgetEvents()
    {
        NativeMemoryBudget budget = new(64, traceCapacity: 1);
        using NativePool<int> pool = new(new NativePoolPreparation(1, 1, 1), budget);
        Assert.Equal(2, budget.CaptureStatistics().DroppedTraceEventCount);
        NativeMemoryBudgetStatistics before = budget.CaptureStatistics();
        NativeLeaseInitializer<int> initialize = static writer => writer.Write(42);
        using (Pooled<int> warm = pool.Rent(1, initialize)) { }
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1024; iteration++)
        {
            if (pool.TryRent(1, initialize, out Pooled<int> lease, out _)) lease.Dispose();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
        Assert.Equal(before, budget.CaptureStatistics());
    }

    [Fact]
    public void KeepingTheDomainAndTraceAliveDoesNotRetainAbandonedPageBacking()
    {
        NativeMemoryBudget budget = new(128, traceCapacity: 8);
        WeakReference pool = CreateAbandonedPreparedPool(budget);
        for (int attempt = 0; attempt < 8 && budget.CaptureStatistics().CommittedBytes != 0; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(pool.IsAlive);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
        GC.KeepAlive(budget);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "This fixture deliberately abandons the prepared owner to verify emergency page cleanup and prove a live budget/trace does not retain it.")]
    private static WeakReference CreateAbandonedPreparedPool(NativeMemoryBudget budget)
    {
        NativePool<int> pool = new(new NativePoolPreparation(2, 1, 2), budget);
        return new WeakReference(pool);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessCannotExposeUninitializedCapacityBeyondThePublishedLogicalLength(bool prepared)
    {
        using NativePool<int> pool = prepared
            ? new(new NativePoolPreparation(1, 4, 1), budget: null)
            : new(preLease: 4, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using Pooled<int> lease = pool.Rent(1, static writer => writer.Write(42));
        int calls = 0;
        bool rejected = false;
        try
        {
            _ = lease.Process(2, 0, (values, _) => { calls++; return values.Length; });
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }
        Assert.True(rejected);
        Assert.Equal(0, calls);
        Assert.Equal(4, lease.Capacity);
        Assert.Equal(1, lease.Length);
        Assert.Equal(42, lease.Read(static view => view[0]));
    }
}
