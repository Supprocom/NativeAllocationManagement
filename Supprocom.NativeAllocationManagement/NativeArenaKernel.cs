using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

internal sealed unsafe partial class NativeArenaKernel
{
    private const nuint DefaultSegmentBytes = 4_096;
    private const nuint SegmentAlignment = 64;
    private const string OwnerKind = "NativeArena";
    private static readonly nuint HeaderBytes = AlignUp(
        (nuint)sizeof(ArenaSegmentHeader),
        SegmentAlignment);

    private readonly NativeMemoryReturn _returnMemoryOnDispose;
    private readonly NativeMemoryBudget? _budget;
    internal long Id { get; } = NativeOwnerIdentity.Next();
    private readonly int _ownerThreadId;
    private ArenaLane _ordinary;
    private ArenaLane _scoped;
    private NativeOwnerLifecycle _lifecycle;
    private ulong _generation = 1;
    private ulong _scopeEpoch = 1;
    private int _activeBorrowCount;
    private int _initializerActive;
    private long _retainedBytes;
    private long _trimmedBytes;
    private long _trimCallCount;
    private long _freshSegmentAllocationCount;
    private long _nextAllocationOrdinal;
    private bool _historyOverflowed;
    private int _segmentCount;

    internal NativeArenaKernel(
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose,
        NativeMemoryBudget? budget = null)
    {
        _returnMemoryOnDispose = returnMemoryOnDispose;
        _budget = budget;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _lifecycle = NativeOwnerLifecycle.Active;
        if (preAllocateBytes != 0)
        {
            AppendSegment(
                ref _ordinary,
                preAllocateBytes,
                "declaration reservation");
            ResetLane(ref _ordinary);
        }
    }

    internal NativeOwnerLifecycle Lifecycle => _lifecycle;

    internal ulong Generation => _generation;

