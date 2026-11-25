using System.Buffers;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Adds a <see cref="MemoryPool{T}"/> view to byte pools.</summary>
public static class NativeConcurrentPoolExtensions
{
    /// <summary>Gets a <see cref="MemoryPool{T}"/> view of this pool.</summary>
    /// <param name="pool">The pool whose storage the view rents.</param>
    /// <returns>The same view on every call for one pool.</returns>
    public static MemoryPool<byte> AsMemoryPool(this NativeConcurrentPool<byte> pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        MemoryPool<byte>? view = pool.MemoryPoolView;
        if (view is null)
        {
            NativeConcurrentPoolMemoryPool created = new(pool);
            view = Interlocked.CompareExchange(ref pool.MemoryPoolView, created, null) ?? created;
        }

        return view;
    }
}

// The MemoryPool<byte> view of one NativeConcurrentPool<byte>. A rent is a transferable lease, so any thread can
// return it, and its owner is the lease's Memory<byte> manager. The view holds no storage and disposing it does nothing:
// the pool is closed through the pool.
internal sealed class NativeConcurrentPoolMemoryPool : MemoryPool<byte>
{
    private const int DefaultBufferSize = 4096;

    private readonly NativeConcurrentPool<byte> _pool;

    internal NativeConcurrentPoolMemoryPool(NativeConcurrentPool<byte> pool) => _pool = pool;

    public override int MaxBufferSize => int.MaxValue;

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        if (minBufferSize == -1)
        {
            minBufferSize = DefaultBufferSize;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(minBufferSize);
        try
        {
            return NativeLeaseMemoryManager<byte>.RentOwned(_pool.KernelForTransfer, minBufferSize);
        }
        catch (NativeAllocationDisposedException exception)
        {
            throw new ObjectDisposedException("The NativeConcurrentPool<byte> behind this memory pool was disposed.", exception);
        }
    }

    protected override void Dispose(bool disposing)
    {
    }
}
