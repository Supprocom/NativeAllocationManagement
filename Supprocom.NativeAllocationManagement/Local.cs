namespace Supprocom.NativeAllocationManagement;

/// <summary>A generation-bound heterogeneous handle owned by a NativeRegion.</summary>
/// <typeparam name="T">The value or reference type stored by the region.</typeparam>
public readonly ref struct Local<T>
{
    private readonly NativeOwnerKernel? _kernel;
    private readonly long _generation;
    private readonly long _allocationId;

    internal Local(NativeOwnerKernel kernel, long generation, long allocationId, int length, int capacity)
    {
        _kernel = kernel;
        _generation = generation;
        _allocationId = allocationId;
        _ = length;
        _ = capacity;
    }

    /// <summary>Gets the logical element count.</summary>
    public int Length => GetMetadata(nameof(Length)).Length;

    /// <summary>Gets the physical capacity in elements.</summary>
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
        NativeOperationToken token = GetKernel(nameof(Clear)).EnterOperation(_generation, _allocationId, nameof(Clear));
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
            throw new ArgumentException("The source length must equal the local logical length.", nameof(source));
        }

        NativeOperationToken token = GetKernel(nameof(CopyFrom)).EnterOperation(_generation, _allocationId, nameof(CopyFrom));
        try
        {
            token.GetView<T>().CopyFrom(source);
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Copies the logical range into a destination with sufficient capacity.</summary>
    public void CopyTo(scoped Span<T> destination)
    {
        NativeHandleMetadata metadata = GetMetadata(nameof(CopyTo));
        if (destination.Length < metadata.Length)
        {
            throw new ArgumentException("The destination must contain at least the local logical length.", nameof(destination));
        }

        NativeOperationToken token = GetKernel(nameof(CopyTo)).EnterOperation(_generation, _allocationId, nameof(CopyTo));
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
        NativeOperationToken token = GetKernel(nameof(Access)).EnterOperation(_generation, _allocationId, nameof(Access));
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
        NativeOperationToken token = GetKernel(nameof(Read)).EnterOperation(_generation, _allocationId, nameof(Read));
        try
        {
            return action(token.GetView<T>());
        }
        finally
        {
            token.Dispose();
        }
    }

    private NativeOperationToken EnterIndexedOperation(string operation, int index)
    {
        NativeHandleMetadata metadata = GetMetadata(operation);
        ValidateIndex(index, metadata.Length);
        return GetKernel(operation).EnterOperation(_generation, _allocationId, operation);
    }

    private NativeOwnerKernel GetKernel(string operation) =>
        _kernel ?? throw new NativeAllocationUninitializedException(nameof(Local<T>), operation);

    private NativeHandleMetadata GetMetadata(string operation) => GetKernel(operation).ValidateHandle(_generation, _allocationId, operation);

    private static void ValidateIndex(int index, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The index is outside the logical local range.");
        }
    }
}
