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
            initializer);
        return NativeTransfer<T>.Create(
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

    internal static NativeTransfer<T> Create(NativeOwnerKernel kernel, NativePoolLease lease, string operation) =>
        new(NativeTransferControl<T>.Create(kernel, lease, operation), authorityVersion: 1);

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
    private readonly long _ownerId;
    private long _backingBytes;
    private readonly bool _borrowedBacking;
    // Direct custody uses the descriptor's epoch/owner as allocation metadata.
    // Kernel custody has no direct block: the same two scalar slots hold its
    // captured generation/allocation identity instead of duplicate fields.
    private NativeBlock _block;
    private readonly int _length;
    private readonly int _capacity;
    private int _state;
    private int _operationAdmission;
    private long _authorityVersion = 1;
    private int _peakBorrows;
    private int _payloadReturned;
    private long _returnFailures;
    private bool _historyOverflowed;
    private bool _sharingAllocationEntered;
    private bool _sharingGenerationEntered;

    private NativeTransferControl(
        NativeOwnerKernel kernel,
        NativeAllocation allocationState,
        long generation,
        long allocationId)
    {
        _kernel = kernel;
        _allocationState = allocationState;
        _ownerId = kernel.Id;
        NativeSegment? segment = allocationState.Segment;
        _borrowedBacking = segment is { AllocationByteLength: 0 };
        _backingBytes = checked((long)(_borrowedBacking ? segment!.ByteLength : segment?.AllocationByteLength ?? 0));
        // A closed kernel no longer roots its domain. The existing block slot
        // keeps identity and still-pending trace custody until real return.
        _block = new NativeBlock(IntPtr.Zero, 0, generation, kernel.BudgetForSharing, allocationId);
        _length = allocationState.Length;
        _capacity = allocationState.Capacity;
        _state = Active;
    }

    private protected NativeTransferControl(
        NativeBlock block,
        int length,
        int capacity,
        bool published)
    {
        _block = block;
        _ownerId = block.OwnerId;
        _backingBytes = checked((long)block.ByteLength);
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
        int borrows = NativeOperationAdmission.Count(ref _operationAdmission);
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
            PeakBorrowCount = Volatile.Read(ref _peakBorrows),
            MoveCount = authority - 1,
            LiveUniqueOwnerCount = !returned && state == Active && allocationActive ? 1 : 0,
            HasReturnObligation = !returned,
            InitializedPayloadBytes = backingPresent && (allocationActive || state == Shared || borrows != 0) ? initialized : 0,
            PeakInitializedPayloadBytes = initialized,
            OwnedBackingBytes = !backingPresent || _borrowedBacking ? 0 : _backingBytes,
            BorrowedBackingBytes = !backingPresent || !_borrowedBacking ? 0 : _backingBytes,
            PeakOwnedBackingBytes = _borrowedBacking ? 0 : _backingBytes,
            PayloadReturnCount = returned ? 1 : 0,
            PayloadReturnFailureCount = Volatile.Read(ref _returnFailures),
            HistoryOverflowed = Volatile.Read(ref _historyOverflowed),
            ControlFieldBytes = 3L * IntPtr.Size + 4L * sizeof(long) + 6L * sizeof(int) + 4L * sizeof(bool) + Unsafe.SizeOf<NativeBlock>()
        };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "Successful terminal retry disarms the acquisition-time emergency finalizer.")]
    internal bool TryCompletePayloadReturn()
    {
        if (Volatile.Read(ref _payloadReturned) != 0) return true;
        if (NativeOperationAdmission.Count(ref _operationAdmission) != 0
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
        EnterTransferOperation(authorityVersion, nameof(Access));
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
        }
        finally
        {
            ExitTransferOperation();
            GC.KeepAlive(this);
        }
    }

    /// <summary>Runs one synchronous bounded callback and returns its managed result.</summary>
    internal TResult Read<TResult>(long authorityVersion, NativeLeaseFunc<T, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterTransferOperation(authorityVersion, nameof(Read));
        try
        {
            if (_kernel is null)
            {
                return action(new NativeLeaseView<T>(
                    _block.Pointer,
                    _length));
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
        finally
        {
            ExitTransferOperation();
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

        int activeOperations =
            NativeOperationAdmission.Close(ref _operationAdmission);
        if (activeOperations != 0)
        {
            NativeOperationAdmission.Open(ref _operationAdmission);
            Volatile.Write(ref _state, Active);
            throw new InvalidOperationException(
                "NativeTransfer.Dispose cannot run during an active callback.");
        }

        try
        {
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
        NativePoolLease lease,
        string operation) =>
        Create(
            kernel,
            lease.AllocationState,
            lease.Generation,
            lease.AllocationId,
            operation);

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
        if (_kernel is null)
        {
            return new NativeHandleMetadata(
                _length,
                _capacity);
        }

        return _kernel.ValidateHandle(
            _allocationState!.GenerationState,
            _allocationState!,
            _block.MetricsEpoch,
            _block.OwnerId,
            operation);
    }

    private NativeOperationToken EnterKernelOperation(
        string operation) =>
        _kernel!.EnterOperation(
            _allocationState!.GenerationState,
            _allocationState!,
            _block.MetricsEpoch,
            _block.OwnerId,
            operation);

    private protected void EnterTransferOperation(long authorityVersion, string operation)
    {
        EnsureActive(authorityVersion, operation);
        if (!NativeOperationAdmission.TryEnter(
            ref _operationAdmission, out int enteredCount))
        {
            ThrowInactive(
                operation,
                Volatile.Read(ref _state));
        }

        int state = Volatile.Read(ref _state);
        if (state == Active && Volatile.Read(ref _authorityVersion) == authorityVersion)
        {
            int peak = Volatile.Read(ref _peakBorrows);
            while (enteredCount > peak)
            {
                int observed = Interlocked.CompareExchange(ref _peakBorrows, enteredCount, peak);
                if (observed == peak) break;
                peak = observed;
            }
            return;
        }

        NativeOperationAdmission.Exit(ref _operationAdmission);
        ThrowInactive(operation, state == Active ? Moved : state);
    }

    private protected void ExitTransferOperation()
    {
        int remaining =
            NativeOperationAdmission.Exit(ref _operationAdmission);
        if (remaining == 0
            && Volatile.Read(ref _state) == Retiring)
        {
            CompleteRetirement();
        }
    }

    private void CompleteRetirement()
    {
        if (NativeOperationAdmission.Count(ref _operationAdmission) == 0
            && Interlocked.CompareExchange(ref _state, Disposing, Retiring) == Retiring)
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

        int activeOperations =
            NativeOperationAdmission.Close(ref _operationAdmission);
        if (activeOperations != 0)
        {
            try
            {
                NativeMemoryTestHooks.NotifyBeforeOperationEntry("NativeTransfer.Retire");
            }
            finally
            {
                Volatile.Write(ref _state, Retiring);
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
        NativeOperationAdmission.Reset(ref _operationAdmission);
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
            if (_kernel is null) NativeBlockAllocator.Free(_block);
            else _kernel.ReturnLease(_block.MetricsEpoch, _block.OwnerId, operation);
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
        ?? (_block.ByteLength == 0 ? 0 : 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TraceOwnership(NativeMemoryBudget? budget, NativeMemoryTraceKind kind, long? ordinal = null)
    {
        if (budget is not { TraceEnabled: true }) return;
        budget.RecordOwnershipTransition(kind, _ownerId, _block.OwnerId,
            checked((nuint)_backingBytes), ordinal ?? BackingOrdinal);
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

    private static void ThrowInactive(
        string operation,
        int state)
    {
        if (state is Disposed or Finalized)
        {
            throw new ObjectDisposedException(
                $"NativeTransfer<{typeof(T).Name}>",
                $"NativeTransfer.{operation} cannot run after disposal.");
        }

        if (state is Moving or Moved or Retiring)
        {
            throw new InvalidOperationException(
                $"NativeTransfer.{operation} cannot run because ownership moved or is moving.");
        }

        throw new InvalidOperationException(
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

        if (NativeOperationAdmission.Close(ref _operationAdmission) != 0
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
    private protected NativeBlock OwnedBlock => _block;
    private protected long BackingBytes => _backingBytes;

    private protected bool TryTransition(int expected, int next) =>
        Interlocked.CompareExchange(ref _state, next, expected) == expected;

    private protected void PublishControlState(int state) => Volatile.Write(ref _state, state);
    private protected void PublishAuthorityVersion(long version) => Volatile.Write(ref _authorityVersion, version);

    private protected void InstallOwnedBlock(NativeBlock block)
    {
        _block = block;
        _backingBytes = checked((long)block.ByteLength);
    }

    private protected void RecordReturnFailure() => NativeOwnerHistory.Increment(ref _returnFailures, ref _historyOverflowed);

    private protected void IncrementControlHistory(ref long counter) =>
        NativeOwnerHistory.Increment(ref counter, ref _historyOverflowed);

    private protected void CompleteStorageReturn()
    {
        _kernel = null;
        _allocationState = null;
        // Returned aliases keep numeric observation, never a pointer, budget,
        // kernel or allocation record. Direct and kernel identities are stable.
        _block = new NativeBlock(IntPtr.Zero, 0, _block.MetricsEpoch, OwnerId: _block.OwnerId);
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
