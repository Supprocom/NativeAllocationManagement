using System.Reflection;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeSynchronizedBackingCorrelationTests
{
    [Fact]
    public void PoolReuseKeepsThePhysicalIdentityAndEveryFreeMatchesItsAcquisition()
    {
        NativeMemoryBudget budget = new(4096, traceCapacity: 64);
        using NativeConcurrentPool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory, false);
        using ConcurrentPooled<int> first = pool.Rent(1, static writer => writer.Write(11));
        using ConcurrentPooled<int> second = pool.Rent(1, static writer => writer.Write(42));
        Assert.Equal(2, budget.CaptureStatistics().AllocationCount);
        first.Dispose();
        using ConcurrentPooled<int> reused = pool.Rent(1, static writer => writer.Write(7));
        Assert.Equal(7, reused.Read(static view => view[0]));
        Assert.Equal(42, second.Read(static view => view[0]));
        Assert.Equal(2, budget.CaptureStatistics().AllocationCount);
        reused.Dispose();
        second.Dispose();
        pool.Dispose();
        AssertPhysicalPairs(budget, pool.Id, 2);
    }

    [Fact]
    public void ArenaGenerationsDoNotResetBackingIdentityOrInventAllocationOnReuse()
    {
        NativeMemoryBudget budget = new(16_384, traceCapacity: 64);
        using NativeConcurrentArena arena = new(budget, 64, NativeMemoryReturn.ToNativeMemory, false);
        ConcurrentArenaLease<int> first = arena.Scratch<int>(1, static writer => writer.Write(11));
        Assert.Equal(11, first.Read(static view => view[0]));
        arena.ReleaseLeasesToNativeMemory();
        // Quiescent rollover transfers the same backing into its successor.
        Assert.Equal(64, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        ConcurrentArenaLease<int> second = arena.Scratch<int>(1, static writer => writer.Write(42));
        Assert.Equal(42, second.Read(static view => view[0]));
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        _ = arena.Scratch<int>(1024, static writer => writer.Fill(7));
        Assert.Equal(2, budget.CaptureStatistics().AllocationCount);
        arena.Dispose();
        AssertPhysicalPairs(budget, arena.Id, 2);
    }

    [Fact]
    public void FailedAcquisitionDoesNotConsumeTheExistingSegmentSequence()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeMemoryBudget budget = new(128, traceCapacity: 16);
            NativeOwnerBackingHistory history = new(17);
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => NativeSegment.Allocate(64, "CorrelationTest", 0,
                "failed acquisition", NativeOwnerLifecycle.Active, false, budget, backingHistory: history, allocationOrdinal: 1));
            NativeSegment acquired = NativeSegment.Allocate(64, "CorrelationTest", 0,
                "successful acquisition", NativeOwnerLifecycle.Active, false, budget, backingHistory: history, allocationOrdinal: 1);
            acquired.FreeNow();
            AssertPhysicalPairs(budget, 17, 1);
            Assert.Equal(1, budget.CaptureStatistics().FailedAllocationCount);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void SynchronizedOrdinalExhaustionOccursBeforeBudgetAdmission()
    {
        NativeMemoryBudget budget = new(4096, traceCapacity: 16);
        using NativeConcurrentPool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory, false);
        typeof(NativeOwnerKernel).GetField("_nextSegmentOrdinal", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(pool.KernelForTransfer, long.MaxValue);
        NativeMemoryBudgetStatistics before = budget.CaptureStatistics();
        Assert.Throws<OverflowException>(() => pool.Rent(1, static writer => writer.Write(11)));
        Assert.Equal(before, budget.CaptureStatistics());
    }

    [Fact]
    public void ExternalOrdinalExhaustionDoesNotAcquireAProviderReference()
    {
        using AddressOnlyBuffer buffer = new();
        using NativeConcurrentArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeOwnerKernel kernel = Assert.IsType<NativeOwnerKernel>(typeof(NativeConcurrentArena)
            .GetField("_kernel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(arena));
        typeof(NativeOwnerKernel).GetField("_nextSegmentOrdinal", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(kernel, long.MaxValue);
        Assert.Throws<OverflowException>(() => arena.ReserveExternalMemory(buffer, 0, 64));
        buffer.Dispose();
        Assert.Equal(1, buffer.ReleaseCount);
        Assert.Equal(0, arena.GetStatistics().BorrowedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedAndExpiredWeakEventsKeepTheOriginalBackingIdentity(bool pooled)
    {
        NativeMemoryBudget budget = new(4096, traceCapacity: 64);
        using NativeConcurrentPool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory, false);
        using NativeBuilder<int> builder = new(budget, 0);
        NativeTransfer<int>? unique;
        long ownerId;
        if (pooled)
        {
            unique = pool.RentTransferable(4, static writer => writer.Fill(42));
            ownerId = pool.Id;
        }
        else
        {
            builder.Append(42);
            unique = builder.Complete();
            ownerId = builder.Id;
        }
        NativeShared<int> owner = NativeShared<int>.Create(ref unique, new(2, 1));
        long sharingId = owner.Id;
        Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
        Assert.True(weak.TryUpgrade(out NativeShared<int> upgraded, out _));
        upgraded.Dispose();
        owner.Dispose();
        Assert.False(weak.TryUpgrade(out _, out _));
        weak.Dispose();
        pool.Dispose();
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[64];
        int count = budget.CopyTraceTo(events);
        int sharedEvents = 0;
        int uniqueEvents = 0;
        foreach (ref readonly NativeMemoryTraceEvent entry in events[..count])
        {
            if (entry.CorrelationId == sharingId)
            {
                Assert.Equal(ownerId, entry.OwnerId);
                Assert.Equal(1L, entry.AllocationOrdinal);
                sharedEvents++;
            }
            if (entry.Kind is NativeMemoryTraceKind.Moved or NativeMemoryTraceKind.UniqueReturned)
            {
                Assert.Equal(ownerId, entry.OwnerId);
                Assert.Equal(1L, entry.AllocationOrdinal);
                uniqueEvents++;
            }
        }
        Assert.True(sharedEvents >= 7);
        Assert.Equal(2, uniqueEvents);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void ProducerPermissionUsesNoBackingIdentityUntilStorageReallyExists()
    {
        NativeMemoryBudget budget = new(64, traceCapacity: 32);
        Assert.True(budget.TryReserve<int>(4, out NativeMemoryReservation<int>? permission, out _));
        permission = NativeMemoryReservation<int>.Move(ref permission);
        permission.Value.PrepareBacking();
        permission = NativeMemoryReservation<int>.Move(ref permission);
        NativeTransfer<int> owner = NativeMemoryReservation<int>.Activate(ref permission, static writer => writer.Fill(42));
        owner.Dispose();
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[32];
        int count = budget.CopyTraceTo(events);
        bool prepared = false;
        int moves = 0;
        foreach (ref readonly NativeMemoryTraceEvent entry in events[..count])
        {
            if (entry.Kind == NativeMemoryTraceKind.ReservationMoved)
            {
                Assert.Equal(prepared ? 1L : (long?)null, entry.AllocationOrdinal);
                moves++;
            }
            if (entry.Kind == NativeMemoryTraceKind.Allocated)
            {
                prepared = true;
                Assert.Equal(1L, entry.AllocationOrdinal);
            }
            if (entry.Kind is NativeMemoryTraceKind.ReservationBackingPrepared or NativeMemoryTraceKind.ReservationActivated
                or NativeMemoryTraceKind.Released or NativeMemoryTraceKind.UniqueReturned)
                Assert.Equal(1L, entry.AllocationOrdinal);
        }
        Assert.True(prepared);
        Assert.Equal(2, moves);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void BackingHistoryIsNumericAndSegmentFieldCountDoesNotGrow()
    {
        FieldInfo[] history = typeof(NativeOwnerBackingHistory).GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Equal(4, history.Length);
        Assert.All(history, static field => Assert.Equal(typeof(long), field.FieldType));
        FieldInfo[] segments = typeof(NativeSegment).GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Equal(2, segments.Count(static field => field.FieldType == typeof(long)));
        Assert.DoesNotContain(segments, static field => string.Equals(field.Name, "_ownerId", StringComparison.Ordinal));
        Assert.Equal(0, new NativeOwnerBackingHistory().OwnerId);
    }

    private static void AssertPhysicalPairs(NativeMemoryBudget budget, long ownerId, int acquisitions)
    {
        NativeMemoryTraceEvent[] events = new NativeMemoryTraceEvent[64];
        int count = budget.CopyTraceTo(events);
        NativeMemoryTraceEvent[] allocated = events[..count].Where(static entry => entry.Kind == NativeMemoryTraceKind.Allocated).ToArray();
        NativeMemoryTraceEvent[] freed = events[..count].Where(static entry => entry.Kind == NativeMemoryTraceKind.Released).ToArray();
        Assert.Equal(acquisitions, allocated.Length);
        Assert.Equal(acquisitions, freed.Length);
        for (int index = 0; index < acquisitions; index++)
        {
            Assert.Equal(ownerId, allocated[index].OwnerId);
            Assert.Equal(index + 1L, allocated[index].AllocationOrdinal);
            int matches = 0;
            NativeMemoryTraceEvent released = default;
            foreach (NativeMemoryTraceEvent candidate in freed)
            {
                if (candidate.AllocationOrdinal != allocated[index].AllocationOrdinal) continue;
                matches++;
                released = candidate;
            }
            Assert.Equal(1, matches);
            Assert.Equal(ownerId, released.OwnerId);
            Assert.Equal(allocated[index].RequestedBytes, released.RequestedBytes);
        }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().DroppedTraceEventCount);
    }

    // No payload access is possible in this failure-before-registration test.
    private sealed class AddressOnlyBuffer : SafeBuffer
    {
        internal int ReleaseCount { get; private set; }
        internal AddressOnlyBuffer() : base(true) { SetHandle((IntPtr)4096); Initialize(64); }
        protected override bool ReleaseHandle() { ReleaseCount++; return true; }
    }
}
