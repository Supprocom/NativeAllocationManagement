using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Adds transferable leases to synchronized typed owners.</summary>
public static class NativeTransferPoolExtensions
{
    /// <summary>Rents an initialized unmanaged range for destructive ownership transfer.</summary>
    public static NativeTransfer<T> RentTransferable<T>(
        this NativeConcurrentPool<T> pool,
        int length,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(pool);
        NativeOwnerKernel kernel = pool.KernelForTransfer;
        NativePoolLease lease = kernel.RentInitialized(
            length,
            scoped: false,
            initializer,
            transferable: true);
        return NativePoolTransferControl<T>.Publish(
            kernel,
            lease,
            "NativeConcurrentPool.RentTransferable");
    }

    /// <summary>Rents a range filled with one value for destructive ownership transfer, without an initializer callback.</summary>
    /// <param name="pool">The synchronized pool.</param>
    /// <param name="length">The complete initialized element count.</param>
    /// <param name="value">The value written to every element before publication.</param>
    public static NativeTransfer<T> RentTransferable<T>(
        this NativeConcurrentPool<T> pool,
        int length,
        T value)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(pool);
        NativeOwnerKernel kernel = pool.KernelForTransfer;
        NativePoolLease lease = kernel.RentFilled(length, value, fill: true, transferable: true);
        return NativePoolTransferControl<T>.Publish(
            kernel,
            lease,
            "NativeConcurrentPool.RentTransferable");
    }
}

/// <summary>A heap-storable value capability with destructive unique ownership.</summary>
/// <typeparam name="T">The unmanaged element type in the native range.</typeparam>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1815", Justification = "A linear ownership capability deliberately has no value-equality contract; copying it does not acquire another owner.")]
public readonly struct NativeTransfer<T> : IDisposable
    where T : unmanaged
{
    private readonly NativeTransferControl<T>? _control;
    private readonly long _authorityVersion;

    private NativeTransfer(NativeTransferControl<T> control, long authorityVersion)
    {
        _control = control;
        _authorityVersion = authorityVersion;
    }

    /// <summary>Gets the stable backing-ownership lineage, retained through move.</summary>
    public long Id => GetControl(nameof(Id)).Id;

    /// <summary>Gets the initialized logical element count.</summary>
    public int Length => GetControl(nameof(Length)).GetLength(_authorityVersion);

    /// <summary>Gets the physical element capacity.</summary>
    public int Capacity => GetControl(nameof(Capacity)).GetCapacity(_authorityVersion);

    /// <summary>Creates a transfer that owns a native copy of the values.</summary>
    /// <param name="values">The values copied into the new native range.</param>
    /// <returns>The unique owning binding; dispose it or move it to the next owner.</returns>
    /// <remarks>
    /// No pool or builder is involved: the storage and its control come from the
    /// calling thread's reuse slot when one fits, otherwise from native memory.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "The factory creates the exact element type, like Move.")]
    public static NativeTransfer<T> Create(scoped ReadOnlySpan<T> values) =>
        NativeBuilderControl<T>.CreateTransfer(values);

    /// <summary>Creates a transfer of <paramref name="length"/> elements, each set to <paramref name="value"/>.</summary>
    /// <param name="length">The element count.</param>
    /// <param name="value">The value written to every element before publication.</param>
    /// <returns>The unique owning binding; dispose it or move it to the next owner.</returns>
    /// <remarks>Storage comes from the calling thread's reuse slot when one fits, otherwise from native memory.</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "The factory creates the exact element type, like Move.")]
    public static NativeTransfer<T> Create(int length, T value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return NativeBuilderControl<T>.CreateTransfer(length, value);
    }

    /// <summary>Creates a transfer of <paramref name="length"/> elements without writing them.</summary>
    /// <param name="length">The element count.</param>
    /// <returns>The unique owning binding; dispose it or move it to the next owner.</returns>
    /// <remarks>The contents are unspecified: they may hold values an earlier lease in this process wrote. Write every element before reading it (for example through <see cref="Process{TState, TResult, TProcessor}(in TState)"/>), or use <see cref="Create(int, T)"/>.</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "The factory creates the exact element type, like Move.")]
    public static NativeTransfer<T> CreateUninitialized(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return NativeBuilderControl<T>.CreateUninitializedTransfer(length);
    }

    /// <summary>Moves unique authority without allocating and clears the source binding.</summary>
    /// <remarks>
    /// Copies of the previous capability become stale. The binding is consumed even
    /// when the move fails. Concurrent access to the same source variable requires
    /// caller synchronization; competing independent aliases arbitrate in the control.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "Destructive transfer uses the exact element type and invalidates a ref source.")]
    public static NativeTransfer<T> Move(ref NativeTransfer<T>? source)
    {
        NativeTransfer<T> observed = source ?? throw new ArgumentNullException(
            nameof(source), "The transfer source has no ownership.");
        source = null;
        NativeTransferControl<T> control = observed.GetControl(nameof(Move));
        long nextVersion = control.Move(observed._authorityVersion);
        return new NativeTransfer<T>(control, nextVersion);
    }

    /// <summary>Runs one synchronous bounded callback over the initialized native span.</summary>
    public void Access(NativeLeaseAction<T> action) =>
        GetControl(nameof(Access)).Access(_authorityVersion, action);

    /// <summary>Runs one synchronous bounded callback and returns its managed result.</summary>
    public TResult Read<TResult>(NativeLeaseFunc<T, TResult> action) =>
        GetControl(nameof(Read)).Read(_authorityVersion, action);

    /// <summary>Runs one compile-time processor over the initialized native span.</summary>
    /// <typeparam name="TState">The caller state type.</typeparam>
    /// <typeparam name="TResult">The result type, which cannot contain the borrowed span.</typeparam>
    /// <typeparam name="TProcessor">The processor; its static member is called without a delegate.</typeparam>
    public TResult Process<TState, TResult, TProcessor>(scoped in TState state)
        where TState : allows ref struct
        where TProcessor : struct, INativeSpanProcessor<T, TState, TResult> =>
        GetControl(nameof(Process)).Process<TState, TResult, TProcessor>(_authorityVersion, in state);

    /// <summary>Releases this unique binding exactly once.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1065", Justification = "Disposal rejects stale authority and active bounded use before freeing native memory.")]
    public void Dispose() => GetControl(nameof(Dispose)).Dispose(_authorityVersion);

    /// <summary>Samples actual control state without granting native authority; available on stale and returned non-default values.</summary>
    public NativeTransferStatistics CaptureSnapshot() => GetControl(nameof(CaptureSnapshot)).CaptureSnapshot(_authorityVersion);

    /// <summary>Retries consumed terminal cleanup without reopening ownership; false means the control is not an idle consumed terminal source or another attempt owns cleanup.</summary>
    public bool TryCompletePayloadReturn() => GetControl(nameof(TryCompletePayloadReturn)).TryCompletePayloadReturn();

    internal object? ControlForTest => _control;

    internal NativeTransferControl<T> CustodyForSharing => GetControl("NativeShared.Adopt");
    internal long SharingAuthorityVersion => _authorityVersion;

    internal static NativeTransfer<T> Create(NativeOwnerKernel kernel, NativeRegionAllocation allocation, string operation) =>
        new(NativeTransferControl<T>.Create(kernel, allocation, operation), authorityVersion: 1);

    internal static NativeTransfer<T> CreateOwnedBlock(NativeBlock block, int length, int capacity) =>
        new(NativeTransferControl<T>.CreateOwnedBlock(block, length, capacity), authorityVersion: 1);

    internal static NativeTransfer<T> CreateAdmitted(NativeTransferControl<T> control, long authorityVersion) =>
        new(control, authorityVersion);

    private NativeTransferControl<T> GetControl(string operation) =>
        _control ?? throw new NativeAllocationUninitializedException(nameof(NativeTransfer<T>), operation);
}

