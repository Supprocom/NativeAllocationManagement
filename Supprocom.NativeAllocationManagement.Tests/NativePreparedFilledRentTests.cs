using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedFilledRentTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(8, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(8, true)]
    public void EmptyPartialAndFullRangesPublishOnlyCompletelyFilledValues(int length, bool tryRent)
    {
        NativeMemoryBudget budget = new(64);
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(2, 8, 2), budget);
        PreparedPooled<int> lease;
        if (tryRent)
        {
            Assert.True(pool.TryRent(length, 42, out lease, out NativePoolExhaustionReason reason));
            Assert.Equal(NativePoolExhaustionReason.None, reason);
        }
        else lease = pool.Rent(length, 42);
        try
        {
            Assert.Equal(length, lease.Length);
            Assert.Equal(8, lease.Capacity);
            Assert.All(lease.Read(static view => view.AsSpan().ToArray()), static value => Assert.Equal(42, value));
            Assert.Equal(0, pool.CurrentInitializationCountForTest);
            NativePreparedPoolStatistics actual = pool.CapturePreparedSnapshot();
            Assert.Equal(1, actual.OccupiedSlotCount);
            Assert.Equal(1, actual.PeakOccupiedSlotCount);
            Assert.Equal(1, actual.SuccessfulRentCount);
            Assert.Equal(0, actual.InitializerFailureCount);
            Assert.Equal(length * 4, pool.GetStatistics().RequestedBytes);
            Assert.Equal(length * 4, pool.CaptureDiagnosticSnapshot().InitializedPayloadBytes);
        }
        finally { lease.Dispose(); }
        Assert.Equal(2, pool.CapturePreparedSnapshot().AvailableSlotCount);
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().ReallocationCount);
        pool.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void FullFillWorksForEachUnmanagedShapeWithoutNewOwnershipStorage()
    {
        CheckFilled<byte>(7);
        CheckFilled<ushort>(0x0707);
        CheckFilled(0x07070707);
        CheckFilled(0x0707070707070707L);
        CheckFilled(new Guid("07070707-0707-0707-0707-070707070707"));
        CheckFilled(new System.Numerics.Vector3(7, 11, 13));
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "This negative test asserts two capacity refusals publish default non-owning ref capabilities; their Dispose must throw NativeAllocationUninitializedException, not return storage. CA2000 cannot authenticate the asserted false outcomes. The only successful lease is using-scoped; no production or NAM ownership rule is suppressed.")]
    public void RefusalsAndInvalidLengthsDoNotTakeAuthorityOrChangeAFreeSlot()
    {
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(1, 2, 1), null);
        Assert.Throws<ArgumentOutOfRangeException>(() => { using PreparedPooled<int> invalid = pool.Rent(-1, 42); });
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.TryRent(-1, 42, out _, out _));
        Assert.False(pool.TryRent(3, 42, out PreparedPooled<int> wrongShape, out NativePoolExhaustionReason shape));
        Assert.Equal(NativePoolExhaustionReason.ShapeExceeded, shape);
        Assert.Equal(0, wrongShape.Length);
        try { wrongShape.Dispose(); Assert.Fail("A rejected default lease cannot return authority."); }
        catch (NativeAllocationUninitializedException) { }
        Assert.Equal(0L, Field(pool, "_leaseTokenCounter").GetValue(pool));
        using (PreparedPooled<int> current = pool.Rent(2, 7))
        {
            Assert.False(pool.TryRent(2, 42, out PreparedPooled<int> missing, out NativePoolExhaustionReason full));
            Assert.Equal(NativePoolExhaustionReason.NoAvailableSlot, full);
            Assert.Equal(0, missing.Length);
            try { missing.Dispose(); Assert.Fail("A rejected default lease cannot return authority."); }
            catch (NativeAllocationUninitializedException) { }
            Assert.Equal(1L, Field(pool, "_leaseTokenCounter").GetValue(pool));
            Assert.Equal(14, current.Read(static view => view[0] + view[1]));
        }
        NativePreparedPoolStatistics actual = pool.CapturePreparedSnapshot();
        Assert.Equal(1, actual.RejectedShapeCount);
        Assert.Equal(1, actual.RejectedFullCount);
        Assert.Equal(1, actual.SuccessfulRentCount);
        Assert.Equal(0, actual.InitializerFailureCount);
        Assert.Equal(1, actual.AvailableSlotCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TokenExhaustionLeavesBackingFreeAndPreviouslyWrittenValuesUntouched(bool tryRent)
    {
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(1, 2, 1), null);
        using (PreparedPooled<int> written = pool.Rent(2, 7)) { }
        NativePreparedPoolStatistics before = pool.CapturePreparedSnapshot();
        Field(pool, "_leaseTokenCounter").SetValue(pool, long.MaxValue);
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (tryRent) pool.TryRent(2, 42, out _, out _);
            else { using PreparedPooled<int> invalid = pool.Rent(2, 42); }
        });
        Assert.Equal(before, pool.CapturePreparedSnapshot());
        NativePreparedPoolStorage.Slot[] slots = (NativePreparedPoolStorage.Slot[])Field(pool, "_slabs").GetValue(pool)!;
        Assert.Equal(-1, slots[0].Next);
        Assert.Equal(7, System.Runtime.InteropServices.Marshal.ReadInt32(slots[0].Pointer));
        Assert.Equal(1, slots[0].Token);
    }

    [Fact]
    public void FilledAuthorityStillRejectsStaleAliasesAndReturnsItsExactSlot()
    {
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(1, 2, 1), null);
        PreparedPooled<int> source = pool.Rent(2, 7);
        PreparedPooled<int> stale = source;
        using (PreparedPooled<int> moved = PreparedPooled<int>.Move(ref source))
        {
            AssertReturned(stale);
            Assert.Equal(14, moved.Read(static view => view[0] + view[1]));
        }
        using PreparedPooled<int> reused = pool.Rent(2, 11);
        AssertReturned(stale);
        Assert.Equal(22, reused.Read(static view => view[0] + view[1]));
        Assert.Equal(2, pool.CapturePreparedSnapshot().SuccessfulRentCount);
        Assert.Equal(1, pool.CapturePreparedSnapshot().OccupiedSlotCount);
    }

    [Fact]
    public void FilledSlotStillRejectsReturnMoveAndPhysicalReleaseDuringAnEnteredBorrow()
    {
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(1, 2, 1), null);
        using PreparedPooled<int> lease = pool.Rent(2, 42);
        lease.Access(_ =>
        {
            Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
            Assert.Throws<InvalidOperationException>(() => pool.Return(0, 1, 2));
            Assert.Throws<InvalidOperationException>(() => pool.Move(0, 1));
            Assert.Throws<InvalidOperationException>(pool.Dispose);
            Assert.Equal((nuint)0, pool.TrimRetainedMemory());
        });
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(84, lease.Read(static view => view[0] + view[1]));
    }

    [Fact]
    public void ExistingCallbackCanNestFilledRentAndRetainItsOwnFailureRollback()
    {
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(2, 2, 1), null);
        Assert.Throws<OperationCanceledException>(() =>
        {
            using PreparedPooled<int> unpublished = pool.Rent(2, writer =>
            {
                using PreparedPooled<int> nested = pool.Rent(2, 42);
                Assert.Equal(1, pool.CurrentInitializationCountForTest);
                Assert.Equal(2, pool.CapturePreparedSnapshot().OccupiedSlotCount);
                Assert.Equal(84, nested.Read(static view => view[0] + view[1]));
                writer.Fill(7);
                throw new OperationCanceledException();
            });
        });
        NativePreparedPoolStatistics actual = pool.CapturePreparedSnapshot();
        Assert.Equal(2, actual.PeakOccupiedSlotCount);
        Assert.Equal(0, actual.OccupiedSlotCount);
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        Assert.Equal(1, actual.SuccessfulRentCount);
        Assert.Equal(1, actual.InitializerFailureCount);
        Assert.Equal(2, actual.AvailableSlotCount);
    }

    [Fact]
    public void WrongThreadAndTerminalOwnersCannotWriteOrPublishFilledSlots()
    {
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(1, 2, 1), null);
        NativePreparedPoolStatistics before = pool.CapturePreparedSnapshot();
        Exception? failure = null;
        Thread worker = new(() => failure = Record.Exception(() =>
        {
            using PreparedPooled<int> invalid = pool.Rent(2, 42);
        }));
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(nameof(NativePreparedPool<int>.Rent), Assert.IsType<NativeAllocationStateException>(failure).Operation);
        Assert.Equal(before, pool.CapturePreparedSnapshot());
        Assert.Equal((nuint)8, pool.TrimRetainedMemory());
        Assert.False(pool.TryRent(2, 42, out _, out NativePoolExhaustionReason reason));
        Assert.Equal(NativePoolExhaustionReason.NoAvailableSlot, reason);
        pool.Dispose();
        Assert.Throws<NativeAllocationDisposedException>(() => { using PreparedPooled<int> invalid = pool.Rent(2, 42); });
        Assert.Throws<NativeAllocationDisposedException>(() => pool.TryRent(2, 42, out _, out _));
    }

    [Fact]
    public void SaturatedRentHistoryDoesNotWrapOrChangeCleanupGauges()
    {
        NativeMemoryBudget budget = new(8);
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(1, 2, 1), budget);
        Field(pool, "_successfulPreparedRents").SetValue(pool, long.MaxValue);
        using (PreparedPooled<int> lease = pool.Rent(2, 7))
        {
            NativePreparedPoolStatistics actual = pool.CapturePreparedSnapshot();
            Assert.Equal(long.MaxValue, actual.SuccessfulRentCount);
            Assert.True(actual.HistoryOverflowed);
            Assert.Equal(1, actual.OccupiedSlotCount);
            Assert.Equal(8, pool.GetStatistics().RequestedBytes);
        }
        pool.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    private static void CheckFilled<T>(T value) where T : unmanaged
    {
        using NativePreparedPool<T> pool = new(new NativePoolPreparation(1, 8, 1), null);
        using PreparedPooled<T> lease = pool.Rent(8, value);
        Assert.All(lease.Read(static view => view.AsSpan().ToArray()), item => Assert.Equal(value, item));
        Assert.Equal(8, pool.GetStatistics().RequestedBytes / Unsafe.SizeOf<T>());
        Assert.Equal(24 + 40, pool.CapturePreparedSnapshot().ManagedBankBytes);
    }

    private static FieldInfo Field(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void AssertReturned(scoped PreparedPooled<int> value)
    {
        try { value.Dispose(); Assert.Fail("Stale authority cannot return an active filled slot."); }
        catch (NativeAllocationReturnedException) { }
    }
}
