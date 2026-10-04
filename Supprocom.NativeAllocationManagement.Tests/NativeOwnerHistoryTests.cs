using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeOwnerHistoryTests
{
    [Theory]
    [InlineData("_trimmedBytes")]
    [InlineData("_trimCallCount")]
    public void ATrimHistoryAloneReportsItsOwnOverflowWithoutAnEarlierOverflow(string field)
    {
        NativeMemoryBudget budget = new(512);
        using NativePool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory);
        using (Pooled<int> value = pool.Rent(1, static writer => writer.Write(42)))
        {
            Assert.Equal(42, value.Read(static view => view[0]));
        }
        Assert.False(pool.GetStatistics().HistoryOverflowed);
        SetHistory(GetKernel(pool), field, long.MaxValue);
        long backing = pool.GetStatistics().RetainedBytes;
        Assert.Equal((nuint)backing, pool.TrimRetainedMemory());
        Assert.True(pool.GetStatistics().HistoryOverflowed);
        Assert.True(pool.CaptureDiagnosticSnapshot().HistoryOverflowed);
        NativeOwnerStatistics snapshot = pool.GetStatistics();
        Assert.Equal(string.Equals(field, "_trimmedBytes", StringComparison.Ordinal) ? long.MaxValue : backing, snapshot.TrimmedBytes);
        Assert.Equal(string.Equals(field, "_trimCallCount", StringComparison.Ordinal) ? long.MaxValue : 1, snapshot.TrimCallCount);
        Assert.Equal(1, snapshot.FreshSegmentAllocationCount);
        Assert.Equal(0, snapshot.RetainedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void ExactMaximumIsNotAnOverflowUntilAnotherRealAcquisitionOccurs()
    {
        NativeMemoryBudget budget = new(512);
        using NativePool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory);
        SetHistory(GetKernel(pool), "_freshSegmentAllocationCount", long.MaxValue - 1);
        using Pooled<int> first = pool.Rent(1, static writer => writer.Write(17));
        Assert.Equal(long.MaxValue, pool.GetStatistics().FreshSegmentAllocationCount);
        Assert.False(pool.GetStatistics().HistoryOverflowed);
        using Pooled<int> second = pool.Rent(1, static writer => writer.Write(19));
        Assert.Equal(long.MaxValue, pool.GetStatistics().FreshSegmentAllocationCount);
        Assert.True(pool.GetStatistics().HistoryOverflowed);
        Assert.Equal(17, first.Read(static view => view[0]));
        Assert.Equal(19, second.Read(static view => view[0]));
        Assert.Equal(2, budget.CaptureStatistics().AllocationCount);
    }

    [Fact]
    public void PoolAcquisitionIdentityIsIndependentOfSaturatedCompletedHistory()
    {
        NativeMemoryBudget budget = new(512, traceCapacity: 16);
        using NativePool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory);
        object kernel = GetKernel(pool);
        SetHistory(kernel, "_freshSegmentAllocationCount", long.MaxValue);
        using (Pooled<int> value = pool.Rent(1, static writer => writer.Write(42)))
        {
            Assert.Equal(42, value.Read(static view => view[0]));
            Assert.True(pool.GetStatistics().HistoryOverflowed);
            Assert.True(pool.CaptureDiagnosticSnapshot().HistoryOverflowed);
            Assert.Equal(long.MaxValue, pool.GetStatistics().FreshSegmentAllocationCount);
            Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        }
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[16];
        int count = budget.CopyTraceTo(events);
        Assert.Equal(2, count);
        Assert.Equal(1, events[1].AllocationOrdinal);
        SetHistory(kernel, "_trimCallCount", long.MaxValue);
        SetHistory(kernel, "_trimmedBytes", long.MaxValue);
        Assert.True(pool.TrimRetainedMemory() > 0);
        Assert.Equal(long.MaxValue, pool.GetStatistics().TrimmedBytes);
        Assert.Equal(long.MaxValue, pool.GetStatistics().TrimCallCount);
        Assert.Equal(0, pool.GetStatistics().RetainedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void ArenaAcquisitionAndWholeSegmentTrimSurviveSaturatedHistory()
    {
        NativeMemoryBudget budget = new(16_384);
        using NativeArena arena = new(budget, 0, NativeMemoryReturn.ToNativeMemory);
        object kernel = GetKernel(arena);
        SetHistory(kernel, "_freshSegmentAllocationCount", long.MaxValue);
        ArenaLease<int> value = arena.Scratch<int>(1, static writer => writer.Write(42));
        Assert.Equal(42, value.Read(static view => view[0]));
        Assert.Equal(long.MaxValue, arena.GetStatistics().FreshSegmentAllocationCount);
        Assert.True(arena.GetStatistics().HistoryOverflowed);
        Assert.True(arena.CaptureDiagnosticSnapshot().HistoryOverflowed);
        long firstBacking = arena.GetStatistics().RetainedBytes;
        ArenaLease<int> tail = arena.Scratch<int>(2_048, static writer => writer.Fill(7));
        Assert.Equal(7, tail.Read(static view => view[2_047]));
        long beforeTrim = arena.GetStatistics().RetainedBytes;
        arena.Reset();
        SetHistory(kernel, "_trimCallCount", long.MaxValue);
        SetHistory(kernel, "_trimmedBytes", long.MaxValue);
        Assert.Equal((nuint)(beforeTrim - firstBacking), arena.TrimRetainedMemory());
        Assert.Equal(long.MaxValue, arena.GetStatistics().TrimmedBytes);
        Assert.Equal(firstBacking, budget.CaptureStatistics().CommittedBytes);
        arena.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void RegionPublicationAndCleanupDoNotThrowAfterSuccessfulBackingAcquisition()
    {
        NativeMemoryBudget budget = new(8_192);
        NativeRegionKernel kernel = new(0, NativeMemoryReturn.ToNativeMemory, budget);
        SetHistory(kernel, "_freshSegmentAllocationCount", long.MaxValue);
        Local<int> value = kernel.LeaseInitialized<int>(1, static writer => writer.Write(42));
        Assert.Equal(42, value.Read(static view => view[0]));
        Assert.Equal(4, kernel.GetStatistics().RequestedBytes);
        Assert.True(kernel.GetStatistics().HistoryOverflowed);
        Assert.True(kernel.GetDiagnosticSnapshot().HistoryOverflowed);
        Assert.Equal(long.MaxValue, kernel.GetStatistics().FreshSegmentAllocationCount);
        kernel.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void SynchronizedPoolHistoryCannotPreventPhysicalTrimOrReuse()
    {
        NativeMemoryBudget budget = new(8_192);
        using NativeConcurrentPool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory, false);
        object kernel = GetKernel(pool);
        SetHistory(kernel, "_freshSegmentAllocationCount", long.MaxValue);
        using (ConcurrentPooled<int> value = pool.Rent(1, static writer => writer.Write(42)))
        {
            Assert.Equal(42, value.Read(static view => view[0]));
            Assert.True(pool.GetStatistics().HistoryOverflowed);
            Assert.True(pool.CaptureDiagnosticSnapshot().HistoryOverflowed);
        }
        SetHistory(kernel, "_trimCallCount", long.MaxValue);
        SetHistory(kernel, "_trimmedBytes", long.MaxValue);
        Assert.True(pool.TrimRetainedMemory() > 0);
        Assert.Equal(long.MaxValue, pool.GetStatistics().TrimmedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        using ConcurrentPooled<int> next = pool.Rent(1, static writer => writer.Write(7));
        Assert.Equal(7, next.Read(static view => view[0]));
    }

    [Fact]
    public void IdentityExhaustionRejectsBeforeBudgetAdmissionOrProducerExecution()
    {
        NativeMemoryBudget budget = new(512);
        using NativePool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory);
        object kernel = GetKernel(pool);
        SetHistory(kernel, "_nextAllocationOrdinal", long.MaxValue);
        bool invoked = false;
        Assert.Throws<OverflowException>(() =>
        {
            pool.Rent(1, writer => { invoked = true; writer.Write(42); });
        });
        Assert.False(invoked);
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, pool.GetStatistics().FreshSegmentAllocationCount);
    }

    private static object GetKernel(object owner) =>
        owner.GetType().GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static void SetHistory(object kernel, string name, long value) =>
        kernel.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(kernel, value);
}
