using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement.Tests;

// Threads initialize ranges of one synchronized arena at the same time; only an
// initializer nested in one its own thread runs is refused. A failed initializer
// gives its range back only while nothing was placed after it, so lanes and
// other threads' ranges placed meanwhile keep their memory. A fault hook set
// anywhere in the process turns lanes off, so this runs alone.
[Collection(ProcessCounterGroup.Name)]
public sealed class NativeConcurrentArenaInitializerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void ThreadsInitializingReferenceRangesAtOnceAreBothAdmitted()
    {
        NativeConcurrentArena arena = ArenaOwnedByAnEndedThread(4096);
        using ManualResetEventSlim firstEntered = new(false);
        using ManualResetEventSlim secondDone = new(false);
        string? first = null;
        string? second = null;
        Exception? failure = null;
        Thread a = new(() => Capture(ref failure, () =>
        {
            ConcurrentArenaLease<string> lease = arena.Scratch<string>(2, writer =>
            {
                firstEntered.Set();
                Assert.True(secondDone.Wait(Timeout));
                writer.Fill("a");
            });
            first = lease.Read(static view => view[1]);
        }));
        Thread b = new(() => Capture(ref failure, () =>
        {
            Assert.True(firstEntered.Wait(Timeout));
            try
            {
                ConcurrentArenaLease<string> lease = arena.Scratch<string>(2, static writer => writer.Fill("b"));
                second = lease.Read(static view => view[1]);
            }
            finally
            {
                secondDone.Set();
            }
        }));
        RunBoth(a, b);

        Assert.Null(failure);
        Assert.Equal("a", first);
        Assert.Equal("b", second);
        arena.Dispose();
    }

    [Fact]
    public void ThreadsInitializingWithoutLanesAtOnceAreBothAdmitted()
    {
        NativeConcurrentArena arena = ArenaOwnedByAnEndedThread(4096);
        using ManualResetEventSlim firstEntered = new(false);
        using ManualResetEventSlim secondDone = new(false);
        int first = 0;
        int second = 0;
        bool anyLane = false;
        Exception? failure = null;

        // An operation hook turns lanes off: both ranges take the locked path.
        NativeMemoryTestHooks.SetOperationEntered(static _ => { });
        try
        {
            Thread a = new(() => Capture(ref failure, () =>
            {
                ConcurrentArenaLease<int> lease = arena.Scratch<int>(2, writer =>
                {
                    firstEntered.Set();
                    Assert.True(secondDone.Wait(Timeout));
                    writer.Fill(1);
                });
                anyLane |= lease.HasLaneCacheForTest;
                first = lease.Read(static view => view[1]);
            }));
            Thread b = new(() => Capture(ref failure, () =>
            {
                Assert.True(firstEntered.Wait(Timeout));
                try
                {
                    ConcurrentArenaLease<int> lease = arena.Scratch<int>(2, static writer => writer.Fill(2));
                    anyLane |= lease.HasLaneCacheForTest;
                    second = lease.Read(static view => view[1]);
                }
                finally
                {
                    secondDone.Set();
                }
            }));
            RunBoth(a, b);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }

        Assert.Null(failure);
        Assert.False(anyLane);
        Assert.Equal(1, first);
        Assert.Equal(2, second);
        arena.Dispose();
    }

    [Fact]
    public void AWorkerInitializesWhileTheOwnersLaneInitializerRuns()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: 4096);
        using ManualResetEventSlim ownerEntered = new(false);
        using ManualResetEventSlim workerDone = new(false);
        string? worker = null;
        Exception? failure = null;
        Thread thread = new(() => Capture(ref failure, () =>
        {
            Assert.True(ownerEntered.Wait(Timeout));
            try
            {
                ConcurrentArenaLease<string> lease = arena.Scratch<string>(2, static writer => writer.Fill("w"));
                worker = lease.Read(static view => view[0]);
            }
            finally
            {
                workerDone.Set();
            }
        }));
        thread.Start();

        // The arena's own thread scratches through its lane.
        ConcurrentArenaLease<int> owned = arena.Scratch<int>(2, writer =>
        {
            ownerEntered.Set();
            Assert.True(workerDone.Wait(Timeout));
            writer.Fill(5);
        });
        Assert.True(thread.Join(Timeout));

        Assert.Null(failure);
        Assert.Equal("w", worker);
        Assert.Equal(5, owned.Read(static view => view[1]));
    }

    [Fact]
    public void TheOwnersLaneScratchesWhileAWorkersInitializerRuns()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: 4096);
        using ManualResetEventSlim workerEntered = new(false);
        using ManualResetEventSlim ownerDone = new(false);
        string? worker = null;
        Exception? failure = null;
        Thread thread = new(() => Capture(ref failure, () =>
        {
            ConcurrentArenaLease<string> lease = arena.Scratch<string>(2, writer =>
            {
                workerEntered.Set();
                Assert.True(ownerDone.Wait(Timeout));
                writer.Fill("w");
            });
            worker = lease.Read(static view => view[0]);
        }));
        thread.Start();
        Assert.True(workerEntered.Wait(Timeout));

        int sum;
        try
        {
            ConcurrentArenaLease<int> initialized = arena.Scratch<int>(4, static writer => writer.Fill(3));
            ConcurrentArenaLease<int> filled = arena.Scratch(4, 4);
            sum = initialized.Read(static view => view[3]) + filled.Read(static view => view[3]);
        }
        finally
        {
            ownerDone.Set();
        }

        Assert.True(thread.Join(Timeout));
        Assert.Null(failure);
        Assert.Equal(7, sum);
        Assert.Equal("w", worker);
    }

    [Fact]
    public void AnInitializerNestedInOneOnTheSameThreadIsStillRefused()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: 4096);
        int refused = 0;
        Exception? failure = null;

        // A worker's locked initializer nests another locked one.
        Thread worker = new(() => Capture(ref failure, () =>
        {
            _ = arena.Scratch<string>(2, writer =>
            {
                try
                {
                    _ = arena.Scratch<string>(1, static nested => nested.Fill("n"));
                }
                catch (NativeAllocationInUseException)
                {
                    Interlocked.Increment(ref refused);
                }

                writer.Fill("w");
            });
        }));
        worker.Start();
        Assert.True(worker.Join(Timeout));

        // The owner's lane initializer nests a locked one, and the owner's
        // locked initializer (a range with references) nests a lane one.
        _ = arena.Scratch<int>(2, writer =>
        {
            try
            {
                _ = arena.Scratch<string>(1, static nested => nested.Fill("n"));
            }
            catch (NativeAllocationInUseException)
            {
                refused++;
            }

            writer.Fill(1);
        });
        _ = arena.Scratch<string>(2, writer =>
        {
            try
            {
                _ = arena.Scratch<int>(1, static nested => nested.Fill(2));
            }
            catch (NativeAllocationInUseException)
            {
                refused++;
            }

            writer.Fill("o");
        });

        Assert.Null(failure);
        Assert.Equal(3, refused);
    }

    // A fails its initializer after B's lane took a chunk after A's range in the
    // same segment; C then scratches. A's range stays a hole, so C's ranges do
    // not overlap B's, and the failure changes no statistic.
    [Fact]
    public void AFailedInitializerLeavesALanePlacedAfterItInPlace()
    {
        NativeConcurrentArena arena = ArenaOwnedByAnEndedThread(64 * 1024);
        RaceResult race = RaceAFailedInitializerAgainstALane(arena, fillFirstSegment: false);

        Assert.Equal(256, race.Sevens);
        AssertTheFailureChangedNothing(race);
        // B's 256 ints, C's 4,096 ints and 512 longs: A's hole counts as nothing.
        Assert.Equal((256 * 4) + (4096 * 4) + (512 * 8), race.AfterC.InitializedPayloadBytes);
        AssertTheReleaseReclaimsEverything(arena);
    }

    // A's failing range opened a new segment and B's lane took a chunk in it:
    // the segment stays, with A's range a hole.
    [Fact]
    public void AFailedInitializerKeepsTheSegmentItOpenedWhileALaneUsesIt()
    {
        NativeConcurrentArena arena = ArenaOwnedByAnEndedThread(4096);
        RaceResult race = RaceAFailedInitializerAgainstALane(arena, fillFirstSegment: true);

        Assert.Equal(256, race.Sevens);
        Assert.Equal(2, race.BeforeFailure.SegmentCount);
        AssertTheFailureChangedNothing(race);
        Assert.Equal((500 * IntPtr.Size) + (256 * 4) + (4096 * 4) + (512 * 8), race.AfterC.InitializedPayloadBytes);
        AssertTheReleaseReclaimsEverything(arena);
    }

    private static void AssertTheFailureChangedNothing(RaceResult race)
    {
        Assert.Equal(race.BeforeFailure.SegmentCount, race.AfterFailure.SegmentCount);
        Assert.Equal(race.BeforeFailure.RetainedBytes, race.AfterFailure.RetainedBytes);
        Assert.Equal(race.BeforeFailure.InitializedPayloadBytes, race.AfterFailure.InitializedPayloadBytes);
        Assert.Equal(race.BeforeFailure.RequestedBytes, race.AfterFailure.RequestedBytes);
    }

    private readonly record struct RaceResult(
        int Sevens,
        NativeOwnerStatistics BeforeFailure,
        NativeOwnerStatistics AfterFailure,
        NativeOwnerStatistics AfterC);

    // In order: A reserves and waits; B's lane takes a chunk; A's initializer
    // fails; C scratches; native blocks of a segment's size are written over; B
    // counts its values.
    private static RaceResult RaceAFailedInitializerAgainstALane(
        NativeConcurrentArena arena,
        bool fillFirstSegment)
    {
        using ManualResetEventSlim aEntered = new(false);
        using ManualResetEventSlim bScratched = new(false);
        using ManualResetEventSlim aProceeds = new(false);
        using ManualResetEventSlim aFailed = new(false);
        using ManualResetEventSlim cStarts = new(false);
        using ManualResetEventSlim cDone = new(false);
        using ManualResetEventSlim bReads = new(false);
        int sevens = -1;
        bool aRefused = false;
        Exception? failure = null;
        Thread a = new(() => Capture(ref failure, () =>
        {
            if (fillFirstSegment)
            {
                // 4,000 of the first segment's 4,096 bytes: the failing range
                // below does not fit and opens a new segment.
                _ = arena.Scratch<string>(500, static writer => writer.Fill("x"));
            }

            try
            {
                _ = arena.Scratch<string>(fillFirstSegment ? 64 : 2, writer =>
                {
                    aEntered.Set();
                    Assert.True(aProceeds.Wait(Timeout));
                    throw new InvalidOperationException("A's initializer fails.");
                });
            }
            catch (InvalidOperationException)
            {
                aRefused = true;
            }
            finally
            {
                aFailed.Set();
            }
        }));
        Thread b = new(() => Capture(ref failure, () =>
        {
            Assert.True(aEntered.Wait(Timeout));
            ConcurrentArenaLease<int> mine;
            try
            {
                mine = arena.Scratch<int>(256, static writer => writer.Fill(7));
            }
            finally
            {
                bScratched.Set();
            }

            Assert.True(mine.HasLaneCacheForTest);
            Assert.True(bReads.Wait(Timeout));
            sevens = mine.Read(static view =>
            {
                int count = 0;
                foreach (ref readonly int value in view.AsSpan())
                {
                    if (value == 7)
                    {
                        count++;
                    }
                }

                return count;
            });
        }));
        Thread c = new(() => Capture(ref failure, () =>
        {
            Assert.True(cStarts.Wait(Timeout));
            _ = arena.Scratch<int>(4096, static writer => writer.Fill(9));
            _ = arena.Scratch<long>(512, static writer => writer.Fill(9));
            cDone.Set();
        }));
        a.Start();
        b.Start();
        c.Start();
        Assert.True(bScratched.Wait(Timeout));
        NativeOwnerStatistics beforeFailure = arena.GetStatistics();
        aProceeds.Set();
        Assert.True(aFailed.Wait(Timeout));
        NativeOwnerStatistics afterFailure = arena.GetStatistics();
        cStarts.Set();
        Assert.True(cDone.Wait(Timeout));
        NativeOwnerStatistics afterC = arena.GetStatistics();

        // Blocks of freed native memory of a segment's size, written over: a
        // segment freed under B's lane would likely be handed out here.
        unsafe
        {
            for (int index = 0; index < 4; index++)
            {
                void* canary = NativeMemory.Alloc(8192);
                new Span<int>(canary, 2048).Fill(-1);
                NativeMemory.Free(canary);
            }
        }

        bReads.Set();
        Assert.True(a.Join(Timeout));
        Assert.True(b.Join(Timeout));
        Assert.True(c.Join(Timeout));
        Assert.Null(failure);
        Assert.True(aRefused);
        return new RaceResult(sevens, beforeFailure, afterFailure, afterC);
    }

    // The release resets every cursor, so the holes go with the generation: the
    // next ranges fill the first segment again without a fresh one.
    private static void AssertTheReleaseReclaimsEverything(NativeConcurrentArena arena)
    {
        arena.ReleaseLeasesToNativeMemory();
        NativeOwnerStatistics released = arena.GetStatistics();
        Assert.Equal(0, released.InitializedPayloadBytes);
        Assert.Equal(0, released.RequestedBytes);
        Exception? failure = null;
        Thread next = new(() => Capture(ref failure, () =>
        {
            _ = arena.Scratch<string>(512, static writer => writer.Fill("y"));
        }));
        next.Start();
        Assert.True(next.Join(Timeout));
        Assert.Null(failure);
        Assert.Equal(released.FreshSegmentAllocationCount, arena.GetStatistics().FreshSegmentAllocationCount);
        arena.Dispose();
    }

    private static NativeConcurrentArena ArenaOwnedByAnEndedThread(int preAllocateBytes)
    {
        NativeConcurrentArena arena = null!;
        Thread maker = new(() => arena = new NativeConcurrentArena(preAllocateBytes: (nuint)preAllocateBytes));
        maker.Start();
        Assert.True(maker.Join(Timeout));
        return arena;
    }

    private static void RunBoth(Thread a, Thread b)
    {
        a.Start();
        b.Start();
        Assert.True(a.Join(Timeout));
        Assert.True(b.Join(Timeout));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "A worker's failure is asserted on the test thread.")]
    private static void Capture(ref Exception? failure, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
    }
}
