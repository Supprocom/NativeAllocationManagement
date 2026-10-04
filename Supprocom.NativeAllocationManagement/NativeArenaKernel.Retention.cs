namespace Supprocom.NativeAllocationManagement;

// Policy selection stays on cold growth and explicit maintenance, not warm bump access.
internal sealed unsafe partial class NativeArenaKernel
{
    private readonly bool _retentionEnabled;
    private readonly NativeArenaRetentionPolicy _retentionPolicy;
    private long _oversizedRetainedBytes;
    private long _peakOversizedRetainedBytes;
    private long _retentionMaintenanceCount;
    private long _retentionReleasedBytes;

    internal NativeArenaKernel(NativeMemoryBudget budget, NativeArenaRetentionPolicy policy,
        nuint preAllocateBytes, NativeMemoryReturn returnMemoryOnDispose)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.OrdinarySegmentCeilingBytes, SegmentAlignment, nameof(policy));
        _returnMemoryOnDispose = returnMemoryOnDispose;
        _budget = budget;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _lifecycle = NativeOwnerLifecycle.Active;
        _retentionEnabled = true;
        _retentionPolicy = policy;
        if (preAllocateBytes != 0)
        {
            AppendSegment(ref _ordinary, preAllocateBytes, "exact declaration reservation");
            ResetLane(ref _ordinary);
        }
    }

    private nuint SelectPolicyGrowthCapacity(nuint requiredCapacity, nuint previousCapacity)
    {
        nuint ceiling = _retentionPolicy.OrdinarySegmentCeilingBytes;
        if (requiredCapacity > ceiling)
        {
            return requiredCapacity;
        }
        nuint preferred = previousCapacity > ceiling || previousCapacity == 0
            ? Math.Min(DefaultSegmentBytes, ceiling)
            : previousCapacity > ceiling / 2 ? ceiling : previousCapacity * 2;
        return Math.Max(requiredCapacity, Math.Min(ceiling, Math.Max(Math.Min(DefaultSegmentBytes, ceiling), preferred)));
    }

    private void RecordPolicyAcquisition(ArenaSegmentHeader* segment)
    {
        if (_retentionEnabled && segment->Capacity > _retentionPolicy.OrdinarySegmentCeilingBytes)
        {
            _oversizedRetainedBytes += checked((long)segment->AllocationBytes);
            _peakOversizedRetainedBytes = Math.Max(_peakOversizedRetainedBytes, _oversizedRetainedBytes);
        }
    }

    private void RecordPolicyRelease(ArenaSegmentHeader* segment)
    {
        if (_retentionEnabled && segment->Capacity > _retentionPolicy.OrdinarySegmentCeilingBytes)
        {
            _oversizedRetainedBytes -= checked((long)segment->AllocationBytes);
        }
    }

    internal NativeArenaRetentionStatistics GetRetentionSnapshot()
    {
        ValidateThread(nameof(NativeArena.CaptureRetentionSnapshot));
        long idleBytes = _lifecycle == NativeOwnerLifecycle.Active
            ? checked(CountIdleNativeBytes(_ordinary) + CountIdleNativeBytes(_scoped)) : 0;
        NativeArenaRetentionStatistics snapshot = new(Id, _lifecycle, _retentionEnabled,
            _retentionPolicy, _retainedBytes, idleBytes, _oversizedRetainedBytes,
            _peakOversizedRetainedBytes, _retentionMaintenanceCount,
            _retentionReleasedBytes, _historyOverflowed);
        GC.KeepAlive(this);
        return snapshot;
    }

    internal nuint MaintainRetention()
    {
        ValidateBoundary(nameof(NativeArena.MaintainRetention));
        if (!_retentionEnabled)
        {
            throw new InvalidOperationException("This arena has no explicit retention policy.");
        }
        return ApplyRetentionPolicy();
    }

    private nuint ApplyRetentionPolicy()
    {
        NativeOwnerHistory.Increment(ref _retentionMaintenanceCount, ref _historyOverflowed);
        NativeOwnerHistory.Increment(ref _trimCallCount, ref _historyOverflowed);
        long idleBytes = checked(CountIdleNativeBytes(_ordinary) + CountIdleNativeBytes(_scoped));
        long before = idleBytes;
        // Outliers must leave before normal blocks are considered for ceiling eviction.
        long availableRetention = checked((long)_retentionPolicy.IdleRetentionBytes);
        TrimPolicyLane(ref _ordinary, oversizedOnly: true, ref idleBytes, ref availableRetention);
        TrimPolicyLane(ref _scoped, oversizedOnly: true, ref idleBytes, ref availableRetention);
        TrimPolicyLane(ref _ordinary, oversizedOnly: false, ref idleBytes, ref availableRetention);
        TrimPolicyLane(ref _scoped, oversizedOnly: false, ref idleBytes, ref availableRetention);
        long released = before - idleBytes;
        NativeOwnerHistory.Add(ref _trimmedBytes, released, ref _historyOverflowed);
        NativeOwnerHistory.Add(ref _retentionReleasedBytes, released, ref _historyOverflowed);
        return checked((nuint)released);
    }

    private static ArenaSegmentHeader* FirstIdleSegment(ArenaLane lane) =>
        lane.Current == null ? lane.First
        : lane.Cursor == GetDataStart(lane.Current) ? lane.Current : lane.Current->Next;

    private static long CountIdleNativeBytes(ArenaLane lane)
    {
        long bytes = 0;
        for (ArenaSegmentHeader* segment = FirstIdleSegment(lane); segment != null; segment = segment->Next)
        {
            if (segment->External == 0)
            {
                bytes = checked(bytes + (long)segment->AllocationBytes);
            }
        }
        return bytes;
    }

    private void TrimPolicyLane(ref ArenaLane lane, bool oversizedOnly, ref long idleBytes, ref long availableRetention)
    {
        ArenaSegmentHeader* firstIdle = FirstIdleSegment(lane);
        if (firstIdle == null)
        {
            return;
        }
        ArenaSegmentHeader* previous = null;
        for (ArenaSegmentHeader* candidate = lane.First; candidate != firstIdle; candidate = candidate->Next)
        {
            previous = candidate;
        }
        bool emptiedCurrent = false;
        for (ArenaSegmentHeader* segment = firstIdle; segment != null;)
        {
            ArenaSegmentHeader* next = segment->Next;
            bool oversized = segment->Capacity > _retentionPolicy.OrdinarySegmentCeilingBytes;
            long bytes = checked((long)segment->AllocationBytes);
            // Preserve the fitting ordinary prefix, sharing the allowance across both lanes.
            bool release = segment->External == 0 && (oversizedOnly ? oversized : bytes > availableRetention);
            if (release)
            {
                if (previous == null) lane.First = next;
                else previous->Next = next;
                if (lane.Tail == segment) lane.Tail = previous;
                emptiedCurrent |= lane.Current == segment;
                idleBytes -= checked((long)segment->AllocationBytes);
                FreeSegment(segment, NativeMemoryTraceKind.Trimmed);
            }
            else
            {
                if (!oversizedOnly && segment->External == 0)
                {
                    availableRetention -= bytes;
                }
                previous = segment;
            }
            segment = next;
        }
        if (emptiedCurrent)
        {
            // Current could be removed only when it and every following unit were idle.
            ResetLane(ref lane);
        }
    }
}
