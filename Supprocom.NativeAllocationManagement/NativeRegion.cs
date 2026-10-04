namespace Supprocom.NativeAllocationManagement;

/// <summary>A thread-confined bump owner with one lexical lifetime.</summary>
public readonly ref struct NativeRegion
{
    private readonly NativeRegionKernel? _kernel;

    /// <summary>Creates one active Region with finalizable emergency cleanup.</summary>
    public NativeRegion()
    {
        _kernel = new NativeRegionKernel(
            preAllocateBytes: 0,
            NativeMemoryReturn.ToGarbageCollector);
    }

    /// <summary>Creates one active Region with the specified native reservation.</summary>
    /// <param name="preAllocateBytes">The initial native byte capacity.</param>
    /// <param name="returnMemoryOnDispose">The cleanup policy for the complete Region.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "RS0027", Justification = "Preserve the published optional signature; the budget-first overload has only required arguments and strictly greater arity, so it cannot capture any existing call.")]
    public NativeRegion(
        nuint preAllocateBytes = 0,
        NativeMemoryReturn returnMemoryOnDispose = NativeMemoryReturn.ToGarbageCollector)
    {
        NativeMemoryReturnValidation.Validate(
            returnMemoryOnDispose,
            nameof(returnMemoryOnDispose));
        _kernel = new NativeRegionKernel(
            preAllocateBytes,
            returnMemoryOnDispose);
    }

    /// <summary>Creates a lexical owner with complete backing admitted by a shared ceiling.</summary>
    /// <param name="budget">The backing admission domain.</param>
    /// <param name="preAllocateBytes">The initial usable byte reservation; native headers are charged too.</param>
    /// <param name="returnMemoryOnDispose">The final storage cleanup policy.</param>
    public NativeRegion(NativeMemoryBudget budget, nuint preAllocateBytes, NativeMemoryReturn returnMemoryOnDispose)
    {
        ArgumentNullException.ThrowIfNull(budget);
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        _kernel = new NativeRegionKernel(preAllocateBytes, returnMemoryOnDispose, budget);
    }

    internal NativeOwnerLifecycle CurrentLifecycle =>
        _kernel?.Lifecycle
        ?? NativeOwnerLifecycle.Uninitialized;

    // Compatibility probe retains the per-owner instance shape.
#pragma warning disable CA1822
    internal int CurrentAllocationRecordCountForTest => 0;
#pragma warning restore CA1822

    /// <summary>Gets the current Region storage statistics.</summary>
    public NativeOwnerStatistics GetStatistics() =>
        GetKernel(nameof(GetStatistics)).GetStatistics();

    /// <summary>Initializes and publishes one unmanaged bump range.</summary>
    public Local<T> Lease<T>(
        int length,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged
    {
        NativeRegionKernel kernel = GetKernel(nameof(Lease));
        return kernel.LeaseInitialized(length, initializer);
    }

    /// <summary>Invalidates all Local handles and ends the Region lifetime.</summary>
    public void Dispose() => GetKernel(nameof(Dispose)).Dispose();

    private NativeRegionKernel GetKernel(string operation) =>
        _kernel
        ?? throw new NativeAllocationUninitializedException(
            nameof(NativeRegion),
            operation);
}
