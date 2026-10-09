namespace Supprocom.NativeAllocationManagement;

/// <summary>Owns reusable native slabs for one unmanaged element type.</summary>
/// <typeparam name="T">The unmanaged value type in each slab.</typeparam>
public sealed partial class NativePool<T> : IDisposable
    where T : unmanaged
{
    /// <summary>Gets the stable process-local allocator identity.</summary>
    public long Id { get; }

    /// <summary>Captures actual thread-confined slab state without retaining native authority.</summary>
    public NativeOwnerDiagnosticSnapshot CaptureDiagnosticSnapshot() =>
        GetDiagnosticSnapshot();

    internal NativeOwnerLifecycle CurrentLifecycle => _lifecycle;

    internal int CurrentAllocationRecordCountForTest =>
        _liveLeaseCount;

    internal (int Slabs, int AvailableSlabs, int Bumps, int OwnerSegments)
        CurrentBankCapacitiesForTest => (_slabs.Length, _slabs.Length, 0, 0);

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

#pragma warning restore CA1822

    internal long[] CurrentSegmentOrdinalsForTest => GetSegmentOrdinals();

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
        : this(preLease, preAllocateBytes, returnMemoryOnDispose, budget: null, requireBudget: false)
    {
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
        : this(preLease, preAllocateBytes, returnMemoryOnDispose, budget, requireBudget: true)
    {
    }

    /// <summary>Frees idle slabs until the byte budget is met.</summary>
    public nuint TrimRetainedMemoryByBytes(nuint bytesToRelease) =>
        TrimRetainedMemory(bytesToRelease);

    /// <summary>Frees idle slabs for one typed request budget.</summary>
    public nuint TrimRetainedMemoryByLeaseSize(int leaseLength = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leaseLength);
        nuint bytes = checked(
            (nuint)(uint)leaseLength
            * (nuint)System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
        return TrimRetainedMemory(bytes);
    }
}
