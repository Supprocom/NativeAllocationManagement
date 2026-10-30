using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

internal sealed unsafe class NativeRegionKernel : INativeFinalizationTarget, INativeKeptKernel
{
    private const nuint DefaultSegmentBytes = 4_096;
    private const string OwnerKind = "NativeRegion";
    private static readonly nuint HeaderBytes = AlignUp(
        (nuint)sizeof(RegionSegmentHeader),
        NativeSegment.Alignment);

    // One disposed garbage-collector-policy region per thread keeps one segment
    // for the next region instead of detaching it for finalization: its largest
    // segment within the limit, so a region that grew starts the next one at
    // that size. A larger segment would be mapped fresh, page by page, for
    // every region that needs it.
    internal const long ReuseLimitBytes = 1024 * 1024;

    private readonly NativeMemoryReturn _returnMemoryOnDispose;
    private NativeMemoryBudget? _budget;
    internal long Id { get; private set; } = NativeOwnerIdentity.Next();
    private NativeOwnerThread _owner;
    // The construction thread's state: disposal reaches its reuse slot without a TLS read.
    private readonly NativeThreadState _ownerThread;
    // The lifecycle and the entered-operation count side by side, so that
    // "active with nothing entered" reads as one 64-bit zero (see Dispose).
    private RegionState _state;
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
    // Region and Local handles carry the lifetime they were issued for; a reused
    // kernel starts the next lifetime, so earlier handles stay disposed.
    private int _generation;
    private int _retainEpoch;

    internal NativeRegionKernel(
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose,
        NativeMemoryBudget? budget = null)
    {
        _returnMemoryOnDispose = returnMemoryOnDispose;
        _budget = budget;
        _ownerThread = NativeThreadState.Current;
        _owner = new NativeOwnerThread(_ownerThread);
        _state.Lifecycle = NativeOwnerLifecycle.Active;

        // Before any native storage: the guard is this kernel's finalizer.
        _guard = NativeFinalizationGuard.Rent(_ownerThread, this);
        if (preAllocateBytes != 0)
        {
            AppendSegment(
                preAllocateBytes,
                "declaration reservation");
        }
    }

    internal NativeOwnerLifecycle Lifecycle => _state.Lifecycle;

    internal int Generation => _generation;

    internal static bool HasReusableKernelForTest =>
        NativeThreadState.Current is { } thread
        && (thread.ReusableRegion is { _state.Lifecycle: NativeOwnerLifecycle.Disposed }
            || thread.MoreReusableRegions is { IsEmpty: false });

    internal static NativeRegionKernel Acquire(
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose,
        NativeMemoryBudget? budget = null)
    {
        if (budget is not null)
        {
            return AcquireBudgeted(preAllocateBytes, returnMemoryOnDispose, budget);
        }

        if (returnMemoryOnDispose == NativeMemoryReturn.Reuse)
        {
            // The slot's kernel stays in the slot while a region uses it, so
            // disposal stores no reference to put it back.
            NativeThreadState thread = NativeThreadState.Current;
            NativeRegionKernel? reusable = thread.ReusableRegion;
            if (reusable is { _state.Lifecycle: NativeOwnerLifecycle.Disposed }
                && reusable.TryReactivateKept(thread, preAllocateBytes))
            {
                return reusable;
            }

            return AcquireReusable(thread, preAllocateBytes);
        }

        return new NativeRegionKernel(
            preAllocateBytes,
            returnMemoryOnDispose);
    }

