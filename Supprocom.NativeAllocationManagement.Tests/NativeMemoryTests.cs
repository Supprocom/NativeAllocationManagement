using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeMemoryTests
{
    private static void Fill(Memory<byte> memory, byte seed)
    {
        Span<byte> span = memory.Span;
        for (int index = 0; index < span.Length; index++)
        {
            span[index] = unchecked((byte)(seed + index));
        }
    }

    [Fact]
    public void TransferMemoryRoundTripsThroughTheOwner()
    {
        using NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(64);
        Memory<byte> memory = buffer.AsMemory();
        Assert.Equal(64, memory.Length);
        Fill(memory, 3);

        int sum = buffer.Read(static view =>
        {
            int total = 0;
            for (int index = 0; index < view.Length; index++)
            {
                total += view[index];
            }

            return total;
        });
        int expected = 0;
        for (int index = 0; index < 64; index++)
        {
            expected += unchecked((byte)(3 + index));
        }

        Assert.Equal(expected, sum);
    }

    [Fact]
    public void TransferMemoryThrowsAfterDispose()
    {
        NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(16);
        Memory<byte> memory = buffer.AsMemory();
        _ = memory.Span;
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => memory.Span.Length);
        Assert.Throws<ObjectDisposedException>(() => memory.Pin());
        Assert.Throws<ObjectDisposedException>(() => buffer.AsMemory());
    }

    [Fact]
    public void TransferMemoryThrowsAfterMoveAndTheNewOwnerGetsItsOwn()
    {
        NativeTransfer<byte>? source = NativeTransfer<byte>.CreateUninitialized(16);
        Memory<byte> before = source.Value.AsMemory();
        Fill(before, 9);
        NativeTransfer<byte> moved = NativeTransfer<byte>.Move(ref source);
        try
        {
            Assert.Throws<ObjectDisposedException>(() => before.Span.Length);
            Assert.Throws<ObjectDisposedException>(() => before.Pin());
            Memory<byte> after = moved.AsMemory();
            Assert.Equal(9, after.Span[0]);
            Assert.Equal(16, after.Length);
        }
        finally
        {
            moved.Dispose();
        }
    }

    [Fact]
    public void RepeatedAsMemoryAllocatesNothing()
    {
        using NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(4096);
        long sink = 0;
        sink += buffer.AsMemory().Length;
        sink += buffer.AsMemory().Span.Length;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            Memory<byte> memory = buffer.AsMemory();
            sink += memory.Span.Length;
            using MemoryHandle handle = memory.Pin();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(sink > 0);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SharedAndLeaseAsMemoryAllocateNothingWhenRepeated()
    {
        NativeTransfer<byte>? source = NativeTransfer<byte>.Create(new byte[] { 1, 2, 3, 4 });
        NativeShared<byte> shared = NativeShared<byte>.Create(ref source, new(2, 0));
        using NativeConcurrentPool<byte> pool = new();
        using (shared)
        {
            ConcurrentPooled<byte> lease = pool.Rent(32, 5);
            try
            {
                long sink = shared.AsMemory().Length + lease.AsMemory().Length;
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int index = 0; index < 10_000; index++)
                {
                    sink += shared.AsMemory().Span.Length + lease.AsMemory().Span.Length;
                }

                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.True(sink > 0);
                Assert.Equal(0, allocated);
            }
            finally
            {
                lease.Dispose();
            }
        }
    }

    [Fact]
    public void PinningKeepsTheStorageUntilTheLastUnpin()
    {
        NativeMemoryBudget budget = new(1024);
        using NativeBuilder<byte> builder = new(budget, 32);
        builder.Append(7);
        NativeTransfer<byte>? source = builder.Complete();
        NativeTransfer<byte> owner = source.Value;
        Memory<byte> memory = owner.AsMemory();
        MemoryHandle first = memory.Pin();
        MemoryHandle second = memory.Pin();
        NativeTransferStatistics before = owner.CaptureSnapshot();

        owner.Dispose();

        Assert.Equal(0, owner.CaptureSnapshot().PayloadReturnCount);
        Assert.True(budget.CaptureStatistics().CommittedBytes > 0);
        Assert.Throws<ObjectDisposedException>(() => memory.Span.Length);
        unsafe
        {
            Assert.Equal(7, *(byte*)first.Pointer);
        }

        first.Dispose();
        first.Dispose();
        Assert.Equal(0, owner.CaptureSnapshot().PayloadReturnCount);
        second.Dispose();
        Assert.Equal(1, owner.CaptureSnapshot().PayloadReturnCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, before.LiveUniqueOwnerCount);
    }

    [Fact]
    public void PinTakenBeforeMoveKeepsTheStorageForTheNewOwner()
    {
        NativeTransfer<byte>? source = NativeTransfer<byte>.Create(new byte[] { 5, 6, 7 });
        Memory<byte> memory = source.Value.AsMemory();
        MemoryHandle handle = memory.Pin();
        NativeTransfer<byte> moved = NativeTransfer<byte>.Move(ref source);
        moved.Dispose();

        Assert.Equal(0, moved.CaptureSnapshot().PayloadReturnCount);
        unsafe
        {
            Assert.Equal(6, ((byte*)handle.Pointer)[1]);
        }

        handle.Dispose();
        Assert.Equal(1, moved.CaptureSnapshot().PayloadReturnCount);
    }

    [Fact]
    public void PinOffsetAddressesTheElement()
    {
        using NativeTransfer<byte> buffer = NativeTransfer<byte>.Create(new byte[] { 1, 2, 3, 4 });
        Memory<byte> memory = buffer.AsMemory();
        using MemoryHandle handle = memory[2..].Pin();
        unsafe
        {
            Assert.Equal(3, *(byte*)handle.Pointer);
        }
    }

    [Fact]
    public async Task TransferMemoryWorksAcrossAwaitsAndThreads()
    {
        byte[] payload = new byte[8192];
        RandomNumberGenerator.Fill(payload);
        using MemoryStream input = new(payload);
        using NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(payload.Length);

        int read = 0;
        while (read < payload.Length)
        {
            int count = await input.ReadAsync(buffer.AsMemory()[read..]).ConfigureAwait(true);
            await Task.Yield();
            Assert.True(count > 0);
            read += count;
        }

        using MemoryStream output = new();
        await output.WriteAsync(buffer.AsMemory()).ConfigureAwait(true);
        Assert.Equal(payload, output.ToArray());

        int observed = await Task.Run(() => buffer.AsMemory().Span.Length).ConfigureAwait(true);
        Assert.Equal(payload.Length, observed);
    }

    [Fact]
    public async Task TransferMemoryFeedsAPipe()
    {
        byte[] payload = new byte[70_000];
        RandomNumberGenerator.Fill(payload);
        using NativeTransfer<byte> buffer = NativeTransfer<byte>.Create(payload);
        Pipe pipe = new();

        Task writer = Task.Run(async () =>
        {
            await pipe.Writer.WriteAsync(buffer.AsMemory()).ConfigureAwait(true);
            await pipe.Writer.CompleteAsync().ConfigureAwait(true);
        });

        using MemoryStream received = new();
        while (true)
        {
            ReadResult result = await pipe.Reader.ReadAsync().ConfigureAwait(true);
            foreach (ReadOnlyMemory<byte> segment in result.Buffer)
            {
                received.Write(segment.Span);
            }

            pipe.Reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
            {
                break;
            }
        }

        await writer.ConfigureAwait(true);
        Assert.Equal(payload, received.ToArray());
    }

    [Fact]
    public async Task DisposeWhileAnOperationIsPinnedDefersTheFree()
    {
        byte[] payload = [1, 2, 3, 4];
        using MemoryStream input = new(payload);
        NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(4);
        Memory<byte> memory = buffer.AsMemory();
        using MemoryHandle handle = memory.Pin();
        buffer.Dispose();

        // The pointer stays valid: the owner is gone, the storage is not.
        using UnmanagedMemory pinned = UnmanagedMemory.Over(handle, 4);
        int read = await input.ReadAsync(pinned.Memory).ConfigureAwait(true);

        Assert.Equal(4, read);
        Assert.Equal(0, buffer.CaptureSnapshot().PayloadReturnCount);
    }

    [Fact]
    public async Task ConcurrentSpanUseOnAnotherThreadWhileLive()
    {
        using NativeTransfer<byte> buffer = NativeTransfer<byte>.Create(new byte[] { 4, 5, 6 });
        Memory<byte> memory = buffer.AsMemory();
        byte value = await Task.Run(() => memory.Span[2]).ConfigureAwait(true);
        Assert.Equal(6, value);
        MemoryHandle handle = await Task.Run(() => memory.Pin()).ConfigureAwait(true);
        handle.Dispose();
    }

    [Fact]
    public void PooledTransferMemoryRoundTripsAndDefersTheFree()
    {
        using NativeConcurrentPool<byte> pool = new();
        NativeTransfer<byte> transfer = pool.RentTransferable(32, (byte)3);
        Memory<byte> memory = transfer.AsMemory();
        Assert.Equal(3, memory.Span[31]);
        MemoryHandle handle = memory.Pin();
        transfer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => memory.Span.Length);
        unsafe
        {
            Assert.Equal(3, ((byte*)handle.Pointer)[5]);
        }

        handle.Dispose();
        Assert.Equal(1, transfer.CaptureSnapshot().PayloadReturnCount);
    }

    [Fact]
    public void SharedMemoryReadsAndThrowsAfterDispose()
    {
        NativeTransfer<byte>? source = NativeTransfer<byte>.Create(new byte[] { 9, 8, 7, 6 });
        NativeShared<byte> shared = NativeShared<byte>.Create(ref source, new(2, 0));
        Assert.True(shared.TrySlice(1, 2, out NativeShared<byte> slice, out _));
        ReadOnlyMemory<byte> all = shared.AsMemory();
        ReadOnlyMemory<byte> part = slice.AsMemory();
        Assert.Equal([9, 8, 7, 6], all.ToArray());
        Assert.Equal([8, 7], part.ToArray());

        slice.Dispose();
        Assert.Throws<ObjectDisposedException>(() => part.Span.Length);
        Assert.Equal(4, all.Span.Length);
        shared.Dispose();
        Assert.Throws<ObjectDisposedException>(() => all.Span.Length);
        Assert.Throws<ObjectDisposedException>(() => all.Pin());
        Assert.Throws<InvalidOperationException>(() => shared.AsMemory());
    }

    [Fact]
    public void SharedPinDefersThePayloadReturn()
    {
        NativeTransfer<byte>? source = NativeTransfer<byte>.Create(new byte[] { 1, 2, 3 });
        NativeShared<byte> shared = NativeShared<byte>.Create(ref source, new(1, 0));
        ReadOnlyMemory<byte> memory = shared.AsMemory();
        MemoryHandle handle = memory.Pin();

        shared.Dispose();

        Assert.Equal(0, shared.CaptureSnapshot().PayloadReturnCount);
        unsafe
        {
            Assert.Equal(2, ((byte*)handle.Pointer)[1]);
        }

        handle.Dispose();
        Assert.Equal(1, shared.CaptureSnapshot().PayloadReturnCount);
    }

    [Fact]
    public void ConvertingAPinnedTransferToSharedIsRefused()
    {
        NativeTransfer<byte>? source = NativeTransfer<byte>.Create(new byte[] { 1, 2, 3 });
        MemoryHandle handle = source.Value.AsMemory().Pin();
        Assert.Throws<InvalidOperationException>(() => NativeShared<byte>.Create(ref source, new(1, 0)));
        Assert.NotNull(source);
        handle.Dispose();
        source.Value.Dispose();
    }

    [Fact]
    public void LeaseMemoryRoundTripsAndThrowsAfterDispose()
    {
        using NativeConcurrentPool<byte> pool = new();
        Memory<byte> memory;
        using (ConcurrentPooled<byte> lease = pool.Rent(32, 0))
        {
            memory = lease.AsMemory();
            Fill(memory, 1);
            Assert.Equal(32, memory.Length);
            Assert.Equal(4, lease.AsMemory().Span[3]);
        }

        Assert.Throws<ObjectDisposedException>(() => memory.Span.Length);
        Assert.Throws<ObjectDisposedException>(() => memory.Pin());

        using ConcurrentPooled<byte> next = pool.Rent(32, 0);
        Assert.Throws<ObjectDisposedException>(() => memory.Span.Length);
        Assert.Equal(32, next.AsMemory().Length);
    }

    [Fact]
    public void LeaseMemoryOfReferenceTypesIsRefused()
    {
        using NativeConcurrentPool<string> pool = new();
        using ConcurrentPooled<string> lease = pool.Rent(4, "x");
        NotSupportedException? refused = null;
        try
        {
            _ = lease.AsMemory();
        }
        catch (NotSupportedException failure)
        {
            refused = failure;
        }

        Assert.NotNull(refused);
    }

    [Fact]
    public void LeasePinKeepsTheRecordFromBeingReused()
    {
        using NativeConcurrentPool<byte> pool = new();
        MemoryHandle handle;
        ConcurrentPooled<byte> lease = pool.Rent(32, 7);
        Memory<byte> memory = lease.AsMemory();
        handle = memory.Pin();
        lease.Dispose();

        using (ConcurrentPooled<byte> other = pool.Rent(32, 1))
        {
            unsafe
            {
                byte* pinned = (byte*)handle.Pointer;
                for (int index = 0; index < 32; index++)
                {
                    Assert.Equal(7, pinned[index]);
                }
            }
        }

        Assert.Throws<ObjectDisposedException>(() => memory.Span.Length);
        handle.Dispose();

        using ConcurrentPooled<byte> reused = pool.Rent(32, 2);
        Assert.Equal(2, reused.AsMemory().Span[0]);
    }

    [Fact]
    public async Task LeasePinReleasedOnAnotherThreadCompletesTheReturn()
    {
        using NativeConcurrentPool<byte> pool = new();
        MemoryHandle handle = RentPinAndDispose(pool);

        await Task.Run(handle.Dispose).ConfigureAwait(true);

        Assert.Equal(3, RentFirst(pool));
    }

    private static MemoryHandle RentPinAndDispose(NativeConcurrentPool<byte> pool)
    {
        ConcurrentPooled<byte> lease = pool.Rent(32, 7);
        MemoryHandle handle = lease.AsMemory().Pin();
        lease.Dispose();
        return handle;
    }

    private static byte RentFirst(NativeConcurrentPool<byte> pool)
    {
        using ConcurrentPooled<byte> again = pool.Rent(32, 3);
        return again.AsMemory().Span[31];
    }

    [Fact]
    public void LeaseMemoryIsUsableFromAnotherThreadWhileLive()
    {
        using NativeConcurrentPool<byte> pool = new();
        using ConcurrentPooled<byte> lease = pool.Rent(16, 9);
        Memory<byte> memory = lease.AsMemory();
        byte value = 0;
        MemoryHandle handle = default;
        Thread worker = new(() =>
        {
            value = memory.Span[15];
            handle = memory.Pin();
        });
        worker.Start();
        worker.Join();
        Assert.Equal(9, value);
        handle.Dispose();
    }

    private sealed unsafe class UnmanagedMemory : MemoryManager<byte>
    {
        private readonly byte* _pointer;
        private readonly int _length;

        internal static UnmanagedMemory Over(MemoryHandle handle, int length)
        {
            unsafe
            {
                return new((byte*)handle.Pointer, length);
            }
        }

        private UnmanagedMemory(byte* pointer, int length)
        {
            _pointer = pointer;
            _length = length;
        }

        public override Span<byte> GetSpan() => new(_pointer, _length);

        public override MemoryHandle Pin(int elementIndex = 0) => new(_pointer + elementIndex);

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
