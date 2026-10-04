using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Bounds the complete native backing extents acquired by participating owners.</summary>
/// <remarks>
/// Admission includes retained, retired and detached storage until physical release.
/// It is not a process-RSS limit. Reusing charged storage requires no budget operation.
/// </remarks>
public sealed class NativeMemoryBudget
{
    private static long _nextId;
    private readonly Lock _gate = new();
    private readonly NativeMemoryTraceEvent[] _trace;
    private int _traceWriteIndex;
    private int _traceCount;
    private long _droppedTraceEventCount;
    private long _traceSequence;
    private bool _traceOverflowed;
    private long _committedBytes;
    private long _reservedBytes;
    private long _peakCommittedBytes;
    private long _peakAdmittedBytes;
    private long _allocationCount;
    private long _reallocationCount;
    private long _freeCount;
    private long _rejectedAllocationCount;
    private long _failedAllocationCount;
    private long _activeAllocationCount;
    private bool _historyOverflowed;

    /// <summary>Creates a shared domain with an immutable native extent limit.</summary>
    /// <param name="capacityBytes">The maximum admitted NAM-requested extent in bytes.</param>
    public NativeMemoryBudget(long capacityBytes)
        : this(capacityBytes, traceCapacity: 0)
    {
    }

    /// <summary>Creates a domain with an optional preallocated, bounded diagnostic ring.</summary>
    /// <param name="capacityBytes">The immutable native extent ceiling.</param>
    /// <param name="traceCapacity">The maximum retained events; zero disables event construction.</param>
    public NativeMemoryBudget(long capacityBytes, int traceCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(traceCapacity);
        _trace = traceCapacity == 0 ? [] : new NativeMemoryTraceEvent[traceCapacity];
        CapacityBytes = capacityBytes;
        Id = NextId();
    }

    /// <summary>Gets the stable process-local domain identity, not a memory address.</summary>
    public long Id { get; }

    /// <summary>Gets the immutable admission ceiling in bytes.</summary>
    public long CapacityBytes { get; }

    /// <summary>Captures one consistent domain snapshot without resetting history.</summary>
    public NativeMemoryBudgetStatistics CaptureStatistics()
    {
        lock (_gate)
        {
            return new(
                Id, CapacityBytes, _committedBytes, _reservedBytes,
                _peakCommittedBytes, _peakAdmittedBytes, _allocationCount,
                _reallocationCount, _freeCount, _activeAllocationCount,
                _rejectedAllocationCount, _failedAllocationCount,
                _trace.Length, _traceCount, _droppedTraceEventCount, _traceOverflowed,
                _historyOverflowed);
        }
    }

    /// <summary>Copies the newest retained events in chronological order without allocating.</summary>
    /// <remarks>A short destination receives the newest suffix. Copying does not reset or remove events.</remarks>
    /// <param name="destination">Caller-provided event storage.</param>
    /// <returns>The number of events copied.</returns>
    public int CopyTraceTo(scoped Span<NativeMemoryTraceEvent> destination)
    {
        lock (_gate)
        {
            int count = Math.Min(destination.Length, _traceCount);
            int index = _traceWriteIndex - count;
            if (index < 0)
            {
                index += _trace.Length;
            }
            if (count != 0)
            {
                int firstCount = Math.Min(count, _trace.Length - index);
                _trace.AsSpan(index, firstCount).CopyTo(destination);
                if (firstCount != count)
                {
                    _trace.AsSpan(0, count - firstCount).CopyTo(destination[firstCount..]);
                }
            }
            return count;
        }
    }

    internal void Reserve(nuint byteLength, long ownerId = 0)
    {
        if (!TryReserve(byteLength, out long availableBytes, ownerId))
        {
            throw new NativeMemoryBudgetExceededException(
                Id, CapacityBytes, byteLength, availableBytes);
        }
    }

    internal bool TryReserve(nuint byteLength, out long availableBytes, long ownerId = 0) =>
        TryReservePreferred(byteLength, byteLength, out _, out availableBytes, ownerId);