    internal ulong ScopeEpoch => _scopeEpoch;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ArenaLease<T> Scratch<T>(
        int length,
        bool scoped,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged
    {
        if (_prepared)
        {
            if (TryScratch(length, scoped, initializer, out ArenaLease<T> lease))
            {
                return lease;
            }
            throw new InvalidOperationException("The prepared arena byte capacity is exhausted. Use TryScratch or TryScratchScoped for non-allocating refusal.");
        }
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateActive(scoped
            ? nameof(NativeArena.ScratchScoped)
            : nameof(NativeArena.Scratch));
        if (_initializerActive != 0)
        {
            ThrowNestedInitializer();
        }

        nuint byteLength = CalculateByteLength<T>(length);
        nuint alignment = CalculateAlignment<T>();
        ref ArenaLane lane = ref (
            scoped ? ref _scoped : ref _ordinary);
        ArenaReservation reservation = Reserve(
            ref lane,
            byteLength,
            alignment,
            scoped);
        _initializerActive = 1;
        try
        {
            Initialize(reservation, length, initializer);
        }
        finally
        {
            _initializerActive = 0;
        }

        return new ArenaLease<T>(
            this,
            reservation.Pointer,
            length,
            _generation,
            scoped ? _scopeEpoch : 0,
            scoped);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Initialize<T>(
        ArenaReservation reservation,
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
                ThrowIncompleteInitialization(
                    initializedLength,
                    length);
            }
        }
        catch
        {
            RollBack(reservation);
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal IntPtr EnterBorrow(
        IntPtr pointer,
        int length,
        ulong generation,
        ulong scopeEpoch,
        bool scoped,
        string operation)
    {
        ValidateHandle(
            generation,
            scopeEpoch,
            scoped,
            operation);
        _activeBorrowCount++;
        return pointer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitBorrow()
    {
        _activeBorrowCount--;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ValidateHandle(
        ulong generation,
        ulong scopeEpoch,
        bool scoped,
        string operation)
    {
        ValidateActive(operation);
        if (generation != _generation
            || (scoped && scopeEpoch != _scopeEpoch))
        {
            ThrowStale(
                generation,
                scopeEpoch,
                scoped,
                operation);
        }
    }

    internal void Reset()
    {
        ValidateBoundary(nameof(NativeArena.Reset));
        ulong nextGeneration = GetNextEpochOrClose(
            _generation,
            nameof(NativeArena.Reset));
        ulong nextScopeEpoch = GetNextEpochOrClose(
            _scopeEpoch,
            nameof(NativeArena.Reset));
        ResetLane(ref _ordinary);
        ResetLane(ref _scoped);
        _generation = nextGeneration;
        _scopeEpoch = nextScopeEpoch;
    }

    internal void RecycleScoped()
    {
        ValidateBoundary(nameof(NativeArena.RecycleScoped));
        ulong nextScopeEpoch = GetNextEpochOrClose(
            _scopeEpoch,
            nameof(NativeArena.RecycleScoped));
        ResetLane(ref _scoped);
        _scopeEpoch = nextScopeEpoch;
    }

    internal NativeOwnerStatistics GetStatistics()
    {
        ValidateActive(nameof(GetStatistics));
        long requestedBytes = checked(
            CountUsedBytes(_ordinary)
            + CountUsedBytes(_scoped));
        int available = checked(
            CountUnusedSegments(_ordinary)
            + CountUnusedSegments(_scoped));
        long usableCapacityBytes = 0;
        for (ArenaSegmentHeader* segment = _ordinary.First; segment != null; segment = segment->Next)
        {
            usableCapacityBytes = checked(usableCapacityBytes + (long)segment->Capacity);
        }
        for (ArenaSegmentHeader* segment = _scoped.First; segment != null; segment = segment->Next)
        {
            usableCapacityBytes = checked(usableCapacityBytes + (long)segment->Capacity);
        }
        return new NativeOwnerStatistics(
            _lifecycle,
            Generation: unchecked((long)_generation),
            requestedBytes,
            _retainedBytes,
            RetiredBytes: 0,
            _segmentCount,
            available,
            RetiredSegmentCount: 0,
            _trimmedBytes,
            _trimCallCount,
            _freshSegmentAllocationCount)
        {
            OwnerId = Id,
            Model = NativeOwnerModel.ThreadConfinedArena,
            HistoryOverflowed = _historyOverflowed,
            UsableCapacityBytes = usableCapacityBytes - _externalActiveBytes,
            BorrowedBytes = _externalRetainedBytes
        };
    }

    internal NativeOwnerDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        ValidateThread(nameof(NativeArena.CaptureDiagnosticSnapshot));
        NativeOwnerDiagnosticSnapshot snapshot = new(
            _lifecycle, unchecked((long)_generation), unchecked((long)_scopeEpoch),
            NativeMemoryAccounting.CurrentMetricsEpoch,
            0, 0, 0, GetCurrentSegmentIndex(_ordinary), GetCurrentSegmentIndex(_scoped),
            _segmentCount,
            _lifecycle == NativeOwnerLifecycle.Active
                ? checked(CountUnusedSegments(_ordinary) + CountUnusedSegments(_scoped)) : 0,
            0, 0, 0, 0, 0, false)
        {
            OwnerId = Id,
            Model = NativeOwnerModel.ThreadConfinedArena,
            HistoryOverflowed = _historyOverflowed
        };
        GC.KeepAlive(this);
        return snapshot;
    }

    private static int GetCurrentSegmentIndex(ArenaLane lane)
    {
        int index = 0;
        for (ArenaSegmentHeader* segment = lane.First; segment != null; segment = segment->Next)
        {
            if (segment == lane.Current)
            {
                return index;
            }
            index = checked(index + 1);
        }
        return -1;
    }

    internal nuint TrimRetainedMemory(nuint byteBudget)
    {
        ValidateBoundary(nameof(NativeArena.TrimRetainedMemory));
        NativeOwnerHistory.Increment(ref _trimCallCount, ref _historyOverflowed);
        if (_prepared)
        {
            nuint preparedReleased = TrimPrepared(byteBudget);
            NativeOwnerHistory.Add(ref _trimmedBytes, checked((long)preparedReleased), ref _historyOverflowed);
            return preparedReleased;
        }
        nuint released = 0;
        released = checked(
            released + TrimLaneTail(
                ref _ordinary,
                byteBudget - Math.Min(byteBudget, released)));
        if (released < byteBudget)
        {
            released = checked(
                released + TrimLaneTail(
                    ref _scoped,
                    byteBudget - released));
        }

        NativeOwnerHistory.Add(ref _trimmedBytes, checked((long)released), ref _historyOverflowed);
        return released;
    }

    internal void Dispose()
    {
        ValidateThread(nameof(Dispose));
        if (_lifecycle == NativeOwnerLifecycle.Disposed)
        {
            return;
        }

        if (_activeBorrowCount != 0 || _initializerActive != 0)
        {
            ThrowBoundaryInUse(nameof(Dispose));
        }

        Close();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "Closing the native arena deterministically disarms its emergency finalizer.")]
    private void Close()
    {
        _lifecycle = NativeOwnerLifecycle.Disposed;
        if (_returnMemoryOnDispose
            == NativeMemoryReturn.ToNativeMemory)
        {
            FreeAllSegments();
            GC.SuppressFinalize(this);
            return;
        }

        MarkDetached(_ordinary);
        MarkDetached(_scoped);
        NativeMemoryAccounting.RecordDetachedGeneration(
            NativeMemoryAccounting.CurrentMetricsEpoch);
    }

    private ulong GetNextEpochOrClose(
        ulong epoch,
        string operation)
    {
        if (epoch != ulong.MaxValue)
        {
            return epoch + 1;
        }

        Close();
        ThrowEpochExhausted(operation);
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ArenaReservation Reserve(
        ref ArenaLane lane,
        nuint byteLength,
        nuint alignment,
        bool scoped)
    {
        ArenaSegmentHeader* originalSegment = lane.Current;
        byte* originalCursor = lane.Cursor;
        nuint originalUsedBytes = lane.UsedBytes;
        if (byteLength == 0)
        {
            return new ArenaReservation(
                scoped,
                originalSegment,
                originalCursor,
                originalUsedBytes,
                (IntPtr)originalCursor);
        }

        nuint aligned = originalCursor == null
            ? 0
            : AlignUp((nuint)originalCursor, alignment);
        if (originalCursor == null
            || aligned > (nuint)lane.End
            || byteLength > (nuint)lane.End - aligned)
        {
            return ReserveSlow(
                ref lane,
                byteLength,
                alignment,
                scoped,
                originalSegment,
                originalCursor);
        }

        byte* end = (byte*)checked(aligned + byteLength);
        nuint usedBytes = checked(
            originalUsedBytes
            + checked((nuint)(end - originalCursor)));
        lane.Cursor = end;
        lane.UsedBytes = usedBytes;
        return new ArenaReservation(
            scoped,
            originalSegment,
            originalCursor,
            originalUsedBytes,
            (IntPtr)aligned);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ArenaReservation ReserveSlow(
        ref ArenaLane lane,
        nuint byteLength,
        nuint alignment,
        bool scoped,
        ArenaSegmentHeader* originalSegment,
        byte* originalCursor)
    {
        nuint originalUsedBytes = lane.UsedBytes;
        ArenaSegmentHeader* selected = lane.Current == null
            ? lane.First
            : lane.Current->Next;
        nuint requiredCapacity = checked(
            byteLength + alignment - 1);
        if (selected == null
            || selected->Capacity < requiredCapacity)
        {
            selected = InsertGrowthSegment(
                ref lane,
                requiredCapacity,
                scoped ? "scoped allocation growth" : "allocation growth");
        }

        byte* start = GetDataStart(selected);
        nuint aligned = AlignUp((nuint)start, alignment);
        byte* end = (byte*)checked(aligned + byteLength);
        nuint usedBytes = checked(
            originalUsedBytes
            + checked((nuint)(end - start)));
        lane.Current = selected;
        lane.Cursor = end;
        lane.End = start + selected->Capacity;
        lane.UsedBytes = usedBytes;
        return new ArenaReservation(
            scoped,
            originalSegment,
            originalCursor,
            originalUsedBytes,
            (IntPtr)aligned);
    }

    private ArenaSegmentHeader* InsertGrowthSegment(
        ref ArenaLane lane,
        nuint requiredCapacity,
        string operation)
    {
        nuint growth = DefaultSegmentBytes;
        if (lane.Current != null)
        {
            try
            {
                growth = checked(lane.Current->Capacity * 2);
            }
            catch (OverflowException)
            {
                growth = requiredCapacity;
            }
        }

        nuint capacity = Math.Max(
            requiredCapacity,
            Math.Max(DefaultSegmentBytes, growth));
        ArenaSegmentHeader* segment = AllocateSegment(
            capacity,
            operation,
            requiredCapacity);
        capacity = segment->Capacity;
        if (lane.Current == null)
        {
            segment->Next = lane.First;
            lane.First = segment;
            if (lane.Tail == null)
            {
                lane.Tail = segment;
            }
        }
        else
        {
            segment->Next = lane.Current->Next;
            lane.Current->Next = segment;
            if (lane.Tail == lane.Current)
            {
                lane.Tail = segment;
            }
        }

        _retainedBytes = checked(
            _retainedBytes + checked((long)segment->AllocationBytes));
        _segmentCount = checked(_segmentCount + 1);
        NativeOwnerHistory.Increment(ref _freshSegmentAllocationCount, ref _historyOverflowed);
        return segment;
    }

    private ArenaSegmentHeader* AppendSegment(
        ref ArenaLane lane,
        nuint capacity,
        string operation,
        bool alreadyReserved = false)
    {
        ArenaSegmentHeader* segment = AllocateSegment(
            capacity,
            operation,
            alreadyReserved: alreadyReserved);
        if (lane.Tail == null)
        {
            lane.First = segment;
        }
        else
        {
            lane.Tail->Next = segment;
        }

        lane.Tail = segment;
        _retainedBytes = checked(
            _retainedBytes + checked((long)segment->AllocationBytes));
        _segmentCount = checked(_segmentCount + 1);
        NativeOwnerHistory.Increment(ref _freshSegmentAllocationCount, ref _historyOverflowed);
        return segment;
    }

    private ArenaSegmentHeader* AllocateSegment(
        nuint capacity,
        string operation,
        nuint minimumCapacity = 0,
        bool alreadyReserved = false)
    {
        long allocationOrdinal = checked(_nextAllocationOrdinal + 1);
        nuint allocationBytes = NativeAlignedAllocation.GetBackingByteLength(checked(HeaderBytes + capacity));
        if (_budget is not null && !alreadyReserved)
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
            _nextAllocationOrdinal = allocationOrdinal;
            if (NativeMemoryTestHooks.ConsumeForcedFailure())
            {
                throw new NativeAllocationFailedException(allocationBytes, OwnerKind,
                    generation: unchecked((long)_generation), operation, _lifecycle);
            }
            memory = NativeMemory.AlignedAlloc(
                allocationBytes,
                SegmentAlignment);
            if (memory == null)
            {
                // AlignedAlloc reports native allocation failure with a null pointer.
#pragma warning disable CA2201
                throw new OutOfMemoryException();
#pragma warning restore CA2201
            }

            ArenaSegmentHeader* segment =
                (ArenaSegmentHeader*)memory;
            *segment = default;
            segment->DataStart = (byte*)segment + HeaderBytes;
            segment->Capacity = capacity;
            segment->AllocationBytes = allocationBytes;
            metricsEpoch = NativeMemoryAccounting.RecordAllocation(allocationBytes, zeroed: false);
            recorded = true;
            segment->MetricsEpoch = metricsEpoch;
            segment->AllocationOrdinal = allocationOrdinal;
            _budget?.Commit(allocationBytes, Id, allocationOrdinal: allocationOrdinal);
            acquired = true;
            return segment;
        }
        catch (OutOfMemoryException exception)
        {
            throw new NativeAllocationFailedException(
                allocationBytes,
                OwnerKind,
                generation: unchecked((long)_generation),
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
    private void RollBack(ArenaReservation reservation)
    {
        ref ArenaLane lane = ref (
            reservation.Scoped ? ref _scoped : ref _ordinary);
        lane.UsedBytes = reservation.OriginalUsedBytes;
        if (reservation.OriginalSegment == null)
        {
            ResetLane(ref lane);
            return;
        }

        lane.Current = reservation.OriginalSegment;
        lane.Cursor = reservation.OriginalCursor;
        lane.End = GetDataStart(lane.Current)
            + lane.Current->Capacity;
    }

    private nuint TrimLaneTail(
        ref ArenaLane lane,
        nuint byteBudget)
    {
        if (byteBudget == 0 || lane.Current == null)
        {
            return 0;
        }

        ArenaSegmentHeader* segment = lane.Current->Next;
        if (segment == null)
        {
            return 0;
        }

        lane.Current->Next = null;
        lane.Tail = lane.Current;
        nuint released = 0;
        ArenaSegmentHeader* retained = null;
        ArenaSegmentHeader* retainedTail = null;
        while (segment != null)
        {
            ArenaSegmentHeader* next = segment->Next;
            if (released < byteBudget)
            {
                released = checked(released + segment->AllocationBytes);
                FreeSegment(segment, NativeMemoryTraceKind.Trimmed);
            }
            else
            {
                segment->Next = null;
                if (retained == null)
                {
                    retained = segment;
                }
                else
                {
                    retainedTail->Next = segment;
                }

                retainedTail = segment;
            }

            segment = next;
        }

        lane.Current->Next = retained;
        if (retainedTail != null)
        {
            lane.Tail = retainedTail;
        }

        return released;
    }

    private void ValidateBoundary(string operation)
    {
        ValidateActive(operation);
        if (_activeBorrowCount != 0 || _initializerActive != 0)
        {
            ThrowBoundaryInUse(operation);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateActive(string operation)
    {
        ValidateThread(operation);
        if (_lifecycle != NativeOwnerLifecycle.Active)
        {
            ThrowDisposed(operation);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateThread(string operation)
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            ThrowWrongThread(operation);
        }
    }

    private static void ResetLane(ref ArenaLane lane)
    {
        lane.Current = lane.First;
        lane.Cursor = lane.First == null
            ? null
            : GetDataStart(lane.First);
        lane.End = lane.First == null
            ? null
            : lane.Cursor + lane.First->Capacity;
        lane.UsedBytes = 0;
    }

    private static long CountUsedBytes(ArenaLane lane) =>
        checked((long)lane.UsedBytes);

    private static int CountUnusedSegments(ArenaLane lane)
    {
        int count = 0;
        ArenaSegmentHeader* segment = lane.Current;
        if (segment != null
            && lane.Cursor == GetDataStart(segment))
        {
            count++;
        }

        segment = segment == null ? null : segment->Next;
        while (segment != null)
        {
            count++;
            segment = segment->Next;
        }

        return count;
    }

    private static void MarkDetached(ArenaLane lane)
    {
        ArenaSegmentHeader* segment = lane.First;
        while (segment != null)
        {
            segment->Detached = 1;
            NativeMemoryAccounting.RecordDetachedBytes(
                segment->AllocationBytes,
                segment->MetricsEpoch);
            segment = segment->Next;
        }
    }

    private void FreeAllSegments()
    {
        FreeLane(ref _ordinary);
        FreeLane(ref _scoped);
        _retainedBytes = 0;
        _segmentCount = 0;
    }

    private void FreeLane(ref ArenaLane lane, NativeMemoryTraceKind traceKind = NativeMemoryTraceKind.Released)
    {
        ArenaSegmentHeader* segment = lane.First;
        lane = default;
        while (segment != null)
        {
            ArenaSegmentHeader* next = segment->Next;
            FreeSegment(segment, traceKind);
            segment = next;
        }
    }

    private void FreeSegment(ArenaSegmentHeader* segment, NativeMemoryTraceKind traceKind = NativeMemoryTraceKind.Released)
    {
        if (segment->External != 0)
        {
            FreeExternalSegment(segment, traceKind);
            return;
        }
        nuint allocationBytes = segment->AllocationBytes;
        long metricsEpoch = segment->MetricsEpoch;
        bool detached = segment->Detached != 0;
        long allocationOrdinal = segment->AllocationOrdinal;
        NativeMemory.AlignedFree(segment);
        _budget?.Release(allocationBytes, Id, traceKind, allocationOrdinal);
        NativeMemoryAccounting.RecordFree(
            allocationBytes,
            detached,
            metricsEpoch);
        _retainedBytes -= checked((long)allocationBytes);
        _segmentCount--;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNestedInitializer() =>
        throw new InvalidOperationException(
            "NativeArena does not permit a nested initializer.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIncompleteInitialization(
        int initializedLength,
        int requiredLength) =>
        throw new InvalidOperationException(
            $"The native lease initializer wrote {initializedLength} of {requiredLength} required elements.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowBoundaryInUse(string operation) =>
        throw new NativeAllocationInUseException(
            "The arena cannot cross a lifetime boundary during an active callback or initializer.",
            OwnerKind,
            unchecked((long)_generation),
            unchecked((long)_generation),
            operation,
            _activeBorrowCount + _initializerActive,
            0,
            _lifecycle);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowDisposed(string operation) =>
        throw new NativeAllocationDisposedException(
            "The native arena is disposed.",
            OwnerKind,
            unchecked((long)_generation),
            unchecked((long)_generation),
            operation,
            _activeBorrowCount,
            allocationId: 0,
            _lifecycle);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowWrongThread(string operation) =>
        throw new NativeAllocationStateException(
            "NativeArena is confined to its construction thread.",
            OwnerKind,
            unchecked((long)_generation),
            unchecked((long)_generation),
            operation,
            _activeBorrowCount,
            allocationId: 0,
            _lifecycle);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowStale(
        ulong generation,
        ulong scopeEpoch,
        bool scoped,
        string operation) =>
        throw new NativeAllocationReturnedException(
            scoped
                ? "The scoped arena lease epoch is stale."
                : "The arena generation is stale.",
            OwnerKind,
            unchecked((long)(scoped ? scopeEpoch : generation)),
            unchecked((long)(scoped ? _scopeEpoch : _generation)),
            operation,
            _activeBorrowCount,
            allocationId: 0,
            _lifecycle);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint CalculateByteLength<T>(int length)
        where T : unmanaged
    {
        uint elementSize = (uint)Unsafe.SizeOf<T>();
        if (IntPtr.Size == 4)
        {
            ulong bytes = (ulong)(uint)length * elementSize;
            if (bytes > uint.MaxValue)
            {
                ThrowByteLengthOverflow();
            }
        }

        return (nuint)(uint)length * elementSize;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowByteLengthOverflow() =>
        throw new OverflowException(
            "The arena byte count exceeds native addressable storage.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint CalculateAlignment<T>()
        where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        return size >= IntPtr.Size
            ? (nuint)IntPtr.Size
            : size >= 4
                ? 4u
                : size >= 2
                    ? 2u
                    : 1u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* GetDataStart(
        ArenaSegmentHeader* segment) =>
        segment->DataStart;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint AlignUp(nuint value, nuint alignment)
    {
        nuint mask = alignment - 1;
        return checked(value + mask) & ~mask;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowEpochExhausted(string operation) =>
        throw new NativeAllocationStateException(
            "NativeArena exhausted its non-reusable lease epoch.",
            OwnerKind,
            unchecked((long)_generation),
            unchecked((long)_generation),
            operation,
            activeOperationCount: 0,
            allocationId: 0,
            _lifecycle);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0055", Justification = "Emergency native-memory cleanup supplements mandatory deterministic disposal.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "An emergency finalizer must never let cleanup exceptions terminate the process.")]
    ~NativeArenaKernel()
    {
        try
        {
            FreeAllSegments();
        }
        catch
        {
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ArenaLane
    {
        internal ArenaSegmentHeader* First;
        internal ArenaSegmentHeader* Tail;
        internal ArenaSegmentHeader* Current;
        internal byte* Cursor;
        internal byte* End;
        internal nuint UsedBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ArenaSegmentHeader
    {
        internal ArenaSegmentHeader* Next;
        internal nuint Capacity;
        internal nuint AllocationBytes;
        internal long MetricsEpoch;
        internal long AllocationOrdinal;
        internal int Detached;
        internal int External;
        internal byte* DataStart;
    }

    private readonly struct ArenaReservation
    {
        internal ArenaReservation(
            bool scoped,
            ArenaSegmentHeader* originalSegment,
            byte* originalCursor,
            nuint originalUsedBytes,
            IntPtr pointer)
        {
            Scoped = scoped;
            OriginalSegment = originalSegment;
            OriginalCursor = originalCursor;
            OriginalUsedBytes = originalUsedBytes;
            Pointer = pointer;
        }

        internal bool Scoped { get; }

        internal ArenaSegmentHeader* OriginalSegment { get; }

        internal byte* OriginalCursor { get; }

        internal nuint OriginalUsedBytes { get; }

        internal IntPtr Pointer { get; }
    }
}
