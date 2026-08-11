using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeWorkspaceTests
{
    [Fact]
    public void RepeatedInitializationReusesOneNativeBlock()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativeWorkspace<int> workspace = new(preLease: 256);
            NativeMemoryTestMetrics reserved =
                NativeMemoryTestHooks.Snapshot();

            Assert.Equal(1, reserved.AllocationCount);
            Assert.Equal(1_024, reserved.OutstandingNativeBytes);
            for (int iteration = 0; iteration < 128; iteration++)
            {
                int value = iteration;
                workspace.Initialize(
                    256,
                    writer => writer.Fill(value));
                Assert.Equal(256, workspace.Length);
                Assert.Equal(
                    checked(value * 256),
                    workspace.Read(
                        static view => Sum(view.AsSpan())));
                workspace.Reset();
                Assert.Equal(0, workspace.Length);
            }

            NativeMemoryTestMetrics reused =
                NativeMemoryTestHooks.Snapshot();
            Assert.Equal(
                reserved.AllocationCount,
                reused.AllocationCount);
            Assert.Equal(0, reused.FreeCount);
            workspace.Dispose();

            NativeMemoryTestMetrics released =
                NativeMemoryTestHooks.Snapshot();
            Assert.Equal(1, released.FreeCount);
            Assert.Equal(0, released.OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void ConstructionFailureLeavesNoNativeOwnership()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeMemoryTestHooks.FailNextAllocation();

            Assert.Throws<NativeAllocationFailedException>(
                CreateWorkspaceAfterInjectedFailure);

            NativeMemoryTestMetrics metrics =
                NativeMemoryTestHooks.Snapshot();
            Assert.Equal(0, metrics.AllocationCount);
            Assert.Equal(0, metrics.FreeCount);
            Assert.Equal(0, metrics.OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void FailedAndCanceledInitializationPublishNothingAndPermitReuse()
    {
        using NativeWorkspace<int> workspace = new(preLease: 16);
        object state = workspace.StateForTest;

        InvalidOperationException failure = Assert.Throws<
            InvalidOperationException>(() => InitializeAndFail(
                state));
        Assert.Equal("failure", failure.Message);
        Assert.Equal(0, workspace.Length);
        Assert.Throws<InvalidOperationException>(
            () => ReadState(state));

        using CancellationTokenSource cancellation = new();
        Assert.Throws<OperationCanceledException>(
            () => InitializeAndCancel(
                state,
                cancellation));
        Assert.Equal(0, workspace.Length);

        workspace.Initialize(
            8,
            static writer => writer.Fill(3));
        Assert.Equal(
            24,
            workspace.Read(static view => Sum(view.AsSpan())));
    }

    [Fact]
    public void EmptyRangeIsPublishedAndReadable()
    {
        using NativeWorkspace<int> workspace = new(preLease: 1);

        workspace.Initialize(
            0,
            static writer => writer.Fill(default));

        Assert.Equal(0, workspace.Length);
        Assert.Equal(
            0,
            workspace.Read(static view => view.Length));
    }

    [Fact]
    public void AccessFailureKeepsThePublishedRangeActive()
    {
        using NativeWorkspace<int> workspace = new(preLease: 4);
        workspace.Initialize(
            4,
            static writer => writer.Fill(7));
        object state = workspace.StateForTest;

        InvalidOperationException failure = Assert.Throws<
            InvalidOperationException>(() => AccessAndFail(
                state));
        Assert.Equal("callback", failure.Message);
        Assert.Equal(
            28,
            workspace.Read(static view => Sum(view.AsSpan())));
    }

    [Fact]
    public void RuntimeStateRejectsCrossThreadOperations()
    {
        using NativeWorkspace<int> workspace = new(preLease: 8);
        workspace.Initialize(
            8,
            static writer => writer.Fill(13));
        NativeWorkspace<int> state =
            Assert.IsType<NativeWorkspace<int>>(
                workspace.StateForTest);

        Exception? exception = RunOnThread(state.Reset);
        InvalidOperationException invalid =
            Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("owning thread", invalid.Message);

        exception = RunOnThread(state.Dispose);
        invalid = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("owning thread", invalid.Message);
        Assert.Equal(
            104,
            workspace.Read(static view => Sum(view.AsSpan())));
    }

    [Fact]
    public void ProcessUsesOneBoundedRangeWithoutPublishingIt()
    {
        using NativeWorkspace<int> workspace = new(preLease: 8);

        int sum = workspace.Process(
            8,
            static values =>
            {
                for (int index = 0; index < values.Length; index++)
                {
                    values[index] = index + 1;
                }
            },
            static values => Sum(values));

        Assert.Equal(36, sum);
        Assert.Equal(0, workspace.Length);
    }

    [Fact]
    public void ProcessRequiresResetAfterPublishedInitialization()
    {
        using NativeWorkspace<int> workspace = new(preLease: 8);
        workspace.Initialize(
            8,
            static writer => writer.Fill(17));
        object state = workspace.StateForTest;

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(() => RunStateProcess(
                state,
                19));
        Assert.Contains("requires Reset", exception.Message);
        Assert.Equal(
            136,
            workspace.Read(static view => Sum(view.AsSpan())));

        workspace.Reset();
        Assert.Equal(
            152,
            workspace.Process(
                8,
                static values => values.Fill(19),
                static values => Sum(values)));
    }

    [Fact]
    public void ProcessFailureAndCancellationPermitReuse()
    {
        using NativeWorkspace<int> workspace = new(preLease: 8);
        object state = workspace.StateForTest;

        InvalidOperationException failure = Assert.Throws<
            InvalidOperationException>(() => ProcessAndFail(
                state));
        Assert.Equal("reader", failure.Message);
        Assert.Equal(0, workspace.Length);

        using CancellationTokenSource cancellation = new();
        Assert.Throws<OperationCanceledException>(
            () => ProcessAndCancel(
                state,
                cancellation));
        Assert.Equal(0, workspace.Length);
        Assert.Equal(
            232,
            workspace.Process(
                8,
                static values => values.Fill(29),
                static values => Sum(values)));
    }

    [Fact]
    public void ExplicitStateProcessForwardsStateWithoutPublishingTheRange()
    {
        using NativeWorkspace<int> workspace = new(preLease: 8);

        int result = workspace.Process(
            8,
            new ProcessState(Value: 7, Addend: 5),
            static (values, state) =>
            {
                values.Fill(state.Value);
                return Sum(values) + state.Addend;
            });

        Assert.Equal(61, result);
        Assert.Equal(0, workspace.Length);
    }

    [Fact]
    public void ExplicitStateFailureAndCancellationPermitReuse()
    {
        using NativeWorkspace<int> workspace = new(preLease: 8);
        object state = workspace.StateForTest;

        InvalidOperationException failure = Assert.Throws<
            InvalidOperationException>(() => ProcessStateAndFail(
                state));
        Assert.Equal("state callback", failure.Message);

        using CancellationTokenSource cancellation = new();
        Assert.Throws<OperationCanceledException>(
            () => ProcessStateAndCancel(
                state,
                cancellation));

        Assert.Equal(
            232,
            workspace.Process(
                8,
                29,
                static (values, state) =>
                {
                    values.Fill(state);
                    return Sum(values);
                }));
    }

    [Fact]
    public void DisposedWorkspaceRejectsOperationsAndFreesExactlyOnce()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeWorkspace<int> workspace = new(preLease: 8);
            NativeWorkspace<int> state =
                Assert.IsType<NativeWorkspace<int>>(
                    workspace.StateForTest);

            workspace.Dispose();
            workspace.Dispose();

            Assert.Throws<ObjectDisposedException>(
                () => state.Process(
                    8,
                    31,
                    static (values, value) =>
                    {
                        values.Fill(value);
                        return Sum(values);
                    },
                    default));
            NativeMemoryTestMetrics metrics =
                NativeMemoryTestHooks.Snapshot();
            Assert.Equal(1, metrics.AllocationCount);
            Assert.Equal(1, metrics.FreeCount);
            Assert.Equal(0, metrics.OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void InitializeKeepsWorkspaceStateAliveThroughCallback()
    {
        VerifyForcedCollectionLifetime(InitializeWithForcedCollection);
    }

    [Fact]
    public void AccessAndReadKeepWorkspaceStateAliveThroughCallbacks()
    {
        VerifyForcedCollectionLifetime(AccessAndReadWithForcedCollections);
    }

    [Fact]
    public void ProcessKeepsWorkspaceStateAliveThroughBothCallbacks()
    {
        VerifyForcedCollectionLifetime(ProcessWithForcedCollections);
    }

    [Fact]
    public void ExplicitStateProcessKeepsWorkspaceStateAliveThroughCallback()
    {
        VerifyForcedCollectionLifetime(
            ProcessWithStateAndForcedCollection);
    }

    [Fact]
    public void AbandonedWorkspaceFinalizerFreesItsBlock()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            WeakReference abandoned = CreateAbandonedWorkspace();

            CollectUntilDead(abandoned);

            Assert.False(abandoned.IsAlive);
            NativeMemoryTestMetrics metrics =
                NativeMemoryTestHooks.Snapshot();
            Assert.Equal(1, metrics.AllocationCount);
            Assert.Equal(1, metrics.FreeCount);
            Assert.Equal(0, metrics.OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void HeapWorkerReusesOneFixedBlockFor1179Builds()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using WorkspaceWorker worker = new();
            long checksum = 0;
            for (int build = 0; build < 1_179; build++)
            {
                checksum += worker.Build(build);
            }

            Assert.NotEqual(0, checksum);
            NativeMemoryTestMetrics active =
                NativeMemoryTestHooks.Snapshot();
            Assert.Equal(1, active.AllocationCount);
            Assert.Equal(0, active.FreeCount);
            Assert.Equal(
                153_600 * sizeof(int),
                active.OutstandingNativeBytes);

            worker.Dispose();
            NativeMemoryTestMetrics released =
                NativeMemoryTestHooks.Snapshot();
            Assert.Equal(1, released.FreeCount);
            Assert.Equal(0, released.OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void ActiveCallbackRejectsOwnerOperations()
    {
        using NativeWorkspace<int> workspace = new(preLease: 8);

        int sum = workspace.Process(
            8,
            workspace,
            static (values, owner) =>
            {
                Assert.Throws<InvalidOperationException>(owner.Reset);
                Assert.Throws<InvalidOperationException>(owner.Dispose);
                Assert.Throws<InvalidOperationException>(() =>
                    owner.Process(
                        1,
                        static nested => nested[0] = 1,
                        static nested => nested[0]));
                values.Fill(3);
                return Sum(values);
            });

        Assert.Equal(24, sum);
        workspace.Dispose();
    }

    [Fact]
    public void ThreadLocalValuesRequireOwnerThreadDisposal()
    {
        using ThreadLocal<WorkspaceWorker> workers = new(
            static () => new WorkspaceWorker(),
            trackAllValues: true);
        using ManualResetEventSlim created = new();
        using ManualResetEventSlim release = new();
        Exception? workerFailure = null;
        Thread thread = new(() =>
        {
            try
            {
                _ = workers.Value!.Build(1);
                created.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                workers.Value.Dispose();
            }
            catch (Exception failure)
            {
                workerFailure = failure;
            }
        });
        thread.Start();
        Assert.True(created.Wait(TimeSpan.FromSeconds(5)));

        WorkspaceWorker value = Assert.Single(workers.Values);
        InvalidOperationException wrongThread = Assert.Throws<
            InvalidOperationException>(value.Dispose);
        Assert.Contains("owning thread", wrongThread.Message);

        release.Set();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(workerFailure);
    }

    [Fact]
    public void ThreadLocalDisposeDoesNotDisposeItsWorkspaceValue()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            WorkspaceWorker? value;
            using (ThreadLocal<WorkspaceWorker> workers = new(
                static () => new WorkspaceWorker(),
                trackAllValues: true))
            {
                value = workers.Value;
                _ = value!.Build(1);
                Assert.Equal(
                    153_600 * sizeof(int),
                    NativeMemoryTestHooks.Snapshot()
                        .OutstandingNativeBytes);
            }

            Assert.Equal(
                153_600 * sizeof(int),
                NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes);
            value!.Dispose();
            Assert.Equal(
                0,
                NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    private static void VerifyForcedCollectionLifetime(
        Func<WeakReference> operation)
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            WeakReference state = operation();
            ReleaseWorkspaceState(state);

            NativeMemoryTestMetrics metrics =
                NativeMemoryTestHooks.Snapshot();
            Assert.Equal(1, metrics.AllocationCount);
            Assert.Equal(1, metrics.FreeCount);
            Assert.Equal(0, metrics.OutstandingNativeBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference InitializeWithForcedCollection()
    {
        NativeWorkspace<int> workspace = new(preLease: 8);
        WeakReference state = new(workspace.StateForTest);
        workspace.Initialize(
            8,
            writer =>
            {
                ForceFullCollection();
                AssertWorkspaceAlive(state);
                writer.Fill(11);
            });
        Assert.Equal(
            88,
            workspace.Read(static view => Sum(view.AsSpan())));
        return state;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AccessAndReadWithForcedCollections()
    {
        NativeWorkspace<int> workspace = new(preLease: 8);
        WeakReference state = new(workspace.StateForTest);
        workspace.Initialize(
            8,
            static writer => writer.Fill(13));
        workspace.Access(
            view =>
            {
                ForceFullCollection();
                AssertWorkspaceAlive(state);
                view[0] = 17;
            });
        int result = workspace.Read(
            view =>
            {
                ForceFullCollection();
                AssertWorkspaceAlive(state);
                return Sum(view.AsSpan());
            });
        Assert.Equal(108, result);
        return state;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ProcessWithForcedCollections()
    {
        NativeWorkspace<int> workspace = new(preLease: 8);
        WeakReference state = new(workspace.StateForTest);

        int result = workspace.Process(
            8,
            values =>
            {
                ForceFullCollection();
                AssertWorkspaceAlive(state);
                values.Fill(17);
            },
            values =>
            {
                ForceFullCollection();
                AssertWorkspaceAlive(state);
                return Sum(values);
            });

        Assert.Equal(136, result);
        return state;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ProcessWithStateAndForcedCollection()
    {
        NativeWorkspace<int> workspace = new(preLease: 8);
        ProcessLifetimeState state = new(
            new WeakReference(workspace.StateForTest),
            Value: 19);

        int result = workspace.Process(
            8,
            state,
            static (values, callbackState) =>
            {
                ForceFullCollection();
                AssertWorkspaceAlive(callbackState.WorkspaceState);
                values.Fill(callbackState.Value);
                return Sum(values);
            });

        Assert.Equal(152, result);
        return state.WorkspaceState;
    }

    private static void AssertWorkspaceAlive(WeakReference state)
    {
        Assert.True(state.IsAlive);
        Assert.Equal(
            sizeof(int) * 8,
            NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes);
    }

    private static void ForceFullCollection()
    {
        GC.Collect(
            2,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(
            2,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: true);
    }

    private static void CollectUntilDead(WeakReference state)
    {
        for (int attempt = 0;
            attempt < 10 && state.IsAlive;
            attempt++)
        {
            ForceFullCollection();
        }
    }

    private static void ReleaseWorkspaceState(WeakReference state)
    {
        NativeWorkspace<int> workspaceState =
            Assert.IsType<NativeWorkspace<int>>(state.Target);
        workspaceState.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAbandonedWorkspace()
    {
        NativeWorkspace<int> workspace = new(preLease: 8);
        workspace.Initialize(
            8,
            static writer => writer.Fill(17));
        return new WeakReference(workspace.StateForTest);
    }

    private static void CreateWorkspaceAfterInjectedFailure()
    {
        using NativeWorkspace<int> workspace = new(preLease: 8);
    }

    private static void InitializeAndFail(
        object state) =>
        ((NativeWorkspace<int>)state).Initialize(
            8,
            static writer =>
            {
                writer.Write(1);
                throw new InvalidOperationException("failure");
            },
            default);

    private static void InitializeAndCancel(
        object state,
        CancellationTokenSource cancellation) =>
        ((NativeWorkspace<int>)state).Initialize(
            8,
            writer =>
            {
                writer.Fill(2);
                cancellation.Cancel();
            },
            cancellation.Token);

    private static int ReadState(object state) =>
        ((NativeWorkspace<int>)state).Read(
            static view => view.Length);

    private static void AccessAndFail(object state) =>
        ((NativeWorkspace<int>)state).Access(
            static _ => throw new InvalidOperationException("callback"));

    private static int RunStateProcess(object state, int value) =>
        ((NativeWorkspace<int>)state).Process(
            8,
            value,
            static (values, fill) =>
            {
                values.Fill(fill);
                return Sum(values);
            },
            default);

    private static int ProcessAndFail(object state) =>
        ((NativeWorkspace<int>)state).Process<int>(
            8,
            static values => values.Fill(19),
            static _ => throw new InvalidOperationException("reader"),
            default);

    private static int ProcessAndCancel(
        object state,
        CancellationTokenSource cancellation) =>
        ((NativeWorkspace<int>)state).Process(
            8,
            values =>
            {
                values.Fill(23);
                cancellation.Cancel();
            },
            static values => Sum(values),
            cancellation.Token);

    private static int ProcessStateAndFail(object state) =>
        ((NativeWorkspace<int>)state).Process<int, int>(
            8,
            19,
            static (values, value) =>
            {
                values.Fill(value);
                throw new InvalidOperationException("state callback");
            },
            default);

    private static int ProcessStateAndCancel(
        object state,
        CancellationTokenSource cancellation) =>
        ((NativeWorkspace<int>)state).Process(
            8,
            cancellation,
            static (values, source) =>
            {
                values.Fill(23);
                source.Cancel();
                return Sum(values);
            },
            cancellation.Token);

    private static Exception? RunOnThread(ThreadStart action)
    {
        Exception? exception = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception failure)
            {
                exception = failure;
            }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        return exception;
    }

    private static int Sum(ReadOnlySpan<int> values)
    {
        int result = 0;
        foreach (int value in values)
        {
            result += value;
        }

        return result;
    }

    private readonly record struct ProcessState(
        int Value,
        int Addend);

    private sealed record ProcessLifetimeState(
        WeakReference WorkspaceState,
        int Value);

    private sealed class WorkspaceWorker : IDisposable
    {
        private readonly NativeWorkspace<int> _workspace = new(
            preLease: 153_600);

        internal long Build(int seed) => _workspace.Process(
            153_600,
            seed,
            static (values, value) =>
            {
                values.Fill(value);
                return (long)values[0]
                    + values[^1]
                    + values.Length;
            });

        public void Dispose() => _workspace.Dispose();
    }
}
