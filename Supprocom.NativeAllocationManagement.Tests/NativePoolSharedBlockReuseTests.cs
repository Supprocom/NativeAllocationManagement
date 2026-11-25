using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

// NativePool<T>.Shared finds its pool by the caller's stack block, without a
// thread-static read. A thread whose stack reuses a dead thread's block, before
// that thread's pools were released, must never reach the dead thread's pool:
// its own Shared call makes a fresh pool, and a stored reference to the dead
// thread's pool fails the owner check like any other thread's. Like
// NativeThreadStateBlockTests, they name blocks no stack can have and run alone.
[Collection(ThreadStateByBlockGroup.Name)]
public sealed class NativePoolSharedBlockReuseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static nuint Block(ulong offset) => (nuint)(NativeThreadState.FirstBlockForTest + offset);

    [Fact]
    public void ANewThreadOnADeadThreadsBlockGetsItsOwnSharedPool()
    {
        if (!NativeThreadEnded.CanRead)
        {
            return;
        }

        nuint block = Block(0x5678_9A00);
        using ManualResetEventSlim nextReady = new(false);
        using ManualResetEventSlim firstEnded = new(false);
        NativePool<int>? dead = null;
        NativePool<int>? fresh = null;
        Exception? failure = null;

        // The second thread has its state before the first ends, so no
        // registration releases the first thread's pools before the lookup.
        Thread next = new(() => failure = Capture(() =>
        {
            _ = NativeThreadState.FromThreadStaticForTest;
            nextReady.Set();
            Assert.True(firstEnded.Wait(Timeout));
            NativeThreadState.BlockForTest = block;
            try
            {
                fresh = NativePool<int>.Shared;
                using Pooled<int> lease = fresh.Rent(8, 3);
                Assert.Equal(3, lease.AsSpan()[7]);
            }
            finally
            {
                NativeThreadState.BlockForTest = 0;
            }
        }));
        next.Start();
        Assert.True(nextReady.Wait(Timeout));
        Thread first = new(() =>
        {
            NativeThreadState.BlockForTest = block;
            dead = NativePool<int>.Shared;
            dead.Rent(8, 1).Dispose();
        });
        first.Start();
        Assert.True(first.Join(Timeout));
        firstEnded.Set();
        Assert.True(next.Join(Timeout));
        Assert.Null(failure);
        Assert.NotNull(dead);
        Assert.NotNull(fresh);
        Assert.NotSame(dead, fresh);
        Assert.Equal(NativeOwnerLifecycle.Active, dead.CurrentLifecycle);
    }

    [Fact]
    public void ADeadThreadsSharedPoolFailsTheOwnerCheckOnAThreadReusingItsBlock()
    {
        if (!NativeThreadEnded.CanRead)
        {
            return;
        }

        using ManualResetEventSlim nextReady = new(false);
        using ManualResetEventSlim firstEnded = new(false);
        NativePool<int>? dead = null;
        Exception? failure = null;
        NativeAllocationStateException? refused = null;
        bool rented = false;

        Thread next = new(() => failure = Capture(() =>
        {
            _ = NativeThreadState.FromThreadStaticForTest;
            nextReady.Set();
            Assert.True(firstEnded.Wait(Timeout));
            refused = RentOnTheOwnersBlock(dead!, out rented);
        }));
        next.Start();
        Assert.True(nextReady.Wait(Timeout));
        Thread first = new(() =>
        {
            dead = NativePool<int>.Shared;
            dead.Rent(8, 1).Dispose();
        });
        first.Start();
        Assert.True(first.Join(Timeout));
        firstEnded.Set();
        Assert.True(next.Join(Timeout));
        Assert.Null(failure);
        Assert.False(rented);
        Assert.IsType<NativeAllocationStateException>(refused);
        Assert.IsAssignableFrom<InvalidOperationException>(refused);
    }

    // This thread is taken to run on the dead owner's stack block: the pool
    // records the block this frame is in.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeAllocationStateException? RentOnTheOwnersBlock(NativePool<int> pool, out bool rented)
    {
        pool.SetOwnerBlockForTest(NativeOwnerThread.CurrentBlock());
        rented = false;
        try
        {
            pool.Rent(4, 2).Dispose();
            rented = true;
            return null;
        }
        catch (NativeAllocationStateException exception)
        {
            return exception;
        }
    }

    [Fact]
    public void LiveThreadsKeepSeparateSharedPoolsAndRefuseEachOthers()
    {
        using ManualResetEventSlim bothHaveOne = new(false);
        using ManualResetEventSlim done = new(false);
        NativePool<int>? other = null;
        Exception? failure = null;
        Thread thread = new(() => failure = Capture(() =>
        {
            other = NativePool<int>.Shared;
            using (Pooled<int> lease = other.Rent(16, 9))
            {
                Assert.Equal(9, lease.AsSpan()[15]);
            }

            bothHaveOne.Set();
            Assert.True(done.Wait(Timeout));
            Assert.Same(other, NativePool<int>.Shared);
        }));
        thread.Start();
        NativePool<int> mine = NativePool<int>.Shared;
        Assert.True(bothHaveOne.Wait(Timeout));
        Assert.NotSame(mine, other);
        Assert.Same(mine, NativePool<int>.Shared);
        Assert.Throws<NativeAllocationStateException>(() => other!.Rent(4, 1));
        using (Pooled<int> lease = mine.Rent(16, 5))
        {
            Assert.Equal(5, lease.AsSpan()[0]);
        }

        done.Set();
        Assert.True(thread.Join(Timeout));
        Assert.Null(failure);
    }

    [Fact]
    public void ADisposedSharedPoolIsReplacedAndTheNewOneWorks()
    {
        NativePool<int> first = NativePool<int>.Shared;
        first.Dispose();
        NativePool<int> second = NativePool<int>.Shared;
        Assert.NotSame(first, second);
        using Pooled<int> lease = second.Rent(8, 4);
        Assert.Equal(4, lease.AsSpan()[7]);
        Assert.Throws<NativeAllocationDisposedException>(() => first.Rent(4, 1));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The worker failure is checked on the test thread.")]
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
}
