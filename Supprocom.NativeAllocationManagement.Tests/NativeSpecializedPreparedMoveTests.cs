using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeSpecializedPreparedMoveTests
{
    [Fact]
    public void MovementInvalidatesAliasesWithoutChangingTheSlotOrItsCharge()
    {
        NativeMemoryBudget budget = new(64);
        using NativePreparedPool<int> pool = CreatePool(budget);
        PreparedPooled<int> source = pool.Rent(2, static writer => writer.Fill(7));
        PreparedPooled<int> alias = source;
        NativeOwnerStatistics before = pool.GetStatistics();
        PreparedPooled<int> destination = PreparedPooled<int>.Move(ref source);
        Assert.Equal(0, source.Length);
        Assert.Equal(0, source.Capacity);
        AssertUninitialized(source);
        AssertReturned(alias);
        Assert.Equal(2, destination.Length);
        Assert.Equal(2, destination.Capacity);
        destination.Access(static view => view[1] = 13);
        PreparedPooled<int> secondAlias = destination;
        PreparedPooled<int> current = PreparedPooled<int>.Move(ref destination);
        AssertReturned(secondAlias);
        Assert.Equal(20, current.Read(static view => view[0] + view[1]));
        Assert.Equal(before, pool.GetStatistics());
        Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(8, budget.CaptureStatistics().CommittedBytes);
        current.Dispose();
        AssertReturned(current);
        using PreparedPooled<int> reused = pool.Rent(2, static writer => writer.Fill(31));
        AssertReturned(alias);
        AssertReturned(secondAlias);
        AssertReturned(current);
        Assert.Equal(62, reused.Read(static view => view[0] + view[1]));
        Assert.Equal(1, pool.GetStatistics().FreshSegmentAllocationCount);
    }

    [Fact]
    public void DefaultCannotMoveOrGainAnOwner()
    {
        PreparedPooled<int> missing = default;
        try
        {
            _ = PreparedPooled<int>.Move(ref missing);
            Assert.Fail("A default capability cannot move.");
        }
        catch (NativeAllocationUninitializedException exception)
        {
            Assert.Equal(nameof(PreparedPooled<int>.Move), exception.Operation);
        }
        AssertUninitialized(missing);
    }

    [Fact]
    public void IdentityExhaustionLeavesTheOriginalLeaseUsableAndReturnable()
    {
        using NativePreparedPool<int> pool = CreatePool(budget: null);
        PreparedPooled<int> source = pool.Rent(2, static writer => writer.Fill(42));
        NativePreparedPool<int> kernel = pool;
        typeof(NativePreparedPool<int>).GetField("_leaseTokenCounter",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(kernel, long.MaxValue);
        try
        {
            _ = PreparedPooled<int>.Move(ref source);
            Assert.Fail("Exhausted authority must not wrap.");
        }
        catch (InvalidOperationException exception)
        {
            Assert.Contains("lease-token authority", exception.Message, StringComparison.Ordinal);
        }
        Assert.Equal(84, source.Read(static view => view[0] + view[1]));
        Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
        source.Dispose();
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public void EnteredBorrowRejectsMovementAndKeepsItsStorageAlive()
    {
        using NativePreparedPool<int> pool = CreatePool(budget: null);
        PreparedPooled<int> source = pool.Rent(2, static writer => writer.Fill(42));
        PreparedPooledBorrow<int> borrow = source.EnterBorrow("move rejection test");
        try
        {
            try
            {
                _ = PreparedPooled<int>.Move(ref source);
                Assert.Fail("Movement cannot invalidate an entered borrow.");
            }
            catch (InvalidOperationException exception)
            {
                Assert.Contains("source remains owning", exception.Message, StringComparison.Ordinal);
            }
            Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
            NativeLeaseView<int> view = borrow.View;
            Assert.Equal(42, view[0]);
            view[1] = 13;
        }
        finally { borrow.Dispose(); }
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        PreparedPooled<int> destination = PreparedPooled<int>.Move(ref source);
        Assert.Equal(55, destination.Read(static view => view[0] + view[1]));
        destination.Dispose();
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Capture all foreign-thread failures for assertions on the actual pool construction thread; an unhandled worker exception would terminate the test process.")]
    public void WrongThreadCannotPublishAnotherTokenOrChangeTheOriginalLease()
    {
        using NativePreparedPool<int> pool = CreatePool(budget: null);
        PreparedPooled<int> source = pool.Rent(2, static writer => writer.Fill(42));
        NativePreparedPool<int> kernel = pool;
        // Exercise the public value operation on another thread, not a captured
        // ref structure. Construction uses internal test authority over slot zero.
        Exception? failure = null;
        int remainingLength = 0;
        Thread worker = new(() =>
        {
            PreparedPooled<int> foreign = new(kernel, 0, 1, 2, 2);
            try
            {
                _ = PreparedPooled<int>.Move(ref foreign);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            remainingLength = foreign.Length;
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(nameof(PreparedPooled<int>.Move), Assert.IsType<NativeAllocationStateException>(failure).Operation);
        Assert.Equal(2, remainingLength);
        PreparedPooled<int> destination = PreparedPooled<int>.Move(ref source);
        Assert.Equal(84, destination.Read(static view => view[0] + view[1]));
        destination.Dispose();
    }

    [Fact]
    public void PreparedRentMoveAccessReturnAndSnapshotsHaveNoRecurringAllocation()
    {
        NativeMemoryBudget budget = new(4096);
        using NativePreparedPool<byte> pool = new(new NativePoolPreparation(1, 4096, 1), budget);
        NativeLeaseInitializer<byte> initialize = static writer => writer.Fill(7);
        NativeLeaseFunc<byte, int> read = static view => view[0] + view[^1];
        RunPreparedCycle(pool, initialize, read);
        var banks = pool.CurrentBankCapacitiesForTest;
        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int iteration = 0; iteration < 1024; iteration++)
        {
            checksum += RunPreparedCycle(pool, initialize, read);
            _ = pool.CapturePreparedSnapshot();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(14 * 1024, checksum);
        Assert.Equal(banks, pool.CurrentBankCapacitiesForTest);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().ReallocationCount);
        Assert.Equal(0, pool.CapturePreparedSnapshot().OccupiedSlotCount);
        Assert.Equal(32, Unsafe.SizeOf<PreparedPooled<byte>>());
    }

    private static int RunPreparedCycle(NativePreparedPool<byte> pool,
        NativeLeaseInitializer<byte> initialize, NativeLeaseFunc<byte, int> read)
    {
        Assert.True(pool.TryRent(4096, initialize, out PreparedPooled<byte> lease, out _));
        for (int move = 0; move < 16; move++) lease = PreparedPooled<byte>.Move(ref lease);
        int result = lease.Read(read);
        lease.Dispose();
        return result;
    }

    [Fact]
    public void TraceUsesTheActualBackingOrdinalAndDoesNotInventACharge()
    {
        NativeMemoryBudget budget = new(64, 16);
        using NativePreparedPool<int> pool = CreatePool(budget);
        PreparedPooled<int> source = pool.Rent(2, static writer => writer.Fill(7));
        PreparedPooled<int> destination = PreparedPooled<int>.Move(ref source);
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[16];
        int count = budget.CopyTraceTo(events);
        NativeMemoryTraceEvent moved = default;
        int moves = 0;
        foreach (ref readonly NativeMemoryTraceEvent item in events[..count])
        {
            if (item.Kind != NativeMemoryTraceKind.Moved) continue;
            moved = item;
            moves++;
        }
        Assert.Equal(1, moves);
        Assert.Equal(pool.Id, moved.OwnerId);
        Assert.Equal(1, moved.AllocationOrdinal);
        Assert.Equal(2, moved.CorrelationId);
        Assert.Equal((nuint)(8), moved.RequestedBytes);
        Assert.Equal(8, moved.CommittedBytes);
        Assert.Equal(0, moved.ReservedBytes);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().FreeCount);
        destination.Dispose();
    }

    private static NativePreparedPool<int> CreatePool(NativeMemoryBudget? budget) =>
        new(new NativePoolPreparation(1, 2, 1), budget);

    private static void AssertUninitialized(scoped PreparedPooled<int> value)
    {
        try { value.Dispose(); Assert.Fail("Default bindings cannot return a slot."); }
        catch (NativeAllocationUninitializedException) { }
    }

    private static void AssertReturned(scoped PreparedPooled<int> value)
    {
        try { _ = value.Read(static view => view[0]); Assert.Fail("Stale authority cannot read reused storage."); }
        catch (NativeAllocationReturnedException) { }
        try { value.Dispose(); Assert.Fail("Stale authority cannot return another binding's slot."); }
        catch (NativeAllocationReturnedException) { }
    }
}
