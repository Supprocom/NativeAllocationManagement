namespace Supprocom.NativeAllocationManagement.Tests;

// Synchronized pools keep per-thread record caches. These tests race owner
// threads against the operations that revoke those caches.
public sealed class NativePoolThreadCacheTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void ThreadsSharingAPoolKeepTheirOwnDataWhileStatisticsAndTrimsRevokeTheirCaches()
    {
        using NativeConcurrentPool<int> pool = new();
        const int workers = 4;
        using Barrier start = new(workers + 1);
        int finished = 0;
        Exception?[] failures = new Exception?[workers];
        Thread[] threads = new Thread[workers];
        for (int worker = 0; worker < workers; worker++)
        {
            int marker = worker + 1;
            threads[worker] = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    for (int iteration = 0; iteration < 20_000; iteration++)
                    {
                        int length = 1 + ((iteration * 7 + marker) & 31);
                        using ConcurrentPooled<int> lease = pool.Rent(length, writer => writer.Fill(marker));
                        int seen = lease.Process<int, int, AllEqual>(length, marker);
                        if (seen != length)
                        {
                            throw new InvalidOperationException($"Thread {marker} saw {seen} of {length} own values.");
                        }
                    }
                }
                catch (InvalidOperationException exception)
                {
                    failures[marker - 1] = exception;
                }
                finally
                {
                    Interlocked.Increment(ref finished);
                }
            });
            threads[worker].Start();
        }

        start.SignalAndWait();
        int revocations = 0;
        while (Volatile.Read(ref finished) < workers)
        {
            _ = pool.GetStatistics();
            _ = pool.CaptureDiagnosticSnapshot();
            _ = pool.TrimRetainedMemory();
            revocations++;
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(Timeout));
        }

        Assert.All(failures, static failure => Assert.Null(failure));
        Assert.True(revocations > 0);
        NativeOwnerStatistics statistics = pool.GetStatistics();
        Assert.Equal(0, statistics.InitializedPayloadBytes);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(0, pool.CurrentInitializationCountForTest);
    }

    [Fact]
    public void ACallbackRunningDuringRevocationExitsThroughTheGeneration()
    {
        using NativeConcurrentPool<int> pool = new();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        int result = 0;
        Thread owner = new(() =>
        {
            using ConcurrentPooled<int> lease = pool.Rent(4, static writer => writer.Fill(5));
            result = lease.Read(view =>
            {
                entered.Set();
                Assert.True(release.Wait(Timeout));
                return view[3];
            });
        });
        owner.Start();
        Assert.True(entered.Wait(Timeout));

        Assert.Equal(16, pool.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Throws<NativeAllocationInUseException>(pool.ReleaseLeasesToNativeMemory);
        release.Set();
        Assert.True(owner.Join(Timeout));

        Assert.Equal(5, result);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
        pool.ReleaseLeasesToNativeMemory();
    }

    [Fact]
    public void AnInitializerRunningDuringRevocationCompletesThroughTheGeneration()
    {
        using NativeConcurrentPool<int> pool = new();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        int result = 0;
        Thread owner = new(() =>
        {
            using ConcurrentPooled<int> lease = pool.Rent(2, writer =>
            {
                entered.Set();
                Assert.True(release.Wait(Timeout));
                writer.Fill(9);
            });
            result = lease.Read(static view => view[1]);
        });
        owner.Start();
        Assert.True(entered.Wait(Timeout));

        Assert.Equal(1, pool.CurrentInitializationCountForTest);
        Assert.Throws<NativeAllocationInUseException>(pool.ReleaseLeasesToNativeMemory);
        release.Set();
        Assert.True(owner.Join(Timeout));

        Assert.Equal(9, result);
        Assert.Equal(0, pool.CurrentInitializationCountForTest);
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
    }

    [Fact]
    public void ReleasingLeasesInvalidatesACachedLeaseHeldByAnotherThread()
    {
        using NativeConcurrentPool<int> pool = new();
        using ManualResetEventSlim rented = new(false);
        using ManualResetEventSlim released = new(false);
        NativeAllocationException? staleRead = null;
        int renewed = 0;
        Thread owner = new(() =>
        {
            ConcurrentPooled<int> lease = pool.Rent(3, static writer => writer.Fill(4));
            rented.Set();
            Assert.True(released.Wait(Timeout));
            try
            {
                _ = lease.Read(static view => view[0]);
            }
            catch (NativeAllocationException exception)
            {
                staleRead = exception;
            }

            lease.Dispose();
            using ConcurrentPooled<int> next = pool.Rent(3, static writer => writer.Fill(8));
            renewed = next.Read(static view => view[2]);
        });
        owner.Start();
        Assert.True(rented.Wait(Timeout));
        pool.ReleaseLeasesToNativeMemory();
        released.Set();
        Assert.True(owner.Join(Timeout));

        Assert.IsAssignableFrom<NativeAllocationException>(staleRead);
        Assert.Equal(8, renewed);
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
    }

    [Fact]
    public void AnExitedThreadsCacheFoldsItsAbandonedLeaseIntoTheGeneration()
    {
        using NativeConcurrentPool<int> pool = new();
        Thread owner = new(() =>
        {
            using (pool.Rent(2, static writer => writer.Fill(1)))
            {
            }

            using (pool.Rent(4, static writer => writer.Fill(2)))
            {
            }

            // Never disposed: the lease is abandoned with its thread.
            _ = pool.Rent(8, static writer => writer.Fill(3));
        });
        owner.Start();
        Assert.True(owner.Join(Timeout));

        Assert.Equal(32, pool.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
        pool.ReleaseLeasesToNativeMemory();
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public void AThreadUsingManyPoolsKeepsEveryPoolExact()
    {
        NativeConcurrentPool<int>[] pools = new NativeConcurrentPool<int>[12];
#pragma warning disable CA2000 // The finally block disposes every pool.
        foreach (ref NativeConcurrentPool<int> pool in pools.AsSpan())
        {
            pool = new NativeConcurrentPool<int>();
        }
#pragma warning restore CA2000

        try
        {
            for (int round = 0; round < 3; round++)
            {
                foreach (NativeConcurrentPool<int> pool in pools)
                {
                    using ConcurrentPooled<int> lease = pool.Rent(4, writer => writer.Fill(round));
                    Assert.Equal(round, lease.Read(static view => view[3]));
                }
            }

            foreach (NativeConcurrentPool<int> pool in pools)
            {
                NativeOwnerStatistics statistics = pool.GetStatistics();
                Assert.Equal(0, statistics.InitializedPayloadBytes);
                Assert.Equal(16, statistics.PeakInitializedPayloadBytes);
                Assert.Equal(1, statistics.SegmentCount);
            }
        }
        finally
        {
            foreach (NativeConcurrentPool<int> pool in pools)
            {
                pool.Dispose();
            }
        }
    }

    [Fact]
    public void CachedLeasesKeepExactPeaksAndConsecutiveIdentitiesOnOneThread()
    {
        using NativeConcurrentPool<int> pool = new();
        ConcurrentPooled<int> small = pool.Rent(4, static writer => writer.Fill(1));
        ConcurrentPooled<int> large = pool.Rent(8, static writer => writer.Fill(2));
        long firstId = small.AllocationIdForComposite;
        Assert.Equal(firstId + 1, large.AllocationIdForComposite);
        small.Dispose();
        large.Dispose();
        using ConcurrentPooled<int> scoped = pool.LeaseScoped(2, static writer => writer.Fill(3));
        Assert.Equal(firstId + 2, scoped.AllocationIdForComposite);

        NativeOwnerStatistics statistics = pool.GetStatistics();
        Assert.Equal(8, statistics.InitializedPayloadBytes);
        Assert.Equal(48, statistics.PeakInitializedPayloadBytes);
    }

    [Fact]
    public void ABudgetedPoolReusesASlabAnotherThreadCachesBeforeGrowing()
    {
        NativeMemoryBudget budget = new(64);
        using NativeConcurrentPool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory, false);
        Thread owner = new(() =>
        {
            using ConcurrentPooled<int> lease = pool.Rent(16, static writer => writer.Fill(1));
        });
        owner.Start();
        Assert.True(owner.Join(Timeout));

        using ConcurrentPooled<int> reused = pool.Rent(16, static writer => writer.Fill(2));
        Assert.Equal(2, reused.Read(static view => view[15]));
        Assert.Equal(1, pool.GetStatistics().SegmentCount);
    }

    [Fact]
    public void APoolReusesASlabAnotherThreadCachesBeforeGrowing()
    {
        using NativeConcurrentPool<int> pool = new();
        Thread owner = new(() =>
        {
            using ConcurrentPooled<int> lease = pool.Rent(16, static writer => writer.Fill(1));
        });
        owner.Start();
        Assert.True(owner.Join(Timeout));

        using ConcurrentPooled<int> reused = pool.Rent(16, static writer => writer.Fill(2));
        Assert.Equal(1, pool.GetStatistics().SegmentCount);
    }

    [Fact]
    public void TransfersReturnedOnOtherThreadsComeBackWithoutGrowth()
    {
        using NativeConcurrentPool<int> pool = new();
        using (pool.RentTransferable(16, static writer => writer.Fill(0)))
        {
        }

        int segments = pool.GetStatistics().SegmentCount;
        for (int round = 0; round < 20; round++)
        {
            NativeTransfer<int>? transfer = null;
            Thread producer = new(() => transfer = pool.RentTransferable(16, writer => writer.Fill(round)));
            producer.Start();
            Assert.True(producer.Join(Timeout));
            NativeTransfer<int> received = transfer!.Value;
            Thread consumer = new(() =>
            {
                Assert.Equal(round, received.Read(static view => view[15]));
                received.Dispose();
            });
            consumer.Start();
            Assert.True(consumer.Join(Timeout));
        }

        Assert.Equal(segments, pool.GetStatistics().SegmentCount);
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
    }

    [Fact]
    public void CachedRentsProcessesAndReturnsAllocateNothingAfterWarmup()
    {
        using NativeConcurrentPool<int> pool = new();
        // Round 0 warms up tiering; runtime work can still land on this thread
        // now and then, so any later round that reads zero proves the path.
        long delta = 0;
        for (int round = 0; round < 4; round++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 10_000; iteration++)
            {
                using ConcurrentPooled<int> lease = pool.Rent(16, static writer => writer.Fill(3));
                _ = lease.Process<int, int, AllEqual>(16, 3);
            }

            delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (round > 0 && delta == 0)
            {
                break;
            }
        }

        Assert.Equal(0, delta);
    }

    [Fact]
    public void ADoubleDisposeOfACachedLeaseIsIgnored()
    {
        using NativeConcurrentPool<int> pool = new();
        ConcurrentPooled<int> lease = pool.Rent(4, static writer => writer.Fill(1));
        ConcurrentPooled<int> copy = lease;
        lease.Dispose();
        copy.Dispose();
        using ConcurrentPooled<int> next = pool.Rent(4, static writer => writer.Fill(6));
        copy.Dispose();
        Assert.Equal(6, next.Read(static view => view[0]));
        NativeAllocationException? staleRead = null;
        try
        {
            _ = copy.Read(static view => view[0]);
        }
        catch (NativeAllocationException exception)
        {
            staleRead = exception;
        }

        Assert.IsType<NativeAllocationReturnedException>(staleRead);
        Assert.Equal(16, pool.GetStatistics().InitializedPayloadBytes);
    }

    [Fact]
    public void DisposingThePoolFoldsCachedLeasesOfOtherThreads()
    {
        NativeConcurrentPool<int> pool = new();
        using ManualResetEventSlim rented = new(false);
        using ManualResetEventSlim disposed = new(false);
        Exception? afterDispose = null;
        Thread owner = new(() =>
        {
            ConcurrentPooled<int> lease = pool.Rent(4, static writer => writer.Fill(1));
            lease.Dispose();
            rented.Set();
            Assert.True(disposed.Wait(Timeout));
            afterDispose = Record.Exception(() => pool.Rent(4, static writer => writer.Fill(2)));
        });
        owner.Start();
        Assert.True(rented.Wait(Timeout));
        pool.Dispose();
        disposed.Set();
        Assert.True(owner.Join(Timeout));

        Assert.IsAssignableFrom<NativeAllocationException>(afterDispose);
        Assert.Equal(NativeOwnerLifecycle.Disposed, pool.GetStatistics().Lifecycle);
    }

    private readonly struct AllEqual : INativeSpanProcessor<int, int, int>
    {
        public static int Invoke(Span<int> span, in int state)
        {
            int count = 0;
            foreach (ref int value in span)
            {
                if (value == state)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
