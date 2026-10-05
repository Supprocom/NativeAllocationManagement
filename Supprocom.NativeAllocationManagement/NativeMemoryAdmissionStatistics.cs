using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Identifies expected pre-producer quota exhaustion, not invalid ownership or acquisition failure.</summary>
public enum NativeMemoryAdmissionExhaustionReason
{
    /// <summary>The declared range was admitted.</summary>
    None,
    /// <summary>The complete declared native extent did not fit.</summary>
    NativeByteCapacity
}

/// <summary>Describes the actual disposition of one explicit application admission.</summary>
public enum NativeMemoryReservationOutcome
{
    /// <summary>No application reservation was constructed.</summary>
    None,
    /// <summary>Bytes and ownership metadata are reserved; no backing has been prepared.</summary>
    Pending,
    /// <summary>The admitted backing is ready, without initialized payload publication.</summary>
    Prepared,
    /// <summary>Complete initialization transferred the same control to unique ownership.</summary>
    Activated,
    /// <summary>Explicit cancellation or disposal ended producer permission.</summary>
    Cancelled,
    /// <summary>A failed or incomplete initializer consumed producer permission.</summary>
    InitializationFailed,
    /// <summary>Emergency finalization ended abandoned producer permission.</summary>
    Abandoned,
    /// <summary>Backing preparation failed before the producer was entered and consumed activation permission.</summary>
    PreparationFailed,
    /// <summary>A non-reusable version exhausted during a destructive move.</summary>
    AuthorityExhausted
}

/// <summary>A consistent admission-domain observation under its existing gate; current gauges are exact and histories saturate.</summary>
/// <remarks>Counts cover explicit application reservations only. Field bytes exclude CLR headers, padding and referenced objects.</remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeMemoryAdmissionStatistics
{
    /// <summary>Gets the stable native extent domain identity.</summary>
    public long BudgetId { get; internal init; }
    /// <summary>Gets its immutable complete native extent ceiling.</summary>
    public long CapacityBytes { get; internal init; }
    /// <summary>Gets outstanding producer/terminal-cleanup obligations, including zero-length reservations.</summary>
    public long OutstandingReservationCount { get; internal init; }
    /// <summary>Gets the maximum outstanding obligation count.</summary>
    public long PeakOutstandingReservationCount { get; internal init; }
    /// <summary>Gets admitted bytes not yet committed to prepared backing.</summary>
    public long PendingBytes { get; internal init; }
    /// <summary>Gets the maximum pending extent.</summary>
    public long PeakPendingBytes { get; internal init; }
    /// <summary>Gets actual prepared backing whose reservation has not activated or completed cleanup.</summary>
    public long PreparedUnpublishedBytes { get; internal init; }
    /// <summary>Gets the maximum unpublished prepared extent.</summary>
    public long PeakPreparedUnpublishedBytes { get; internal init; }
    /// <summary>Gets successfully published producer permissions.</summary>
    public long AdmittedReservationCount { get; internal init; }
    /// <summary>Gets requests rejected before control preparation and payload work.</summary>
    public long RejectedReservationCount { get; internal init; }
    /// <summary>Gets failed ownership/ledger/identity preparations, without an invented native reservation.</summary>
    public long ControlPreparationFailureCount { get; internal init; }
    /// <summary>Gets completed backing preparations, including genuine zero-length no-growth readiness.</summary>
    public long BackingPreparationCount { get; internal init; }
    /// <summary>Gets failed backing preparation attempts; permission remains available for retry or cancellation.</summary>
    public long BackingPreparationFailureCount { get; internal init; }
    /// <summary>Gets native allocation-failure outcomes within failed backing preparations.</summary>
    public long BackendAllocationFailureCount { get; internal init; }
    /// <summary>Gets fully initialized unique publications.</summary>
    public long ActivationCount { get; internal init; }
    /// <summary>Gets explicit cancelled/disposed reservations whose pending or prepared resource was returned.</summary>
    public long CancelledReservationCount { get; internal init; }
    /// <summary>Gets initializer failures, recorded once before terminal cleanup.</summary>
    public long InitializationFailureCount { get; internal init; }
    /// <summary>Gets abandoned reservations whose actual emergency cleanup completed.</summary>
    public long AbandonedReservationCount { get; internal init; }
    /// <summary>Gets actual failed reservation resource-return attempts.</summary>
    public long ReturnFailureCount { get; internal init; }
    /// <summary>Gets whether a domain history saturated; current gauges remain exact.</summary>
    public bool HistoryOverflowed { get; internal init; }
    /// <summary>Gets retained optional-ledger field representations, zero if no ledger was retained.</summary>
    public long LedgerFieldBytes { get; internal init; }
    /// <summary>Gets admission-specific field representations carried by the domain, excluding other budget fields.</summary>
    public long BudgetAdmissionFieldBytes { get; internal init; }
}

/// <summary>A sampled non-owning reservation/control observation; it does not guarantee later authority or allocation.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeMemoryReservationStatistics
{
    /// <summary>Gets the original admission domain, retained after resource return.</summary>
    public long BudgetId { get; internal init; }
    /// <summary>Gets the control's stable backing-owner lineage.</summary>
    public long OwnerId { get; internal init; }
    /// <summary>Gets this value binding's non-reusable version.</summary>
    public long BindingVersion { get; internal init; }
    /// <summary>Gets the sampled current control version.</summary>
    public long AuthorityVersion { get; internal init; }
    /// <summary>Gets whether this binding currently owns producer permission, not initialized payload access.</summary>
    public bool BindingIsActive { get; internal init; }
    /// <summary>Gets the actual permission disposition; a failed return can retain a terminal cleanup obligation.</summary>
    public NativeMemoryReservationOutcome Outcome { get; internal init; }
    /// <summary>Gets the exact declared initialized element count.</summary>
    public int DeclaredLength { get; internal init; }
    /// <summary>Gets its complete direct native byte requirement.</summary>
    public long RequestedBytes { get; internal init; }
    /// <summary>Gets pending admitted bytes, distinct from owned backing.</summary>
    public long ReservedBytes { get; internal init; }
    /// <summary>Gets actual associated backing still held by the same control, including after activation.</summary>
    public long OwnedBackingBytes { get; internal init; }
    /// <summary>Gets the largest actual backing extent acquired by this control.</summary>
    public long PeakOwnedBackingBytes { get; internal init; }
    /// <summary>Gets whether backing preparation completed, including a zero-length range.</summary>
    public bool BackingIsPrepared { get; internal init; }
    /// <summary>Gets successful reservation moves only; unique movement has its own history.</summary>
    public long ReservationMoveCount { get; internal init; }
    /// <summary>Gets failed preparations attempted by this control.</summary>
    public long BackingPreparationFailureCount { get; internal init; }
    /// <summary>Gets whether pending/prepared reservation cleanup remains, not whether initialized unique authority exists.</summary>
    public bool HasReservationReturnObligation { get; internal init; }
    /// <summary>Gets failed payload/control resource returns, including later unique cleanup.</summary>
    public long ReturnFailureCount { get; internal init; }
    /// <summary>Gets whether a control history saturated.</summary>
    public bool HistoryOverflowed { get; internal init; }
    /// <summary>Gets all base and specialized control field representations, not its CLR heap size.</summary>
    public long ControlFieldBytes { get; internal init; }
}
