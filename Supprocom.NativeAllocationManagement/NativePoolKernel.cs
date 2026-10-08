using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

public sealed unsafe partial class NativePool<T>
    where T : unmanaged
{
    private const int SizeClassCount = 32;
    private const nuint SlabAlignment = 64;
    private const string OwnerKind = "NativePool";

    private readonly NativeMemoryReturn _returnMemoryOnDispose;
    private NativeMemoryBudget? _budget;
    private readonly int _ownerThreadId;
    private readonly int[] _freeHeads = [];
    private Slab[] _slabs = [];
    private readonly Page[] _pages = [];
    private readonly NativePoolPreparation _preparation;
    private uint _nonEmptyFreeClasses;
    private NativeOwnerLifecycle _lifecycle;
    private int _slabCount;
    private int _unusedHead = -1;
    private int _returnedSlabIndex = -1;
    private int _returnedLogicalLength = -1;
    private int _liveLeaseCount;
    private int _retirementState;
    private long _leaseTokenCounter;
    private long _requestedBytes;
    private long _peakInitializedPayloadBytes;
    private long _retainedBytes;
    private long _trimmedBytes;
    private long _trimCallCount;
    private long _freshSegmentAllocationCount;
    private long _nextAllocationOrdinal;
    private int _peakOccupiedSlots;
    private long _peakRetainedBytes;
    private long _successfulPreparedRents;
    private long _rejectedPreparedShapes;
    private long _rejectedPreparedFull;
    private long _preparedInitializerFailures;
    private bool _historyOverflowed;

    private NativePool(
        int preLease,
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose,
        NativeMemoryBudget? budget,
        bool requireBudget)
    {
        if (requireBudget) ArgumentNullException.ThrowIfNull(budget);
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        Id = NativeOwnerIdentity.Next();
        ArgumentOutOfRangeException.ThrowIfNegative(preLease);
        _returnMemoryOnDispose = returnMemoryOnDispose;
        _budget = budget;
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

    /// <summary>Prepares all fixed-shape slot metadata and backing pages without subsequent growth.</summary>
    /// <remarks>
    /// Preparation admits every page before acquiring backing or metadata.
    /// Dispose returns backing deterministically after all leases return.
    /// Trim removes idle pages without implicit refill; original bounds remain diagnostic.
    /// Prepared slots are packed using the CLR element stride, without cache-line
    /// padding or an additional SIMD-address alignment guarantee.
    /// </remarks>
    /// <param name="preparation">The positive simultaneous shape and page bounds.</param>
    /// <param name="budget">The optional shared backing domain, or null for no byte ceiling.</param>
    public NativePool(NativePoolPreparation preparation, NativeMemoryBudget? budget)
    {
        Id = NativeOwnerIdentity.Next();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preparation.SlotCount, nameof(preparation));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preparation.SlotCapacity, nameof(preparation));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preparation.SlotsPerPage, nameof(preparation));
        _returnMemoryOnDispose = NativeMemoryReturn.ToNativeMemory;
        _budget = budget;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _preparation = preparation;
        nuint slotBytes = CalculateByteLength(preparation.SlotCapacity);
        // This owner is thread-confined and exposes ordinary typed spans, not
        // aligned SIMD addresses. Consecutive slots use the CLR element stride;
        // cache-line padding and an over-aligned backend add no authority.
        nuint stride = slotBytes;
        nuint totalBytes = checked(stride * (nuint)preparation.SlotCount);
        _ = checked((long)totalBytes);
        _budget?.Reserve(totalBytes, Id);
        nuint remainingReservation = totalBytes;
        bool prepared = false;
        try
        {
            NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativePool.Preparation", 1, "metadata banks");
            _freeHeads = new int[SizeClassCount];
            Array.Fill(_freeHeads, -1);
            _slabs = new Slab[preparation.SlotCount];
            _pages = new Page[1 + (preparation.SlotCount - 1) / preparation.SlotsPerPage];
            _lifecycle = NativeOwnerLifecycle.Active;
            foreach (ref Page page in _pages.AsSpan())
            {
                long ordinal = checked(++_nextAllocationOrdinal);
                NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativePool.Preparation",
                    checked((int)ordinal + 1), "page acquisition");
                int firstSlot = _slabCount;
                int slots = Math.Min(preparation.SlotsPerPage, preparation.SlotCount - firstSlot);
                nuint bytes = checked(stride * (nuint)slots);
                void* memory = null;
                bool committed = false;
                long epoch = 0;
                try
                {
                    long prospectiveBytes = checked(_retainedBytes + checked((long)bytes));
                    if (NativeMemoryTestHooks.ConsumeForcedFailure())
                    {
                        throw CreateAllocationFailure(bytes, "page preparation");
                    }
                    memory = NativeMemory.Alloc(bytes);
                    if (memory == null)
                    {
                        throw CreateAllocationFailure(bytes, "page preparation");
                    }
                    _peakRetainedBytes = Math.Max(_peakRetainedBytes, prospectiveBytes);
                    epoch = NativeMemoryAccounting.RecordAllocation(bytes, zeroed: false);
                    _budget?.Commit(bytes, Id, NativeMemoryTraceKind.PageAcquired, ordinal);
                    remainingReservation -= bytes;
                    committed = true;
                    page = new Page((IntPtr)memory, bytes, firstSlot, slots,
                        epoch, ordinal);
                    _retainedBytes = prospectiveBytes;
                    IncrementHistory(ref _freshSegmentAllocationCount);
                    for (int offset = 0; offset < slots; offset++)
                    {
                        int index = _slabCount++;
                        _slabs[index] = new Slab
                        {
                            Pointer = (IntPtr)((byte*)memory + checked(stride * (nuint)offset)),
                            Capacity = preparation.SlotCapacity,
                            State = SlabState.Free,
                            Next = -1
                        };
                        PushFreeList(index);
                    }
                }
                finally
                {
                    if (!committed && memory != null)
                    {
                        NativeMemory.Free(memory);
                        NativeMemoryAccounting.RecordFree(bytes, detached: false, epoch);
                    }
                }
            }
            NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativePool.Preparation", _pages.Length + 2, "prepared authority");
            _budget?.RecordPreparation(totalBytes, Id);
            prepared = true;
        }
        catch (OutOfMemoryException exception)
        {
            throw new NativeAllocationFailedException(totalBytes, OwnerKind, 0,
                "page preparation", _lifecycle, exception);
        }
        finally
        {
            if (remainingReservation != 0)
            {
                _budget?.Cancel(remainingReservation, Id);
            }
            if (!prepared)
            {
                FreeAll();
            }
        }
    }

    internal int CurrentGenerationActiveOperationsForTest
    {
        get
        {
            int count = 0;
            foreach (ref readonly Slab slab in _slabs.AsSpan(0, _slabCount))
            {
                count = checked(count + slab.BorrowCount + (slab.State == SlabState.Initializing ? 1 : 0));
            }
            return count;
        }
    }

    internal int CurrentInitializationCountForTest
    {
        get
        {
            int count = 0;
            foreach (ref readonly Slab slab in _slabs.AsSpan(0, _slabCount))
            {
                count += slab.State == SlabState.Initializing ? 1 : 0;
            }
            return count;
        }
    }

    /// <summary>Initializes and publishes one pooled slab lease.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Pooled<T> Rent(
        int length,
        NativeLeaseInitializer<T> initializer)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateOwner(nameof(NativePool<T>.Rent));
        long token = TakeLeaseToken();

        int slabIndex = TakeOrAddSlab(length);

        return _preparation.SlotCount == 0
            ? InitializeSlab(slabIndex, token, length, initializer)
            : InitializePreparedSlab(slabIndex, token, length, initializer);
    }

    /// <summary>Publishes a prepared slot, returning false only for expected shape or slot exhaustion.</summary>
    /// <remarks>Invalid ownership and initialization failures throw; no partial lease is published.</remarks>
    /// <param name="length">The required initialized elements, at most the prepared shape.</param>
    /// <param name="initializer">The bounded complete initializer, invoked only after acquiring a slot.</param>
    /// <param name="lease">The initialized owning capability on success; default on exhaustion.</param>
    /// <param name="reason">The exact expected capacity refusal, or None on success.</param>
    /// <returns>True only after initialization and publication.</returns>
    public bool TryRent(int length, NativeLeaseInitializer<T> initializer,
        out Pooled<T> lease, out NativePoolExhaustionReason reason)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateOwner(nameof(NativePool<T>.TryRent));
        ValidatePrepared();
        if (length > _preparation.SlotCapacity)
        {
            IncrementHistory(ref _rejectedPreparedShapes);
            lease = default;
            reason = NativePoolExhaustionReason.ShapeExceeded;
            return false;
        }
        if (_returnedSlabIndex < 0 && _nonEmptyFreeClasses == 0)
        {
            IncrementHistory(ref _rejectedPreparedFull);
            lease = default;
            reason = NativePoolExhaustionReason.NoAvailableSlot;
            return false;
        }
        long token = TakeLeaseToken();
        int index = TakeOrAddSlab(length);
        lease = InitializePreparedSlab(index, token, length, initializer);
        reason = NativePoolExhaustionReason.None;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Pooled<T> InitializePreparedSlab(int slabIndex, long token, int length,
        NativeLeaseInitializer<T> initializer)
    {
        _peakOccupiedSlots = Math.Max(_peakOccupiedSlots, _liveLeaseCount + 1);
        try
        {
            Pooled<T> result = InitializeSlab(slabIndex, token, length, initializer);
            IncrementHistory(ref _successfulPreparedRents);
            return result;
        }
        catch
        {
            IncrementHistory(ref _preparedInitializerFailures);
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Pooled<T> InitializeSlab(int slabIndex, long token, int length,
        NativeLeaseInitializer<T> initializer)
    {

        ref Slab slab = ref _slabs[slabIndex];
        slab.State = SlabState.Initializing;
        slab.Token = token;
        _liveLeaseCount++;

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

            _requestedBytes += checked((long)CalculateByteLength(length));
            _peakInitializedPayloadBytes = Math.Max(_peakInitializedPayloadBytes, _requestedBytes);
            slab.State = SlabState.Leased;
            return new Pooled<T>(
                this,
                slabIndex,
                token,
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
        string operation)
    {
        ValidateOwner(operation);
        ref Slab slab = ref ValidateLease(
            slabIndex,
            token,
            operation);
        // Pooled carries the immutable fully initialized length supplied only by
        // InitializeSlab. Process bounds a shorter prefix at its public boundary;
        // ordinary bounded access needs no redundant physical-capacity comparison.
        slab.BorrowCount++;
        return slab.Pointer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitBorrow(int slabIndex)
    {
        ref Slab slab = ref _slabs[slabIndex];
        slab.BorrowCount--;
    }

    internal long Move(int slabIndex, long token)
    {
        ValidateOwner(nameof(Pooled<T>.Move));
        ref Slab slab = ref ValidateLease(slabIndex, token, nameof(Pooled<T>.Move));
        if (slab.BorrowCount != 0)
        {
            ThrowActiveMove();
        }

        // Take a never-reused identity before changing either the slot or binding.
        // Exhaustion therefore leaves the original lease able to return its slot.
        long nextToken = TakeLeaseToken();
        slab.Token = nextToken;
        if (_budget is { TraceEnabled: true } budget)
        {
            Page page = _preparation.SlotCount == 0
                ? default : _pages[slabIndex / _preparation.SlotsPerPage];
            nuint slotExtent = _preparation.SlotCount == 0
                ? slab.AllocationBytes : page.AllocationBytes / (nuint)page.SlotCount;
            budget.RecordOwnershipTransition(NativeMemoryTraceKind.Moved, Id,
                nextToken, slotExtent,
                _preparation.SlotCount == 0 ? slab.Ordinal : page.Ordinal);
        }
        return nextToken;
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

    /// <summary>Reads the current typed slab state.</summary>
    public NativeOwnerStatistics GetStatistics()
    {
        ValidateOwner(nameof(GetStatistics));
        var counts = GetStorageCounts();

        return new NativeOwnerStatistics(
            _lifecycle,
            Generation: 0,
            _requestedBytes,
            _retainedBytes,
            RetiredBytes: 0,
            SegmentCount: counts.Retained,
            AvailableSegmentCount: counts.Available,
            RetiredSegmentCount: 0,
            _trimmedBytes,
            _trimCallCount,
            _freshSegmentAllocationCount)
        {
            OwnerId = Id,
            Model = NativeOwnerModel.ThreadConfinedPool,
            HistoryOverflowed = _historyOverflowed,
            OutstandingNativeBytes = _retainedBytes,
            DetachedNativeBytes = _lifecycle == NativeOwnerLifecycle.Disposed
                && _returnMemoryOnDispose == NativeMemoryReturn.ToGarbageCollector ? _retainedBytes : 0,
            PeakOutstandingNativeBytes = _peakRetainedBytes,
            InitializedPayloadBytes = _requestedBytes,
            PeakInitializedPayloadBytes = _peakInitializedPayloadBytes,
            UsableCapacityBytes = counts.UsableBytes
        };
    }

    internal NativeOwnerDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        ValidateThread(nameof(NativePool<T>.CaptureDiagnosticSnapshot));
        var counts = GetStorageCounts();
        NativeOwnerDiagnosticSnapshot snapshot = new(
            _lifecycle, 0, 0, NativeMemoryAccounting.CurrentMetricsEpoch,
            _liveLeaseCount, 0, 0,
            _lifecycle == NativeOwnerLifecycle.Active ? _returnedSlabIndex : -1,
            -1, counts.Retained,
            _lifecycle == NativeOwnerLifecycle.Active ? counts.Available : 0,
            0, 0, 0, 0, 0, false)
        {
            OwnerId = Id,
            Model = NativeOwnerModel.ThreadConfinedPool,
            HistoryOverflowed = _historyOverflowed,
            OutstandingNativeBytes = _retainedBytes,
            DetachedNativeBytes = _lifecycle == NativeOwnerLifecycle.Disposed
                && _returnMemoryOnDispose == NativeMemoryReturn.ToGarbageCollector ? _retainedBytes : 0,
            PeakOutstandingNativeBytes = _peakRetainedBytes,
            InitializedPayloadBytes = _requestedBytes,
            PeakInitializedPayloadBytes = _peakInitializedPayloadBytes
        };
        GC.KeepAlive(this);
        return snapshot;
    }

    private (int Retained, int Available, long UsableBytes) GetStorageCounts()
    {
        if (_preparation.SlotCount != 0)
        {
            int pages = 0;
            int availablePages = 0;
            long pageUsableBytes = 0;
            foreach (ref readonly Page page in _pages.AsSpan())
            {
                if (page.AllocationBytes == 0)
                {
                    continue;
                }
                pages++;
                availablePages += IsPageIdle(page) ? 1 : 0;
                pageUsableBytes += checked((long)page.SlotCount * _preparation.SlotCapacity * Unsafe.SizeOf<T>());
            }
            return (pages, availablePages, pageUsableBytes);
        }
        int retained = 0;
        int available = 0;
        long usableBytes = 0;
        foreach (ref readonly Slab slab in _slabs.AsSpan(0, _slabCount))
        {
            if (slab.State == SlabState.Unused || slab.AllocationBytes == 0)
            {
                continue;
            }
            retained++;
            available += slab.State == SlabState.Free ? 1 : 0;
            usableBytes = checked(usableBytes + (long)slab.Capacity * Unsafe.SizeOf<T>());
        }
        return (retained, available, usableBytes);
    }

    /// <summary>Frees all idle slabs.</summary>
    public nuint TrimRetainedMemory() =>
        TrimRetainedMemory(nuint.MaxValue);

    internal nuint TrimRetainedMemory(nuint byteBudget)
    {
        ValidateOwner(nameof(NativePool<T>.TrimRetainedMemory));
        IncrementHistory(ref _trimCallCount);
        nuint released = 0;
        if (_preparation.SlotCount != 0)
        {
            foreach (ref Page page in _pages.AsSpan())
            {
                if (released >= byteBudget || page.AllocationBytes == 0 || !IsPageIdle(page))
                {
                    continue;
                }
                released = checked(released + page.AllocationBytes);
                foreach (ref Slab slot in _slabs.AsSpan(page.FirstSlot, page.SlotCount))
                {
                    slot = default;
                }
                FreePage(ref page, trimmed: true);
            }
            NativeOwnerHistory.Add(ref _trimmedBytes, checked((long)released), ref _historyOverflowed);
            RebuildFreeListsWithoutFreedSlabs();
            return released;
        }
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

        NativeOwnerHistory.Add(ref _trimmedBytes, checked((long)released), ref _historyOverflowed);
        RebuildFreeListsWithoutFreedSlabs();
        return released;
    }

    /// <summary>Ends worker-thread use and converts this pool into cleanup-only authority.</summary>
    public void Retire()
    {
        ValidateOwner(nameof(NativePool<T>.Retire));
        if (_liveLeaseCount != 0 || HasInFlightSlab())
        {
            ThrowLiveRetirementState();
        }

        _lifecycle = NativeOwnerLifecycle.Returned;
        Volatile.Write(ref _retirementState, 1);
    }

    /// <summary>Releases one retired pool from a coordinator thread.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "Coordinator-owned retirement cleanup disarms the emergency finalizer.")]
    public void ReleaseRetiredStorage()
    {
        int prior = Interlocked.CompareExchange(
            ref _retirementState,
            2,
            1);
        if (prior != 1)
        {
            ThrowInvalidRetiredCleanup(prior);
        }

        _lifecycle = NativeOwnerLifecycle.Disposed;
        FreeAll();
        GC.SuppressFinalize(this);
    }

    /// <summary>Closes the pool after all leases return.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "ToNativeMemory disposal suppresses finalization after physical cleanup. The explicit ToGarbageCollector policy must retain emergency finalization until the detached backing is actually freed.")]
    public void Dispose()
    {
        ValidateThread(nameof(Dispose));
        if (Volatile.Read(ref _retirementState) != 0)
        {
            ThrowRetiredDispose();
        }

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

    private bool HasInFlightSlab()
    {
#pragma warning disable HLQ013 // The slab's writable ref and active prefix are required.
        for (int index = 0; index < _slabCount; index++)
        {
            ref Slab slab = ref _slabs[index];
            if (slab.BorrowCount != 0
                || slab.State is SlabState.Initializing
                    or SlabState.Leased)
            {
                return true;
            }
        }
#pragma warning restore HLQ013

        return false;
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

        if (_preparation.SlotCount != 0)
        {
            if (length > _preparation.SlotCapacity)
            {
                IncrementHistory(ref _rejectedPreparedShapes);
            }
            else
            {
                IncrementHistory(ref _rejectedPreparedFull);
            }
            throw new InvalidOperationException("Prepared pool capacity is exhausted; use TryRent for expected exhaustion.");
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
        nuint backingBytes = NativeAlignedAllocation.GetBackingByteLength(allocationBytes);
        long ordinal = allocationBytes == 0 ? 0 : checked(_nextAllocationOrdinal + 1);
        _budget?.Reserve(backingBytes, Id);
        int index = -1;
        void* memory = null;
        long metricsEpoch = 0;
        bool acquired = false;
        try
        {
            long prospectiveBytes = checked(_retainedBytes + checked((long)backingBytes));
            index = ReserveSlabSlot();
            if (allocationBytes != 0)
            {
                _nextAllocationOrdinal = ordinal;
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

                _peakRetainedBytes = Math.Max(_peakRetainedBytes, prospectiveBytes);
                metricsEpoch = NativeMemoryAccounting.RecordAllocation(
                    backingBytes,
                    zeroed: false);
            }

            _slabs[index] = new Slab
            {
                Pointer = (IntPtr)memory,
                AllocationBytes = backingBytes,
                Capacity = capacity,
                Next = -1,
                Token = 0,
                State = SlabState.Free,
                MetricsEpoch = metricsEpoch,
                Ordinal = ordinal
            };
            _retainedBytes = prospectiveBytes;
            if (allocationBytes != 0)
            {
                IncrementHistory(ref _freshSegmentAllocationCount);
            }
            _budget?.Commit(backingBytes, Id, allocationOrdinal: ordinal);
            acquired = true;
            return index;
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
                    if (metricsEpoch != 0)
                    {
                        NativeMemoryAccounting.RecordFree(backingBytes, detached: false, metricsEpoch);
                    }
                }

                if (index >= 0)
                {
                    ReleaseReservedSlot(index);
                }
                _budget?.Cancel(backingBytes, Id);
            }
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
    private static void ThrowActiveMove() =>
        throw new InvalidOperationException(
            "A pooled lease cannot move during an active callback; the source remains owning.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLiveLease() =>
        throw new InvalidOperationException(
            "NativePool cannot dispose while a pooled lease is active.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLiveRetirementState() =>
        throw new InvalidOperationException(
            "NativePool cannot retire while a lease, initializer, or callback is active.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRetiredDispose() =>
        throw new InvalidOperationException(
            "NativePool.Dispose cannot release retired storage. Use ReleaseRetiredStorage on the coordinator thread.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidRetiredCleanup(int state) =>
        throw new InvalidOperationException(
            state == 0
                ? "NativePool must retire before coordinator cleanup."
                : "NativePool retired storage was already released.");

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

    private void MarkDetached()
    {
#pragma warning disable HLQ013 // The slab's writable ref and active prefix are required.
        for (int index = 0; index < _slabCount; index++)
        {
            ref Slab slab = ref _slabs[index];
            if (slab.State == SlabState.Unused
                || slab.AllocationBytes == 0)
            {
                continue;
            }

            slab.Detached = true;
            NativeMemoryAccounting.RecordDetachedBytes(
                slab.AllocationBytes,
                slab.MetricsEpoch);
        }
#pragma warning restore HLQ013

        NativeMemoryAccounting.RecordDetachedGeneration(
            NativeMemoryAccounting.CurrentMetricsEpoch);
    }

    private void FreeAll()
    {
#pragma warning disable HLQ013 // Free only initialized slabs by writable reference.
        for (int index = 0; index < _slabCount; index++)
        {
            FreeSlab(ref _slabs[index]);
        }
#pragma warning restore HLQ013

        foreach (ref Page page in _pages.AsSpan())
        {
            FreePage(ref page);
        }

        _retainedBytes = 0;
        _budget = null;
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
        long ordinal = slab.Ordinal;
        NativeMemory.AlignedFree((void*)slab.Pointer);
        _budget?.Release(bytes, Id, allocationOrdinal: ordinal);
        NativeMemoryAccounting.RecordFree(
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

    /// <summary>Returns abandoned or GC-detached backing through emergency cleanup.</summary>
    /// <remarks>Call Dispose or ReleaseRetiredStorage for deterministic native release; finalization is not a scheduling or memory-ceiling guarantee.</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0055", Justification = "Emergency native-memory cleanup supplements mandatory deterministic disposal.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "An emergency finalizer must never let cleanup exceptions terminate the process.")]
    ~NativePool()
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
        internal long Ordinal;
        internal bool Detached;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Page
    {
        internal Page(IntPtr pointer, nuint allocationBytes, int firstSlot,
            int slotCount, long metricsEpoch, long ordinal)
        {
            Pointer = pointer;
            AllocationBytes = allocationBytes;
            FirstSlot = firstSlot;
            SlotCount = slotCount;
            MetricsEpoch = metricsEpoch;
            Ordinal = ordinal;
        }
        internal readonly IntPtr Pointer;
        internal readonly nuint AllocationBytes;
        internal readonly int FirstSlot;
        internal readonly int SlotCount;
        internal readonly long MetricsEpoch;
        internal readonly long Ordinal;
    }

    private bool IsPageIdle(in Page page)
    {
        foreach (ref readonly Slab slab in _slabs.AsSpan(page.FirstSlot, page.SlotCount))
        {
            if (slab.State != SlabState.Free || slab.BorrowCount != 0)
            {
                return false;
            }
        }
        return true;
    }

    private void FreePage(ref Page page, bool trimmed = false)
    {
        if (page.AllocationBytes == 0)
        {
            return;
        }
        nuint bytes = page.AllocationBytes;
        NativeMemory.Free((void*)page.Pointer);
        _budget?.Release(bytes, Id,
            trimmed ? NativeMemoryTraceKind.Trimmed : NativeMemoryTraceKind.Released,
            page.Ordinal);
        NativeMemoryAccounting.RecordFree(bytes, detached: false, page.MetricsEpoch);
        _retainedBytes -= checked((long)bytes);
        page = default;
    }

    private void ValidatePrepared()
    {
        if (_preparation.SlotCount == 0)
        {
            throw new InvalidOperationException("This pool has no fixed-shape page preparation contract.");
        }
    }

    internal NativePreparedPoolStatistics GetPreparedStatistics()
    {
        ValidateThread(nameof(NativePool<T>.CapturePreparedSnapshot));
        ValidatePrepared();
        int pages = 0;
        int slots = 0;
        foreach (ref readonly Page page in _pages.AsSpan())
        {
            if (page.AllocationBytes != 0)
            {
                pages++;
                slots += page.SlotCount;
            }
        }
        long bankBytes = checked((long)_slabs.Length * Unsafe.SizeOf<Slab>()
            + (long)_pages.Length * Unsafe.SizeOf<Page>() + (long)_freeHeads.Length * sizeof(int));
        NativePreparedPoolStatistics result = new(Id, _lifecycle, _preparation,
            pages, slots, _liveLeaseCount, _peakOccupiedSlots, _retainedBytes, _peakRetainedBytes,
            _successfulPreparedRents, _rejectedPreparedShapes, _rejectedPreparedFull,
            _preparedInitializerFailures, bankBytes,
            checked((long)(slots - _liveLeaseCount) * _preparation.SlotCapacity * Unsafe.SizeOf<T>()),
            _historyOverflowed);
        GC.KeepAlive(this);
        return result;
    }

    private void IncrementHistory(ref long counter) =>
        NativeOwnerHistory.Increment(ref counter, ref _historyOverflowed);

    internal long[] GetSegmentOrdinals()
    {
        ValidateThread(nameof(GetSegmentOrdinals));
        long[] result = new long[GetStorageCounts().Retained];
        int index = 0;
        if (_preparation.SlotCount == 0)
        {
            foreach (ref readonly Slab slab in _slabs.AsSpan(0, _slabCount))
            {
                if (slab.AllocationBytes != 0)
                {
                    result[index++] = slab.Ordinal;
                }
            }
            Array.Sort(result);
        }
        else
        {
            foreach (ref readonly Page page in _pages.AsSpan())
            {
                if (page.AllocationBytes != 0)
                {
                    result[index++] = page.Ordinal;
                }
            }
        }
        GC.KeepAlive(this);
        return result;
    }

    private enum SlabState : byte
    {
        Unused,
        Free,
        Initializing,
        Leased
    }
}
