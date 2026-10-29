using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

internal sealed unsafe partial class NativeArenaKernel : INativeFinalizationTarget, INativeKeptKernel
{
    private const nuint DefaultSegmentBytes = 4_096;
    private const nuint SegmentAlignment = 64;
    private const string OwnerKind = "NativeArena";
    private static readonly nuint HeaderBytes = AlignUp(
        (nuint)sizeof(ArenaSegmentHeader),
        SegmentAlignment);

    private readonly NativeMemoryReturn _returnMemoryOnDispose;
    private NativeMemoryBudget? _budget;
    private NativeOwnerThread _owner;
    private ArenaLane _ordinary;
    private ArenaLane _scoped;
    private NativeOwnerLifecycle _lifecycle;
    private ulong _generation = 1;
    private ulong _scopeEpoch = 1;
    private int _activeBorrowCount;
    private int _initializerActive;
    private long _retainedBytes;
    private long _peakRetainedBytes;
    private long _initializedPayloadBytes;
    private long _scopedInitializedPayloadBytes;
    private long _peakInitializedPayloadBytes;
    private long _trimmedBytes;
    private long _trimCallCount;
    private long _freshSegmentAllocationCount;
    private long _nextAllocationOrdinal;
    private bool _historyOverflowed;
    private int _segmentCount;
    private int _retainEpoch;

    // A disposed default-policy arena kernel waits in its thread's reuse slot
    // (and nested scopes' kernels behind it) for the next NativeArena the
    // thread creates, with its one segment if it had one: an arena made for
    // one scope then allocates no native memory and no kernel, only the small
    // public object. A larger segment goes to NativeLargeBlocks instead.
    private const long ReuseLimitBytes = 1024 * 1024;

    // Each NativeArena binds one lifetime of its kernel; a reused kernel starts
    // the next. Generations keep counting across lifetimes, so a lease of an
    // earlier lifetime is never current again; reports count from the
    // lifetime's start, as a new arena's would.
    internal int Lifetime;
    private ulong _generationBase;
    private ulong _scopeEpochBase;

    internal long Id { get; private set; }

    internal NativeArenaKernel(
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose,
        NativeMemoryBudget? budget,
        bool requireBudget)
    {
        if (requireBudget) ArgumentNullException.ThrowIfNull(budget);
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        // One thread-static read for the identity, the owner thread and the guard.
        NativeMemoryAccounting.PrepareThread();
        NativeThreadState thread = NativeThreadState.Current;
        Id = NativeOwnerIdentity.Next(thread);
        _returnMemoryOnDispose = returnMemoryOnDispose;
        _budget = budget;
        _owner = new NativeOwnerThread(thread);
        _lifecycle = NativeOwnerLifecycle.Active;

        // Before any native storage: the guard is this arena's finalizer.
        _guard = NativeFinalizationGuard.Rent(thread, this);

        if (preAllocateBytes != 0)
        {
            AppendSegment(
                ref _ordinary,
                preAllocateBytes,
                "declaration reservation");
            ResetLane(ref _ordinary);
        }
    }

    // The kernel for a new default arena: the one this thread kept, if it
    // fits, or a new one.
    internal static NativeArenaKernel Acquire(nuint preAllocateBytes, NativeMemoryReturn returnMemoryOnDispose)
    {
        if (returnMemoryOnDispose == NativeMemoryReturn.Reuse)
        {
            // The slot's kernel stays in the slot while an arena uses it, so
            // disposal stores no reference to put it back.
            NativeThreadState thread = NativeThreadState.Current;
            NativeArenaKernel? reusable = thread.ReusableArena;
            bool inSlot = reusable is { _lifecycle: NativeOwnerLifecycle.Disposed };
            if (!inSlot)
            {
                // One in use there belongs to an enclosing arena, which puts it
                // back on disposal, or to an arena never disposed: leaving it to
                // its arena lets the guard return an abandoned arena's storage.
                if (reusable is not null)
                {
                    thread.ReusableArena = null;
                }

                reusable = thread.MoreReusableArenas is null ? null : TakeMoreReusable(thread);
            }

            if (reusable is not null)
            {
                if (reusable.TryReactivate(thread, preAllocateBytes))
                {
                    return reusable;
                }

                // Its storage is freed; the slot must not offer it again.
                if (inSlot)
                {
                    thread.ReusableArena = null;
                }
            }
        }

        return new NativeArenaKernel(preAllocateBytes, returnMemoryOnDispose, budget: null, requireBudget: false);
    }

