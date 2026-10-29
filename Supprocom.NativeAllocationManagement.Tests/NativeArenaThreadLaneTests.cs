namespace Supprocom.NativeAllocationManagement.Tests;

// Threads other than a synchronized arena's own fast thread scratch from
// private chunks of its shared segments. These tests race them against each
// other and against the operations that revoke their chunks.
public sealed class NativeArenaThreadLaneTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void ThreadsScratchingOneArenaAtOnceKeepTheirOwnData()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: 4096);
        const int workers = 4;
        const int frames = 40;
        using Barrier frameStart = new(workers + 1);
        using Barrier frameEnd = new(workers + 1);
        InvalidOperationException?[] failures = new InvalidOperationException?[workers];
        Thread[] threads = new Thread[workers];
        for (int worker = 0; worker < workers; worker++)
        {
            int marker = worker + 1;
            threads[worker] = new Thread(() =>
            {
                for (int frame = 0; frame < frames; frame++)
                {
                    frameStart.SignalAndWait();
                    try
                    {
                        for (int iteration = 0; iteration < 200; iteration++)
                        {
                            int length = 1 + ((iteration * 5 + marker) & 63);
                            ConcurrentArenaLease<int> first = arena.Scratch<int>(length, writer => writer.Fill(marker));
                            ConcurrentArenaLease<int> second = arena.Scratch<int>(length, writer => writer.Fill(marker));
                            int seen = first.Read(view => Count(view.AsSpan(), marker))
                                + second.Read(view => Count(view.AsSpan(), marker));
                            if (seen != 2 * length)
                            {
                                throw new InvalidOperationException($"Thread {marker} saw {seen} of {2 * length} own values.");
                            }
                        }
                    }
                    catch (InvalidOperationException exception)
                    {
                        failures[marker - 1] ??= exception;
                    }

                    frameEnd.SignalAndWait();
                }
            });
            threads[worker].Start();
        }

        for (int frame = 0; frame < frames; frame++)
        {
            frameStart.SignalAndWait();
            _ = arena.GetStatistics();
            frameEnd.SignalAndWait();
            arena.ReleaseLeasesToNativeMemory();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(Timeout));
        }

        Assert.All(failures, static failure => Assert.Null(failure));
        Assert.Equal(0, arena.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(0, arena.CurrentInitializationCountForTest);
    }

    [Fact]
    public void LaneScratchCountsItsPayloadExactly()
    {
        using NativeConcurrentArena arena = new();
        long expected = 0;
        Thread owner = new(() =>
        {
            for (int length = 1; length <= 40; length++)
            {
                ConcurrentArenaLease<long> lease = arena.Scratch<long>(length, writer => writer.Fill(length));
                Assert.Equal(length, lease.Read(static view => view[^1]));
                expected += 8L * length;
            }
        });
        owner.Start();
        Assert.True(owner.Join(Timeout));

        NativeOwnerStatistics statistics = arena.GetStatistics();
        Assert.Equal(expected, statistics.InitializedPayloadBytes);
        Assert.Equal(expected, statistics.PeakInitializedPayloadBytes);
        arena.ReleaseLeasesToNativeMemory();
        Assert.Equal(0, arena.GetStatistics().InitializedPayloadBytes);
    }

    [Fact]
    public void AFailedLaneInitializerLeavesNoPayloadAndTheLaneKeepsWorking()
    {
        using NativeConcurrentArena arena = new();
        Exception? failure = null;
        int after = 0;
        Thread owner = new(() =>
        {
            _ = arena.Scratch<int>(4, static writer => writer.Fill(1));
            failure = Record.Exception(() => arena.Scratch<int>(8, static writer =>
            {
                writer.Write(2);
                throw new InvalidOperationException("Injected lane initializer failure.");
            }));
            ConcurrentArenaLease<int> next = arena.Scratch<int>(8, static writer => writer.Fill(3));
            after = next.Read(static view => view[7]);
        });
        owner.Start();
        Assert.True(owner.Join(Timeout));

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(3, after);
        Assert.Equal(48, arena.GetStatistics().InitializedPayloadBytes);
        Assert.Equal(0, arena.CurrentInitializationCountForTest);
    }

    [Fact]
    public void ALaneInitializerRunningDuringRevocationCompletesThroughTheGeneration()
    {
        using NativeConcurrentArena arena = new();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        int result = 0;
        Thread owner = new(() =>
        {
            ConcurrentArenaLease<int> lease = arena.Scratch<int>(3, writer =>
            {
                entered.Set();
                Assert.True(release.Wait(Timeout));
                writer.Fill(6);
            });
            result = lease.Read(static view => view[2]);
        });
        owner.Start();
        Assert.True(entered.Wait(Timeout));

        Assert.Equal(1, arena.CurrentInitializationCountForTest);
        Assert.Throws<NativeAllocationInUseException>(arena.ReleaseLeasesToNativeMemory);
        release.Set();
        Assert.True(owner.Join(Timeout));

        Assert.Equal(6, result);
        Assert.Equal(0, arena.CurrentInitializationCountForTest);
        Assert.Equal(12, arena.GetStatistics().InitializedPayloadBytes);
        arena.ReleaseLeasesToNativeMemory();
    }

    [Fact]
    public void ReleasingLeasesInvalidatesAnotherThreadsLaneScratch()
    {
        using NativeConcurrentArena arena = new();
        using ManualResetEventSlim scratched = new(false);
        using ManualResetEventSlim released = new(false);
        NativeAllocationException? staleRead = null;
        int renewed = 0;
        Thread owner = new(() =>
        {
            ConcurrentArenaLease<int> lease = arena.Scratch<int>(2, static writer => writer.Fill(4));
            scratched.Set();
            Assert.True(released.Wait(Timeout));
            try
            {
                _ = lease.Read(static view => view[0]);
            }
            catch (NativeAllocationException exception)
            {
                staleRead = exception;
            }

            ConcurrentArenaLease<int> next = arena.Scratch<int>(2, static writer => writer.Fill(9));
            renewed = next.Read(static view => view[1]);
        });
        owner.Start();
        Assert.True(scratched.Wait(Timeout));
        arena.ReleaseLeasesToNativeMemory();
        released.Set();
        Assert.True(owner.Join(Timeout));

        Assert.NotNull(staleRead);
        Assert.Equal(9, renewed);
    }

    [Fact]
    public void ABudgetedArenaTakesBackAnotherThreadsChunkTailBeforeGrowing()
    {
        NativeMemoryBudget budget = new(64 * 1024);
        using NativeConcurrentArena arena = new(budget, 0, NativeMemoryReturn.Free, false);
        Thread first = new(() => _ = arena.Scratch<byte>(16, static writer => writer.Fill(1)));
        first.Start();
        Assert.True(first.Join(Timeout));
        int segmentsAfterFirst = arena.GetStatistics().SegmentCount;

        Thread second = new(() => _ = arena.Scratch<byte>(16, static writer => writer.Fill(2)));
        second.Start();
        Assert.True(second.Join(Timeout));
        Assert.Equal(segmentsAfterFirst, arena.GetStatistics().SegmentCount);
    }

    [Fact]
    public void ALaneOperationRunningDuringRevocationExitsThroughTheGeneration()
    {
        using NativeConcurrentArena arena = new();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        int result = 0;
        Thread owner = new(() =>
        {
            ConcurrentArenaLease<int> lease = arena.Scratch<int>(3, static writer => writer.Fill(4));
            result = lease.Read(view =>
            {
                entered.Set();
                Assert.True(release.Wait(Timeout));
                return view[2];
            });
            lease.CopyFrom([5, 6, 7]);
            Span<int> copy = stackalloc int[3];
            lease.CopyTo(copy);
            result += copy[2];
            lease.Clear();
            result += lease.Read(static view => view[0]);
        });
        owner.Start();
        Assert.True(entered.Wait(Timeout));

        Assert.Equal(1, arena.CurrentGenerationActiveOperationsForTest);
        Assert.Throws<NativeAllocationInUseException>(arena.ReleaseLeasesToNativeMemory);
        release.Set();
        Assert.True(owner.Join(Timeout));

        Assert.Equal(11, result);
        Assert.Equal(0, arena.CurrentGenerationActiveOperationsForTest);
        arena.ReleaseLeasesToNativeMemory();
    }

    [Fact]
    public void ALaneOperationAfterReleaseIsRejected()
    {
        using NativeConcurrentArena arena = new();
        using ManualResetEventSlim scratched = new(false);
        using ManualResetEventSlim released = new(false);
        int rejected = 0;
        Thread owner = new(() =>
        {
            ConcurrentArenaLease<int> lease = arena.Scratch<int>(2, static writer => writer.Fill(1));
            scratched.Set();
            Assert.True(released.Wait(Timeout));
            try
            {
                lease.Clear();
            }
            catch (NativeAllocationException)
            {
                rejected++;
            }

            try
            {
                lease.CopyTo(new int[2]);
            }
            catch (NativeAllocationException)
            {
                rejected++;
            }
        });
        owner.Start();
        Assert.True(scratched.Wait(Timeout));
        arena.ReleaseLeasesToNativeMemory();
        released.Set();
        Assert.True(owner.Join(Timeout));
        Assert.Equal(2, rejected);
        Assert.Equal(0, arena.CurrentGenerationActiveOperationsForTest);
    }

    private static int Count(Span<int> span, int marker)
    {
        int count = 0;
        foreach (ref int value in span)
        {
            if (value == marker)
            {
                count++;
            }
        }

        return count;
    }
}
