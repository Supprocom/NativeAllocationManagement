using System.Numerics;
using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedPoolConstructionTests
{
    [Theory]
    [InlineData(7, 1, 3)]
    [InlineData(7, 3, 3)]
    [InlineData(7, 16, 3)]
    [InlineData(5, 1001, 2)]
    [InlineData(1, 1, 64)]
    [InlineData(64, 4096, 16)]
    public void FreshMetadataHasCompleteDefaultsAndOneDescendingChainAcrossEveryPage(int slotCount, int capacity, int slotsPerPage)
    {
        NativeMemoryBudget budget = new(checked((long)slotCount * capacity));
        using NativePool<byte> pool = new(new NativePoolPreparation(slotCount, capacity, slotsPerPage), budget);
        Array slots = (Array)Field(pool, "_slabs");
        int[] heads = (int[])Field(pool, "_freeHeads");
        int sizeClass = capacity <= 1 ? 0 : Math.Min(31, BitOperations.Log2((uint)(capacity - 1)) + 1);
        Assert.Equal(slotCount, slots.Length);
        Assert.Equal(32, heads.Length);
        Assert.Equal(1u << sizeClass, (uint)Field(pool, "_nonEmptyFreeClasses"));
        for (int index = 0; index < heads.Length; index++)
            Assert.Equal(index == sizeClass ? slotCount - 1 : -1, heads[index]);
        for (int index = 0; index < slots.Length; index++)
        {
            object slot = slots.GetValue(index)!;
            Assert.NotEqual(IntPtr.Zero, (IntPtr)Field(slot, "Pointer"));
            Assert.Equal(capacity, (int)Field(slot, "Capacity"));
            Assert.Equal(index - 1, (int)Field(slot, "Next"));
            Assert.Equal("Free", Field(slot, "State").ToString());
            Assert.Equal((nuint)0, (nuint)Field(slot, "AllocationBytes"));
            Assert.Equal(0L, (long)Field(slot, "Token"));
            Assert.Equal(0, (int)Field(slot, "BorrowCount"));
            Assert.Equal(0L, (long)Field(slot, "MetricsEpoch"));
            Assert.Equal(0L, (long)Field(slot, "Ordinal"));
            Assert.False((bool)Field(slot, "Detached"));
        }
        HoldAll(pool, slotCount, capacity);
        Assert.Equal(slotCount, pool.CapturePreparedSnapshot().AvailableSlotCount);
        Assert.Equal(slotCount, pool.CapturePreparedSnapshot().SuccessfulRentCount);
        Assert.Equal(1 + (slotCount - 1) / slotsPerPage, budget.CaptureStatistics().AllocationCount);
        pool.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(budget.CaptureStatistics().AllocationCount, budget.CaptureStatistics().FreeCount);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    public void EveryPublicationFailureReclaimsOnlyItsActuallyCommittedPrefix(int boundary, long acquiredPages)
    {
        NativeMemoryBudget budget = new(91, traceCapacity: 32);
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(boundary);
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
        {
            using NativePool<byte> pool = new(new NativePoolPreparation(7, 13, 3), budget);
        });
        Assert.Contains("Injected managed publication failure during NativePool.Preparation", failure.Message, StringComparison.Ordinal);
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(acquiredPages, snapshot.AllocationCount);
        Assert.Equal(acquiredPages, snapshot.FreeCount);
        Assert.Equal(0, snapshot.CommittedBytes);
        Assert.Equal(0, snapshot.ReservedBytes);
        Assert.Equal(0, snapshot.ActiveAllocationCount);
    }

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void HoldAll(NativePool<byte> pool, int remaining, int capacity)
    {
        using Pooled<byte> lease = pool.Rent(capacity, static writer => writer.Fill(7));
        if (remaining > 1) HoldAll(pool, remaining - 1, capacity);
        else
        {
            Assert.Equal(0, pool.CapturePreparedSnapshot().AvailableSlotCount);
            Assert.False(pool.TryRent(capacity, static writer => writer.Fill(0), out _, out NativePoolExhaustionReason reason));
            Assert.Equal(NativePoolExhaustionReason.NoAvailableSlot, reason);
        }
        Assert.Equal(7, lease.Read(static values => values[0]));
    }
}
