namespace Supprocom.NativeAllocationManagement;

/// <summary>
/// A region-bound unmanaged handle that remains valid until its enclosing region ends.
/// </summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
public readonly ref struct Local<T>
    where T : unmanaged
{
    private readonly NativeOwnerKernel? _kernel;
    private readonly long _generation;
    private readonly long _allocationId;

    internal Local(
        NativeOwnerKernel kernel,
        long generation,
        long allocationId,
        int length,
        int capacity)
    {
        _kernel = kernel;
        _generation = generation;
        _allocationId = allocationId;
        _ = length;
        _ = capacity;
    }

    /// <summary>Gets the logical element count.</summary>
    public int Length => GetMetadata(nameof(Length)).Length;

    /// <summary>Gets the allocation capacity in elements.</summary>
    public int Capacity => GetMetadata(nameof(Capacity)).Capacity;

    /// <summary>Reads or writes one value through the owner operation gate.</summary>
    public T this[int index]
    {
        get
        {
            NativeOwnerKernel kernel = GetKernel("get_Item");
            NativeHandleMetadata metadata = kernel.ValidateHandle(_generation, _allocationId, "get_Item");
            ValidateIndex(index, metadata.Length);
            NativeOperationToken token = kernel.EnterOperation(_generation, _allocationId, "get_Item");
            try
            {
                return token.GetSpan<T>()[index];
            }
            finally
            {
                token.Dispose();
            }
        }
        set
        {
            NativeOwnerKernel kernel = GetKernel("set_Item");
            NativeHandleMetadata metadata = kernel.ValidateHandle(_generation, _allocationId, "set_Item");
            ValidateIndex(index, metadata.Length);
            NativeOperationToken token = kernel.EnterOperation(_generation, _allocationId, "set_Item");
            try
            {
                token.GetSpan<T>()[index] = value;
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
        NativeOwnerKernel kernel = GetKernel(nameof(Clear));
        NativeOperationToken token = kernel.EnterOperation(_generation, _allocationId, nameof(Clear));
        try
        {
            token.GetSpan<T>().Clear();
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Copies exactly the logical range from a bounded source span.</summary>
    public void CopyFrom(scoped ReadOnlySpan<T> source)
    {
        NativeOwnerKernel kernel = GetKernel(nameof(CopyFrom));
        NativeHandleMetadata metadata = kernel.ValidateHandle(_generation, _allocationId, nameof(CopyFrom));
        if (source.Length != metadata.Length)
        {
            throw new ArgumentException("The source length must equal the local logical length.", nameof(source));
        }

        NativeOperationToken token = kernel.EnterOperation(_generation, _allocationId, nameof(CopyFrom));
        try
        {
            source.CopyTo(token.GetSpan<T>());
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Copies exactly the logical range into a bounded destination span.</summary>
    public void CopyTo(scoped Span<T> destination)
    {
        NativeOwnerKernel kernel = GetKernel(nameof(CopyTo));
        NativeHandleMetadata metadata = kernel.ValidateHandle(_generation, _allocationId, nameof(CopyTo));
        if (destination.Length < metadata.Length)
        {
            throw new ArgumentException("The destination must contain at least the local logical length.", nameof(destination));
        }

        NativeOperationToken token = kernel.EnterOperation(_generation, _allocationId, nameof(CopyTo));
        try
        {
            token.GetSpan<T>().CopyTo(destination);
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Runs one synchronous bounded mutation callback.</summary>
    public void Access(NativeSpanAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOwnerKernel kernel = GetKernel(nameof(Access));
        NativeOperationToken token = kernel.EnterOperation(_generation, _allocationId, nameof(Access));
        try
        {
            action(token.GetSpan<T>());
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Runs one synchronous bounded read callback and returns its managed result.</summary>
    public TResult Read<TResult>(NativeSpanFunc<T, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOwnerKernel kernel = GetKernel(nameof(Read));
        NativeOperationToken token = kernel.EnterOperation(_generation, _allocationId, nameof(Read));
        try
        {
            return action(token.GetSpan<T>());
        }
        finally
        {
            token.Dispose();
        }
    }

    private NativeOwnerKernel GetKernel(string operation)
    {
        return _kernel ?? throw new NativeAllocationUninitializedException(nameof(Local<T>), operation);
    }

    private NativeHandleMetadata GetMetadata(string operation)
    {
        return GetKernel(operation).ValidateHandle(_generation, _allocationId, operation);
    }

    private static void ValidateIndex(int index, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The index is outside the logical local range.");
        }
    }
}

