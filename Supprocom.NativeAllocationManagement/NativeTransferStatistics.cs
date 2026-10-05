using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Describes the actual unique-control transition, not native backend ownership.</summary>
public enum NativeTransferLifecycle
{
    /// <summary>The capability was never constructed.</summary>
    Uninitialized,
    /// <summary>The control accepts its current unique binding and bounded borrows.</summary>
    Active,
    /// <summary>A destructive move has claimed the control.</summary>
    Moving,
    /// <summary>A return attempt has claimed the control.</summary>
    Returning,
    /// <summary>Consumed ownership awaits its last borrow or a cleanup retry.</summary>
    Retiring,
    /// <summary>Payload authority was successfully returned.</summary>
    Returned,
    /// <summary>Emergency cleanup has claimed an abandoned control.</summary>
    Finalizing,
    /// <summary>The allocator revoked payload authority; this control may still require cleanup of its references.</summary>
    Invalidated
}

/// <summary>An allocation-free unique observation; concurrent fields are sampled, not a cross-owner transaction.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeTransferStatistics
{
    /// <summary>Gets stable backing-owner lineage, not a native address.</summary>
    public long OwnerId { get; internal init; }
    /// <summary>Gets the allocation lineage; the pair with OwnerId identifies this acquisition.</summary>
    public long AllocationId { get; internal init; }
    /// <summary>Gets the version carried by the observed capability.</summary>
    public long BindingVersion { get; internal init; }
    /// <summary>Gets the control's non-wrapping current authority version.</summary>
    public long AuthorityVersion { get; internal init; }
    /// <summary>Gets whether this particular capability is the live unique binding.</summary>
    public bool BindingIsActive { get; internal init; }
    /// <summary>Gets the actual unique-control transition state.</summary>
    public NativeTransferLifecycle Lifecycle { get; internal init; }
    /// <summary>Gets actual entered unique borrows; sharing's separate lifetime pin is not a unique borrow.</summary>
    public int ActiveBorrowCount { get; internal init; }
    /// <summary>Gets the recorded simultaneous-borrow high-water count.</summary>
    public int PeakBorrowCount { get; internal init; }
    /// <summary>Gets completed authority publications, exactly derived from the non-wrapping publication version.</summary>
    public long MoveCount { get; internal init; }
    /// <summary>Gets one while the allocator and control accept the published unique binding, otherwise zero; aliases are not additional owners.</summary>
    public int LiveUniqueOwnerCount { get; internal init; }
    /// <summary>Gets whether this managed control's return obligation remains uncompleted, including an invalidated or retiring payload.</summary>
    public bool HasReturnObligation { get; internal init; }
    /// <summary>Gets the initialized logical extent while payload authority remains live.</summary>
    public long InitializedPayloadBytes { get; internal init; }
    /// <summary>Gets the initial initialized logical extent, retained as a lifetime peak after return.</summary>
    public long PeakInitializedPayloadBytes { get; internal init; }
    /// <summary>Gets complete associated NAM-owned backing still retained through this control's uncompleted obligation, not a domain aggregate or free event.</summary>
    public long OwnedBackingBytes { get; internal init; }
    /// <summary>Gets complete associated provider-owned backing still registered through the uncompleted obligation, separate from owned storage.</summary>
    public long BorrowedBackingBytes { get; internal init; }
    /// <summary>Gets the original complete associated NAM-owned extent, including a pooled page shared with other allocations.</summary>
    public long PeakOwnedBackingBytes { get; internal init; }
    /// <summary>Gets one only after a successful payload-authority return, including empty and reusable pooled payloads.</summary>
    public long PayloadReturnCount { get; internal init; }
    /// <summary>Gets failed real storage-return attempts; invalid disposal during a borrow is not an attempted storage return.</summary>
    public long PayloadReturnFailureCount { get; internal init; }
    /// <summary>Gets whether failed-return history saturated; exact authority and admission gauges do not saturate.</summary>
    public bool HistoryOverflowed { get; internal init; }
    /// <summary>Gets the sum of declared control-field representations, excluding CLR headers, inter-field padding and referenced objects.</summary>
    public long ControlFieldBytes { get; internal init; }
}
