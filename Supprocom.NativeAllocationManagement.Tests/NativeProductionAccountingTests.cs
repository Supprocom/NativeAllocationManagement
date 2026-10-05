using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeProductionAccountingTests
{
    [Fact]
    public void RetainedExtentIncludesDirectOwnershipButExcludesDetachedStorage()
    {
        NativeMemoryTestHooks.Reset();
        NativeGenerationOwner bank = new(0, null, 0);
        try
        {
            using NativeBuilder<int> direct = new(2);
            NativeSegment segment = NativeSegment.Allocate(8, "AccountingTest", 0,
                "retained extent", NativeOwnerLifecycle.Active, zeroed: false);
            bank.AddSegment(segment);
            long extent = OperatingSystem.IsWindows() ? 8 : 64;
            NativeMemoryStatistics acquired = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(extent + 8, acquired.OutstandingNativeBytes);
            Assert.Equal(extent + 8, acquired.RetainedNativeBytes);
            Assert.Equal(extent + 8, NativeMemoryTestHooks.Snapshot().RetainedNativeBytes);
            bank.Detach();
            NativeMemoryStatistics detached = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(extent + 8, detached.OutstandingNativeBytes);
            Assert.Equal(extent, detached.DetachedNativeBytes);
            Assert.Equal(8, detached.RetainedNativeBytes);
            Assert.Equal(8, NativeMemoryTestHooks.Snapshot().RetainedNativeBytes);
            bank.ReleaseToNative();
            Assert.Equal(8, NativeMemoryDiagnostics.Snapshot().RetainedNativeBytes);
            direct.Dispose();
            Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().RetainedNativeBytes);
            Assert.Equal(0, NativeMemoryTestHooks.Snapshot().RetainedNativeBytes);
        }
        finally
        {
            bank.ReleaseToNative();
            NativeMemoryTestHooks.Reset();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ColdMetadataFailureFallsBackWithoutLosingPriorHistoryOrFailingWork(int boundary)
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            Thread prior = new(static () => NativeMemoryAccounting.RecordBumpTraversalVisit());
            prior.Start();
            Assert.True(prior.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, NativeMemoryDiagnostics.Snapshot().BumpTraversalVisitCount);
            NativeMemoryTestHooks.FailAccountingClaimAt(boundary);
            Exception? failure = null;
            bool shared = false;
            long allocated = -1;
            Thread actor = new(() =>
            {
                failure = Record.Exception(() =>
                {
                    NativeMemoryAccounting.RecordBumpTraversalVisit();
                    object metrics = AccountingField("_threadHotMetrics").GetValue(null)!;
                    shared = (bool)metrics.GetType().GetField("Shared", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(metrics)!;
                    Assert.Null(metrics.GetType().GetField("OwnerThread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(metrics));
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int operation = 0; operation < 1_000; operation++)
                    {
                        NativeMemoryAccounting.RecordBumpTraversalVisit();
                    }
                    allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                });
            });
            actor.Start();
            Assert.True(actor.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(failure);
            Assert.True(shared);
            Assert.Equal(0, allocated);
            Assert.Equal(1_002, NativeMemoryDiagnostics.Snapshot().BumpTraversalVisitCount);
            Thread next = new(static () => NativeMemoryAccounting.RecordBumpTraversalVisit());
            next.Start();
            Assert.True(next.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal(1_003, NativeMemoryDiagnostics.Snapshot().BumpTraversalVisitCount);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public unsafe void ZeroedAcquisitionFieldObservesTheRealBackendBranchAndRelease()
    {
        NativeMemoryTestHooks.Reset();
        NativeMemoryBudget budget = new(512);
        NativeSegment? segment = null;
        try
        {
            segment = NativeSegment.Allocate(8, "AccountingTest", 0, "zeroed acquisition",
                NativeOwnerLifecycle.Active, zeroed: true, budget: budget);
            Assert.Equal(1, NativeMemoryDiagnostics.Snapshot().ZeroedAllocationCount);
            Assert.Equal(1, NativeMemoryDiagnostics.Snapshot().AllocationCount);
            Assert.Equal((long)segment.AllocationByteLength, NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes);
            ReadOnlySpan<byte> actual = new((void*)segment.Pointer, 8);
            foreach (ref readonly byte value in actual)
            {
                Assert.Equal(0, value);
            }
            segment.FreeNow();
            Assert.Equal(1, NativeMemoryDiagnostics.Snapshot().FreeCount);
            Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes);
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        }
        finally
        {
            segment?.FreeNow();
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void PublicDiagnosticsAccountWithoutFaultHookActivation()
    {
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        using (NativeBuilder<int> builder = new(preLease: 2))
        {
            builder.Append([17, 19, 23]);
            NativeMemoryStatistics live = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(before.MetricsEpoch, live.MetricsEpoch);
            Assert.Equal(before.AllocationCount + 1, live.AllocationCount);
            Assert.Equal(before.ReallocationCount + 1, live.ReallocationCount);
            Assert.Equal(before.OutstandingNativeBytes + 16, live.OutstandingNativeBytes);
            Assert.True(live.PeakOutstandingNativeBytes >= live.OutstandingNativeBytes);
        }
        NativeMemoryStatistics released = NativeMemoryDiagnostics.Snapshot();
        Assert.Equal(before.OutstandingNativeBytes, released.OutstandingNativeBytes);
        Assert.Equal(before.FreeCount + 1, released.FreeCount);
    }

    [Fact]
    public void FailedBackendDoesNotDisableSubsequentRealAccounting()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => new NativeBuilder<int>(preLease: 2));
            Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().AllocationCount);
            using (NativeBuilder<int> builder = new(preLease: 2))
            {
                builder.Append(42);
                Assert.Equal(8, NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes);
            }
            Assert.Equal(1, NativeMemoryDiagnostics.Snapshot().AllocationCount);
            Assert.Equal(1, NativeMemoryDiagnostics.Snapshot().FreeCount);
            Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void PreEpochReleaseAndResizeRespectMeasurementBoundaries()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativeBuilder<int> releasedBeforeMeasurement = new(preLease: 2);
            using NativeBuilder<int> resizedIntoMeasurement = new(preLease: 2);
            long previousEpoch = NativeMemoryDiagnostics.Snapshot().MetricsEpoch;
            NativeMemoryTestHooks.Reset();
            Assert.Equal(previousEpoch + 1, NativeMemoryDiagnostics.Snapshot().MetricsEpoch);
            releasedBeforeMeasurement.Dispose();
            Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().FreeCount);
            resizedIntoMeasurement.Append([1, 2, 3]);
            NativeMemoryStatistics live = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(1, live.AllocationCount);
            Assert.Equal(1, live.ReallocationCount);
            Assert.Equal(16, live.OutstandingNativeBytes);
            resizedIntoMeasurement.Dispose();
            Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes);
            Assert.Equal(16, NativeMemoryDiagnostics.Snapshot().PeakOutstandingNativeBytes);
            Assert.Equal(1, NativeMemoryDiagnostics.Snapshot().FreeCount);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void StorageCleanupFieldsMeasureRealReferenceStorageNotConsumerClears()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativeConcurrentPool<string> pool = new(0, 0, NativeMemoryReturn.ToNativeMemory, false);
            using (ConcurrentPooled<string> value = pool.Rent(2, static writer => writer.Fill("live")))
            {
                Assert.Equal("live", value.Read(static view => view[1]));
                Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().StorageClearCount);
            }
            NativeMemoryStatistics cleanup = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(1, cleanup.StorageClearCount);
            Assert.Equal(2 * IntPtr.Size, cleanup.StorageClearBytes);
            Assert.Equal(cleanup.StorageClearBytes, cleanup.WrittenClearBytes);
            Assert.Equal(NativeMemoryTestHooks.Snapshot().StorageClearBytes, cleanup.StorageClearBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void PreparedOperationsAndRepeatedPublicSnapshotsAllocateNoManagedStorage()
    {
        NativeMemoryBudget budget = new(1_024);
        using NativePool<int> pool = new(new NativePoolPreparation(1, 4, 1), budget);
        NativeLeaseInitializer<int> initialize = static writer => writer.Fill(42);
        if (pool.TryRent(4, initialize, out Pooled<int> warm, out _))
        {
            warm.Dispose();
        }
        _ = NativeMemoryDiagnostics.Snapshot();
        long before = GC.GetAllocatedBytesForCurrentThread();
        long checksum = 0;
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            if (!pool.TryRent(4, initialize, out Pooled<int> value, out _))
            {
                throw new InvalidOperationException("Prepared accounting test exhausted.");
            }
            value.Dispose();
            checksum += NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes;
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(checksum > 0);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
    }

    [Fact]
    public void ReferenceCleanupDistinguishesVisitedSlotsFromActualZeroWrites()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativeConcurrentPool<string?> pool = new(0, 0, NativeMemoryReturn.ToNativeMemory, false);
            using (ConcurrentPooled<string?> value = pool.Rent(3, static writer =>
            {
                writer.Write("live");
                writer.Write((string?)null);
                writer.Write("tail");
            }))
            {
                Assert.Equal(2, pool.CaptureDiagnosticSnapshot().ReferenceRoots);
                value.Access(static view => view.Clear());
                Assert.Equal(0, pool.CaptureDiagnosticSnapshot().ReferenceRoots);
                Assert.Null(value.Read(static view => view[2]));
                NativeMemoryStatistics clear = NativeMemoryDiagnostics.Snapshot();
                Assert.Equal(1, clear.StorageClearCount);
                Assert.Equal(3 * IntPtr.Size, clear.StorageClearBytes);
                Assert.Equal(2 * IntPtr.Size, clear.WrittenClearBytes);
            }
            NativeMemoryStatistics released = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(2, released.StorageClearCount);
            Assert.Equal(6 * IntPtr.Size, released.StorageClearBytes);
            Assert.Equal(2 * IntPtr.Size, released.WrittenClearBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void PartialInitializationCleanupCountsOnlyTheInitializedReferencePrefix()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativeConcurrentPool<string> pool = new(0, 0, NativeMemoryReturn.ToNativeMemory, false);
            Assert.Throws<InvalidOperationException>(() => pool.Rent(3, static writer => writer.Write("prefix")));
            NativeMemoryStatistics cleanup = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(1, cleanup.StorageClearCount);
            Assert.Equal(IntPtr.Size, cleanup.StorageClearBytes);
            Assert.Equal(IntPtr.Size, cleanup.WrittenClearBytes);
            Assert.Equal(0, pool.CaptureDiagnosticSnapshot().ReferenceRoots);
            Assert.Equal(0, pool.CaptureDiagnosticSnapshot().ActiveRecords);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void DirectBoundedClearIsObservedButArbitraryConsumerSpanClearIsNotInvented()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativePool<int> pool = new(new NativePoolPreparation(1, 4, 1), new NativeMemoryBudget(512));
            Assert.True(pool.TryRent(4, static writer => writer.Fill(42), out Pooled<int> value, out _));
            using (value)
            {
                value.Access(static view => view.AsSpan().Clear());
                Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().StorageClearCount);
                value.Access(static view => view.Clear());
                NativeMemoryStatistics clear = NativeMemoryDiagnostics.Snapshot();
                Assert.Equal(1, clear.StorageClearCount);
                Assert.Equal(16, clear.StorageClearBytes);
                Assert.Equal(16, clear.WrittenClearBytes);
                Assert.Equal(0, value.Read(static view => view[3]));
            }
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Theory]
    [InlineData("_allocationCount")]
    [InlineData("_reallocationCount")]
    [InlineData("_freeCount")]
    [InlineData("_zeroedAllocationCount")]
    [InlineData("_detachedGenerationCount")]
    public void SaturatedGlobalEventHistoryCannotWrapOrThrowAfterAccounting(string field)
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            SetStaticField(field, long.MaxValue);
            long epoch = NativeMemoryAccounting.CurrentMetricsEpoch;
            switch (field)
            {
                case "_allocationCount": NativeMemoryAccounting.RecordAllocation(8, false); break;
                case "_reallocationCount": NativeMemoryAccounting.RecordReallocation(0, 8, epoch); break;
                case "_freeCount":
                    NativeMemoryAccounting.RecordAllocation(8, false);
                    NativeMemoryAccounting.RecordFree(8, false, epoch);
                    break;
                case "_zeroedAllocationCount": NativeMemoryAccounting.RecordAllocation(8, true); break;
                case "_detachedGenerationCount": NativeMemoryAccounting.RecordDetachedGeneration(epoch); break;
                default: throw new InvalidOperationException("Unknown history fixture.");
            }
            Assert.Equal(long.MaxValue, GetStaticField(field));
            Assert.True(NativeMemoryDiagnostics.Snapshot().HistoryOverflowed);
            Assert.True(NativeMemoryTestHooks.Snapshot().HistoryOverflowed);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void ThreadLocalHistorySaturatesWithoutLosingCurrentGauges()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeMemoryAccounting.PrepareThread();
            object metrics = AccountingField("_threadHotMetrics").GetValue(null)!;
            metrics.GetType().GetField("BumpTraversalVisitCount", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(metrics, long.MaxValue);
            Assert.False(NativeMemoryDiagnostics.Snapshot().HistoryOverflowed);
            NativeMemoryAccounting.RecordBumpTraversalVisit();
            NativeMemoryStatistics saturated = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(long.MaxValue, saturated.BumpTraversalVisitCount);
            Assert.True(saturated.HistoryOverflowed);
            Assert.Equal(0, saturated.OutstandingNativeBytes);
            Assert.Equal(0, saturated.AllocationCount);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void SummedLocalHistoryReportsOverflowBeforeEitherThreadHistoryOverflows()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            Thread first = new(static () =>
            {
                NativeMemoryAccounting.PrepareThread();
                object metrics = AccountingField("_threadHotMetrics").GetValue(null)!;
                metrics.GetType().GetField("ReusedNativeSegmentCount", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(metrics, long.MaxValue);
            });
            first.Start();
            Assert.True(first.Join(TimeSpan.FromSeconds(10)));
            Assert.False(NativeMemoryDiagnostics.Snapshot().HistoryOverflowed);
            Thread second = new(NativeMemoryAccounting.RecordReusedNativeSegment);
            second.Start();
            Assert.True(second.Join(TimeSpan.FromSeconds(10)));
            NativeMemoryStatistics aggregate = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(long.MaxValue, aggregate.ReusedNativeSegmentCount);
            Assert.True(aggregate.HistoryOverflowed);
            Assert.Equal(0, aggregate.OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void ShortLivedThreadsPreserveHistoryInFixedMetadata()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            Array slots = (Array)AccountingField("HotMetrics").GetValue(null)!;
            for (int iteration = 0; iteration < NativeMemoryAccounting.ThreadMetricSlotCapacity * 3; iteration++)
            {
                Thread thread = new(static () =>
                {
                    NativeMemoryAccounting.RecordBumpTraversalVisit();
                    NativeMemoryAccounting.RecordReclaimedRangeReuse(2, 8);
                });
                thread.Start();
                Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
            }
            NativeMemoryStatistics result = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(NativeMemoryAccounting.ThreadMetricSlotCapacity * 3, result.BumpTraversalVisitCount);
            Assert.Equal(result.BumpTraversalVisitCount * 2, result.ReclaimedRangeReuseCount);
            Assert.Equal(result.BumpTraversalVisitCount * 8, result.ReclaimedRangeReuseBytes);
            Assert.Same(slots, AccountingField("HotMetrics").GetValue(null));
            Assert.Equal(NativeMemoryAccounting.ThreadMetricSlotCapacity, slots.Length);
            Assert.False(result.HistoryOverflowed);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void AliveThreadWithoutAnExternalStrongWrapperCannotLoseItsClaimedHistory()
    {
        NativeMemoryTestHooks.Reset();
        using ManualResetEventSlim ready = new(false);
        using ManualResetEventSlim release = new(false);
        WeakReference<Thread> observed = StartUnretainedThread(ready, release);
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.True(observed.TryGetTarget(out Thread? alive));
            Assert.True(alive.IsAlive);
            Assert.Equal(1, NativeMemoryDiagnostics.Snapshot().BumpTraversalVisitCount);
            Thread competitor = new(static () =>
            {
                NativeMemoryAccounting.RecordBumpTraversalVisit();
                NativeMemoryAccounting.RecordBumpTraversalVisit();
            });
            competitor.Start();
            Assert.True(competitor.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal(3, NativeMemoryDiagnostics.Snapshot().BumpTraversalVisitCount);
        }
        finally
        {
            release.Set();
            if (observed.TryGetTarget(out Thread? alive))
            {
                Assert.True(alive.Join(TimeSpan.FromSeconds(10)));
            }
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void EmptyClearsDoNotInventRangeWorkAndWorkspaceBackingClearIsObserved()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new NativeWorkspace<int>(preLease: 0));
            using NativePool<int> pool = new(0, 0, NativeMemoryReturn.ToNativeMemory);
            using (Pooled<int> empty = pool.Rent(0, static writer => writer.Fill(42)))
            {
                empty.Access(static view => view.Clear());
            }
            Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().StorageClearCount);
            using NativeWorkspace<int> workspace = new(preLease: 2);
            NativeMemoryStatistics clear = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(1, clear.StorageClearCount);
            Assert.Equal(8, clear.StorageClearBytes);
            Assert.Equal(8, clear.WrittenClearBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void ExcessLiveThreadsUseExactAtomicFallbackWithoutPerOperationAllocation()
    {
        NativeMemoryTestHooks.Reset();
        const int operations = 256;
        int count = NativeMemoryAccounting.ThreadMetricSlotCapacity + 8;
        Thread[] workers = new Thread[count];
        Exception?[] failures = new Exception?[count];
        long[] allocations = new long[count];
        int sharedCount = 0;
        using CountdownEvent ready = new(count);
        using ManualResetEventSlim execute = new(false);
        using CountdownEvent completed = new(count);
        using ManualResetEventSlim release = new(false);
        try
        {
            for (int index = 0; index < count; index++)
            {
                int worker = index;
                workers[index] = new Thread(() =>
                {
                    failures[worker] = Record.Exception(() =>
                    {
                        NativeMemoryAccounting.PrepareThread();
                        object metrics = AccountingField("_threadHotMetrics").GetValue(null)!;
                        bool shared = (bool)metrics.GetType().GetField("Shared", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(metrics)!;
                        if (shared)
                        {
                            Interlocked.Increment(ref sharedCount);
                        }
                        ready.Signal();
                        if (!execute.Wait(TimeSpan.FromSeconds(30)))
                        {
                            throw new TimeoutException("Accounting execution was not released.");
                        }
                        NativeMemoryAccounting.RecordBumpTraversalVisit();
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        for (int operation = 0; operation < operations; operation++)
                        {
                            NativeMemoryAccounting.RecordBumpTraversalVisit();
                            NativeMemoryAccounting.RecordReclaimedRangeReuse(2, 8);
                        }
                        allocations[worker] = GC.GetAllocatedBytesForCurrentThread() - before;
                    });
                    completed.Signal();
                    release.Wait(TimeSpan.FromSeconds(30));
                });
                workers[index].Start();
            }
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)));
            GC.Collect();
            execute.Set();
            Assert.True(completed.Wait(TimeSpan.FromSeconds(30)));
            Assert.All(failures, static failure => Assert.Null(failure));
            Assert.All(allocations, static allocated => Assert.Equal(0, allocated));
            Assert.True(sharedCount >= 8);
            NativeMemoryStatistics result = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(count * (operations + 1), result.BumpTraversalVisitCount);
            Assert.Equal(count * operations * 2, result.ReclaimedRangeReuseCount);
            Assert.Equal(count * operations * 8, result.ReclaimedRangeReuseBytes);
        }
        finally
        {
            execute.Set();
            release.Set();
            foreach (Thread? thread in workers)
            {
                if (thread is not null)
                {
                    Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
                }
            }
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void ExhaustedMeasurementIdentityRejectsBeforeChangingAnyCounter()
    {
        NativeMemoryTestHooks.Reset();
        long epoch = NativeMemoryAccounting.CurrentMetricsEpoch;
        try
        {
            SetStaticField("_metricsEpoch", long.MaxValue);
            SetStaticField("_allocationCount", 17L);
            Assert.Throws<OverflowException>(NativeMemoryAccounting.ResetForTests);
            Assert.Equal(long.MaxValue, NativeMemoryAccounting.CurrentMetricsEpoch);
            Assert.Equal(17, GetStaticField("_allocationCount"));
        }
        finally
        {
            SetStaticField("_metricsEpoch", epoch);
            NativeMemoryTestHooks.Reset();
        }
    }

    private static FieldInfo AccountingField(string name) =>
        typeof(NativeMemoryAccounting).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static void SetStaticField(string name, long value) => AccountingField(name).SetValue(null, value);

    private static long GetStaticField(string name) => (long)AccountingField(name).GetValue(null)!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Thread> StartUnretainedThread(ManualResetEventSlim ready, ManualResetEventSlim release)
    {
        Thread thread = new(() =>
        {
            NativeMemoryAccounting.RecordBumpTraversalVisit();
            ready.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });
        thread.Start();
        return new WeakReference<Thread>(thread);
    }
}
