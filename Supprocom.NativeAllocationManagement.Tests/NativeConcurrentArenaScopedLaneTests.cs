namespace Supprocom.NativeAllocationManagement.Tests;

// A synchronized arena's own thread allocates from its lane without the owner
// lock, up to its segment's top. A scoped range another thread places under the
// lock takes a segment's top. These tests hold that thread after it chose a
// segment and before it moved the segment's top, let the owner fill its lane,
// and check that no range overlaps another. The hook is process-wide, so they
// run alone.
[CollectionDefinition(Name, DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit requires collection-definition classes to be public.
public sealed class ConcurrentArenaScopedLaneGroup
#pragma warning restore CA1515
{
    public const string Name = "Concurrent arena scoped lanes";
}

[Collection(ConcurrentArenaScopedLaneGroup.Name)]
public sealed class NativeConcurrentArenaScopedLaneTests
{
    private const int SegmentBytes = 64 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void AWorkersScopedRangeDoesNotOverlapTheOwnersLane()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: SegmentBytes);
        int capacity = checked((int)arena.GetStatistics().UsableCapacityBytes);
        Assert.True(capacity >= SegmentBytes);

        // The owner's first scratch makes the preallocated segment its lane.
        ConcurrentArenaLease<byte> first = arena.Scratch<byte>(1, static writer => writer.Fill(1));
        using ManualResetEventSlim workerPlacing = new(false);
        using ManualResetEventSlim ownerDone = new(false);
        int workerWrong = -1;
        Exception? failure = null;
        NativeMemoryTestHooks.SetBeforeScopedCursorWrite(() =>
        {
            workerPlacing.Set();
            Assert.True(ownerDone.Wait(Timeout));
        });
        try
        {
            Thread worker = new(() => failure = Capture(() =>
            {
                ConcurrentArenaLease<int> scoped = arena.ScratchScoped<int>(1024, static writer => writer.Fill(7));
                workerWrong = scoped.Read(static view => CountOther(view.AsSpan(), 7));
            }));
            worker.Start();
            Assert.True(workerPlacing.Wait(Timeout));

            // The owner fills all but 512 bytes of its lane, without the owner
            // lock: past the 4,096 bytes the worker's range needs at the top.
            int ownerWrong;
            try
            {
                ConcurrentArenaLease<byte> rest = arena.Scratch<byte>(capacity - 512, static writer => writer.Fill(9));
                ownerDone.Set();
                Assert.True(worker.Join(Timeout));
                ownerWrong = rest.Read(static view => CountOther(view.AsSpan(), (byte)9));
            }
            finally
            {
                ownerDone.Set();
            }

            Assert.Null(failure);
            Assert.Equal(0, workerWrong);
            Assert.Equal(0, ownerWrong);
            Assert.Equal((byte)1, first.Read(static view => view[0]));
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    // The same through a scoped group: four ranges placed together from one
    // source lease.
    [Fact]
    public void AWorkersScopedGroupDoesNotOverlapTheOwnersLane()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: SegmentBytes);
        int capacity = checked((int)arena.GetStatistics().UsableCapacityBytes);
        Assert.True(capacity >= SegmentBytes);
        ConcurrentArenaLease<byte> first = arena.Scratch<byte>(1, static writer => writer.Fill(1));
        using ManualResetEventSlim workerPlacing = new(false);
        using ManualResetEventSlim ownerDone = new(false);
        int workerWrong = -1;
        Exception? failure = null;
        NativeMemoryTestHooks.SetBeforeScopedCursorWrite(() =>
        {
            workerPlacing.Set();
            Assert.True(ownerDone.Wait(Timeout));
        });
        try
        {
            Thread worker = new(() => failure = Capture(() =>
            {
                using NativePool<int> pool = new();
                using Pooled<int> source = pool.Rent(1, static writer => writer.Fill(7));
                NativeLeaseOperations.InitializeScoped(
                    source,
                    arena,
                    256,
                    256,
                    256,
                    256,
                    static (view, a, b, c, d) =>
                    {
                        int value = view[0];
                        a.Fill(value);
                        b.Fill(value);
                        c.Fill(value);
                        d.Fill(value);
                    },
                    out ConcurrentArenaLease<int> one,
                    out ConcurrentArenaLease<int> two,
                    out ConcurrentArenaLease<int> three,
                    out ConcurrentArenaLease<int> four);
                workerWrong = one.Read(static view => CountOther(view.AsSpan(), 7))
                    + two.Read(static view => CountOther(view.AsSpan(), 7))
                    + three.Read(static view => CountOther(view.AsSpan(), 7))
                    + four.Read(static view => CountOther(view.AsSpan(), 7));
            }));
            worker.Start();
            Assert.True(workerPlacing.Wait(Timeout));

            int ownerWrong;
            try
            {
                ConcurrentArenaLease<byte> rest = arena.Scratch<byte>(capacity - 512, static writer => writer.Fill(9));
                ownerDone.Set();
                Assert.True(worker.Join(Timeout));
                ownerWrong = rest.Read(static view => CountOther(view.AsSpan(), (byte)9));
            }
            finally
            {
                ownerDone.Set();
            }

            Assert.Null(failure);
            Assert.Equal(0, workerWrong);
            Assert.Equal(0, ownerWrong);
            Assert.Equal((byte)1, first.Read(static view => view[0]));
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
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

    private static int CountOther<T>(ReadOnlySpan<T> values, T expected)
        where T : unmanaged, IEquatable<T>
    {
        int count = 0;
        foreach (ref readonly T value in values)
        {
            if (!value.Equals(expected))
            {
                count++;
            }
        }

        return count;
    }
}