    internal bool TryReservePreferred(
        nuint preferredByteLength,
        nuint minimumByteLength,
        out nuint admittedByteLength,
        out long availableBytes,
        long ownerId = 0)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumByteLength, preferredByteLength);

        long preferredBytes = checked((long)preferredByteLength);
        long minimumBytes = checked((long)minimumByteLength);
        lock (_gate)
        {
            availableBytes = CapacityBytes - _committedBytes - _reservedBytes;
            long bytes = preferredBytes <= availableBytes ? preferredBytes : minimumBytes;
            if (bytes > availableBytes)
            {
                IncrementHistory(ref _rejectedAllocationCount);
                RecordTrace(NativeMemoryTraceKind.Rejected, ownerId, minimumByteLength);
                admittedByteLength = 0;
                return false;
            }

            _reservedBytes += bytes;
            _peakAdmittedBytes = Math.Max(
                _peakAdmittedBytes, _committedBytes + _reservedBytes);
            admittedByteLength = checked((nuint)bytes);
            if (bytes != 0)
            {
                RecordTrace(NativeMemoryTraceKind.Admitted, ownerId, admittedByteLength);
            }
            return true;
        }
    }

    internal void Commit(nuint byteLength, long ownerId = 0,
        NativeMemoryTraceKind traceKind = NativeMemoryTraceKind.Allocated, long allocationOrdinal = 0)
    {
        if (byteLength == 0)
        {
            return;
        }

        long bytes = checked((long)byteLength);
        lock (_gate)
        {
            ValidateReservation(bytes);
            _reservedBytes -= bytes;
            _committedBytes += bytes;
            _peakCommittedBytes = Math.Max(_peakCommittedBytes, _committedBytes);
            // Every active allocation owns at least one charged byte. Admission
            // bounds this exact gauge by CapacityBytes, independently of history.
            _activeAllocationCount++;
            IncrementHistory(ref _allocationCount);
            RecordTrace(traceKind, ownerId, byteLength, allocationOrdinal: allocationOrdinal);
        }
    }

    internal void CommitReallocation(nuint byteLength, nuint previousByteLength, long ownerId = 0)
    {
        long bytes = checked((long)byteLength);
        long previousBytes = checked((long)previousByteLength);
        lock (_gate)
        {
            ValidateReservation(bytes);
            if (previousBytes > _committedBytes)
            {
                throw new InvalidOperationException("The replacement exceeds the committed budget charge.");
            }

            _reservedBytes -= bytes;
            _committedBytes = _committedBytes - previousBytes + bytes;
            _peakCommittedBytes = Math.Max(_peakCommittedBytes, _committedBytes);
            IncrementHistory(ref _reallocationCount);
            if (previousBytes == 0)
            {
                _activeAllocationCount++;
                IncrementHistory(ref _allocationCount);
            }
            RecordTrace(NativeMemoryTraceKind.Reallocated, ownerId, byteLength, previousByteLength);
        }
    }

    internal void Cancel(nuint byteLength, long ownerId = 0)
    {
        if (byteLength == 0)
        {
            return;
        }

        long bytes = checked((long)byteLength);
        lock (_gate)
        {
            ValidateReservation(bytes);
            _reservedBytes -= bytes;
            IncrementHistory(ref _failedAllocationCount);
            RecordTrace(NativeMemoryTraceKind.AcquisitionFailed, ownerId, byteLength);
        }
    }

    internal void Release(nuint byteLength, long ownerId = 0,
        NativeMemoryTraceKind traceKind = NativeMemoryTraceKind.Released, long allocationOrdinal = 0)
    {
        if (byteLength == 0)
        {
            return;
        }

        long bytes = checked((long)byteLength);
        lock (_gate)
        {
            if (bytes > _committedBytes || _activeAllocationCount == 0)
            {
                throw new InvalidOperationException("Physical release has no matching budget charge.");
            }

            _committedBytes -= bytes;
            _activeAllocationCount--;
            IncrementHistory(ref _freeCount);
            RecordTrace(traceKind, ownerId, byteLength, allocationOrdinal: allocationOrdinal);
        }
    }

    internal void RecordPreparation(nuint byteLength, long ownerId)
    {
        if (_trace.Length == 0)
        {
            return;
        }
        lock (_gate)
        {
            RecordTrace(NativeMemoryTraceKind.Prepared, ownerId, byteLength);
        }
    }

    private void RecordTrace(NativeMemoryTraceKind kind, long ownerId, nuint bytes,
        nuint previousBytes = 0, long allocationOrdinal = 0)
    {
        if (_trace.Length == 0)
        {
            return;
        }

        // Diagnostics must never throw after a successful storage transition.
        // Exhausting sequence identity stops recording rather than wrapping it.
        if (_traceSequence == long.MaxValue)
        {
            _traceOverflowed = true;
            RecordDroppedTraceEvent();
            return;
        }
        _traceSequence++;
        _trace[_traceWriteIndex] = new NativeMemoryTraceEvent(
            _traceSequence, System.Diagnostics.Stopwatch.GetTimestamp(), Id,
            ownerId == 0 ? null : ownerId, kind, bytes, previousBytes,
            _committedBytes, _reservedBytes, allocationOrdinal);
        _traceWriteIndex++;
        if (_traceWriteIndex == _trace.Length)
        {
            _traceWriteIndex = 0;
        }
        if (_traceCount == _trace.Length)
        {
            RecordDroppedTraceEvent();
        }
        else
        {
            _traceCount++;
        }
    }

    private void RecordDroppedTraceEvent()
    {
        if (_droppedTraceEventCount == long.MaxValue)
        {
            _traceOverflowed = true;
            return;
        }
        _droppedTraceEventCount++;
    }

    private void IncrementHistory(ref long counter)
    {
        if (counter == long.MaxValue)
        {
            _historyOverflowed = true;
        }
        else
        {
            counter++;
        }
    }

    private void ValidateReservation(long bytes)
    {
        if (bytes > _reservedBytes)
        {
            throw new InvalidOperationException("The allocation has no matching budget reservation.");
        }
    }

    private static long NextId()
    {
        while (true)
        {
            long current = Volatile.Read(ref _nextId);
            long next = checked(current + 1);
            if (Interlocked.CompareExchange(ref _nextId, next, current) == current)
            {
                return next;
            }
        }
    }
}

