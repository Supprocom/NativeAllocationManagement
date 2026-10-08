using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedPackedPageTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ExactPackedPagesSupportOrdinaryUnmanagedLayoutsWithoutSlotOverlap(int kind)
    {
        switch (kind)
        {
            case 0: Verify((byte)17, (byte)19); break;
            case 1: Verify(23, 27); break;
            case 2: Verify(29L, 31L); break;
            case 3: Verify(31.5, 37.5); break;
            case 4: Verify(new ThreeBytes(37, 41, 43), new ThreeBytes(47, 53, 59)); break;
            case 5: Verify(Vector128.Create(47L, 53L), Vector128.Create(59L, 61L)); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static void Verify<T>(T value, T alternate) where T : unmanaged, IEquatable<T>
    {
        int slotBytes = checked(3 * Unsafe.SizeOf<T>());
        long totalBytes = checked(5L * slotBytes);
        NativeMemoryBudget budget = new(totalBytes);
        using NativePool<T> pool = new(new NativePoolPreparation(5, 3, 3), budget);
        Assert.Equal(totalBytes, pool.CapturePreparedSnapshot().RetainedBytes);
        Assert.Equal(totalBytes, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(2, pool.GetStatistics().FreshSegmentAllocationCount);
        object kernel = pool;
        Array slots = (Array)kernel.GetType().GetField("_slabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        Assert.Equal(slotBytes, Pointer(slots, 1) - Pointer(slots, 0));
        Assert.Equal(slotBytes, Pointer(slots, 2) - Pointer(slots, 1));
        Assert.Equal(slotBytes, Pointer(slots, 4) - Pointer(slots, 3));
        HoldSlots(pool, 5, value, alternate);
        Assert.Equal(5, pool.CapturePreparedSnapshot().PeakOccupiedSlotCount);
        Assert.Equal(5, pool.CapturePreparedSnapshot().AvailableSlotCount);
        Assert.Equal(0, pool.CapturePreparedSnapshot().OccupiedSlotCount);
        Assert.Equal((nuint)totalBytes, pool.TrimRetainedMemory());
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(2, budget.CaptureStatistics().FreeCount);
        Assert.False(pool.TryRent(3, writer => writer.Fill(value), out _, out NativePoolExhaustionReason reason));
        Assert.Equal(NativePoolExhaustionReason.NoAvailableSlot, reason);
    }

    private static long Pointer(Array slots, int index)
    {
        object slot = slots.GetValue(index)!;
        return ((IntPtr)slot.GetType().GetField("Pointer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(slot)!).ToInt64();
    }

    private static void HoldSlots<T>(NativePool<T> pool, int count, T value, T alternate) where T : unmanaged, IEquatable<T>
    {
        using Pooled<T> lease = pool.Rent(3, writer => writer.Fill(value));
        if (count > 1) HoldSlots(pool, count - 1, alternate, value);
        else Assert.Equal(5, pool.CapturePreparedSnapshot().OccupiedSlotCount);
        Assert.True(lease.Process(3, value, static (values, expected) =>
        {
            foreach (ref readonly T element in values)
                if (!element.Equals(expected)) return false;
            return true;
        }));
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly record struct ThreeBytes(byte A, byte B, byte C);
}
