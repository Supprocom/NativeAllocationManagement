using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Owns a bounded set of independently reusable fixed-shape native slots on one thread.</summary>
/// <typeparam name="T">The unmanaged value type stored in every slot.</typeparam>
/// <remarks>
/// All backing and metadata are acquired at preparation; rent, move and return never grow them.
/// Empty pages can be trimmed explicitly without refill. A surviving slot retains its complete page.
/// This owner uses a local single-shape free list, not the variable-shape NativePool size classes.
/// </remarks>
public sealed unsafe class NativePreparedPool<T> : IDisposable
    where T : unmanaged
{
    private const string OwnerKind = "NativePreparedPool";
    private NativeMemoryBudget? _budget;
    private readonly int _ownerThreadId;
    private readonly Slot[] _slabs = [];
    private readonly Page[] _pages = [];
    private readonly NativePoolPreparation _preparation;
    private int _freeHead = -1;
    private NativeOwnerLifecycle _lifecycle;
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

    /// <summary>Gets the stable process-local allocator identity.</summary>
    public long Id { get; }

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
    public NativePreparedPool(NativePoolPreparation preparation, NativeMemoryBudget? budget)
    {
        Id = NativeOwnerIdentity.Next();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preparation.SlotCount, nameof(preparation));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preparation.SlotCapacity, nameof(preparation));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preparation.SlotsPerPage, nameof(preparation));
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
        int preparedSlotCount = 0;
        try
        {
            NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativePreparedPool.Preparation", 1, "metadata banks");
            _slabs = new Slot[preparation.SlotCount];
            _pages = new Page[1 + (preparation.SlotCount - 1) / preparation.SlotsPerPage];
            _lifecycle = NativeOwnerLifecycle.Active;
            foreach (ref Page page in _pages.AsSpan())
            {
                long ordinal = checked(++_nextAllocationOrdinal);
                NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativePreparedPool.Preparation",
                    checked((int)ordinal + 1), "page acquisition");
                int firstSlot = preparedSlotCount;
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
                        int index = preparedSlotCount++;
                        _slabs[index] = new Slot
                        {
                            Pointer = (IntPtr)((byte*)memory + checked(stride * (nuint)offset)),
                            State = SlotState.Free,
                            Next = index - 1
                        };
                        _freeHead = index;
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
            NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativePreparedPool.Preparation", _pages.Length + 2, "prepared authority");
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


    /// <summary>Captures actual thread-confined storage state without retaining native authority.</summary>
    public NativeOwnerDiagnosticSnapshot CaptureDiagnosticSnapshot() => GetDiagnosticSnapshot();

    /// <summary>Captures the original bounds, actual retained pages and complete prepared histories.</summary>
    public NativePreparedPoolStatistics CapturePreparedSnapshot() => GetPreparedStatistics();

    internal NativeOwnerLifecycle CurrentLifecycle => _lifecycle;
    internal int CurrentAllocationRecordCountForTest => _liveLeaseCount;
    internal (int Slabs, int AvailableSlabs, int Bumps, int OwnerSegments)
        CurrentBankCapacitiesForTest => (_slabs.Length, _slabs.Length, 0, _pages.Length);
    internal long[] CurrentSegmentOrdinalsForTest => GetSegmentOrdinals();

    internal int CurrentGenerationActiveOperationsForTest
    {
        get
        {
            int count = 0;
            foreach (ref readonly Slot slab in _slabs.AsSpan())
            {
                count = checked(count + slab.BorrowCount + (slab.State == SlotState.Initializing ? 1 : 0));
            }
            return count;
        }
    }

    internal int CurrentInitializationCountForTest
    {
        get
        {
            int count = 0;
            foreach (ref readonly Slot slab in _slabs.AsSpan())
            {
                count += slab.State == SlotState.Initializing ? 1 : 0;
            }
            return count;
        }
    }


    /// <summary>Initializes and publishes one bounded fixed-shape slot.</summary>
    /// <param name="length">The complete initialized element count, at most the prepared capacity.</param>
    /// <param name="initializer">The bounded complete initializer.</param>
    /// <returns>The owning token-bound slot capability.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PreparedPooled<T> Rent(int length, NativeLeaseInitializer<T> initializer)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateOwner(nameof(Rent));
        if (CheckCapacity(length) != NativePoolExhaustionReason.None)
        {
            ThrowCapacityExhausted();
        }
        long token = TakeLeaseToken();
        int index = _freeHead;
        _freeHead = _slabs[index].Next;
        return InitializeSlot(index, token, length, initializer);
    }

    /// <summary>Publishes a prepared slot, returning false only for expected shape or slot exhaustion.</summary>
    /// <param name="length">The required initialized element count.</param>
    /// <param name="initializer">The complete bounded initializer, not invoked on capacity refusal.</param>
    /// <param name="lease">The initialized owning capability on success, or default on refusal.</param>
    /// <param name="reason">The exact expected capacity refusal, or None on success.</param>
    /// <returns>True only after initialization and publication.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRent(int length, NativeLeaseInitializer<T> initializer,
        out PreparedPooled<T> lease, out NativePoolExhaustionReason reason)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateOwner(nameof(TryRent));
        reason = CheckCapacity(length);
        if (reason != NativePoolExhaustionReason.None)
        {
            lease = default;
            return false;
        }
        long token = TakeLeaseToken();
        int index = _freeHead;
        _freeHead = _slabs[index].Next;
        lease = InitializeSlot(index, token, length, initializer);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativePoolExhaustionReason CheckCapacity(int length)
    {
        if (length > _preparation.SlotCapacity)
        {
            IncrementHistory(ref _rejectedPreparedShapes);
            return NativePoolExhaustionReason.ShapeExceeded;
        }
        if (_freeHead < 0)
        {
            IncrementHistory(ref _rejectedPreparedFull);
            return NativePoolExhaustionReason.NoAvailableSlot;
        }
        return NativePoolExhaustionReason.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PreparedPooled<T> InitializeSlot(int index, long token, int length,
        NativeLeaseInitializer<T> initializer)
    {
        ref Slot slot = ref _slabs[index];
        slot.State = SlotState.Initializing;
        slot.Token = token;
        _liveLeaseCount++;
        _peakOccupiedSlots = Math.Max(_peakOccupiedSlots, _liveLeaseCount);
        int initializedLength = 0;
        try
        {
            NativeLeaseWriter<T> writer = new(slot.Pointer, length, ref initializedLength);
            initializer(writer);
            if (initializedLength != length)
            {
                ThrowIncompleteInitialization(initializedLength, length);
            }
            _requestedBytes += checked((long)CalculateByteLength(length));
            _peakInitializedPayloadBytes = Math.Max(_peakInitializedPayloadBytes, _requestedBytes);
            slot.State = SlotState.Leased;
            IncrementHistory(ref _successfulPreparedRents);
            return new PreparedPooled<T>(this, index, token, length, _preparation.SlotCapacity);
        }
        catch
        {
            IncrementHistory(ref _preparedInitializerFailures);
            slot.State = SlotState.Free;
            _liveLeaseCount--;
            slot.Next = _freeHead;
            _freeHead = index;
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal IntPtr EnterBorrow(int index, long token, string operation)
    {
        ValidateOwner(operation);
        ref Slot slot = ref ValidateLease(index, token, operation);
        slot.BorrowCount++;
        return slot.Pointer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitBorrow(int index) => _slabs[index].BorrowCount--;

    internal long Move(int index, long token)
    {
        ValidateOwner(nameof(PreparedPooled<T>.Move));
        ref Slot slot = ref ValidateLease(index, token, nameof(PreparedPooled<T>.Move));
        if (slot.BorrowCount != 0)
        {
            ThrowActiveMove();
        }
        long nextToken = TakeLeaseToken();
        slot.Token = nextToken;
        if (_budget is { TraceEnabled: true } budget)
        {
            Page page = _pages[index / _preparation.SlotsPerPage];
            budget.RecordOwnershipTransition(NativeMemoryTraceKind.Moved, Id,
                nextToken, CalculateByteLength(_preparation.SlotCapacity), page.Ordinal);
        }
        return nextToken;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void Return(int index, long token, int length)
    {
        ValidateOwner(nameof(PreparedPooled<T>.Dispose));
        ref Slot slot = ref ValidateLease(index, token, nameof(PreparedPooled<T>.Dispose));
        if (slot.BorrowCount != 0)
        {
            ThrowActiveBorrow();
        }
        slot.State = SlotState.Free;
        _liveLeaseCount--;
        _requestedBytes -= checked((long)CalculateByteLength(length));
        slot.Next = _freeHead;
        _freeHead = index;
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
            DetachedNativeBytes = 0,
            PeakOutstandingNativeBytes = _peakRetainedBytes,
            InitializedPayloadBytes = _requestedBytes,
            PeakInitializedPayloadBytes = _peakInitializedPayloadBytes,
            UsableCapacityBytes = counts.UsableBytes
        };
    }

    internal NativeOwnerDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        ValidateThread(nameof(NativePreparedPool<T>.CaptureDiagnosticSnapshot));
        var counts = GetStorageCounts();
        NativeOwnerDiagnosticSnapshot snapshot = new(
            _lifecycle, 0, 0, NativeMemoryAccounting.CurrentMetricsEpoch,
            _liveLeaseCount, 0, 0,
            _lifecycle == NativeOwnerLifecycle.Active ? _freeHead : -1,
            -1, counts.Retained,
            _lifecycle == NativeOwnerLifecycle.Active ? counts.Available : 0,
            0, 0, 0, 0, 0, false)
        {
            OwnerId = Id,
            Model = NativeOwnerModel.ThreadConfinedPool,
            HistoryOverflowed = _historyOverflowed,
            OutstandingNativeBytes = _retainedBytes,
            DetachedNativeBytes = 0,
            PeakOutstandingNativeBytes = _peakRetainedBytes,
            InitializedPayloadBytes = _requestedBytes,
            PeakInitializedPayloadBytes = _peakInitializedPayloadBytes
        };
        GC.KeepAlive(this);
        return snapshot;
    }


    private (int Retained, int Available, long UsableBytes) GetStorageCounts()
    {
        int pages = 0;
        int availablePages = 0;
        long usableBytes = 0;
        foreach (ref readonly Page page in _pages.AsSpan())
        {
            if (page.AllocationBytes == 0)
            {
                continue;
            }
            pages++;
            availablePages += IsPageIdle(page) ? 1 : 0;
            usableBytes += checked((long)page.SlotCount * _preparation.SlotCapacity * Unsafe.SizeOf<T>());
        }
        return (pages, availablePages, usableBytes);
    }

    /// <summary>Frees all idle pages without refilling prepared capacity.</summary>
    public nuint TrimRetainedMemory() => TrimRetainedMemory(nuint.MaxValue);

    /// <summary>Frees whole idle pages until the requested byte target is reached.</summary>
    /// <param name="bytesToRelease">The minimum release target, subject to whole-page boundaries and live slots.</param>
    /// <returns>The actual physically released page extent.</returns>
    public nuint TrimRetainedMemoryByBytes(nuint bytesToRelease) => TrimRetainedMemory(bytesToRelease);

    /// <summary>Frees idle pages for one typed request budget, retaining page granularity.</summary>
    /// <param name="leaseLength">The nonnegative element count to use as a release target.</param>
    /// <returns>The actual physically released page extent.</returns>
    public nuint TrimRetainedMemoryByLeaseSize(int leaseLength = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leaseLength);
        return TrimRetainedMemory(CalculateByteLength(leaseLength));
    }

    private nuint TrimRetainedMemory(nuint byteBudget)
    {
        ValidateOwner(nameof(TrimRetainedMemory));
        IncrementHistory(ref _trimCallCount);
        nuint released = 0;
        foreach (ref Page page in _pages.AsSpan())
        {
            if (released >= byteBudget || page.AllocationBytes == 0 || !IsPageIdle(page))
            {
                continue;
            }
            released = checked(released + page.AllocationBytes);
            _slabs.AsSpan(page.FirstSlot, page.SlotCount).Clear();
            FreePage(ref page, trimmed: true);
        }
        NativeOwnerHistory.Add(ref _trimmedBytes, checked((long)released), ref _historyOverflowed);
        _freeHead = -1;
        for (int index = 0; index < _slabs.Length; index++)
        {
            if (_slabs[index].State == SlotState.Free)
            {
                _slabs[index].Next = _freeHead;
                _freeHead = index;
            }
        }
        return released;
    }

    /// <summary>Ends worker use and transfers cleanup-only authority to a coordinator.</summary>
    public void Retire()
    {
        ValidateOwner(nameof(Retire));
        // Initializing and borrowed slots are included in the live count. Return
        // cannot decrement it while borrowed, so no redundant slot scan is needed.
        if (_liveLeaseCount != 0)
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


    /// <summary>Closes the prepared pool after all initializing and published leases return.</summary>
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
        FreeAll();
        GC.SuppressFinalize(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Slot ValidateLease(
        int slabIndex,
        long token,
        string operation)
    {
        if ((uint)slabIndex >= (uint)_slabs.Length)
        {
            ThrowStaleIndex(slabIndex, operation);
        }

        ref Slot slab = ref _slabs[slabIndex];
        if (slab.State != SlotState.Leased
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
            "NativePreparedPool cannot dispose while a pooled lease is active.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLiveRetirementState() =>
        throw new InvalidOperationException(
            "NativePreparedPool cannot retire while a lease, initializer, or callback is active.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRetiredDispose() =>
        throw new InvalidOperationException(
            "NativePreparedPool.Dispose cannot release retired storage. Use ReleaseRetiredStorage on the coordinator thread.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidRetiredCleanup(int state) =>
        throw new InvalidOperationException(
            state == 0
                ? "NativePreparedPool must retire before coordinator cleanup."
                : "NativePreparedPool retired storage was already released.");

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
            "NativePreparedPool is confined to its construction thread.",
            OwnerKind,
            generation: 0,
            currentGeneration: 0,
            operation,
            activeOperationCount: 0,
            allocationId: 0,
            _lifecycle);


    private void FreeAll()
    {
        // Slot authority is managed metadata; physical ownership lives only in pages.
        Array.Clear(_slabs);
        _freeHead = -1;
        foreach (ref Page page in _pages.AsSpan())
        {
            FreePage(ref page);
        }
        _retainedBytes = 0;
        _budget = null;
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
            "NativePreparedPool exhausted its lease-token authority.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIncompleteInitialization(
        int initializedLength,
        int requiredLength) =>
        throw new InvalidOperationException(
            $"The native lease initializer wrote {initializedLength} of {requiredLength} required elements.");

    /// <summary>Returns abandoned prepared pages through emergency cleanup.</summary>
    /// <remarks>Call Dispose or ReleaseRetiredStorage for deterministic native release; finalization is not a scheduling or memory-ceiling guarantee.</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0055", Justification = "Emergency native-memory cleanup supplements mandatory deterministic disposal.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "An emergency finalizer must never let cleanup exceptions terminate the process.")]
    ~NativePreparedPool()
    {
        try
        {
            FreeAll();
        }
        catch
        {
        }
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct Slot
    {
        internal IntPtr Pointer;
        internal long Token;
        internal int Next;
        internal int BorrowCount;
        internal SlotState State;
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
        foreach (ref readonly Slot slab in _slabs.AsSpan(page.FirstSlot, page.SlotCount))
        {
            if (slab.State != SlotState.Free || slab.BorrowCount != 0)
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

    internal NativePreparedPoolStatistics GetPreparedStatistics()
    {
        ValidateThread(nameof(NativePreparedPool<T>.CapturePreparedSnapshot));
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
        long bankBytes = checked((long)_slabs.Length * Unsafe.SizeOf<Slot>()
            + (long)_pages.Length * Unsafe.SizeOf<Page>());
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

    private long[] GetSegmentOrdinals()
    {
        ValidateThread(nameof(GetSegmentOrdinals));
        long[] result = new long[GetStorageCounts().Retained];
        int index = 0;
        foreach (ref readonly Page page in _pages.AsSpan())
        {
            if (page.AllocationBytes != 0)
            {
                result[index++] = page.Ordinal;
            }
        }
        GC.KeepAlive(this);
        return result;
    }

    private enum SlotState : byte
    {
        Unused,
        Free,
        Initializing,
        Leased
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCapacityExhausted() =>
        throw new InvalidOperationException("Prepared pool capacity is exhausted; use TryRent for expected exhaustion.");
}