/// <summary>One consistent native admission-domain observation; it holds no ownership authority.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeMemoryBudgetStatistics
{
    internal NativeMemoryBudgetStatistics(
        long id, long capacityBytes, long committedBytes, long reservedBytes,
        long peakCommittedBytes, long peakAdmittedBytes, long allocationCount,
        long reallocationCount, long freeCount, long activeAllocationCount,
        long rejectedAllocationCount, long failedAllocationCount,
        int traceCapacity, int traceCount, long droppedTraceEventCount, bool traceOverflowed,
        bool historyOverflowed)
    {
        Id = id;
        CapacityBytes = capacityBytes;
        CommittedBytes = committedBytes;
        ReservedBytes = reservedBytes;
        PeakCommittedBytes = peakCommittedBytes;
        PeakAdmittedBytes = peakAdmittedBytes;
        AllocationCount = allocationCount;
        ReallocationCount = reallocationCount;
        FreeCount = freeCount;
        ActiveAllocationCount = activeAllocationCount;
        RejectedAllocationCount = rejectedAllocationCount;
        FailedAllocationCount = failedAllocationCount;
        TraceCapacity = traceCapacity;
        TraceCount = traceCount;
        DroppedTraceEventCount = droppedTraceEventCount;
        TraceOverflowed = traceOverflowed;
        HistoryOverflowed = historyOverflowed;
    }

    /// <summary>Gets the observed domain's stable identity.</summary>
    public long Id { get; }
    /// <summary>Gets the immutable native extent ceiling in bytes.</summary>
    public long CapacityBytes { get; }
    /// <summary>Gets complete extents still owned after successful acquisition.</summary>
    public long CommittedBytes { get; }
    /// <summary>Gets admitted extents awaiting successful acquisition or rollback.</summary>
    public long ReservedBytes { get; }
    /// <summary>Gets the lifetime maximum committed extent, not opaque realloc's internal peak.</summary>
    public long PeakCommittedBytes { get; }
    /// <summary>Gets the lifetime maximum committed-plus-reserved admission demand.</summary>
    public long PeakAdmittedBytes { get; }
    /// <summary>Gets fresh backing acquisitions, including realloc from null.</summary>
    public long AllocationCount { get; }
    /// <summary>Gets successful realloc calls, including realloc of a null pointer.</summary>
    public long ReallocationCount { get; }
    /// <summary>Gets physical release calls; replacing backing by realloc is not a free call.</summary>
    public long FreeCount { get; }
    /// <summary>Gets the exact charged backing allocation gauge, independent of saturated lifetime histories.</summary>
    public long ActiveAllocationCount { get; }
    /// <summary>Gets requests refused by the ceiling before native allocation.</summary>
    public long RejectedAllocationCount { get; }
    /// <summary>Gets admitted acquisition attempts rolled back after failure.</summary>
    public long FailedAllocationCount { get; }
    /// <summary>Gets the fixed preallocated event capacity, or zero when tracing is disabled.</summary>
    public int TraceCapacity { get; }
    /// <summary>Gets retained events currently available for copying.</summary>
    public int TraceCount { get; }
    /// <summary>Gets overwritten or sequence-exhausted events, saturating at long.MaxValue.</summary>
    public long DroppedTraceEventCount { get; }
    /// <summary>Gets whether event identities were exhausted or the dropped count ceased being exact.</summary>
    public bool TraceOverflowed { get; }
    /// <summary>Gets whether a lifetime event counter saturated; values at long.MaxValue are then lower bounds.</summary>
    public bool HistoryOverflowed { get; }
}

/// <summary>Raised before allocation when a domain cannot admit a complete backing extent.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032", Justification = "Only admission creates this exception; every instance requires the exact rejected decision metadata.")]
public sealed class NativeMemoryBudgetExceededException : InvalidOperationException
{
    internal NativeMemoryBudgetExceededException(
        long budgetId, long capacityBytes, nuint requestedBytes, long availableBytes)
        : base($"Native budget {budgetId} cannot admit {requestedBytes} bytes: {availableBytes} of {capacityBytes} bytes remain.")
    {
        BudgetId = budgetId;
        CapacityBytes = capacityBytes;
        RequestedBytes = requestedBytes;
        AvailableBytes = availableBytes;
    }

    /// <summary>Gets the rejecting domain identity.</summary>
    public long BudgetId { get; }
    /// <summary>Gets the immutable admission ceiling.</summary>
    public long CapacityBytes { get; }
    /// <summary>Gets the complete requested extent, not the logical slice length.</summary>
    public nuint RequestedBytes { get; }
    /// <summary>Gets remaining admission capacity at the rejected decision.</summary>
    public long AvailableBytes { get; }
}
