using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>
/// Owns reusable native slabs for one unmanaged element type.
/// </summary>
/// <typeparam name="T">The unmanaged element stored in each lease.</typeparam>
public sealed class NativePool<T> : IDisposable
    where T : unmanaged
{
    private readonly NativeOwnerKernel _kernel;

    internal NativeOwnerLifecycle CurrentLifecycle => _kernel.Lifecycle;

    /// <summary>
    /// Creates an active pool generation.
    /// </summary>
    /// <param name="initialCapacity">Optional number of elements reserved immediately.</param>
    /// <param name="returnOnDispose">The physical cleanup policy used by <see cref="Dispose"/>.</param>
    public NativePool(
        int initialCapacity = 0,
        NativeReturn returnOnDispose = NativeReturn.ToGarbageCollector)
    {
        NativeReturnValidation.Validate(returnOnDispose, nameof(returnOnDispose));
        _kernel = NativeOwnerKernel.CreatePool(
            initialCapacity,
            Unsafe.SizeOf<T>(),
            $"NativePool<{typeof(T).FullName ?? typeof(T).Name}>",
            returnOnDispose);
    }

    /// <summary>
    /// Rents a logical range from the active generation.
    /// </summary>
    /// <param name="length">The logical element count; zero is a valid identity-bearing lease.</param>
    /// <returns>A generation-bound pooled handle.</returns>
    public Pooled<T> Rent(int length)
    {
        NativePoolLease lease = _kernel.Rent(length);
        return new Pooled<T>(_kernel, lease.Generation, lease.AllocationId, lease.Length, lease.Capacity);
    }

    /// <summary>
    /// Invalidates the current generation and frees its native segments immediately.
    /// </summary>
    public void ReturnToNativeMemory()
    {
        _kernel.ReturnToNativeMemory();
    }

    /// <summary>
    /// Invalidates the current generation and detaches its segments to a finalizable owner.
    /// </summary>
    public void ReturnToGarbageCollector()
    {
        _kernel.ReturnToGarbageCollector();
    }

    /// <summary>
    /// Starts a fresh generation on this pool after a successful whole-generation return.
    /// </summary>
    public void LeaseFromMemory()
    {
        _kernel.LeaseFromMemory();
    }

    /// <summary>
    /// Permanently closes the pool and applies its configured cleanup policy to the active generation.
    /// </summary>
    public void Dispose()
    {
        _kernel.Dispose();
    }
}
