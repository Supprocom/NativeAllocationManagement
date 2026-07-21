namespace Supprocom.NativeAllocationManagement;

/// <summary>Owns reusable heterogeneous arena generations with one shared segment bank.</summary>
public sealed class NativeArena : IDisposable
{
    private readonly NativeOwnerKernel _kernel;

    internal NativeOwnerLifecycle CurrentLifecycle => _kernel.Lifecycle;

    /// <summary>Creates an active arena unless the first generation is explicitly deferred.</summary>
    /// <param name="preAllocateBytes">Optional initial byte reservation.</param>
    /// <param name="returnMemoryOnDispose">The physical cleanup policy used by <see cref="Dispose"/>.</param>
    /// <param name="doNotLeaseOnDeclaration">When true, defer the first generation until <see cref="LeaseFromMemory"/>.</param>
    public NativeArena(
        nuint preAllocateBytes = 0,
        NativeMemoryReturn returnMemoryOnDispose = NativeMemoryReturn.ToGarbageCollector,
        bool doNotLeaseOnDeclaration = false)
    {
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        _kernel = NativeOwnerKernel.CreateArena(
            preAllocateBytes,
            "NativeArena",
            returnMemoryOnDispose,
            doNotLeaseOnDeclaration);
    }

    /// <summary>Acquires an ordinary heterogeneous scratch range.</summary>
    public ArenaLease<T> Scratch<T>(int length)
    {
        NativeRegionAllocation allocation = _kernel.LeaseBump(
            length,
            NativeTypeLayout.StorageSize<T>(),
            NativeTypeLayout.Alignment<T>(),
            scoped: false,
            typeof(T));
        return new ArenaLease<T>(_kernel, allocation.Generation, allocation.AllocationId, allocation.Length, allocation.Capacity);
    }

    /// <summary>Acquires a scratch range whose storage can be completed by <see cref="RecycleScoped"/>.</summary>
    public ArenaLease<T> ScratchScoped<T>(int length)
    {
        NativeRegionAllocation allocation = _kernel.LeaseBump(
            length,
            NativeTypeLayout.StorageSize<T>(),
            NativeTypeLayout.Alignment<T>(),
            scoped: true,
            typeof(T));
        return new ArenaLease<T>(_kernel, allocation.Generation, allocation.AllocationId, allocation.Length, allocation.Capacity);
    }

    /// <summary>Ends the current memory generation and frees native storage immediately.</summary>
    public void ReturnMemoryToNativeMemory() => _kernel.ReturnMemoryToNativeMemory();

    /// <summary>Ends the current memory generation and detaches its storage for finalizable cleanup.</summary>
    public void ReturnMemoryToGarbageCollector() => _kernel.ReturnMemoryToGarbageCollector();

    /// <summary>Invalidates all current arena leases and reuses retained segments.</summary>
    public void ReleaseLeasesToNativeMemory() => _kernel.ReleaseLeasesToNativeMemory();

    /// <summary>Invalidates all current arena leases while entered operations drain.</summary>
    public void ReleaseLeasesToGarbageCollector() => _kernel.ReleaseLeasesToGarbageCollector();

    /// <summary>Publishes the first generation or the generation reserved by a memory return.</summary>
    public void LeaseFromMemory() => _kernel.LeaseFromMemory();

    /// <summary>Recycles the complete analyzer-proven dead scoped pending set.</summary>
    public void RecycleScoped() => _kernel.RecycleScoped();

    /// <summary>Releases every eligible idle segment.</summary>
    public nuint TrimRetainedMemory() => _kernel.TrimRetainedMemory();

    /// <summary>Releases whole idle segments until the requested physical byte budget is met.</summary>
    public nuint TrimRetainedMemoryByBytes(nuint bytesToRelease) => _kernel.TrimRetainedMemoryByBytes(bytesToRelease);

    /// <summary>Trims by the exact physical footprint requested by a heterogeneous scratch shape.</summary>
    public nuint TrimRetainedMemoryByLeaseSize<T>(int leaseLength = 1) =>
        _kernel.TrimRetainedMemoryByLeaseSize(leaseLength, NativeTypeLayout.StorageSize<T>(), typeof(T));

    /// <summary>Permanently closes the arena and applies its configured memory policy.</summary>
    public void Dispose() => _kernel.Dispose();
}

/// <summary>A heterogeneous generation-bound arena handle.</summary>
/// <typeparam name="T">The value or reference type stored by the arena.</typeparam>
public readonly ref struct ArenaLease<T>
{
    private readonly NativeOwnerKernel? _kernel;
    private readonly long _generation;
    private readonly long _allocationId;

    internal ArenaLease(NativeOwnerKernel kernel, long generation, long allocationId, int length, int capacity)
    {
        _kernel = kernel;
        _generation = generation;
        _allocationId = allocationId;
        _ = length;
        _ = capacity;
    }

    /// <summary>Gets the logical element count.</summary>
    public int Length => GetMetadata(nameof(Length)).Length;

    /// <summary>Gets the physical capacity represented by this handle.</summary>
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

    /// <summary>Clears the logical range.</summary>
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

    /// <summary>Copies exactly the logical range from a source span.</summary>
    public void CopyFrom(scoped ReadOnlySpan<T> source)
    {
        NativeHandleMetadata metadata = GetMetadata(nameof(CopyFrom));
        if (source.Length != metadata.Length)
        {
            throw new ArgumentException("The source length must equal the arena logical length.", nameof(source));
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

    /// <summary>Copies the logical range into a destination span.</summary>
    public void CopyTo(scoped Span<T> destination)
    {
        NativeHandleMetadata metadata = GetMetadata(nameof(CopyTo));
        if (destination.Length < metadata.Length)
        {
            throw new ArgumentException("The destination must contain at least the arena logical length.", nameof(destination));
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

    /// <summary>Runs one synchronous bounded read callback.</summary>
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
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= metadata.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The index is outside the logical arena range.");
        }

        return GetKernel(operation).EnterOperation(_generation, _allocationId, operation);
    }

    private NativeOwnerKernel GetKernel(string operation) =>
        _kernel ?? throw new NativeAllocationUninitializedException(nameof(ArenaLease<T>), operation);

    private NativeHandleMetadata GetMetadata(string operation) => GetKernel(operation).ValidateHandle(_generation, _allocationId, operation);
}
