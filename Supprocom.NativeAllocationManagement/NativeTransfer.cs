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

    internal object? ControlForTest => _control;

    internal static NativeTransfer<T> Create(NativeOwnerKernel kernel, NativePoolLease lease, string operation) =>
        new(NativeTransferControl<T>.Create(kernel, lease, operation), authorityVersion: 1);

    internal static NativeTransfer<T> Create(NativeOwnerKernel kernel, NativeRegionAllocation allocation, string operation) =>
        new(NativeTransferControl<T>.Create(kernel, allocation, operation), authorityVersion: 1);

    internal static NativeTransfer<T> CreateOwnedBlock(NativeBlock block, int length, int capacity) =>
        new(NativeTransferControl<T>.CreateOwnedBlock(block, length, capacity), authorityVersion: 1);

    private NativeTransferControl<T> GetControl(string operation) =>
        _control ?? throw new NativeAllocationUninitializedException(nameof(NativeTransfer<T>), operation);
}

// One acquisition-time control, not one wrapper/finalizer per move.
internal sealed class NativeTransferControl<T>
    where T : unmanaged
{
    private const int Active = 1;
    private const int Moving = 2;
    private const int Moved = 3;
    private const int Disposing = 4;
    private const int Disposed = 5;
    private const int Retiring = 6;
    private const int Finalized = 7;

    private readonly NativeOwnerKernel? _kernel;
    private readonly NativeGeneration? _generationState;
    private readonly NativeAllocation? _allocationState;
    private readonly long _generation;
    private readonly long _allocationId;
    private readonly NativeBlock _block;
    private readonly int _length;
    private readonly int _capacity;
    private int _state;
    private int _operationAdmission;
    private long _authorityVersion = 1;

    private NativeTransferControl(
        NativeOwnerKernel kernel,
        NativeGeneration generationState,
        NativeAllocation allocationState,
        long generation,
        long allocationId)
    {
        _kernel = kernel;
        _generationState = generationState;
        _allocationState = allocationState;
        _generation = generation;
        _allocationId = allocationId;
        _block = default;
        _length = allocationState.Length;
        _capacity = allocationState.Capacity;
        _state = Active;
    }

    private NativeTransferControl(
        NativeBlock block,
        int length,
        int capacity)
    {
        _block = block;
        _length = length;
        _capacity = capacity;
        _state = Active;
    }

    internal long Id => _kernel?.Id ?? _block.OwnerId;

    internal int GetLength(long authorityVersion) => Validate(authorityVersion, nameof(NativeTransfer<T>.Length)).Length;

    internal int GetCapacity(long authorityVersion) => Validate(authorityVersion, nameof(NativeTransfer<T>.Capacity)).Capacity;

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
            NativeOperationAdmission.Open(ref _operationAdmission);
            Volatile.Write(ref _state, Active);
            throw;
        }
    }

    internal static NativeTransferControl<T> Create(
        NativeOwnerKernel kernel,
        NativePoolLease lease,
        string operation) =>
        Create(
            kernel,
            lease.GenerationState,
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
            allocation.GenerationState,
            allocation.AllocationState,
            allocation.Generation,
            allocation.AllocationId,
            operation);

    private static NativeTransferControl<T> Create(
        NativeOwnerKernel kernel,
        NativeGeneration generationState,
        NativeAllocation allocationState,
        long generation,
        long allocationId,
        string operation)
    {
        try
        {
            return new NativeTransferControl<T>(
                kernel,
                generationState,
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
            capacity);

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
            _generationState!,
            _allocationState!,
            _generation,
            _allocationId,
            operation);
    }

    private NativeOperationToken EnterKernelOperation(
        string operation) =>
        _kernel!.EnterOperation(
            _generationState!,
            _allocationState!,
            _generation,
            _allocationId,
            operation);

    private void EnterTransferOperation(long authorityVersion, string operation)
    {
        EnsureActive(authorityVersion, operation);
        if (!NativeOperationAdmission.TryEnter(
            ref _operationAdmission))
        {
            ThrowInactive(
                operation,
                Volatile.Read(ref _state));
        }

        int state = Volatile.Read(ref _state);
        if (state == Active && Volatile.Read(ref _authorityVersion) == authorityVersion)
        {
            return;
        }

        NativeOperationAdmission.Exit(ref _operationAdmission);
        ThrowInactive(operation, state == Active ? Moved : state);
    }

    private void ExitTransferOperation()
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
            ReturnStorageFromFinalizer();
            Volatile.Write(ref _state, Disposed);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "Failed consumed moves release storage and disarm the acquisition-time finalizer.")]
    internal long Move(long authorityVersion)
    {
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
                    _generationState!,
                    _allocationState!,
                    _generation,
                    _allocationId,
                    "NativeTransfer.Move",
                    publication,
                    static state => state.Publish());
            }
            return publication.AuthorityVersion;
        }
        catch
        {
            ReturnStorageFromFinalizer();
            Volatile.Write(ref _state, Disposed);
            GC.SuppressFinalize(this);
            throw;
        }
    }

    private void PublishMove(long authorityVersion)
    {
        Volatile.Write(ref _authorityVersion, authorityVersion);
        NativeOperationAdmission.Reset(ref _operationAdmission);
        Volatile.Write(ref _state, Active);
    }

    private void ReturnStorage(string operation)
    {
        if (_kernel is null)
        {
            NativeBlockAllocator.Free(_block);
            return;
        }

        _kernel.ReturnLease(
            _generation,
            _allocationId,
            operation);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "This boundary captures any failure to preserve cleanup and report the original error.")]
    private void ReturnStorageFromFinalizer()
    {
        try
        {
            ReturnStorage("NativeTransfer.Finalize");
        }
        catch
        {
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
        if (Interlocked.CompareExchange(
            ref _state,
            Finalized,
            Active) != Active)
        {
            return;
        }

        NativeOperationAdmission.Close(
            ref _operationAdmission);
        ReturnStorageFromFinalizer();
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
