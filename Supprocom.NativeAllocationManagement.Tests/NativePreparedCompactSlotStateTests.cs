using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedCompactSlotStateTests
{
    [Fact]
    public void StateMarkersAreDisjointFromEveryPossibleFreeLink()
    {
        Assert.Equal(-2, NativePreparedPoolStorage.Initializing);
        Assert.Equal(-3, NativePreparedPoolStorage.Leased);
        Assert.NotEqual(NativePreparedPoolStorage.Initializing, NativePreparedPoolStorage.Leased);
        Assert.True(NativePreparedPoolStorage.Initializing < -1);
        Assert.True(NativePreparedPoolStorage.Leased < -1);
        Assert.True(int.MaxValue > NativePreparedPoolStorage.Initializing);
        Type storage = typeof(NativePreparedPoolStorage);
        FieldInfo[] fields = storage.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.Equal(2, fields.Length);
        foreach (FieldInfo field in fields) Assert.True(field.IsLiteral);
    }

    [Fact]
    public void NestedInitializationAndExceptionRollbackReconcileStateAndFreeLinks()
    {
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(3, 1, 1), budget: null);
        Assert.Throws<OperationCanceledException>(() =>
        {
            using PreparedPooled<int> unpublished = pool.Rent(1, writer =>
            {
                Assert.Equal(1, pool.CurrentInitializationCountForTest);
                Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
                Assert.Equal(-2, Slot(pool, 2).Next);
                using PreparedPooled<int> nested = pool.Rent(1, inner =>
                {
                    Assert.Equal(2, pool.CurrentInitializationCountForTest);
                    Assert.Equal(2, pool.CurrentGenerationActiveOperationsForTest);
                    Assert.Equal(-2, Slot(pool, 1).Next);
                    inner.Write(42);
                });
                Assert.Equal(-3, Slot(pool, 1).Next);
                nested.Access(_ =>
                {
                    Assert.Equal(2, pool.CurrentGenerationActiveOperationsForTest);
                    Assert.Throws<InvalidOperationException>(pool.Dispose);
                    Assert.Throws<InvalidOperationException>(pool.Retire);
                    Assert.Equal((nuint)4, pool.TrimRetainedMemory());
                });
                writer.Write(7);
                throw new OperationCanceledException();
            });
        });
        Assert.Equal(0, pool.CurrentInitializationCountForTest);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(0, pool.CapturePreparedSnapshot().OccupiedSlotCount);
        Assert.Equal(2, pool.CapturePreparedSnapshot().AvailableSlotCount);
        Assert.Equal(1, pool.CapturePreparedSnapshot().InitializerFailureCount);
        Assert.Equal(1, pool.CapturePreparedSnapshot().SuccessfulRentCount);
        Assert.Equal(1, Slot(pool, 2).Next);
        Assert.Equal(-1, Slot(pool, 1).Next);
        Assert.Equal(default, Slot(pool, 0));
        using PreparedPooled<int> recovered = pool.Rent(1, static writer => writer.Write(11));
        Assert.Equal(-3, Slot(pool, 2).Next);
        Assert.Equal(11, recovered.Read(static values => values[0]));
    }

    [Fact]
    public void RepeatedSparseTrimNeverPublishesZeroClearedSlotsOrStaleTokens()
    {
        NativeMemoryBudget budget = new(40);
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(5, 1, 2), budget);
        long returnedToken;
        using (PreparedPooled<int> first = pool.Rent(1, static writer => writer.Write(17)))
        {
            returnedToken = Slot(pool, 4).Token;
            Assert.Equal(-3, Slot(pool, 4).Next);
        }
        using (PreparedPooled<int> survivor = pool.Rent(1, static writer => writer.Write(29)))
        {
            Assert.NotEqual(returnedToken, Slot(pool, 4).Token);
            Assert.Throws<NativeAllocationReturnedException>(() => pool.Return(4, returnedToken, 1));
            for (int iteration = 0; iteration < 4; iteration++)
            {
                Assert.Equal((nuint)(iteration == 0 ? 16 : 0), pool.TrimRetainedMemory());
                Assert.Equal(1, pool.CapturePreparedSnapshot().RetainedSlotCount);
                Assert.Equal(0, pool.CapturePreparedSnapshot().AvailableSlotCount);
                Assert.Equal(0, pool.CurrentInitializationCountForTest);
                for (int index = 0; index < 4; index++) Assert.Equal(default, Slot(pool, index));
                Assert.False(pool.TryRent(1, static _ => Assert.Fail("A trimmed slot may not call its producer."),
                    out _, out NativePoolExhaustionReason reason));
                Assert.Equal(NativePoolExhaustionReason.NoAvailableSlot, reason);
                Assert.Equal(29, survivor.Read(static values => values[0]));
            }
        }
        Assert.Equal(-1, Slot(pool, 4).Next);
        Assert.Equal((nuint)4, pool.TrimRetainedMemory());
        Assert.Equal(default, Slot(pool, 4));
        Assert.Throws<NativeAllocationReturnedException>(() => pool.Return(4, returnedToken, 1));
        Assert.False(pool.TryRent(1, static _ => Assert.Fail("No retained backing remains."), out _, out _));
        Assert.Equal(0, pool.CapturePreparedSnapshot().AvailableSlotCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(3, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(3, budget.CaptureStatistics().FreeCount);
    }

    private static NativePreparedPoolStorage.Slot Slot(NativePreparedPool<int> pool, int index) =>
        ((NativePreparedPoolStorage.Slot[])typeof(NativePreparedPool<int>)
            .GetField("_slabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!)[index];
}
