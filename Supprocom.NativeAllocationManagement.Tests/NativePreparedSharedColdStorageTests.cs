using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedSharedColdStorageTests
{
    [Fact]
    public void AllTypedOwnersUseTheSameNongenericRecordsWithoutAnotherOwner()
    {
        Type[] owners =
        [
            typeof(NativePreparedPool<byte>), typeof(NativePreparedPool<ushort>),
            typeof(NativePreparedPool<int>), typeof(NativePreparedPool<long>),
            typeof(NativePreparedPool<Guid>), typeof(NativePreparedPool<Vector3>)
        ];
        Type slot = Metadata(owners[0], "_slabs");
        Type page = Metadata(owners[0], "_pages");
        foreach (Type owner in owners)
        {
            Assert.Equal(slot, Metadata(owner, "_slabs"));
            Assert.Equal(page, Metadata(owner, "_pages"));
            Assert.Null(owner.GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.Null(owner.GetField("_storage", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.Null(owner.GetNestedType("Slot", BindingFlags.NonPublic));
            Assert.Null(owner.GetNestedType("Page", BindingFlags.NonPublic));
        }
        Assert.False(slot.IsGenericType);
        Assert.False(page.IsGenericType);
        Assert.Equal(24, Unsafe.SizeOf<NativePreparedPoolStorage.Slot>());
        Assert.Equal(40, Unsafe.SizeOf<NativePreparedPoolStorage.Page>());
        Assert.Equal(4, slot.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Length);
        Assert.Equal(6, page.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Length);
        Type acquisition = typeof(NativePreparedPoolStorage);
        Assert.True(acquisition.IsAbstract && acquisition.IsSealed);
        Assert.Empty(acquisition.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        MethodInfo method = acquisition.GetMethod("Acquire", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.False(method.IsGenericMethod);
    }

    [Fact]
    public void SharedRecordsStillPublishExactTypedPayloadsAndAllOwnerHistories()
    {
        VerifyType<byte>();
        VerifyType<ushort>();
        VerifyType<int>();
        VerifyType<long>();
        VerifyType<Guid>();
        VerifyType<Vector3>();
    }

    [Fact]
    public void BackendRefusalKeepsActualFailureIdentityAndCancelsEveryCharge()
    {
        NativeMemoryBudget budget = new(48);
        NativeMemoryTestHooks.FailNextAllocation();
        NativeAllocationFailedException failure = Assert.Throws<NativeAllocationFailedException>(() =>
        {
            using NativePreparedPool<int> pool = new(new NativePoolPreparation(4, 3, 2), budget);
        });
        Assert.Equal("NativePreparedPool", failure.OwnerKind);
        Assert.Equal("page preparation", failure.Operation);
        Assert.Equal(NativeOwnerLifecycle.Active, failure.CurrentLifecycle);
        Assert.Equal((nuint)24, failure.RequestedBytes);
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(0, snapshot.ReservedBytes);
        Assert.Equal(0, snapshot.CommittedBytes);
        Assert.Equal(0, snapshot.AllocationCount);
        Assert.Equal(0, snapshot.FreeCount);
    }

    private static Type Metadata(Type owner, string field) =>
        owner.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType.GetElementType()!;

    private static void VerifyType<T>() where T : unmanaged
    {
        Span<byte> pattern = stackalloc byte[Unsafe.SizeOf<T>()];
        pattern.Fill(7);
        T value = MemoryMarshal.Read<T>(pattern);
        long bytes = 4L * 3 * Unsafe.SizeOf<T>();
        NativeMemoryBudget budget = new(bytes, traceCapacity: 16);
        using NativePreparedPool<T> pool = new(new NativePoolPreparation(4, 3, 2), budget);
        NativePreparedPoolStatistics before = pool.CapturePreparedSnapshot();
        Assert.Equal(bytes, before.RetainedBytes);
        Assert.Equal(bytes, before.PeakRetainedBytes);
        Assert.Equal(2, before.RetainedPageCount);
        Assert.Equal(4 * 24 + 2 * 40, before.ManagedBankBytes);
        Assert.Equal(2, pool.GetStatistics().FreshSegmentAllocationCount);
        Assert.Equal([1L, 2L], pool.CurrentSegmentOrdinalsForTest);
        using (PreparedPooled<T> lease = pool.Rent(3, writer => writer.Fill(value)))
        {
            Assert.True(lease.Process(3, 0, static (span, _) =>
            {
                foreach (ref readonly byte item in MemoryMarshal.AsBytes(span))
                {
                    if (item != 7) return false;
                }
                return true;
            }));
            Assert.Equal(1, pool.CapturePreparedSnapshot().OccupiedSlotCount);
        }
        Assert.Equal(1, pool.CapturePreparedSnapshot().SuccessfulRentCount);
        Assert.Equal((nuint)bytes, pool.TrimRetainedMemory());
        Assert.Equal(0, pool.CapturePreparedSnapshot().RetainedPageCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(2, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(2, budget.CaptureStatistics().FreeCount);
    }
}
