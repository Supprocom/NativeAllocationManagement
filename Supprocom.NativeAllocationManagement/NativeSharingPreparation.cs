using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Declares all simultaneously live strong and weak binding slots for one immutable payload.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeSharingPreparation
{
    /// <summary>Creates fixed metadata bounds; the initial owning binding consumes one strong slot.</summary>
    /// <param name="strongBindingCount">Maximum independently acquired strong bindings, at least one.</param>
    /// <param name="weakBindingCount">Maximum independently acquired weak observers, possibly zero.</param>
    public NativeSharingPreparation(int strongBindingCount, int weakBindingCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(strongBindingCount);
        ArgumentOutOfRangeException.ThrowIfNegative(weakBindingCount);
        StrongBindingCount = strongBindingCount;
        WeakBindingCount = weakBindingCount;
    }

    /// <summary>Gets the fixed strong binding capacity.</summary>
    public int StrongBindingCount { get; }
    /// <summary>Gets the fixed weak observer capacity.</summary>
    public int WeakBindingCount { get; }
}

/// <summary>Distinguishes expected sharing capacity or expiration refusal from invalid ownership.</summary>
public enum NativeSharingExhaustionReason
{
    /// <summary>No refusal occurred.</summary>
    None,
    /// <summary>No prepared strong binding slot is free.</summary>
    NoStrongBinding,
    /// <summary>No prepared weak observer slot is free.</summary>
    NoWeakBinding,
    /// <summary>The payload has no surviving strong ownership and cannot be resurrected.</summary>
    ExpiredPayload
}

/// <summary>One consistent immutable-sharing observation, containing no native authority or payload reference.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeSharingStatistics
{
    internal NativeSharingStatistics(long id, long ownerId, NativeSharingPreparation preparation,
        int strongBindings, int weakBindings, int activeReads, int peakStrongBindings,
        int peakWeakBindings, bool expired, bool released, long ownedBytes, long borrowedBytes,
        long initializedBytes, long bankBytes, long shareCount, long weakCount,
        long upgradeCount, long rejectedStrongCount, long rejectedWeakCount,
        long expiredUpgradeCount, long payloadReturnCount, long detachCount,
        bool historyOverflowed)
    {
        Id = id; OwnerId = ownerId; Preparation = preparation;
        StrongBindingCount = strongBindings; WeakBindingCount = weakBindings; ActiveReadCount = activeReads;
        PeakStrongBindingCount = peakStrongBindings; PeakWeakBindingCount = peakWeakBindings;
        Expired = expired; PayloadReleased = released; OwnedBackingBytes = ownedBytes;
        BorrowedBackingBytes = borrowedBytes; InitializedPayloadBytes = initializedBytes;
        ManagedBankBytes = bankBytes; ShareCount = shareCount; WeakCreationCount = weakCount;
        SuccessfulUpgradeCount = upgradeCount; RejectedStrongCount = rejectedStrongCount;
        RejectedWeakCount = rejectedWeakCount; ExpiredUpgradeCount = expiredUpgradeCount;
        PayloadReturnCount = payloadReturnCount; DetachCount = detachCount; HistoryOverflowed = historyOverflowed;
    }

    /// <summary>Gets the non-reusable shared-payload identity, not an address.</summary>
    public long Id { get; }
    /// <summary>Gets the original backing-owner lineage.</summary>
    public long OwnerId { get; }
    /// <summary>Gets immutable prepared binding capacities.</summary>
    public NativeSharingPreparation Preparation { get; }
    /// <summary>Gets independently acquired live strong bindings, excluding ordinary aliases.</summary>
    public int StrongBindingCount { get; }
    /// <summary>Gets independently acquired live weak observers, excluding ordinary aliases.</summary>
    public int WeakBindingCount { get; }
    /// <summary>Gets actually entered bounded readers, including readers finishing after last strong release.</summary>
    public int ActiveReadCount { get; }
    /// <summary>Gets the recorded independently acquired strong-binding high-water count.</summary>
    public int PeakStrongBindingCount { get; }
    /// <summary>Gets the recorded weak observer high-water count.</summary>
    public int PeakWeakBindingCount { get; }
    /// <summary>Gets whether strong acquisition is permanently closed.</summary>
    public bool Expired { get; }
    /// <summary>Gets whether deterministic or emergency payload return has completed.</summary>
    public bool PayloadReleased { get; }
    /// <summary>Gets the full pinned NAM-owned backing extent, counted once; zero after payload return.</summary>
    public long OwnedBackingBytes { get; }
    /// <summary>Gets the full pinned provider-owned extent, counted separately; zero after payload return.</summary>
    public long BorrowedBackingBytes { get; }
    /// <summary>Gets initialized logical payload bytes counted once, not once per overlapping binding or slice.</summary>
    public long InitializedPayloadBytes { get; }
    /// <summary>Gets the original immutable initialized extent, counted once and retained after payload return; slices and aliases never increase it.</summary>
    public long PeakInitializedPayloadBytes { get; internal init; }
    /// <summary>Gets currently retained binding-array element storage, excluding CLR headers, unreachable arrays awaiting collection, and allocator bookkeeping.</summary>
    public long ManagedBankBytes { get; }
    /// <summary>Gets successful explicit strong share or slice acquisitions, excluding the initial owner and weak upgrades.</summary>
    public long ShareCount { get; }
    /// <summary>Gets successful explicit weak observer acquisitions.</summary>
    public long WeakCreationCount { get; }
    /// <summary>Gets successfully published weak-to-strong upgrades.</summary>
    public long SuccessfulUpgradeCount { get; }
    /// <summary>Gets valid ownership operations refused before publication for lack of a strong slot.</summary>
    public long RejectedStrongCount { get; }
    /// <summary>Gets valid observer acquisitions refused for lack of a weak slot.</summary>
    public long RejectedWeakCount { get; }
    /// <summary>Gets valid weak upgrade attempts rejected after payload expiration.</summary>
    public long ExpiredUpgradeCount { get; }
    /// <summary>Gets completed payload-authority returns, including reusable pooled slots and zero-length payloads; not physical backend frees.</summary>
    public long PayloadReturnCount { get; }
    /// <summary>Gets successful explicit slice copies into independent unique ownership.</summary>
    public long DetachCount { get; }
    /// <summary>Gets explicit detach requests refused before acquisition or copying by the destination native budget.</summary>
    public long RejectedDetachCount { get; internal init; }
    /// <summary>Gets failed payload cleanup attempts; failed attempts do not receive successful-return credit.</summary>
    public long PayloadReturnFailureCount { get; internal init; }
    /// <summary>Gets whether lifetime event histories saturated; current gauges remain exact.</summary>
    public bool HistoryOverflowed { get; }
}
