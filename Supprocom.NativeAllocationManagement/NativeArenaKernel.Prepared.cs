namespace Supprocom.NativeAllocationManagement;

// Preparation remains outside the ordinary bump-reservation hot path.
public sealed unsafe partial class NativeArena
{
    private readonly bool _prepared;
    private readonly NativeArenaPreparation _preparation;
    private long _preparedSuccessCount;
    private long _preparedRefusalCount;
    private long _preparedInitializerFailureCount;
    private long _preparedPeakOrdinaryUsedBytes;
    private long _preparedPeakScopedUsedBytes;

    /// <summary>Prepares both bounded lanes and prohibits fresh backing during execution.</summary>
    /// <param name="preparation">Exact ordinary and scoped usable-byte bounds.</param>
    /// <param name="budget">The shared ceiling for complete header and aligned backing extents.</param>
    public NativeArena(NativeArenaPreparation preparation, NativeMemoryBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        Id = NativeOwnerIdentity.Next();
        _returnMemoryOnDispose = NativeMemoryReturn.ToNativeMemory;
        _budget = budget;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _lifecycle = NativeOwnerLifecycle.Active;
        _prepared = true;
        _preparation = preparation;
        nuint ordinaryExtent = PreparedExtent(preparation.OrdinaryBytes);
        nuint scopedExtent = PreparedExtent(preparation.ScopedBytes);
        nuint remaining = checked(ordinaryExtent + scopedExtent);
        // Reject signed accounting overflow before admission or backend work.
        _ = checked((long)remaining);
        budget.Reserve(remaining, Id);
        try
        {
            if (ordinaryExtent != 0)
            {
                remaining -= ordinaryExtent;
                AppendSegment(ref _ordinary, preparation.OrdinaryBytes,
                    "ordinary preparation", alreadyReserved: true);
                ResetLane(ref _ordinary);
            }
            if (scopedExtent != 0)
            {
                NativeMemoryTestHooks.NotifyBeforeOperationEntry("NativeArena.PrepareScoped");
                remaining -= scopedExtent;
                AppendSegment(ref _scoped, preparation.ScopedBytes,
                    "scoped preparation", alreadyReserved: true);
                ResetLane(ref _scoped);
            }
            budget.RecordPreparation(checked((nuint)_retainedBytes), Id);
        }
        catch
        {
            FreeAllSegments();
            if (remaining != 0)
            {
                budget.Cancel(remaining, Id);
            }
            throw;
        }
    }

    private static nuint PreparedExtent(nuint capacity) => capacity == 0 ? 0
        : NativeAlignedAllocation.GetBackingByteLength(checked(HeaderBytes + capacity));

    internal NativePreparedArenaStatistics GetPreparedSnapshot()
    {
        ValidateThread(nameof(NativeArena.CapturePreparedSnapshot));
        EnsurePrepared();
        NativePreparedArenaStatistics snapshot = new(Id, _lifecycle, _preparation,
            checked((long)_ordinary.UsedBytes), checked((long)_scoped.UsedBytes),
            _lifecycle == NativeOwnerLifecycle.Active ? LaneAvailable(_ordinary) : 0,
            _lifecycle == NativeOwnerLifecycle.Active ? LaneAvailable(_scoped) : 0,
            _preparedPeakOrdinaryUsedBytes, _preparedPeakScopedUsedBytes,
            _retainedBytes, _peakRetainedBytes, _preparedSuccessCount,
            _preparedRefusalCount, _preparedInitializerFailureCount, _historyOverflowed);
        snapshot = snapshot with
        {
            ActiveBorrowedBytes = _externalActiveBytes,
            RetainedBorrowedBytes = _externalRetainedBytes
        };
        GC.KeepAlive(this);
        return snapshot;
    }

    private static long LaneAvailable(ArenaLane lane) => lane.Cursor == null ? 0
        : checked((long)(lane.End - lane.Cursor));

    internal bool TryScratch<T>(int length, bool scoped, NativeLeaseInitializer<T> initializer,
        out ArenaLease<T> lease) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        EnsurePrepared();
        ValidateActive(scoped ? nameof(NativeArena.TryScratchScoped) : nameof(NativeArena.TryScratch));
        if (_initializerActive != 0)
        {
            ThrowNestedInitializer();
        }

        ref ArenaLane lane = ref (scoped ? ref _scoped : ref _ordinary);
        if (!TryReservePrepared(ref lane, CalculateByteLength<T>(length),
            CalculateAlignment<T>(), scoped, out ArenaReservation reservation))
        {
            IncrementPreparedHistory(ref _preparedRefusalCount);
            lease = default;
            return false;
        }

        if (scoped)
        {
            _preparedPeakScopedUsedBytes = Math.Max(_preparedPeakScopedUsedBytes, checked((long)lane.UsedBytes));
        }
        else
        {
            _preparedPeakOrdinaryUsedBytes = Math.Max(_preparedPeakOrdinaryUsedBytes, checked((long)lane.UsedBytes));
        }

        _initializerActive = 1;
        try
        {
            Initialize(reservation, length, initializer);
        }
        catch
        {
            IncrementPreparedHistory(ref _preparedInitializerFailureCount);
            throw;
        }
        finally
        {
            _initializerActive = 0;
        }
        IncrementPreparedHistory(ref _preparedSuccessCount);
        lease = new ArenaLease<T>(this, reservation.Pointer, length,
            _generation, scoped ? _scopeEpoch : 0, scoped);
        return true;
    }

    private static bool TryReservePrepared(ref ArenaLane lane, nuint byteLength,
        nuint alignment, bool scoped, out ArenaReservation reservation)
    {
        byte* originalCursor = lane.Cursor;
        nuint aligned = originalCursor == null ? 0 : AlignUp((nuint)originalCursor, alignment);
        if (byteLength != 0 && (originalCursor == null || aligned > (nuint)lane.End
            || byteLength > (nuint)lane.End - aligned))
        {
            reservation = default;
            return false;
        }
        nuint originalUsedBytes = lane.UsedBytes;
        reservation = new ArenaReservation(scoped, lane.Current, originalCursor,
            originalUsedBytes, (IntPtr)aligned);
        if (byteLength != 0)
        {
            byte* end = (byte*)checked(aligned + byteLength);
            lane.UsedBytes = checked(originalUsedBytes + (nuint)(end - originalCursor));
            lane.Cursor = end;
        }
        return true;
    }

    private void IncrementPreparedHistory(ref long counter) =>
        NativeOwnerHistory.Increment(ref counter, ref _historyOverflowed);

    private void EnsurePrepared()
    {
        if (!_prepared)
        {
            throw new InvalidOperationException("This operation requires an explicitly prepared NativeArena.");
        }
    }

    private nuint TrimPrepared(nuint byteBudget)
    {
        nuint released = 0;
        if (byteBudget != 0 && _ordinary.UsedBytes == 0 && _ordinary.First != null)
        {
            long previousRetained = _retainedBytes;
            FreeLane(ref _ordinary, NativeMemoryTraceKind.Trimmed);
            released = checked((nuint)(previousRetained - _retainedBytes));
        }
        if (released < byteBudget && _scoped.UsedBytes == 0 && _scoped.First != null)
        {
            long previousRetained = _retainedBytes;
            FreeLane(ref _scoped, NativeMemoryTraceKind.Trimmed);
            released = checked(released + (nuint)(previousRetained - _retainedBytes));
        }
        return released;
    }
}