    // Every other default-policy acquisition: a slot kernel without its
    // segment or with stale storage, a nested scope, or no kernel to reuse.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeRegionKernel AcquireReusable(
        NativeThreadState thread,
        nuint preAllocateBytes)
    {
        NativeRegionKernel? reusable = thread.ReusableRegion;
        bool inSlot = reusable is { _state.Lifecycle: NativeOwnerLifecycle.Disposed };
        if (!inSlot)
        {
            // One in use there belongs to an enclosing region, which puts it
            // back on disposal, or to a region never disposed: leaving it to
            // its region lets the guard return an abandoned region's storage.
            if (reusable is not null)
            {
                thread.ReusableRegion = null;
            }

            reusable = thread.MoreReusableRegions is null ? null : TakeMoreReusable(thread);
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
                thread.ReusableRegion = null;
            }
        }

        return new NativeRegionKernel(
            preAllocateBytes,
            NativeMemoryReturn.Reuse);
    }

    // The kernel a budgeted region of this thread kept for the next one with
    // the same budget, unless another thread's refused admission took it.
    private static NativeRegionKernel AcquireBudgeted(
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose,
        NativeMemoryBudget budget)
    {
        if (returnMemoryOnDispose == NativeMemoryReturn.Reuse)
        {
            NativeThreadState thread = NativeThreadState.Current;
            NativeRegionKernel? kept = NativeKeptStorage.Take(ref thread.KeptBudgetedRegion, budget);
            if (kept is not null && kept.TryReactivate(thread, preAllocateBytes))
            {
                return kept;
            }
        }

        return new NativeRegionKernel(
            preAllocateBytes,
            returnMemoryOnDispose,
            budget);
    }

    bool INativeKeptKernel.IsKeptFor(NativeMemoryBudget? budget) => budget is null || _budget == budget;

    void INativeKeptKernel.ReleaseKept() => ReleaseSegments();

    // A nested scope: the slot is in use, so a region behind it serves.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeRegionKernel? TakeMoreReusable(NativeThreadState thread) =>
        thread.MoreReusableRegions!.Pop();

    // The slot kernel's common case: it kept its first segment, which still
    // serves. False leaves everything to AcquireReusable.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReactivateKept(NativeThreadState thread, nuint preAllocateBytes)
    {
        RegionSegmentHeader* segment = _firstSegment;
        if (segment == null
            || _retainEpoch != thread.ReuseEpoch
            || segment->MetricsEpoch != NativeMemoryAccounting.CurrentMetricsEpoch
            || segment->Capacity < preAllocateBytes
            || _generation == int.MaxValue)
        {
            return false;
        }

        Reactivate(thread);
        byte* cursor = (byte*)segment + HeaderBytes;
        _currentSegment = segment;
        _currentCursor = cursor;
        _currentEnd = cursor + segment->Capacity;
        return true;
    }

    private bool TryReactivate(NativeThreadState thread, nuint preAllocateBytes)
    {
        RegionSegmentHeader* segment = _firstSegment;
        // A new measurement epoch observes fresh acquisition, and a short or
        // missing segment cannot satisfy the requested reservation.
        if (_retainEpoch != thread.ReuseEpoch
            || (segment == null
                ? preAllocateBytes != 0
                : segment->MetricsEpoch != NativeMemoryAccounting.CurrentMetricsEpoch
                    || segment->Capacity < preAllocateBytes)
            || _generation == int.MaxValue)
        {
            FreeSegments();
            ReleaseGuard();
            return false;
        }

        Reactivate(thread);
        _currentSegment = segment;
        _currentCursor = segment == null ? null : (byte*)segment + HeaderBytes;
        _currentEnd = segment == null ? null : _currentCursor + segment->Capacity;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Reactivate(NativeThreadState thread)
    {
        NativeMemoryAccounting.PrepareThread();
        Id = NativeOwnerIdentity.Next(thread);
        _generation++;
        _state.Lifecycle = NativeOwnerLifecycle.Active;
        _requestedBytes = 0;
        _peakRetainedBytes = _retainedBytes;
        _activationSegmentCount = _segmentCount;
    }

    private bool TryRetainForReuse()
    {
        // Disposal already proved no initializer or borrow is entered, and no
        // other thread can reach this storage, so detaching it buys no safety.
        NativeThreadState thread = _ownerThread;
        if (_budget is not null)
        {
            return TryKeepBudgeted(thread);
        }

        // A region that never leased, or grew only past the limit, is kept
        // without storage: the next region then allocates no kernel.
        RegionSegmentHeader* keep = null;
        for (RegionSegmentHeader* segment = _firstSegment; segment != null; segment = segment->Next)
        {
            if ((long)segment->Capacity <= ReuseLimitBytes
                && (keep == null || segment->Capacity > keep->Capacity))
            {
                keep = segment;
            }
        }

        long keptBytes = keep == null ? 0 : (long)keep->Capacity;
        // A kernel this thread took from the slot is still in it.
        bool inSlot = ReferenceEquals(thread.ReusableRegion, this);
        if (!inSlot
            && thread.ReusableRegion is not null
            && !(thread.MoreReusableRegions ??= new()).HasRoom(keptBytes))
        {
            return false;
        }

        if (_firstSegment != null && (keep == null || _firstSegment->Next != null))
        {
            FreeSegmentsExcept(keep);
        }

        _retainEpoch = thread.ReuseEpoch;
        if (inSlot)
        {
            return true;
        }

        if (thread.ReusableRegion is null)
        {
            thread.ReusableRegion = this;
        }
        else
        {
            thread.MoreReusableRegions!.Push(this, keptBytes);
        }

        return true;
    }

    // A budgeted region keeps its kernel, with its largest segment within the
    // limit, in its thread's budgeted slot for the next region with the same
    // budget; the storage stays charged to the budget. A developer expects
    // disposal to make room in the budget: a disposed budgeted region used to
    // detach its storage until finalization, so regions made per scope ran
    // the budget out (a 1 GiB budget after 16,368 regions of 64 KiB) before
    // any collection, and each one acquired its storage afresh (533-669 ns
    // per region against 21 without a budget). Now the next region takes the
    // kept storage, and a budget that refuses an admission first takes back
    // what every thread kept for it (NativeEndedThreads.ReleaseKeptFor), so
    // it never refuses while kept storage holds its bytes. The slot is shared
    // with those threads (NativeKeptStorage), so the disposed lifetime's
    // handles end here: none can reach storage another thread may free.
    private bool TryKeepBudgeted(NativeThreadState thread)
    {
        if (_budget!.TraceEnabled || _generation >= int.MaxValue - 1)
        {
            return false;
        }

        RegionSegmentHeader* keep = null;
        for (RegionSegmentHeader* segment = _firstSegment; segment != null; segment = segment->Next)
        {
            if ((long)segment->Capacity <= ReuseLimitBytes
                && (keep == null || segment->Capacity > keep->Capacity))
            {
                keep = segment;
            }
        }

        if (_firstSegment != null && (keep == null || _firstSegment->Next != null))
        {
            FreeSegmentsExcept(keep);
        }

        _retainEpoch = thread.ReuseEpoch;
        _generation++;
        NativeKeptStorage.Keep(ref thread.KeptBudgetedRegion, this);
        return true;
    }

    // The kernels an ended thread kept for its next regions: large segments
    // go to NativeLargeBlocks, the rest to native memory.
    internal static void ReleaseRetained(NativeThreadState thread)
    {
        NativeRegionKernel? kernel = thread.ReusableRegion;
        thread.ReusableRegion = null;
        // A kernel still in use belongs to a region its thread never disposed;
        // its guard returns that storage once the region is unreachable.
        if (kernel is { _state.Lifecycle: NativeOwnerLifecycle.Active })
        {
            kernel = thread.MoreReusableRegions?.Pop();
        }

        while (kernel is not null)
        {
            kernel.ReleaseSegments();
            kernel = thread.MoreReusableRegions?.Pop();
        }

        NativeKeptStorage.Release(ref thread.KeptBudgetedRegion, budget: null);
    }

    private void ReleaseSegments()
    {
        if (_budget is null)
        {
            KeepLargeSegments();
        }

        FreeSegments();
        _guard?.Disarm();
        _guard = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Local<T> LeaseInitialized<T>(
        int length,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateLeaseAdmission(nameof(NativeRegion.Lease));
        nuint byteLength = CalculateByteLength<T>(length);
        nuint alignment = CalculateAlignment<T>();
        long requestedBytes = RequestedAfter(byteLength);
        // The high bit is initializer admission; lower bits remain the local
        // bounded-borrow count. No per-range control or owner field is needed.
        _state.Borrows |= int.MinValue;
        try
        {
            RegionReservation reservation = Reserve(byteLength, alignment);
            Initialize(reservation, length, initializer);
            _requestedBytes = requestedBytes;
            return new Local<T>(this, reservation.Pointer, length, _generation);
        }
        finally
        {
            _state.Borrows &= int.MaxValue;
        }
    }

    internal Local<T> LeaseFilled<T>(
        int length,
        T value)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateLeaseAdmission(nameof(NativeRegion.Lease));
        nuint byteLength = CalculateByteLength<T>(length);
        long requestedBytes = RequestedAfter(byteLength);
        RegionReservation reservation = Reserve(byteLength, CalculateAlignment<T>());
        // An unmanaged fill runs no user code: no initializer admission or rollback.
        new Span<T>((void*)reservation.Pointer, length).Fill(value);
        _requestedBytes = requestedBytes;
        return new Local<T>(this, reservation.Pointer, length, _generation);
    }

    internal Local<T> LeaseUninitialized<T>(int length)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateLeaseAdmission(nameof(NativeRegion.LeaseUninitialized));
        nuint byteLength = CalculateByteLength<T>(length);
        long requestedBytes = RequestedAfter(byteLength);
        RegionReservation reservation = Reserve(byteLength, CalculateAlignment<T>());
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
        if (_state.Lifecycle != NativeOwnerLifecycle.Active)
        {
            throw CreateDisposed(operation);
        }

        if (!_owner.IsCurrent())
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
        if (_state.Lifecycle != NativeOwnerLifecycle.Active)
        {
            throw CreateDisposed(operation);
        }
    }

    // A new lease needs an active region and no entered initializer. Active is
    // zero and an entered initializer sets the borrow count's high bit, so one
    // test of the packed state checks both.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateLeaseAdmission(string operation)
    {
        if ((Unsafe.As<RegionState, ulong>(ref _state) & RegionState.LeaseRefusedMask) != 0)
        {
            ThrowLeaseRefused(operation);
        }
    }

    // The requested bytes with one more lease, added unchecked. A lease's byte
    // count is below 2^62: an int length times an unmanaged size, each below
    // 2^31 (in a 32-bit process CalculateByteLength refuses more than 2^32 - 1).
    // The current sum counts only published leases, which lie apart inside
    // this region's segments, so it is below the bytes the process can map:
    // under 2^57 on every 64-bit platform, under 2^32 in a 32-bit process. The
    // sum therefore stays below 2^63.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long RequestedAfter(nuint byteLength) => _requestedBytes + (long)byteLength;

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowLeaseRefused(string operation)
    {
        ValidateLeaseActive(operation);
        ThrowActiveOperation(operation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ValidateLease(string operation, int generation)
    {
        if (generation != _generation)
        {
            throw CreateDisposed(operation);
        }

        ValidateLeaseActive(operation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnterBorrow(string operation, int generation)
    {
        if (generation != _generation)
        {
            throw CreateDisposed(operation);
        }

        ValidateLeaseActive(operation);
        _state.Borrows++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitBorrow() => _state.Borrows--;

    internal NativeOwnerStatistics GetStatistics()
    {
        ValidateActive(nameof(GetStatistics));
        long usableCapacityBytes = 0;
        for (RegionSegmentHeader* segment = _firstSegment; segment != null; segment = segment->Next)
        {
            usableCapacityBytes = checked(usableCapacityBytes + (long)segment->Capacity);
        }
        return new NativeOwnerStatistics(
            _state.Lifecycle,
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
            InitializedPayloadBytes = _state.Lifecycle == NativeOwnerLifecycle.Active ? _requestedBytes : 0,
            PeakInitializedPayloadBytes = _requestedBytes,
            UsableCapacityBytes = usableCapacityBytes
        };
    }

    internal NativeOwnerDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        if (!_owner.IsCurrent())
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
            _state.Lifecycle, 0, 0, NativeMemoryAccounting.CurrentMetricsEpoch,
            0, 0, 0, currentIndex, -1, _firstSegment == null ? 0 : _segmentCount,
            _state.Lifecycle == NativeOwnerLifecycle.Active ? GetAvailableSegmentCount() : 0,
            0, 0, 0, 0, 0, false)
        {
            OwnerId = Id,
            Model = NativeOwnerModel.ThreadConfinedRegion,
            HistoryOverflowed = false,
            OutstandingNativeBytes = _retainedBytes,
            // A disposed region kept its storage for reuse or freed it.
            DetachedNativeBytes = 0,
            PeakOutstandingNativeBytes = _peakRetainedBytes,
            InitializedPayloadBytes = _state.Lifecycle == NativeOwnerLifecycle.Active ? _requestedBytes : 0,
            PeakInitializedPayloadBytes = _requestedBytes
        };
        GC.KeepAlive(this);
        return snapshot;
    }

    private int GetAvailableSegmentCount() =>
        _currentSegment != null && _currentCursor == (byte*)_currentSegment + HeaderBytes ? 1 : 0;

    // Only NativeRegion.Dispose calls this, and the ref struct cannot leave its
    // construction thread, so disposal needs no thread check.
    // The common case inlines into the region's own Dispose: an active kernel
    // with nothing entered, still in its thread's slot, only marks itself free
    // there. This is what TryRetainForReuse does for it, without the calls. Its
    // place in the slot proves the rest: only a default-policy region with no
    // budget takes or returns the slot's kernel, and one that grows past one
    // segment within the reuse limit leaves the slot (AppendSegment). Two
    // checks, so that a using statement's finally stays small enough for the
    // JIT to copy inline rather than call as a funclet: with the eight checks
    // this replaced, it did not (a region with one lease 15.6 -> 13.2 ns).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Dispose()
    {
        NativeThreadState thread = _ownerThread;
        if (Unsafe.As<RegionState, long>(ref _state) == 0
            && ReferenceEquals(thread.ReusableRegion, this))
        {
            _state.Lifecycle = NativeOwnerLifecycle.Disposed;
            _retainEpoch = thread.ReuseEpoch;
            return;
        }

        DisposeSlow();
    }

    // Every other case, out of line so that the inlined part stays small.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DisposeSlow()
    {
        if (_state.Lifecycle == NativeOwnerLifecycle.Disposed)
        {
            return;
        }

        if (_state.Borrows != 0)
        {
            ThrowActiveOperation(nameof(Dispose));
        }

        _state.Lifecycle = NativeOwnerLifecycle.Disposed;
        if (_returnMemoryOnDispose == NativeMemoryReturn.Free)
        {
            FreeSegments();
            ReleaseGuard();
            return;
        }

        if (TryRetainForReuse())
        {
            return;
        }

        // What is not kept is freed now, not detached for finalization: a
        // developer who disposes a region expects its storage kept for the
        // next owner or given back, and disposal proved no borrow is entered
        // and no other thread can reach it. A budgeted region is not kept when
        // its budget traces, so tracing does not change whether a budget runs
        // out. Without a budget, large segments go to NativeLargeBlocks first.
        if (_budget is null)
        {
            KeepLargeSegments();
        }

        FreeSegments();
        ReleaseGuard();
    }

    // Disposal already proved no borrow is entered, so large segments go to
    // NativeLargeBlocks for the next owner on any thread; the rest stay.
    private void KeepLargeSegments()
    {
        RegionSegmentHeader* remaining = null;
        RegionSegmentHeader** tail = &remaining;
        bool kept = false;
        for (RegionSegmentHeader* segment = _firstSegment, next; segment != null; segment = next)
        {
            next = segment->Next;
            nuint allocationBytes = segment->AllocationBytes;
            if (NativeLargeBlocks.TryKeep(segment, allocationBytes, segment->MetricsEpoch))
            {
                _retainedBytes -= checked((long)allocationBytes);
                kept = true;
            }
            else
            {
                *tail = segment;
                tail = &segment->Next;
            }
        }

        if (!kept)
        {
            return;
        }

        *tail = null;
        _firstSegment = remaining;
        _currentSegment = null;
        _currentCursor = null;
        _currentEnd = null;
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

        // A region without a segment has a null cursor and end, which align to
        // zero and leave no room, so it grows here like a full one. The new
        // cursor is added unchecked: past the room test, aligned <= end and
        // byteLength <= end - aligned, so aligned + byteLength <= end; after
        // GrowSegment the cursor starts a segment of at least byteLength bytes.
        byte* originalCursor = _currentCursor;
        nuint end = (nuint)_currentEnd;
        nuint aligned = byteLength >= NativeVectorAlignment.MinimumBytes
            ? AlignLarge((nuint)originalCursor, end, byteLength, alignment)
            : AlignUp((nuint)originalCursor, alignment);
        if (aligned > end || byteLength > end - aligned)
        {
            GrowSegment(byteLength, "allocation growth");
            originalCursor = _currentCursor;
            aligned = (nuint)originalCursor;
        }

        _currentCursor = (byte*)(aligned + byteLength);
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

        // A kernel in its thread's slot holds at most one segment within the
        // reuse limit, which its inlined disposal relies on: past that it leaves
        // the slot, and its disposal trims it and puts it back.
        if ((segment != _firstSegment || (long)capacity > ReuseLimitBytes)
            && ReferenceEquals(_ownerThread.ReusableRegion, this))
        {
            _ownerThread.ReusableRegion = null;
        }

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
                throw new NativeAllocationFailedException(allocationBytes, OwnerKind, generation: 0, operation, _state.Lifecycle);
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
                    NativeSegment.Alignment);
            }
            if (memory == null)
            {
                throw new NativeAllocationFailedException(
                    allocationBytes,
                    OwnerKind,
                    generation: 0,
                    operation,
                    _state.Lifecycle);
            }

            _peakRetainedBytes = Math.Max(_peakRetainedBytes, prospectiveBytes);
            RegionSegmentHeader* segment =
                (RegionSegmentHeader*)memory;
            *segment = default;
            segment->Capacity = capacity;
            segment->AllocationBytes = allocationBytes;
            segment->AllocationOrdinal = allocationOrdinal;
            if (!recorded)
            {
                metricsEpoch = NativeMemoryAccounting.RecordAllocation(allocationBytes, zeroed: false);
                recorded = true;
            }

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
                _state.Lifecycle,
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

    // Frees every segment but the kept one, which becomes the only segment;
    // with none kept, the region is left without storage. Large segments go
    // to NativeLargeBlocks.
    private void FreeSegmentsExcept(RegionSegmentHeader* keep)
    {
        RegionSegmentHeader* segment = _firstSegment;
        while (segment != null)
        {
            RegionSegmentHeader* next = segment->Next;
            if (segment != keep)
            {
                nuint allocationBytes = segment->AllocationBytes;
                long metricsEpoch = segment->MetricsEpoch;
                if (_budget is not null)
                {
                    int allocationOrdinal = segment->AllocationOrdinal;
                    NativeMemory.AlignedFree(segment);
                    _budget.Release(allocationBytes, Id, allocationOrdinal: allocationOrdinal);
                    NativeMemoryAccounting.RecordFree(allocationBytes, detached: false, metricsEpoch);
                }
                else if (!NativeLargeBlocks.TryKeep(segment, allocationBytes, metricsEpoch))
                {
                    NativeMemory.AlignedFree(segment);
                    NativeMemoryAccounting.RecordFree(allocationBytes, detached: false, metricsEpoch);
                }
            }

            segment = next;
        }

        if (keep == null)
        {
            _firstSegment = null;
            _currentSegment = null;
            _currentCursor = null;
            _currentEnd = null;
            _segmentCount = 0;
            _retainedBytes = 0;
            return;
        }

        // The next lifetime starts with this one segment, as a region that
        // never grew does; ordinals only number the chain (a budget that
        // traces them never keeps a kernel).
        keep->Next = null;
        keep->AllocationOrdinal = 1;
        _firstSegment = keep;
        _currentSegment = keep;
        _segmentCount = 1;
        _retainedBytes = checked((long)keep->AllocationBytes);
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
            int allocationOrdinal = segment->AllocationOrdinal;
            NativeMemory.AlignedFree(segment);
            _budget?.Release(allocationBytes, Id, allocationOrdinal: allocationOrdinal);
            NativeMemoryAccounting.RecordFree(
                allocationBytes,
                detached: false,
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
            _state.Lifecycle);

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
            _state.Lifecycle);

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void ThrowActiveOperation(string operation) =>
        throw new NativeAllocationInUseException(
            "NativeRegion cannot initialize recursively or dispose during an entered initializer or bounded callback.",
            OwnerKind, generation: 0, currentGeneration: 0, operation,
            activeOperationCount: ActiveOperationCount, allocationId: 0, _state.Lifecycle);

    private int ActiveOperationCount =>
        (_state.Borrows & int.MaxValue) + (_state.Borrows < 0 ? 1 : 0);

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

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
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

    // Aligns a cursor within a segment, or the header size, unchecked. The
    // largest alignment is 64 (NativeSegment.Alignment, for the header; a
    // range takes at most 32), so the mask is at most 63, and a cursor lies at
    // or below its segment's end. No platform .NET runs on maps the top 64 KiB
    // of the address space into a process: 64-bit address spaces end below
    // 2^57, win-x86 reserves its top 64 KiB and linux-arm's ends below 3 GiB.
    // A cursor plus 63 therefore cannot wrap.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint AlignUp(nuint value, nuint alignment)
    {
        nuint mask = alignment - 1;
        return (value + mask) & ~mask;
    }

    // This kernel's emergency finalizer, shared through its thread; see
    // NativeFinalizationGuard. A finalizable kernel cost about 85 ns to
    // allocate, paid by every region that did not reuse a kernel (one that
    // frees its storage on disposal, or has a budget). Null once the kernel
    // returned its storage; a kept kernel keeps its guard across lifetimes.
    private NativeFinalizationGuard? _guard;

    void INativeFinalizationTarget.FinalizeNativeStorage() => FreeSegments();

    // After the storage is actually returned, on the owner thread.
    private void ReleaseGuard()
    {
        NativeFinalizationGuard? guard = _guard;
        if (guard is not null)
        {
            _guard = null;
            guard.Release(_ownerThread);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RegionSegmentHeader
    {
        internal RegionSegmentHeader* Next;
        internal nuint Capacity;
        internal nuint AllocationBytes;
        internal long MetricsEpoch;
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

// A region kernel's lifecycle and entered-operation count, laid out together.
// Active is zero, so "active with nothing entered" is a zero 64-bit value on
// either byte order.
[StructLayout(LayoutKind.Sequential)]
internal struct RegionState
{
    // The lifecycle's bits and the initializer bit, as one 64-bit value.
    internal static readonly ulong LeaseRefusedMask =
        BitConverter.IsLittleEndian ? 0x8000_0000_FFFF_FFFFUL : 0xFFFF_FFFF_8000_0000UL;

    internal NativeOwnerLifecycle Lifecycle;

    // The high bit is initializer admission; the lower bits count bounded borrows.
    internal int Borrows;
}
