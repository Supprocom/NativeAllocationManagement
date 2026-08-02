using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeWorkspaceTests
{
    [Fact]
    public void RepeatedInitializationReusesOnePublishedRecordAndSegment()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativePool<int> pool = new(
                preLease: 256,
                returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
            using NativeWorkspace<int> workspace =
                pool.CreateWorkspace(256);
            long allocations =
                NativeMemoryTestHooks.Snapshot().AllocationCount;

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
                        static view =>
                        {
                            int sum = 0;
                            foreach (int item in view.AsSpan())
                            {
                                sum += item;
                            }

                            return sum;
                        }));
                workspace.Reset();
                Assert.Equal(0, workspace.Length);
            }

            Assert.Equal(
                allocations,
                NativeMemoryTestHooks.Snapshot().AllocationCount);
            workspace.Dispose();
            Assert.Equal(
                0,
                pool.CurrentAllocationRecordCountForTest);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void FailedAndCanceledInitializationPublishNothingAndPermitReuse()
    {
        using NativePool<int> pool = new(
            preLease: 16,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeWorkspace<int> workspace =
            pool.CreateWorkspace(16);

        try
        {
            workspace.Initialize(
                8,
                static writer =>
                {
                    writer.Write(1);
                    throw new InvalidOperationException("failure");
                });
            Assert.Fail("The failed initializer completed.");
        }
        catch (InvalidOperationException exception)
        {
            Assert.Equal("failure", exception.Message);
        }

        Assert.Equal(0, workspace.Length);
        try
        {
            workspace.Read(static view => view.Length);
            Assert.Fail("The unpublished workspace was readable.");
        }
        catch (InvalidOperationException)
        {
        }

        using CancellationTokenSource cancellation = new();
        try
        {
            workspace.Initialize(
                8,
                writer =>
                {
                    writer.Fill(2);
                    cancellation.Cancel();
                },
                cancellation.Token);
            Assert.Fail("The canceled initializer completed.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal(0, workspace.Length);

        workspace.Initialize(
            8,
            static writer => writer.Fill(3));
        Assert.Equal(
            24,
            workspace.Read(
                static view => Sum(view.AsSpan())));
    }

    [Fact]
    public void EmptyRangeIsPublishedAndReadable()
    {
        using NativePool<int> pool = new(
            preLease: 1,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeWorkspace<int> workspace =
            pool.CreateWorkspace(1);

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
        using NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeWorkspace<int> workspace =
            pool.CreateWorkspace(4);
        workspace.Initialize(
            4,
            static writer => writer.Fill(7));

        try
        {
            workspace.Access(
                static _ => throw new InvalidOperationException("callback"));
            Assert.Fail("The failed access callback completed.");
        }
        catch (InvalidOperationException exception)
        {
            Assert.Equal("callback", exception.Message);
        }

        Assert.Equal(
            28,
            workspace.Read(
                static view => Sum(view.AsSpan())));
    }

    [Fact]
    public void OwnerShutdownRejectsALiveWorkspace()
    {
        NativePool<int> pool = new(
            preLease: 4,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeWorkspace<int> workspace =
            pool.CreateWorkspace(4);
        workspace.Initialize(
            4,
            static writer => writer.Fill(11));

        Assert.Throws<NativeAllocationInUseException>(
            pool.Dispose);
        Assert.Equal(
            44,
            workspace.Read(
                static view => Sum(view.AsSpan())));

        workspace.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void OwnerGenerationTransitionsRejectALiveWorkspace()
    {
        NativePool<int> pool = new(
            preLease: 8,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeWorkspace<int> workspace =
            pool.CreateWorkspace(8);

        Assert.Throws<NativeAllocationInUseException>(
            pool.ReturnMemoryToNativeMemory);
        Assert.Throws<NativeAllocationInUseException>(
            pool.ReturnMemoryToGarbageCollector);
        Assert.Throws<NativeAllocationInUseException>(
            pool.ReleaseLeasesToNativeMemory);
        Assert.Throws<NativeAllocationInUseException>(
            pool.ReleaseLeasesToGarbageCollector);

        Assert.Equal(
            248,
            workspace.Process(
                8,
                static values => values.Fill(31),
                static values => Sum(values)));
        workspace.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void RuntimeStateRejectsCrossThreadOperations()
    {
        using NativePool<int> pool = new(
            preLease: 8,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeWorkspace<int> workspace =
            pool.CreateWorkspace(8);
        workspace.Initialize(
            8,
            static writer => writer.Fill(13));
        NativeWorkspaceState<int> state =
            Assert.IsType<NativeWorkspaceState<int>>(
                workspace.StateForTest);

        Exception? exception = null;
        Thread thread = new(
            () => exception = Record.Exception(state.Reset));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        InvalidOperationException invalid =
            Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("owning thread", invalid.Message);

        Assert.Equal(
            104,
            workspace.Read(
                static view => Sum(view.AsSpan())));

        exception = null;
        thread = new(
            () => exception = Record.Exception(state.Release));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        invalid = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("owning thread", invalid.Message);

        Assert.Equal(
            104,
            workspace.Read(
                static view => Sum(view.AsSpan())));
    }

    [Fact]
    public void ProcessUsesOneBoundedPublishedRangeAndResetsIt()
    {
        using NativePool<int> pool = new(
            preLease: 8,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeWorkspace<int> workspace =
            pool.CreateWorkspace(8);

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
        using NativePool<int> pool = new(
            preLease: 8,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeWorkspace<int> workspace =
            pool.CreateWorkspace(8);
        workspace.Initialize(
            8,
            static writer => writer.Fill(17));

        InvalidOperationException? exception = null;
        try
        {
            workspace.Process(
                8,
                static values => values.Fill(19),
                static values => Sum(values));
            Assert.Fail("The published workspace was processed without Reset.");
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Contains("requires Reset", exception.Message);
        Assert.Equal(
            136,
            workspace.Read(
                static view => Sum(view.AsSpan())));

        workspace.Reset();
        Assert.Equal(
            152,
            workspace.Process(
                8,
                static values => values.Fill(19),
                static values => Sum(values)));
    }

    [Fact]
    public void ProcessFailureAndCancellationResetForReuse()
    {
        using NativePool<int> pool = new(
            preLease: 8,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeWorkspace<int> workspace =
            pool.CreateWorkspace(8);

        try
        {
            workspace.Process<int>(
                8,
                static values => values.Fill(19),
                static _ => throw new InvalidOperationException("reader"));
            Assert.Fail("The failed reader completed.");
        }
        catch (InvalidOperationException exception)
        {
            Assert.Equal("reader", exception.Message);
        }

        Assert.Equal(0, workspace.Length);
        using CancellationTokenSource cancellation = new();
        try
        {
            workspace.Process(
                8,
                values =>
                {
                    values.Fill(23);
                    cancellation.Cancel();
                },
                static values => Sum(values),
                cancellation.Token);
            Assert.Fail("The canceled process completed.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal(0, workspace.Length);
        Assert.Equal(
            232,
            workspace.Process(
                8,
                static values => values.Fill(29),
                static values => Sum(values)));
    }

    [Fact]
    public void AbandonedWorkspaceFinalizerReturnsItsRecord()
    {
        using NativePool<int> pool = new(
            preLease: 8,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        WeakReference abandoned =
            CreateAbandonedWorkspace(pool);

        for (int attempt = 0;
            attempt < 10 && abandoned.IsAlive;
            attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(abandoned.IsAlive);
        Assert.Equal(
            0,
            pool.CurrentAllocationRecordCountForTest);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAbandonedWorkspace(
        NativePool<int> pool)
    {
        NativeWorkspace<int> workspace =
            pool.CreateWorkspace(8);
        workspace.Initialize(
            8,
            static writer => writer.Fill(17));
        return new WeakReference(workspace.StateForTest);
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
}
