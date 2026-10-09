using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Page = Supprocom.NativeAllocationManagement.NativePreparedPoolStorage.Page;
using Slot = Supprocom.NativeAllocationManagement.NativePreparedPoolStorage.Slot;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Owns a bounded set of independently reusable fixed-shape native slots on one thread.</summary>
/// <typeparam name="T">The unmanaged value type stored in every slot.</typeparam>
/// <remarks>
/// All backing and metadata are acquired at preparation; rent, move and return never grow them.
/// Empty pages can be trimmed explicitly without refill. A surviving slot retains its complete page.
/// This owner uses a local single-shape free list, not the variable-shape NativePool size classes.
/// </remarks>
public sealed unsafe class NativePreparedPool<T> : NativePreparedPoolBase, IDisposable
    where T : unmanaged
{
    /// <summary>Gets the stable process-local allocator identity.</summary>
    public long Id => _id;

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
        : base(preparation, budget, Unsafe.SizeOf<T>())
    {
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
                count = checked(count + slab.BorrowCount + (slab.Next == NativePreparedPoolStorage.Initializing ? 1 : 0));
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
                count += slab.Next == NativePreparedPoolStorage.Initializing ? 1 : 0;
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

    /// <summary>Fills and publishes one bounded slot without an initializer callback.</summary>
    /// <param name="length">The complete initialized element count, at most the prepared capacity.</param>
    /// <param name="value">The value written to every element before the lease is published.</param>
    /// <returns>The owning token-bound slot capability.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PreparedPooled<T> Rent(int length, T value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateOwner(nameof(Rent));
        if (CheckCapacity(length) != NativePoolExhaustionReason.None)
        {
            ThrowCapacityExhausted();
        }
        long token = TakeLeaseToken();
        return InitializeFilledSlot(_freeHead, token, length, value);
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

    /// <summary>Fills a slot without a callback, returning false only for shape or slot exhaustion.</summary>
    /// <param name="length">The required initialized element count.</param>
    /// <param name="value">The value written to every element on successful admission.</param>
    /// <param name="lease">The fully initialized owning capability on success, or default on refusal.</param>
    /// <param name="reason">The exact expected capacity refusal, or None on success.</param>
    /// <returns>True only after complete fill and publication.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRent(int length, T value,
        out PreparedPooled<T> lease, out NativePoolExhaustionReason reason)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateOwner(nameof(TryRent));
        reason = CheckCapacity(length);
        if (reason != NativePoolExhaustionReason.None)
        {
            lease = default;
            return false;
        }
        long token = TakeLeaseToken();
        lease = InitializeFilledSlot(_freeHead, token, length, value);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PreparedPooled<T> InitializeFilledSlot(int index, long token, int length, T value)
    {
        ref Slot slot = ref _slabs[index];
        // T is unmanaged and this bounded fill cannot invoke user code. Leave
        // the slot free until every value is written: no callback writer,
        // initializing frame or incomplete-prefix rollback is needed.
        new Span<T>((void*)slot.Pointer, length).Fill(value);
        _freeHead = slot.Next;
        slot.Token = token;
        slot.Next = NativePreparedPoolStorage.Leased;
        _liveLeaseCount++;
        _peakOccupiedSlots = Math.Max(_peakOccupiedSlots, _liveLeaseCount);
        _requestedBytes += checked((long)CalculateByteLength(length));
        _peakInitializedPayloadBytes = Math.Max(_peakInitializedPayloadBytes, _requestedBytes);
        IncrementHistory(ref _successfulPreparedRents);
        return new PreparedPooled<T>(this, index, token, length, _preparation.SlotCapacity);
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
        slot.Next = NativePreparedPoolStorage.Initializing;
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
            slot.Next = NativePreparedPoolStorage.Leased;
            IncrementHistory(ref _successfulPreparedRents);
            return new PreparedPooled<T>(this, index, token, length, _preparation.SlotCapacity);
        }
        catch
        {
            IncrementHistory(ref _preparedInitializerFailures);
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
        _liveLeaseCount--;
        _requestedBytes -= checked((long)CalculateByteLength(length));
        slot.Next = _freeHead;
        _freeHead = index;
    }

    /// <summary>Reads the current typed slab state.</summary>
    public NativeOwnerStatistics GetStatistics()
    {
        NativeOwnerStatistics result = GetStatisticsCore(Unsafe.SizeOf<T>());
        GC.KeepAlive(this);
        return result;
    }

    internal NativeOwnerDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        NativeOwnerDiagnosticSnapshot result = GetDiagnosticSnapshotCore(Unsafe.SizeOf<T>());
        GC.KeepAlive(this);
        return result;
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

    private nuint TrimRetainedMemory(nuint byteBudget) => TrimRetainedMemoryCore(byteBudget);

    /// <summary>Ends worker use and transfers cleanup-only authority to a coordinator.</summary>
    public void Retire() => RetireCore();

    /// <summary>Releases one retired pool from a coordinator thread.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "Coordinator-owned retirement cleanup disarms the emergency finalizer.")]
    public void ReleaseRetiredStorage()
    {
        ReleaseRetiredStorageCore();
        GC.SuppressFinalize(this);
    }

    /// <summary>Closes the prepared pool after all initializing and published leases return.</summary>
    public void Dispose()
    {
        if (DisposeCore())
        {
            GC.SuppressFinalize(this);
        }
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
            FreeAllCore();
        }
        catch
        {
        }
    }

    internal NativePreparedPoolStatistics GetPreparedStatistics()
    {
        NativePreparedPoolStatistics result = GetPreparedStatisticsCore(Unsafe.SizeOf<T>());
        GC.KeepAlive(this);
        return result;
    }

    private void IncrementHistory(ref long counter) =>
        NativeOwnerHistory.Increment(ref counter, ref _historyOverflowed);

    private long[] GetSegmentOrdinals()
    {
        long[] result = GetSegmentOrdinalsCore(Unsafe.SizeOf<T>());
        GC.KeepAlive(this);
        return result;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCapacityExhausted() =>
        throw new InvalidOperationException("Prepared pool capacity is exhausted; use TryRent for expected exhaustion.");

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
        if (slab.Next != NativePreparedPoolStorage.Leased
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
    private void ThrowStaleIndex(int slabIndex, string operation) =>
        throw new NativeAllocationReturnedException(
            "The pooled slab index is stale.",
            "NativePreparedPool",
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
            "NativePreparedPool",
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
            "NativePreparedPool",
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
            "NativePreparedPool",
            generation: 0,
            currentGeneration: 0,
            operation,
            activeOperationCount: 0,
            allocationId: 0,
            _lifecycle);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLeaseTokenExhausted() =>
        throw new InvalidOperationException(
            "NativePreparedPool exhausted its lease-token authority.");
}
