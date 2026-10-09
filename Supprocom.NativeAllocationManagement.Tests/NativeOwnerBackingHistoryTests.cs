using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeOwnerBackingHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FastTerminalGcOwnersKeepActualDetachedExtentUntilFinalization(bool arenaKind)
    {
        NativeMemoryBudget budget = new(512);
        if (arenaKind)
        {
            using NativeArena arena = new(budget, 64, NativeMemoryReturn.ToGarbageCollector);
            long extent = budget.CaptureStatistics().CommittedBytes;
            Assert.True(extent >= 64);
            AssertBacking(arena.CaptureDiagnosticSnapshot(), extent, 0, extent);
            arena.Dispose();
            AssertBacking(arena.CaptureDiagnosticSnapshot(), extent, extent, extent);
            arena.Dispose();
            AssertBacking(arena.CaptureDiagnosticSnapshot(), extent, extent, extent);
            Assert.Equal(extent, budget.CaptureStatistics().CommittedBytes);
            GC.KeepAlive(arena);
        }
        else
        {
            using NativePool<byte> pool = new(budget, 0, 64, NativeMemoryReturn.ToGarbageCollector);
            AssertBacking(pool.CaptureDiagnosticSnapshot(), 64, 0, 64);
            pool.Dispose();
            AssertBacking(pool.CaptureDiagnosticSnapshot(), 64, 64, 64);
            pool.Dispose();
            AssertBacking(pool.CaptureDiagnosticSnapshot(), 64, 64, 64);
            Assert.Equal(64, budget.CaptureStatistics().CommittedBytes);
            GC.KeepAlive(pool);
        }
    }

    [Fact]
    public void PreparedPageAndArenaPeaksAreTheSameActualPhysicalHistory()
    {
        NativeMemoryBudget budget = new(512);
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(4, 4, 2), budget);
        long poolExtent = pool.CapturePreparedSnapshot().RetainedBytes;
        Assert.Equal(poolExtent, pool.CapturePreparedSnapshot().PeakRetainedBytes);
        AssertBacking(pool.CaptureDiagnosticSnapshot(), poolExtent, 0, poolExtent);
        using NativeArena arena = new(new NativeArenaPreparation(16, 16), budget);
        long arenaExtent = arena.CapturePreparedSnapshot().RetainedBytes;
        Assert.Equal(arenaExtent, arena.CapturePreparedSnapshot().PeakRetainedBytes);
        AssertBacking(arena.CaptureDiagnosticSnapshot(), arenaExtent, 0, arenaExtent);
        Assert.True(poolExtent > 0);
        Assert.True(arenaExtent > 0);
        pool.Dispose();
        arena.Dispose();
        AssertBacking(pool.CaptureDiagnosticSnapshot(), 0, 0, poolExtent);
        AssertBacking(arena.CaptureDiagnosticSnapshot(), 0, 0, arenaExtent);
        Assert.Equal(poolExtent, pool.CapturePreparedSnapshot().PeakRetainedBytes);
        Assert.Equal(arenaExtent, arena.CapturePreparedSnapshot().PeakRetainedBytes);
    }

    [Fact]
    public void ProcessMeasurementResetDoesNotReconstructOrResetOwnerLifetimePeaks()
    {
        NativeMemoryBudget budget = new(512);
        using NativeConcurrentPool<byte> pool = new(budget, 0, 64, NativeMemoryReturn.ToNativeMemory, false);
        using NativeRegion region = new(budget, 64, NativeMemoryReturn.ToNativeMemory);
        long regionExtent = region.GetStatistics().RetainedBytes;
        try
        {
            NativeMemoryTestHooks.Reset();
            AssertBacking(pool.CaptureDiagnosticSnapshot(), 64, 0, 64);
            AssertBacking(region.CaptureDiagnosticSnapshot(), regionExtent, 0, regionExtent);
            pool.Dispose();
            region.Dispose();
            AssertBacking(pool.CaptureDiagnosticSnapshot(), 0, 0, 64);
            AssertBacking(region.CaptureDiagnosticSnapshot(), 0, 0, regionExtent);
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FastArenaAndRegionBackendFailureLeavesRealZeroBeforeSuccessfulRetry(bool arenaKind)
    {
        NativeMemoryBudget budget = new(512);
        try
        {
            NativeMemoryTestHooks.FailNextAllocation();
            if (arenaKind)
            {
                using NativeArena arena = new(budget, 0, NativeMemoryReturn.ToNativeMemory);
                Assert.Throws<NativeAllocationFailedException>(() => arena.Scratch<int>(1, static writer => writer.Write(7)));
                AssertBacking(arena.CaptureDiagnosticSnapshot(), 0, 0, 0);
                Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
                _ = arena.Scratch<int>(1, static writer => writer.Write(7));
                long extent = arena.GetStatistics().RetainedBytes;
                Assert.True(extent > 0);
                AssertBacking(arena.CaptureDiagnosticSnapshot(), extent, 0, extent);
            }
            else
            {
                NativeRegionKernel kernel = new(0, NativeMemoryReturn.ToNativeMemory, budget);
                try
                {
                    Assert.Throws<NativeAllocationFailedException>(() => kernel.LeaseInitialized<int>(1, static writer => writer.Write(7)));
                    AssertBacking(kernel.GetDiagnosticSnapshot(), 0, 0, 0);
                    Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
                    _ = kernel.LeaseInitialized<int>(1, static writer => writer.Write(7));
                    long extent = kernel.GetStatistics().RetainedBytes;
                    Assert.True(extent > 0);
                    AssertBacking(kernel.GetDiagnosticSnapshot(), extent, 0, extent);
                }
                finally { kernel.Dispose(); }
            }
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void FastPoolPeakSurvivesReturnTrimAndPhysicalDisposal()
    {
        NativeMemoryBudget budget = new(1_024);
        using NativePool<byte> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory);
        AssertBacking(pool.CaptureDiagnosticSnapshot(), 0, 0, 0);
        long first;
        long peak;
        using (Pooled<byte> lease = pool.Rent(65, static writer => writer.Fill(7)))
        {
            first = pool.GetStatistics().RetainedBytes;
            Assert.True(first >= 65);
            AssertBacking(pool.CaptureDiagnosticSnapshot(), first, 0, first);
            AssertMatches(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot());
            using Pooled<byte> second = pool.Rent(129, static writer => writer.Fill(3));
            peak = pool.GetStatistics().RetainedBytes;
            Assert.True(peak > first);
            AssertBacking(pool.CaptureDiagnosticSnapshot(), peak, 0, peak);
        }
        Assert.Equal(peak, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal((nuint)peak, pool.TrimRetainedMemory());
        AssertBacking(pool.CaptureDiagnosticSnapshot(), 0, 0, peak);
        using (Pooled<byte> reused = pool.Rent(1, static writer => writer.Write(1)))
        {
            Assert.True(pool.GetStatistics().RetainedBytes < peak);
            AssertBacking(pool.CaptureDiagnosticSnapshot(), pool.GetStatistics().RetainedBytes, 0, peak);
        }
        pool.Dispose();
        AssertBacking(pool.CaptureDiagnosticSnapshot(), 0, 0, peak);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void FastArenaPeakCoversBothLanesAndSurvivesResetAndTrim()
    {
        NativeMemoryBudget budget = new(32_768);
        using NativeArena arena = new(budget, 0, NativeMemoryReturn.ToNativeMemory);
        AssertBacking(arena.CaptureDiagnosticSnapshot(), 0, 0, 0);
        _ = arena.Scratch<int>(1, static writer => writer.Write(7));
        long first = arena.GetStatistics().RetainedBytes;
        _ = arena.ScratchScoped<long>(1, static writer => writer.Write(3));
        long retainedHeads = arena.GetStatistics().RetainedBytes;
        _ = arena.Scratch<byte>(4_096, static writer => writer.Fill(7));
        _ = arena.ScratchScoped<byte>(4_096, static writer => writer.Fill(3));
        long peak = arena.GetStatistics().RetainedBytes;
        Assert.True(peak > first);
        AssertBacking(arena.CaptureDiagnosticSnapshot(), peak, 0, peak);
        AssertMatches(arena.GetStatistics(), arena.CaptureDiagnosticSnapshot());
        arena.RecycleScoped();
        arena.Reset();
        Assert.Equal(0, arena.GetStatistics().RequestedBytes);
        AssertBacking(arena.CaptureDiagnosticSnapshot(), peak, 0, peak);
        Assert.Equal((nuint)(peak - retainedHeads), arena.TrimRetainedMemory());
        AssertBacking(arena.CaptureDiagnosticSnapshot(), retainedHeads, 0, peak);
        arena.Dispose();
        AssertBacking(arena.CaptureDiagnosticSnapshot(), 0, 0, peak);
    }

    [Theory]
    [InlineData(NativeMemoryReturn.ToNativeMemory)]
    [InlineData(NativeMemoryReturn.ToGarbageCollector)]
    public void RegionRecordsActualBackingDespiteFailedInitializer(NativeMemoryReturn policy)
    {
        NativeMemoryBudget budget = new(256);
        NativeRegionKernel kernel = new(0, policy, budget);
        try
        {
            Assert.Throws<InvalidOperationException>(() => kernel.LeaseInitialized<int>(1,
                static writer => { writer.Write(7); throw new InvalidOperationException("producer failed"); }));
            long extent = budget.CaptureStatistics().CommittedBytes;
            Assert.True(extent > 0);
            Assert.Equal(0, kernel.GetStatistics().RequestedBytes);
            AssertBacking(kernel.GetDiagnosticSnapshot(), extent, 0, extent);
            AssertMatches(kernel.GetStatistics(), kernel.GetDiagnosticSnapshot());
            kernel.Dispose();
            bool detached = policy == NativeMemoryReturn.ToGarbageCollector;
            AssertBacking(kernel.GetDiagnosticSnapshot(), detached ? extent : 0, detached ? extent : 0, extent);
        }
        finally { kernel.Dispose(); }
        GC.KeepAlive(kernel);
    }

    [Fact]
    public void SharedBudgetPeakIsNotCopiedIntoIndividualOwnerPeaks()
    {
        NativeMemoryBudget budget = new(512);
        using NativePool<byte> first = new(budget, 0, 64, NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentPool<byte> second = new(budget, 0, 128, NativeMemoryReturn.ToNativeMemory, false);
        AssertBacking(first.CaptureDiagnosticSnapshot(), 64, 0, 64);
        AssertBacking(second.CaptureDiagnosticSnapshot(), 128, 0, 128);
        Assert.Equal(192, budget.CaptureStatistics().PeakCommittedBytes);
        first.Dispose();
        AssertBacking(first.CaptureDiagnosticSnapshot(), 0, 0, 64);
        AssertBacking(second.CaptureDiagnosticSnapshot(), 128, 0, 128);
        AssertMatches(second.GetStatistics(), second.CaptureDiagnosticSnapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SynchronizedPeakSurvivesTrimAndGenerationChanges(bool arenaKind)
    {
        NativeMemoryBudget budget = new(512);
        NativeOwnerKernel kernel = arenaKind
            ? NativeOwnerKernel.CreateArena(64, "BackingTest", NativeMemoryReturn.ToNativeMemory, false, budget)
            : NativeOwnerKernel.CreatePool(0, 64, sizeof(byte), "BackingTest", NativeMemoryReturn.ToNativeMemory, false, false, budget);
        try
        {
            AssertBacking(kernel.GetDiagnosticSnapshot(), 64, 0, 64);
            AssertMatches(kernel.GetStatistics(), kernel.GetDiagnosticSnapshot());
            Assert.Equal((nuint)64, kernel.TrimRetainedMemory());
            AssertBacking(kernel.GetDiagnosticSnapshot(), 0, 0, 64);
            kernel.ReturnMemoryToNativeMemory();
            kernel.LeaseFromMemory();
            AssertBacking(kernel.GetDiagnosticSnapshot(), 64, 0, 64);
            kernel.Dispose();
            AssertBacking(kernel.GetDiagnosticSnapshot(), 0, 0, 64);
        }
        finally { kernel.Dispose(); }
    }

    [Fact]
    public void InternalSynchronizedRegionUsesTheSameRealBackingHistory()
    {
        NativeMemoryBudget budget = new(128);
        NativeOwnerKernel kernel = NativeOwnerKernel.CreateRegion(64, "RegionBackingTest",
            NativeMemoryReturn.ToNativeMemory, false, false, budget);
        try
        {
            Assert.Equal(NativeOwnerModel.SynchronizedRegion, kernel.GetStatistics().Model);
            AssertBacking(kernel.GetDiagnosticSnapshot(), 64, 0, 64);
            AssertMatches(kernel.GetStatistics(), kernel.GetDiagnosticSnapshot());
            kernel.Dispose();
            AssertBacking(kernel.GetDiagnosticSnapshot(), 0, 0, 64);
        }
        finally { kernel.Dispose(); }
    }

    [Fact]
    public void FailedBackendAndRejectedBudgetNeverBecomePhysicalOwnerPeaks()
    {
        NativeMemoryBudget refused = new(0);
        NativeMemoryBudget admitted = new(128);
        using NativePool<int> pool = new(refused, 0, 0, NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentPool<int> synchronized = new(admitted, 0, 0, NativeMemoryReturn.ToNativeMemory, false);
        try
        {
            Assert.Throws<NativeMemoryBudgetExceededException>(() =>
            {
                using Pooled<int> lease = pool.Rent(1, static writer => writer.Write(1));
            });
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() =>
            {
                using ConcurrentPooled<int> lease = synchronized.Rent(1, static writer => writer.Write(1));
            });
            AssertBacking(pool.CaptureDiagnosticSnapshot(), 0, 0, 0);
            AssertBacking(synchronized.CaptureDiagnosticSnapshot(), 0, 0, 0);
            Assert.Equal(0, admitted.CaptureStatistics().CommittedBytes);
            Assert.Equal(0, admitted.CaptureStatistics().ReservedBytes);
            Assert.Equal(0, admitted.CaptureStatistics().AllocationCount);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void RepeatedCapturesWithRealBackingAllocateNoManagedStorage()
    {
        using NativeConcurrentPool<byte> pool = new(preLease: 0, preAllocateBytes: 64, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentArena arena = new(preAllocateBytes: 64, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        _ = pool.CaptureDiagnosticSnapshot();
        _ = arena.CaptureDiagnosticSnapshot();
        long before = GC.GetAllocatedBytesForCurrentThread();
        long observed = 0;
        for (int iteration = 0; iteration < 1_024; iteration++)
        {
            observed += pool.CaptureDiagnosticSnapshot().OutstandingNativeBytes;
            observed += arena.CaptureDiagnosticSnapshot().PeakOutstandingNativeBytes;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(1_024 * 128, observed);
    }

    [Fact]
    public void FreedUnpublishedSegmentRetainsItsActualPeakAndCannotDebitTwice()
    {
        NativeMemoryBudget budget = new(64);
        NativeOwnerBackingHistory history = new(1);
        NativeGenerationOwner owner = new(0, budget, 1);
        owner.ReleaseToNative();
        NativeSegment segment = NativeSegment.Allocate(64, "BackingTest", 0, "publication",
            NativeOwnerLifecycle.Active, false, budget, backingHistory: history);
        try
        {
            Assert.Equal((64L, 0L, 64L), history.Capture());
            Assert.Throws<InvalidOperationException>(() => owner.AddSegment(segment));
            Assert.Equal((0L, 0L, 64L), history.Capture());
            Assert.False(segment.MarkDetached());
            segment.FreeNow();
            Assert.Equal((0L, 0L, 64L), history.Capture());
            Assert.Equal(1, budget.CaptureStatistics().FreeCount);
        }
        finally { segment.FreeNow(); }
    }

    [Fact]
    public void DescriptorOwnsOnlyNumericHistoryAndChecksOverflowBeforeBackendWork()
    {
        Assert.All(typeof(NativeOwnerBackingHistory).GetFields(BindingFlags.NonPublic | BindingFlags.Instance),
            field => Assert.Equal(typeof(long), field.FieldType));
        NativeOwnerBackingHistory history = new();
        FieldInfo field = typeof(NativeOwnerBackingHistory).GetField("_outstandingBytes", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(history, long.MaxValue - 32);
        NativeMemoryBudget budget = new(64);
        try
        {
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<OverflowException>(() => NativeSegment.Allocate(64, "BackingTest", 0, "overflow",
                NativeOwnerLifecycle.Active, false, budget, backingHistory: history));
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
            Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
            Assert.Throws<NativeAllocationFailedException>(static () => new NativeWorkspace<byte>(1));
        }
        finally { field.SetValue(history, 0L); NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void DetachedOldGenerationAndFreshStorageShareOneLifetimePeakUntilRealFree()
    {
        NativeMemoryBudget budget = new(256);
        using NativeConcurrentPool<byte> pool = new(budget, 0, 64, NativeMemoryReturn.ToNativeMemory, false);
        NativeGenerationOwner old = CaptureAndDetach(pool);
        try
        {
            AssertBacking(pool.CaptureDiagnosticSnapshot(), 128, 64, 128);
            AssertMatches(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot());
            Assert.Equal(64, pool.GetStatistics().RetainedBytes);
            Assert.Equal(128, budget.CaptureStatistics().CommittedBytes);
            old.ReleaseToNative();
            AssertBacking(pool.CaptureDiagnosticSnapshot(), 64, 0, 128);
            old.ReleaseToNative();
            AssertBacking(pool.CaptureDiagnosticSnapshot(), 64, 0, 128);
            pool.Dispose();
            AssertBacking(pool.CaptureDiagnosticSnapshot(), 0, 0, 128);
        }
        finally { old.ReleaseToNative(); }
    }

    [Fact]
    public void DetachedSegmentFinalizersCanDebitWhileTheOwnerAndFreshGenerationStayAlive()
    {
        NativeMemoryBudget budget = new(256);
        using NativeConcurrentPool<byte> pool = new(budget, 0, 64, NativeMemoryReturn.ToNativeMemory, false);
        WeakReference old = AbandonDetachedGeneration(pool);
        for (int attempt = 0; attempt < 8 && pool.CaptureDiagnosticSnapshot().DetachedNativeBytes != 0; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(old.IsAlive);
        AssertBacking(pool.CaptureDiagnosticSnapshot(), 64, 0, 128);
        Assert.Equal(64, budget.CaptureStatistics().CommittedBytes);
        using ConcurrentPooled<byte> fresh = pool.Rent(1, static writer => writer.Write(42));
        Assert.Equal(42, fresh.Read(static view => view[0]));
        GC.KeepAlive(pool);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbandonDetachedGeneration(NativeConcurrentPool<byte> pool) =>
        new(CaptureAndDetach(pool));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeGenerationOwner CaptureAndDetach(NativeConcurrentPool<byte> pool)
    {
        NativeGenerationOwner? owner = null;
        NativeMemoryTestHooks.SetOperationEnteredWithGenerationOwner((operation, entered) =>
        {
            if (string.Equals(operation, nameof(ConcurrentPooled<byte>.Access), StringComparison.Ordinal)) { owner = entered; }
        });
        try
        {
            using ConcurrentPooled<byte> lease = pool.Rent(1, static writer => writer.Write(7));
            lease.Access(static view => Assert.Equal(7, view[0]));
            Assert.NotNull(owner);
            pool.ReturnMemoryToGarbageCollector();
            AssertBacking(pool.CaptureDiagnosticSnapshot(), 64, 64, 64);
            pool.LeaseFromMemory();
            AssertBacking(pool.CaptureDiagnosticSnapshot(), 128, 64, 128);
            return owner;
        }
        finally { NativeMemoryTestHooks.SetOperationEnteredWithGenerationOwner(null); }
    }

    private static void AssertMatches(NativeOwnerStatistics statistics, NativeOwnerDiagnosticSnapshot snapshot)
    {
        Assert.Equal(statistics.OutstandingNativeBytes, snapshot.OutstandingNativeBytes);
        Assert.Equal(statistics.DetachedNativeBytes, snapshot.DetachedNativeBytes);
        Assert.Equal(statistics.PeakOutstandingNativeBytes, snapshot.PeakOutstandingNativeBytes);
    }

    private static void AssertBacking(NativeOwnerDiagnosticSnapshot snapshot, long outstanding, long detached, long peak)
    {
        Assert.Equal(outstanding, snapshot.OutstandingNativeBytes);
        Assert.Equal(detached, snapshot.DetachedNativeBytes);
        Assert.Equal(peak, snapshot.PeakOutstandingNativeBytes);
    }
}
