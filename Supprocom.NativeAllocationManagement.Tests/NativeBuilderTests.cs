using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeBuilderTests
{
    [Fact]
    public void PoolBuilderGrowsAndPublishesExactLogicalLength()
    {
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeBuilder<int> builder =
            pool.CreateBuilder(preLease: 1);

        for (int value = 0; value < 100; value++)
        {
            builder.Append(value * 3);
        }

        Assert.Equal(100, builder.Count);
        Assert.True(builder.Capacity >= 100);
        NativeTransfer<int> transfer = builder.Complete();

        Assert.Equal(100, transfer.Length);
        Assert.True(transfer.Capacity >= transfer.Length);
        Assert.Equal(
            Enumerable.Range(0, 100)
                .Select(value => value * 3)
                .ToArray(),
            transfer.Read(static view => view.AsSpan().ToArray()));

        transfer.Dispose();
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public void RangeAppendWritesDirectlyIntoTheInitializedPrefix()
    {
        using NativePool<uint> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeBuilder<uint> builder =
            pool.CreateBuilder(preLease: 2);

        builder.Append([2U, 3U]);
        builder.Append(5U);
        builder.Append([7U, 11U, 13U]);
        NativeTransfer<uint> transfer = builder.Complete();

        Assert.Equal(
            new uint[] { 2, 3, 5, 7, 11, 13 },
            transfer.Read(static view => view.AsSpan().ToArray()));
        transfer.Dispose();
    }

    [Fact]
    public void ArenaBuilderGrowsAndPreservesEveryInitializedValue()
    {
        using NativeArena arena = new(
            preAllocateBytes: 32,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeBuilder<long> builder =
            arena.CreateBuilder<long>(preLease: 1);

        for (long value = 0; value < 100; value++)
        {
            builder.Append(value * 5);
        }

        NativeTransfer<long> transfer = builder.Complete();

        Assert.Equal(100, transfer.Length);
        Assert.Equal(
            Enumerable.Range(0, 100)
                .Select(value => (long)value * 5)
                .ToArray(),
            transfer.Read(static view => view.AsSpan().ToArray()));
        transfer.Dispose();
    }

    [Fact]
    public void EmptyBuilderCompletesAsAnEmptyTransfer()
    {
        using NativePool<long> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeBuilder<long> builder = pool.CreateBuilder();

        NativeTransfer<long> transfer = builder.Complete();

        Assert.Equal(0, transfer.Length);
        Assert.Equal(0, transfer.Capacity);
        transfer.Dispose();
    }

    [Fact]
    public void CompletionInvalidatesEveryBuilderOperation()
    {
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeBuilder<int> builder = pool.CreateBuilder();
        builder.Append(17);
        NativeTransfer<int> transfer = builder.Complete();

        Assert.Throws<InvalidOperationException>(
            () => builder.Append(19));
        Assert.Throws<InvalidOperationException>(
            () => _ = builder.Count);
        Assert.Throws<InvalidOperationException>(
            () => builder.Complete());

        builder.Dispose();
        transfer.Dispose();
    }

    [Fact]
    public void DisposalReturnsBuilderStorageOnlyOnce()
    {
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeBuilder<int> builder =
            pool.CreateBuilder(preLease: 4);
        builder.Append([1, 2, 3]);

        builder.Dispose();
        builder.Dispose();

        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Throws<ObjectDisposedException>(
            () => builder.Append(4));
    }

    [Fact]
    public void GrowthFailureReleasesAllBuilderAllocations()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativePool<int> pool = new(
                returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
            NativeBuilder<int> builder =
                pool.CreateBuilder(preLease: 1);
            builder.Append(1);
            NativeMemoryTestHooks.FailNextAllocation();

            Assert.Throws<NativeAllocationFailedException>(
                () => builder.Append(2));

            Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
            Assert.Throws<ObjectDisposedException>(
                () => builder.Append(3));
            builder.Dispose();
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void AppendCancellationReleasesBuilderStorage()
    {
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeBuilder<int> builder =
            pool.CreateBuilder(preLease: 4);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => builder.Append(1, cancellation.Token));

        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Throws<ObjectDisposedException>(
            () => builder.Append(2));
    }

    [Fact]
    public void CompletionCancellationReleasesBuilderStorage()
    {
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeBuilder<int> builder =
            pool.CreateBuilder(preLease: 4);
        builder.Append([1, 2, 3]);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => builder.Complete(cancellation.Token));

        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Throws<ObjectDisposedException>(
            () => builder.Append(4));
    }

    [Fact]
    public void ArenaBuilderUsesMappedExternalStorage()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
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
            using NativeBuilder<long> builder =
                arena.CreateBuilder<long>(preLease: 8);
            builder.Append([2L, 3L, 5L, 7L]);

            NativeTransfer<long> transfer = builder.Complete();

            Assert.Equal(
                17,
                transfer.Read(
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
            transfer.Dispose();
            arena.Dispose();
            Assert.Equal(1, buffer.ReleaseCount);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void StrictOwnerDisposalRejectsALiveBuilder()
    {
        NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeBuilder<int> builder = pool.CreateBuilder();
        builder.Append(23);

        Assert.Throws<NativeAllocationInUseException>(
            () => pool.Dispose());

        builder.Append(29);
        NativeTransfer<int> transfer = builder.Complete();
        Assert.Equal(
            new[] { 23, 29 },
            transfer.Read(static view => view.AsSpan().ToArray()));
        transfer.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void DeferredOwnerDisposalAlsoRejectsALiveBuilder()
    {
        NativePool<int> pool = new(
            returnMemoryOnDispose:
                NativeMemoryReturn.ToGarbageCollector);
        using NativeBuilder<int> builder =
            pool.CreateBuilder(preLease: 4);
        builder.Append(31);

        Assert.Throws<NativeAllocationInUseException>(
            () => pool.Dispose());

        builder.Append(37);
        NativeTransfer<int> transfer = builder.Complete();
        Assert.Equal(
            new[] { 31, 37 },
            transfer.Read(static view => view.AsSpan().ToArray()));
        transfer.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void AbandonedBuilderFinalizerReturnsItsAllocation()
    {
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        WeakReference abandoned = CreateAbandonedBuilder(pool);

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

    [Fact]
    public async Task ConcurrentBuilderOperationsFailClosed()
    {
        NativeMemoryTestHooks.Reset();
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeBuilder<int> builder = pool.CreateBuilder();
        using ManualResetEventSlim appendEntered = new(false);
        using ManualResetEventSlim releaseAppend = new(false);
        NativeMemoryTestHooks.SetBeforeOperationEntry(
            operation =>
            {
                if (operation != "NativeBuilder.Append")
                {
                    return;
                }

                appendEntered.Set();
                releaseAppend.Wait();
            });

        try
        {
            Task first = Task.Run(() => builder.Append(41));
            Assert.True(
                appendEntered.Wait(TimeSpan.FromSeconds(5)));

            Assert.Throws<InvalidOperationException>(
                () => builder.Append(43));
            Assert.Throws<InvalidOperationException>(
                () => builder.Complete());

            releaseAppend.Set();
            await first;
        }
        finally
        {
            releaseAppend.Set();
            NativeMemoryTestHooks.Reset();
        }

        NativeTransfer<int> transfer = builder.Complete();
        Assert.Equal(
            41,
            transfer.Read(static view => view[0]));
        transfer.Dispose();
    }

    [Fact]
    public async Task ConcurrentCompletionAndDisposalReturnStorageOnce()
    {
        using NativePool<int> pool = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        for (int attempt = 0; attempt < 64; attempt++)
        {
            NativeBuilder<int> builder =
                pool.CreateBuilder(preLease: 4);
            builder.Append([47, 53, 59, 61]);
            using ManualResetEventSlim start = new(false);
            NativeTransfer<int>? transfer = null;
            Exception? completionFailure = null;
            Exception? disposalFailure = null;

            Task completion = Task.Run(
                () =>
                {
                    start.Wait();
                    try
                    {
                        transfer = builder.Complete();
                    }
                    catch (Exception failure)
                    {
                        completionFailure = failure;
                    }
                });
            Task disposal = Task.Run(
                () =>
                {
                    start.Wait();
                    try
                    {
                        builder.Dispose();
                    }
                    catch (Exception failure)
                    {
                        disposalFailure = failure;
                    }
                });

            start.Set();
            await Task.WhenAll(completion, disposal);

            if (transfer is not null)
            {
                Assert.Null(completionFailure);
                Assert.True(
                    disposalFailure is null
                    or InvalidOperationException);
                Assert.Equal(
                    new[] { 47, 53, 59, 61 },
                    transfer.Read(
                        static view => view.AsSpan().ToArray()));
                transfer.Dispose();
                Assert.Throws<ObjectDisposedException>(
                    transfer.Dispose);
            }
            else
            {
                Assert.Null(disposalFailure);
                Assert.True(
                    completionFailure is InvalidOperationException
                    or ObjectDisposedException);
            }

            builder.Dispose();
            Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        }
    }

    [Fact]
    public void ReturnedBuilderSlabIsReusedWithoutFreshGrowth()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativePool<int> pool = new(
                returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
            using (NativeBuilder<int> first =
                pool.CreateBuilder(preLease: 1))
            {
                for (int value = 0; value < 100; value++)
                {
                    first.Append(value);
                }

                NativeTransfer<int> transfer = first.Complete();
                transfer.Dispose();
            }

            long allocations =
                NativeMemoryTestHooks.Snapshot().AllocationCount;
            using (NativeBuilder<int> second =
                pool.CreateBuilder(preLease: 1))
            {
                for (int value = 0; value < 100; value++)
                {
                    second.Append(value);
                }

                NativeTransfer<int> transfer = second.Complete();
                transfer.Dispose();
            }

            Assert.Equal(
                allocations,
                NativeMemoryTestHooks.Snapshot().AllocationCount);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAbandonedBuilder(
        NativePool<int> pool)
    {
        NativeBuilder<int> builder =
            pool.CreateBuilder(preLease: 8);
        builder.Append([1, 2, 3, 4]);
        return new WeakReference(builder);
    }

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
