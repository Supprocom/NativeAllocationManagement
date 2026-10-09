using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeBudgetHistoryTests
{
    [Fact]
    public void SaturatedCompletedHistoriesDoNotBlockNewBackingOrPhysicalRelease()
    {
        NativeMemoryBudget budget = new(64, traceCapacity: 8);
        SetHistory(budget, "_allocationCount", long.MaxValue);
        SetHistory(budget, "_freeCount", long.MaxValue);
        using (NativeWorkspace<int> workspace = new(budget, 4))
        {
            NativeMemoryBudgetStatistics live = budget.CaptureStatistics();
            Assert.Equal(1, live.ActiveAllocationCount);
            Assert.Equal(16, live.CommittedBytes);
            Assert.Equal(0, live.ReservedBytes);
            Assert.Equal(long.MaxValue, live.AllocationCount);
            Assert.True(live.HistoryOverflowed);
            workspace.Initialize(4, static writer => writer.Fill(42));
            Assert.Equal(42, workspace.Read(static view => view[0]));
        }
        NativeMemoryBudgetStatistics released = budget.CaptureStatistics();
        Assert.Equal(0, released.ActiveAllocationCount);
        Assert.Equal(0, released.CommittedBytes);
        Assert.Equal(0, released.ReservedBytes);
        Assert.Equal(long.MaxValue, released.FreeCount);
        Assert.Equal(16, released.PeakCommittedBytes);
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[8];
        Assert.Equal(3, budget.CopyTraceTo(events));
        Assert.Equal(NativeMemoryTraceKind.Released, events[2].Kind);
    }

    [Fact]
    public void ExactMaximumHistoryRemainsExactUntilAnotherEventActuallyOccurs()
    {
        NativeMemoryBudget budget = new(64);
        SetHistory(budget, "_allocationCount", long.MaxValue - 1);
        SetHistory(budget, "_freeCount", long.MaxValue - 1);
        using (NativeWorkspace<int> workspace = new(budget, 4))
        {
            Assert.Equal(long.MaxValue, budget.CaptureStatistics().AllocationCount);
            Assert.False(budget.CaptureStatistics().HistoryOverflowed);
        }
        Assert.Equal(long.MaxValue, budget.CaptureStatistics().FreeCount);
        Assert.False(budget.CaptureStatistics().HistoryOverflowed);
        using (NativeWorkspace<int> next = new(budget, 4))
        {
            Assert.True(budget.CaptureStatistics().HistoryOverflowed);
            Assert.Equal(1, budget.CaptureStatistics().ActiveAllocationCount);
        }
        Assert.Equal(0, budget.CaptureStatistics().ActiveAllocationCount);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 2)]
    public void ReallocHistorySaturationCannotLoseTheReplacementOrItsCharge(int initialCapacity, int expectedCapacity)
    {
        NativeMemoryBudget budget = new(64);
        using NativeBuilder<int> builder = new(budget, initialCapacity);
        SetHistory(budget, "_reallocationCount", long.MaxValue);
        Assert.True(builder.TryEnsureCapacity(2));
        builder.Append(42);
        NativeMemoryBudgetStatistics resized = budget.CaptureStatistics();
        Assert.Equal(1, resized.ActiveAllocationCount);
        Assert.Equal(1, resized.AllocationCount);
        Assert.Equal(long.MaxValue, resized.ReallocationCount);
        Assert.True(resized.HistoryOverflowed);
        Assert.Equal(expectedCapacity, builder.Capacity);
        Assert.Equal(expectedCapacity * sizeof(int), resized.CommittedBytes);
        Assert.Equal(0, resized.ReservedBytes);
        NativeTransfer<int> transfer = builder.Complete();
        try { Assert.Equal(42, transfer.Read(static view => view[0])); }
        finally { transfer.Dispose(); }
        Assert.Equal(0, budget.CaptureStatistics().ActiveAllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void RefusalHistorySaturationPreservesNonAllocatingExpectedExhaustion()
    {
        NativeMemoryBudget budget = new(16);
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(42);
        SetHistory(budget, "_rejectedAllocationCount", long.MaxValue);
        Assert.False(builder.TryEnsureCapacity(5));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(builder.TryEnsureCapacity(5));
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        NativeMemoryBudgetStatistics refused = budget.CaptureStatistics();
        Assert.Equal(long.MaxValue, refused.RejectedAllocationCount);
        Assert.True(refused.HistoryOverflowed);
        Assert.Equal(16, refused.CommittedBytes);
        Assert.Equal(0, refused.ReservedBytes);
        Assert.Equal(1, refused.ActiveAllocationCount);
        Assert.Equal(1, builder.Count);
    }

    [Fact]
    public void AdmittedFailureStillCancelsItsChargeWithSaturatedFailureHistory()
    {
        NativeMemoryBudget budget = new(64);
        SetHistory(budget, "_failedAllocationCount", long.MaxValue);
        NativeMemoryTestHooks.FailNextAllocation();
        Assert.Throws<NativeAllocationFailedException>(() => new NativeWorkspace<int>(budget, 4));
        NativeMemoryBudgetStatistics failed = budget.CaptureStatistics();
        Assert.Equal(0, failed.CommittedBytes);
        Assert.Equal(0, failed.ReservedBytes);
        Assert.Equal(0, failed.ActiveAllocationCount);
        Assert.Equal(0, failed.AllocationCount);
        Assert.Equal(long.MaxValue, failed.FailedAllocationCount);
        Assert.True(failed.HistoryOverflowed);
    }

    [Fact]
    public void PagePreparationRollbackReleasesRealPagesDespiteSaturatedAcquisitionAndFreeHistory()
    {
        NativeMemoryBudget budget = new(256);
        SetHistory(budget, "_allocationCount", long.MaxValue);
        SetHistory(budget, "_freeCount", long.MaxValue);
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(3);
        Assert.Throws<InvalidOperationException>(() => new NativePreparedPool<int>(new NativePoolPreparation(4, 1, 2), budget));
        NativeMemoryBudgetStatistics released = budget.CaptureStatistics();
        Assert.Equal(0, released.CommittedBytes);
        Assert.Equal(0, released.ReservedBytes);
        Assert.Equal(0, released.ActiveAllocationCount);
        Assert.Equal(1, released.FailedAllocationCount);
        Assert.True(released.HistoryOverflowed);
    }

    private static void SetHistory(NativeMemoryBudget budget, string field, long value) =>
        typeof(NativeMemoryBudget).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(budget, value);
}
