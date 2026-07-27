namespace Supprocom.NativeAllocationManagement;

/// <summary>A generation-bound pooled handle whose view exists only during bounded operations.</summary>
/// <typeparam name="T">The value or reference type stored by the pool.</typeparam>
public readonly ref struct Pooled<T>
{
    private readonly NativeOwnerKernel? _kernel;
    private readonly long _generation;
    private readonly long _allocationId;
    private readonly NativeGeneration? _generationState;
    private readonly NativeAllocation? _allocationState;

    internal Pooled(
        NativeOwnerKernel kernel,
        NativePoolLease lease)
    {
        _kernel = kernel;
        _generation = lease.Generation;
        _allocationId = lease.AllocationId;
        _generationState = lease.GenerationState;
        _allocationState = lease.AllocationState;
    }

    internal NativeOwnerKernel KernelForComposite =>
        GetKernel("NativeLeaseOperations.Access");

    internal long GenerationForComposite => _generation;

    internal long AllocationIdForComposite => _allocationId;

    internal NativeGeneration GenerationStateForComposite =>
        GetGenerationState("NativeLeaseOperations.Access");

    internal NativeAllocation AllocationStateForComposite =>
        GetAllocationState("NativeLeaseOperations.Access");

    internal NativeOperationToken EnterForComposite(string operation) =>
        EnterOperation(operation);

    /// <summary>Gets the logical element count.</summary>
    public int Length => GetMetadata(nameof(Length)).Length;

    /// <summary>Gets the physical slab capacity in elements.</summary>
    public int Capacity => GetMetadata(nameof(Capacity)).Capacity;

    /// <summary>Reads or writes one value through the owner operation gate.</summary>
    public T this[int index]
    {
        get
        {
            NativeOperationToken token = EnterIndexedOperation("get_Item", index);
            try
            {
                return token.GetValue<T>(index);
            }
            finally
            {
                token.Dispose();
            }
        }
        set
        {
            NativeOperationToken token = EnterIndexedOperation("set_Item", index);
            try
            {
                token.SetValue(index, value);
            }
            finally
            {
                token.Dispose();
            }
        }
    }

    /// <summary>Zeroes the logical range while holding one native operation token.</summary>
    public void Clear()
    {
        NativeOperationToken token = EnterOperation(nameof(Clear));
        try
        {
            token.GetView<T>().Clear();
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Copies exactly the logical range from a bounded source span.</summary>
    public void CopyFrom(scoped ReadOnlySpan<T> source)
    {
        NativeHandleMetadata metadata = GetMetadata(nameof(CopyFrom));
        if (source.Length != metadata.Length)
        {
            throw new ArgumentException("The source length must equal the pooled logical length.", nameof(source));
        }

        NativeOperationToken token = EnterOperation(nameof(CopyFrom));
        try
        {
            token.GetView<T>().CopyFrom(source);
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Copies exactly the logical range into a bounded destination span.</summary>
    public void CopyTo(scoped Span<T> destination)
    {
        NativeHandleMetadata metadata = GetMetadata(nameof(CopyTo));
        if (destination.Length < metadata.Length)
        {
            throw new ArgumentException("The destination must contain at least the pooled logical length.", nameof(destination));
        }

        NativeOperationToken token = EnterOperation(nameof(CopyTo));
        try
        {
            token.GetView<T>().CopyTo(destination);
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Runs one synchronous bounded mutation callback.</summary>
    public void Access(NativeLeaseAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOperationToken token = EnterOperation(nameof(Access));
        try
        {
            action(token.GetView<T>());
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Runs one synchronous bounded read callback and returns its managed result.</summary>
    public TResult Read<TResult>(NativeLeaseFunc<T, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOperationToken token = EnterOperation(nameof(Read));
        try
        {
            return action(token.GetView<T>());
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Returns this lease's slab to the active pool generation.</summary>
    public void Dispose() => GetKernel(nameof(Dispose)).ReturnLease(_generation, _allocationId);

    private NativeOperationToken EnterIndexedOperation(string operation, int index)
    {
        NativeHandleMetadata metadata = GetMetadata(operation);
        ValidateIndex(index, metadata.Length);
        return EnterOperation(operation);
    }

    private NativeOwnerKernel GetKernel(string operation) =>
        _kernel ?? throw new NativeAllocationUninitializedException(nameof(Pooled<T>), operation);

    private NativeGeneration GetGenerationState(string operation) =>
        _generationState
        ?? throw new NativeAllocationUninitializedException(
            nameof(Pooled<T>),
            operation);

    private NativeAllocation GetAllocationState(string operation) =>
        _allocationState
        ?? throw new NativeAllocationUninitializedException(
            nameof(Pooled<T>),
            operation);

    private NativeOperationToken EnterOperation(string operation) =>
        GetKernel(operation).EnterOperation(
            GetGenerationState(operation),
            GetAllocationState(operation),
            _generation,
            _allocationId,
            operation);

    private NativeHandleMetadata GetMetadata(string operation) =>
        GetKernel(operation).ValidateHandle(
            GetGenerationState(operation),
            GetAllocationState(operation),
            _generation,
            _allocationId,
            operation);

    private static void ValidateIndex(int index, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The index is outside the logical pooled range.");
        }
    }
}