// One acquisition-time control, not one wrapper/finalizer per move.
internal partial class NativeTransferControl<T>
    where T : unmanaged
{
    private protected const int Active = 1;
    private protected const int Moving = 2;
    private const int Moved = 3;
    private protected const int Disposing = 4;
    private protected const int Disposed = 5;
    private protected const int Retiring = 6;
    private protected const int Finalized = 7;
    private const int Shared = 10;

    private NativeOwnerKernel? _kernel;
    private NativeSharedControl<T>? _sharingControl;
    private NativeAllocation? _allocationState;
    private long _ownerId;
    private bool _borrowedBacking;
    // Direct custody uses the descriptor's epoch/owner as allocation metadata.
    // Kernel custody has no direct block: the same two scalar slots hold its
    // captured generation/allocation identity instead of duplicate fields.
    private NativeBlockSlot _block;
    private int _length;
    private int _capacity;
    private int _state;
    private int _operationAdmission;
    // One borrow at a time enters here with a CAS and leaves with a store; a
    // second concurrent borrow uses _operationAdmission. Every check of active
    // borrows reads both.
    private int _loneBorrow;
    private long _authorityVersion = 1;
    private int _peakBorrows;
    private bool _borrowed;
    private int _payloadReturned;
    private long _returnFailures;
    private bool _historyOverflowed;
    private bool _sharingAllocationEntered;
    private bool _sharingGenerationEntered;

    private protected NativeTransferControl(
        NativeOwnerKernel kernel,
        NativeAllocation allocationState,
        long generation,
        long allocationId)
    {
        _kernel = kernel;
        _allocationState = allocationState;
        _ownerId = kernel.Id;
        _block.Set(KernelBlock(kernel, allocationState, generation, allocationId, out _borrowedBacking));
        _length = allocationState.Length;
        _capacity = allocationState.Capacity;
        _state = Active;
    }

    // A closed kernel no longer roots its domain. The existing block slot
    // keeps identity and still-pending trace custody until real return.
    private static NativeBlock KernelBlock(
        NativeOwnerKernel kernel,
        NativeAllocation allocationState,
        long generation,
        long allocationId,
        out bool borrowedBacking)
    {
        NativeSegment? segment = allocationState.Segment;
        borrowedBacking = segment is { AllocationByteLength: 0 };
        nuint backingBytes = borrowedBacking ? segment!.ByteLength : segment?.AllocationByteLength ?? 0;
        _ = checked((long)backingBytes);
        return new NativeBlock(IntPtr.Zero, backingBytes, generation, kernel.BudgetForSharing, allocationId);
    }

    private protected NativeTransferControl(
        NativeBlock block,
        int length,
        int capacity,
        bool published)
    {
        _block.Set(block);
        _ownerId = block.OwnerId;
        _ = checked((long)block.ByteLength);
        _length = length;
        _capacity = capacity;
        _state = published ? Active : 0;
    }

    internal long Id => _ownerId;

    internal virtual NativeTransferStatistics CaptureSnapshot(long bindingVersion)
    {
        int state = Volatile.Read(ref _state);
        // Failed pin preparation may leave the private moved binding able to
        // retry cleanup. It never republishes a unique owner to the caller.
        if (state == Active && _sharingControl is not null) state = Shared;
        long authority = Volatile.Read(ref _authorityVersion);
        bool returned = Volatile.Read(ref _payloadReturned) != 0;
        NativeOwnerKernel? kernel = Volatile.Read(ref _kernel);
        NativeAllocation? allocation = _allocationState;
        NativeGeneration? generation = allocation?.GenerationState;
        bool allocationActive = kernel is null || (generation is not null && allocation is not null
            && kernel.TransferAuthorityIsActive(generation, allocation, _block.MetricsEpoch, _block.OwnerId));
        bool bindingActive = !returned && state == Active && authority == bindingVersion && allocationActive;
        int borrows = ActiveBorrows;
        bool backingPresent = !returned && (kernel is null
            || (allocation?.Segment is { } segment && segment.Pointer != IntPtr.Zero));
        long initialized = (long)_length * Unsafe.SizeOf<T>();
        return new()
        {
            OwnerId = _ownerId,
            AllocationId = _block.OwnerId,
            BindingVersion = bindingVersion,
            AuthorityVersion = authority,
            BindingIsActive = bindingActive,
            Lifecycle = state switch
            {
                Active => allocationActive ? NativeTransferLifecycle.Active : NativeTransferLifecycle.Invalidated,
                Moving => NativeTransferLifecycle.Moving,
                Disposing => NativeTransferLifecycle.Returning,
                Retiring => NativeTransferLifecycle.Retiring,
                Disposed => NativeTransferLifecycle.Returned,
                Finalized => NativeTransferLifecycle.Finalizing,
                Shared => NativeTransferLifecycle.Shared,
                _ => NativeTransferLifecycle.Uninitialized
            },
            ActiveBorrowCount = borrows,
            PeakBorrowCount = Math.Max(Volatile.Read(ref _peakBorrows), Volatile.Read(ref _borrowed) ? 1 : 0),
            MoveCount = authority - 1,
            LiveUniqueOwnerCount = !returned && state == Active && allocationActive ? 1 : 0,
            HasReturnObligation = !returned,
            InitializedPayloadBytes = backingPresent && (allocationActive || state == Shared || borrows != 0) ? initialized : 0,
            PeakInitializedPayloadBytes = initialized,
            OwnedBackingBytes = !backingPresent || _borrowedBacking ? 0 : (long)_block.ByteLength,
            BorrowedBackingBytes = !backingPresent || !_borrowedBacking ? 0 : (long)_block.ByteLength,
            PeakOwnedBackingBytes = _borrowedBacking ? 0 : (long)_block.ByteLength,
            PayloadReturnCount = returned ? 1 : 0,
            PayloadReturnFailureCount = Volatile.Read(ref _returnFailures),
            HistoryOverflowed = Volatile.Read(ref _historyOverflowed),
            ControlFieldBytes = 3L * IntPtr.Size + 3L * sizeof(long) + 7L * sizeof(int) + 5L * sizeof(bool) + Unsafe.SizeOf<NativeBlockSlot>()
        };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "Successful terminal retry disarms the acquisition-time emergency finalizer.")]
    internal bool TryCompletePayloadReturn()
    {
        if (Volatile.Read(ref _payloadReturned) != 0) return true;
        if (ActiveBorrows != 0
            || Interlocked.CompareExchange(ref _state, Disposing, Retiring) != Retiring) return false;
        try
        {
            ReturnStorage("NativeTransfer.RetryReturn");
            Volatile.Write(ref _state, Disposed);
            GC.SuppressFinalize(this);
            return true;
        }
        catch
        {
            bool returned = Volatile.Read(ref _payloadReturned) != 0;
            Volatile.Write(ref _state, returned ? Disposed : Retiring);
            if (returned) GC.SuppressFinalize(this);
            throw;
        }
    }

    internal int GetLength(long authorityVersion) => Validate(authorityVersion, nameof(NativeTransfer<T>.Length)).Length;

    internal int GetCapacity(long authorityVersion) => Validate(authorityVersion, nameof(NativeTransfer<T>.Capacity)).Capacity;

    internal bool StorageHasBeenReturned => Volatile.Read(ref _payloadReturned) != 0;

    /// <summary>Runs one synchronous bounded callback over the native span.</summary>
    internal void Access(long authorityVersion, NativeLeaseAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        bool lone = EnterTransferOperation(authorityVersion, nameof(Access));
        try
        {
            if (_kernel is null)
            {
                action(new NativeLeaseView<T>(
                    _block.Pointer,
                    _length));
            }
            else
            {
                AccessKernelLease(_kernel, action);
            }
        }
        finally
        {
            ExitTransferOperation(lone);
            GC.KeepAlive(this);
        }
    }

    // Out of line, as ReadKernelLease, so a direct transfer's Access stays small.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AccessKernelLease(NativeOwnerKernel kernel, NativeLeaseAction<T> action)
    {
        NativeAllocation allocation = _allocationState!;
        if (kernel.TryEnterCachedTransferOperation(allocation, _block.MetricsEpoch, _block.OwnerId, out NativePoolThreadCache? cache, out int epoch))
        {
            try
            {
                action(new NativeLeaseView<T>(allocation));
            }
            finally
            {
                kernel.ExitCacheCountedOperation(allocation.GenerationState, cache, epoch);
            }

            return;
        }

        NativeOperationToken token =
            EnterKernelOperation(nameof(Access));
        try
        {
            action(token.GetView<T>());
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Runs one synchronous bounded callback and returns its managed result.</summary>
    internal TResult Read<TResult>(long authorityVersion, NativeLeaseFunc<T, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        bool lone = EnterTransferOperation(authorityVersion, nameof(Read));
        try
        {
            if (_kernel is null)
            {
                return action(new NativeLeaseView<T>(
                    _block.Pointer,
                    _length));
            }

            return ReadKernelLease(_kernel, action);
        }
        finally
        {
            ExitTransferOperation(lone);
            GC.KeepAlive(this);
        }
    }

    // Out of line, so a direct transfer's Read stays small: a lease cached on
    // this thread is read in one counted operation (21 ns down to 17), any
    // other through a token.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private TResult ReadKernelLease<TResult>(NativeOwnerKernel kernel, NativeLeaseFunc<T, TResult> action)
    {
        NativeAllocation allocation = _allocationState!;
        if (kernel.TryEnterCachedTransferOperation(allocation, _block.MetricsEpoch, _block.OwnerId, out NativePoolThreadCache? cache, out int epoch))
        {
            try
            {
                return action(new NativeLeaseView<T>(allocation));
            }
            finally
            {
                kernel.ExitCacheCountedOperation(allocation.GenerationState, cache, epoch);
            }
        }

        NativeOperationToken token =
            EnterKernelOperation(nameof(Read));
        try
        {
            return action(token.GetView<T>());
        }
        finally
        {
            token.Dispose();
        }
    }

    internal TResult Process<TState, TResult, TProcessor>(long authorityVersion, scoped in TState state)
        where TState : allows ref struct
        where TProcessor : struct, INativeSpanProcessor<T, TState, TResult>
    {
        bool lone = EnterTransferOperation(authorityVersion, nameof(NativeTransfer<T>.Process));

        // A direct block and a lease cached on this thread share one call of
        // the processor, so a caller inlining this method inlines the processor
        // once; any other lease takes the token path out of line. Nothing
        // between the entry above and the protected region can throw, and one
        // region with a small finally lets the JIT run its exit inline instead
        // of calling a finally funclet.
        NativeOwnerKernel? kernel = _kernel;
        NativeAllocation? allocation = null;
        NativePoolThreadCache? cache = null;
        int epoch = 0;
        Span<T> span;
        if (kernel is null)
        {
            unsafe
            {
                span = new Span<T>((void*)_block.Pointer, _length);
            }
        }
        else
        {
            allocation = _allocationState!;
            if (!kernel.TryEnterCachedTransferOperation(allocation, _block.MetricsEpoch, _block.OwnerId, out cache, out epoch))
            {
                return ProcessThroughToken<TState, TResult, TProcessor>(lone, in state);
            }

            span = NativeOwnerKernel.CachedSpan<T>(allocation, allocation.Length);
        }

        try
        {
            return TProcessor.Invoke(span, in state);
        }
        finally
        {
            if (cache is not null)
            {
                ExitCachedProcess(kernel!, allocation!, cache, epoch);
            }

            ExitTransferOperation(lone);
            GC.KeepAlive(this);
        }
    }

    // Out of line, so the finally above stays small enough to run inline.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExitCachedProcess(NativeOwnerKernel kernel, NativeAllocation allocation, NativePoolThreadCache cache, int epoch) =>
        kernel.ExitCacheCountedOperation(allocation.GenerationState, cache, epoch);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private TResult ProcessThroughToken<TState, TResult, TProcessor>(bool lone, scoped in TState state)
        where TState : allows ref struct
        where TProcessor : struct, INativeSpanProcessor<T, TState, TResult>
    {
        try
        {
            NativeOperationToken token = EnterKernelOperation(nameof(NativeTransfer<T>.Process));
            try
            {
                return TProcessor.Invoke(token.GetView<T>().AsSpan(), in state);
            }
            finally
            {
                token.Dispose();
            }
        }
        finally
        {
            ExitTransferOperation(lone);
            GC.KeepAlive(this);
        }
    }

    /// <summary>Returns this transfer's storage exactly once.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1065", Justification = "Disposal must reject active bounded use before freeing native memory.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "The public unique capability delegates terminal release to its acquisition-time finalizable control.")]
    internal void Dispose(long authorityVersion)
    {
        int observed = Interlocked.CompareExchange(
            ref _state,
            Disposing,
            Active);
        if (observed != Active)
        {
            ThrowInactive(nameof(Dispose), observed);
        }

        if (Volatile.Read(ref _authorityVersion) != authorityVersion)
        {
            Volatile.Write(ref _state, Active);
            ThrowInactive(nameof(Dispose), Moved);
        }

        // The state CAS above and an entering callback's admission CAS or
        // increment are both full fences, so either that callback sees Disposing
        // and leaves, or this read sees it. Admission needs no separate close here.
        if (BorrowsRemainActive())
        {
            Volatile.Write(ref _state, Active);
            throw new InvalidOperationException(
                "NativeTransfer.Dispose cannot run during an active callback.");
        }

        try
        {
            // A handed-off control already published Disposed and may serve
            // another value on another thread: do not touch it again.
            if (TryHandOffForReuse())
            {
                return;
            }

            ReturnStorage("NativeTransfer.Dispose");
            Volatile.Write(ref _state, Disposed);
            GC.SuppressFinalize(this);
        }
        catch
        {
            if (Volatile.Read(ref _payloadReturned) != 0)
            {
                Volatile.Write(ref _state, Disposed);
                GC.SuppressFinalize(this);
            }
            else
            {
                NativeOperationAdmission.Open(ref _operationAdmission);
                Volatile.Write(ref _state, Active);
            }
            throw;
        }
    }

    internal static NativeTransferControl<T> Create(
        NativeOwnerKernel kernel,
        NativeRegionAllocation allocation,
        string operation) =>
        Create(
            kernel,
            allocation.AllocationState,
            allocation.Generation,
            allocation.AllocationId,
            operation);

    private static NativeTransferControl<T> Create(
        NativeOwnerKernel kernel,
        NativeAllocation allocationState,
        long generation,
        long allocationId,
        string operation)
    {
        try
        {
            return new NativeTransferControl<T>(
                kernel,
                allocationState,
                generation,
                allocationId);
        }
        catch
        {
            kernel.ReturnLease(
                generation,
                allocationId,
                operation);
            throw;
        }
    }

    internal static NativeTransferControl<T> CreateOwnedBlock(
        NativeBlock block,
        int length,
        int capacity) =>
        new(
            block,
            length,
            capacity,
            published: true);

    private NativeHandleMetadata Validate(long authorityVersion, string operation)
    {
        EnsureActive(authorityVersion, operation);
        return _kernel is null
            ? new NativeHandleMetadata(_length, _capacity)
            : ValidateKernelHandle(_kernel, operation);
    }

    // A lease current in this thread's cache needs no kernel operation to show
    // its shape (21 ns down to 10); anything else is decided, and reported, by
    // ValidateHandle as before.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativeHandleMetadata ValidateKernelHandle(NativeOwnerKernel kernel, string operation) =>
        kernel.TryReadCachedTransferShape(_allocationState!, _block.MetricsEpoch, _block.OwnerId, out NativeHandleMetadata shape)
            ? shape
            : kernel.ValidateHandle(
                _allocationState!.GenerationState,
                _allocationState!,
                _block.MetricsEpoch,
                _block.OwnerId,
                operation);

    private NativeOperationToken EnterKernelOperation(
        string operation) =>
        _kernel!.EnterOperation(
            _allocationState!.GenerationState,
            _allocationState!,
            _block.MetricsEpoch,
            _block.OwnerId,
            operation);

    private protected int ActiveBorrows =>
        NativeOperationAdmission.Count(ref _operationAdmission) + Volatile.Read(ref _loneBorrow);

    // Called after the state left Active, so no borrow can newly enter. A
    // borrow still counted may be an alias that passed its first check before
    // that change and is about to refuse itself: wait a few microseconds for
    // it before treating it as an active callback.
    private protected bool BorrowsRemainActive()
    {
        if (ActiveBorrows == 0)
        {
            return false;
        }

        return WaitForRefusedBorrows();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool WaitForRefusedBorrows()
    {
        for (int attempt = 0; attempt < 64; attempt++)
        {
            Thread.SpinWait(8);
            if (ActiveBorrows == 0)
            {
                return false;
            }
        }

        return true;
    }

    // Returns true for the lone borrow, which ExitTransferOperation must be told.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected bool EnterTransferOperation(long authorityVersion, string operation)
    {
        EnsureActive(authorityVersion, operation);
        if (NativeTransferRacesForTest.Enabled)
        {
            NativeTransferRacesForTest.Run(NativeTransferRacesForTest.AfterActiveCheck);
        }

        if (Interlocked.CompareExchange(ref _loneBorrow, 1, 0) == 0)
        {
            if (Volatile.Read(ref _state) == Active && Volatile.Read(ref _authorityVersion) == authorityVersion)
            {
                _borrowed = true;
                return true;
            }

            LeaveRefusedLoneBorrow(operation);
        }

        EnterSharedTransferOperation(authorityVersion, operation);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LeaveRefusedLoneBorrow(string operation)
    {
        Interlocked.Exchange(ref _loneBorrow, 0);
        int state = Volatile.Read(ref _state);
        if (state == Retiring)
        {
            CompleteRetirement();
        }

        ThrowInactive(operation, state == Active ? Moved : state);
    }

    // A borrow while another one holds the lone slot.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EnterSharedTransferOperation(long authorityVersion, string operation)
    {
        int enteredCount = Interlocked.Increment(ref _operationAdmission);
        if (enteredCount <= 0)
        {
            RefuseSharedEntry(enteredCount, operation);
        }

        int state = Volatile.Read(ref _state);
        if (state == Active && Volatile.Read(ref _authorityVersion) == authorityVersion)
        {
            RecordPeakBorrows(enteredCount + 1);
            return;
        }

        ExitSharedTransferOperation();
        ThrowInactive(operation, state == Active ? Moved : state);
    }

    // A closed admission refused this borrow. Its increment is taken back at
    // once, but the last borrow of a retiring transfer may have counted it and
    // left the retirement to this decrement.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RefuseSharedEntry(int entered, string operation)
    {
        if (NativeTransferRacesForTest.Enabled)
        {
            NativeTransferRacesForTest.Run(NativeTransferRacesForTest.BeforeRefusedExit);
        }

        Interlocked.Decrement(ref _operationAdmission);
        if (entered == int.MinValue)
        {
            throw new InvalidOperationException(
                "The native operation count reached its limit.");
        }

        int state = Volatile.Read(ref _state);
        if (state == Retiring)
        {
            CompleteRetirement();
        }

        ThrowInactive(operation, state);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RecordPeakBorrows(int enteredCount)
    {
        int peak = Volatile.Read(ref _peakBorrows);
        while (enteredCount > peak)
        {
            int observed = Interlocked.CompareExchange(ref _peakBorrows, enteredCount, peak);
            if (observed == peak) break;
            peak = observed;
        }
    }

    // The lone exit is a release store: a retiring move fences every processor
    // before it rechecks, so either it sees this exit or this sees Retiring.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void ExitTransferOperation(bool lone)
    {
        if (lone)
        {
            Volatile.Write(ref _loneBorrow, 0);
            if (Volatile.Read(ref _state) == Retiring)
            {
                CompleteRetirement();
            }

            return;
        }

        ExitSharedTransferOperation();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ExitSharedTransferOperation()
    {
        int remaining =
            NativeOperationAdmission.Exit(ref _operationAdmission);
        if (remaining == 0
            && Volatile.Read(ref _state) == Retiring)
        {
            CompleteRetirement();
        }
    }

    // Every exit that may be the last calls this after reading Retiring. A
    // shared exit is a full fence, a lone exit only a release store, so a lone
    // borrow that still looks active is rechecked after fencing every
    // processor: its exit store is then visible, or that exit reads Retiring
    // and completes the retirement itself.
    private void CompleteRetirement()
    {
        if (NativeOperationAdmission.Count(ref _operationAdmission) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _loneBorrow) != 0)
        {
            Interlocked.MemoryBarrierProcessWide();
            if (Volatile.Read(ref _loneBorrow) != 0)
            {
                return;
            }
        }

        if (Interlocked.CompareExchange(ref _state, Disposing, Retiring) == Retiring)
        {
            if (ReturnStorageFromFinalizer()) Volatile.Write(ref _state, Disposed);
            else Volatile.Write(ref _state, Retiring);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "Failed consumed moves release storage and disarm the acquisition-time finalizer.")]
    internal long Move(long authorityVersion)
    {
        NativeMemoryBudget? budget = _kernel?.BudgetForSharing ?? _block.Budget;
        int observed = Interlocked.CompareExchange(
            ref _state,
            Moving,
            Active);
        if (observed != Active)
        {
            ThrowInactive(nameof(Move), observed);
        }

        // Validate after claiming the state, not only before the CAS: a competing
        // move can finish and reopen Active between those two observations.
        if (Volatile.Read(ref _authorityVersion) != authorityVersion)
        {
            Volatile.Write(ref _state, Active);
            ThrowInactive(nameof(Move), Moved);
        }

        if (NativeOperationAdmission.Close(ref _operationAdmission) + Volatile.Read(ref _loneBorrow) != 0
            && BorrowsRemainActive())
        {
            try
            {
                NativeMemoryTestHooks.NotifyBeforeOperationEntry("NativeTransfer.Retire");
            }
            finally
            {
                // A full fence: the recheck below reads the counts only after
                // every exit can see Retiring.
                Interlocked.Exchange(ref _state, Retiring);
                TraceOwnership(budget, NativeMemoryTraceKind.Retired);
                // The last callback may have exited while the state was Moving.
                // Recheck after publication; the terminal CAS arbitrates with its exit.
                CompleteRetirement();
            }
            throw new InvalidOperationException(
                "NativeTransfer.Move found an active callback. The source will return after that callback ends.");
        }

        try
        {
            MovePublication publication = new(
                this,
                checked(authorityVersion + 1));
            if (_kernel is null)
            {
                publication.Publish();
            }
            else
            {
                _kernel.TransferLeaseAuthority(
                    _allocationState!.GenerationState,
                    _allocationState!,
                    _block.MetricsEpoch,
                    _block.OwnerId,
                    "NativeTransfer.Move",
                    publication,
                    static state => state.Publish());
            }
            return publication.AuthorityVersion;
        }
        catch
        {
            if (ReturnStorageFromFinalizer())
            {
                Volatile.Write(ref _state, Disposed);
                GC.SuppressFinalize(this);
            }
            else Volatile.Write(ref _state, Retiring);
            throw;
        }
    }

    private void PublishMove(long authorityVersion)
    {
        Volatile.Write(ref _authorityVersion, authorityVersion);
        // Clear only the closed bit: an alias of the old version that is
        // stepping out of a refused entry still holds its increment.
        NativeOperationAdmission.Open(ref _operationAdmission);
        NativeMemoryBudget? budget = _kernel?.BudgetForSharing ?? _block.Budget;
        TraceOwnership(budget, NativeMemoryTraceKind.Moved);
        Volatile.Write(ref _state, Active);
    }

    private protected virtual void ReturnStorage(string operation)
    {
        if (Volatile.Read(ref _payloadReturned) != 0) return;
        NativeMemoryBudget? budget = _kernel?.BudgetForSharing ?? _block.Budget;
        long ordinal = budget is { TraceEnabled: true } ? BackingOrdinal : 0;
        try
        {
            NativeMemoryTestHooks.CheckManagedPublicationBoundary(operation, 4, "unique payload authority return");
            if (_kernel is null) NativeBlockAllocator.FreeOrKeep(_block.Value);
            else _kernel.ReturnTransferLease(_allocationState!, _block.MetricsEpoch, _block.OwnerId, operation);
        }
        catch
        {
            RecordReturnFailure();
            throw;
        }
        // Stale aliases retain observation, not old pooled generations or budget
        // objects once the return obligation really ended.
        CompleteStorageReturn();
        NativeMemoryTestHooks.CheckManagedPublicationBoundary(operation, 5, "unique return trace emission after successful cleanup");
        TraceOwnership(budget, NativeMemoryTraceKind.UniqueReturned, ordinal);
    }

    private protected long BackingOrdinal => _allocationState?.Segment?.AllocationOrdinal
        ?? (_block.Pointer == IntPtr.Zero ? 0 : 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TraceOwnership(NativeMemoryBudget? budget, NativeMemoryTraceKind kind, long? ordinal = null)
    {
        if (budget is not { TraceEnabled: true }) return;
        budget.RecordOwnershipTransition(kind, _ownerId, _block.OwnerId,
            _block.ByteLength, ordinal ?? BackingOrdinal);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "This boundary captures any failure to preserve cleanup and report the original error.")]
    private bool ReturnStorageFromFinalizer()
    {
        try
        {
            ReturnStorage("NativeTransfer.Finalize");
            return true;
        }
        catch
        {
            return Volatile.Read(ref _payloadReturned) != 0;
        }
    }

    private void EnsureActive(long authorityVersion, string operation)
    {
        int state = Volatile.Read(ref _state);
        if (state != Active)
        {
            ThrowInactive(operation, state);
        }
        if (Volatile.Read(ref _authorityVersion) != authorityVersion)
        {
            ThrowInactive(operation, Moved);
        }
    }

    // Small enough for the JIT to see that it never returns, so the guards
    // that call it keep their callers' registers; the message is built out of line.
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowInactive(
        string operation,
        int state) =>
        throw CreateInactiveException(operation, state);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception CreateInactiveException(
        string operation,
        int state)
    {
        if (state is Disposed or Finalized)
        {
            return new ObjectDisposedException(
                $"NativeTransfer<{typeof(T).Name}>",
                $"NativeTransfer.{operation} cannot run after disposal.");
        }

        if (state is Moving or Moved or Retiring)
        {
            return new InvalidOperationException(
                $"NativeTransfer.{operation} cannot run because ownership moved or is moving.");
        }

        return new InvalidOperationException(
            $"NativeTransfer.{operation} cannot run without active ownership.");
    }

    private void FinalizeLease()
    {
        if (_sharingControl is not null)
        {
            FinalizeSharedPayload();
            return;
        }
        int observed = Volatile.Read(ref _state);
        if (!CanFinalize(observed)
            || Interlocked.CompareExchange(ref _state, Finalized, observed) != observed)
        {
            return;
        }

        if (NativeOperationAdmission.Close(ref _operationAdmission) + Volatile.Read(ref _loneBorrow) != 0
            || !ReturnStorageFromFinalizer())
        {
            Volatile.Write(ref _state, Retiring);
            GC.ReRegisterForFinalize(this);
        }
        else Volatile.Write(ref _state, Disposed);
    }

    private protected virtual bool CanFinalize(int state) => state is Active or Retiring;

    private protected long CurrentAuthorityVersion => Volatile.Read(ref _authorityVersion);
    private protected int CurrentControlState => Volatile.Read(ref _state);
    private protected bool StorageReturned => Volatile.Read(ref _payloadReturned) != 0;
    private protected int DeclaredLength => _length;
    private protected NativeBlock OwnedBlock => _block.Value;
    private protected IntPtr OwnedPointer => _block.Pointer;
    private protected nuint OwnedByteLength => _block.ByteLength;
    private protected bool TryTransition(int expected, int next) =>
        Interlocked.CompareExchange(ref _state, next, expected) == expected;

    private protected void PublishControlState(int state) => Volatile.Write(ref _state, state);
    private protected void PublishAuthorityVersion(long version) => Volatile.Write(ref _authorityVersion, version);

    // A control parked for reuse keeps its finalizer registration, so the next
    // lifetime needs no re-registration and thread exit still frees its storage.
    // Returns the storage and publishes Disposed, then hands the control to a
    // reuse slot; true means the caller no longer owns this object.
    private protected virtual bool TryHandOffForReuse() => false;

    private protected bool IsSharing => _sharingControl is not null;

    // Starts a later lifetime on a returned control. Versions only increase, so
    // values from earlier lifetimes never match the new authority.
    private protected void BeginReusedLifetime(NativeBlock block, long ownerId, long firstVersion)
    {
        _kernel = null;
        _sharingControl = null;
        _allocationState = null;
        _block.Set(block);
        _ownerId = ownerId;
        _length = 0;
        _capacity = 0;
        // _loneBorrow is not reset: it is zero once disposal succeeded, and a
        // stale borrow refused since then clears only the slot it took.
        NativeOperationAdmission.Reset(ref _operationAdmission);
        _peakBorrows = 0;
        _borrowed = false;
        _returnFailures = 0;
        _historyOverflowed = false;
        _sharingAllocationEntered = false;
        _sharingGenerationEntered = false;
        Volatile.Write(ref _payloadReturned, 0);
        Volatile.Write(ref _authorityVersion, firstVersion);
    }

    // Starts a later lifetime on a returned control for a kernel lease and
    // publishes it Active, as the kernel constructor does for a new control.
    private protected void BeginKernelLifetime(
        NativeOwnerKernel kernel,
        NativeAllocation allocationState,
        long generation,
        long allocationId,
        long firstVersion)
    {
        NativeBlock block = KernelBlock(kernel, allocationState, generation, allocationId, out bool borrowedBacking);
        BeginReusedLifetime(block, kernel.Id, firstVersion);
        _kernel = kernel;
        _allocationState = allocationState;
        _borrowedBacking = borrowedBacking;
        _length = allocationState.Length;
        _capacity = allocationState.Capacity;
        Volatile.Write(ref _state, Active);
    }

    // An unpublished control learns its final shape before publishing Active.
    private protected void PublishShape(int length, int capacity)
    {
        _length = length;
        _capacity = capacity;
    }

    private protected void InstallOwnedBlock(NativeBlock block)
    {
        _block.Set(block);
        _ = checked((long)block.ByteLength);
    }

    private protected void RecordReturnFailure() => NativeOwnerHistory.Increment(ref _returnFailures, ref _historyOverflowed);

    private protected void IncrementControlHistory(ref long counter) =>
        NativeOwnerHistory.Increment(ref counter, ref _historyOverflowed);

    private protected void CompleteStorageReturn()
    {
        _kernel = null;
        _allocationState = null;
        // Returned aliases keep numeric observation, never a pointer, budget,
        // kernel or allocation record. Numeric extent is lifetime peak history,
        // not live storage or authority. Direct and kernel identities are stable.
        _block.Detach();
        Volatile.Write(ref _payloadReturned, 1);
    }

    /// <summary>Returns storage when a receiver abandons the active transfer.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0055", Justification = "Emergency native-memory cleanup supplements mandatory deterministic disposal.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "An emergency finalizer must never let cleanup exceptions terminate the process.")]
    ~NativeTransferControl()
    {
        try
        {
            FinalizeLease();
        }
        catch
        {
        }
    }

    private readonly record struct MovePublication(
        NativeTransferControl<T> Source,
        long AuthorityVersion)
    {
        internal void Publish() => Source.PublishMove(AuthorityVersion);
    }
}

// Tests hold a borrow at the points where it races a move. Only a test
// process sets the switch; elsewhere Enabled is false and optimized code
// drops every call.
internal static class NativeTransferRacesForTest
{
    internal const string Switch = "Supprocom.NativeAllocationManagement.Testing.TransferRaces";

    internal static readonly bool Enabled = AppContext.TryGetSwitch(Switch, out bool enabled) && enabled;

    // After a borrow's active check, before it takes the lone slot or the count.
    internal static Action? AfterActiveCheck;

    // After a refused borrow's increment, before it takes the increment back.
    internal static Action? BeforeRefusedExit;

    internal static void Run(Action? hook) => hook?.Invoke();

    internal static void Reset()
    {
        AfterActiveCheck = null;
        BeforeRefusedExit = null;
    }
}
