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
        using NativeConcurrentPool<int> pool = new(budget, 0, 0, NativeMemoryReturn.Free, false);
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
    public void TransfersAnotherThreadReturnsServeTheRentingThreadsNextRents()
    {
        Assert.Equal(SegmentsAfterPipelineRounds(1), SegmentsAfterPipelineRounds(20));
    }

    // A long-lived producer rents a batch of transfers, a long-lived consumer
    // processes and returns them, and the producer rents the next batch.
    private static int SegmentsAfterPipelineRounds(int rounds)
    {
        using NativeConcurrentPool<int> pool = new();
        NativeTransfer<int>[] batch = new NativeTransfer<int>[32];
        using SemaphoreSlim filled = new(0);
        using SemaphoreSlim drained = new(0);
        Exception? failure = null;
        Thread producer = new(() =>
        {
            try
            {
                for (int round = 0; round < rounds; round++)
                {
                    for (int index = 0; index < batch.Length; index++)
                    {
                        batch[index] = pool.RentTransferable(16, (round * 100) + index);
                    }

                    filled.Release();
                    if (!drained.Wait(Timeout))
                    {
                        throw new InvalidOperationException("The consumer did not return the batch.");
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or NativeAllocationException)
            {
                failure ??= exception;
                filled.Release();
            }
        });
        Thread consumer = new(() =>
        {
            try
            {
                for (int round = 0; round < rounds; round++)
                {
                    if (!filled.Wait(Timeout))
                    {
                        throw new InvalidOperationException("The producer did not fill the batch.");
                    }

                    for (int index = 0; index < batch.Length; index++)
                    {
                        if (batch[index].Process<int, int, AllEqual>((round * 100) + index) != 16)
                        {
                            throw new InvalidOperationException($"Transfer {index} of round {round} held other values.");
                        }

                        batch[index].Dispose();
                    }

                    drained.Release();
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or NativeAllocationException)
            {
                failure ??= exception;
                drained.Release();
            }
        });
        producer.Start();
        consumer.Start();
        Assert.True(producer.Join(Timeout));
        Assert.True(consumer.Join(Timeout));
        Assert.Null(failure);
        NativeOwnerStatistics statistics = pool.GetStatistics();
        Assert.Equal(0, statistics.InitializedPayloadBytes);
        return statistics.SegmentCount;
    }

    [Fact]
    public void ATransferReturnedOnItsRentingThreadServesItsNextRentsOfEitherKind()
    {
        int single;
        using (NativeConcurrentPool<int> reference = new())
        {
            using (reference.RentTransferable(16, 0))
            {
            }

            single = reference.GetStatistics().SegmentCount;
        }

        using NativeConcurrentPool<int> pool = new();
        for (int round = 0; round < 8; round++)
        {
            NativeTransfer<int> transfer = (round & 1) == 0
                ? pool.RentTransferable(16, round)
                : pool.RentTransferable(16, writer => writer.Fill(round));
            Assert.Equal(16, transfer.Process<int, int, AllEqual>(round));
            transfer.Dispose();
            using ConcurrentPooled<int> lease = pool.Rent(16, round + 1);
            Assert.Equal(16, lease.Process<int, int, AllEqual>(16, round + 1));
        }

        NativeOwnerStatistics statistics = pool.GetStatistics();
        Assert.Equal(single, statistics.SegmentCount);
        Assert.Equal(0, statistics.InitializedPayloadBytes);
    }

    [Fact]
    public void AThreadThatOnlyProcessesATransferKeepsItsOperationThroughARevocation()
    {
        using NativeConcurrentPool<int> pool = new();
        NativeTransfer<int> transfer = pool.RentTransferable(4, 5);
        BlockingProcessor.Entered.Reset();
        BlockingProcessor.Release.Reset();
        int result = 0;
        Thread consumer = new(() =>
        {
            result = transfer.Process<int, int, BlockingProcessor>(1);
            transfer.Dispose();
        });
        consumer.Start();
        Assert.True(BlockingProcessor.Entered.Wait(Timeout));

        Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Throws<NativeAllocationInUseException>(pool.ReleaseLeasesToNativeMemory);
        BlockingProcessor.Release.Set();
        Assert.True(consumer.Join(Timeout));

        Assert.Equal(6, result);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
        pool.ReleaseLeasesToNativeMemory();
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

    [Fact]
    public void AProcessorRunningDuringRevocationExitsThroughTheGeneration()
    {
        using NativeConcurrentPool<int> pool = new();
        BlockingProcessor.Entered.Reset();
        BlockingProcessor.Release.Reset();
        int result = 0;
        Thread owner = new(() =>
        {
            using ConcurrentPooled<int> lease = pool.Rent(4, static writer => writer.Fill(5));
            result = lease.Process<int, int, BlockingProcessor>(3, 1);
        });
        owner.Start();
        Assert.True(BlockingProcessor.Entered.Wait(Timeout));

        Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Throws<NativeAllocationInUseException>(pool.ReleaseLeasesToNativeMemory);
        BlockingProcessor.Release.Set();
        Assert.True(owner.Join(Timeout));

        Assert.Equal(6, result);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
        pool.ReleaseLeasesToNativeMemory();
    }

    [Fact]
    public void CachedLeaseOperationsCheckPrefixesAndStaleHandles()
    {
        using NativeConcurrentPool<int> pool = new();
        ConcurrentPooled<int> lease = pool.Rent(4, static writer => writer.Fill(2));
        Assert.Equal(4, lease.Length);
        Assert.True(lease.Capacity >= 4);
        Assert.Equal(4, lease.Process<int, int, SumProcessor>(2, 0));
        bool negative = false;
        bool tooLong = false;
        try
        {
            _ = lease.Process<int, int, SumProcessor>(-1, 0);
        }
        catch (ArgumentOutOfRangeException exception) when (string.Equals(exception.ParamName, "length", StringComparison.Ordinal))
        {
            negative = true;
        }

        try
        {
            _ = lease.Process<int, int, SumProcessor>(5, 0);
        }
        catch (ArgumentOutOfRangeException exception) when (string.Equals(exception.ParamName, "length", StringComparison.Ordinal))
        {
            tooLong = true;
        }

        Assert.True(negative);
        Assert.True(tooLong);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        lease.Dispose();
        bool stale = false;
        try
        {
            _ = lease.Process<int, int, SumProcessor>(2, 0);
        }
        catch (NativeAllocationException)
        {
            stale = true;
        }

        Assert.True(stale);
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
    }

    [Fact]
    public void AnOperationOutlivesARevocationAndRearmFromItsOwnCallback()
    {
        using NativeConcurrentPool<int> pool = new();
        ConcurrentPooled<int> lease = pool.Rent(2, static writer => writer.Fill(1));
        int nested = 0;
        lease.Access(_ =>
        {
            Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
            using ConcurrentPooled<int> again = pool.Rent(2, static writer => writer.Fill(3));
            nested = again.Read(static view => view[1]);
        });
        Assert.Equal(3, nested);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(1, lease.Read(static view => view[1]));
        lease.Dispose();
        Assert.Equal(0, pool.GetStatistics().InitializedPayloadBytes);
    }

    [Fact]
    public void AnOperationThatOutlivesAFoldExitsWhileAnotherThreadHoldsTheOwnerLock()
    {
        using NativeConcurrentPool<int> pool = new();
        using NativeConcurrentArena arena = new();
        Lock poolGate = OwnerGate(pool.KernelForTransfer);
        Lock arenaGate = OwnerGate(arena.KernelForInitialization);
        using CountdownEvent entered = new(2);
        using ManualResetEventSlim release = new(false);
        using CountdownEvent exited = new(2);
        int pooled = 0;
        int scratched = 0;
        Thread poolOwner = new(() =>
        {
            using ConcurrentPooled<int> lease = pool.Rent(2, static writer => writer.Fill(3));
            pooled = lease.Read(view =>
            {
                entered.Signal();
                Assert.True(release.Wait(Timeout));
                return view[0];
            });
            exited.Signal();
        });
        Thread arenaOwner = new(() =>
        {
            ConcurrentArenaLease<int> scratch = arena.Scratch<int>(2, static writer => writer.Fill(4));
            scratched = scratch.Read(view =>
            {
                entered.Signal();
                Assert.True(release.Wait(Timeout));
                return view[1];
            });
            exited.Signal();
        });
        poolOwner.Start();
        arenaOwner.Start();
        Assert.True(entered.Wait(Timeout));

        // Both operations are folded into their generations, then their owners'
        // locks are held while the operations end.
        Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(1, arena.CurrentGenerationActiveOperationsForTest);
        using ManualResetEventSlim held = new(false);
        using ManualResetEventSlim unlock = new(false);
        Thread holder = new(() =>
        {
            poolGate.Enter();
            arenaGate.Enter();
            held.Set();
            Assert.True(unlock.Wait(Timeout));
            arenaGate.Exit();
            poolGate.Exit();
        });
        holder.Start();
        Assert.True(held.Wait(Timeout));
        release.Set();
        bool exitedWhileHeld = exited.Wait(Timeout);
        unlock.Set();
        Assert.True(holder.Join(Timeout));
        Assert.True(poolOwner.Join(Timeout));
        Assert.True(arenaOwner.Join(Timeout));

        Assert.True(exitedWhileHeld);
        Assert.Equal(3, pooled);
        Assert.Equal(4, scratched);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(0, arena.CurrentGenerationActiveOperationsForTest);
    }

    private static Lock OwnerGate(NativeOwnerKernel kernel) =>
        (Lock)typeof(NativeOwnerKernel)
            .GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(kernel)!;

    private struct SumProcessor : INativeSpanProcessor<int, int, int>
    {
        public static int Invoke(Span<int> span, in int add)
        {
            int sum = add;
            foreach (ref int value in span)
            {
                sum += value;
            }

            return sum;
        }
    }

    private struct BlockingProcessor : INativeSpanProcessor<int, int, int>
    {
        internal static readonly ManualResetEventSlim Entered = new(false);
        internal static readonly ManualResetEventSlim Release = new(false);

        public static int Invoke(Span<int> span, in int add)
        {
            Entered.Set();
            Assert.True(Release.Wait(Timeout));
            return span[^1] + add;
        }
    }
}
