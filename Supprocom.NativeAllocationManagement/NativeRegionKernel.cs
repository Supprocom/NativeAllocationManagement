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

    private readonly NativeMemoryReturn _returnMemoryOnDispose;
    private readonly int _ownerThreadId;
    private NativeOwnerLifecycle _lifecycle;
    private RegionSegmentHeader* _firstSegment;
    private RegionSegmentHeader* _currentSegment;
    private byte* _currentCursor;
    private byte* _currentEnd;
    private long _requestedBytes;
    private long _retainedBytes;
    private int _segmentCount;
    private int _activeBorrowCount;
    private long _freshSegmentAllocationCount;

    internal NativeRegionKernel(
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose)
    {
        _returnMemoryOnDispose = returnMemoryOnDispose;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _lifecycle = NativeOwnerLifecycle.Active;

        if (preAllocateBytes != 0)
        {
            AppendSegment(
                preAllocateBytes,
                "declaration reservation");
        }
    }

    internal NativeOwnerLifecycle Lifecycle => _lifecycle;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Local<T> LeaseInitialized<T>(
        int length,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateActive(nameof(NativeRegion.Lease));

        nuint byteLength = CalculateByteLength<T>(length);
        nuint alignment = CalculateAlignment<T>();
        long requestedBytes = checked(
            _requestedBytes + checked((long)byteLength));
        RegionReservation reservation = Reserve(
            byteLength,
            alignment);
        Initialize(reservation, length, initializer);
        _requestedBytes = requestedBytes;
        return new Local<T>(
            this,
            reservation.Pointer,
            length);
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

        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw CreateState(
                operation,
                "NativeRegion is confined to its construction thread.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnterBorrow(string operation)
    {
        ValidateActive(operation);
        _activeBorrowCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitBorrow() => _activeBorrowCount--;

    internal NativeOwnerStatistics GetStatistics()
    {
        ValidateActive(nameof(GetStatistics));
        return new NativeOwnerStatistics(
            _lifecycle,
            Generation: 0,
            _requestedBytes,
            _retainedBytes,
            RetiredBytes: 0,
            _segmentCount,
            AvailableSegmentCount: 0,
            RetiredSegmentCount: 0,
            TrimmedBytes: 0,
            TrimCallCount: 0,
            _freshSegmentAllocationCount);
    }

    internal void Dispose()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
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
            ThrowActiveBorrow();
        }

        _lifecycle = NativeOwnerLifecycle.Disposed;
        _requestedBytes = 0;
        if (_returnMemoryOnDispose == NativeMemoryReturn.ToNativeMemory)
        {
            FreeSegments();
            GC.SuppressFinalize(this);
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
        return AppendSegment(capacity, operation);
    }

    private RegionSegmentHeader* AppendSegment(
        nuint capacity,
        string operation)
    {
        RegionSegmentHeader* segment = AllocateSegment(
            capacity,
            operation);
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
            _retainedBytes + checked((long)capacity));
        _segmentCount = checked(_segmentCount + 1);
        _freshSegmentAllocationCount = checked(
            _freshSegmentAllocationCount + 1);
        return segment;
    }

    private RegionSegmentHeader* AllocateSegment(
        nuint capacity,
        string operation)
    {
        nuint allocationBytes = checked(HeaderBytes + capacity);
        if (NativeMemoryTestHooks.ConsumeForcedFailure())
        {
            throw new NativeAllocationFailedException(
                allocationBytes,
                OwnerKind,
                generation: 0,
                operation,
                _lifecycle);
        }

        try
        {
            void* memory = NativeMemory.AlignedAlloc(
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

            RegionSegmentHeader* segment =
                (RegionSegmentHeader*)memory;
            *segment = default;
            segment->Capacity = capacity;
            segment->AllocationBytes = allocationBytes;
            try
            {
                segment->MetricsEpoch =
                    NativeMemoryTestHooks.RecordAllocation(
                        allocationBytes,
                        zeroed: false);
            }
            catch
            {
                NativeMemory.AlignedFree(memory);
                throw;
            }

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
            NativeMemoryTestHooks.RecordDetachedBytes(
                segment->AllocationBytes,
                segment->MetricsEpoch);
            segment = segment->Next;
        }

        NativeMemoryTestHooks.RecordDetachedGeneration(
            NativeMemoryTestHooks.CurrentMetricsEpoch);
    }

    private void FreeSegments()
    {
        RegionSegmentHeader* segment = _firstSegment;
        _firstSegment = null;
        _currentSegment = null;
        _currentCursor = null;
        _currentEnd = null;
        _retainedBytes = 0;
        _segmentCount = 0;
        while (segment != null)
        {
            RegionSegmentHeader* next = segment->Next;
            nuint allocationBytes = segment->AllocationBytes;
            long metricsEpoch = segment->MetricsEpoch;
            bool detached = segment->Detached != 0;
            NativeMemory.AlignedFree(segment);
            NativeMemoryTestHooks.RecordFree(
                allocationBytes,
                detached,
                metricsEpoch);
            segment = next;
        }
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
            activeOperationCount: 0,
            allocationId: 0,
            _lifecycle);

    private NativeAllocationDisposedException CreateDisposed(
        string operation) =>
        new(
            "The native region is disposed.",
            OwnerKind,
            generation: 0,
            currentGeneration: 0,
            operation,
            activeOperationCount: 0,
            allocationId: 0,
            _lifecycle);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowActiveBorrow() =>
        throw new InvalidOperationException(
            "NativeRegion cannot dispose during a bounded access callback.");

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
    }

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
