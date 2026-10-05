using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeFastArenaIntegrationTests
{
    [Fact]
    public void MappedLanesChargeOnlyOneRealHeaderAllocationAndKeepTheProviderAlive()
    {
        using AlignedBuffer buffer = new(256);
        NativeMemoryBudget budget = new(128);
        using NativeArena arena = new(buffer, 64, new NativeArenaPreparation(64, 64), budget);
        buffer.Dispose();
        Assert.Equal(0, buffer.ReleaseCount);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(128, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(128, arena.GetStatistics().BorrowedBytes);
        Assert.Equal(0, arena.GetStatistics().UsableCapacityBytes);
        Assert.Equal(128, arena.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
        Assert.Equal(128, arena.CaptureDiagnosticSnapshot().PeakOutstandingNativeBytes);
        Assert.Equal(arena.GetStatistics().OutstandingNativeBytes, arena.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
        ArenaLease<int> source = arena.Scratch<int>(1, static writer => writer.Write(17));
        Assert.Equal(17, source.Read(static view => view[0]));
        arena.Dispose();
        Assert.Equal(1, buffer.ReleaseCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, arena.CapturePreparedSnapshot().RetainedBorrowedBytes);
        Assert.Equal(0, arena.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
        Assert.Equal(0, arena.CaptureDiagnosticSnapshot().DetachedNativeBytes);
        Assert.Equal(128, arena.CaptureDiagnosticSnapshot().PeakOutstandingNativeBytes);
    }

    [Fact]
    public void PartialTrimReportsTheRangeAndHeadersThatRemainPhysicallyHeld()
    {
        using AlignedBuffer buffer = new(128);
        NativeMemoryBudget budget = new(128);
        using NativeArena arena = new(buffer, 0, new NativeArenaPreparation(64, 64), budget);
        _ = arena.Scratch<int>(1, static writer => writer.Write(1));
        buffer.Dispose();
        Assert.Equal((nuint)0, arena.TrimRetainedMemory());
        NativePreparedArenaStatistics partial = arena.CapturePreparedSnapshot();
        Assert.Equal(64, partial.ActiveBorrowedBytes);
        Assert.Equal(128, partial.RetainedBorrowedBytes);
        Assert.Equal(128, partial.RetainedBytes);
        Assert.Equal(128, arena.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
        Assert.Equal(128, arena.CaptureDiagnosticSnapshot().PeakOutstandingNativeBytes);
        Assert.Equal(0, partial.ScopedAvailableBytes);
        Assert.False(arena.TryScratchScoped<int>(1, static writer => writer.Write(1), out _));
        Assert.Equal(0, buffer.ReleaseCount);
        arena.Reset();
        Assert.Equal((nuint)128, arena.TrimRetainedMemory());
        Assert.Equal(1, buffer.ReleaseCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, arena.CapturePreparedSnapshot().RetainedBorrowedBytes);
        Assert.Equal(0, arena.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
        Assert.Equal(128, arena.CaptureDiagnosticSnapshot().PeakOutstandingNativeBytes);
    }

    [Theory]
    [InlineData(1, 64, 64)]
    [InlineData(192, 64, 64)]
    [InlineData(0, 1, 64)]
    public void InvalidMappedRangesPublishNoBackingAndReleaseTheirTemporaryHold(int offset, int ordinary, int scoped)
    {
        using AlignedBuffer buffer = new(256);
        NativeMemoryBudget budget = new(128);
        Assert.ThrowsAny<ArgumentException>(() => new NativeArena(buffer, (nuint)offset,
            new NativeArenaPreparation((nuint)ordinary, (nuint)scoped), budget));
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        buffer.Dispose();
        Assert.Equal(1, buffer.ReleaseCount);
    }

    [Fact]
    public void HeaderFailureReleasesTheMappedHoldAndCancelsAdmission()
    {
        using AlignedBuffer buffer = new(128);
        NativeMemoryBudget budget = new(128);
        NativeMemoryTestHooks.FailNextAllocation();
        Assert.Throws<NativeAllocationFailedException>(() =>
            new NativeArena(buffer, 0, new NativeArenaPreparation(64, 64), budget));
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        buffer.Dispose();
        Assert.Equal(1, buffer.ReleaseCount);
    }

    [Fact]
    public void GroupsPublishAllRangesAndReuseThePreparedLaneWithoutAllocating()
    {
        using AlignedBuffer buffer = new(192);
        NativeMemoryBudget budget = new(128);
        using NativeArena arena = new(buffer, 0, new NativeArenaPreparation(64, 128), budget);
        ArenaLease<int> source = arena.Scratch<int>(1, static writer => writer.Write(17));
        NativeLeaseSourceQuadSpanInitializer<int, int, long, byte, int> fill =
            static (input, first, second, third, fourth) =>
            {
                first.Fill(input[0]);
                second.Fill(42);
                third.Fill(3);
                fourth.Fill(7);
            };
        NativeLeaseQuintupleAction<int, int, long, byte, int> verify =
            static (input, first, second, third, fourth) =>
            {
                if (input[0] != first[0] || second[0] != 42 || third[0] != 3 || fourth[0] != 7)
                {
                    throw new InvalidOperationException("Grouped output parity failed.");
                }
            };
        RunGroup(source, arena, fill, verify);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            RunGroup(source, arena, fill, verify);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(4_005, arena.CapturePreparedSnapshot().SuccessfulScratchCount);
    }

    [Fact]
    public void ThrowingGroupRollsBackEveryRangeAndPublishesNoSubset()
    {
        NativeMemoryBudget budget = new(512);
        using NativeArena arena = new(new NativeArenaPreparation(16, 64), budget);
        ArenaLease<int> source = arena.Scratch<int>(1, static writer => writer.Write(17));
        bool failed = false;
        try
        {
            NativeLeaseOperations.InitializeScoped<int, int, long, byte, int>(source, arena, 1, 1, 1, 1,
                static (_, first, _, _, _) =>
                {
                    first.Fill(1);
                    throw new InvalidOperationException("Injected group initializer failure.");
                }, out _, out _, out _, out _);
        }
        catch (InvalidOperationException exception) when (string.Equals(exception.Message,
            "Injected group initializer failure.", StringComparison.Ordinal))
        {
            failed = true;
        }
        Assert.True(failed);
        Assert.Equal(0, arena.CapturePreparedSnapshot().ScopedUsedBytes);
        Assert.Equal(1, arena.CapturePreparedSnapshot().InitializerFailureCount);
        Assert.Equal(1, arena.CapturePreparedSnapshot().SuccessfulScratchCount);
        Assert.True(arena.TryScratchScoped<long>(8, static writer => writer.Fill(42), out _));
        arena.RecycleScoped();
    }

    [Fact]
    public void LaterGroupCapacityFailureRollsBackBeforeInvokingTheProducer()
    {
        NativeMemoryBudget budget = new(512);
        using NativeArena arena = new(new NativeArenaPreparation(16, 16), budget);
        ArenaLease<int> source = arena.Scratch<int>(1, static writer => writer.Write(17));
        bool invoked = false;
        bool failed = false;
        try
        {
            NativeLeaseOperations.InitializeScoped<int, int, long, byte, int>(source, arena, 1, 1, 1, 1,
                (_, _, _, _, _) => invoked = true, out _, out _, out _, out _);
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }
        Assert.True(failed);
        Assert.False(invoked);
        Assert.Equal(0, arena.CapturePreparedSnapshot().ScopedUsedBytes);
        Assert.Equal(1, arena.CapturePreparedSnapshot().RejectedCapacityCount);
        Assert.Equal(0, arena.CapturePreparedSnapshot().InitializerFailureCount);
    }

    [Fact]
    public void CompositeStaleValidationIsAtomicAndDoesNotLeaveAnEnteredBorrow()
    {
        NativeMemoryBudget budget = new(512);
        using NativeArena arena = new(new NativeArenaPreparation(16, 16), budget);
        ArenaLease<int> source = arena.Scratch<int>(1, static writer => writer.Write(17));
        ArenaLease<int> stale = arena.ScratchScoped<int>(1, static writer => writer.Write(1));
        arena.RecycleScoped();
        bool invoked = false;
        bool failed = false;
        try
        {
            NativeLeaseOperations.Access(source, stale, (_, _) => invoked = true);
        }
        catch (NativeAllocationReturnedException)
        {
            failed = true;
        }
        Assert.True(failed);
        Assert.False(invoked);
        arena.Reset();
    }

    [Fact]
    public void NativeAddressOverflowIsRejectedBeforeHeadersOrPayloadCanBePublished()
    {
        using AddressOnlyBuffer buffer = new(-64, 256);
        NativeMemoryBudget budget = new(128);
        Assert.Throws<OverflowException>(() => new NativeArena(buffer, 0,
            new NativeArenaPreparation(128, 0), budget));
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        buffer.Dispose();
        Assert.Equal(1, buffer.ReleaseCount);
    }

    [Fact]
    public void DeclaredCapacityOverflowNeverAcquiresTheProvider()
    {
        using AlignedBuffer buffer = new(128);
        NativeMemoryBudget budget = new(128);
        Assert.Throws<OverflowException>(() => new NativeArena(buffer, 0,
            new NativeArenaPreparation(nuint.MaxValue, 1), budget));
        Assert.Equal(0, budget.CaptureStatistics().PeakAdmittedBytes);
        buffer.Dispose();
        Assert.Equal(1, buffer.ReleaseCount);
    }

    [Fact]
    public void DifferentOwnersAndDefaultCapabilitiesCannotExposeAPartialComposite()
    {
        NativeMemoryBudget budget = new(512);
        using NativeArena first = new(new NativeArenaPreparation(16, 0), budget);
        using NativeArena second = new(new NativeArenaPreparation(16, 0), budget);
        ArenaLease<int> left = first.Scratch<int>(1, static writer => writer.Write(1));
        ArenaLease<int> right = second.Scratch<int>(1, static writer => writer.Write(2));
        bool invoked = false;
        bool rejected = false;
        try
        {
            NativeLeaseOperations.Access(left, right, (_, _) => invoked = true);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        Assert.True(rejected);
        Assert.False(invoked);
        rejected = false;
        try
        {
            NativeLeaseOperations.Access(left, default(ArenaLease<int>), (_, _) => invoked = true);
        }
        catch (NativeAllocationUninitializedException)
        {
            rejected = true;
        }
        Assert.True(rejected);
        Assert.False(invoked);
        first.Reset();
        second.Reset();
    }

    [Fact]
    public void GroupCallbacksCannotRecycleDisposeOrStartANestedInitializer()
    {
        NativeMemoryBudget budget = new(512);
        using NativeArena arena = new(new NativeArenaPreparation(16, 64), budget);
        ArenaLease<int> source = arena.Scratch<int>(1, static writer => writer.Write(17));
        NativeLeaseOperations.Access(source, source, (_, _) =>
        {
            Assert.Throws<NativeAllocationInUseException>(arena.RecycleScoped);
            Assert.Throws<NativeAllocationInUseException>(arena.Dispose);
        });
        NativeLeaseOperations.InitializeScoped<int, int, int, int, int>(source, arena, 1, 1, 1, 1,
            (_, a, b, c, d) =>
            {
                Assert.Throws<NativeAllocationInUseException>(arena.RecycleScoped);
                Assert.Throws<NativeAllocationInUseException>(arena.Dispose);
                Assert.Throws<InvalidOperationException>(() => { arena.Scratch<int>(1, static writer => writer.Write(1)); });
                a.Fill(1); b.Fill(2); c.Fill(3); d.Fill(4);
            }, out _, out _, out _, out _);
        arena.RecycleScoped();
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The test worker records any exception for assertion on the owner thread instead of letting an unhandled worker exception terminate the test process.")]
    public void PreparedMappedExecutionRejectsTheWrongThreadBeforeItsProducer()
    {
        using AlignedBuffer buffer = new(128);
        NativeMemoryBudget budget = new(128);
        using NativeArena arena = new(buffer, 0, new NativeArenaPreparation(64, 64), budget);
        bool invoked = false;
        Exception? failure = null;
        Thread worker = new(() =>
        {
            try
            {
                arena.TryScratch<int>(1, writer => { invoked = true; writer.Write(1); }, out _);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        Assert.IsType<NativeAllocationStateException>(failure);
        Assert.False(invoked);
        Assert.Equal(0, arena.CapturePreparedSnapshot().SuccessfulScratchCount);
    }

    private static void RunGroup(scoped ArenaLease<int> source, NativeArena arena,
        NativeLeaseSourceQuadSpanInitializer<int, int, long, byte, int> fill,
        NativeLeaseQuintupleAction<int, int, long, byte, int> verify)
    {
        NativeLeaseOperations.InitializeScoped(source, arena, 1, 1, 1, 1, fill,
            out ArenaLease<int> first, out ArenaLease<long> second,
            out ArenaLease<byte> third, out ArenaLease<int> fourth);
        try
        {
            NativeLeaseOperations.Access(source, first, second, third, fourth, verify);
        }
        finally
        {
            arena.RecycleScoped();
        }
    }

    private sealed class AddressOnlyBuffer : SafeBuffer
    {
        internal AddressOnlyBuffer(nint address, ulong byteLength) : base(ownsHandle: true)
        {
            SetHandle(address);
            Initialize(byteLength);
        }

        internal int ReleaseCount { get; private set; }

        protected override bool ReleaseHandle()
        {
            // This provider supplies only an invalid test address, never storage.
            ReleaseCount++;
            return true;
        }
    }

    private sealed unsafe class AlignedBuffer : SafeBuffer
    {
        internal AlignedBuffer(nuint length) : base(ownsHandle: true)
        {
            void* pointer = NativeMemory.AlignedAlloc(length, 64);
            if (pointer == null)
            {
                throw new InvalidOperationException("Test buffer allocation failed.");
            }
            SetHandle((IntPtr)pointer);
            Initialize(checked((ulong)length));
        }

        internal int ReleaseCount { get; private set; }

        protected override bool ReleaseHandle()
        {
            NativeMemory.AlignedFree((void*)handle);
            ReleaseCount++;
            return true;
        }
    }
}
