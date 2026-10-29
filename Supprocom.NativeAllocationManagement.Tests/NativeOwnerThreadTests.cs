using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

// The owner-thread check of pools, arenas and regions (NativeOwnerThread): the
// 4 KiB stack block where the owner thread was last confirmed, and otherwise
// the thread serial. These tests start and end threads and wait for managed
// thread ids to be recycled, so they run alone.
[Collection(EndedThreadGroup.Name)]
public sealed class NativeOwnerThreadTests
{
    [Fact]
    public void TheOwnerThreadPassesByTheStackBlock()
    {
        NativeThreadState thread = NativeThreadState.Current;
        OwnerBox box = new() { Owner = new NativeOwnerThread(thread) };

        // The first check may come from another block than the constructor's
        // frame; the second comes from the same frame and must not miss.
        Assert.True(Check(box));
        int slowChecks = thread.OwnerThreadSlowChecks;
        Assert.True(Check(box));
        Assert.Equal(slowChecks, thread.OwnerThreadSlowChecks);
    }

    // A region is a ref struct, which no other thread can reach in safe code;
    // its kernel's check is the same struct.
    [Fact]
    public void AnotherLiveThreadIsRefused()
    {
        OwnerBox box = new() { Owner = new NativeOwnerThread(NativeThreadState.Current) };
        Assert.True(Check(box));
        using NativePool<int> pool = new();
        using NativeArena arena = new();
        using (pool.Rent(4, 1))
        {
        }

        _ = arena.Scratch<int>(4, 1);

        bool passed = true;
        Exception? poolFailure = null, arenaFailure = null;
        TestThread.Run(() =>
        {
            passed = Check(box);
            poolFailure = Record.Exception(() => pool.Rent(4, 1).Dispose());
            arenaFailure = Record.Exception(() => arena.Scratch<int>(4, 1));
        });

        Assert.False(passed);
        Assert.IsType<NativeAllocationStateException>(poolFailure);
        Assert.IsType<NativeAllocationStateException>(arenaFailure);
        Assert.True(Check(box));
    }

    [Fact]
    public void ThreadSerialsAreNeverReused()
    {
        HashSet<long> serials = [NativeThreadState.Current.Serial];
        for (int index = 0; index < 32; index++)
        {
            Assert.True(serials.Add(TestThread.Run(static () => { }).Serial));
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    [Fact]
    public void AfterTheOwnerThreadExitsANewThreadWithItsManagedIdIsRefused()
    {
        Run owner = RunOwnerThread();
        owner.Box.Owner.ForgetBlockForTest();
        Collect();

        // Each new thread takes a free managed id. They stay alive until one is
        // given the exited owner thread's id, so no id is freed in between.
        using ManualResetEventSlim release = new();
        List<Thread> holders = [];
        Run? recycled = null;
        try
        {
            for (int attempt = 0; attempt < 64 && recycled is null; attempt++)
            {
                Collect();
                Run run = new(owner.Box) { CheckOnlyId = owner.ManagedThreadId, Hold = release };
                holders.Add(Start(run));
                run.Checked.Wait();
                if (run.ManagedThreadId == owner.ManagedThreadId)
                {
                    recycled = run;
                }
            }
        }
        finally
        {
            release.Set();
            foreach (ref readonly Thread holder in CollectionsMarshal.AsSpan(holders))
            {
                holder.Join();
            }
        }

        Assert.True(recycled is not null, "No new thread was given the exited owner thread's managed id.");
        Assert.False(recycled.Passed);
    }

    [Fact]
    public void ANewThreadOnAReusedStackIsTheOnlyOneThatCanPass()
    {
        for (int trial = 0; trial < 8; trial++)
        {
            Run owner = RunOwnerThread();
            using ManualResetEventSlim release = new();

            // The first may reuse the exited thread's stack; the second, started
            // while the first still runs, cannot reuse the same memory.
            Run first = new(owner.Box) { Hold = release };
            Run second = new(owner.Box) { Hold = release };
            Thread firstThread = Start(first);
            first.Checked.Wait();
            Thread secondThread = Start(second);
            second.Checked.Wait();
            release.Set();
            firstThread.Join();
            secondThread.Join();

            Assert.True(!first.Passed || !second.Passed, $"Two threads passed in trial {trial}.");
        }
    }

    // An owner whose thread confirmed it once more from the thread procedure's
    // frame and then exited.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Run RunOwnerThread()
    {
        Run run = new(new OwnerBox()) { Create = true };
        Start(run).Join();
        Assert.True(run.Passed);
        return run;
    }

    private static Thread Start(Run run)
    {
        Thread thread = new(ThreadProcedure) { IsBackground = true };
        thread.Start(run);
        return thread;
    }

    // One procedure for every thread, so a thread whose stack reuses an exited
    // thread's memory checks the owner from the same addresses.
    private static void ThreadProcedure(object? state)
    {
        Run run = (Run)state!;
        run.ManagedThreadId = Environment.CurrentManagedThreadId;
        if (run.Create)
        {
            run.Box.Owner = new NativeOwnerThread(NativeThreadState.Current);
        }

        if (run.CheckOnlyId == 0 || run.CheckOnlyId == run.ManagedThreadId)
        {
            run.Passed = Check(run.Box);
        }

        run.Checked.Set();
        run.Hold?.Wait();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Check(OwnerBox box) => box.Owner.IsCurrent();

    private static void Collect()
    {
        for (int round = 0; round < 2; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class OwnerBox
    {
        internal NativeOwnerThread Owner;
    }

    private sealed class Run(OwnerBox box)
    {
        internal OwnerBox Box { get; } = box;
        internal bool Create { get; init; }
        internal int CheckOnlyId { get; init; }
        internal ManualResetEventSlim? Hold { get; init; }
        internal ManualResetEventSlim Checked { get; } = new();
        internal int ManagedThreadId { get; set; }
        internal bool Passed { get; set; }
    }
}
