using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Bounds ordinary geometric growth and native idle retention at explicit arena maintenance boundaries.</summary>
/// <remarks>The idle limit includes NAM-owned headers and padding. It is not a live-byte cap or an RSS limit.</remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeArenaRetentionPolicy
{
    /// <summary>Creates an opt-in policy; requests larger than the ordinary ceiling are exact-sized outliers.</summary>
    /// <param name="ordinarySegmentCeilingBytes">Maximum preferred ordinary usable segment capacity, at least 64 bytes.</param>
    /// <param name="idleRetentionBytes">Complete native extent permitted to remain idle after maintenance.</param>
    public NativeArenaRetentionPolicy(nuint ordinarySegmentCeilingBytes, nuint idleRetentionBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinarySegmentCeilingBytes, (nuint)64);
        _ = checked((long)ordinarySegmentCeilingBytes);
        _ = checked((long)idleRetentionBytes);
        OrdinarySegmentCeilingBytes = ordinarySegmentCeilingBytes;
        IdleRetentionBytes = idleRetentionBytes;
    }

    /// <summary>Gets the preferred ordinary usable-capacity ceiling; it does not cap individual requests.</summary>
    public nuint OrdinarySegmentCeilingBytes { get; }

    /// <summary>Gets the complete idle native-extent ceiling, including headers and alignment.</summary>
    public nuint IdleRetentionBytes { get; }
}

/// <summary>Reports actual thread-confined arena retention state without resetting history or allocating.</summary>
/// <remarks>Idle storage is an unused whole segment. Occupied bump segments cannot be evicted; closed owners expose no reusable idle authority.</remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeArenaRetentionStatistics
{
    internal NativeArenaRetentionStatistics(long ownerId, NativeOwnerLifecycle lifecycle,
        bool enabled, NativeArenaRetentionPolicy policy, long retainedBytes,
        long idleBytes, long oversizedBytes, long peakOversizedBytes,
        long maintenanceCount, long releasedBytes, bool historyOverflowed)
    {
        OwnerId = ownerId;
        Lifecycle = lifecycle;
        Enabled = enabled;
        Policy = policy;
        RetainedBytes = retainedBytes;
        IdleBytes = idleBytes;
        OversizedBytes = oversizedBytes;
        PeakOversizedBytes = peakOversizedBytes;
        MaintenanceCount = maintenanceCount;
        ReleasedBytes = releasedBytes;
        HistoryOverflowed = historyOverflowed;
    }

    /// <summary>Gets stable owner lineage.</summary>
    public long OwnerId { get; }
    /// <summary>Gets actual owner lifecycle.</summary>
    public NativeOwnerLifecycle Lifecycle { get; }
    /// <summary>Gets whether this owner opted into policy-controlled growth and maintenance.</summary>
    public bool Enabled { get; }
    /// <summary>Gets the immutable configured policy; disabled owners have the default policy.</summary>
    public NativeArenaRetentionPolicy Policy { get; }
    /// <summary>Gets all currently retained NAM-owned native extents, including detached storage.</summary>
    public long RetainedBytes { get; }
    /// <summary>Gets complete currently reusable whole native extents; partial bump slack is not idle.</summary>
    public long IdleBytes { get; }
    /// <summary>Gets retained complete extents whose usable capacity exceeds the configured ordinary ceiling.</summary>
    public long OversizedBytes { get; }
    /// <summary>Gets the recorded high-water oversized native extent.</summary>
    public long PeakOversizedBytes { get; }
    /// <summary>Gets reset, scoped recycle and explicit policy-maintenance invocations on enabled owners.</summary>
    public long MaintenanceCount { get; }
    /// <summary>Gets complete native bytes physically freed by policy maintenance, not by final disposal or unrelated trim.</summary>
    public long ReleasedBytes { get; }
    /// <summary>Gets the owner history-saturation observation; exact current gauges do not saturate.</summary>
    public bool HistoryOverflowed { get; }
}
