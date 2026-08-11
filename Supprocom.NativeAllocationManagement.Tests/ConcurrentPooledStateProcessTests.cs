using Supprocom.NativeAllocationManagement;
using Xunit.Abstractions;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class ConcurrentPooledStateProcessTests
{
    private readonly ITestOutputHelper _output;

    public ConcurrentPooledStateProcessTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ProcessExposesOnlyTheRequestedPrefixAndReturnsTheResult()
    {
        using NativeConcurrentPool<int> pool = new();
        using ConcurrentPooled<int> lease = pool.Rent(
            4,
            static writer =>
            {
                writer.Write(2);
                writer.Write(3);
                writer.Write(5);
                writer.Write(7);
            });
        ProcessState state = new(10);

        int result = lease.Process(
            2,
            in state,
            static (view, current) =>
            {
                Assert.Equal(2, view.Length);
                Assert.Equal(2, view.Capacity);
                Span<int> values = view.AsSpan();
                values[0] += current.Offset;
                values[1] += current.Offset;
                return values[0] + values[1];
            });

        Assert.Equal(25, result);
        lease.Read(static view =>
        {
            Assert.Equal(4, view.Length);
            Assert.Equal(12, view[0]);
            Assert.Equal(13, view[1]);
            Assert.Equal(5, view[2]);
            Assert.Equal(7, view[3]);
            return 0;
        });
    }

    [Fact]
    public void ProcessValidatesPrefixBoundsInsideOneOperation()
    {
        using NativeConcurrentPool<int> pool = new();
        using ConcurrentPooled<int> lease = pool.Rent(
            2,
            static writer => writer.Fill(1));
        ProcessState state = new(0);
        int entries = 0;
        NativeMemoryTestHooks.SetOperationEntered(operation =>
        {
            if (operation == nameof(ConcurrentPooled<int>.Process))
            {
                entries++;
            }
        });

        try
        {
            AssertProcessBoundsFailure(lease, -1, in state);
            AssertProcessBoundsFailure(lease, 3, in state);
            int value = lease.Process(
                1,
                in state,
                static (view, _) => view[0]);
            Assert.Equal(1, value);
            Assert.Equal(3, entries);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void ProcessSupportsReferenceStorageWithoutASecondPath()
    {
        using NativeConcurrentPool<string> pool = new();
        using ConcurrentPooled<string> lease = pool.Rent(
            3,
            static writer =>
            {
                writer.Write("one");
                writer.Write("two");
                writer.Write("three");
            });
        ProcessState state = new(0);

        string result = lease.Process(
            2,
            in state,
            static (view, current) =>
            {
                _ = current;
                Assert.Equal(2, view.Length);
                view[1] = "changed";
                try
                {
                    Span<string> direct = view.AsSpan();
                    _ = direct.Length;
                }
                catch (NotSupportedException)
                {
                    return view[0] + ":" + view[1];
                }

                throw new Xunit.Sdk.XunitException(
                    "Reference storage exposed a direct span.");
            });

        Assert.Equal("one:changed", result);
        lease.Read(static view =>
        {
            Assert.Equal("one", view[0]);
            Assert.Equal("changed", view[1]);
            Assert.Equal("three", view[2]);
            return 0;
        });
    }

    [Fact]
    public void PrefixOperationsNeverChangeTheHiddenTail()
    {
        using NativeConcurrentPool<int> pool = new();
        using ConcurrentPooled<int> lease = pool.Rent(
            4,
            static writer =>
            {
                writer.Write(1);
                writer.Write(2);
                writer.Write(3);
                writer.Write(4);
            });
        ProcessState state = new(0);

        int checksum = lease.Process(
            2,
            in state,
            static (view, _) =>
            {
                view.Clear();
                view.Fill(5);
                ReadOnlySpan<int> source = [7, 11];
                view.CopyFrom(source);
                Span<int> destination = stackalloc int[2];
                view.CopyTo(destination);
                return destination[0] + destination[1];
            });

        Assert.Equal(18, checksum);
        lease.Read(static view =>
        {
            Assert.Equal(7, view[0]);
            Assert.Equal(11, view[1]);
            Assert.Equal(3, view[2]);
            Assert.Equal(4, view[3]);
            return 0;
        });
    }

    [Fact]
    public void CallbackFailureReleasesTheOperation()
    {
        using NativeConcurrentPool<int> pool = new();
        using ConcurrentPooled<int> lease = pool.Rent(
            1,
            static writer => writer.Fill(7));
        ProcessState state = new(0);

        InvalidOperationException failure;
        try
        {
            _ = InvokeFailingProcess(lease, in state);
            throw new Xunit.Sdk.XunitException(
                "Process accepted the failing callback.");
        }
        catch (InvalidOperationException exception)
        {
            failure = exception;
        }
        Assert.Equal("callback failure", failure.Message);
        Assert.Equal(
            7,
            lease.Process(
                1,
                in state,
                static (view, _) => view[0]));
    }

    [Fact]
    public void CancellationFailureReleasesTheOperation()
    {
        using NativeConcurrentPool<int> pool = new();
        using ConcurrentPooled<int> lease = pool.Rent(
            1,
            static writer => writer.Fill(29));
        ProcessState state = new(0);

        try
        {
            _ = lease.Process<ProcessState, int>(
                1,
                in state,
                static (_, _) =>
                    throw new OperationCanceledException());
            throw new Xunit.Sdk.XunitException(
                "Process accepted the cancellation failure.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal(
            29,
            lease.Process(
                1,
                in state,
                static (view, _) => view[0]));
    }

    [Fact]
    public void ZeroPrefixIsAValidBoundedOperation()
    {
        using NativeConcurrentPool<int> pool = new();
        using ConcurrentPooled<int> lease = pool.Rent(
            2,
            static writer => writer.Fill(31));
        ProcessState state = new(0);

        int result = lease.Process(
            0,
            in state,
            static (view, _) =>
            {
                Assert.Equal(0, view.Length);
                Assert.Equal(0, view.Capacity);
                Assert.True(view.AsSpan().IsEmpty);
                return 37;
            });

        Assert.Equal(37, result);
        Assert.Equal(31, lease.Read(static view => view[0]));
    }

    [Fact]
    public void ProcessSupportsAStackBoundState()
    {
        using NativeConcurrentPool<int> pool = new();
        using ConcurrentPooled<int> lease = pool.Rent(
            2,
            static writer => writer.Fill(0));
        ReadOnlySpan<int> source = [41, 43];
        SpanState state = new(source);

        int result = lease.Process(
            2,
            in state,
            static (view, current) =>
            {
                view.CopyFrom(current.Values);
                return view[0] + view[1];
            });

        Assert.Equal(84, result);
    }

    [Fact]
    public void LeaseReturnFailsDuringProcessAndSucceedsAfterward()
    {
        using NativeConcurrentPool<int> pool = new();
        ConcurrentPooled<int> lease = pool.Rent(
            1,
            static writer => writer.Fill(11));
        ProcessState state = new(0);
        NativeAllocationInUseException? failure = null;
        NativeMemoryTestHooks.SetOperationEnteredWithAllocation(
            (operation, kernel, generation, allocationId) =>
            {
                if (operation == nameof(ConcurrentPooled<int>.Process))
                {
                    failure = Assert.Throws<NativeAllocationInUseException>(() =>
                        kernel.ReturnLease(generation, allocationId));
                }
            });

        try
        {
            int result = lease.Process(
                1,
                in state,
                static (view, _) => view[0]);
            Assert.Equal(11, result);
            Assert.NotNull(failure);
            lease.Dispose();
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public async Task OwnerDisposalFailsDuringProcessAndLeavesTheOwnerUsable()
    {
        NativeConcurrentPool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ManualResetEventSlim entered = new();
        ManualResetEventSlim release = new();
        ProcessWaitState state = new(entered, release);

        Task worker = Task.Run(() =>
        {
            ConcurrentPooled<int> lease = pool.Rent(
                1,
                static writer => writer.Fill(13));
            try
            {
                _ = lease.Process(
                    1,
                    in state,
                    static (view, current) =>
                    {
                        current.Entered.Set();
                        current.Release.Wait(TimeSpan.FromSeconds(10));
                        return view[0];
                    });
            }
            finally
            {
                lease.Dispose();
            }
        });

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Throws<NativeAllocationInUseException>(pool.Dispose);
            release.Set();
            await worker;
            using ConcurrentPooled<int> next = pool.Rent(
                1,
                static writer => writer.Fill(17));
            Assert.Equal(17, next.Read(static view => view[0]));
        }
        finally
        {
            release.Set();
            pool.Dispose();
        }
    }

    [Fact]
    public void ReturnedGenerationRejectsProcessBeforeTheCallback()
    {
        using NativeConcurrentPool<int> pool = new();
        ConcurrentPooled<int> lease = pool.Rent(
            1,
            static writer => writer.Fill(19));
        pool.ReturnMemoryToNativeMemory();
        CallbackFlag callbackFlag = new();
        CallbackFlagState callbackState = new(callbackFlag);

        try
        {
            InvokeStaleProcess(
                lease,
                in callbackState);
            throw new Xunit.Sdk.XunitException(
                "Process accepted a stale lease.");
        }
        catch (NativeAllocationReturnedException)
        {
        }

        Assert.False(callbackFlag.Called);
        lease.Dispose();
    }

    [Fact]
    public void StaticStatePathAllocatesNoCallbackObjectsFor1179Chunks()
    {
        using NativeConcurrentPool<int> pool = new(preLease: 1);
        using ConcurrentPooled<int> lease = pool.Rent(
            1,
            static writer => writer.Fill(23));

        const int chunkCount = 1_179;
        _ = RunStaticStatePath(lease, 1);
        long beforeStatic = GC.GetAllocatedBytesForCurrentThread();
        int staticChecksum = RunStaticStatePath(
            lease,
            chunkCount);
        long staticAllocation = GC.GetAllocatedBytesForCurrentThread()
            - beforeStatic;

        ProcessState empty = new(0);
        long beforeCapturing = GC.GetAllocatedBytesForCurrentThread();
        int capturingChecksum = 0;
        for (int index = 0; index < chunkCount; index++)
        {
            int current = index;
            capturingChecksum += lease.Process(
                1,
                in empty,
                (view, _) => view[0] + current);
        }

        long capturingAllocation = GC.GetAllocatedBytesForCurrentThread()
            - beforeCapturing;

        CachedReadContext cached = new();
        _ = lease.Read(cached.Callback);
        long beforeCached = GC.GetAllocatedBytesForCurrentThread();
        int cachedChecksum = 0;
        for (int index = 0; index < chunkCount; index++)
        {
            cached.Offset = index;
            cachedChecksum += lease.Read(cached.Callback);
        }

        long cachedAllocation = GC.GetAllocatedBytesForCurrentThread()
            - beforeCached;

        _output.WriteLine(
            $"Static state allocated {staticAllocation} bytes for {chunkCount} calls.");
        _output.WriteLine(
            $"Capturing callbacks allocated {capturingAllocation} bytes for {chunkCount} calls.");
        _output.WriteLine(
            $"The cached delegate allocated {cachedAllocation} bytes for {chunkCount} calls.");

        Assert.Equal(0, staticAllocation);
        Assert.True(capturingAllocation >= chunkCount * 64L);
        Assert.Equal(0, cachedAllocation);
        Assert.Equal(staticChecksum, capturingChecksum);
        Assert.Equal(staticChecksum, cachedChecksum);
    }

    private static int RunStaticStatePath(
        scoped ConcurrentPooled<int> lease,
        int chunkCount)
    {
        int checksum = 0;
        for (int index = 0; index < chunkCount; index++)
        {
            ProcessState state = new(index);
            checksum += lease.Process(
                1,
                in state,
                static (view, current) =>
                    view[0] + current.Offset);
        }

        return checksum;
    }

    private static void AssertProcessBoundsFailure(
        scoped ConcurrentPooled<int> lease,
        int length,
        scoped in ProcessState state)
    {
        try
        {
            _ = lease.Process(
                length,
                in state,
                static (_, _) => 0);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        throw new Xunit.Sdk.XunitException(
            "Process accepted an invalid prefix length.");
    }

    private static int InvokeFailingProcess(
        scoped ConcurrentPooled<int> lease,
        scoped in ProcessState state) =>
        lease.Process<ProcessState, int>(
            1,
            in state,
            static (_, _) =>
                throw new InvalidOperationException(
                    "callback failure"));

    private static void InvokeStaleProcess(
        scoped ConcurrentPooled<int> lease,
        scoped in CallbackFlagState state) =>
        lease.Process(
            1,
            in state,
            static (_, current) =>
            {
                current.Flag.Called = true;
                return 0;
            });

    private readonly record struct ProcessState(int Offset);

    private readonly record struct ProcessWaitState(
        ManualResetEventSlim Entered,
        ManualResetEventSlim Release);

    private readonly record struct CallbackFlagState(
        CallbackFlag Flag);

    private readonly ref struct SpanState
    {
        internal SpanState(ReadOnlySpan<int> values)
        {
            Values = values;
        }

        internal ReadOnlySpan<int> Values { get; }
    }

    private sealed class CallbackFlag
    {
        internal bool Called { get; set; }
    }

    private sealed class CachedReadContext
    {
        internal CachedReadContext()
        {
            Callback = Read;
        }

        internal NativeLeaseFunc<int, int> Callback { get; }

        internal int Offset { get; set; }

        private int Read(scoped NativeLeaseView<int> view) =>
            view[0] + Offset;
    }
}
