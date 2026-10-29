using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

public sealed unsafe partial class NativePool<T> : INativeFinalizationTarget
    where T : unmanaged
{
    private const int SizeClassCount = 32;
    private const nuint SlabAlignment = 64;
    private const string OwnerKind = "NativePool";

    private readonly NativeMemoryReturn _returnMemoryOnDispose;
    private NativeMemoryBudget? _budget;
    private readonly int _ownerThreadId;
    // A thread's shared pool proves ownership by sitting in that thread's
    // slot, which the Shared getter has just read: no second thread-static
    // lookup on its rents.
    private bool _isShared;
    private int[] _freeHeads = [];
    private Slab[] _slabs = [];
    private uint _nonEmptyFreeClasses;
    private NativeOwnerLifecycle _lifecycle;
    private int _slabCount;
    private int _unusedHead = -1;
    private int _returnedSlabIndex = -1;
    // The capacity of the slab returned last, or -1, and the length above
    // which it is in that slab's size class (half its capacity rounded up to
    // a power of two).
    private int _returnedCapacity = -1;
    private int _returnedFloor;
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
    private long _peakRetainedBytes;
    private bool _historyOverflowed;
    private int _retainEpoch;

    // One disposed default-policy pool per thread and element type keeps its
    // slabs for the next pool the thread creates, as regions and arenas do:
    // a pool made for one scope then allocates no native memory, and the
    // disposed one needs no finalization. Stale leases of the disposed pool
    // still name it and fail its validation.
    private const long ReuseLimitBytes = 1024 * 1024;

    [ThreadStatic]
    private static NativePool<T>? t_reusable;

    // More disposed pools behind the slot, for nested scopes.
    [ThreadStatic]
    private static NativeReuseStack<NativePool<T>>? t_moreReusable;

    private NativePool(
        int preLease,
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose,
        NativeMemoryBudget? budget,
        bool requireBudget)
    {
        if (requireBudget) ArgumentNullException.ThrowIfNull(budget);
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        // Growable pools need hot-history records only for actual clear/copy work.
        // Fixed-shape prepared pools still acquire them during preparation.
        Id = NativeOwnerIdentity.NextWithoutPreparation();
        ArgumentOutOfRangeException.ThrowIfNegative(preLease);
        _returnMemoryOnDispose = returnMemoryOnDispose;
        _budget = budget;
        _ownerThreadId = NativeThreadIdentity.Current;
        _lifecycle = NativeOwnerLifecycle.Active;
        NativeThreadState thread = NativeThreadState.Current;
        if (returnMemoryOnDispose == NativeMemoryReturn.ToGarbageCollector
            && budget is null
            && preLease == 0
            && preAllocateBytes == 0
            && TryAdoptRetainedSlabs(thread))
        {
            return;
        }

        // Before any native storage: the guard is this pool's finalizer.
        _guard = NativeFinalizationGuard.Rent(thread, this);
        _slabs = new Slab[4];

        try
        {
            if (preLease != 0)
            {
                nuint bytes = CalculateByteLength(preLease);
                int index = AddSlab(preLease, bytes, "typed reservation");
                CacheReturnedSlab(index);
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
                CacheReturnedSlab(index);
            }
        }
        catch
        {
            FreeAll();
            throw;
        }
    }

    internal int CurrentGenerationActiveOperationsForTest
    {
        get
        {
            int count = 0;
            foreach (ref readonly Slab slab in _slabs.AsSpan(0, _slabCount))
            {
                count = checked(count + slab.BorrowCount + (slab.State is SlabState.Initializing or SlabState.Processing ? 1 : 0));
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

        return InitializeSlab(slabIndex, token, length, initializer);
    }

    /// <summary>Fills and publishes one pooled slab without an initializer callback.</summary>
    /// <param name="length">The complete initialized element count.</param>
    /// <param name="value">The value written to every element before publication.</param>
    /// <returns>The owning token-bound slab capability.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Pooled<T> Rent(int length, T value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateOwner(nameof(Rent));
        long token = TakeLeaseToken();
        // Take the index first: growth can replace the slot table.
        int slabIndex = TakeOrAddSlab(length);
        Slab slab = _slabs[slabIndex];
        // Admission proves the whole range fits. This unmanaged fill cannot
        // invoke user code, so no callback writer or initializing frame is
        // needed. Publish authority only after every element has been written.
        new Span<T>((void*)slab.Pointer, length).Fill(value);
        slab.Token = token;
        slab.State = SlabState.Leased;
        _liveLeaseCount++;
        _requestedBytes += checked((long)CalculateByteLength(length));
        _peakInitializedPayloadBytes = Math.Max(_peakInitializedPayloadBytes, _requestedBytes);
        return new Pooled<T>(this, slab, token, length, slab.Capacity);
    }

    /// <summary>Rents one slab without writing its elements.</summary>
    /// <param name="length">The element count.</param>
    /// <returns>The owning token-bound slab capability.</returns>
    /// <remarks>
    /// The contents are unspecified: they may hold values an earlier lease in this process wrote. Write every element before reading it, or use
    /// <see cref="Rent(int, T)"/> for a filled range. This skips the fill for callers that overwrite the range anyway.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Pooled<T> RentUninitialized(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateOwner(nameof(RentUninitialized));
        long token = TakeLeaseToken();
        int slabIndex = TakeOrAddSlab(length);
        Slab slab = _slabs[slabIndex];
        slab.Token = token;
        slab.State = SlabState.Leased;
        _liveLeaseCount++;
        _requestedBytes += checked((long)CalculateByteLength(length));
        _peakInitializedPayloadBytes = Math.Max(_peakInitializedPayloadBytes, _requestedBytes);
        return new Pooled<T>(this, slab, token, length, slab.Capacity);
    }

    /// <summary>Fills a temporary rental, processes it, and returns it before this call completes.</summary>
    /// <param name="length">The complete initialized element count exposed to the processor.</param>
    /// <param name="value">The value written to every element before processing.</param>
    /// <param name="state">Explicit state passed to the synchronous processor.</param>
    /// <param name="processor">The bounded operation; its span cannot escape the callback.</param>
    /// <typeparam name="TState">The caller state type.</typeparam>
    /// <typeparam name="TResult">The result type, which cannot contain the borrowed span.</typeparam>
    /// <returns>The processor result after the temporary slab has been returned.</returns>
    /// <remarks>Processor failure also returns the slab. Other rentals may be nested inside the callback.</remarks>
    public TResult Process<TState, TResult>(
        int length,
        T value,
        TState state,
        NativeSpanStateProcessor<T, TState, TResult> processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateOwner(nameof(Process));
        // Take the index first: growth can replace the slot table.
        int slabIndex = TakeOrAddSlab(length);
        Slab slab = _slabs[slabIndex];
        Span<T> values = new((void*)slab.Pointer, length);
        values.Fill(value);
        // No public lease escapes this frame. A distinct state rejects every
        // public token without creating lease authority or a borrow record.
        slab.State = SlabState.Processing;
        _liveLeaseCount++;
        _requestedBytes += checked((long)CalculateByteLength(length));
        _peakInitializedPayloadBytes = Math.Max(_peakInitializedPayloadBytes, _requestedBytes);
        try
        {
            return processor(values, state);
        }
        finally
        {
            // Only this frame owns the rental; a callback cannot return it.
            slab.State = SlabState.Free;
            _liveLeaseCount--;
            _requestedBytes -= checked((long)CalculateByteLength(length));
            CacheReturnedSlab(slab);
        }
    }

    /// <summary>Fills a temporary rental, runs a compile-time processor, and returns it before this call completes.</summary>
    /// <param name="length">The complete initialized element count exposed to the processor.</param>
    /// <param name="value">The value written to every element before processing.</param>
    /// <param name="state">Explicit state passed to the processor.</param>
    /// <typeparam name="TState">The caller state type.</typeparam>
    /// <typeparam name="TResult">The result type, which cannot contain the borrowed span.</typeparam>
    /// <typeparam name="TProcessor">The processor; its static member is called without a delegate.</typeparam>
    /// <returns>The processor result after the temporary slab has been returned.</returns>
    /// <remarks>Processor failure also returns the slab. Other rentals may be nested inside the processor.</remarks>
    public TResult Process<TState, TResult, TProcessor>(
        int length,
        T value,
        scoped in TState state)
        where TState : allows ref struct
        where TProcessor : struct, INativeSpanProcessor<T, TState, TResult>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateOwner(nameof(Process));
        // Take the index first: growth can replace the slot table.
        int slabIndex = TakeOrAddSlab(length);
        Slab slab = _slabs[slabIndex];
        Span<T> values = new((void*)slab.Pointer, length);
        values.Fill(value);
        slab.State = SlabState.Processing;
        _liveLeaseCount++;
        _requestedBytes += checked((long)CalculateByteLength(length));
        _peakInitializedPayloadBytes = Math.Max(_peakInitializedPayloadBytes, _requestedBytes);
        try
        {
            return TProcessor.Invoke(values, in state);
        }
        finally
        {
            slab.State = SlabState.Free;
            _liveLeaseCount--;
            _requestedBytes -= checked((long)CalculateByteLength(length));
            CacheReturnedSlab(slab);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Pooled<T> InitializeSlab(int slabIndex, long token, int length,
        NativeLeaseInitializer<T> initializer)
    {

        Slab slab = _slabs[slabIndex];
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
            // Nested rentals can grow the slot table, but this slab object stays put.
            slab.State = SlabState.Leased;
            return new Pooled<T>(
                this,
                slab,
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

    internal IntPtr EnterBorrow(
        Slab slab,
        long token,
        string operation)
    {
        ValidateLeaseOwner(operation);
        ValidateLease(slab, token, operation);
        // Pooled carries the immutable length supplied only after complete
        // initialization. Process bounds a shorter prefix at its public boundary;
        // ordinary bounded access needs no redundant physical-capacity comparison.
        slab.BorrowCount++;
        return slab.Pointer;
    }

    // Direct span access validates once and enters no borrow: the span has no
    // ownership, and its scope is the lease's own lexical scope.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal IntPtr GetLeasePointer(Slab slab, long token, string operation)
    {
        ValidateLeaseOwner(operation);
        ValidateLease(slab, token, operation);
        IntPtr pointer = slab.Pointer;
        GC.KeepAlive(this);
        return pointer;
    }

    // Unmanaged bulk operations invoke no user code and cannot reenter this
    // thread-confined owner. Validate authority without a callback borrow frame;
    // keep the actual finalizable owner alive through the last native access.
    internal void Clear(Slab slab, long token, int length)
    {
        ValidateLeaseOwner(nameof(Pooled<T>.Clear));
        ValidateLease(slab, token, nameof(Pooled<T>.Clear));
        NativeMemoryAccounting.ClearStorage(new Span<T>((void*)slab.Pointer, length));
        GC.KeepAlive(this);
    }

    internal void CopyFrom(Slab slab, long token, scoped ReadOnlySpan<T> source)
    {
        ValidateLeaseOwner(nameof(Pooled<T>.CopyFrom));
        ValidateLease(slab, token, nameof(Pooled<T>.CopyFrom));
        NativeMemoryAccounting.CopyRange(source, new Span<T>((void*)slab.Pointer, source.Length));
        GC.KeepAlive(this);
    }

    internal void CopyTo(Slab slab, long token, int length, scoped Span<T> destination)
    {
        ValidateLeaseOwner(nameof(Pooled<T>.CopyTo));
        ValidateLease(slab, token, nameof(Pooled<T>.CopyTo));
        NativeMemoryAccounting.CopyRange(new ReadOnlySpan<T>((void*)slab.Pointer, length), destination);
        GC.KeepAlive(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitBorrow(Slab slab)
    {
        slab.BorrowCount--;
        // Keep the finalizable owner, not just its metadata bank, alive through
        // the borrow even when callers and their cleanup are inlined.
        GC.KeepAlive(this);
    }

    internal long Move(Slab slab, long token)
    {
        ValidateLeaseOwner(nameof(Pooled<T>.Move));
        ValidateLease(slab, token, nameof(Pooled<T>.Move));
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
            budget.RecordOwnershipTransition(NativeMemoryTraceKind.Moved, Id,
                nextToken, slab.AllocationBytes, slab.Ordinal);
        }
        return nextToken;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void Return(
        Slab slab,
        long token,
        int length)
    {
        ValidateLeaseOwner(nameof(Pooled<T>.Dispose));
        ValidateLease(slab, token, nameof(Pooled<T>.Dispose));
        if (slab.BorrowCount != 0)
        {
            ThrowActiveBorrow();
        }

        slab.State = SlabState.Free;
        _liveLeaseCount--;
        _requestedBytes -= checked((long)CalculateByteLength(length));
        CacheReturnedSlab(slab);
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
        RebuildFreeListsWithoutFreedSlabs();
        for (int index = 0; index < _slabCount; index++)
        {
            Slab slab = _slabs[index];
            if (slab.State != SlabState.Free
                || released >= byteBudget)
            {
                continue;
            }

            nuint bytes = slab.AllocationBytes;
            FreeSlab(slab);
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
        ReleaseGuard();
    }

    /// <summary>Closes the pool after all leases return.</summary>
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
            ReleaseGuard();
            return;
        }

        if (TryRetainForReuse())
        {
            return;
        }

        MarkDetached();
    }

    // Every slab is free: disposal refused live leases.
    private bool TryRetainForReuse()
    {
        if (_budget is not null
            || _slabCount == 0
            || _retainedBytes > ReuseLimitBytes)
        {
            return false;
        }

        if (t_reusable is null)
        {
            _retainEpoch = NativeThreadState.Current.ReuseEpoch;
            t_reusable = this;
            return true;
        }

        // A nested scope's pool waits behind the slot.
        NativeReuseStack<NativePool<T>> more = t_moreReusable ??= new();
        if (!more.HasRoom(_retainedBytes))
        {
            return false;
        }

        _retainEpoch = NativeThreadState.Current.ReuseEpoch;
        more.Push(this, _retainedBytes);
        return true;
    }

    // Takes the slab table of this thread's retained pool, unless it is from
    // an older reuse or measurement epoch; such a donor is detached for its
    // finalizer as its disposal would have.
    private bool TryAdoptRetainedSlabs(NativeThreadState thread)
    {
        NativePool<T>? donor = t_reusable;
        if (donor is not null)
        {
            t_reusable = null;
        }
        else
        {
            donor = t_moreReusable?.Pop();
            if (donor is null)
            {
                return false;
            }
        }

        if (donor._retainEpoch != thread.ReuseEpoch || !donor.SlabsInCurrentMetricsEpoch())
        {
            donor.MarkDetached();
            return false;
        }

        _slabs = donor._slabs;
        _slabCount = donor._slabCount;
        _freeHeads = donor._freeHeads;
        _nonEmptyFreeClasses = donor._nonEmptyFreeClasses;
        _unusedHead = donor._unusedHead;
        _returnedSlabIndex = donor._returnedSlabIndex;
        _returnedCapacity = donor._returnedCapacity;
        _returnedFloor = donor._returnedFloor;
        _retainedBytes = donor._retainedBytes;
        _peakRetainedBytes = _retainedBytes;
        _nextAllocationOrdinal = donor._nextAllocationOrdinal;
        _leaseTokenCounter = donor._leaseTokenCounter;
        donor._slabs = [];
        donor._slabCount = 0;
        donor._freeHeads = [];
        donor._nonEmptyFreeClasses = 0;
        donor._unusedHead = -1;
        donor._returnedSlabIndex = -1;
        donor._returnedCapacity = -1;
        donor._retainedBytes = 0;
        // The donor's guard, still registered, becomes this pool's.
        _guard = donor._guard;
        donor._guard = null;
        _guard!.Target = this;
        return true;
    }

    private bool SlabsInCurrentMetricsEpoch()
    {
        long epoch = NativeMemoryAccounting.CurrentMetricsEpoch;
        foreach (ref readonly Slab slab in _slabs.AsSpan(0, _slabCount))
        {
            if (slab.AllocationBytes != 0 && slab.MetricsEpoch != epoch)
            {
                return false;
            }
        }

        return true;
    }

    private bool HasInFlightSlab()
    {
#pragma warning disable HLQ013 // The slab's writable ref and active prefix are required.
        for (int index = 0; index < _slabCount; index++)
        {
            Slab slab = _slabs[index];
            if (slab.BorrowCount != 0
                || slab.State is SlabState.Initializing
                    or SlabState.Leased
                    or SlabState.Processing)
            {
                return true;
            }
        }
#pragma warning restore HLQ013

        return false;
    }

    // The slab returned last serves any length of its own size class that
    // it holds, as a fresh slab (rounded up to a power of two) would; lengths
    // changing between rents within a class stay on this path. Other lengths
    // take the best fit from the free lists.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int TakeOrAddSlab(int length)
    {
        if (length <= _returnedCapacity && length > _returnedFloor)
        {
            int slabIndex = _returnedSlabIndex;
            _returnedSlabIndex = -1;
            _returnedCapacity = -1;
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
            if (_nonEmptyFreeClasses == 0)
            {
                if (_slabs[slabIndex].Capacity >= length)
                {
                    _returnedSlabIndex = -1;
                    _returnedCapacity = -1;
                    return slabIndex;
                }
                // Keep the only available slab reachable if growth fails.
            }
            else
            {
                _returnedSlabIndex = -1;
                _returnedCapacity = -1;
                PushFreeList(slabIndex);
            }
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

    // Inlined into its one caller, already out of line: one call per rent
    // whose length changes size class.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
                Slab slab = _slabs[current];
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
                Ordinal = ordinal,
                Index = index
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

        if (_slabCount != 0 && _freeHeads.Length == 0)
        {
            // A second record can displace a returned slab. Prepare its class
            // table before publication, never during return or rollback.
            int[] heads = new int[SizeClassCount];
            Array.Fill(heads, -1);
            _freeHeads = heads;
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
            Next = _unusedHead,
            Index = index
        };
        _unusedHead = index;
    }

    private void RollBackRent(int slabIndex, int length)
    {
        Slab slab = _slabs[slabIndex];
        slab.State = SlabState.Free;
        _liveLeaseCount--;
        CacheReturnedSlab(slab);
    }

    // A lease holds its slab object, so validation reads no slot table. Tokens
    // are never reused, and trimming or disposal resets the object to Unused,
    // so a stale lease fails here even after its slot holds another slab.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateLease(
        Slab slab,
        long token,
        string operation)
    {
        if (slab.State != SlabState.Leased
            || slab.Token != token)
        {
            ThrowStaleToken(
                slab,
                token,
                operation);
        }
    }

    // Pooled<T> is a ref struct: safe code cannot store it on the heap, capture
    // it or carry it across an await, so every lease operation runs on the thread
    // whose Rent passed ValidateOwner. Only the lifecycle can change since then.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateLeaseOwner(string operation)
    {
        if (_lifecycle != NativeOwnerLifecycle.Active)
        {
            ThrowDisposed(operation);
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
    private void ValidateThread(string operation)
    {
        if (_isShared
            ? ReferenceEquals(t_shared, this)
            : NativeThreadIdentity.Current == _ownerThreadId)
        {
            return;
        }

        ThrowWrongThread(operation);
    }

    [DoesNotReturn]
    private static void ThrowActiveBorrow() =>
        throw new InvalidOperationException(
            "A pooled lease cannot return during an active callback.");

    [DoesNotReturn]
    private static void ThrowActiveMove() =>
        throw new InvalidOperationException(
            "A pooled lease cannot move during an active callback; the source remains owning.");

    [DoesNotReturn]
    private static void ThrowLiveLease() =>
        throw new InvalidOperationException(
            "NativePool cannot dispose while a pooled lease is active.");

    [DoesNotReturn]
    private static void ThrowLiveRetirementState() =>
        throw new InvalidOperationException(
            "NativePool cannot retire while a lease, initializer, or callback is active.");

    [DoesNotReturn]
    private static void ThrowRetiredDispose() =>
        throw new InvalidOperationException(
            "NativePool.Dispose cannot release retired storage. Use ReleaseRetiredStorage on the coordinator thread.");

    [DoesNotReturn]
    private static void ThrowInvalidRetiredCleanup(int state) =>
        throw new InvalidOperationException(
            state == 0
                ? "NativePool must retire before coordinator cleanup."
                : "NativePool retired storage was already released.");

    [DoesNotReturn]
    private void ThrowStaleToken(
        Slab slab,
        long token,
        string operation) =>
        throw new NativeAllocationReturnedException(
            "The pooled lease token is stale.",
            OwnerKind,
            generation: token,
            currentGeneration: slab.Token,
            operation,
            slab.State == SlabState.Processing ? 1 : slab.BorrowCount,
            slab.Index,
            _lifecycle);

    [DoesNotReturn]
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

    private void CacheReturnedSlab(int slabIndex) =>
        CacheReturnedSlab(_slabs[slabIndex]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CacheReturnedSlab(Slab slab)
    {
        int displaced = _returnedSlabIndex;
        int capacity = slab.Capacity;
        _returnedSlabIndex = slab.Index;
        _returnedCapacity = capacity;
        _returnedFloor = capacity <= 1 ? 0 : (int)(BitOperations.RoundUpToPowerOf2((uint)capacity) >> 1);
        slab.Next = -1;
        if (displaced >= 0)
        {
            PushFreeList(displaced);
        }
    }

    private void PushFreeList(int slabIndex)
    {
        Slab slab = _slabs[slabIndex];
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
        _returnedCapacity = -1;
        for (int index = 0; index < _slabCount; index++)
        {
            if (_slabs[index].State == SlabState.Free)
            {
                CacheReturnedSlab(index);
            }
        }
    }

    private void MarkDetached()
    {
#pragma warning disable HLQ013 // The slab's writable ref and active prefix are required.
        for (int index = 0; index < _slabCount; index++)
        {
            Slab slab = _slabs[index];
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
            FreeSlab(_slabs[index]);
        }
#pragma warning restore HLQ013

        _retainedBytes = 0;
        _budget = null;
    }

    private void FreeSlab(Slab? slab)
    {
        if (slab is null)
        {
            return;
        }

        if (slab.AllocationBytes == 0)
        {
            slab.Reset();
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
        slab.Reset();
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
    private static void ThrowLeaseTokenExhausted() =>
        throw new InvalidOperationException(
            "NativePool exhausted its lease-token authority.");

    [DoesNotReturn]
    private static void ThrowIncompleteInitialization(
        int initializedLength,
        int requiredLength) =>
        throw new InvalidOperationException(
            $"The native lease initializer wrote {initializedLength} of {requiredLength} required elements.");

    // This pool's emergency finalizer, shared through its thread; see
    // NativeFinalizationGuard. Null once the pool returned its storage.
    private NativeFinalizationGuard? _guard;

    void INativeFinalizationTarget.FinalizeNativeStorage() => FreeAll();

    // After the storage is actually returned; detached backing keeps its guard.
    private void ReleaseGuard()
    {
        NativeFinalizationGuard? guard = _guard;
        if (guard is not null)
        {
            _guard = null;
            guard.Release(NativeThreadState.Current);
        }
    }

    // One retained slab. Leases hold the object itself, so lease checks skip
    // the slot table; the slot index serves the free lists and diagnostics.
    internal sealed class Slab
    {
        internal IntPtr Pointer;
        internal nuint AllocationBytes;
        internal int Capacity;
        internal int Next;
        internal long Token;
        internal int BorrowCount;
        internal SlabState State;
        internal int Index;
        internal long MetricsEpoch;
        internal long Ordinal;
        internal bool Detached;

        // Keeps the slot index; every other field returns to an Unused slab.
        internal void Reset()
        {
            Pointer = IntPtr.Zero;
            AllocationBytes = 0;
            Capacity = 0;
            Next = 0;
            Token = 0;
            BorrowCount = 0;
            State = SlabState.Unused;
            MetricsEpoch = 0;
            Ordinal = 0;
            Detached = false;
        }
    }

    private void IncrementHistory(ref long counter) =>
        NativeOwnerHistory.Increment(ref counter, ref _historyOverflowed);

    internal long[] GetSegmentOrdinals()
    {
        ValidateThread(nameof(GetSegmentOrdinals));
        long[] result = new long[GetStorageCounts().Retained];
        int index = 0;
        foreach (ref readonly Slab slab in _slabs.AsSpan(0, _slabCount))
        {
            if (slab.AllocationBytes != 0)
            {
                result[index++] = slab.Ordinal;
            }
        }
        Array.Sort(result);
        GC.KeepAlive(this);
        return result;
    }

    internal enum SlabState : byte
    {
        Unused,
        Free,
        Initializing,
        Leased,
        Processing,
    }
}
