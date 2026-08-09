using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

internal sealed unsafe class NativeArenaKernel
{
    private const nuint DefaultSegmentBytes = 4_096;
    private const nuint SegmentAlignment = 64;
    private const string OwnerKind = "NativeArena";
    private static readonly nuint HeaderBytes = AlignUp(
        (nuint)sizeof(ArenaSegmentHeader),
        SegmentAlignment);

    private readonly NativeMemoryReturn _returnMemoryOnDispose;
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
    private int _segmentCount;

    internal NativeArenaKernel(
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose)
    {
        _returnMemoryOnDispose = returnMemoryOnDispose;
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
        ResetLane(ref _ordinary);
        ResetLane(ref _scoped);
        _generation = NextEpoch(_generation);
        _scopeEpoch = NextEpoch(_scopeEpoch);
    }

    internal void RecycleScoped()
    {
        ValidateBoundary(nameof(NativeArena.RecycleScoped));
        ResetLane(ref _scoped);
        _scopeEpoch = NextEpoch(_scopeEpoch);
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
            _freshSegmentAllocationCount);
    }

    internal nuint TrimRetainedMemory(nuint byteBudget)
    {
        ValidateBoundary(nameof(NativeArena.TrimRetainedMemory));
        _trimCallCount++;
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

        _trimmedBytes = checked(
            _trimmedBytes + checked((long)released));
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

        _lifecycle = NativeOwnerLifecycle.Disposed;
        _generation = NextEpoch(_generation);
        _scopeEpoch = NextEpoch(_scopeEpoch);
        if (_returnMemoryOnDispose
            == NativeMemoryReturn.ToNativeMemory)
        {
            FreeAllSegments();
            GC.SuppressFinalize(this);
            return;
        }

        MarkDetached(_ordinary);
        MarkDetached(_scoped);
        NativeMemoryTestHooks.RecordDetachedGeneration(
            NativeMemoryTestHooks.CurrentMetricsEpoch);
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
            operation);
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
            _retainedBytes + checked((long)capacity));
        _segmentCount = checked(_segmentCount + 1);
        _freshSegmentAllocationCount = checked(
            _freshSegmentAllocationCount + 1);
        return segment;
    }

    private ArenaSegmentHeader* AppendSegment(
        ref ArenaLane lane,
        nuint capacity,
        string operation)
    {
        ArenaSegmentHeader* segment = AllocateSegment(
            capacity,
            operation);
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
            _retainedBytes + checked((long)capacity));
        _segmentCount = checked(_segmentCount + 1);
        _freshSegmentAllocationCount = checked(
            _freshSegmentAllocationCount + 1);
        return segment;
    }

    private ArenaSegmentHeader* AllocateSegment(
        nuint capacity,
        string operation)
    {
        nuint allocationBytes = checked(HeaderBytes + capacity);
        if (NativeMemoryTestHooks.ConsumeForcedFailure())
        {
            throw new NativeAllocationFailedException(
                allocationBytes,
                OwnerKind,
                generation: unchecked((long)_generation),
                operation,
                _lifecycle);
        }

        void* memory = null;
        try
        {
            memory = NativeMemory.AlignedAlloc(
                allocationBytes,
                SegmentAlignment);
            if (memory == null)
            {
                throw new OutOfMemoryException();
            }

            ArenaSegmentHeader* segment =
                (ArenaSegmentHeader*)memory;
            *segment = default;
            segment->Capacity = capacity;
            segment->AllocationBytes = allocationBytes;
            segment->MetricsEpoch =
                NativeMemoryTestHooks.RecordAllocation(
                    allocationBytes,
                    zeroed: false);
            return segment;
        }
        catch (OutOfMemoryException exception)
        {
            if (memory != null)
            {
                NativeMemory.AlignedFree(memory);
            }

            throw new NativeAllocationFailedException(
                allocationBytes,
                OwnerKind,
                generation: unchecked((long)_generation),
                operation,
                _lifecycle,
                exception);
        }
        catch
        {
            if (memory != null)
            {
                NativeMemory.AlignedFree(memory);
            }

            throw;
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
                released = checked(released + segment->Capacity);
                FreeSegment(segment);
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
            NativeMemoryTestHooks.RecordDetachedBytes(
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

    private void FreeLane(ref ArenaLane lane)
    {
        ArenaSegmentHeader* segment = lane.First;
        lane = default;
        while (segment != null)
        {
            ArenaSegmentHeader* next = segment->Next;
            FreeSegment(segment);
            segment = next;
        }
    }

    private void FreeSegment(ArenaSegmentHeader* segment)
    {
        nuint capacity = segment->Capacity;
        nuint allocationBytes = segment->AllocationBytes;
        long metricsEpoch = segment->MetricsEpoch;
        bool detached = segment->Detached != 0;
        NativeMemory.AlignedFree(segment);
        NativeMemoryTestHooks.RecordFree(
            allocationBytes,
            detached,
            metricsEpoch);
        _retainedBytes -= checked((long)capacity);
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
        (byte*)segment + HeaderBytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint AlignUp(nuint value, nuint alignment)
    {
        nuint mask = alignment - 1;
        return checked(value + mask) & ~mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong NextEpoch(ulong epoch)
    {
        epoch++;
        return epoch == 0 ? 1 : epoch;
    }

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
        internal int Detached;
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
