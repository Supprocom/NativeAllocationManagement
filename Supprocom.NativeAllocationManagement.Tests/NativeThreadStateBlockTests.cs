namespace Supprocom.NativeAllocationManagement.Tests;

// NativeThreadState.Current finds the calling thread's state by stack block.
// These tests name the blocks through the test-only override, which also lets
// a thread take the block of one that ended, as a thread whose stack reuses
// that memory would. The table is process-wide, so they run alone. With the
// suite run as another runtime (-p:NamTestRuntimeMajor=11), Current reads the
// thread-static only, and the table checks are skipped.
[CollectionDefinition(Name, DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit requires collection-definition classes to be public.
public sealed class ThreadStateByBlockGroup
#pragma warning restore CA1515
{
    public const string Name = "Thread state by block";
}

[Collection(ThreadStateByBlockGroup.Name)]
public sealed class NativeThreadStateBlockTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // On CoreCLR 10, which this suite runs on, the lookup is on; a run that
    // forces another runtime version turns it off.
    [Fact]
    public void TheBlockLookupIsOnUnderCoreClr10Only()
    {
        object? forced = AppContext.GetData(NativeThreadEnded.RuntimeMajorVersionSwitch);
        if (forced is null)
        {
            Assert.Equal(10, Environment.Version.Major);
            Assert.True(NativeThreadEnded.CanRead);
        }
        else
        {
            Assert.Equal(Convert.ToInt32(forced, System.Globalization.CultureInfo.InvariantCulture), NativeThreadEnded.RuntimeMajorVersion);
        }

        Assert.Equal(NativeThreadEnded.RuntimeMajorVersion == 10, NativeThreadEnded.CanRead);
    }

    [Fact]
    public void TheRuntimeMarksAThreadEndedOnceItIsJoined()
    {
        Thread thread = new(static () => { });
        thread.Start();
        Assert.True(thread.Join(Timeout));
        Assert.True(NativeThreadEnded.Read(thread));
        Assert.False(NativeThreadEnded.Read(Thread.CurrentThread));
    }

    // Eight blocks, one per state slot: one miss each, then hits.
    [Fact]
    public void AThreadHitsFromEachOfEightBlocksAfterOneMissEach()
    {
        Assert.True(NativeStackBlockForTest.Enabled);
        RunOnNewThread(() =>
        {
            NativeThreadState own = NativeThreadState.FromThreadStaticForTest;
            nuint first = (nuint)0x3456_7800;
            int before = own.BlockMisses;
            for (int pass = 0; pass < 2; pass++)
            {
                for (nuint offset = 0; offset < 8; offset++)
                {
                    using (new OverrideBlock(first + offset))
                    {
                        Assert.Same(own, NativeThreadState.Current);
                    }
                }
            }

            Assert.Equal(NativeThreadEnded.CanRead ? 8 : 0, own.BlockMisses - before);
        });
    }

    // A thread that ended left its state in a block's slot. A new thread at
    // that block finds it there, refuses it and takes its own.
    [Fact]
    public void ANewThreadAtAnEndedThreadsBlockGetsItsOwnState()
    {
        nuint block = (nuint)0x4567_8900;
        using ManualResetEventSlim nextReady = new(false);
        using ManualResetEventSlim firstEnded = new(false);
        NativeThreadState? ended = null;
        NativeThreadState? found = null;
        NativeThreadState? own = null;
        int misses = -1;
        Exception? failure = null;

        // The next thread has its state before the first one ends, so no
        // registration releases the ended state before the lookup.
        Thread next = new(() => failure = Capture(() =>
        {
            own = NativeThreadState.FromThreadStaticForTest;
            nextReady.Set();
            Assert.True(firstEnded.Wait(Timeout));
            using (new OverrideBlock(block))
            {
                int before = own.BlockMisses;
                found = NativeThreadState.Current;
                misses = own.BlockMisses - before;
            }
        }));
        next.Start();
        try
        {
            Assert.True(nextReady.Wait(Timeout));
            Thread first = new(() =>
            {
                using (new OverrideBlock(block))
                {
                    ended = NativeThreadState.Current;
                }
            });
            first.Start();
            Assert.True(first.Join(Timeout));
            Assert.True(NativeThreadEnded.Read(first));
            if (NativeThreadEnded.CanRead)
            {
                Assert.Same(ended, NativeThreadState.AtBlockSlotForTest(block));
            }
        }
        finally
        {
            firstEnded.Set();
        }

        Assert.True(next.Join(Timeout));
        Assert.Null(failure);
        Assert.NotNull(ended);
        Assert.NotSame(ended, found);
        Assert.Same(own, found);
        Assert.Equal(NativeThreadEnded.CanRead ? 1 : 0, misses);
    }

    // Two live threads whose blocks share a slot take it from each other, and
    // each finds only its own state.
    [Fact]
    public void LiveThreadsWhoseBlocksShareASlotEachGetTheirOwnState()
    {
        nuint one = (nuint)0x5678_9A00;
        nuint other = one + 1024;
        while (NativeThreadState.BlockSlotForTest(other) != NativeThreadState.BlockSlotForTest(one))
        {
            other += 1024;
        }

        using SemaphoreSlim firstTurn = new(1);
        using SemaphoreSlim secondTurn = new(0);
        Exception? firstFailure = null;
        Exception? secondFailure = null;
        Thread first = new(() => firstFailure = Capture(() => TakeTurns(one, firstTurn, secondTurn)));
        Thread second = new(() => secondFailure = Capture(() => TakeTurns(other, secondTurn, firstTurn)));
        first.Start();
        second.Start();
        Assert.True(first.Join(Timeout));
        Assert.True(second.Join(Timeout));
        Assert.Null(firstFailure);
        Assert.Null(secondFailure);
    }

    // NativeEndedThreads releases an ended thread's state and drops it from
    // the table.
    [Fact]
    public void ReleasingAnEndedThreadDropsItsStateFromTheTable()
    {
        if (!NativeThreadEnded.CanRead)
        {
            return;
        }

        nuint block = (nuint)0x6789_AB00;
        NativeThreadState ended = StateOfEndedThreadAt(block);
        Assert.Same(ended, NativeThreadState.AtBlockSlotForTest(block));
        NativeEndedThreads.ReleaseEndedForTest();
        Assert.NotSame(ended, NativeThreadState.AtBlockSlotForTest(block));
    }

    // A full collection drops ended threads' states that no registration
    // released, so the table keeps them no longer than before.
    [Fact]
    public void AFullCollectionDropsEndedThreadsStatesFromTheTable()
    {
        if (!NativeThreadEnded.CanRead)
        {
            return;
        }

        nuint block = (nuint)0x789A_BC00;
        NativeThreadState ended = StateOfEndedThreadAt(block);
        Assert.Same(ended, NativeThreadState.AtBlockSlotForTest(block));
        for (int collection = 0; collection < 2; collection++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.NotSame(ended, NativeThreadState.AtBlockSlotForTest(block));
    }

    private static void TakeTurns(nuint block, SemaphoreSlim mine, SemaphoreSlim theirs)
    {
        NativeThreadState own = NativeThreadState.FromThreadStaticForTest;
        for (int round = 0; round < 3; round++)
        {
            Assert.True(mine.Wait(Timeout));
            using (new OverrideBlock(block))
            {
                Assert.Same(own, NativeThreadState.Current);
            }

            theirs.Release();
        }
    }

    private static NativeThreadState StateOfEndedThreadAt(nuint block)
    {
        NativeThreadState? state = null;
        Thread thread = new(() =>
        {
            using (new OverrideBlock(block))
            {
                state = NativeThreadState.Current;
            }
        });
        thread.Start();
        Assert.True(thread.Join(Timeout));
        Assert.NotNull(state);
        return state;
    }

    private static void RunOnNewThread(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() => failure = Capture(action));
        thread.Start();
        Assert.True(thread.Join(Timeout));
        Assert.Null(failure);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "A worker's failure is asserted on the test thread.")]
    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private readonly struct OverrideBlock : IDisposable
    {
        private readonly nuint _previous;

        internal OverrideBlock(nuint block)
        {
            _previous = NativeStackBlockForTest.Override;
            NativeStackBlockForTest.Override = block;
        }

        public void Dispose() => NativeStackBlockForTest.Override = _previous;
    }
}
