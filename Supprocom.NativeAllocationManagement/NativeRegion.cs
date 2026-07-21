namespace Supprocom.NativeAllocationManagement;

/// <summary>A stack-confined heterogeneous bump owner for one explicit braced using statement.</summary>
public readonly ref struct NativeRegion
{
    private readonly NativeOwnerKernel? _kernel;

    /// <summary>Creates an active region with default reservation and cleanup policy.</summary>
    public NativeRegion()
    {
        _kernel = NativeOwnerKernel.CreateRegion(
            preAllocateBytes: 0,
            "NativeRegion",
            NativeMemoryReturn.ToGarbageCollector,
            containsReferences: false,
            doNotLeaseOnDeclaration: false);
    }

    internal NativeOwnerLifecycle CurrentLifecycle => _kernel?.Lifecycle ?? NativeOwnerLifecycle.Uninitialized;

    /// <summary>Creates an active region unless the first generation is explicitly deferred.</summary>
    /// <param name="preAllocateBytes">Optional initial byte reservation.</param>
    /// <param name="returnMemoryOnDispose">The physical cleanup policy used by <see cref="Dispose"/>.</param>
    /// <param name="doNotLeaseOnDeclaration">When true, defer the first generation until <see cref="LeaseFromMemory"/>.</param>
    public NativeRegion(
        nuint preAllocateBytes = 0,
        NativeMemoryReturn returnMemoryOnDispose = NativeMemoryReturn.ToGarbageCollector,
        bool doNotLeaseOnDeclaration = false)
    {
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        _kernel = NativeOwnerKernel.CreateRegion(
            preAllocateBytes,
            "NativeRegion",
            returnMemoryOnDispose,
            containsReferences: false,
            doNotLeaseOnDeclaration);
    }

    /// <summary>Leases an ordinary heterogeneous range from the active region.</summary>
    public Local<T> Lease<T>(int length)
    {
        NativeOwnerKernel kernel = GetKernel(nameof(Lease));
        NativeRegionAllocation allocation = kernel.LeaseBump(
            length,
            NativeTypeLayout.StorageSize<T>(),
            NativeTypeLayout.Alignment<T>(),
            scoped: false,
            typeof(T));
        return new Local<T>(kernel, allocation.Generation, allocation.AllocationId, allocation.Length, allocation.Capacity);
    }

    /// <summary>Leases a scoped range whose storage can be completed by <see cref="RecycleScoped"/>.</summary>
    public Local<T> LeaseScoped<T>(int length)
    {
        NativeOwnerKernel kernel = GetKernel(nameof(LeaseScoped));
        NativeRegionAllocation allocation = kernel.LeaseBump(
            length,
            NativeTypeLayout.StorageSize<T>(),
            NativeTypeLayout.Alignment<T>(),
            scoped: true,
            typeof(T));
        return new Local<T>(kernel, allocation.Generation, allocation.AllocationId, allocation.Length, allocation.Capacity);
    }

    /// <summary>Publishes the initial region generation when declaration leasing was deferred.</summary>
    public void LeaseFromMemory() => GetKernel(nameof(LeaseFromMemory)).LeaseFromMemory();

    /// <summary>Recycles the complete analyzer-proven dead scoped pending set.</summary>
    public void RecycleScoped() => GetKernel(nameof(RecycleScoped)).RecycleScoped();

    /// <summary>Ends the current region generation and frees native storage immediately.</summary>
    public void ReturnMemoryToNativeMemory() => GetKernel(nameof(ReturnMemoryToNativeMemory)).ReturnMemoryToNativeMemory();

    /// <summary>Ends the current region generation and detaches storage for finalizable cleanup.</summary>
    public void ReturnMemoryToGarbageCollector() => GetKernel(nameof(ReturnMemoryToGarbageCollector)).ReturnMemoryToGarbageCollector();

    /// <summary>Releases every eligible idle segment without changing the region lifecycle.</summary>
    public nuint TrimRetainedMemory() => GetKernel(nameof(TrimRetainedMemory)).TrimRetainedMemory();

    /// <summary>Releases whole idle segments until the requested physical byte budget is met.</summary>
    public nuint TrimRetainedMemoryByBytes(nuint bytesToRelease) =>
        GetKernel(nameof(TrimRetainedMemoryByBytes)).TrimRetainedMemoryByBytes(bytesToRelease);

    /// <summary>Trims by the exact physical footprint requested by a heterogeneous lease shape.</summary>
    public nuint TrimRetainedMemoryByLeaseSize<T>(int leaseLength = 1) =>
        GetKernel(nameof(TrimRetainedMemoryByLeaseSize)).TrimRetainedMemoryByLeaseSize(
            leaseLength,
            NativeTypeLayout.StorageSize<T>(),
            typeof(T));

    /// <summary>Ends the lexical region using its configured memory policy.</summary>
    public void Dispose() => GetKernel(nameof(Dispose)).Dispose();

    private NativeOwnerKernel GetKernel(string operation) =>
        _kernel ?? throw new NativeAllocationUninitializedException(nameof(NativeRegion), operation);
}
