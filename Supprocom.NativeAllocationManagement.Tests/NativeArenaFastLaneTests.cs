namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeArenaFastLaneTests
{
    [Fact]
    public void WarmUnmanagedScratchUsesNoRecordsAllocationsOrSlowPaths()
    {
        NativeMemoryTestHooks.Reset();
        using NativeArena arena = new(
            preAllocateBytes: 65_536,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeLeaseInitializer<int> fillSeven =
            static writer => writer.Fill(7);
        NativeLeaseFunc<int, int> sumFour =
            static view =>
                view[0] + view[1] + view[2] + view[3];
        ArenaLease<int> warm = arena.Scratch<int>(
            4,
            fillSeven);
        Assert.Equal(28, warm.Read(sumFour));

        NativeOwnerStatistics before = arena.GetStatistics();
        NativeMemoryTestMetrics hooksBefore =
            NativeMemoryTestHooks.Snapshot();
        long slowPathsBefore =
            arena.CurrentFastLaneSlowPathCountForTest;
        long allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int index = 0; index < 1_000; index++)
        {
            ArenaLease<int> lease = arena.Scratch<int>(
                4,
                fillSeven);
            checksum = checked(
                checksum
                + lease.Read(sumFour));
        }

        long allocatedBytes =
            GC.GetAllocatedBytesForCurrentThread()
            - allocatedBefore;
        NativeOwnerStatistics after = arena.GetStatistics();
        NativeMemoryTestMetrics hooksAfter =
            NativeMemoryTestHooks.Snapshot();

        Assert.Equal(28_000, checksum);
        Assert.Equal(0, allocatedBytes);
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(
            slowPathsBefore,
            arena.CurrentFastLaneSlowPathCountForTest);
        Assert.Equal(
            hooksBefore.BumpTraversalVisitCount,
            hooksAfter.BumpTraversalVisitCount);
        Assert.Equal(
            before.FreshSegmentAllocationCount,
            after.FreshSegmentAllocationCount);
    }

    [Fact]
    public void WarmUnmanagedScopedScratchUsesNoRecordsAllocationsOrSlowPaths()
    {
        NativeMemoryTestHooks.Reset();
        using NativeArena arena = new(
            preAllocateBytes: 65_536,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeLeaseInitializer<int> fillSeven =
            static writer => writer.Fill(7);
        NativeLeaseFunc<int, int> sumFour =
            static view =>
                view[0] + view[1] + view[2] + view[3];
        ArenaLease<int> warm = arena.ScratchScoped<int>(
            4,
            fillSeven);
        Assert.Equal(28, warm.Read(sumFour));
        arena.RecycleScoped();
        ArenaLease<int> reusedWarm = arena.ScratchScoped<int>(
            4,
            fillSeven);
        Assert.Equal(28, reusedWarm.Read(sumFour));
        arena.RecycleScoped();

        NativeOwnerStatistics before = arena.GetStatistics();
        NativeMemoryTestMetrics hooksBefore =
            NativeMemoryTestHooks.Snapshot();
        long slowPathsBefore =
            arena.CurrentFastLaneSlowPathCountForTest;
        long allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int index = 0; index < 1_000; index++)
        {
            ArenaLease<int> lease = arena.ScratchScoped<int>(
                4,
                fillSeven);
            checksum = checked(
                checksum
                + lease.Read(sumFour));
            arena.RecycleScoped();
        }

        long allocatedBytes =
            GC.GetAllocatedBytesForCurrentThread()
            - allocatedBefore;
        NativeOwnerStatistics after = arena.GetStatistics();
        NativeMemoryTestMetrics hooksAfter =
            NativeMemoryTestHooks.Snapshot();

        Assert.Equal(28_000, checksum);
        Assert.Equal(0, allocatedBytes);
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(before.RequestedBytes, after.RequestedBytes);
        Assert.Equal(
            slowPathsBefore,
            arena.CurrentFastLaneSlowPathCountForTest);
        Assert.Equal(
            hooksBefore.BumpTraversalVisitCount,
            hooksAfter.BumpTraversalVisitCount);
        Assert.Equal(
            before.FreshSegmentAllocationCount,
            after.FreshSegmentAllocationCount);
    }

    [Fact]
    public void ScopedRecycleInvalidatesFastHandlesOnly()
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
        long scopeEpoch =
            arena.CaptureDiagnosticSnapshot().ScopeEpoch;

        arena.RecycleScoped();

        Assert.Equal(17, ordinary[0]);
        Assert.IsType<NativeAllocationReturnedException>(
            CaptureReturned(scoped));
        Assert.Equal(
            scopeEpoch + 1,
            arena.CaptureDiagnosticSnapshot().ScopeEpoch);

        ArenaLease<int> empty = arena.ScratchScoped<int>(
            0,
            static writer => writer.Fill(default!));
        arena.RecycleScoped();
        Assert.IsType<NativeAllocationReturnedException>(
            CaptureReturned(empty));
    }

    [Fact]
    public void FailedFastInitializerRestoresTheWarmCursor()
    {
        NativeMemoryTestHooks.Reset();
        using NativeArena arena = new(
            preAllocateBytes: 4_096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> warm = arena.Scratch<int>(
            1,
            static writer => writer.Write(3));
        Assert.Equal(3, warm[0]);
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

        Assert.Equal(
            before.RequestedBytes,
            afterFailure.RequestedBytes);
        Assert.Equal(11, replacement[0]);
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(
            before.FreshSegmentAllocationCount,
            arena.GetStatistics().FreshSegmentAllocationCount);
    }

    [Fact]
    public void FastLaneDoesNotCrossTheScopedHighCursor()
    {
        NativeMemoryTestHooks.Reset();
        using NativeArena arena = new(
            preAllocateBytes: 4_096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<byte> first = arena.Scratch<byte>(
            3_000,
            static writer => writer.Fill(3));
        ArenaLease<byte> scoped = arena.ScratchScoped<byte>(
            512,
            static writer => writer.Fill(41));
        NativeOwnerStatistics beforeGrowth = arena.GetStatistics();

        ArenaLease<byte> next = arena.Scratch<byte>(
            700,
            static writer => writer.Fill(7));

        Assert.Equal(3, first[0]);
        Assert.Equal(3, first[2_999]);
        Assert.Equal(7, next[0]);
        Assert.Equal(7, next[699]);
        Assert.Equal(
            20_992,
            scoped.Read(static view =>
            {
                int sum = 0;
                for (int index = 0; index < view.Length; index++)
                {
                    sum += view[index];
                }

                return sum;
            }));
        Assert.Equal(
            beforeGrowth.FreshSegmentAllocationCount + 1,
            arena.GetStatistics().FreshSegmentAllocationCount);
    }

    private static Exception CaptureReturned<T>(
        ArenaLease<T> lease)
    {
        try
        {
            _ = lease.Length;
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException(
            "Expected a returned Arena lease.");
    }
}
