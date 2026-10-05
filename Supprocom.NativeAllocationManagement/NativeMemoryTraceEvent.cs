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
    Detached,
    /// <summary>Unique payload authority was returned; pooled backing may remain reusable and charged.</summary>
    UniqueReturned,
    /// <summary>A destructive unique move published its next non-reusable authority version.</summary>
    Moved,
    /// <summary>A consumed unique source awaits its last entered borrow or cleanup retry.</summary>
    Retired,
    /// <summary>Application producer permission and ownership metadata were prepared before production.</summary>
    ReservationAdmitted,
    /// <summary>Prospective admission failed during metadata preparation without a fabricated native charge.</summary>
    ReservationPreparationFailed,
    /// <summary>Backing is genuinely ready for an uninitialized producer permission.</summary>
    ReservationBackingPrepared,
    /// <summary>Backing preparation failed while producer permission remains charged; a post-acquisition failure can retain prepared backing.</summary>
    ReservationBackingFailed,
    /// <summary>Producer permission moved without another control or charge.</summary>
    ReservationMoved,
    /// <summary>Complete initialization transferred the same admitted control to unique ownership.</summary>
    ReservationActivated,
    /// <summary>Pending or prepared producer permission actually completed terminal resource return.</summary>
    ReservationReturned,
    /// <summary>Explicit cancellation/disposal completed actual pending/prepared resource return.</summary>
    ReservationCancelled,
    /// <summary>A producer failed or did not initialize its complete declared range.</summary>
    ReservationInitializationFailed,
    /// <summary>Emergency cleanup completed an abandoned producer permission.</summary>
    ReservationAbandoned,
    /// <summary>A real pending/prepared resource-return attempt failed without success credit.</summary>
    ReservationReturnFailed,
    /// <summary>Terminal resource return completed after a move exhausted its checked authority version.</summary>
    ReservationAuthorityExhausted,
    /// <summary>Checked typed-layout alignment is ready over actual admitted backing.</summary>
    LayoutPrepared,
    /// <summary>Every typed region and non-payload range finished initialization, before final publication.</summary>
    LayoutInitialized,
    /// <summary>A generation relinquished deterministic cleanup to emergency finalization; its owned backing remains charged.</summary>
    GenerationDetached,
    /// <summary>An ended generation retained backing while an entered operation or owning control remained active.</summary>
    GenerationRetired,
    /// <summary>A failed cleanup removed an ended generation's backing from every reusable bank without releasing its charge.</summary>
    GenerationQuarantined,
    /// <summary>A generation completed native cleanup. Its remaining owned extent can be zero after transfer or prior segment finalization.</summary>
    GenerationReleased
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
    /// <summary>Gets the ownership control, typed-layout identity for layout preparation/initialization, operation-specific detach identity, or actual generation, as defined by the event kind; otherwise unavailable.</summary>
    public long? CorrelationId { get; internal init; }
    /// <summary>Gets the actual allocator generation for generation lifecycle transitions, including generation zero; otherwise unavailable.</summary>
    public long? Generation => Kind is NativeMemoryTraceKind.GenerationDetached
        or NativeMemoryTraceKind.GenerationRetired
        or NativeMemoryTraceKind.GenerationQuarantined
        or NativeMemoryTraceKind.GenerationReleased ? CorrelationId : null;
}
