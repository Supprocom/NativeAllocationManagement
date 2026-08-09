using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

internal sealed unsafe class NativePoolKernel<T>
    where T : unmanaged
{
    private const int SizeClassCount = 32;
    private const nuint SlabAlignment = 64;
    private const string OwnerKind = "NativePool";

    private readonly NativeMemoryReturn _returnMemoryOnDispose;
    private readonly int _ownerThreadId;
    private readonly int[] _freeHeads;
    private Slab[] _slabs;
    private uint _nonEmptyFreeClasses;
    private NativeOwnerLifecycle _lifecycle;
    private int _slabCount;
    private int _unusedHead = -1;
    private int _returnedSlabIndex = -1;
    private int _returnedLogicalLength = -1;
    private int _liveLeaseCount;
    private long _leaseTokenCounter;
    private long _requestedBytes;
    private long _retainedBytes;
    private long _trimmedBytes;
    private long _trimCallCount;
    private long _freshSegmentAllocationCount;

    internal NativePoolKernel(
        int preLease,
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(preLease);
        _returnMemoryOnDispose = returnMemoryOnDispose;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _freeHeads = new int[SizeClassCount];
        Array.Fill(_freeHeads, -1);
        _slabs = new Slab[4];
        _lifecycle = NativeOwnerLifecycle.Active;

        try
        {
            if (preLease != 0)
            {
                nuint bytes = CalculateByteLength(preLease);
                int index = AddSlab(preLease, bytes, "typed reservation");
                PushFreeList(index);
            }

            if (preAllocateBytes != 0)
            {
                nuint elementSize = (nuint)Unsafe.SizeOf<T>();
                nuint elements = preAllocateBytes / elementSize;
                int capacity = elements > int.MaxValue
                    ? int.MaxValue
                    : (int)elements;
                int index = AddSlab(
                    capacity,
                    preAllocateBytes,
                    "raw reservation");
                PushFreeList(index);
            }
        }
        catch
        {
            FreeAll();
            throw;
        }
    }

    internal NativeOwnerLifecycle Lifecycle => _lifecycle;

    internal int LiveLeaseCount => _liveLeaseCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Pooled<T> Rent(
        int length,
        NativeLeaseInitializer<T> initializer)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateOwner(nameof(NativePool<T>.Rent));
        long token = TakeLeaseToken();

        int slabIndex = TakeOrAddSlab(length);

        ref Slab slab = ref _slabs[slabIndex];
        slab.State = SlabState.Initializing;
        slab.Token = token;
        _liveLeaseCount++;
        _requestedBytes = checked(
            _requestedBytes + checked((long)CalculateByteLength(length)));

        int initializedLength = 0;
        try
        {
            NativeLeaseWriter<T> writer = new(
                slab.Pointer,
                length,
                ref initializedLength);
            initializer(writer);
            if (initializedLength != length)
            {
                ThrowIncompleteInitialization(
                    initializedLength,
                    length);
            }

            slab.State = SlabState.Leased;
            return new Pooled<T>(
                this,
                slabIndex,
                token,
                slab.Pointer,
                length,
                slab.Capacity);
        }
        catch
        {
            RollBackRent(slabIndex, length);
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal IntPtr EnterBorrow(
        int slabIndex,
        long token,
        int logicalLength,
        string operation)
    {
        ValidateOwner(operation);
        ref Slab slab = ref ValidateLease(
            slabIndex,
            token,
            operation);
        if ((uint)logicalLength > (uint)slab.Capacity)
        {
            ThrowInvalidLength(logicalLength, slab.Capacity);
        }

        slab.BorrowCount++;
        return slab.Pointer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitBorrow(int slabIndex)
    {
        ref Slab slab = ref _slabs[slabIndex];
        slab.BorrowCount--;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void Return(
        int slabIndex,
        long token,
        int length)
    {
        ValidateOwner(nameof(Pooled<T>.Dispose));
        ref Slab slab = ref ValidateLease(
            slabIndex,
            token,
            nameof(Pooled<T>.Dispose));
        if (slab.BorrowCount != 0)
        {
            ThrowActiveBorrow();
        }

        slab.State = SlabState.Free;
        _liveLeaseCount--;
        _requestedBytes -= checked((long)CalculateByteLength(length));
        CacheReturnedSlab(slabIndex, length);
    }

    internal NativeOwnerStatistics GetStatistics()
    {
        ValidateOwner(nameof(GetStatistics));
        int freeCount = 0;
        for (int index = 0; index < _slabCount; index++)
        {
            if (_slabs[index].State == SlabState.Free)
            {
                freeCount++;
            }
        }

        return new NativeOwnerStatistics(
            _lifecycle,
            Generation: 0,
            _requestedBytes,
            _retainedBytes,
            RetiredBytes: 0,
            SegmentCount: _slabCount - CountUnused(),
            AvailableSegmentCount: freeCount,
            RetiredSegmentCount: 0,
            _trimmedBytes,
            _trimCallCount,
            _freshSegmentAllocationCount);
    }

    internal nuint TrimRetainedMemory() =>
        TrimRetainedMemory(nuint.MaxValue);

    internal nuint TrimRetainedMemory(nuint byteBudget)
    {
        ValidateOwner(nameof(NativePool<T>.TrimRetainedMemory));
        _trimCallCount++;
        nuint released = 0;
        RebuildFreeListsWithoutFreedSlabs();
        for (int index = 0; index < _slabCount; index++)
        {
            ref Slab slab = ref _slabs[index];
            if (slab.State != SlabState.Free
                || released >= byteBudget)
            {
                continue;
            }

            nuint bytes = slab.AllocationBytes;
            FreeSlab(ref slab);
            slab.State = SlabState.Unused;
            slab.Next = _unusedHead;
            _unusedHead = index;
            released = checked(released + bytes);
        }

        _trimmedBytes = checked(
            _trimmedBytes + checked((long)released));
        RebuildFreeListsWithoutFreedSlabs();
        return released;
    }

    internal void Dispose()
    {
        ValidateThread(nameof(Dispose));
        if (_lifecycle == NativeOwnerLifecycle.Disposed)
        {
            return;
        }

        if (_liveLeaseCount != 0)
        {
            ThrowLiveLease();
        }

        _lifecycle = NativeOwnerLifecycle.Disposed;
        if (_returnMemoryOnDispose == NativeMemoryReturn.ToNativeMemory)
        {
            FreeAll();
            GC.SuppressFinalize(this);
            return;
        }

        MarkDetached();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int TakeOrAddSlab(int length)
    {
        if (_returnedLogicalLength == length)
        {
            int slabIndex = _returnedSlabIndex;
            _returnedSlabIndex = -1;
            _returnedLogicalLength = -1;
            return slabIndex;
        }

        return TakeOrAddSlabSlow(length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int TakeOrAddSlabSlow(int length)
    {
        if (_returnedSlabIndex >= 0)
        {
            int slabIndex = _returnedSlabIndex;
            _returnedSlabIndex = -1;
            _returnedLogicalLength = -1;
            PushFreeList(slabIndex);
        }

        int selectedSlab = TakeFreeSlow(length);
        if (selectedSlab >= 0)
        {
            return selectedSlab;
        }

        int capacity = RoundCapacity(length);
        return AddSlab(
            capacity,
            CalculateByteLength(capacity),
            "rent growth");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int TakeFreeSlow(int length)
    {
        int firstClass = GetSizeClass(length);
        uint eligibleClasses = _nonEmptyFreeClasses
            & (uint.MaxValue << firstClass);
        while (eligibleClasses != 0)
        {
            int sizeClass = BitOperations.TrailingZeroCount(
                eligibleClasses);
            int previous = -1;
            int current = _freeHeads[sizeClass];
            while (current >= 0)
            {
                ref Slab slab = ref _slabs[current];
                int next = slab.Next;
                if (slab.Capacity >= length)
                {
                    if (previous < 0)
                    {
                        _freeHeads[sizeClass] = next;
                        if (next < 0)
                        {
                            _nonEmptyFreeClasses &=
                                ~(1u << sizeClass);
                        }
                    }
                    else
                    {
                        _slabs[previous].Next = next;
                    }

                    slab.Next = -1;
                    return current;
                }

                previous = current;
                current = next;
            }

            eligibleClasses &= ~(1u << sizeClass);
        }

        return -1;
    }

    private int AddSlab(
        int capacity,
        nuint allocationBytes,
        string operation)
    {
        int index = ReserveSlabSlot();
        void* memory = null;
        long metricsEpoch = 0;
        try
        {
            if (allocationBytes != 0)
            {
                if (NativeMemoryTestHooks.ConsumeForcedFailure())
                {
                    throw CreateAllocationFailure(
                        allocationBytes,
                        operation);
                }

                memory = NativeMemory.AlignedAlloc(
                    allocationBytes,
                    SlabAlignment);
                if (memory == null)
                {
                    throw CreateAllocationFailure(
                        allocationBytes,
                        operation);
                }

                metricsEpoch = NativeMemoryTestHooks.RecordAllocation(
                    allocationBytes,
                    zeroed: false);
            }

            _slabs[index] = new Slab
            {
                Pointer = (IntPtr)memory,
                AllocationBytes = allocationBytes,
                Capacity = capacity,
                Next = -1,
                Token = 0,
                State = SlabState.Free,
                MetricsEpoch = metricsEpoch
            };
            _retainedBytes = checked(
                _retainedBytes + checked((long)allocationBytes));
            if (allocationBytes != 0)
            {
                _freshSegmentAllocationCount++;
            }
            return index;
        }
        catch (OutOfMemoryException exception)
        {
            if (memory != null)
            {
                NativeMemory.AlignedFree(memory);
            }

            ReleaseReservedSlot(index);
            throw new NativeAllocationFailedException(
                allocationBytes,
                OwnerKind,
                generation: 0,
                operation,
                _lifecycle,
                exception);
        }
        catch
        {
            if (memory != null)
            {
                NativeMemory.AlignedFree(memory);
                if (metricsEpoch != 0)
                {
                    NativeMemoryTestHooks.RecordFree(
                        allocationBytes,
                        detached: false,
                        metricsEpoch);
                }
            }

            ReleaseReservedSlot(index);
            throw;
        }
    }

    private int ReserveSlabSlot()
    {
        if (_unusedHead >= 0)
        {
            int index = _unusedHead;
            _unusedHead = _slabs[index].Next;
            return index;
        }

        if (_slabCount == _slabs.Length)
        {
            Array.Resize(ref _slabs, checked(_slabs.Length * 2));
        }

        return _slabCount++;
    }

    private void ReleaseReservedSlot(int index)
    {
        if (index == _slabCount - 1)
        {
            _slabCount--;
            return;
        }

        _slabs[index] = new Slab
        {
            State = SlabState.Unused,
            Next = _unusedHead
        };
        _unusedHead = index;
    }

    private void RollBackRent(int slabIndex, int length)
    {
        ref Slab slab = ref _slabs[slabIndex];
        slab.State = SlabState.Free;
        _liveLeaseCount--;
        _requestedBytes -= checked((long)CalculateByteLength(length));
        CacheReturnedSlab(slabIndex, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Slab ValidateLease(
        int slabIndex,
        long token,
        string operation)
    {
        if ((uint)slabIndex >= (uint)_slabCount)
        {
            ThrowStaleIndex(slabIndex, operation);
        }

        ref Slab slab = ref _slabs[slabIndex];
        if (slab.State != SlabState.Leased
            || slab.Token != token)
        {
            ThrowStaleToken(
                slabIndex,
                token,
                slab.Token,
                slab.BorrowCount,
                operation);
        }

        return ref slab;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateOwner(string operation)
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
        if (Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            return;
        }

        ThrowWrongThread(operation);
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowActiveBorrow() =>
        throw new InvalidOperationException(
            "A pooled lease cannot return during an active callback.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLiveLease() =>
        throw new InvalidOperationException(
            "NativePool cannot dispose while a pooled lease is active.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidLength(
        int logicalLength,
        int capacity) =>
        throw new ArgumentOutOfRangeException(
            nameof(logicalLength),
            logicalLength,
            $"The logical length must be between zero and {capacity}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowStaleIndex(int slabIndex, string operation) =>
        throw new NativeAllocationReturnedException(
            "The pooled slab index is stale.",
            OwnerKind,
            generation: 0,
            currentGeneration: 0,
            operation,
            activeOperationCount: 0,
            allocationId: slabIndex,
            _lifecycle);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowStaleToken(
        int slabIndex,
        long token,
        long currentToken,
        int borrowCount,
        string operation) =>
        throw new NativeAllocationReturnedException(
            "The pooled lease token is stale.",
            OwnerKind,
            generation: token,
            currentGeneration: currentToken,
            operation,
            borrowCount,
            slabIndex,
            _lifecycle);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowDisposed(string operation) =>
        throw new NativeAllocationDisposedException(
            "The native pool is disposed.",
            OwnerKind,
            generation: 0,
            currentGeneration: 0,
            operation,
            activeOperationCount: 0,
            allocationId: 0,
            _lifecycle);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowWrongThread(string operation) =>
        throw new NativeAllocationStateException(
            "NativePool is confined to its construction thread.",
            OwnerKind,
            generation: 0,
            currentGeneration: 0,
            operation,
            activeOperationCount: 0,
            allocationId: 0,
            _lifecycle);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CacheReturnedSlab(int slabIndex, int logicalLength)
    {
        int displaced = _returnedSlabIndex;
        _returnedSlabIndex = slabIndex;
        _returnedLogicalLength = logicalLength;
        _slabs[slabIndex].Next = -1;
        if (displaced >= 0)
        {
            PushFreeList(displaced);
        }
    }

    private void PushFreeList(int slabIndex)
    {
        ref Slab slab = ref _slabs[slabIndex];
        int sizeClass = GetSizeClass(slab.Capacity);
        slab.Next = _freeHeads[sizeClass];
        _freeHeads[sizeClass] = slabIndex;
        _nonEmptyFreeClasses |= 1u << sizeClass;
    }

    private void RebuildFreeListsWithoutFreedSlabs()
    {
        Array.Fill(_freeHeads, -1);
        _nonEmptyFreeClasses = 0;
        _returnedSlabIndex = -1;
        _returnedLogicalLength = -1;
        for (int index = 0; index < _slabCount; index++)
        {
            if (_slabs[index].State == SlabState.Free)
            {
                PushFreeList(index);
            }
        }
    }

    private int CountUnused()
    {
        int count = 0;
        for (int index = 0; index < _slabCount; index++)
        {
            if (_slabs[index].State == SlabState.Unused)
            {
                count++;
            }
        }

        return count;
    }

    private void MarkDetached()
    {
        for (int index = 0; index < _slabCount; index++)
        {
            ref Slab slab = ref _slabs[index];
            if (slab.State == SlabState.Unused
                || slab.AllocationBytes == 0)
            {
                continue;
            }

            slab.Detached = true;
            NativeMemoryTestHooks.RecordDetachedBytes(
                slab.AllocationBytes,
                slab.MetricsEpoch);
        }

        NativeMemoryTestHooks.RecordDetachedGeneration(
            NativeMemoryTestHooks.CurrentMetricsEpoch);
    }

    private void FreeAll()
    {
        for (int index = 0; index < _slabCount; index++)
        {
            FreeSlab(ref _slabs[index]);
        }

        _retainedBytes = 0;
    }

    private void FreeSlab(ref Slab slab)
    {
        if (slab.AllocationBytes == 0)
        {
            slab = default;
            return;
        }

        nuint bytes = slab.AllocationBytes;
        long metricsEpoch = slab.MetricsEpoch;
        bool detached = slab.Detached;
        NativeMemory.AlignedFree((void*)slab.Pointer);
        NativeMemoryTestHooks.RecordFree(
            bytes,
            detached,
            metricsEpoch);
        _retainedBytes -= checked((long)bytes);
        slab = default;
    }

    private NativeAllocationFailedException CreateAllocationFailure(
        nuint bytes,
        string operation) =>
        new(
            bytes,
            OwnerKind,
            generation: 0,
            operation,
            _lifecycle);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint CalculateByteLength(int length)
    {
        if (IntPtr.Size == 4)
        {
            ulong bytes = (ulong)(uint)length
                * (uint)Unsafe.SizeOf<T>();
            if (bytes > uint.MaxValue)
            {
                throw new OverflowException(
                    "The pooled byte count exceeds native addressable storage.");
            }
        }

        return (nuint)(uint)length * (nuint)Unsafe.SizeOf<T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundCapacity(int length)
    {
        if (length <= 1)
        {
            return length;
        }

        uint rounded = BitOperations.RoundUpToPowerOf2((uint)length);
        return rounded == 0 || rounded > int.MaxValue
            ? length
            : (int)rounded;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetSizeClass(int capacity)
    {
        if (capacity <= 1)
        {
            return 0;
        }

        return Math.Min(
            SizeClassCount - 1,
            BitOperations.Log2((uint)(capacity - 1)) + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long TakeLeaseToken()
    {
        if (_leaseTokenCounter == long.MaxValue)
        {
            ThrowLeaseTokenExhausted();
        }

        return ++_leaseTokenCounter;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLeaseTokenExhausted() =>
        throw new InvalidOperationException(
            "NativePool exhausted its lease-token authority.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIncompleteInitialization(
        int initializedLength,
        int requiredLength) =>
        throw new InvalidOperationException(
            $"The native lease initializer wrote {initializedLength} of {requiredLength} required elements.");

    ~NativePoolKernel()
    {
        try
        {
            FreeAll();
        }
        catch
        {
        }
    }

    private struct Slab
    {
        internal IntPtr Pointer;
        internal nuint AllocationBytes;
        internal int Capacity;
        internal int Next;
        internal long Token;
        internal int BorrowCount;
        internal SlabState State;
        internal long MetricsEpoch;
        internal bool Detached;
    }

    private enum SlabState : byte
    {
        Unused,
        Free,
        Initializing,
        Leased
    }
}
