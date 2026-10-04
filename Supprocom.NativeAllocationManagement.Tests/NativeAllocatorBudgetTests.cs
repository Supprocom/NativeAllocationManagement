using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeAllocatorBudgetTests
{
    [Fact]
    public void AlignedExtentIncludesKnownBackendRoundingWithoutChangingUsableBytes()
    {
        long expected = OperatingSystem.IsWindows() ? 17 : 64;
        NativeMemoryBudget budget = new(expected);
        using NativePool<byte> pool = new(budget, 0, 17, NativeMemoryReturn.ToNativeMemory);
        NativeOwnerStatistics statistics = pool.GetStatistics();
        Assert.Equal(expected, statistics.RetainedBytes);
        Assert.Equal(17, statistics.UsableCapacityBytes);
        Assert.Equal(expected, budget.CaptureStatistics().CommittedBytes);
        using Pooled<byte> lease = pool.Rent(17, static writer => writer.Fill(3));
        Assert.Equal(17, lease.Capacity);
        Assert.Equal(3, lease.Read(static view => view[16]));
    }

    [Fact]
    public void PoolReturnRetainsChargeUntilItsPhysicalTrim()
    {
        long extent = OperatingSystem.IsWindows() ? sizeof(int) : 64;
        NativeMemoryBudget budget = new(extent);
        using NativePool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToNativeMemory);
        using (Pooled<int> lease = pool.Rent(1, static writer => writer.Write(42)))
        {
            Assert.Equal(42, lease.Read(static view => view[0]));
        }
        Assert.Equal(extent, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal((nuint)extent, pool.TrimRetainedMemory());
        NativeMemoryBudgetStatistics released = budget.CaptureStatistics();
        Assert.Equal(0, released.CommittedBytes);
        Assert.Equal(1, released.AllocationCount);
        Assert.Equal(1, released.FreeCount);
    }

    [Fact]
    public void PoolCeilingRefusesBeforeInitializerOrNativeFailureConsumption()
    {
        NativeMemoryBudget budget = new(0);
        using NativePool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory);
        bool initialized = false;
        NativeMemoryTestHooks.FailNextAllocation();
        try
        {
            Assert.Throws<NativeMemoryBudgetExceededException>(() =>
            {
                using Pooled<int> lease = pool.Rent(1, writer =>
                {
                    initialized = true;
                    writer.Fill(7);
                });
            });
            Assert.False(initialized);
            Assert.Throws<NativeAllocationFailedException>(static () => new NativeWorkspace<int>(1));
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(0, budget.CaptureStatistics().FailedAllocationCount);
            Assert.Equal(1, budget.CaptureStatistics().RejectedAllocationCount);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void FastArenaFallsBackWithinACompleteHeaderAndPayloadCeiling()
    {
        long extent = OperatingSystem.IsWindows() ? 64 + sizeof(int) + 3 : 128;
        NativeMemoryBudget budget = new(extent);
        using NativeArena arena = new(budget, 0, NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> lease = arena.Scratch<int>(1, static writer => writer.Write(42));
        Assert.Equal(42, lease.Read(static view => view[0]));
        Assert.Equal(extent, arena.GetStatistics().RetainedBytes);
        Assert.Equal(extent, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().RejectedAllocationCount);
        Assert.True(arena.GetStatistics().UsableCapacityBytes < 4096);
    }

    [Fact]
    public void RegionFallbackChargesItsHeaderAndReleasesAtLexicalExit()
    {
        long extent = OperatingSystem.IsWindows() ? 64 + sizeof(int) : 128;
        NativeMemoryBudget budget = new(extent);
        using (NativeRegion region = new(budget, 0, NativeMemoryReturn.ToNativeMemory))
        {
            Local<int> lease = region.Lease<int>(1, static writer => writer.Write(42));
            Assert.Equal(42, lease.Read(static view => view[0]));
            Assert.Equal(extent, region.GetStatistics().RetainedBytes);
            Assert.Equal(sizeof(int), region.GetStatistics().UsableCapacityBytes);
            Assert.Equal(extent, budget.CaptureStatistics().CommittedBytes);
        }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void SynchronizedArenaFallbackDoesNotReserveMetadataForRefusedGrowth()
    {
        NativeMemoryBudget budget = new(64);
        using NativeConcurrentArena arena = new(budget, 0, NativeMemoryReturn.ToNativeMemory, false);
        ConcurrentArenaLease<byte> lease = arena.Scratch<byte>(1, static writer => writer.Write(42));
        Assert.Equal(42, lease.Read(static view => view[0]));
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 64, budget.CaptureStatistics().CommittedBytes);
        NativeOwnerDiagnosticSnapshot before = arena.CaptureDiagnosticSnapshot();
        var beforeBanks = arena.CurrentBankCapacitiesForTest;
        Assert.Throws<NativeMemoryBudgetExceededException>(() => arena.ReserveRetainedMemory(65));
        NativeOwnerDiagnosticSnapshot after = arena.CaptureDiagnosticSnapshot();
        var afterBanks = arena.CurrentBankCapacitiesForTest;
        Assert.Equal(beforeBanks.Bumps, afterBanks.Bumps);
        Assert.Equal(beforeBanks.OwnerSegments, afterBanks.OwnerSegments);
        Assert.Equal(before.ActiveRecords, after.ActiveRecords);
        Assert.Equal(42, lease.Read(static view => view[0]));
    }

    [Fact]
    public void AllOwnerFamiliesCompeteInTheSameDomain()
    {
        long pooledExtent = OperatingSystem.IsWindows() ? 17 : 64;
        long arenaExtent = OperatingSystem.IsWindows() ? 65 : 128;
        NativeMemoryBudget budget = new(64 + pooledExtent + arenaExtent);
        using NativeBuilder<int> builder = new(budget, 16);
        using NativePool<byte> pool = new(budget, 0, 17, NativeMemoryReturn.ToNativeMemory);
        using NativeArena arena = new(budget, 1, NativeMemoryReturn.ToNativeMemory);
        builder.Append(42);
        Assert.Equal(budget.CapacityBytes, budget.CaptureStatistics().CommittedBytes);
        Assert.False(builder.TryEnsureCapacity(17));
        Assert.Equal(1, builder.Count);
        Assert.Equal((nuint)pooledExtent, pool.TrimRetainedMemory());
        arena.Dispose();
        Assert.True(builder.TryEnsureCapacity(17));
        Assert.Equal(OperatingSystem.IsWindows() ? 68 : 128, budget.CaptureStatistics().CommittedBytes);
        NativeTransfer<int> transfer = builder.Complete();
        try
        {
            Assert.Equal(42, transfer.Read(static view => view[0]));
        }
        finally
        {
            transfer.Dispose();
        }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void PartialPoolConstructionFailureReturnsEveryAcquiredCharge()
    {
        long extent = OperatingSystem.IsWindows() ? sizeof(int) : 64;
        NativeMemoryBudget budget = new(extent);
        Assert.Throws<NativeMemoryBudgetExceededException>(() =>
            new NativeConcurrentPool<int>(budget, 1, 4, NativeMemoryReturn.ToNativeMemory, false));
        NativeMemoryBudgetStatistics statistics = budget.CaptureStatistics();
        Assert.Equal(0, statistics.CommittedBytes);
        Assert.Equal(0, statistics.ReservedBytes);
        Assert.Equal(1, statistics.AllocationCount);
        Assert.Equal(1, statistics.FreeCount);
        Assert.Equal(1, statistics.RejectedAllocationCount);
    }

    [Fact]
    public void DeferredSynchronizedOwnersDoNotAcquireBeforeActivation()
    {
        NativeMemoryBudget budget = new(0);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, true);
        using NativeConcurrentArena arena = new(budget, 64, NativeMemoryReturn.ToNativeMemory, true);
        Assert.Equal(NativeOwnerLifecycle.Unleased, pool.GetStatistics().Lifecycle);
        Assert.Equal(NativeOwnerLifecycle.Unleased, arena.GetStatistics().Lifecycle);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Throws<NativeMemoryBudgetExceededException>(pool.LeaseFromMemory);
        Assert.Throws<NativeMemoryBudgetExceededException>(arena.LeaseFromMemory);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
    }

    [Fact]
    public void ExternalRangeIsReportedSeparatelyWithoutFabricatedOwnedBacking()
    {
        using AlignedBuffer buffer = new(128);
        NativeMemoryBudget budget = new(0);
        using NativeConcurrentArena arena = new(budget, 0, NativeMemoryReturn.ToNativeMemory, false);
        Assert.Equal((nuint)128, arena.ReserveExternalMemory(buffer, 0, 128));
        buffer.Dispose();
        ConcurrentArenaLease<int> lease = arena.Scratch<int>(4, static writer => writer.Fill(42));
        Assert.Equal(42, lease.Read(static view => view[3]));
        NativeOwnerStatistics statistics = arena.GetStatistics();
        Assert.Equal(0, statistics.RetainedBytes);
        Assert.Equal(0, statistics.UsableCapacityBytes);
        Assert.Equal(128, statistics.BorrowedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, buffer.ReleaseCount);
        arena.Dispose();
        Assert.Equal(1, buffer.ReleaseCount);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The regression captures the deliberately injected drain exception and always joins the bounded worker before cleanup.")]
    public async Task RetiredAndQuarantinedBackingCannotEscapeItsBudgetCharge()
    {
        NativeMemoryTestHooks.Reset();
        long extent = OperatingSystem.IsWindows() ? sizeof(int) : 64;
        NativeMemoryBudget budget = new(extent);
        using NativeConcurrentPool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToNativeMemory, false);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim allowCallback = new();
        NativeMemoryTestHooks.SetOperationEntered(operation =>
        {
            if (string.Equals(operation, nameof(ConcurrentPooled<int>.Access), StringComparison.Ordinal))
            {
                entered.Set();
            }
        });
        Task<Exception?>? worker = null;
        try
        {
            worker = Task.Run(() =>
            {
                try
                {
                    ConcurrentPooled<int> lease = pool.Rent(1, static writer => writer.Fill(7));
                    lease.Access(_ => allowCallback.Wait(TimeSpan.FromSeconds(10)));
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            pool.ReleaseLeasesToGarbageCollector();
            Assert.Equal(extent, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(extent, pool.CaptureDiagnosticSnapshot().RetiredBytes);
            NativeMemoryTestHooks.FailAfterCommitBoundary(1);
            allowCallback.Set();
            Assert.IsType<NativeAllocationQuarantinedException>(await worker.ConfigureAwait(true));
            NativeOwnerDiagnosticSnapshot quarantined = pool.CaptureDiagnosticSnapshot();
            Assert.Equal(1, quarantined.QuarantinedSegmentCount);
            Assert.Equal(extent, quarantined.RetiredBytes);
            Assert.Equal(extent, budget.CaptureStatistics().CommittedBytes);
            Assert.Throws<NativeMemoryBudgetExceededException>(() =>
            {
                using ConcurrentPooled<int> fresh = pool.Rent(1, static writer => writer.Fill(0));
            });
            Assert.Equal(0, budget.CaptureStatistics().FreeCount);
        }
        finally
        {
            allowCallback.Set();
            if (worker is not null)
            {
                _ = await worker.ConfigureAwait(true);
            }
            NativeMemoryTestHooks.Reset();
            pool.Dispose();
        }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    private sealed unsafe class AlignedBuffer : SafeBuffer
    {
        internal AlignedBuffer(nuint bytes) : base(ownsHandle: true)
        {
            SetHandle((IntPtr)NativeMemory.AlignedAlloc(bytes, 64));
            Initialize(bytes);
        }

        internal int ReleaseCount { get; private set; }

        protected override bool ReleaseHandle()
        {
            NativeMemory.AlignedFree((void*)handle);
            ReleaseCount++;
            return true;
        }
    }
}
