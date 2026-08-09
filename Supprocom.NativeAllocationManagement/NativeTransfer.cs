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

/// <summary>A heap-storable native lease with destructive move ownership.</summary>
/// <typeparam name="T">The unmanaged element type in the native range.</typeparam>
public sealed class NativeTransfer<T> : IDisposable
    where T : unmanaged
{
    private const int Unowned = 0;
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

    private NativeTransfer(
        NativeOwnerKernel kernel,
        NativeGeneration generationState,
        NativeAllocation allocationState,
        long generation,
        long allocationId,
        bool active)
    {
        _kernel = kernel;
        _generationState = generationState;
        _allocationState = allocationState;
        _generation = generation;
        _allocationId = allocationId;
        _block = default;
        _length = allocationState.Length;
        _capacity = allocationState.Capacity;
        _state = active ? Active : Unowned;
    }

    private NativeTransfer(
        NativeBlock block,
        int length,
        int capacity,
        bool active)
    {
        _block = block;
        _length = length;
        _capacity = capacity;
        _state = active ? Active : Unowned;
    }

    private NativeTransfer(
        NativeTransfer<T> source,
        bool active)
    {
        _kernel = source._kernel;
        _generationState = source._generationState;
        _allocationState = source._allocationState;
        _generation = source._generation;
        _allocationId = source._allocationId;
        _block = source._block;
        _length = source._length;
        _capacity = source._capacity;
        _state = active ? Active : Unowned;
    }

    /// <summary>Gets the logical element count.</summary>
    public int Length => Validate(nameof(Length)).Length;

    /// <summary>Gets the physical element capacity.</summary>
    public int Capacity => Validate(nameof(Capacity)).Capacity;

    /// <summary>Moves ownership and sets the source variable to null.</summary>
    /// <remarks>All aliases of the old object become invalid after a successful move.</remarks>
    public static NativeTransfer<T> Move(
        ref NativeTransfer<T>? source)
    {
        NativeTransfer<T> observed =
            source
            ?? throw new ArgumentNullException(
                nameof(source),
                "The transfer source has no ownership.");
        NativeTransfer<T> destination = new(
            observed,
            active: false);
        if (!ReferenceEquals(
            Interlocked.CompareExchange(
                ref source,
                null,
                observed),
            observed))
        {
            throw new InvalidOperationException(
                "The transfer source changed during the move.");
        }

        observed.MoveTo(destination);
        return destination;
    }

    /// <summary>Runs one synchronous bounded callback over the native span.</summary>
    public void Access(NativeLeaseAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterTransferOperation(nameof(Access));
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
    public TResult Read<TResult>(NativeLeaseFunc<T, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterTransferOperation(nameof(Read));
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
    public void Dispose()
    {
        int observed = Interlocked.CompareExchange(
            ref _state,
            Disposing,
            Active);
        if (observed != Active)
        {
            ThrowInactive(nameof(Dispose), observed);
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

    internal static NativeTransfer<T> Create(
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

    internal static NativeTransfer<T> Create(
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

    private static NativeTransfer<T> Create(
        NativeOwnerKernel kernel,
        NativeGeneration generationState,
        NativeAllocation allocationState,
        long generation,
        long allocationId,
        string operation)
    {
        try
        {
            return new NativeTransfer<T>(
                kernel,
                generationState,
                allocationState,
                generation,
                allocationId,
                active: true);
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

    internal static NativeTransfer<T> CreateOwnedBlock(
        NativeBlock block,
        int length,
        int capacity) =>
        new(
            block,
            length,
            capacity,
            active: true);

    private NativeHandleMetadata Validate(string operation)
    {
        EnsureActive(operation);
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

    private void EnterTransferOperation(string operation)
    {
        EnsureActive(operation);
        if (!NativeOperationAdmission.TryEnter(
            ref _operationAdmission))
        {
            ThrowInactive(
                operation,
                Volatile.Read(ref _state));
        }

        int state = Volatile.Read(ref _state);
        if (state == Active)
        {
            return;
        }

        NativeOperationAdmission.Exit(ref _operationAdmission);
        ThrowInactive(operation, state);
    }

    private void ExitTransferOperation()
    {
        int remaining =
            NativeOperationAdmission.Exit(ref _operationAdmission);
        if (remaining == 0
            && Volatile.Read(ref _state) == Retiring)
        {
            ReturnStorageFromFinalizer();
            Volatile.Write(ref _state, Disposed);
        }
    }

    private void MoveTo(NativeTransfer<T> destination)
    {
        int observed = Interlocked.CompareExchange(
            ref _state,
            Moving,
            Active);
        if (observed != Active)
        {
            ThrowInactive(nameof(Move), observed);
        }

        int activeOperations =
            NativeOperationAdmission.Close(ref _operationAdmission);
        if (activeOperations != 0)
        {
            Volatile.Write(ref _state, Retiring);
            throw new InvalidOperationException(
                "NativeTransfer.Move found an active callback. The source will return after that callback ends.");
        }

        try
        {
            MovePublication publication = new(
                this,
                destination);
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
        }
        catch
        {
            ReturnStorageFromFinalizer();
            Volatile.Write(ref _state, Disposed);
            GC.SuppressFinalize(this);
            throw;
        }
    }

    private void PublishMove(NativeTransfer<T> destination)
    {
        NativeOperationAdmission.Reset(
            ref destination._operationAdmission);
        Volatile.Write(ref destination._state, Active);
        Volatile.Write(ref _state, Moved);
        GC.SuppressFinalize(this);
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

    private void EnsureActive(string operation)
    {
        int state = Volatile.Read(ref _state);
        if (state != Active)
        {
            ThrowInactive(operation, state);
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
    ~NativeTransfer()
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
        NativeTransfer<T> Source,
        NativeTransfer<T> Destination)
    {
        internal void Publish() => Source.PublishMove(Destination);
    }
}
