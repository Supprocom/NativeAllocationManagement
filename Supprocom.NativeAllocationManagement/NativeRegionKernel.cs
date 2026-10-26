using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

internal sealed unsafe class NativeRegionKernel
{
    private const nuint DefaultSegmentBytes = 4_096;
    private const string OwnerKind = "NativeRegion";
    private static readonly nuint HeaderBytes = AlignUp(
        (nuint)sizeof(RegionSegmentHeader),
        NativeSegment.Alignment);

    // One disposed garbage-collector-policy region per thread keeps its single
    // segment for the next region instead of detaching it for finalization.
    private const long ReuseLimitBytes = 64 * 1024;

    [ThreadStatic]
    private static NativeRegionKernel? t_reusable;

    private readonly NativeMemoryReturn _returnMemoryOnDispose;
    private NativeMemoryBudget? _budget;
    internal long Id { get; private set; } = NativeOwnerIdentity.Next();
    private readonly int _ownerThreadId;
    private NativeOwnerLifecycle _lifecycle;
    private RegionSegmentHeader* _firstSegment;
    private RegionSegmentHeader* _currentSegment;
    private byte* _currentCursor;
    private byte* _currentEnd;
    private long _requestedBytes;
    private long _retainedBytes;
    private long _peakRetainedBytes;
    // Append-only lifetime count is also the non-reusing backing ordinal.
    // Terminal cleanup clears the head, not this completed acquisition history.
    private int _segmentCount;
    // Backing count when the current lifetime began; nonzero only after reuse.
    private int _activationSegmentCount;
    private int _activeBorrowCount;
    // Region and Local handles carry the lifetime they were issued for; a reused
    // kernel starts the next lifetime, so earlier handles stay disposed.
    private int _generation;
    private bool _retainedForReuse;

    internal NativeRegionKernel(
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose,
        NativeMemoryBudget? budget = null)
    {
        _returnMemoryOnDispose = returnMemoryOnDispose;
        _budget = budget;
        _ownerThreadId = NativeThreadIdentity.Current;
        _lifecycle = NativeOwnerLifecycle.Active;

        if (preAllocateBytes != 0)
        {
            AppendSegment(
                preAllocateBytes,
                "declaration reservation");
        }
    }

    internal NativeOwnerLifecycle Lifecycle => _lifecycle;

    internal int Generation => _generation;

    internal static bool HasReusableKernelForTest => t_reusable is not null;

