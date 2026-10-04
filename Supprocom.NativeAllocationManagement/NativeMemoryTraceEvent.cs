using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Identifies an actual native-admission or physical-storage transition.</summary>
public enum NativeMemoryTraceKind
{
    /// <summary>A complete extent was reserved before acquisition.</summary>
    Admitted,
    /// <summary>Fresh backing was acquired and committed.</summary>
    Allocated,
    /// <summary>Realloc replaced backing, including realloc from null.</summary>
    Reallocated,
    /// <summary>Backing was physically released.</summary>
    Released,
    /// <summary>The minimum extent was refused before acquisition.</summary>
    Rejected,
    /// <summary>An admitted acquisition failed and its reservation was cancelled.</summary>
    AcquisitionFailed
}

/// <summary>A bounded value-only storage transition; it contains no owner or native authority.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeMemoryTraceEvent
{
    internal NativeMemoryTraceEvent(
        long sequence, long timestampTicks, long budgetId, long? ownerId,
        NativeMemoryTraceKind kind, nuint requestedBytes, nuint previousBytes,
        long committedBytes, long reservedBytes)
    {
        Sequence = sequence;
        TimestampTicks = timestampTicks;
        BudgetId = budgetId;
        OwnerId = ownerId;
        Kind = kind;
        RequestedBytes = requestedBytes;
        PreviousBytes = previousBytes;
        CommittedBytes = committedBytes;
        ReservedBytes = reservedBytes;
    }

    /// <summary>Gets the lifetime event sequence within the domain.</summary>
    public long Sequence { get; }
    /// <summary>Gets the monotonic Stopwatch timestamp, not a UTC time.</summary>
    public long TimestampTicks { get; }
    /// <summary>Gets the admission domain identity.</summary>
    public long BudgetId { get; }
    /// <summary>Gets the backing-owner lineage, or null for ownerless internal domain operations.</summary>
    public long? OwnerId { get; }
    /// <summary>Gets the actual transition kind.</summary>
    public NativeMemoryTraceKind Kind { get; }
    /// <summary>Gets the complete known extent associated with the transition.</summary>
    public nuint RequestedBytes { get; }
    /// <summary>Gets the previous complete extent for realloc, otherwise zero.</summary>
    public nuint PreviousBytes { get; }
    /// <summary>Gets committed bytes immediately after this transition.</summary>
    public long CommittedBytes { get; }
    /// <summary>Gets reserved bytes immediately after this transition.</summary>
    public long ReservedBytes { get; }
}
