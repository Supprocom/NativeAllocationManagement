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
        using NativePreparedPool<byte> pool = new(new NativePoolPreparation(slotCount, capacity, slotsPerPage), budget);
        Array slots = (Array)Field(pool, "_slabs");
        Assert.Equal(slotCount, slots.Length);
        Assert.Equal(slotCount - 1, (int)Field(pool, "_freeHead"));
        Assert.Null(pool.GetType().GetField("_freeHeads", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(pool.GetType().GetField("_nonEmptyFreeClasses", BindingFlags.Instance | BindingFlags.NonPublic));
        for (int index = 0; index < slots.Length; index++)
        {
            object slot = slots.GetValue(index)!;
            Assert.NotEqual(IntPtr.Zero, (IntPtr)Field(slot, "Pointer"));
            Assert.Equal(index - 1, (int)Field(slot, "Next"));
            Assert.Null(slot.GetType().GetField("State", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.Equal(0L, (long)Field(slot, "Token"));
            Assert.Equal(0, (int)Field(slot, "BorrowCount"));
            Assert.Equal(4, slot.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Length);
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
            using NativePreparedPool<byte> pool = new(new NativePoolPreparation(7, 13, 3), budget);
        });
        Assert.Contains("Injected managed publication failure during NativePreparedPool.Preparation", failure.Message, StringComparison.Ordinal);
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(acquiredPages, snapshot.AllocationCount);
        Assert.Equal(acquiredPages, snapshot.FreeCount);
        Assert.Equal(0, snapshot.CommittedBytes);
        Assert.Equal(0, snapshot.ReservedBytes);
        Assert.Equal(0, snapshot.ActiveAllocationCount);
    }

    [Theory]
    [InlineData(7, 1001, 3)]
    [InlineData(17, 1, 4)]
    [InlineData(64, 4096, 16)]
    public void EveryTypedSlotAddressEqualsItsIndependentPageExtent(int slotCount, int capacity, int slotsPerPage)
    {
        CheckAddresses<byte>(slotCount, capacity, slotsPerPage);
        CheckAddresses<ushort>(slotCount, capacity, slotsPerPage);
        CheckAddresses<int>(slotCount, capacity, slotsPerPage);
        CheckAddresses<long>(slotCount, capacity, slotsPerPage);
        CheckAddresses<Guid>(slotCount, capacity, slotsPerPage);
        CheckAddresses<System.Numerics.Vector3>(slotCount, capacity, slotsPerPage);
    }

    private static void CheckAddresses<T>(int slotCount, int capacity, int slotsPerPage)
        where T : unmanaged
    {
        int elementSize = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        long stride = checked((long)capacity * elementSize);
        NativeMemoryBudget budget = new(checked(stride * slotCount));
        using NativePreparedPool<T> pool = new(new NativePoolPreparation(slotCount, capacity, slotsPerPage), budget);
        Array slots = (Array)Field(pool, "_slabs");
        Array pages = (Array)Field(pool, "_pages");
        int observed = 0;
        foreach (object page in pages)
        {
            long pageBase = ((IntPtr)Field(page, "Pointer")).ToInt64();
            int first = (int)Field(page, "FirstSlot");
            int count = (int)Field(page, "SlotCount");
            Assert.Equal(observed, first);
            Assert.Equal(checked((nuint)(stride * count)), (nuint)Field(page, "AllocationBytes"));
            for (int offset = 0; offset < count; offset++)
            {
                object slot = slots.GetValue(first + offset)!;
                Assert.Equal(checked(pageBase + stride * offset), ((IntPtr)Field(slot, "Pointer")).ToInt64());
                Assert.Equal(first + offset - 1, (int)Field(slot, "Next"));
                Assert.Equal(0L, (long)Field(slot, "Token"));
                Assert.Equal(0, (int)Field(slot, "BorrowCount"));
                observed++;
            }
        }
        Assert.Equal(slotCount, observed);
        Assert.Equal(checked(stride * slotCount), budget.CaptureStatistics().CommittedBytes);
        pool.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(budget.CaptureStatistics().AllocationCount, budget.CaptureStatistics().FreeCount);
    }

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void HoldAll(NativePreparedPool<byte> pool, int remaining, int capacity)
    {
        using PreparedPooled<byte> lease = pool.Rent(capacity, static writer => writer.Fill(7));
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