    internal static NativeRegionKernel Acquire(
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose)
    {
        if (returnMemoryOnDispose == NativeMemoryReturn.ToGarbageCollector)
        {
            NativeRegionKernel? reusable = t_reusable;
            if (reusable is not null)
            {
                t_reusable = null;
                if (reusable.TryReactivate(preAllocateBytes))
                {
                    return reusable;
                }
            }
        }

        return new NativeRegionKernel(
            preAllocateBytes,
            returnMemoryOnDispose);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "A discarded reuse candidate frees its storage and disarms its emergency finalizer.")]
    private bool TryReactivate(nuint preAllocateBytes)
    {
        RegionSegmentHeader* segment = _firstSegment;
        // A new measurement epoch observes fresh acquisition, and a short
        // segment cannot satisfy the requested reservation.
        if (segment == null
            || segment->MetricsEpoch != NativeMemoryAccounting.CurrentMetricsEpoch
            || segment->Capacity < preAllocateBytes
            || _generation == int.MaxValue)
        {
            FreeSegments();
            GC.SuppressFinalize(this);
            return false;
        }

        Id = NativeOwnerIdentity.Next();
        _generation++;
        _retainedForReuse = false;
        _lifecycle = NativeOwnerLifecycle.Active;
        _currentSegment = segment;
        _currentCursor = (byte*)segment + HeaderBytes;
        _currentEnd = _currentCursor + segment->Capacity;
        _requestedBytes = 0;
        _peakRetainedBytes = _retainedBytes;
        _activationSegmentCount = _segmentCount;
        return true;
    }

    private bool TryRetainForReuse()
    {
        // Disposal already proved no initializer or borrow is entered, and no
        // other thread can reach this storage, so detaching it buys no safety.
        if (_budget is not null
            || t_reusable is not null
            || _firstSegment == null
            || _firstSegment->Next != null
            || _retainedBytes > ReuseLimitBytes)
        {
            return false;
        }

        _retainedForReuse = true;
        t_reusable = this;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Local<T> LeaseInitialized<T>(
        int length,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateLeaseActive(nameof(NativeRegion.Lease));
        if (_activeBorrowCount < 0)
        {
            ThrowActiveOperation(nameof(NativeRegion.Lease));
        }

        nuint byteLength = CalculateByteLength<T>(length);
        nuint alignment = CalculateAlignment<T>();
        long requestedBytes = checked(
            _requestedBytes + checked((long)byteLength));
        // The high bit is initializer admission; lower bits remain the local
        // bounded-borrow count. No per-range control or owner field is needed.
        _activeBorrowCount |= int.MinValue;
        try
        {
            RegionReservation reservation = Reserve(byteLength, alignment);
            Initialize(reservation, length, initializer);
            _requestedBytes = requestedBytes;
            return new Local<T>(this, reservation.Pointer, length, _generation);
        }
        finally
        {
            _activeBorrowCount &= int.MaxValue;
        }
    }

    internal Local<T> LeaseFilled<T>(
        int length,
        T value)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateLeaseActive(nameof(NativeRegion.Lease));
        if (_activeBorrowCount < 0)
        {
            ThrowActiveOperation(nameof(NativeRegion.Lease));
        }

        nuint byteLength = CalculateByteLength<T>(length);
        long requestedBytes = checked(
            _requestedBytes + checked((long)byteLength));
        RegionReservation reservation = Reserve(byteLength, CalculateAlignment<T>());
        // An unmanaged fill runs no user code: no initializer admission or rollback.
        new Span<T>((void*)reservation.Pointer, length).Fill(value);
        _requestedBytes = requestedBytes;
        return new Local<T>(this, reservation.Pointer, length, _generation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Initialize<T>(
        RegionReservation reservation,
        int length,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged
    {
        int initializedLength = 0;
        try
        {
            NativeLeaseWriter<T> writer = new(
                reservation.Pointer,
                length,
                ref initializedLength);
            initializer(writer);
            if (initializedLength != length)
            {
                throw new InvalidOperationException(
                    $"The native lease initializer wrote {initializedLength} of {length} required elements.");
            }

        }
        catch
        {
            RollBack(reservation);
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ValidateActive(string operation)
    {
        if (_lifecycle != NativeOwnerLifecycle.Active)
        {
            throw CreateDisposed(operation);
        }

        if (NativeThreadIdentity.Current != _ownerThreadId)
        {
            throw CreateState(
                operation,
                "NativeRegion is confined to its construction thread.");
        }
    }

    // NativeRegion and Local<T> are ref structs: safe code cannot move them off
    // the construction thread, so per-lease paths only check the lifecycle.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateLeaseActive(string operation)
    {
        if (_lifecycle != NativeOwnerLifecycle.Active)
        {
            throw CreateDisposed(operation);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnterBorrow(string operation, int generation)
    {
        if (generation != _generation)
        {
            throw CreateDisposed(operation);
        }

        ValidateLeaseActive(operation);
        _activeBorrowCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitBorrow() => _activeBorrowCount--;

    internal NativeOwnerStatistics GetStatistics()
    {
        ValidateActive(nameof(GetStatistics));
        long usableCapacityBytes = 0;
        for (RegionSegmentHeader* segment = _firstSegment; segment != null; segment = segment->Next)
        {
            usableCapacityBytes = checked(usableCapacityBytes + (long)segment->Capacity);
        }
        return new NativeOwnerStatistics(
            _lifecycle,
            Generation: 0,
            _requestedBytes,
            _retainedBytes,
            RetiredBytes: 0,
            _firstSegment == null ? 0 : _segmentCount,
            AvailableSegmentCount: GetAvailableSegmentCount(),
            RetiredSegmentCount: 0,
            TrimmedBytes: 0,
            TrimCallCount: 0,
            FreshSegmentAllocationCount: _segmentCount - _activationSegmentCount)
        {
            OwnerId = Id,
            Model = NativeOwnerModel.ThreadConfinedRegion,
            // Checked int backing identity exhausts before long history can overflow.
            HistoryOverflowed = false,
            OutstandingNativeBytes = _retainedBytes,
            DetachedNativeBytes = 0,
            PeakOutstandingNativeBytes = _peakRetainedBytes,
            InitializedPayloadBytes = _lifecycle == NativeOwnerLifecycle.Active ? _requestedBytes : 0,
            PeakInitializedPayloadBytes = _requestedBytes,
            UsableCapacityBytes = usableCapacityBytes
        };
    }

    internal NativeOwnerDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        if (NativeThreadIdentity.Current != _ownerThreadId)
        {
            throw CreateState(nameof(NativeRegion.CaptureDiagnosticSnapshot),
                "NativeRegion is confined to its construction thread.");
        }
        int index = 0;
        int currentIndex = -1;
        for (RegionSegmentHeader* segment = _firstSegment; segment != null; segment = segment->Next)
        {
            if (segment == _currentSegment)
            {
                currentIndex = index;
                break;
            }
            index = checked(index + 1);
        }
        NativeOwnerDiagnosticSnapshot snapshot = new(
            _lifecycle, 0, 0, NativeMemoryAccounting.CurrentMetricsEpoch,
            0, 0, 0, currentIndex, -1, _firstSegment == null ? 0 : _segmentCount,
            _lifecycle == NativeOwnerLifecycle.Active ? GetAvailableSegmentCount() : 0,
            0, 0, 0, 0, 0, false)
        {
            OwnerId = Id,
            Model = NativeOwnerModel.ThreadConfinedRegion,
            HistoryOverflowed = false,
            OutstandingNativeBytes = _retainedBytes,
            DetachedNativeBytes = _lifecycle == NativeOwnerLifecycle.Disposed
                && _returnMemoryOnDispose == NativeMemoryReturn.ToGarbageCollector
                && !_retainedForReuse ? _retainedBytes : 0,
            PeakOutstandingNativeBytes = _peakRetainedBytes,
            InitializedPayloadBytes = _lifecycle == NativeOwnerLifecycle.Active ? _requestedBytes : 0,
            PeakInitializedPayloadBytes = _requestedBytes
        };
        GC.KeepAlive(this);
        return snapshot;
    }

    private int GetAvailableSegmentCount() =>
        _currentSegment != null && _currentCursor == (byte*)_currentSegment + HeaderBytes ? 1 : 0;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "Internal region-kernel disposal disarms its emergency finalizer; the outer owner exposes disposal.")]
    internal void Dispose()
    {
        if (NativeThreadIdentity.Current != _ownerThreadId)
        {
            throw CreateState(
                nameof(Dispose),
                "NativeRegion is confined to its construction thread.");
        }

        if (_lifecycle == NativeOwnerLifecycle.Disposed)
        {
            return;
        }

        if (_activeBorrowCount != 0)
        {
            ThrowActiveOperation(nameof(Dispose));
        }

        _lifecycle = NativeOwnerLifecycle.Disposed;
        if (_returnMemoryOnDispose == NativeMemoryReturn.ToNativeMemory)
        {
            FreeSegments();
            GC.SuppressFinalize(this);
            return;
        }

        if (TryRetainForReuse())
        {
            return;
        }

        MarkSegmentsDetached();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RegionReservation Reserve(
        nuint byteLength,
        nuint alignment)
    {
        if (byteLength == 0)
        {
            return new RegionReservation(
                _currentCursor,
                IntPtr.Zero);
        }

        byte* originalCursor = _currentCursor;
        nuint aligned = originalCursor == null
            ? 0
            : AlignUp((nuint)originalCursor, alignment);
        if (originalCursor == null
            || aligned > (nuint)_currentEnd
            || byteLength > (nuint)_currentEnd - aligned)
        {
            GrowSegment(byteLength, "allocation growth");
            originalCursor = _currentCursor;
            aligned = (nuint)originalCursor;
        }

        _currentCursor = (byte*)checked(aligned + byteLength);
        return new RegionReservation(
            originalCursor,
            (IntPtr)aligned);
    }

    private RegionSegmentHeader* GrowSegment(
        nuint requiredBytes,
        string operation)
    {
        nuint growth = DefaultSegmentBytes;
        if (_currentSegment != null)
        {
            try
            {
                growth = checked(_currentSegment->Capacity * 2);
            }
            catch (OverflowException)
            {
                growth = requiredBytes;
            }
        }

        nuint capacity = Math.Max(
            requiredBytes,
            Math.Max(DefaultSegmentBytes, growth));
        return AppendSegment(capacity, operation, requiredBytes);
    }

    private RegionSegmentHeader* AppendSegment(
        nuint capacity,
        string operation,
        nuint minimumCapacity = 0)
    {
        // A lexical bank only appends. The existing count therefore supplies
        // its lifetime backing ordinal; exhaustion must precede admission.
        int segmentCount = checked(_segmentCount + 1);
        RegionSegmentHeader* segment = AllocateSegment(
            capacity,
            operation,
            segmentCount,
            minimumCapacity);
        capacity = segment->Capacity;
        if (_currentSegment == null)
        {
            _firstSegment = segment;
        }
        else
        {
            _currentSegment->Next = segment;
        }

        _currentSegment = segment;
        _currentCursor = (byte*)segment + HeaderBytes;
        _currentEnd = _currentCursor + capacity;
        _retainedBytes = checked(
            _retainedBytes + checked((long)segment->AllocationBytes));
        _segmentCount = segmentCount;
        return segment;
    }

    private RegionSegmentHeader* AllocateSegment(
        nuint capacity,
        string operation,
        int allocationOrdinal,
        nuint minimumCapacity = 0)
    {
        nuint allocationBytes = NativeAlignedAllocation.GetBackingByteLength(checked(HeaderBytes + capacity));
        if (_budget is not null)
        {
            nuint preferredBytes = allocationBytes;
            nuint minimumBytes = minimumCapacity == 0
                ? allocationBytes : NativeAlignedAllocation.GetBackingByteLength(checked(HeaderBytes + minimumCapacity));
            if (!_budget.TryReservePreferred(preferredBytes, minimumBytes, out allocationBytes, out long availableBytes, Id))
            {
                throw new NativeMemoryBudgetExceededException(_budget.Id, _budget.CapacityBytes, minimumBytes, availableBytes);
            }
            if (allocationBytes != preferredBytes)
            {
                capacity = minimumCapacity;
            }
        }

        void* memory = null;
        bool acquired = false;
        bool recorded = false;
        long metricsEpoch = 0;
        try
        {
            long prospectiveBytes = checked(_retainedBytes + checked((long)allocationBytes));
            if (NativeMemoryTestHooks.ConsumeForcedFailure())
            {
                throw new NativeAllocationFailedException(allocationBytes, OwnerKind, generation: 0, operation, _lifecycle);
            }
            memory = NativeMemory.AlignedAlloc(
                allocationBytes,
                NativeSegment.Alignment);
            if (memory == null)
            {
                throw new NativeAllocationFailedException(
                    allocationBytes,
                    OwnerKind,
                    generation: 0,
                    operation,
                    _lifecycle);
            }

            _peakRetainedBytes = Math.Max(_peakRetainedBytes, prospectiveBytes);
            RegionSegmentHeader* segment =
                (RegionSegmentHeader*)memory;
            *segment = default;
            segment->Capacity = capacity;
            segment->AllocationBytes = allocationBytes;
            segment->AllocationOrdinal = allocationOrdinal;
            metricsEpoch = NativeMemoryAccounting.RecordAllocation(allocationBytes, zeroed: false);
            recorded = true;
            segment->MetricsEpoch = metricsEpoch;
            _budget?.Commit(allocationBytes, Id, allocationOrdinal: allocationOrdinal);
            acquired = true;
            return segment;
        }
        catch (OutOfMemoryException exception)
        {
            throw new NativeAllocationFailedException(
                allocationBytes,
                OwnerKind,
                generation: 0,
                operation,
                _lifecycle,
                exception);
        }
        finally
        {
            if (!acquired)
            {
                if (memory != null)
                {
                    NativeMemory.AlignedFree(memory);
                    if (recorded)
                    {
                        NativeMemoryAccounting.RecordFree(allocationBytes, detached: false, metricsEpoch);
                    }
                }
                _budget?.Cancel(allocationBytes, Id);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RollBack(RegionReservation reservation)
    {
        _currentCursor = reservation.OriginalCursor;
    }

    private void MarkSegmentsDetached()
    {
        RegionSegmentHeader* segment = _firstSegment;
        while (segment != null)
        {
            segment->Detached = 1;
            NativeMemoryAccounting.RecordDetachedBytes(
                segment->AllocationBytes,
                segment->MetricsEpoch);
            segment = segment->Next;
        }

        NativeMemoryAccounting.RecordDetachedGeneration(
            NativeMemoryAccounting.CurrentMetricsEpoch);
    }

    private void FreeSegments()
    {
        RegionSegmentHeader* segment = _firstSegment;
        _firstSegment = null;
        _currentSegment = null;
        _currentCursor = null;
        _currentEnd = null;
        _retainedBytes = 0;
        while (segment != null)
        {
            RegionSegmentHeader* next = segment->Next;
            nuint allocationBytes = segment->AllocationBytes;
            long metricsEpoch = segment->MetricsEpoch;
            bool detached = segment->Detached != 0;
            int allocationOrdinal = segment->AllocationOrdinal;
            NativeMemory.AlignedFree(segment);
            _budget?.Release(allocationBytes, Id, allocationOrdinal: allocationOrdinal);
            NativeMemoryAccounting.RecordFree(
                allocationBytes,
                detached,
                metricsEpoch);
            segment = next;
        }
        _budget = null;
    }

    private NativeAllocationStateException CreateState(
        string operation,
        string message) =>
        new(
            message,
            OwnerKind,
            generation: 0,
            currentGeneration: 0,
            operation,
            activeOperationCount: ActiveOperationCount,
            allocationId: 0,
            _lifecycle);

    internal static NativeAllocationDisposedException CreateEndedLifetime(
        string operation) =>
        new(
            "The native region is disposed.",
            OwnerKind,
            generation: 0,
            currentGeneration: 0,
            operation,
            activeOperationCount: 0,
            allocationId: 0,
            NativeOwnerLifecycle.Disposed);

    private NativeAllocationDisposedException CreateDisposed(
        string operation) =>
        new(
            "The native region is disposed.",
            OwnerKind,
            generation: 0,
            currentGeneration: 0,
            operation,
            activeOperationCount: ActiveOperationCount,
            allocationId: 0,
            _lifecycle);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowActiveOperation(string operation) =>
        throw new NativeAllocationInUseException(
            "NativeRegion cannot initialize recursively or dispose during an entered initializer or bounded callback.",
            OwnerKind, generation: 0, currentGeneration: 0, operation,
            activeOperationCount: ActiveOperationCount, allocationId: 0, _lifecycle);

    private int ActiveOperationCount =>
        (_activeBorrowCount & int.MaxValue) + (_activeBorrowCount < 0 ? 1 : 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint CalculateByteLength<T>(int length)
        where T : unmanaged
    {
        uint elementSize = (uint)Unsafe.SizeOf<T>();
        if (IntPtr.Size == 4)
        {
            ulong byteLength = (ulong)(uint)length * elementSize;
            if (byteLength > uint.MaxValue)
            {
                ThrowByteLengthOverflow();
            }
        }

        return (nuint)(uint)length * elementSize;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowByteLengthOverflow() =>
        throw new OverflowException(
            "The Lease byte count exceeds native addressable storage.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint CalculateAlignment<T>()
        where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (size >= IntPtr.Size)
        {
            return (nuint)IntPtr.Size;
        }

        return size >= 4
            ? 4u
            : size >= 2
                ? 2u
                : 1u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint AlignUp(nuint value, nuint alignment)
    {
        nuint mask = alignment - 1;
        return checked(value + mask) & ~mask;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0055", Justification = "Emergency native-memory cleanup supplements mandatory deterministic disposal.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "An emergency finalizer must never let cleanup exceptions terminate the process.")]
    ~NativeRegionKernel()
    {
        try
        {
            FreeSegments();
        }
        catch
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RegionSegmentHeader
    {
        internal RegionSegmentHeader* Next;
        internal nuint Capacity;
        internal nuint AllocationBytes;
        internal long MetricsEpoch;
        internal int Detached;
        internal int AllocationOrdinal;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private readonly struct RegionReservation
    {
        internal RegionReservation(
            byte* originalCursor,
            IntPtr pointer)
        {
            OriginalCursor = originalCursor;
            Pointer = pointer;
        }

        internal byte* OriginalCursor { get; }

        internal IntPtr Pointer { get; }
    }
}
