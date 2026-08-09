namespace Supprocom.NativeAllocationManagement;

/// <summary>Owns reusable native slabs for one unmanaged element type.</summary>
/// <typeparam name="T">The unmanaged value type in each slab.</typeparam>
public sealed class NativePool<T> : IDisposable
    where T : unmanaged
{
    private readonly NativePoolKernel<T> _kernel;

    internal NativeOwnerLifecycle CurrentLifecycle => _kernel.Lifecycle;

    internal int CurrentAllocationRecordCountForTest =>
        _kernel.LiveLeaseCount;

    internal int CurrentInitializationCountForTest => 0;

    internal int CurrentGenerationActiveOperationsForTest => 0;

    internal int CurrentReferenceRootCountForTest => 0;

    internal int QuarantinedSegmentCountForTest => 0;

    internal int QuarantinedGenerationCountForTest => 0;

    internal int RetiredGenerationCountForTest => 0;

    internal int QuarantineCapacityForTest => 0;

    internal (int Slabs, int AvailableSlabs, int Bumps, int OwnerSegments)
        CurrentBankCapacitiesForTest
    {
        get
        {
            NativeOwnerStatistics statistics = _kernel.GetStatistics();
            return (
                statistics.SegmentCount,
                statistics.AvailableSegmentCount,
                0,
                0);
        }
    }

    internal long CurrentScopeEpochForTest => 0;

    internal long GenerationCounterForTest => 0;

    internal long[] CurrentSegmentOrdinalsForTest => [];

    internal void SetScopeEpochForTest(long value) =>
        throw new NotSupportedException(
            "NativePool does not have scoped generations.");

    internal void SetGenerationCounterForTest(long value) =>
        throw new NotSupportedException(
            "NativePool does not have generation counters.");

    /// <summary>Creates one active typed slab pool.</summary>
    /// <param name="preLease">The typed element capacity to reserve.</param>
    /// <param name="returnMemoryOnDispose">The final storage cleanup policy.</param>
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

    /// <summary>Closes the pool after all leases return.</summary>
    public void Dispose() => _kernel.Dispose();
}
