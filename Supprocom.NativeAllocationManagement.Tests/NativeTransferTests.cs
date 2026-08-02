using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeTransferTests
{
    [Fact]
    public void MoveInvalidatesTheSourceAndAllOldAliases()
    {
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeTransfer<int>? source = pool.RentTransferable(
            4,
            static writer => writer.Write([1, 2, 3, 4]));
        NativeTransfer<int> alias = source;
        TransferHolder<int> holder = new();

        holder.Destination = NativeTransfer<int>.Move(ref source);

        Assert.Null(source);
        Assert.Throws<InvalidOperationException>(
            () => alias.Access(static _ => { }));
        Assert.Equal(
            10,
            holder.Destination.Read(
                static view =>
                {
                    int sum = 0;
                    foreach (int value in view.AsSpan())
                    {
                        sum += value;
                    }

                    return sum;
                }));
        holder.Destination.Dispose();
        Assert.Throws<ObjectDisposedException>(
            () => holder.Destination.Dispose());
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public void BoundedChannelTransfersTheLeaseAcrossThreads()
    {
        using NativePool<uint> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeTransfer<uint>? source = pool.RentTransferable(
            4,
            static writer => writer.Write([3U, 5U, 7U, 11U]));
        Channel<NativeTransfer<uint>> channel =
            Channel.CreateBounded<NativeTransfer<uint>>(1);
        NativeTransfer<uint> outbound =
            NativeTransfer<uint>.Move(ref source);
        Assert.True(channel.Writer.TryWrite(outbound));
        int senderThread = Environment.CurrentManagedThreadId;
        int receiverThread = 0;
        uint sum = 0;
        Exception? failure = null;
        Thread receiver = new(
            () =>
            {
                try
                {
                    receiverThread = Environment.CurrentManagedThreadId;
                    NativeTransfer<uint> inbound = channel.Reader
                        .ReadAsync()
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                    inbound.Access(
                        view =>
                        {
                            Span<uint> values = view.AsSpan();
                            foreach (uint value in values)
                            {
                                sum += value;
                            }
                        });
                    inbound.Dispose();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            });

        receiver.Start();
        Assert.True(receiver.Join(TimeSpan.FromSeconds(5)));

        Assert.Null(failure);
        Assert.NotEqual(senderThread, receiverThread);
        Assert.Equal(26U, sum);
        Assert.Null(source);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public async Task ConcurrentMovesPublishExactlyOneDestination()
    {
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeTransfer<int>? original = pool.RentTransferable(
            1,
            static writer => writer.Write(37));
        TransferRaceHolder<int> holder = new()
        {
            First = original,
            Second = original
        };
        original = null;
        Barrier start = new(3);
        ConcurrentBag<NativeTransfer<int>> destinations = [];
        ConcurrentBag<Exception> failures = [];

        Task first = Task.Run(
            () => MoveForRace(
                start,
                ref holder.First,
                destinations,
                failures));
        Task second = Task.Run(
            () => MoveForRace(
                start,
                ref holder.Second,
                destinations,
                failures));
        start.SignalAndWait();
        await Task.WhenAll(first, second);

        Assert.Null(holder.First);
        Assert.Null(holder.Second);
        NativeTransfer<int> destination = Assert.Single(destinations);
        Assert.IsType<InvalidOperationException>(
            Assert.Single(failures));
        Assert.Equal(
            37,
            destination.Read(static view => view.AsSpan()[0]));
        destination.Dispose();
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public async Task MoveDuringAccessConsumesAndReturnsTheSource()
    {
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeTransfer<int>? source = pool.RentTransferable(
            1,
            static writer => writer.Write(41));
        NativeTransfer<int> alias = source;
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        Task access = Task.Run(
            () => alias.Access(
                static (view, state) =>
                {
                    state.Entered.Set();
                    state.Release.Wait();
                    Assert.Equal(41, view.AsSpan()[0]);
                },
                (Entered: entered, Release: release)));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Throws<InvalidOperationException>(
            () => NativeTransfer<int>.Move(ref source));
        Assert.Null(source);
        release.Set();
        await access;

        Assert.Throws<ObjectDisposedException>(
            () => alias.Access(static _ => { }));
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public void CallbackExceptionKeepsTheDestinationUsable()
    {
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeTransfer<int>? source = pool.RentTransferable(
            2,
            static writer => writer.Write([13, 17]));
        NativeTransfer<int> destination =
            NativeTransfer<int>.Move(ref source);

        Assert.Throws<TransferMarkerException>(
            () => destination.Access(
                static _ => throw new TransferMarkerException()));
        Assert.Equal(
            30,
            destination.Read(
                static view => view.AsSpan()[0] + view.AsSpan()[1]));

        destination.Dispose();
    }

    [Fact]
    public void InitializerExceptionReturnsTheUnpublishedLease()
    {
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        Assert.Throws<TransferMarkerException>(
            () => pool.RentTransferable(
                4,
                static writer =>
                {
                    writer.Write(1);
                    throw new TransferMarkerException();
                }));

        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        NativeTransfer<int> next = pool.RentTransferable(
            4,
            static writer => writer.Fill(9));
        Assert.Equal(
            9,
            next.Read(static view => view.AsSpan()[3]));
        next.Dispose();
    }

    [Fact]
    public async Task CanceledChannelWriteReturnsTheMovedDestination()
    {
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Channel<NativeTransfer<int>> channel =
            Channel.CreateBounded<NativeTransfer<int>>(1);
        NativeTransfer<int>? firstSource = pool.RentTransferable(
            1,
            static writer => writer.Write(1));
        NativeTransfer<int> first =
            NativeTransfer<int>.Move(ref firstSource);
        Assert.True(channel.Writer.TryWrite(first));
        NativeTransfer<int>? secondSource = pool.RentTransferable(
            1,
            static writer => writer.Write(2));
        NativeTransfer<int> second =
            NativeTransfer<int>.Move(ref secondSource);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await channel.Writer.WriteAsync(
                second,
                cancellation.Token));
        second.Dispose();
        NativeTransfer<int> received = await channel.Reader.ReadAsync();
        received.Dispose();

        Assert.Null(firstSource);
        Assert.Null(secondSource);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public async Task OwnerShutdownRejectsAnActiveReceiverCallback()
    {
        NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeTransfer<int> transfer = pool.RentTransferable(
            1,
            static writer => writer.Write(23));
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        Task access = Task.Run(
            () => transfer.Access(
                static (view, state) =>
                {
                    state.Entered.Set();
                    state.Release.Wait();
                    Assert.Equal(23, view.AsSpan()[0]);
                },
                (Entered: entered, Release: release)));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Throws<NativeAllocationInUseException>(
            () => pool.Dispose());
        release.Set();
        await access;

        pool.Dispose();
        Assert.Throws<NativeAllocationDisposedException>(
            () => transfer.Access(static _ => { }));
        transfer.Dispose();
    }

    [Fact]
    public void IdleOwnerShutdownInvalidatesTheLiveTransfer()
    {
        NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeTransfer<int> transfer = pool.RentTransferable(
            1,
            static writer => writer.Write(29));

        pool.Dispose();

        Assert.Throws<NativeAllocationDisposedException>(
            () => transfer.Access(static _ => { }));
        transfer.Dispose();
        Assert.Throws<ObjectDisposedException>(
            () => transfer.Dispose());
    }

    [Fact]
    public void TransferUsesMappedExternalArenaStorage()
    {
        NativeMemoryTestHooks.Reset();
        AlignedTestBuffer buffer = new(4096);
        NativeArena arena = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        long upstreamAllocations =
            NativeMemoryTestHooks.Snapshot().AllocationCount;
        Assert.Equal(
            (nuint)4096,
            arena.ReserveExternalMemory(
                buffer,
                byteOffset: 0,
                byteLength: 4096));
        buffer.Dispose();
        NativeTransfer<long>? source = arena.ScratchTransferable<long>(
            4,
            static writer => writer.Write([2L, 3L, 5L, 7L]));
        NativeTransfer<long> destination =
            NativeTransfer<long>.Move(ref source);

        Assert.Equal(
            17,
            destination.Read(
                static view =>
                {
                    long total = 0;
                    foreach (long value in view.AsSpan())
                    {
                        total += value;
                    }

                    return total;
                }));
        Assert.Equal(
            upstreamAllocations,
            NativeMemoryTestHooks.Snapshot().AllocationCount);
        destination.Dispose();
        arena.Dispose();

        Assert.Equal(1, buffer.ReleaseCount);
    }

    [Fact]
    public void AbandonedReceiverFinalizerReturnsTheLease()
    {
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        WeakReference abandoned = CreateAbandonedReceiver(pool);

        for (int attempt = 0;
            attempt < 10 && abandoned.IsAlive;
            attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(abandoned.IsAlive);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    private static void MoveForRace<T>(
        Barrier start,
        ref NativeTransfer<T>? source,
        ConcurrentBag<NativeTransfer<T>> destinations,
        ConcurrentBag<Exception> failures)
        where T : unmanaged
    {
        start.SignalAndWait();
        try
        {
            destinations.Add(
                NativeTransfer<T>.Move(ref source));
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAbandonedReceiver(
        NativePool<int> pool)
    {
        NativeTransfer<int>? source = pool.RentTransferable(
            8,
            static writer => writer.Fill(31));
        NativeTransfer<int> receiver =
            NativeTransfer<int>.Move(ref source);
        return new WeakReference(receiver);
    }

    private sealed class TransferHolder<T>
        where T : unmanaged
    {
        internal NativeTransfer<T> Destination { get; set; } = null!;
    }

    private sealed class TransferRaceHolder<T>
        where T : unmanaged
    {
        internal NativeTransfer<T>? First;
        internal NativeTransfer<T>? Second;
    }

    private sealed class TransferMarkerException : Exception;

    private sealed unsafe class AlignedTestBuffer : SafeBuffer
    {
        internal AlignedTestBuffer(nuint byteLength)
            : base(ownsHandle: true)
        {
            void* pointer = NativeMemory.AlignedAlloc(
                byteLength,
                NativeSegment.Alignment);
            if (pointer is null)
            {
                throw new OutOfMemoryException();
            }

            SetHandle((nint)pointer);
            Initialize(checked((ulong)byteLength));
        }

        internal int ReleaseCount { get; private set; }

        protected override bool ReleaseHandle()
        {
            NativeMemory.AlignedFree((void*)handle);
            ReleaseCount++;
            return true;
        }
    }
}

internal static class NativeTransferTestExtensions
{
    internal static void Access<T, TState>(
        this NativeTransfer<T> transfer,
        NativeTransferStateAction<T, TState> action,
        TState state)
        where T : unmanaged
    {
        transfer.Access(view => action(view, state));
    }
}

internal delegate void NativeTransferStateAction<T, TState>(
    scoped NativeLeaseView<T> view,
    TState state)
    where T : unmanaged;