    // The kernel a budgeted arena of this thread kept for the next one with
    // the same budget, unless another thread's refused admission took it.
    internal static NativeArenaKernel AcquireBudgeted(NativeMemoryBudget budget, nuint preAllocateBytes, NativeMemoryReturn returnMemoryOnDispose)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (returnMemoryOnDispose == NativeMemoryReturn.Reuse)
        {
            NativeThreadState thread = NativeThreadState.Current;
            NativeArenaKernel? kept = NativeKeptStorage.Take(ref thread.KeptBudgetedArena, budget);
            if (kept is not null && kept.TryReactivate(thread, preAllocateBytes))
            {
                return kept;
            }
        }

        return new NativeArenaKernel(preAllocateBytes, returnMemoryOnDispose, budget, requireBudget: true);
    }

    bool INativeKeptKernel.IsKeptFor(NativeMemoryBudget? budget) => budget is null || _budget == budget;

    void INativeKeptKernel.ReleaseKept() => ReleaseKeptSegments();

    private void ReleaseKeptSegments()
    {
        ReleaseSegmentsExcept(null);
        _guard?.Disarm();
        _guard = null;
    }

    // A nested scope: the slot is in use, so a kernel behind it serves.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeArenaKernel? TakeMoreReusable(NativeThreadState thread) =>
        thread.MoreReusableArenas!.Pop();

    // Starts the next lifetime, unless the kernel is from an older reuse or
    // measurement epoch or its segment is too short for the reservation; such
    // a kernel frees its storage.
    private bool TryReactivate(NativeThreadState thread, nuint preAllocateBytes)
    {
        ArenaSegmentHeader* segment = _ordinary.First;
        if (_retainEpoch != thread.ReuseEpoch
            || (segment == null
                ? preAllocateBytes != 0
                : segment->MetricsEpoch != NativeMemoryAccounting.CurrentMetricsEpoch
                    || segment->Capacity < preAllocateBytes)
            || _generation >= ulong.MaxValue - 1
            || _scopeEpoch >= ulong.MaxValue - 1
            || Lifetime == int.MaxValue)
        {
            // Freed, as a disposed arena's unkept storage is; large segments go
            // to NativeLargeBlocks when it has no budget.
            ReleaseSegmentsExcept(null);
            ReleaseGuard(thread);
            return false;
        }

        NativeMemoryAccounting.PrepareThread();
        Id = NativeOwnerIdentity.Next(thread);
        Lifetime++;
        _generationBase = _generation;
        _generation++;
        _scopeEpochBase = _scopeEpoch;
        _scopeEpoch++;
        _lifecycle = NativeOwnerLifecycle.Active;
        ResetLane(ref _ordinary);
        _peakRetainedBytes = _retainedBytes;
        _initializedPayloadBytes = 0;
        _scopedInitializedPayloadBytes = 0;
        _peakInitializedPayloadBytes = 0;
        _trimmedBytes = 0;
        _trimCallCount = 0;
        _freshSegmentAllocationCount = 0;
        _nextAllocationOrdinal = 0;
        _historyOverflowed = false;
        return true;
    }

    internal NativeOwnerLifecycle CurrentLifecycle => _lifecycle;

    internal void ValidateOwnerThread(string operation) => ValidateThread(operation);

    // A disposed arena's snapshot, for an arena whose kernel serves a later one.
    internal static NativeOwnerDiagnosticSnapshot EndedSnapshot(long ownerId) =>
        new(NativeOwnerLifecycle.Disposed, 0, 0, NativeMemoryAccounting.CurrentMetricsEpoch,
            0, 0, 0, -1, -1, 0, 0, 0, 0, 0, 0, 0, false)
        {
            OwnerId = ownerId,
            Model = NativeOwnerModel.ThreadConfinedArena
        };

    // Generation and scope epoch as a new arena counts them, from 1.
    private long ReportedGeneration => unchecked((long)(_generation - _generationBase));

    private long ReportedScopeEpoch => unchecked((long)(_scopeEpoch - _scopeEpochBase));

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

    internal ArenaLease<T> ScratchFilled<T>(
        int length,
        bool scoped,
        T value)
        where T : unmanaged =>
        ScratchDirect(length, scoped, value, fill: true);

    internal ArenaLease<T> ScratchUninitialized<T>(
        int length,
        bool scoped)
        where T : unmanaged =>
        ScratchDirect(length, scoped, default(T), fill: false);

    private ArenaLease<T> ScratchDirect<T>(
        int length,
        bool scoped,
        T value,
        bool fill)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateActive(scoped
            ? nameof(NativeArena.ScratchScoped)
            : nameof(NativeArena.Scratch));
        if (_initializerActive != 0)
        {
            ThrowNestedInitializer();
        }

        nuint byteLength = CalculateByteLength<T>(length);
        long payloadBytes = checked((long)byteLength);
        _ = checked(_initializedPayloadBytes + payloadBytes);
        ArenaReservation reservation;
        if (_prepared)
        {
            if (!TryReservePreparedScratch<T>(length, scoped, out reservation))
            {
                throw new InvalidOperationException("The prepared arena byte capacity is exhausted. Use TryScratch or TryScratchScoped for non-allocating refusal.");
            }
        }
        else
        {
            reservation = Reserve(
                ref scoped ? ref _scoped : ref _ordinary,
                byteLength,
                CalculateAlignment<T>(),
                scoped);
        }

        // An unmanaged fill runs no user code, so it needs no initializer frame,
        // writer or rollback; publication follows the complete write.
        if (fill)
        {
            new Span<T>((void*)reservation.Pointer, length).Fill(value);
        }

        RecordInitialization(payloadBytes, scoped);
        if (_prepared)
        {
            IncrementPreparedHistory(ref _preparedSuccessCount);
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
            long payloadBytes = checked((long)CalculateByteLength<T>(length));
            _ = checked(_initializedPayloadBytes + payloadBytes);
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
            RecordInitialization(payloadBytes, reservation.Scoped);
        }
        catch
        {
            RollBack(reservation);
            throw;
        }
    }

    private void RecordInitialization(long payloadBytes, bool scoped)
    {
        _initializedPayloadBytes += payloadBytes;
        if (scoped)
        {
            _scopedInitializedPayloadBytes += payloadBytes;
        }
        _peakInitializedPayloadBytes = Math.Max(_peakInitializedPayloadBytes, _initializedPayloadBytes);
    }

    // Only lifecycle and epoch checks remain; let the JIT inline the entry.
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

    // ArenaLease<T> is a ref struct: safe code cannot move it to another thread,
    // so its operations run where Scratch already validated the owner thread.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ValidateHandle(
        ulong generation,
        ulong scopeEpoch,
        bool scoped,
        string operation)
    {
        if (_lifecycle != NativeOwnerLifecycle.Active)
        {
            ThrowDisposed(operation);
        }

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

    /// <summary>Invalidates all leases and reuses all retained segments.</summary>
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
        _initializedPayloadBytes = 0;
        _scopedInitializedPayloadBytes = 0;
        _generation = nextGeneration;
        _scopeEpoch = nextScopeEpoch;
        if (_retentionEnabled)
        {
            ApplyRetentionPolicy();
        }
    }

    /// <summary>Invalidates scoped leases and reuses scoped segments.</summary>
    internal void RecycleScoped()
    {
        ValidateBoundary(nameof(NativeArena.RecycleScoped));
        ulong nextScopeEpoch = GetNextEpochOrClose(
            _scopeEpoch,
            nameof(NativeArena.RecycleScoped));
        ResetLane(ref _scoped);
        _initializedPayloadBytes -= _scopedInitializedPayloadBytes;
        _scopedInitializedPayloadBytes = 0;
        _scopeEpoch = nextScopeEpoch;
        if (_retentionEnabled)
        {
            ApplyRetentionPolicy();
        }
    }

    /// <summary>Gets the current Arena storage statistics.</summary>
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
            Generation: ReportedGeneration,
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
            OutstandingNativeBytes = _retainedBytes,
            DetachedNativeBytes = 0,
            PeakOutstandingNativeBytes = _peakRetainedBytes,
            InitializedPayloadBytes = _lifecycle == NativeOwnerLifecycle.Active ? _initializedPayloadBytes : 0,
            PeakInitializedPayloadBytes = _peakInitializedPayloadBytes,
            UsableCapacityBytes = usableCapacityBytes - _externalActiveBytes,
            BorrowedBytes = _externalRetainedBytes
        };
    }

    internal NativeOwnerDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        ValidateThread(nameof(NativeArena.CaptureDiagnosticSnapshot));
        NativeOwnerDiagnosticSnapshot snapshot = new(
            _lifecycle, ReportedGeneration, ReportedScopeEpoch,
            NativeMemoryAccounting.CurrentMetricsEpoch,
            0, 0, 0, GetCurrentSegmentIndex(_ordinary), GetCurrentSegmentIndex(_scoped),
            _segmentCount,
            _lifecycle == NativeOwnerLifecycle.Active
                ? checked(CountUnusedSegments(_ordinary) + CountUnusedSegments(_scoped)) : 0,
            0, 0, 0, 0, 0, false)
        {
            OwnerId = Id,
            Model = NativeOwnerModel.ThreadConfinedArena,
            HistoryOverflowed = _historyOverflowed,
            OutstandingNativeBytes = _retainedBytes,
            // A disposed arena kept its storage for reuse or freed it.
            DetachedNativeBytes = 0,
            PeakOutstandingNativeBytes = _peakRetainedBytes,
            InitializedPayloadBytes = _lifecycle == NativeOwnerLifecycle.Active ? _initializedPayloadBytes : 0,
            PeakInitializedPayloadBytes = _peakInitializedPayloadBytes
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

    /// <summary>Closes the Arena and applies its cleanup policy.</summary>
    [SuppressMessage("Usage", "CA1816", Justification = "Close disarms finalization only after actual native return; a kept kernel retains its emergency finalizer.")]
    internal void Dispose()
    {
        // The thread check also yields the thread's reuse slot and spare guard.
        NativeThreadState thread = NativeThreadState.Current;
        if (thread.Serial != _owner.Serial)
        {
            ThrowWrongThread(nameof(Dispose));
        }

        if (_lifecycle == NativeOwnerLifecycle.Disposed)
        {
            return;
        }

        if (_activeBorrowCount != 0 || _initializerActive != 0)
        {
            ThrowBoundaryInUse(nameof(Dispose));
        }

        Close(thread);
    }

    private void Close() => Close(NativeThreadState.Current);

    private void Close(NativeThreadState thread)
    {
        _lifecycle = NativeOwnerLifecycle.Disposed;
        if (_returnMemoryOnDispose
            == NativeMemoryReturn.Free)
        {
            FreeAllSegments();
            ReleaseGuard(thread);
            return;
        }

        if (TryRetainForReuse(thread))
        {
            return;
        }

        // What is not kept is freed now, not detached for finalization, as a
        // region's is: disposal proved no borrow or initializer is entered. A
        // budgeted arena is not kept when its budget traces, and an arena with
        // a retention policy never is. Without a budget, large segments go to
        // NativeLargeBlocks first.
        if (_budget is null)
        {
            KeepLargeSegments(ref _ordinary);
            KeepLargeSegments(ref _scoped);
        }

        FreeAllSegments();
        ReleaseGuard(thread);
    }

    // Disposal already proved no borrow or initializer is entered, so large
    // segments go to NativeLargeBlocks for the next owner on any thread.
    private void KeepLargeSegments(ref ArenaLane lane)
    {
        ArenaSegmentHeader* remaining = null;
        ArenaSegmentHeader** tail = &remaining;
        bool kept = false;
        for (ArenaSegmentHeader* segment = lane.First, next; segment != null; segment = next)
        {
            next = segment->Next;
            nuint allocationBytes = segment->AllocationBytes;
            if (segment->External == 0 && NativeLargeBlocks.TryKeep(segment, allocationBytes, segment->MetricsEpoch))
            {
                _retainedBytes -= checked((long)allocationBytes);
                _segmentCount--;
                kept = true;
            }
            else
            {
                *tail = segment;
                tail = &segment->Next;
            }
        }

        if (kept)
        {
            *tail = null;
            lane = default;
            lane.First = remaining;
        }
    }

    // A plain arena's kernel, on the thread that owns it, waits in that
    // thread's slot (or behind it, for nested scopes) for the next arena,
    // keeping its largest ordinary segment within the limit. Disposal already
    // proved no borrow or initializer is entered, so every other segment is
    // released now: freed, or kept in NativeLargeBlocks if large.
    private bool TryRetainForReuse(NativeThreadState thread)
    {
        if (_prepared
            || _retentionEnabled
            || _externalBuffer is not null)
        {
            return false;
        }

        if (_budget is not null)
        {
            return TryKeepBudgeted(thread);
        }

        ArenaSegmentHeader* keep = null;
        for (ArenaSegmentHeader* segment = _ordinary.First; segment != null; segment = segment->Next)
        {
            if ((long)segment->Capacity <= ReuseLimitBytes
                && (keep == null || segment->Capacity > keep->Capacity))
            {
                keep = segment;
            }
        }

        long keptBytes = keep == null ? 0 : (long)keep->Capacity;
        // A kernel this thread took from the slot is still in it.
        bool inSlot = ReferenceEquals(thread.ReusableArena, this);
        if (!inSlot
            && thread.ReusableArena is not null
            && !(thread.MoreReusableArenas ??= new()).HasRoom(keptBytes))
        {
            return false;
        }

        ReleaseSegmentsExcept(keep);
        _retainEpoch = thread.ReuseEpoch;
        if (inSlot)
        {
            return true;
        }

        if (thread.ReusableArena is null)
        {
            thread.ReusableArena = this;
        }
        else
        {
            thread.MoreReusableArenas!.Push(this, keptBytes);
        }

        return true;
    }

    // A budgeted arena keeps its kernel, with its largest segment within the
    // limit, in its thread's budgeted slot for the next arena with the same
    // budget, as a budgeted region does (see NativeRegionKernel); the storage
    // stays charged to the budget until reused or taken back for it. The
    // slot is shared with other threads, so the disposed lifetime ends here
    // for its arena and leases.
    private bool TryKeepBudgeted(NativeThreadState thread)
    {
        if (_budget!.TraceEnabled || Lifetime >= int.MaxValue - 1)
        {
            return false;
        }

        ArenaSegmentHeader* keep = null;
        for (ArenaSegmentHeader* segment = _ordinary.First; segment != null; segment = segment->Next)
        {
            if ((long)segment->Capacity <= ReuseLimitBytes
                && (keep == null || segment->Capacity > keep->Capacity))
            {
                keep = segment;
            }
        }

        ReleaseSegmentsExcept(keep);
        _retainEpoch = thread.ReuseEpoch;
        Lifetime++;
        NativeKeptStorage.Keep(ref thread.KeptBudgetedArena, this);
        return true;
    }

    // The kernels an ended thread kept for its next arenas: large segments go
    // to NativeLargeBlocks, the rest to native memory.
    internal static void ReleaseRetained(NativeThreadState thread)
    {
        NativeArenaKernel? kernel = thread.ReusableArena;
        thread.ReusableArena = null;
        // A kernel still in use belongs to an arena its thread never disposed;
        // its guard returns that storage once the arena is unreachable.
        if (kernel is { _lifecycle: NativeOwnerLifecycle.Active })
        {
            kernel = thread.MoreReusableArenas?.Pop();
        }

        while (kernel is not null)
        {
            kernel.ReleaseSegmentsExcept(null);
            kernel._guard?.Disarm();
            kernel._guard = null;
            kernel = thread.MoreReusableArenas?.Pop();
        }

        NativeKeptStorage.Release(ref thread.KeptBudgetedArena, budget: null);
    }

    private void ReleaseSegmentsExcept(ArenaSegmentHeader* keep)
    {
        for (ArenaSegmentHeader* segment = _ordinary.First, next; segment != null; segment = next)
        {
            next = segment->Next;
            if (segment != keep)
            {
                ReleaseSegment(segment);
            }
        }

        for (ArenaSegmentHeader* segment = _scoped.First, next; segment != null; segment = next)
        {
            next = segment->Next;
            ReleaseSegment(segment);
        }

        _ordinary = default;
        _scoped = default;
        if (keep != null)
        {
            keep->Next = null;
            _ordinary.First = keep;
            _ordinary.Tail = keep;
        }
    }

    // A plain arena's segment: freed, or kept for later owners if large.
    private void ReleaseSegment(ArenaSegmentHeader* segment)
    {
        // A budgeted segment is freed and its charge released.
        if (_budget is not null)
        {
            FreeSegment(segment);
            return;
        }

        nuint allocationBytes = segment->AllocationBytes;
        long metricsEpoch = segment->MetricsEpoch;
        if (!NativeLargeBlocks.TryKeep(segment, allocationBytes, metricsEpoch))
        {
            NativeMemory.AlignedFree(segment);
            NativeMemoryAccounting.RecordFree(allocationBytes, detached: false, metricsEpoch);
        }

        _retainedBytes -= checked((long)allocationBytes);
        _segmentCount--;
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
            : byteLength >= NativeVectorAlignment.MinimumBytes
                ? AlignLarge((nuint)originalCursor, (nuint)lane.End, byteLength, alignment)
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
        nuint capacity;
        if (_retentionEnabled)
        {
            capacity = SelectPolicyGrowthCapacity(requiredCapacity, lane.Current == null ? 0 : lane.Current->Capacity);
        }
        else
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
            capacity = Math.Max(requiredCapacity, Math.Max(DefaultSegmentBytes, growth));
        }
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
        RecordPolicyAcquisition(segment);
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
        RecordPolicyAcquisition(segment);
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
            long prospectiveBytes = checked(_retainedBytes + checked((long)allocationBytes));
            _nextAllocationOrdinal = allocationOrdinal;
            if (NativeMemoryTestHooks.ConsumeForcedFailure())
            {
                throw new NativeAllocationFailedException(allocationBytes, OwnerKind,
                    generation: ReportedGeneration, operation, _lifecycle);
            }
            nuint keptBytes = 0;
            memory = _budget is null
                ? NativeLargeBlocks.TryTake(allocationBytes, out keptBytes, out metricsEpoch)
                : null;
            if (memory != null)
            {
                // Still counted from when it was kept.
                allocationBytes = keptBytes;
                capacity = keptBytes - HeaderBytes;
                prospectiveBytes = checked(_retainedBytes + checked((long)allocationBytes));
                recorded = true;
            }
            else
            {
                memory = NativeMemory.AlignedAlloc(
                    allocationBytes,
                    SegmentAlignment);
            }

            if (memory == null)
            {
                // AlignedAlloc reports native allocation failure with a null pointer.
#pragma warning disable CA2201
                throw new OutOfMemoryException();
#pragma warning restore CA2201
            }

            _peakRetainedBytes = Math.Max(_peakRetainedBytes, prospectiveBytes);
            ArenaSegmentHeader* segment =
                (ArenaSegmentHeader*)memory;
            *segment = default;
            segment->DataStart = (byte*)segment + HeaderBytes;
            segment->Capacity = capacity;
            segment->AllocationBytes = allocationBytes;
            if (!recorded)
            {
                metricsEpoch = NativeMemoryAccounting.RecordAllocation(allocationBytes, zeroed: false);
                recorded = true;
            }

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
                generation: ReportedGeneration,
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
        if (!_owner.IsCurrent())
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

    private void FreeAllSegments()
    {
        FreeLane(ref _ordinary);
        FreeLane(ref _scoped);
        _retainedBytes = 0;
        _segmentCount = 0;
        _budget = null;
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
        long allocationOrdinal = segment->AllocationOrdinal;
        RecordPolicyRelease(segment);
        NativeMemory.AlignedFree(segment);
        _budget?.Release(allocationBytes, Id, traceKind, allocationOrdinal);
        NativeMemoryAccounting.RecordFree(
            allocationBytes,
            detached: false,
            metricsEpoch);
        _retainedBytes -= checked((long)allocationBytes);
        _segmentCount--;
    }

    [DoesNotReturn]
    private static void ThrowNestedInitializer() =>
        throw new InvalidOperationException(
            "NativeArena does not permit a nested initializer.");

    [DoesNotReturn]
    private static void ThrowIncompleteInitialization(
        int initializedLength,
        int requiredLength) =>
        throw new InvalidOperationException(
            $"The native lease initializer wrote {initializedLength} of {requiredLength} required elements.");

    [DoesNotReturn]
    private void ThrowBoundaryInUse(string operation) =>
        throw new NativeAllocationInUseException(
            "The arena cannot cross a lifetime boundary during an active callback or initializer.",
            OwnerKind,
            ReportedGeneration,
            ReportedGeneration,
            operation,
            _activeBorrowCount + _initializerActive,
            0,
            _lifecycle);

    [DoesNotReturn]
    private void ThrowDisposed(string operation) =>
        throw new NativeAllocationDisposedException(
            "The native arena is disposed.",
            OwnerKind,
            ReportedGeneration,
            ReportedGeneration,
            operation,
            _activeBorrowCount,
            allocationId: 0,
            _lifecycle);

    [DoesNotReturn]
    private void ThrowWrongThread(string operation) =>
        throw new NativeAllocationStateException(
            "NativeArena is confined to its construction thread.",
            OwnerKind,
            ReportedGeneration,
            ReportedGeneration,
            operation,
            _activeBorrowCount,
            allocationId: 0,
            _lifecycle);

    // A lease of an earlier lifetime belongs to a disposed arena.
    [DoesNotReturn]
    private void ThrowStale(
        ulong generation,
        ulong scopeEpoch,
        bool scoped,
        string operation)
    {
        if (generation <= _generationBase)
        {
            ThrowEnded(operation);
        }

        throw new NativeAllocationReturnedException(
            scoped
                ? "The scoped arena lease epoch is stale."
                : "The arena generation is stale.",
            OwnerKind,
            unchecked((long)(scoped ? scopeEpoch - _scopeEpochBase : generation - _generationBase)),
            scoped ? ReportedScopeEpoch : ReportedGeneration,
            operation,
            _activeBorrowCount,
            allocationId: 0,
            _lifecycle);
    }

    // What a disposed arena throws, for an arena whose kernel has moved on.
    [DoesNotReturn]
    internal static void ThrowEnded(string operation) =>
        throw new NativeAllocationDisposedException(
            "The native arena is disposed.",
            OwnerKind,
            0,
            0,
            operation,
            0,
            allocationId: 0,
            NativeOwnerLifecycle.Disposed);

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

    // A large range takes a vector-aligned start when the segment has room
    // for it; otherwise the element alignment decides, as for every range.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint AlignLarge(nuint cursor, nuint end, nuint byteLength, nuint alignment)
    {
        nuint vector = AlignUp(cursor, NativeVectorAlignment.For(byteLength, alignment));
        return vector <= end && byteLength <= end - vector
            ? vector
            : AlignUp(cursor, alignment);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint AlignUp(nuint value, nuint alignment)
    {
        nuint mask = alignment - 1;
        return checked(value + mask) & ~mask;
    }

    [DoesNotReturn]
    private void ThrowEpochExhausted(string operation) =>
        throw new NativeAllocationStateException(
            "NativeArena exhausted its non-reusable lease epoch.",
            OwnerKind,
            ReportedGeneration,
            ReportedGeneration,
            operation,
            activeOperationCount: 0,
            allocationId: 0,
            _lifecycle);

    // This arena's emergency finalizer, shared through its thread; see
    // NativeFinalizationGuard. Null once the arena returned its storage.
    private NativeFinalizationGuard? _guard;

    void INativeFinalizationTarget.FinalizeNativeStorage() => FreeAllSegments();

    // After the storage is actually returned.
    private void ReleaseGuard(NativeThreadState thread)
    {
        NativeFinalizationGuard? guard = _guard;
        if (guard is not null)
        {
            _guard = null;
            guard.Release(thread);
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
