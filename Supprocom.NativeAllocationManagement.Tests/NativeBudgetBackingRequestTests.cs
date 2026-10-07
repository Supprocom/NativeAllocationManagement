using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeBudgetBackingRequestTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64)]
    public void VariableAcquisitionsSurviveReleaseTraceLossAndRejectedOrFailedAttempts(int traceCapacity)
    {
        NativeMemoryBudget budget = new(64, traceCapacity);
        foreach (ref readonly int count in (ReadOnlySpan<int>)[1, 3, 9])
        {
            using NativeWorkspace<int> workspace = new(budget, count);
            workspace.Initialize(count, static writer => writer.Fill(42));
            Assert.Equal(42, workspace.Read(static view => view[0]));
        }
        Assert.Throws<NativeMemoryBudgetExceededException>(() => new NativeWorkspace<int>(budget, 17));
        NativeMemoryTestHooks.FailNextAllocation();
        try { Assert.Throws<NativeAllocationFailedException>(() => new NativeWorkspace<int>(budget, 2)); }
        finally { NativeMemoryTestHooks.Reset(); }
        NativeMemoryBudgetStatistics ended = budget.CaptureStatistics();
        Assert.Equal(52, ended.AcquiredBackingBytes);
        Assert.Equal(0, ended.ReplacementBackingBytes);
        Assert.Equal(3, ended.AllocationCount);
        Assert.Equal(3, ended.FreeCount);
        Assert.Equal(0, ended.CommittedBytes);
        Assert.Equal(0, ended.ReservedBytes);
        Assert.Equal(1, ended.RejectedAllocationCount);
        Assert.Equal(1, ended.FailedAllocationCount);
        Assert.False(ended.HistoryOverflowed);
        if (traceCapacity == 1) Assert.True(ended.DroppedTraceEventCount > 0);
        if (traceCapacity == 0) Assert.Equal(0, ended.TraceCount);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 32)]
    [InlineData(2, 0)]
    [InlineData(2, 32)]
    public void NullReallocationAndReplacementTargetsAreDisjoint(int initialCapacity, int traceCapacity)
    {
        NativeMemoryBudget budget = new(128, traceCapacity);
        using (NativeBuilder<int> builder = new(budget, initialCapacity))
        {
            Assert.True(builder.TryEnsureCapacity(4));
            Assert.Equal(4, builder.Capacity);
            Assert.True(builder.TryEnsureCapacity(5));
            Assert.Equal(8, builder.Capacity);
            builder.Append(42);
            NativeMemoryBudgetStatistics live = budget.CaptureStatistics();
            Assert.Equal(initialCapacity == 0 ? 16 : 8, live.AcquiredBackingBytes);
            Assert.Equal(initialCapacity == 0 ? 32 : 48, live.ReplacementBackingBytes);
            Assert.Equal(1, live.AllocationCount);
            Assert.Equal(2, live.ReallocationCount);
            Assert.Equal(32, live.CommittedBytes);
            NativeTransfer<int> transfer = builder.Complete();
            try { Assert.Equal(42, transfer.Read(static view => view[0])); }
            finally { transfer.Dispose(); }
        }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void PendingFailedPreparedAndActivatedPermissionCountBackingExactlyOnce()
    {
        NativeMemoryBudget budget = new(16);
        Assert.True(budget.TryReserve<int>(4, out NativeMemoryReservation<int>? permission, out _));
        Assert.Equal(0, budget.CaptureStatistics().AcquiredBackingBytes);
        using (NativeMemoryReservation<int> reservation = NativeMemoryReservation<int>.Move(ref permission))
        {
            NativeMemoryTestHooks.FailNextAllocation();
            try { Assert.Throws<NativeAllocationFailedException>(() => reservation.PrepareBacking()); }
            finally { NativeMemoryTestHooks.Reset(); }
            Assert.Equal(0, budget.CaptureStatistics().AcquiredBackingBytes);
            reservation.PrepareBacking();
            Assert.Equal(16, budget.CaptureStatistics().AcquiredBackingBytes);
        }
        Assert.Equal(16, budget.CaptureStatistics().AcquiredBackingBytes);
        Assert.True(budget.TryReserve<int>(4, out permission, out _));
        NativeTransfer<int> transfer = NativeMemoryReservation<int>.Activate(ref permission, static writer => writer.Fill(42));
        try
        {
            Assert.Equal(32, budget.CaptureStatistics().AcquiredBackingBytes);
            Assert.Equal(0, budget.CaptureStatistics().ReplacementBackingBytes);
            Assert.Equal(42, transfer.Read(static view => view[0]));
        }
        finally { transfer.Dispose(); }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void RetainedReuseAndSnapshotsDoNotAddRequestBytesOrAllocate()
    {
        NativeMemoryBudget budget = new(16);
        using NativeBuilder<int> builder = new(budget, 4);
        Assert.True(builder.TryEnsureCapacity(4));
        _ = budget.CaptureStatistics();
        bool unchanged = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1000; iteration++)
        {
            unchanged &= builder.TryEnsureCapacity(4);
            NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
            unchanged &= snapshot.AcquiredBackingBytes == 16 && snapshot.ReplacementBackingBytes == 0;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(unchanged);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ConcurrentDomainsHistoryCountsEverySuccessfulExtent()
    {
        NativeMemoryBudget budget = new(64);
        Parallel.For(0, 8, _ =>
        {
            for (int iteration = 0; iteration < 16; iteration++)
            {
                using NativeWorkspace<int> workspace = new(budget, 2);
                workspace.Initialize(2, static writer => writer.Fill(42));
                Assert.Equal(42, workspace.Read(static view => view[0]));
            }
        });
        NativeMemoryBudgetStatistics ended = budget.CaptureStatistics();
        Assert.Equal(1024, ended.AcquiredBackingBytes);
        Assert.Equal(128, ended.AllocationCount);
        Assert.Equal(128, ended.FreeCount);
        Assert.Equal(0, ended.ReplacementBackingBytes);
        Assert.Equal(0, ended.CommittedBytes);
        Assert.InRange(ended.PeakCommittedBytes, 8, 64);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ByteHistorySaturationCannotInterruptBackingOrCleanup(bool replacing)
    {
        NativeMemoryBudget budget = new(64);
        string field = replacing ? "_replacementBackingBytes" : "_acquiredBackingBytes";
        typeof(NativeMemoryBudget).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(budget, long.MaxValue - 16);
        using (NativeBuilder<int> builder = new(budget, replacing ? 2 : 4))
        {
            if (replacing) Assert.True(builder.TryEnsureCapacity(4));
            NativeMemoryBudgetStatistics exact = budget.CaptureStatistics();
            Assert.Equal(long.MaxValue, replacing ? exact.ReplacementBackingBytes : exact.AcquiredBackingBytes);
            Assert.False(exact.HistoryOverflowed);
            if (replacing) Assert.True(builder.TryEnsureCapacity(5));
            else
            {
                using NativeWorkspace<int> another = new(budget, 4);
                Assert.True(budget.CaptureStatistics().HistoryOverflowed);
            }
            builder.Append(42);
            NativeTransfer<int> transfer = builder.Complete();
            try { Assert.Equal(42, transfer.Read(static view => view[0])); }
            finally { transfer.Dispose(); }
        }
        NativeMemoryBudgetStatistics ended = budget.CaptureStatistics();
        Assert.True(ended.HistoryOverflowed);
        Assert.Equal(long.MaxValue, replacing ? ended.ReplacementBackingBytes : ended.AcquiredBackingBytes);
        Assert.Equal(0, ended.CommittedBytes);
        Assert.Equal(0, ended.ReservedBytes);
        Assert.Equal(0, ended.ActiveAllocationCount);
    }
}
