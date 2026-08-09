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
        NativeWorkspaceState<int> state =
            Assert.IsType<NativeWorkspaceState<int>>(
                workspace.StateForTest);

        Exception? exception = RunOnThread(state.Reset);
        InvalidOperationException invalid =
            Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("owning thread", invalid.Message);

        exception = RunOnThread(state.Release);
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
            NativeWorkspaceState<int> state =
                Assert.IsType<NativeWorkspaceState<int>>(
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
        NativeWorkspaceState<int> workspaceState =
            Assert.IsType<NativeWorkspaceState<int>>(state.Target);
        workspaceState.Release();
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
        ((NativeWorkspaceState<int>)state).Initialize(
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
        ((NativeWorkspaceState<int>)state).Initialize(
            8,
            writer =>
            {
                writer.Fill(2);
                cancellation.Cancel();
            },
            cancellation.Token);

    private static int ReadState(object state) =>
        ((NativeWorkspaceState<int>)state).Read(
            static view => view.Length);

    private static void AccessAndFail(object state) =>
        ((NativeWorkspaceState<int>)state).Access(
            static _ => throw new InvalidOperationException("callback"));

    private static int RunStateProcess(object state, int value) =>
        ((NativeWorkspaceState<int>)state).Process(
            8,
            value,
            static (values, fill) =>
            {
                values.Fill(fill);
                return Sum(values);
            },
            default);

    private static int ProcessAndFail(object state) =>
        ((NativeWorkspaceState<int>)state).Process<int>(
            8,
            static values => values.Fill(19),
            static _ => throw new InvalidOperationException("reader"),
            default);

    private static int ProcessAndCancel(
        object state,
        CancellationTokenSource cancellation) =>
        ((NativeWorkspaceState<int>)state).Process(
            8,
            values =>
            {
                values.Fill(23);
                cancellation.Cancel();
            },
            static values => Sum(values),
            cancellation.Token);

    private static int ProcessStateAndFail(object state) =>
        ((NativeWorkspaceState<int>)state).Process<int, int>(
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
        ((NativeWorkspaceState<int>)state).Process(
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
}
