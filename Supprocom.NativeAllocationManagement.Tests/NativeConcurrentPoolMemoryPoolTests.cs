using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeConcurrentPoolMemoryPoolTests
{
    private static unsafe nint Address(IMemoryOwner<byte> owner) =>
        (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(owner.Memory.Span));

    [Fact]
    public void RentedMemoryRoundTripsAndStaleMemoryThrows()
    {
        using NativeConcurrentPool<byte> pool = new();
        MemoryPool<byte> view = pool.AsMemoryPool();
        IMemoryOwner<byte> owner = view.Rent(5000);
        Memory<byte> memory = owner.Memory;
        Assert.True(memory.Length >= 5000);
        for (int index = 0; index < 5000; index++)
        {
            memory.Span[index] = unchecked((byte)index);
        }

        Assert.Equal(unchecked((byte)4999), owner.Memory.Span[4999]);
        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => memory.Span.Length);
        Assert.Throws<ObjectDisposedException>(() => memory.Pin());
        owner.Dispose();
    }

    [Fact]
    public void ViewShape()
    {
        using NativeConcurrentPool<byte> pool = new();
        MemoryPool<byte> view = pool.AsMemoryPool();
        Assert.Same(view, pool.AsMemoryPool());
        Assert.Equal(int.MaxValue, view.MaxBufferSize);
        using IMemoryOwner<byte> owner = view.Rent();
        Assert.Equal(4096, owner.Memory.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Rent(-2));
        Assert.Throws<ArgumentNullException>(() => NativeConcurrentPoolExtensions.AsMemoryPool(null!));
        view.Dispose();
        using IMemoryOwner<byte> after = pool.AsMemoryPool().Rent(16);
        after.Memory.Span[0] = 1;
    }

    [Fact]
    public async Task ReturnOnAnotherThreadKeepsThePoolWorkingAndAccounted()
    {
        using NativeConcurrentPool<byte> pool = new();
        MemoryPool<byte> view = pool.AsMemoryPool();
        for (int round = 0; round < 2000; round++)
        {
            IMemoryOwner<byte> owner = view.Rent(4096);
            owner.Memory.Span[0] = 1;
            await Task.Run(owner.Dispose).ConfigureAwait(true);
        }

        using (IMemoryOwner<byte> again = view.Rent(4096))
        {
            again.Memory.Span[4095] = 1;
        }

        // Everything came back: the pool closes without a lease in use, and its storage did not grow with the rounds.
        NativeOwnerStatistics statistics = pool.GetStatistics();
        Assert.True(statistics.RetainedBytes < 2000L * 4096);
        pool.Dispose();
    }

    [Fact]
    public void PinDefersTheReturnUntilTheLastUnpin()
    {
        using NativeConcurrentPool<byte> pool = new();
        MemoryPool<byte> view = pool.AsMemoryPool();
        IMemoryOwner<byte> owner = view.Rent(64);
        nint address = Address(owner);
        MemoryHandle first = owner.Memory.Pin();
        MemoryHandle second = owner.Memory.Pin();
        owner.Dispose();

        using (IMemoryOwner<byte> other = view.Rent(64))
        {
            Assert.NotEqual(address, Address(other));
        }

        first.Dispose();
        first.Dispose();
        using (IMemoryOwner<byte> other = view.Rent(64))
        {
            Assert.NotEqual(address, Address(other));
        }

        second.Dispose();
        using IMemoryOwner<byte> reused = view.Rent(64);
        Assert.Equal(address, Address(reused));
    }

    [Fact]
    public void RentAfterPoolDisposeThrowsObjectDisposedWithTheNativeExceptionInside()
    {
        NativeConcurrentPool<byte> pool = new();
        MemoryPool<byte> view = pool.AsMemoryPool();
        pool.Dispose();
        ObjectDisposedException exception = Assert.Throws<ObjectDisposedException>(() => view.Rent(64));
        Assert.IsType<NativeAllocationDisposedException>(exception.InnerException);
    }

    [Fact]
    public void UnpinnedOwnersOutlivingThePoolThrowOnAccessAndDisposeQuietly()
    {
        NativeConcurrentPool<byte> pool = new();
        IMemoryOwner<byte> owner = pool.AsMemoryPool().Rent(64);
        Memory<byte> memory = owner.Memory;
        memory.Span[0] = 1;
        pool.Dispose();
        Assert.Throws<ObjectDisposedException>(() => memory.Span.Length);
        owner.Dispose();
        owner.Dispose();
    }

    [Fact]
    public void PoolDisposeWhilePinnedThrowsInUseUntilTheUnpin()
    {
        NativeConcurrentPool<byte> pool = new();
        IMemoryOwner<byte> owner = pool.AsMemoryPool().Rent(64);
        MemoryHandle handle = owner.Memory.Pin();
        Assert.Throws<NativeAllocationInUseException>(pool.Dispose);
        handle.Dispose();
        owner.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void TrimAndStatisticsAreTheRealPools()
    {
        using NativeConcurrentPool<byte> pool = new();
        MemoryPool<byte> view = pool.AsMemoryPool();
        Stack<IMemoryOwner<byte>> owners = new();
        for (int index = 0; index < 8; index++)
        {
            owners.Push(view.Rent(64 * 1024));
        }

        while (owners.Count > 0)
        {
            owners.Pop().Dispose();
        }

        long before = pool.GetStatistics().RetainedBytes;
        nuint released = pool.TrimRetainedMemory();
        long after = pool.GetStatistics().RetainedBytes;
        Assert.True(released > 0);
        Assert.True(after < before);
        Assert.Equal(pool.GetStatistics().RetainedBytes, pool.GetStatistics().RetainedBytes);
        _ = pool.CaptureDiagnosticSnapshot();
    }

    [Fact]
    public void BudgetedPoolRefusesBeyondItsBudget()
    {
        NativeMemoryBudget budget = new(1024 * 1024);
        using NativeConcurrentPool<byte> pool = new(budget, 0, 0, NativeMemoryReturn.Free, false);
        MemoryPool<byte> view = pool.AsMemoryPool();
        using IMemoryOwner<byte> fits = view.Rent(256 * 1024);
        Assert.Throws<NativeMemoryBudgetExceededException>(() => view.Rent(2 * 1024 * 1024));
    }

    [Fact]
    public void SteadyStateRentAllocatesOneObject()
    {
        using NativeConcurrentPool<byte> pool = new();
        MemoryPool<byte> view = pool.AsMemoryPool();
        for (int warm = 0; warm < 100; warm++)
        {
            using IMemoryOwner<byte> owner = view.Rent(65536);
            owner.Memory.Span[0] = 1;
        }

        const int rounds = 10_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < rounds; index++)
        {
            using IMemoryOwner<byte> owner = view.Rent(65536);
            owner.Memory.Span[0] = 1;
        }

        double perRent = (double)(GC.GetAllocatedBytesForCurrentThread() - before) / rounds;
        Assert.InRange(perRent, 1, 120);
    }

    [Fact]
    public async Task PipeOverTheViewCopiesData()
    {
        using NativeConcurrentPool<byte> pool = new();
        byte[] payload = new byte[300_000];
        RandomNumberGenerator.Fill(payload);
        Pipe pipe = new(new PipeOptions(pool.AsMemoryPool(), minimumSegmentSize: 8192, useSynchronizationContext: false));

        Task writer = Task.Run(async () =>
        {
            await pipe.Writer.WriteAsync(payload).ConfigureAwait(true);
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
        await pipe.Reader.CompleteAsync().ConfigureAwait(true);
        pool.Dispose();
    }
}
