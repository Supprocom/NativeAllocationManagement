using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePooledMoveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovementInvalidatesAliasesWithoutChangingTheSlotOrItsCharge(bool prepared)
    {
        NativeMemoryBudget budget = new(64);
        using NativePool<int> pool = CreatePool(prepared, budget);
        Pooled<int> source = pool.Rent(2, static writer => writer.Fill(7));
        Pooled<int> alias = source;
        NativeOwnerStatistics before = pool.GetStatistics();
        Pooled<int> destination = Pooled<int>.Move(ref source);
        Assert.Equal(0, source.Length);
        Assert.Equal(0, source.Capacity);
        AssertUninitialized(source);
        AssertReturned(alias);
        Assert.Equal(2, destination.Length);
        Assert.Equal(2, destination.Capacity);
        destination.Access(static view => view[1] = 13);
        Pooled<int> secondAlias = destination;
        Pooled<int> current = Pooled<int>.Move(ref destination);
        AssertReturned(secondAlias);
        Assert.Equal(20, current.Read(static view => view[0] + view[1]));
        Assert.Equal(before, pool.GetStatistics());
        Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(prepared ? 8 : 64, budget.CaptureStatistics().CommittedBytes);
        current.Dispose();
        AssertReturned(current);
        using Pooled<int> reused = pool.Rent(2, static writer => writer.Fill(31));
        AssertReturned(alias);
        AssertReturned(secondAlias);
        AssertReturned(current);
        Assert.Equal(62, reused.Read(static view => view[0] + view[1]));
        Assert.Equal(1, pool.GetStatistics().FreshSegmentAllocationCount);
    }

    [Fact]
    public void DefaultCannotMoveOrGainAnOwner()
    {
        Pooled<int> missing = default;
        try
        {
            _ = Pooled<int>.Move(ref missing);
            Assert.Fail("A default capability cannot move.");
        }
        catch (NativeAllocationUninitializedException exception)
        {
            Assert.Equal(nameof(Pooled<int>.Move), exception.Operation);
        }
        AssertUninitialized(missing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentityExhaustionLeavesTheOriginalLeaseUsableAndReturnable(bool prepared)
    {
        using NativePool<int> pool = CreatePool(prepared, budget: null);
        Pooled<int> source = pool.Rent(2, static writer => writer.Fill(42));
        NativePoolKernel<int> kernel = GetKernel(pool);
        typeof(NativePoolKernel<int>).GetField("_leaseTokenCounter",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(kernel, long.MaxValue);
        try
        {
            _ = Pooled<int>.Move(ref source);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnteredBorrowRejectsMovementAndKeepsItsStorageAlive(bool prepared)
    {
        using NativePool<int> pool = CreatePool(prepared, budget: null);
        Pooled<int> source = pool.Rent(2, static writer => writer.Fill(42));
        PooledBorrow<int> borrow = source.EnterBorrow("move rejection test");
        try
        {
            try
            {
                _ = Pooled<int>.Move(ref source);
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
        Pooled<int> destination = Pooled<int>.Move(ref source);
        Assert.Equal(55, destination.Read(static view => view[0] + view[1]));
        destination.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Capture all foreign-thread failures for assertions on the actual pool construction thread; an unhandled worker exception would terminate the test process.")]
    public void WrongThreadCannotPublishAnotherTokenOrChangeTheOriginalLease(bool prepared)
    {
        using NativePool<int> pool = CreatePool(prepared, budget: null);
        Pooled<int> source = pool.Rent(2, static writer => writer.Fill(42));
        NativePoolKernel<int> kernel = GetKernel(pool);
        // Exercise the public value operation on another thread, not a captured
        // ref structure. Construction uses internal test authority over slot zero.
        Exception? failure = null;
        int remainingLength = 0;
        Thread worker = new(() =>
        {
            Pooled<int> foreign = new(kernel, 0, 1, 2, 2);
            try
            {
                _ = Pooled<int>.Move(ref foreign);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            remainingLength = foreign.Length;
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(nameof(Pooled<int>.Move), Assert.IsType<NativeAllocationStateException>(failure).Operation);
        Assert.Equal(2, remainingLength);
        Pooled<int> destination = Pooled<int>.Move(ref source);
        Assert.Equal(84, destination.Read(static view => view[0] + view[1]));
        destination.Dispose();
    }

    [Fact]
    public void PreparedRentMoveAccessReturnAndSnapshotsHaveNoRecurringAllocation()
    {
        NativeMemoryBudget budget = new(4096);
        using NativePool<byte> pool = new(new NativePoolPreparation(1, 4096, 1), budget);
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
        Assert.Equal(32, Unsafe.SizeOf<Pooled<byte>>());
    }

    private static int RunPreparedCycle(NativePool<byte> pool,
        NativeLeaseInitializer<byte> initialize, NativeLeaseFunc<byte, int> read)
    {
        Assert.True(pool.TryRent(4096, initialize, out Pooled<byte> lease, out _));
        for (int move = 0; move < 16; move++) lease = Pooled<byte>.Move(ref lease);
        int result = lease.Read(read);
        lease.Dispose();
        return result;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TraceUsesTheActualBackingOrdinalAndDoesNotInventACharge(bool prepared)
    {
        NativeMemoryBudget budget = new(64, 16);
        using NativePool<int> pool = CreatePool(prepared, budget);
        Pooled<int> source = pool.Rent(2, static writer => writer.Fill(7));
        Pooled<int> destination = Pooled<int>.Move(ref source);
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
        Assert.Equal((nuint)(prepared ? 8 : 64), moved.RequestedBytes);
        Assert.Equal(prepared ? 8 : 64, moved.CommittedBytes);
        Assert.Equal(0, moved.ReservedBytes);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().FreeCount);
        destination.Dispose();
    }

    private static NativePool<int> CreatePool(bool prepared, NativeMemoryBudget? budget) =>
        prepared ? new(new NativePoolPreparation(1, 2, 1), budget)
            : budget is null ? new(2, NativeMemoryReturn.ToNativeMemory)
            : new(budget, 2, 0, NativeMemoryReturn.ToNativeMemory);

    private static NativePoolKernel<int> GetKernel(NativePool<int> pool) =>
        (NativePoolKernel<int>)typeof(NativePool<int>).GetField("_kernel",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;

    private static void AssertUninitialized(scoped Pooled<int> value)
    {
        try { value.Dispose(); Assert.Fail("Default bindings cannot return a slot."); }
        catch (NativeAllocationUninitializedException) { }
    }

    private static void AssertReturned(scoped Pooled<int> value)
    {
        try { _ = value.Read(static view => view[0]); Assert.Fail("Stale authority cannot read reused storage."); }
        catch (NativeAllocationReturnedException) { }
        try { value.Dispose(); Assert.Fail("Stale authority cannot return another binding's slot."); }
        catch (NativeAllocationReturnedException) { }
    }
}
