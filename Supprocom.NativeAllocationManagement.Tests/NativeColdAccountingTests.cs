using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeColdAccountingTests
{
    private static readonly int[] Input = [3, 5, 7, 11];

    [Fact]
    public void PhysicalAccountingHasNoEagerObjectsAndHotInitializationIsStrict()
    {
        Type physical = typeof(NativeMemoryAccounting);
        Assert.Null(physical.TypeInitializer);
        Type hot = physical.GetNestedType("HotAccounting", BindingFlags.NonPublic)!;
        Assert.NotNull(hot.TypeInitializer);
        Assert.Equal((TypeAttributes)0, hot.Attributes & TypeAttributes.BeforeFieldInit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4096)]
    public void PhysicalRecordsAndSnapshotsDoNotRegisterUnusedThreadHotMetrics(int length)
    {
        OnFreshThread(() =>
        {
            Assert.Null(ThreadAccounting());
            NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
            NativeBlock block = NativeBlockAllocator.Allocate((nuint)length, "AccountingTest", "PhysicalOnly",
                ownerId: NativeOwnerIdentity.NextWithoutPreparation());
            try
            {
                NativeMemoryStatistics acquired = NativeMemoryDiagnostics.Snapshot();
                Assert.Equal(before.OutstandingNativeBytes + length, acquired.OutstandingNativeBytes);
                Assert.Equal(before.AllocationCount + (length == 0 ? 0 : 1), acquired.AllocationCount);
                Assert.Equal(before.BumpTraversalVisitCount, acquired.BumpTraversalVisitCount);
                Assert.Equal(before.CopiedBytes, acquired.CopiedBytes);
                Assert.Null(ThreadAccounting());
            }
            finally { NativeBlockAllocator.Free(block); }
            NativeMemoryStatistics returned = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(before.OutstandingNativeBytes, returned.OutstandingNativeBytes);
            Assert.Equal(before.FreeCount + (length == 0 ? 0 : 1), returned.FreeCount);
            Assert.Null(ThreadAccounting());
        });
    }

    [Fact]
    public void RealHotHistoryPublishesInitializationBeforeAggregation()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            OnFreshThread(static () =>
            {
                Assert.Null(ThreadAccounting());
                NativeMemoryAccounting.RecordBumpTraversalVisit();
                Assert.True((bool)typeof(NativeMemoryAccounting)
                    .GetField("_hotAccountingInitialized", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!);
                Assert.Equal(1, NativeMemoryDiagnostics.Snapshot().BumpTraversalVisitCount);
                Assert.NotNull(ThreadAccounting());
            });
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4096)]
    public void DirectFillReadAndReturnDoNotClaimUnusedThreadAccounting(int length)
    {
        OnFreshThread(() =>
        {
            Assert.Null(ThreadAccounting());
            NativeMemoryBudget budget = new(length);
            Assert.True(budget.TryReserve<byte>(length, out NativeMemoryReservation<byte>? permission, out _));
            try
            {
                permission.Value.PrepareBacking();
                using NativeTransfer<byte> owner = NativeMemoryReservation<byte>.Activate(ref permission, static writer => writer.Fill(7));
                Assert.Equal(length * 7L, owner.Read(static view =>
                {
                    long sum = 0;
                    foreach (ref readonly byte value in view.AsSpan()) sum += value;
                    return sum;
                }));
                Assert.Null(ThreadAccounting());
            }
            // Activation consumes the nullable ref permission on success/failure.
#pragma warning disable CA1508
            finally { permission?.Dispose(); }
#pragma warning restore CA1508
            Assert.Null(ThreadAccounting());
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(0, budget.CaptureAdmissionStatistics().OutstandingReservationCount);
        });
    }

    [Fact]
    public void FirstRealCopyClaimsAccountingAndPreservesCompleteCopyHistory()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            OnFreshThread(static () =>
            {
                Assert.Null(ThreadAccounting());
                NativeMemoryBudget budget = new(16);
                Assert.True(budget.TryReserve<int>(4, out NativeMemoryReservation<int>? permission, out _));
                try
                {
                    Assert.Null(ThreadAccounting());
                    using NativeTransfer<int> owner = NativeMemoryReservation<int>.Activate(ref permission,
                        static writer => writer.Write(Input.AsSpan()));
                    Assert.NotNull(ThreadAccounting());
                    Assert.Equal(16, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
                    owner.Access(static view =>
                    {
                        Span<int> copy = stackalloc int[4];
                        view.CopyTo(copy);
                        Assert.True(copy.SequenceEqual(Input));
                    });
                    Assert.Equal(32, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
                }
#pragma warning disable CA1508
                finally { permission?.Dispose(); }
#pragma warning restore CA1508
                Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
                Assert.Equal(32, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            });
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void PreparationPaysForTheFirstRealClearAndCopyWithoutDeferringMetadata()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            OnFreshThread(static () =>
            {
                Assert.Null(ThreadAccounting());
                NativeLeaseInitializer<int> initialize = static writer => writer.Fill(7);
                using NativePreparedPool<int> pool = new(new NativePoolPreparation(1, 4, 1), null);
                Assert.NotNull(ThreadAccounting());
                ReadOnlySpan<int> input = Input;
                Span<int> output = stackalloc int[4];
                long before = GC.GetAllocatedBytesForCurrentThread();
                long afterRent;
                long afterClear;
                long afterCopyFrom;
                long afterCopyTo;
                using (PreparedPooled<int> lease = pool.Rent(4, initialize))
                {
                    afterRent = GC.GetAllocatedBytesForCurrentThread();
                    lease.Clear();
                    afterClear = GC.GetAllocatedBytesForCurrentThread();
                    lease.CopyFrom(input);
                    afterCopyFrom = GC.GetAllocatedBytesForCurrentThread();
                    lease.CopyTo(output);
                    afterCopyTo = GC.GetAllocatedBytesForCurrentThread();
                }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.Equal(before, afterRent);
                Assert.Equal(before, afterClear);
                Assert.Equal(before, afterCopyFrom);
                Assert.Equal(before, afterCopyTo);
                Assert.Equal(0, allocated);
                Assert.True(output.SequenceEqual(Input));
                NativeMemoryStatistics actual = NativeMemoryDiagnostics.Snapshot();
                Assert.Equal(1, actual.StorageClearCount);
                Assert.Equal(16, actual.StorageClearBytes);
                Assert.Equal(16, actual.WrittenClearBytes);
                Assert.Equal(32, actual.CopiedBytes);
            });
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    private static object? ThreadAccounting() => typeof(NativeMemoryAccounting)
        .GetField("_threadHotMetrics", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);

    private static void OnFreshThread(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() => failure = Record.Exception(action));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
    }
}
