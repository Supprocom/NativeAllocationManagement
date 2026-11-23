using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeMemoryPoolTests
{
    private static unsafe nint Address(IMemoryOwner<byte> owner) =>
        (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(owner.Memory.Span));

    [Fact]
    public void RentedMemoryRoundTrips()
    {
        using IMemoryOwner<byte> owner = NativeMemoryPool.Default.Rent(5000);
        Memory<byte> memory = owner.Memory;
        Assert.True(memory.Length >= 5000);
        for (int index = 0; index < 5000; index++)
        {
            memory.Span[index] = unchecked((byte)index);
        }

        Assert.Equal(unchecked((byte)4999), owner.Memory.Span[4999]);
    }

    [Fact]
    public void StaleMemoryThrowsAfterDispose()
    {
        IMemoryOwner<byte> owner = NativeMemoryPool.Default.Rent(64);
        Memory<byte> memory = owner.Memory;
        memory.Span[0] = 1;
        owner.Dispose();

        Assert.Throws<ObjectDisposedException>(() => memory.Span.Length);
        Assert.Throws<ObjectDisposedException>(() => memory.Pin());
        Assert.Throws<ObjectDisposedException>(() => owner.Memory);
    }

    [Fact]
    public void StaleMemoryStaysStaleWhenTheStorageIsRentedAgain()
    {
        IMemoryOwner<byte> first = NativeMemoryPool.Default.Rent(64);
        Memory<byte> stale = first.Memory;
        nint address = Address(first);
        first.Dispose();

        using IMemoryOwner<byte> second = NativeMemoryPool.Default.Rent(64);
        Assert.Equal(address, Address(second));
        Assert.Throws<ObjectDisposedException>(() => stale.Span.Length);
    }

    [Fact]
    public void DisposingTwiceReturnsTheStorageOnce()
    {
        IMemoryOwner<byte> owner = NativeMemoryPool.Default.Rent(64);
        nint address = Address(owner);
        owner.Dispose();
        owner.Dispose();

        using IMemoryOwner<byte> first = NativeMemoryPool.Default.Rent(64);
        using IMemoryOwner<byte> second = NativeMemoryPool.Default.Rent(64);
        Assert.Equal(address, Address(first));
        Assert.NotEqual(Address(first), Address(second));
    }

    [Fact]
    public void PinningDefersTheStorageReuse()
    {
        IMemoryOwner<byte> owner = NativeMemoryPool.Default.Rent(64);
        nint address = Address(owner);
        MemoryHandle handle = owner.Memory.Pin();
        owner.Dispose();

        using (IMemoryOwner<byte> other = NativeMemoryPool.Default.Rent(64))
        {
            Assert.NotEqual(address, Address(other));
        }

        handle.Dispose();
        handle.Dispose();
        using IMemoryOwner<byte> reused = NativeMemoryPool.Default.Rent(64);
        Assert.Equal(address, Address(reused));
    }

    [Fact]
    public async Task ReturningOnAnotherThreadFeedsThatThreadsCache()
    {
        IMemoryOwner<byte> owner = NativeMemoryPool.Default.Rent(4096);
        nint address = Address(owner);

        bool reused = await Task.Run(() =>
        {
            owner.Dispose();
            using IMemoryOwner<byte> next = NativeMemoryPool.Default.Rent(4096);
            return Address(next) == address;
        }).ConfigureAwait(true);

        Assert.True(reused);
    }

    [Fact]
    public void SteadyStateRentAllocatesOnlyTheOwner()
    {
        for (int warm = 0; warm < 100; warm++)
        {
            using IMemoryOwner<byte> owner = NativeMemoryPool.Default.Rent(65536);
            owner.Memory.Span[0] = 1;
        }

        const int rounds = 10_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < rounds; index++)
        {
            using IMemoryOwner<byte> owner = NativeMemoryPool.Default.Rent(65536);
            owner.Memory.Span[0] = 1;
        }

        double perRent = (double)(GC.GetAllocatedBytesForCurrentThread() - before) / rounds;
        Assert.InRange(perRent, 1, 160);
    }

    [Fact]
    public async Task PipeOverTheNativePoolCopiesData()
    {
        byte[] payload = new byte[300_000];
        RandomNumberGenerator.Fill(payload);
        Pipe pipe = new(new PipeOptions(NativeMemoryPool.Default, minimumSegmentSize: 8192, useSynchronizationContext: false));

        Task writer = Task.Run(async () =>
        {
            await pipe.Writer.WriteAsync(payload).ConfigureAwait(false);
            await pipe.Writer.CompleteAsync().ConfigureAwait(false);
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
}
