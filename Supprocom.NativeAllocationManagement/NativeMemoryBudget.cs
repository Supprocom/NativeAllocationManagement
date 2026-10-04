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
    private long _committedBytes;
    private long _reservedBytes;
    private long _peakCommittedBytes;
    private long _peakAdmittedBytes;
    private long _allocationCount;
    private long _reallocationCount;
    private long _freeCount;
    private long _rejectedAllocationCount;
    private long _failedAllocationCount;

    /// <summary>Creates a shared domain with an immutable native extent limit.</summary>
    /// <param name="capacityBytes">The maximum admitted NAM-requested extent in bytes.</param>
    public NativeMemoryBudget(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
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
                _reallocationCount, _freeCount, _allocationCount - _freeCount,
                _rejectedAllocationCount, _failedAllocationCount);
        }
    }

    internal void Reserve(nuint byteLength)
    {
        if (!TryReserve(byteLength, out long availableBytes))
        {
            throw new NativeMemoryBudgetExceededException(
                Id, CapacityBytes, byteLength, availableBytes);
        }
    }

    internal bool TryReserve(nuint byteLength, out long availableBytes) =>
        TryReservePreferred(byteLength, byteLength, out _, out availableBytes);

    internal bool TryReservePreferred(
        nuint preferredByteLength,
        nuint minimumByteLength,
        out nuint admittedByteLength,
        out long availableBytes)
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
                _rejectedAllocationCount = checked(_rejectedAllocationCount + 1);
                admittedByteLength = 0;
                return false;
            }

            _reservedBytes += bytes;
            _peakAdmittedBytes = Math.Max(
                _peakAdmittedBytes, _committedBytes + _reservedBytes);
            admittedByteLength = checked((nuint)bytes);
            return true;
        }
    }

    internal void Commit(nuint byteLength)
    {
        if (byteLength == 0)
        {
            return;
        }

        long bytes = checked((long)byteLength);
        lock (_gate)
        {
            ValidateReservation(bytes);
            long allocationCount = checked(_allocationCount + 1);
            _reservedBytes -= bytes;
            _committedBytes += bytes;
            _peakCommittedBytes = Math.Max(_peakCommittedBytes, _committedBytes);
            _allocationCount = allocationCount;
        }
    }

    internal void CommitReallocation(nuint byteLength, nuint previousByteLength)
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

            long reallocationCount = checked(_reallocationCount + 1);
            long allocationCount = checked(_allocationCount + (previousBytes == 0 ? 1 : 0));
            _reservedBytes -= bytes;
            _committedBytes = _committedBytes - previousBytes + bytes;
            _peakCommittedBytes = Math.Max(_peakCommittedBytes, _committedBytes);
            _reallocationCount = reallocationCount;
            _allocationCount = allocationCount;
        }
    }

    internal void Cancel(nuint byteLength)
    {
        if (byteLength == 0)
        {
            return;
        }

        long bytes = checked((long)byteLength);
        lock (_gate)
        {
            ValidateReservation(bytes);
            long failedCount = checked(_failedAllocationCount + 1);
            _reservedBytes -= bytes;
            _failedAllocationCount = failedCount;
        }
    }

    internal void Release(nuint byteLength)
    {
        if (byteLength == 0)
        {
            return;
        }

        long bytes = checked((long)byteLength);
        lock (_gate)
        {
            if (bytes > _committedBytes || _allocationCount == _freeCount)
            {
                throw new InvalidOperationException("Physical release has no matching budget charge.");
            }

            long freeCount = checked(_freeCount + 1);
            _committedBytes -= bytes;
            _freeCount = freeCount;
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
        long rejectedAllocationCount, long failedAllocationCount)
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
    /// <summary>Gets charged backing allocations not yet physically released.</summary>
    public long ActiveAllocationCount { get; }
    /// <summary>Gets requests refused by the ceiling before native allocation.</summary>
    public long RejectedAllocationCount { get; }
    /// <summary>Gets admitted acquisition attempts rolled back after failure.</summary>
    public long FailedAllocationCount { get; }
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
