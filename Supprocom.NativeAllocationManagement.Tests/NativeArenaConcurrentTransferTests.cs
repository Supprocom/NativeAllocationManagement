using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeArenaConcurrentTransferTests
{
    [Fact]
    public async Task ConcurrentReservationsMoveThroughOneBoundedChannel()
    {
        const int workerCount = 24;
        const int length = 256;
        using NativeArena arena = new(
            preAllocateBytes: workerCount * length * sizeof(int),
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Channel<NativeTransfer<int>> channel =
            Channel.CreateBounded<NativeTransfer<int>>(workerCount);

        Task[] producers = Enumerable.Range(0, workerCount)
            .Select(index => Task.Run(
                async () =>
                {
                    NativeTransfer<int>? source =
                        arena.ScratchTransferable<int>(
                            length,
                            writer => writer.Fill(index + 1));
                    try
                    {
                        await channel.Writer.WriteAsync(
                            NativeTransfer<int>.Move(ref source));
                    }
                    finally
                    {
                        source?.Dispose();
                    }
                }))
            .ToArray();

        await Task.WhenAll(producers);
        bool[] seen = new bool[workerCount];
        for (int index = 0; index < workerCount; index++)
        {
            NativeTransfer<int> transfer =
                await channel.Reader.ReadAsync();
            try
            {
                int value = transfer.Read(static view => view[0]);
                Assert.InRange(value, 1, workerCount);
                Assert.False(seen[value - 1]);
                seen[value - 1] = true;
            }
            finally
            {
                transfer.Dispose();
            }
        }

        Assert.All(seen, Assert.True);
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, arena.CurrentConcurrentReservationCountForTest);
    }

    [Fact]
    public async Task OneArenaInitializesIndependentTransfersConcurrently()
    {
        const int workerCount = 24;
        const int length = 1_024;
        NativeMemoryTestHooks.Reset();
        NativeArena arena = new(
            preAllocateBytes: workerCount * length * sizeof(float),
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        CountdownEvent entered = new(workerCount);
        ManualResetEventSlim release = new();

        try
        {
            Task<NativeTransfer<float>>[] tasks = Enumerable.Range(
                    0,
                    workerCount)
                .Select(index => Task.Factory.StartNew(
                    () => arena.ScratchTransferable<float>(
                        length,
                        writer =>
                        {
                            entered.Signal();
                            if (!release.Wait(TimeSpan.FromSeconds(10)))
                            {
                                throw new TimeoutException(
                                    "Concurrent initialization did not receive its release signal.");
                            }

                            writer.Fill(index + 1);
                        }),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();

            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(
                workerCount,
                arena.CurrentConcurrentReservationCountForTest);
            release.Set();
            NativeTransfer<float>[] transfers = await Task.WhenAll(tasks);

            Assert.Equal(
                workerCount,
                arena.CurrentAllocationRecordCountForTest);
            for (int index = 0; index < transfers.Length; index++)
            {
                double sum = transfers[index].Read(
                    static view =>
                    {
                        double result = 0;
                        foreach (float value in view.AsSpan())
                        {
                            result += value;
                        }

                        return result;
                    });
                Assert.Equal((double)((index + 1) * length), sum);
            }

            long allocationCount =
                NativeMemoryTestHooks.Snapshot().AllocationCount;
            foreach (NativeTransfer<float> transfer in transfers.Reverse())
            {
                transfer.Dispose();
            }

            Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
            Assert.Equal(0, arena.CurrentConcurrentReservationCountForTest);

            NativeTransfer<float>[] reused = await Task.WhenAll(
                Enumerable.Range(0, workerCount)
                    .Select(index => Task.Run(() =>
                        arena.ScratchTransferable<float>(
                            length,
                            writer => writer.Fill(index + 31)))));
            Assert.Equal(
                allocationCount,
                NativeMemoryTestHooks.Snapshot().AllocationCount);
            foreach (NativeTransfer<float> transfer in reused)
            {
                transfer.Dispose();
            }
        }
        finally
        {
            release.Set();
            arena.Dispose();
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public async Task ConcurrentReservationsReuseFragmentedRangesWithoutOverlap()
    {
        const int reservationCount = 64;
        int[] lengths = Enumerable.Range(0, reservationCount)
            .Select(index => (index * 3) + 1)
            .ToArray();
        int totalLength = lengths.Sum();
        NativeMemoryTestHooks.Reset();
        using NativeArena arena = new(
            preAllocateBytes: checked(
                (nuint)(totalLength * sizeof(long))),
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeTransfer<long>?[] transfers = new NativeTransfer<long>?[
            reservationCount];
        NativeTransfer<long>?[] replacements = [];

        try
        {
            for (int index = 0; index < reservationCount; index++)
            {
                int value = index + 1;
                transfers[index] = arena.ScratchTransferable<long>(
                    lengths[index],
                    writer => writer.Fill(value));
            }

            long allocationCount =
                NativeMemoryTestHooks.Snapshot().AllocationCount;
            int[] returnedIndexes = Enumerable.Range(0, reservationCount)
                .Where(static index => (index & 1) != 0)
                .Reverse()
                .ToArray();
            foreach (int index in returnedIndexes)
            {
                transfers[index]!.Dispose();
                transfers[index] = null;
            }

            replacements = await Task.WhenAll(
                returnedIndexes.Select(index => Task.Run(
                    () => arena.ScratchTransferable<long>(
                        lengths[index],
                        writer => writer.Fill(-(index + 1L))))));
            Assert.Equal(
                allocationCount,
                NativeMemoryTestHooks.Snapshot().AllocationCount);

            for (int index = 0; index < reservationCount; index += 2)
            {
                Assert.Equal(
                    index + 1L,
                    transfers[index]!.Read(static view => view[0]));
            }

            for (int resultIndex = 0;
                resultIndex < replacements.Length;
                resultIndex++)
            {
                Assert.Equal(
                    -(returnedIndexes[resultIndex] + 1L),
                    replacements[resultIndex]!.Read(
                        static view => view[0]));
                replacements[resultIndex]!.Dispose();
                replacements[resultIndex] = null;
            }

            for (int index = 0; index < reservationCount; index += 2)
            {
                transfers[index]!.Dispose();
                transfers[index] = null;
            }

            Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
            Assert.Equal(0, arena.CurrentConcurrentReservationCountForTest);
        }
        finally
        {
            foreach (NativeTransfer<long>? transfer in transfers)
            {
                if (transfer is not null)
                {
                    transfer.Dispose();
                }
            }

            foreach (NativeTransfer<long>? replacement in replacements)
            {
                if (replacement is not null)
                {
                    replacement.Dispose();
                }
            }

            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public async Task FailedAndCanceledInitializersAbortOnlyTheirRanges()
    {
        const int workerCount = 8;
        const int length = 512;
        NativeMemoryTestHooks.Reset();
        NativeArena arena = new(
            preAllocateBytes: workerCount * length * sizeof(int),
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        CountdownEvent entered = new(workerCount);
        ManualResetEventSlim release = new();
        ConcurrentBag<Exception> failures = [];
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        try
        {
            Task<NativeTransfer<int>?>[] tasks = Enumerable.Range(
                    0,
                    workerCount)
                .Select(index => Task.Factory.StartNew(
                    () =>
                    {
                        try
                        {
                            return arena.ScratchTransferable<int>(
                                length,
                                writer =>
                                {
                                    entered.Signal();
                                    if (!release.Wait(
                                        TimeSpan.FromSeconds(10)))
                                    {
                                        throw new TimeoutException(
                                            "Concurrent initialization did not receive its release signal.");
                                    }

                                    writer.Write(index);
                                    if (index == 0)
                                    {
                                        throw new InvalidOperationException(
                                            "Injected initializer failure.");
                                    }

                                    if (index == 1)
                                    {
                                        cancellation.Token.ThrowIfCancellationRequested();
                                    }

                                    writer.Fill(index);
                                });
                        }
                        catch (Exception exception)
                        {
                            failures.Add(exception);
                            return null;
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();

            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            release.Set();
            NativeTransfer<int>?[] results = await Task.WhenAll(tasks);
            NativeTransfer<int>[] transfers = results
                .OfType<NativeTransfer<int>>()
                .ToArray();

            Assert.Equal(2, failures.Count);
            Assert.Contains(
                failures,
                static exception => exception is InvalidOperationException);
            Assert.Contains(
                failures,
                static exception => exception is OperationCanceledException);
            Assert.Equal(workerCount - 2, transfers.Length);
            Assert.Equal(
                workerCount - 2,
                arena.CurrentConcurrentReservationCountForTest);
            foreach (NativeTransfer<int> transfer in transfers)
            {
                Assert.Equal(
                    length,
                    transfer.Read(static view => view.Length));
                transfer.Dispose();
            }

            long allocationCount =
                NativeMemoryTestHooks.Snapshot().AllocationCount;
            NativeTransfer<int> replacement =
                arena.ScratchTransferable<int>(
                    length,
                    static writer => writer.Fill(97));
            Assert.Equal(
                allocationCount,
                NativeMemoryTestHooks.Snapshot().AllocationCount);
            Assert.Equal(
                97,
                replacement.Read(static view => view[0]));
            replacement.Dispose();
            Assert.Equal(0, arena.CurrentConcurrentReservationCountForTest);
        }
        finally
        {
            release.Set();
            arena.Dispose();
            NativeMemoryTestHooks.Reset();
        }
    }

    [Theory]
    [InlineData(NativeMemoryReturn.ToNativeMemory)]
    [InlineData(NativeMemoryReturn.ToGarbageCollector)]
    public async Task OwnerShutdownRejectsActiveConcurrentInitialization(
        NativeMemoryReturn policy)
    {
        NativeArena arena = new(
            preAllocateBytes: 4_096,
            returnMemoryOnDispose: policy);
        ManualResetEventSlim entered = new();
        ManualResetEventSlim release = new();
        Task<NativeTransfer<int>> task = Task.Factory.StartNew(
            () => arena.ScratchTransferable<int>(
                128,
                writer =>
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException(
                            "Concurrent initialization did not receive its release signal.");
                    }

                    writer.Fill(5);
                }),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Throws<NativeAllocationInUseException>(
                () => arena.Scratch<int>(
                    8,
                    static writer => writer.Fill(2)));
            Assert.Throws<NativeAllocationInUseException>(arena.Dispose);
            Assert.Equal(NativeOwnerLifecycle.Active, arena.CurrentLifecycle);
            release.Set();
            NativeTransfer<int> transfer = await task;
            transfer.Dispose();
            arena.Dispose();
            Assert.Equal(NativeOwnerLifecycle.Disposed, arena.CurrentLifecycle);
        }
        finally
        {
            release.Set();
            if (arena.CurrentLifecycle != NativeOwnerLifecycle.Disposed)
            {
                arena.Dispose();
            }
        }
    }

    [Fact]
    public async Task ConcurrentReservationRejectsARegularActiveInitializer()
    {
        using NativeArena arena = new(
            preAllocateBytes: 4_096,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ManualResetEventSlim entered = new();
        ManualResetEventSlim release = new();
        Task initializer = Task.Factory.StartNew(
            () =>
            {
                _ = arena.Scratch<int>(
                    128,
                    writer =>
                    {
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(10)))
                        {
                            throw new TimeoutException(
                                "The regular initializer did not receive its release signal.");
                        }

                        writer.Fill(3);
                    });
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Throws<NativeAllocationInUseException>(
                () => arena.ScratchTransferable<int>(
                    32,
                    static writer => writer.Fill(5)));
        }
        finally
        {
            release.Set();
            await initializer;
        }
    }

    [Fact]
    public async Task ConcurrentTransfersUseMappedExternalStorage()
    {
        const int workerCount = 12;
        const int length = 256;
        NativeMemoryTestHooks.Reset();
        AlignedTestBuffer buffer = new(
            workerCount * length * sizeof(long));
        NativeArena arena = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        long allocationCount =
            NativeMemoryTestHooks.Snapshot().AllocationCount;

        try
        {
            Assert.Equal(
                buffer.OwnedByteLength,
                arena.ReserveExternalMemory(
                    buffer,
                    byteOffset: 0,
                    byteLength: buffer.OwnedByteLength));
            buffer.Dispose();
            NativeTransfer<long>[] transfers = await Task.WhenAll(
                Enumerable.Range(0, workerCount)
                    .Select(index => Task.Run(() =>
                        arena.ScratchTransferable<long>(
                            length,
                            writer => writer.Fill(index + 101L)))));

            Assert.Equal(
                allocationCount,
                NativeMemoryTestHooks.Snapshot().AllocationCount);
            for (int index = 0; index < transfers.Length; index++)
            {
                Assert.Equal(
                    index + 101L,
                    transfers[index].Read(static view => view[0]));
                transfers[index].Dispose();
            }

            Assert.Equal(0, arena.CurrentConcurrentReservationCountForTest);
            arena.Dispose();
            Assert.Equal(1, buffer.ReleaseCount);
        }
        finally
        {
            if (arena.CurrentLifecycle != NativeOwnerLifecycle.Disposed)
            {
                arena.Dispose();
            }

            buffer.Dispose();
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void ReturnedWaveReusesStorageAroundAnOrdinaryArenaLease()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativeArena arena = new(
                preAllocateBytes: 1_024,
                returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
            NativeTransfer<int> first =
                arena.ScratchTransferable<int>(
                    64,
                    static writer => writer.Fill(17));
            ArenaLease<long> anchor = arena.Scratch<long>(
                32,
                static writer => writer.Fill(29));
            long allocationCount =
                NativeMemoryTestHooks.Snapshot().AllocationCount;

            first.Dispose();
            NativeTransfer<int> replacement =
                arena.ScratchTransferable<int>(
                    64,
                    static writer => writer.Fill(31));

            Assert.Equal(
                allocationCount,
                NativeMemoryTestHooks.Snapshot().AllocationCount);
            Assert.Equal(29, anchor.Read(static view => view[0]));
            Assert.Equal(
                31,
                replacement.Read(static view => view[0]));
            replacement.Dispose();
            Assert.Equal(29, anchor.Read(static view => view[0]));
            Assert.Equal(0, arena.CurrentConcurrentReservationCountForTest);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void EmptyTransferPublishesAndReturnsExactlyOnce()
    {
        using NativeArena arena = new(
            preAllocateBytes: 64,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeTransfer<int> transfer =
            arena.ScratchTransferable<int>(
                0,
                static writer => Assert.Equal(0, writer.Length));

        Assert.Equal(0, transfer.Length);
        transfer.Dispose();
        Assert.Throws<ObjectDisposedException>(transfer.Dispose);
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, arena.CurrentConcurrentReservationCountForTest);
    }

    [Fact]
    public void GenerationResetInvalidatesEveryPublishedReservation()
    {
        using NativeArena arena = new(
            preAllocateBytes: 512,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeTransfer<int> first = arena.ScratchTransferable<int>(
            32,
            static writer => writer.Fill(3));
        NativeTransfer<long> second = arena.ScratchTransferable<long>(
            16,
            static writer => writer.Fill(5));

        arena.ReleaseLeasesToNativeMemory();

        Assert.Throws<NativeAllocationReturnedException>(
            () => first.Read(static view => view[0]));
        Assert.Throws<NativeAllocationReturnedException>(
            () => second.Read(static view => view[0]));
        first.Dispose();
        second.Dispose();

        NativeTransfer<int> next = arena.ScratchTransferable<int>(
            32,
            static writer => writer.Fill(7));
        Assert.Equal(7, next.Read(static view => view[0]));
        next.Dispose();
        Assert.Equal(0, arena.CurrentConcurrentReservationCountForTest);
    }

    private sealed unsafe class AlignedTestBuffer : SafeBuffer
    {
        private int _releaseCount;

        internal AlignedTestBuffer(int byteLength)
            : base(ownsHandle: true)
        {
            OwnedByteLength = checked((nuint)byteLength);
            SetHandle((IntPtr)NativeMemory.AlignedAlloc(
                OwnedByteLength,
                64));
            if (handle == IntPtr.Zero)
            {
                throw new OutOfMemoryException();
            }

            Initialize(OwnedByteLength);
        }

        internal nuint OwnedByteLength { get; }

        internal int ReleaseCount => Volatile.Read(ref _releaseCount);

        protected override bool ReleaseHandle()
        {
            NativeMemory.AlignedFree((void*)handle);
            Interlocked.Increment(ref _releaseCount);
            handle = IntPtr.Zero;
            return true;
        }
    }
}
