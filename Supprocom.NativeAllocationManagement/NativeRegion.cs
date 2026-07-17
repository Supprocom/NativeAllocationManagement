using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>
/// A stack-confined heterogeneous bump allocator for unmanaged local values.
/// </summary>
public readonly ref struct NativeRegion
{
    private readonly NativeOwnerKernel? _kernel;

    internal NativeOwnerLifecycle CurrentLifecycle => _kernel?.Lifecycle ?? NativeOwnerLifecycle.Uninitialized;

    /// <summary>
    /// Creates a lazily allocated region using deferred cleanup by default.
    /// </summary>
    public NativeRegion()
    {
        _kernel = NativeOwnerKernel.CreateRegion(
            preAllocateBytes: 0,
            "NativeRegion",
            NativeReturn.ToGarbageCollector,
            doNotLeaseOnDeclaration: false);
    }

    /// <summary>
    /// Creates a region that remains unleased until <see cref="LeaseFromMemory"/> succeeds.
    /// </summary>
    /// <param name="doNotLeaseOnDeclaration">When true, defer the first generation until <see cref="LeaseFromMemory"/>.</param>
    public NativeRegion(bool doNotLeaseOnDeclaration)
    {
        _kernel = NativeOwnerKernel.CreateRegion(
            preAllocateBytes: 0,
            "NativeRegion",
            NativeReturn.ToGarbageCollector,
            doNotLeaseOnDeclaration);
    }

    /// <summary>
    /// Creates a lazily allocated region with an explicit cleanup policy.
    /// </summary>
    public NativeRegion(
        NativeReturn returnOnDispose,
        bool doNotLeaseOnDeclaration = false)
    {
        NativeReturnValidation.Validate(returnOnDispose, nameof(returnOnDispose));
        _kernel = NativeOwnerKernel.CreateRegion(0, "NativeRegion", returnOnDispose, doNotLeaseOnDeclaration);
    }

    /// <summary>
    /// Creates a region with an optional initial byte reservation.
    /// </summary>
    public NativeRegion(
        nuint preAllocateBytes,
        NativeReturn returnOnDispose = NativeReturn.ToGarbageCollector,
        bool doNotLeaseOnDeclaration = false)
    {
        NativeReturnValidation.Validate(returnOnDispose, nameof(returnOnDispose));
        _kernel = NativeOwnerKernel.CreateRegion(preAllocateBytes, "NativeRegion", returnOnDispose, doNotLeaseOnDeclaration);
    }

    /// <summary>
    /// Leases a typed local range from the region's current generation.
    /// </summary>
    public Local<T> Lease<T>(int length)
        where T : unmanaged
    {
        NativeOwnerKernel kernel = GetKernel(nameof(Lease));
        NativeRegionAllocation allocation = kernel.LeaseRegion(
            length,
            Unsafe.SizeOf<T>(),
            GetAlignment<T>());
        return new Local<T>(kernel, allocation.Generation, allocation.AllocationId, allocation.Length, allocation.Capacity);
    }

    /// <summary>
    /// Publishes the initial region generation when declaration leasing was deferred.
    /// </summary>
    public void LeaseFromMemory()
    {
        GetKernel(nameof(LeaseFromMemory)).LeaseFromMemory();
    }

    /// <summary>
    /// Ends the current region generation and frees native segments immediately.
    /// </summary>
    public void ReturnToNativeMemory()
    {
        GetKernel(nameof(ReturnToNativeMemory)).ReturnToNativeMemory();
    }

    /// <summary>
    /// Ends the current region generation and detaches native segments for finalizable cleanup.
    /// </summary>
    public void ReturnToGarbageCollector()
    {
        GetKernel(nameof(ReturnToGarbageCollector)).ReturnToGarbageCollector();
    }

    /// <summary>
    /// Ends the region exactly once using its configured cleanup policy.
    /// </summary>
    public void Dispose()
    {
        GetKernel(nameof(Dispose)).Dispose();
    }

    private NativeOwnerKernel GetKernel(string operation)
    {
        return _kernel ?? throw new NativeAllocationUninitializedException(nameof(NativeRegion), operation);
    }

    private static nuint GetAlignment<T>()
        where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        nuint alignment = 1;
        nuint limit = (nuint)Math.Min(size, IntPtr.Size);
        while (alignment <= limit / 2)
        {
            alignment *= 2;
        }

        return alignment;
    }
}
