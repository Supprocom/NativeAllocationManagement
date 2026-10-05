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
    AcquisitionFailed,
    /// <summary>One prepared backing page was acquired and committed.</summary>
    PageAcquired,
    /// <summary>Backing and metadata preparation completed and its capacity is ready.</summary>
    Prepared,
    /// <summary>A maintenance trim physically released backing.</summary>
    Trimmed,
    /// <summary>An independently owning immutable binding was published.</summary>
    Shared,
    /// <summary>A control-only weak observer was published.</summary>
    WeakCreated,
    /// <summary>A weak observer successfully acquired strong ownership.</summary>
    WeakUpgraded,
    /// <summary>A weak upgrade was refused for expiration or prepared capacity exhaustion.</summary>
    UpgradeRejected,
    /// <summary>One independently acquired strong or weak binding was released.</summary>
    OwnershipReleased,
    /// <summary>Payload authority was returned after the last strong binding and entered reader.</summary>
    PayloadReturned,
    /// <summary>A bounded explicit copy into independent unique ownership completed.</summary>
    Detached
}

/// <summary>A bounded value-only storage transition; it contains no owner or native authority.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeMemoryTraceEvent
{
    internal NativeMemoryTraceEvent(
        long sequence, long timestampTicks, long budgetId, long? ownerId,
        NativeMemoryTraceKind kind, nuint requestedBytes, nuint previousBytes,
        long committedBytes, long reservedBytes, long allocationOrdinal = 0)
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
        AllocationOrdinal = allocationOrdinal == 0 ? null : allocationOrdinal;
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
    /// <summary>Gets the owner-local backing acquisition ordinal when supplied, otherwise unavailable.</summary>
    public long? AllocationOrdinal { get; }
    /// <summary>Gets the ownership control identity for ownership transitions, otherwise unavailable.</summary>
    public long? CorrelationId { get; internal init; }
}
