using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Declares fixed usable byte bounds for the ordinary and scoped bump lanes.</summary>
/// <remarks>Both zero-length lanes are valid. Native headers and backend padding are admitted separately from usable capacity.</remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeArenaPreparation
{
    /// <summary>Creates immutable lane bounds; alignment between ranges consumes these bounds.</summary>
    /// <param name="ordinaryBytes">Usable bytes retained across scoped recycling.</param>
    /// <param name="scopedBytes">Usable bytes reusable at each scoped recycle.</param>
    public NativeArenaPreparation(nuint ordinaryBytes, nuint scopedBytes)
    {
        OrdinaryBytes = ordinaryBytes;
        ScopedBytes = scopedBytes;
    }

    /// <summary>Gets the declared ordinary usable-byte bound.</summary>
    public nuint OrdinaryBytes { get; }

    /// <summary>Gets the declared scoped usable-byte bound.</summary>
    public nuint ScopedBytes { get; }
}

/// <summary>Describes actual prepared arena capacity and lifetime observations.</summary>
/// <remarks>
/// Capture is thread-confined and does not reset history. Used bytes include
/// inter-range alignment. Native extents include headers and backend padding,
/// not CLR object headers, allocator-internal bookkeeping or process RSS.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativePreparedArenaStatistics
{
    internal NativePreparedArenaStatistics(long ownerId, NativeOwnerLifecycle lifecycle,
        NativeArenaPreparation preparation, long ordinaryUsedBytes, long scopedUsedBytes,
        long ordinaryAvailableBytes, long scopedAvailableBytes, long peakOrdinaryUsedBytes,
        long peakScopedUsedBytes, long retainedBytes, long peakRetainedBytes,
        long successfulScratchCount, long rejectedCapacityCount, long initializerFailureCount,
        bool historyOverflowed)
    {
        OwnerId = ownerId;
        Lifecycle = lifecycle;
        Preparation = preparation;
        OrdinaryUsedBytes = ordinaryUsedBytes;
        ScopedUsedBytes = scopedUsedBytes;
        OrdinaryAvailableBytes = ordinaryAvailableBytes;
        ScopedAvailableBytes = scopedAvailableBytes;
        PeakOrdinaryUsedBytes = peakOrdinaryUsedBytes;
        PeakScopedUsedBytes = peakScopedUsedBytes;
        RetainedBytes = retainedBytes;
        PeakRetainedBytes = peakRetainedBytes;
        SuccessfulScratchCount = successfulScratchCount;
        RejectedCapacityCount = rejectedCapacityCount;
        InitializerFailureCount = initializerFailureCount;
        HistoryOverflowed = historyOverflowed;
    }

    /// <summary>Gets the stable process-local allocator identity.</summary>
    public long OwnerId { get; }

    /// <summary>Gets the actual owner lifecycle.</summary>
    public NativeOwnerLifecycle Lifecycle { get; }

    /// <summary>Gets the original declared bounds, unchanged by closure.</summary>
    public NativeArenaPreparation Preparation { get; }

    /// <summary>Gets ordinary occupied bump bytes, including alignment.</summary>
    public long OrdinaryUsedBytes { get; }

    /// <summary>Gets scoped occupied bump bytes, including alignment.</summary>
    public long ScopedUsedBytes { get; }

    /// <summary>Gets remaining ordinary lane bytes; closed owners expose zero authority.</summary>
    public long OrdinaryAvailableBytes { get; }

    /// <summary>Gets remaining scoped lane bytes; closed owners expose zero authority.</summary>
    public long ScopedAvailableBytes { get; }

    /// <summary>Gets the recorded initializing-or-published ordinary bump high-water value.</summary>
    public long PeakOrdinaryUsedBytes { get; }

    /// <summary>Gets the recorded initializing-or-published scoped bump high-water value.</summary>
    public long PeakScopedUsedBytes { get; }

    /// <summary>Gets complete physically retained native extents, including detached storage.</summary>
    public long RetainedBytes { get; }

    /// <summary>Gets the recorded physically retained native high-water extent.</summary>
    public long PeakRetainedBytes { get; }

    /// <summary>Gets fully initialized and published ordinary or scoped scratches.</summary>
    public long SuccessfulScratchCount { get; }

    /// <summary>Gets byte-bound refusals before the initializer or backend ran.</summary>
    public long RejectedCapacityCount { get; }

    /// <summary>Gets throwing or incomplete initializers, whose reservation was rolled back.</summary>
    public long InitializerFailureCount { get; }

    /// <summary>Gets whether a lifetime event count saturated and now represents a lower bound.</summary>
    public bool HistoryOverflowed { get; }
}
