using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Declares independently reusable, fixed-shape slots and their page grouping.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativePoolPreparation
{
    /// <summary>Creates checked positive slot and page bounds.</summary>
    /// <param name="slotCount">The maximum simultaneously occupied slots.</param>
    /// <param name="slotCapacity">The exact capacity of each slot in elements.</param>
    /// <param name="slotsPerPage">The maximum slots sharing one backing allocation.</param>
    public NativePoolPreparation(int slotCount, int slotCapacity, int slotsPerPage)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotsPerPage);
        SlotCount = slotCount;
        SlotCapacity = slotCapacity;
        SlotsPerPage = slotsPerPage;
    }

    /// <summary>Gets the declared simultaneous slot bound.</summary>
    public int SlotCount { get; }
    /// <summary>Gets the exact element capacity of each slot.</summary>
    public int SlotCapacity { get; }
    /// <summary>Gets the maximum slots per allocation, with a shorter final page.</summary>
    public int SlotsPerPage { get; }
}

/// <summary>Distinguishes expected prepared capacity exhaustion from invalid use or backend failure.</summary>
public enum NativePoolExhaustionReason
{
    /// <summary>No capacity refusal occurred.</summary>
    None,
    /// <summary>The requested element count exceeds the fixed slot shape.</summary>
    ShapeExceeded,
    /// <summary>No retained slot is currently free.</summary>
    NoAvailableSlot
}

/// <summary>Describes actual page capacity and lifetime observations of a prepared pool.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativePreparedPoolStatistics
{
    internal NativePreparedPoolStatistics(long ownerId, NativeOwnerLifecycle lifecycle,
        NativePoolPreparation preparation, int retainedPages, int retainedSlots,
        int occupiedSlots, int peakOccupiedSlots, long retainedBytes, long peakRetainedBytes,
        long successfulRentCount, long rejectedShapeCount, long rejectedFullCount,
        long initializerFailureCount, long managedBankBytes, long unusedSlotBytes, bool historyOverflowed)
    {
        OwnerId = ownerId;
        Lifecycle = lifecycle;
        Preparation = preparation;
        RetainedPageCount = retainedPages;
        RetainedSlotCount = retainedSlots;
        OccupiedSlotCount = occupiedSlots;
        PeakOccupiedSlotCount = peakOccupiedSlots;
        RetainedBytes = retainedBytes;
        PeakRetainedBytes = peakRetainedBytes;
        SuccessfulRentCount = successfulRentCount;
        RejectedShapeCount = rejectedShapeCount;
        RejectedFullCount = rejectedFullCount;
        InitializerFailureCount = initializerFailureCount;
        ManagedBankBytes = managedBankBytes;
        UnusedSlotBytes = unusedSlotBytes;
        HistoryOverflowed = historyOverflowed;
    }

    /// <summary>Gets the stable allocator identity.</summary>
    public long OwnerId { get; }
    /// <summary>Gets actual lifecycle state.</summary>
    public NativeOwnerLifecycle Lifecycle { get; }
    /// <summary>Gets the original declared bounds, unchanged by trim.</summary>
    public NativePoolPreparation Preparation { get; }
    /// <summary>Gets physically retained native pages.</summary>
    public int RetainedPageCount { get; }
    /// <summary>Gets slots whose pages are still retained.</summary>
    public int RetainedSlotCount { get; }
    /// <summary>Gets initializing or published occupied slots.</summary>
    public int OccupiedSlotCount { get; }
    /// <summary>Gets the recorded initializing-or-published occupancy high-water value.</summary>
    public int PeakOccupiedSlotCount { get; }
    /// <summary>Gets reusable slots, excluding trimmed pages.</summary>
    public int AvailableSlotCount => RetainedSlotCount - OccupiedSlotCount;
    /// <summary>Gets complete known page allocation extents.</summary>
    public long RetainedBytes { get; }
    /// <summary>Gets the recorded complete owned extent high-water value.</summary>
    public long PeakRetainedBytes { get; }
    /// <summary>Gets successfully initialized and published rents.</summary>
    public long SuccessfulRentCount { get; }
    /// <summary>Gets requests refused for an oversized shape before initialization.</summary>
    public long RejectedShapeCount { get; }
    /// <summary>Gets requests refused for lack of a free retained slot.</summary>
    public long RejectedFullCount { get; }
    /// <summary>Gets initializers that threw or failed to fill the required range.</summary>
    public long InitializerFailureCount { get; }
    /// <summary>Gets allocated array element storage for slot, page and free-head banks, excluding CLR headers.</summary>
    public long ManagedBankBytes { get; }
    /// <summary>Gets usable element bytes in currently reusable slots, excluding page padding.</summary>
    public long UnusedSlotBytes { get; }
    /// <summary>Gets whether any lifetime event counter saturated; saturated values are lower bounds.</summary>
    public bool HistoryOverflowed { get; }
}
