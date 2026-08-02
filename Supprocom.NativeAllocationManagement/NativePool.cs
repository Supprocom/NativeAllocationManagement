namespace Supprocom.NativeAllocationManagement;

/// <summary>Owns reusable native slabs for one element type.</summary>
/// <typeparam name="T">The value or reference type stored in each lease.</typeparam>
public sealed class NativePool<T> : IDisposable
{
    private readonly NativeOwnerKernel _kernel;

    internal NativeOwnerKernel KernelForTransfer => _kernel;

    internal NativeOwnerLifecycle CurrentLifecycle => _kernel.Lifecycle;

    internal int CurrentAllocationRecordCountForTest => _kernel.CurrentAllocationRecordCountForTest();

    internal int CurrentReferenceRootCountForTest => _kernel.CurrentReferenceRootCountForTest();

    internal int QuarantinedSegmentCountForTest => _kernel.QuarantinedSegmentCountForTest();

    internal int QuarantinedGenerationCountForTest => _kernel.QuarantinedGenerationCountForTest();

    internal int RetiredGenerationCountForTest => _kernel.RetiredGenerationCountForTest();

    internal int QuarantineCapacityForTest => _kernel.QuarantineCapacityForTest();

    internal (int Slabs, int AvailableSlabs, int Bumps, int OwnerSegments) CurrentBankCapacitiesForTest =>
        _kernel.CurrentBankCapacitiesForTest();

    internal long CurrentScopeEpochForTest => _kernel.CurrentScopeEpochForTest();

    internal long GenerationCounterForTest => _kernel.GenerationCounterForTest();

    internal long[] CurrentSegmentOrdinalsForTest => _kernel.CurrentSegmentOrdinalsForTest();

    internal void SetScopeEpochForTest(long value) => _kernel.SetScopeEpochForTest(value);

    internal void SetGenerationCounterForTest(long value) => _kernel.SetGenerationCounterForTest(value);

    /// <summary>Reads the current logical and physical state of this owner.</summary>
    public NativeOwnerStatistics GetStatistics() => _kernel.GetStatistics();

    /// <summary>Creates a typed pool, active immediately unless declaration leasing is disabled.</summary>
    /// <param name="initialCapacity">Optional number of elements reserved immediately or on activation.</param>
    /// <param name="returnMemoryOnDispose">The physical cleanup policy used by <see cref="Dispose"/>.</param>
    /// <param name="doNotLeaseOnDeclaration">When true, defer the first generation until <see cref="LeaseFromMemory"/>.</param>
    public NativePool(
        int initialCapacity = 0,
        NativeMemoryReturn returnMemoryOnDispose = NativeMemoryReturn.ToGarbageCollector,
        bool doNotLeaseOnDeclaration = false)
    {
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        _kernel = NativeOwnerKernel.CreatePool(
            initialCapacity,
            NativeTypeLayout.StorageSize<T>(),
            $"NativePool<{typeof(T).FullName ?? typeof(T).Name}>",
            returnMemoryOnDispose,
            NativeTypeLayout.ContainsReferences<T>(),
            doNotLeaseOnDeclaration);
    }

    /// <summary>Rents a range after one initializer writes all logical elements.</summary>
    public Pooled<T> Rent(
        int length,
        NativeLeaseInitializer<T> initializer)
    {
        NativePoolLease lease = _kernel.RentInitialized(
            length,
            scoped: false,
            initializer);
        return new Pooled<T>(_kernel, lease);
    }

    /// <summary>Initializes a scoped range before NAM publishes its readable lease.</summary>
    public Pooled<T> LeaseScoped(
        int length,
        NativeLeaseInitializer<T> initializer)
    {
        NativePoolLease lease = _kernel.RentInitialized(
            length,
            scoped: true,
            initializer);
        return new Pooled<T>(_kernel, lease);
    }

    /// <summary>Ends the current memory generation and frees its native storage immediately.</summary>
    public void ReturnMemoryToNativeMemory() => _kernel.ReturnMemoryToNativeMemory();

    /// <summary>Ends the current memory generation and detaches its storage for finalizable cleanup.</summary>
    public void ReturnMemoryToGarbageCollector() => _kernel.ReturnMemoryToGarbageCollector();

    /// <summary>Invalidates all current leases and reuses retained slabs in a new generation.</summary>
    public void ReleaseLeasesToNativeMemory() => _kernel.ReleaseLeasesToNativeMemory();

    /// <summary>Invalidates all current leases while allowing already entered operations to drain.</summary>
    public void ReleaseLeasesToGarbageCollector() => _kernel.ReleaseLeasesToGarbageCollector();

    /// <summary>Publishes the first generation or the generation reserved by a memory return.</summary>
    public void LeaseFromMemory() => _kernel.LeaseFromMemory();

    /// <summary>Recycles the complete analyzer-proven dead scoped pending set.</summary>
    public void RecycleScoped() => _kernel.RecycleScoped();

    /// <summary>Releases all eligible idle slabs and reports actual physical bytes freed.</summary>
    public nuint TrimRetainedMemory() => _kernel.TrimRetainedMemory();

    /// <summary>Releases whole idle slabs until the requested physical byte budget is met.</summary>
    public nuint TrimRetainedMemoryByBytes(nuint bytesToRelease) => _kernel.TrimRetainedMemoryByBytes(bytesToRelease);

    /// <summary>Trims by the exact physical footprint requested by a typed lease shape.</summary>
    public nuint TrimRetainedMemoryByLeaseSize(int leaseLength = 1) =>
        _kernel.TrimRetainedMemoryByLeaseSize(
            leaseLength,
            NativeTypeLayout.StorageSize<T>(),
            NativeTypeLayout.Alignment<T>());

    /// <summary>Permanently closes the owner and applies its configured memory policy.</summary>
    public void Dispose() => _kernel.Dispose();
}
