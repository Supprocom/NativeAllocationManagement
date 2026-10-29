namespace Supprocom.NativeAllocationManagement;

/// <summary>A thread-confined bump owner with one lexical lifetime.</summary>
public readonly ref struct NativeRegion
{
    private readonly NativeRegionKernel? _kernel;
    private readonly int _generation;

    /// <summary>Gets the stable process-local allocator identity.</summary>
    public long Id => GetKernel(nameof(Id)).Id;

    /// <summary>Captures actual lexical storage state without retaining native authority.</summary>
    public NativeOwnerDiagnosticSnapshot CaptureDiagnosticSnapshot() =>
        GetKernel(nameof(CaptureDiagnosticSnapshot)).GetDiagnosticSnapshot();

    /// <summary>Creates one active Region with finalizable emergency cleanup.</summary>
    public NativeRegion()
    {
        _kernel = NativeRegionKernel.Acquire(
            preAllocateBytes: 0,
            NativeMemoryReturn.Reuse);
        _generation = _kernel.Generation;
    }

    /// <summary>Creates one active Region with the specified native reservation.</summary>
    /// <param name="preAllocateBytes">The initial native byte capacity.</param>
    /// <param name="returnMemoryOnDispose">The cleanup policy for the complete Region.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "RS0027", Justification = "Preserve the published optional signature; the budget-first overload has only required arguments and strictly greater arity, so it cannot capture any existing call.")]
    public NativeRegion(
        nuint preAllocateBytes = 0,
        NativeMemoryReturn returnMemoryOnDispose = NativeMemoryReturn.Reuse)
    {
        NativeMemoryReturnValidation.Validate(
            returnMemoryOnDispose,
            nameof(returnMemoryOnDispose));
        _kernel = NativeRegionKernel.Acquire(
            preAllocateBytes,
            returnMemoryOnDispose);
        _generation = _kernel.Generation;
    }

    /// <summary>Creates a lexical owner with complete backing admitted by a shared ceiling.</summary>
    /// <param name="budget">The backing admission domain.</param>
    /// <param name="preAllocateBytes">The initial usable byte reservation; native headers are charged too.</param>
    /// <param name="returnMemoryOnDispose">The final storage cleanup policy.</param>
    public NativeRegion(NativeMemoryBudget budget, nuint preAllocateBytes, NativeMemoryReturn returnMemoryOnDispose)
    {
        ArgumentNullException.ThrowIfNull(budget);
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        _kernel = NativeRegionKernel.Acquire(preAllocateBytes, returnMemoryOnDispose, budget);
        _generation = _kernel.Generation;
    }

    internal NativeOwnerLifecycle CurrentLifecycle =>
        _kernel is null
            ? NativeOwnerLifecycle.Uninitialized
            : _kernel.Generation != _generation
                ? NativeOwnerLifecycle.Disposed
                : _kernel.Lifecycle;

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

    /// <summary>Fills and publishes one unmanaged bump range without an initializer callback.</summary>
    /// <param name="length">The complete initialized element count.</param>
    /// <param name="value">The value written to every element before publication.</param>
    public Local<T> Lease<T>(
        int length,
        T value)
        where T : unmanaged =>
        GetKernel(nameof(Lease)).LeaseFilled(length, value);

    /// <summary>Publishes one unmanaged bump range without writing its elements.</summary>
    /// <param name="length">The element count.</param>
    /// <remarks>The contents are unspecified: they may hold values an earlier lease in this process wrote. Write every element before reading it, or use <see cref="Lease{T}(int, T)"/> for a filled range.</remarks>
    public Local<T> LeaseUninitialized<T>(int length)
        where T : unmanaged =>
        GetKernel(nameof(LeaseUninitialized)).LeaseUninitialized<T>(length);

    /// <summary>Invalidates all Local handles and ends the Region lifetime.</summary>
    public void Dispose()
    {
        // Small enough that a using statement's finally runs inline instead
        // of as a funclet call: the failure is thrown out of line.
        NativeRegionKernel? kernel = _kernel;
        if (kernel is null)
        {
            ThrowUninitializedDispose();
        }

        // A later region may reuse this kernel; disposal of an earlier lifetime
        // is already complete.
        if (kernel.Generation == _generation)
        {
            kernel.Dispose();
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowUninitializedDispose() =>
        throw new NativeAllocationUninitializedException(
            nameof(NativeRegion),
            nameof(Dispose));

    private NativeRegionKernel GetKernel(string operation)
    {
        NativeRegionKernel kernel = _kernel
            ?? throw new NativeAllocationUninitializedException(
                nameof(NativeRegion),
                operation);
        if (kernel.Generation != _generation)
        {
            throw NativeRegionKernel.CreateEndedLifetime(operation);
        }

        return kernel;
    }
}
