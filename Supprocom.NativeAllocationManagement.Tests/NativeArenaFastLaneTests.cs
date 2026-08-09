namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeArenaFastLaneTests
{
    private static readonly NativeLeaseInitializer<int> FillSeven =
        static writer => writer.Fill(7);

    private static readonly NativeLeaseFunc<int, int> SumFour =
        static view => view[0] + view[1] + view[2] + view[3];

    [Fact]
    public void WarmScratchUsesNoManagedAllocationOrFreshSegment()
    {
        NativeMemoryTestHooks.Reset();
        using NativeArena arena = new(
            preAllocateBytes: 65_536,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> warm = arena.Scratch<int>(4, FillSeven);
        Assert.Equal(28, warm.Read(SumFour));

        NativeOwnerStatistics before = arena.GetStatistics();
        long allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int index = 0; index < 1_000; index++)
        {
            ArenaLease<int> lease = arena.Scratch<int>(4, FillSeven);
            checksum = checked(checksum + lease.Read(SumFour));
        }

        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        NativeOwnerStatistics after = arena.GetStatistics();

        Assert.Equal(28_000, checksum);
        Assert.Equal(0, allocated);
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(
            before.FreshSegmentAllocationCount,
            after.FreshSegmentAllocationCount);
    }

    [Fact]
    public void ScopedRecycleInvalidatesOnlyScopedLease()
    {
        using NativeArena arena = new(
            preAllocateBytes: 4_096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> ordinary = arena.Scratch<int>(
            1,
            static writer => writer.Write(17));
        ArenaLease<int> scoped = arena.ScratchScoped<int>(
            1,
            static writer => writer.Write(23));

        arena.RecycleScoped();

        Assert.Equal(17, ordinary.Read(static view => view[0]));
        Assert.IsType<NativeAllocationReturnedException>(
            CaptureRead(scoped));
    }

    [Fact]
    public void ResetInvalidatesOrdinaryAndScopedLeases()
    {
        using NativeArena arena = new(
            preAllocateBytes: 4_096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> ordinary = arena.Scratch<int>(
            1,
            static writer => writer.Write(17));
        ArenaLease<int> scoped = arena.ScratchScoped<int>(
            1,
            static writer => writer.Write(23));

        arena.Reset();

        Assert.IsType<NativeAllocationReturnedException>(
            CaptureRead(ordinary));
        Assert.IsType<NativeAllocationReturnedException>(
            CaptureRead(scoped));
    }

    [Fact]
    public void FailedInitializerRestoresCursorAndCapacity()
    {
        NativeMemoryTestHooks.Reset();
        using NativeArena arena = new(
            preAllocateBytes: 4_096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeOwnerStatistics before = arena.GetStatistics();

        Assert.Throws<InvalidOperationException>(
            () => arena.Scratch<int>(
                8,
                static writer =>
                {
                    writer.Write(9);
                    throw new InvalidOperationException(
                        "Expected initializer failure.");
                }));

        NativeOwnerStatistics afterFailure = arena.GetStatistics();
        ArenaLease<int> replacement = arena.Scratch<int>(
            8,
            static writer => writer.Fill(11));

        Assert.Equal(before.RequestedBytes, afterFailure.RequestedBytes);
        Assert.Equal(
            before.FreshSegmentAllocationCount,
            arena.GetStatistics().FreshSegmentAllocationCount);
        Assert.Equal(88, replacement.Read(static view =>
        {
            int sum = 0;
            for (int index = 0; index < view.Length; index++)
            {
                sum += view[index];
            }

            return sum;
        }));
    }

    [Fact]
    public void EmptyFailedInitializerDoesNotCorruptArena()
    {
        using NativeArena arena = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Assert.Throws<InvalidOperationException>(
            () => arena.Scratch<int>(
                0,
                static _ => throw new InvalidOperationException(
                    "Expected initializer failure.")));

        ArenaLease<int> lease = arena.Scratch<int>(
            1,
            static writer => writer.Write(31));
        Assert.Equal(31, lease.Read(static view => view[0]));
    }

    [Fact]
    public void LifetimeBoundariesRejectAnActiveBorrow()
    {
        using NativeArena arena = new(
            preAllocateBytes: 4_096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> lease = arena.Scratch<int>(
            1,
            static writer => writer.Write(5));

        lease.Access(_ =>
        {
            Assert.Throws<NativeAllocationInUseException>(arena.Reset);
            Assert.Throws<NativeAllocationInUseException>(
                arena.RecycleScoped);
            Assert.Throws<NativeAllocationInUseException>(arena.Dispose);
        });

        Assert.Equal(5, lease.Read(static view => view[0]));
    }

    [Fact]
    public void ArenaRejectsCrossThreadUse()
    {
        using NativeArena arena = new(
            preAllocateBytes: 4_096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Exception? exception = null;
        Thread thread = new(() =>
            exception = Record.Exception(() => arena.GetStatistics()));
        thread.Start();
        thread.Join();

        Assert.IsType<NativeAllocationStateException>(exception);
    }

    [Fact]
    public void TrimFreesUnusedTailSegments()
    {
        NativeMemoryTestHooks.Reset();
        using NativeArena arena = new(
            preAllocateBytes: 128,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        _ = arena.Scratch<byte>(
            8_192,
            static writer => writer.Fill(3));
        arena.Reset();
        NativeOwnerStatistics before = arena.GetStatistics();

        nuint released = arena.TrimRetainedMemory();
        NativeOwnerStatistics after = arena.GetStatistics();

        Assert.True(released >= 8_192);
        Assert.True(after.SegmentCount < before.SegmentCount);
        Assert.True(after.RetainedBytes < before.RetainedBytes);
    }

    [Fact]
    public void HeterogeneousRangesKeepExactValues()
    {
        using NativeArena arena = new(
            preAllocateBytes: 4_096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<byte> bytes = arena.Scratch<byte>(
            3,
            static writer => writer.Fill(0xA5));
        ArenaLease<long> longs = arena.Scratch<long>(
            2,
            static writer =>
            {
                writer.Write(long.MinValue);
                writer.Write(long.MaxValue);
            });
        ArenaLease<decimal> decimals = arena.Scratch<decimal>(
            2,
            static writer =>
            {
                writer.Write(1.25m);
                writer.Write(-9.5m);
            });

        Assert.Equal(0x1EF, bytes.Read(static view =>
            view[0] + view[1] + view[2]));
        Assert.True(longs.Read(static view =>
            view[0] == long.MinValue && view[1] == long.MaxValue));
        Assert.Equal(-8.25m, decimals.Read(static view =>
            view[0] + view[1]));
    }

    [Fact]
    public void DisposedArenaRejectsLeaseAccess()
    {
        NativeArena arena = new(
            preAllocateBytes: 4_096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> lease = arena.Scratch<int>(
            1,
            static writer => writer.Write(7));

        arena.Dispose();

        Assert.IsType<NativeAllocationDisposedException>(
            CaptureRead(lease));
    }

    private static Exception CaptureRead<T>(ArenaLease<T> lease)
        where T : unmanaged
    {
        try
        {
            _ = lease.Read(static view => view.Length);
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException(
            "Expected the Arena lease read to fail.");
    }
}
