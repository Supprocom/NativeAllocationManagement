using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class PersistentMapWorkersTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void CompletedPhasesPreserveWorkAndReleaseEveryWorker()
    {
        int[] visits = new int[11];
        int[] stopped = new int[3];
        using PersistentMapWorkers workers = new(3, visits.Length,
            index => Interlocked.Increment(ref stopped[index]));
        for (int phase = 0; phase < 3; phase++)
            workers.Run((worker, map) =>
            {
                Assert.Equal(map % 3, worker);
                visits[map]++;
            });
        Assert.All(visits, static count => Assert.Equal(3, count));
        workers.Dispose();
        workers.Dispose();
        Assert.True(workers.ResourcesReleased);
        Assert.Equal(0, workers.LiveWorkerCount);
        Assert.All(stopped, static count => Assert.Equal(1, count));
        Assert.Throws<ObjectDisposedException>(() => workers.Run(static (_, _) => { }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void PartialStartupStopsEveryActualThreadAndPreservesOriginalFailure(int failIndex)
    {
        PersistentMapWorkers? witness = null;
        int terminalCleanup = 0;
        int[] stopped = new int[3];
        InvalidOperationException original = new("injected start failure");
        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() =>
            new PersistentMapWorkers(3, 3, TestDeadline, TestDeadline, TestDeadline,
                index => Interlocked.Increment(ref stopped[index]),
                (workers, index) =>
                {
                    witness = workers;
                    if (index == failIndex) throw original;
                }, null, () => Interlocked.Increment(ref terminalCleanup)));
        Assert.Same(original, actual);
        Assert.NotNull(witness);
        try
        {
            Assert.True(witness.ResourcesReleased);
            Assert.Equal(0, witness.LiveWorkerCount);
            Assert.Equal(1, terminalCleanup);
            for (int index = 0; index < stopped.Length; index++)
                Assert.Equal(index < failIndex ? 1 : 0, stopped[index]);
        }
        finally { witness.Dispose(); }
    }

    [Fact]
    public void LateReadinessRetainsPrimitivesUntilTheWorkerActuallyExits()
    {
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        PersistentMapWorkers? witness = null;
        int terminalCleanup = 0;
        try
        {
            AggregateException failure = Assert.Throws<AggregateException>(() =>
                new PersistentMapWorkers(1, 1, ShortDeadline, TestDeadline, ShortDeadline, null,
                    (workers, _) => witness = workers,
                    _ =>
                    {
                        entered.Set();
                        if (!release.Wait(TestDeadline)) throw new TimeoutException("test readiness gate did not release");
                    }, () => Interlocked.Increment(ref terminalCleanup)));
            Assert.True(entered.Wait(TestDeadline));
            Assert.All(failure.InnerExceptions, static exception => Assert.IsType<TimeoutException>(exception));
            Assert.NotNull(witness);
            Assert.False(witness.ResourcesReleased);
            Assert.Equal(1, witness.LiveWorkerCount);
            Assert.Equal(0, terminalCleanup);
        }
        finally
        {
            release.Set();
            if (witness is not null) Assert.True(SpinWait.SpinUntil(() => witness.ResourcesReleased, TestDeadline));
            witness?.Dispose();
        }
        Assert.NotNull(witness);
        Assert.True(witness.ResourcesReleased);
        Assert.Equal(0, witness.LiveWorkerCount);
        Assert.Equal(1, terminalCleanup);
    }

    [Fact]
    public void PhaseTimeoutRejectsReuseAndLateCompletionCanFinishCleanup()
    {
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        int stopped = 0;
        PersistentMapWorkers workers = new(1, 1, TestDeadline, ShortDeadline, ShortDeadline,
            _ => Interlocked.Increment(ref stopped), null, null);
        try
        {
            Assert.Throws<TimeoutException>(() => workers.Run((_, _) =>
            {
                entered.Set();
                if (!release.Wait(TestDeadline)) throw new TimeoutException("test phase gate did not release");
            }));
            Assert.True(entered.Wait(TestDeadline));
            Assert.Throws<ObjectDisposedException>(() => workers.Run(static (_, _) => { }));
            Assert.Throws<TimeoutException>(workers.Dispose);
            Assert.False(workers.ResourcesReleased);
            Assert.Equal(1, workers.LiveWorkerCount);
        }
        finally
        {
            release.Set();
            // The production ten-second policy is not changed; only this controlled
            // fixture uses short deadlines to force the pending-cleanup transition.
            Assert.True(SpinWait.SpinUntil(() => workers.ResourcesReleased, TestDeadline));
            workers.Dispose();
        }
        Assert.Equal(1, stopped);
        Assert.Equal(0, workers.LiveWorkerCount);
    }

    [Fact]
    public void OperationFailureDoesNotPreventOtherWorkersOrTerminalCleanup()
    {
        int[] stopped = new int[2];
        int[] visits = new int[2];
        InvalidOperationException original = new("injected callback failure");
        using PersistentMapWorkers workers = new(2, 2,
            index => Interlocked.Increment(ref stopped[index]));
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => workers.Run((worker, _) =>
        {
            visits[worker]++;
            if (worker == 0) throw original;
        })));
        workers.Dispose();
        Assert.All(visits, static count => Assert.Equal(1, count));
        Assert.All(stopped, static count => Assert.Equal(1, count));
        Assert.True(workers.ResourcesReleased);
    }

    [Fact]
    public void CleanupFailureStillJoinsAndCleansEveryWorker()
    {
        int[] stopped = new int[3];
        InvalidOperationException original = new("injected cleanup failure");
        bool verified = false;
        try
        {
            using PersistentMapWorkers workers = new(3, 3, index =>
            {
                Interlocked.Increment(ref stopped[index]);
                if (index == 0) throw original;
            });
            Assert.Same(original, Assert.Throws<InvalidOperationException>(workers.Dispose));
            Assert.True(workers.ResourcesReleased);
            Assert.Equal(0, workers.LiveWorkerCount);
            Assert.All(stopped, static count => Assert.Equal(1, count));
            Assert.Same(original, Assert.Throws<InvalidOperationException>(workers.Dispose));
            Assert.All(stopped, static count => Assert.Equal(1, count));
            verified = true;
        }
        catch (InvalidOperationException exception) { Assert.Same(original, exception); }
        Assert.True(verified);
    }

    [Fact]
    public void TerminalCallbackFailurePreservesItsIdentityAfterReleasingPrimitives()
    {
        InvalidOperationException original = new("injected terminal failure");
        bool verified = false;
        try
        {
            using PersistentMapWorkers workers = new(1, 1, null, () => throw original);
            Assert.Same(original, Assert.Throws<InvalidOperationException>(workers.Dispose));
            Assert.True(workers.ResourcesReleased);
            Assert.Equal(0, workers.LiveWorkerCount);
            Assert.Same(original, Assert.Throws<InvalidOperationException>(workers.Dispose));
            verified = true;
        }
        catch (InvalidOperationException exception) { Assert.Same(original, exception); }
        Assert.True(verified);
    }
}
