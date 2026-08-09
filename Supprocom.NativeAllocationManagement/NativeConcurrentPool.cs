namespace Supprocom.NativeAllocationManagement;

/// <summary>Owns synchronized typed storage for transferable native values.</summary>
/// <typeparam name="T">The unmanaged value type in owned storage.</typeparam>
public sealed class NativeConcurrentPool<T> : IDisposable
{
    private readonly NativeOwnerKernel _kernel;

    internal NativeOwnerKernel KernelForTransfer => _kernel;

    internal NativeOwnerLifecycle CurrentLifecycle => _kernel.Lifecycle;

    internal int CurrentAllocationRecordCountForTest =>
        _kernel.CurrentAllocationRecordCountForTest();

    internal int CurrentInitializationCountForTest =>
        _kernel.CurrentInitializationCountForTest();

    internal int CurrentGenerationActiveOperationsForTest =>
        _kernel.CurrentGenerationActiveOperationsForTest();

    internal int CurrentReferenceRootCountForTest =>
        _kernel.CurrentReferenceRootCountForTest();

    internal int QuarantinedSegmentCountForTest =>
        _kernel.QuarantinedSegmentCountForTest();

    internal int QuarantinedGenerationCountForTest =>
        _kernel.QuarantinedGenerationCountForTest();

    internal int RetiredGenerationCountForTest =>
        _kernel.RetiredGenerationCountForTest();

    internal int QuarantineCapacityForTest =>
        _kernel.QuarantineCapacityForTest();

    internal (int Slabs, int AvailableSlabs, int Bumps, int OwnerSegments)
        CurrentBankCapacitiesForTest =>
            _kernel.CurrentBankCapacitiesForTest();

    internal long CurrentScopeEpochForTest =>
        _kernel.CurrentScopeEpochForTest();

    internal long GenerationCounterForTest =>
        _kernel.GenerationCounterForTest();

    internal long[] CurrentSegmentOrdinalsForTest =>
        _kernel.CurrentSegmentOrdinalsForTest();

    internal void SetScopeEpochForTest(long value) =>
        _kernel.SetScopeEpochForTest(value);

    internal void SetGenerationCounterForTest(long value) =>
        _kernel.SetGenerationCounterForTest(value);

    /// <summary>Creates one active synchronized typed owner.</summary>
    /// <param name="preLease">The typed element capacity to reserve.</param>
    /// <param name="returnMemoryOnDispose">The final storage cleanup policy.</param>
    /// <param name="doNotLeaseOnDeclaration">Defers the first generation when true.</param>
    public NativeConcurrentPool(
        int preLease = 0,
        NativeMemoryReturn returnMemoryOnDispose =
            NativeMemoryReturn.ToGarbageCollector,
        bool doNotLeaseOnDeclaration = false)
        : this(
            preLease,
            preAllocateBytes: 0,
            returnMemoryOnDispose,
            doNotLeaseOnDeclaration)
    {
    }

    /// <summary>Creates one active owner with typed and raw reservations.</summary>
    /// <param name="preLease">The typed element capacity to reserve.</param>
    /// <param name="preAllocateBytes">The exact raw byte capacity to reserve.</param>
    /// <param name="returnMemoryOnDispose">The final storage cleanup policy.</param>
    /// <param name="doNotLeaseOnDeclaration">Defers the first generation when true.</param>
    public NativeConcurrentPool(
        int preLease,
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose =
            NativeMemoryReturn.ToGarbageCollector,
        bool doNotLeaseOnDeclaration = false)
    {
        NativeMemoryReturnValidation.Validate(
            returnMemoryOnDispose,
            nameof(returnMemoryOnDispose));
        _kernel = NativeOwnerKernel.CreatePool(
            preLease,
            preAllocateBytes,
            NativeTypeLayout.StorageSize<T>(),
            $"NativeConcurrentPool<{typeof(T).FullName ?? typeof(T).Name}>",
            returnMemoryOnDispose,
            NativeTypeLayout.ContainsReferences<T>(),
            doNotLeaseOnDeclaration);
    }

    /// <summary>Reads the synchronized owner state.</summary>
    public NativeOwnerStatistics GetStatistics() =>
        _kernel.GetStatistics();

    /// <summary>Initializes and publishes one synchronized lease.</summary>
    public ConcurrentPooled<T> Rent(
        int length,
        NativeLeaseInitializer<T> initializer)
    {
        NativePoolLease lease = _kernel.RentInitialized(
            length,
            scoped: false,
            initializer);
        return new ConcurrentPooled<T>(_kernel, lease);
    }

    /// <summary>Initializes one synchronized scoped lease.</summary>
    public ConcurrentPooled<T> LeaseScoped(
        int length,
        NativeLeaseInitializer<T> initializer)
    {
        NativePoolLease lease = _kernel.RentInitialized(
            length,
            scoped: true,
            initializer);
        return new ConcurrentPooled<T>(_kernel, lease);
    }

    /// <summary>Ends the active generation and releases its storage.</summary>
    public void ReturnMemoryToNativeMemory() =>
        _kernel.ReturnMemoryToNativeMemory();

    /// <summary>Ends the active generation and detaches its storage.</summary>
    public void ReturnMemoryToGarbageCollector() =>
        _kernel.ReturnMemoryToGarbageCollector();

    /// <summary>Ends all leases and reuses retained storage.</summary>
    public void ReleaseLeasesToNativeMemory() =>
        _kernel.ReleaseLeasesToNativeMemory();

    /// <summary>Ends all leases after active callbacks drain.</summary>
    public void ReleaseLeasesToGarbageCollector() =>
        _kernel.ReleaseLeasesToGarbageCollector();

    /// <summary>Starts the next synchronized memory generation.</summary>
    public void LeaseFromMemory() =>
        _kernel.LeaseFromMemory();

    /// <summary>Recycles the analyzer-proven scoped lease set.</summary>
    public void RecycleScoped() =>
        _kernel.RecycleScoped();

    /// <summary>Frees all eligible idle storage.</summary>
    public nuint TrimRetainedMemory() =>
        _kernel.TrimRetainedMemory();

    /// <summary>Frees idle storage until the byte budget is met.</summary>
    public nuint TrimRetainedMemoryByBytes(nuint bytesToRelease) =>
        _kernel.TrimRetainedMemoryByBytes(bytesToRelease);

    /// <summary>Frees idle storage for one typed request budget.</summary>
    public nuint TrimRetainedMemoryByLeaseSize(int leaseLength = 1) =>
        _kernel.TrimRetainedMemoryByLeaseSize(
            leaseLength,
            NativeTypeLayout.StorageSize<T>(),
            NativeTypeLayout.Alignment<T>());

    /// <summary>Closes the owner after all native ownership returns.</summary>
    public void Dispose() => _kernel.Dispose();
}
