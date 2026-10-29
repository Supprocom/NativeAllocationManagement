using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolColdAccountingTests
{
    private static readonly int[] Input = [3, 5, 7, 11];

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void FilledRentInitializerScopeAndReturnLeaveUnusedThreadCountersUnclaimed(int length)
    {
        Quiesce();
        OnFreshThread(() =>
        {
            Assert.Null(ThreadAccounting());
            NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
            using (NativePool<int> pool = new(length, NativeMemoryReturn.Free))
            {
                Assert.Null(ThreadAccounting());
                using (Pooled<int> filled = pool.Rent(length, 7))
                    Assert.Equal(-1, filled.Read(static view => view.AsSpan().IndexOfAnyExcept(7)));
                using (Pooled<int> initialized = pool.Rent(length, static writer => writer.Fill(11)))
                    Assert.Equal(-1, initialized.Read(static view => view.AsSpan().IndexOfAnyExcept(11)));
                Assert.Equal(length, pool.Process(length, 13, 0, static (values, _) =>
                {
                    Assert.Equal(-1, values.IndexOfAnyExcept(13));
                    Assert.Null(ThreadAccounting());
                    return values.Length;
                }));
                Assert.Null(ThreadAccounting());
                Assert.Equal(0, pool.GetStatistics().RequestedBytes);
            }
            NativeMemoryStatistics after = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(before.AllocationCount + (length == 0 ? 0 : 1), after.AllocationCount);
            Assert.Equal(before.FreeCount + (length == 0 ? 0 : 1), after.FreeCount);
            Assert.Equal(before.OutstandingNativeBytes, after.OutstandingNativeBytes);
            Assert.Null(ThreadAccounting());
        });
    }

    [Theory]
    [InlineData("Clear")]
    [InlineData("CopyFrom")]
    [InlineData("CopyTo")]
    [InlineData("InitializerCopy")]
    public void TheFirstRealBulkOperationClaimsAndRecordsItsExactHistory(string operation)
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            OnFreshThread(() =>
            {
                using NativePool<int> pool = new(preLease: 4, NativeMemoryReturn.Free);
                Assert.Null(ThreadAccounting());
                if (string.Equals(operation, "InitializerCopy", StringComparison.Ordinal))
                {
                    using Pooled<int> copied = pool.Rent(4, static writer => writer.Write(Input.AsSpan()));
                    Assert.Equal(11, copied.Read(static view => view[3]));
                }
                else
                {
                    using Pooled<int> values = pool.Rent(4, 7);
                    Span<int> output = stackalloc int[4];
                    if (string.Equals(operation, "Clear", StringComparison.Ordinal))
                    {
                        values.Clear();
                        Assert.Equal(-1, values.Read(static view => view.AsSpan().IndexOfAnyExcept(0)));
                    }
                    else if (string.Equals(operation, "CopyFrom", StringComparison.Ordinal))
                    {
                        values.CopyFrom(Input);
                        Assert.Equal(11, values.Read(static view => view[3]));
                    }
                    else
                    {
                        values.CopyTo(output);
                        Assert.Equal(-1, output.IndexOfAnyExcept(7));
                    }
                }
                Assert.NotNull(ThreadAccounting());
                NativeMemoryStatistics history = NativeMemoryDiagnostics.Snapshot();
                bool clear = string.Equals(operation, "Clear", StringComparison.Ordinal);
                Assert.Equal(clear ? 1 : 0, history.StorageClearCount);
                Assert.Equal(clear ? 16 : 0, history.StorageClearBytes);
                Assert.Equal(history.StorageClearBytes, history.WrittenClearBytes);
                Assert.Equal(clear ? 0 : 16, history.CopiedBytes);
            });
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void EmptyBulkOperationsDoNotClaimRecordsOrInventHistory()
    {
        OnFreshThread(static () =>
        {
            NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
            using NativePool<int> pool = new(preLease: 0, NativeMemoryReturn.Free);
            using Pooled<int> empty = pool.Rent(0, 7);
            empty.Clear();
            empty.CopyFrom([]);
            empty.CopyTo([]);
            Assert.Null(ThreadAccounting());
            NativeMemoryStatistics after = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(before.StorageClearCount, after.StorageClearCount);
            Assert.Equal(before.StorageClearBytes, after.StorageClearBytes);
            Assert.Equal(before.CopiedBytes, after.CopiedBytes);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FirstClearRetainsSharedHistoryWhenItsThreadRecordCannotBeClaimed(int boundary)
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            OnFreshThread(() =>
            {
                using NativePool<int> pool = new(preLease: 4, NativeMemoryReturn.Free);
                using Pooled<int> values = pool.Rent(4, 7);
                Assert.Null(ThreadAccounting());
                NativeMemoryTestHooks.FailAccountingClaimAt(boundary);
                values.Clear();
                Assert.Equal(-1, values.Read(static view => view.AsSpan().IndexOfAnyExcept(0)));
                Assert.NotNull(ThreadAccounting());
                NativeMemoryStatistics history = NativeMemoryDiagnostics.Snapshot();
                Assert.Equal(1, history.StorageClearCount);
                Assert.Equal(16, history.StorageClearBytes);
                Assert.Equal(16, history.WrittenClearBytes);
                Assert.Equal(0, history.CopiedBytes);
                Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
            });
            Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    private static object? ThreadAccounting() => typeof(NativeMemoryAccounting)
        .GetField("_threadHotMetrics", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);

    // The test compares process-wide counters, so nothing else may change them
    // in its window. A thread's first use of NAM frees what ended threads
    // kept, and guards free leaked storage when finalized: both happen here
    // first.
    private static void Quiesce()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        NativeEndedThreads.ReleaseEndedForTest();
    }

    private static void OnFreshThread(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() => failure = Record.Exception(action));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
    }
}
