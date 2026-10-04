namespace Supprocom.NativeAllocationManagement;

/// <summary>Owns reusable native slabs for one unmanaged element type.</summary>
/// <typeparam name="T">The unmanaged value type in each slab.</typeparam>
public sealed class NativePool<T> : IDisposable
    where T : unmanaged
{
    private readonly NativePoolKernel<T> _kernel;

    /// <summary>Gets the stable process-local allocator identity.</summary>
    public long Id => _kernel.Id;

    internal NativeOwnerLifecycle CurrentLifecycle => _kernel.Lifecycle;

    internal int CurrentAllocationRecordCountForTest =>
        _kernel.LiveLeaseCount;

    internal int CurrentInitializationCountForTest => _kernel.InitializationCount;

    internal int CurrentGenerationActiveOperationsForTest => _kernel.ActiveOperationCount;

    internal (int Slabs, int AvailableSlabs, int Bumps, int OwnerSegments)
        CurrentBankCapacitiesForTest => _kernel.BankCapacities;

    // These structures genuinely do not exist in the unmanaged, thread-confined
    // pool model. There is no reference-root bank or generational retirement.
#pragma warning disable CA1822
    internal int CurrentReferenceRootCountForTest => 0;

    internal int QuarantinedSegmentCountForTest => 0;

    internal int QuarantinedGenerationCountForTest => 0;

    internal int RetiredGenerationCountForTest => 0;

    internal int QuarantineCapacityForTest => 0;

    internal long CurrentScopeEpochForTest => 0;

    internal long GenerationCounterForTest => 0;

    internal long[] CurrentSegmentOrdinalsForTest => [];
#pragma warning restore CA1822

    internal void SetScopeEpochForTest(long value) =>
        throw new NotSupportedException(
            "NativePool does not have scoped generations.");

    internal void SetGenerationCounterForTest(long value) =>
        throw new NotSupportedException(
            "NativePool does not have generation counters.");

    /// <summary>Creates one active typed slab pool.</summary>
    /// <param name="preLease">The typed element capacity to reserve.</param>
    /// <param name="returnMemoryOnDispose">The final storage cleanup policy.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "RS0027", Justification = "Preserve the published optional signature; the budget-first overload has only required arguments and strictly greater arity, so it cannot capture any existing call.")]
    public NativePool(
        int preLease = 0,
        NativeMemoryReturn returnMemoryOnDispose =
            NativeMemoryReturn.ToGarbageCollector)
        : this(
            preLease,
            preAllocateBytes: 0,
            returnMemoryOnDispose)
    {
    }

    /// <summary>Creates one active pool with typed and raw reservations.</summary>
    /// <param name="preLease">The typed element capacity to reserve.</param>
    /// <param name="preAllocateBytes">The exact raw byte capacity to reserve.</param>
    /// <param name="returnMemoryOnDispose">The final storage cleanup policy.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "RS0027", Justification = "Preserve the published optional signature; the budget-first overload has only required arguments and strictly greater arity, so it cannot capture any existing call.")]
    public NativePool(
        int preLease,
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose =
            NativeMemoryReturn.ToGarbageCollector)
    {
        NativeMemoryReturnValidation.Validate(
            returnMemoryOnDispose,
            nameof(returnMemoryOnDispose));
        _kernel = new NativePoolKernel<T>(
            preLease,
            preAllocateBytes,
            returnMemoryOnDispose);
    }

    /// <summary>Creates a pool whose complete backing extents share one admission ceiling.</summary>
    /// <param name="budget">The backing admission domain.</param>
    /// <param name="preLease">The typed element capacity to reserve.</param>
    /// <param name="preAllocateBytes">The exact raw byte capacity to reserve.</param>
    /// <param name="returnMemoryOnDispose">The final storage cleanup policy.</param>
    public NativePool(
        NativeMemoryBudget budget,
        int preLease,
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose)
    {
        ArgumentNullException.ThrowIfNull(budget);
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        _kernel = new NativePoolKernel<T>(preLease, preAllocateBytes, returnMemoryOnDispose, budget);
    }

    /// <summary>Reads the current typed slab state.</summary>
    public NativeOwnerStatistics GetStatistics() =>
        _kernel.GetStatistics();

    /// <summary>Initializes and publishes one pooled slab lease.</summary>
    public Pooled<T> Rent(
        int length,
        NativeLeaseInitializer<T> initializer) =>
        _kernel.Rent(length, initializer);

    /// <summary>Frees all idle slabs.</summary>
    public nuint TrimRetainedMemory() =>
        _kernel.TrimRetainedMemory();

    /// <summary>Frees idle slabs until the byte budget is met.</summary>
    public nuint TrimRetainedMemoryByBytes(nuint bytesToRelease) =>
        _kernel.TrimRetainedMemory(bytesToRelease);

    /// <summary>Frees idle slabs for one typed request budget.</summary>
    public nuint TrimRetainedMemoryByLeaseSize(int leaseLength = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leaseLength);
        nuint bytes = checked(
            (nuint)(uint)leaseLength
            * (nuint)System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
        return _kernel.TrimRetainedMemory(bytes);
    }

    /// <summary>Ends worker-thread use and converts this pool into cleanup-only authority.</summary>
    public void Retire() => _kernel.Retire();

    /// <summary>Releases one retired pool from a coordinator thread.</summary>
    public void ReleaseRetiredStorage() =>
        _kernel.ReleaseRetiredStorage();

    /// <summary>Closes the pool after all leases return.</summary>
    public void Dispose() => _kernel.Dispose();
}
