using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Owns reusable heterogeneous arena generations with one shared segment bank.</summary>
public sealed class NativeArena : IDisposable
{
    private readonly NativeOwnerKernel _kernel;

    internal NativeOwnerKernel KernelForInitialization => _kernel;

    internal NativeOwnerLifecycle CurrentLifecycle => _kernel.Lifecycle;

    internal int CurrentAllocationRecordCountForTest => _kernel.CurrentAllocationRecordCountForTest();

    internal int CurrentReferenceRootCountForTest => _kernel.CurrentReferenceRootCountForTest();

    internal int CurrentConcurrentReservationCountForTest =>
        _kernel.CurrentConcurrentReservationCountForTest();

    internal long CurrentFastLaneSlowPathCountForTest =>
        _kernel.ArenaFastSlowPathCountForTest();

    internal int QuarantinedSegmentCountForTest => _kernel.QuarantinedSegmentCountForTest();

    internal int QuarantinedGenerationCountForTest => _kernel.QuarantinedGenerationCountForTest();

    internal int RetiredGenerationCountForTest => _kernel.RetiredGenerationCountForTest();

    internal int QuarantineCapacityForTest => _kernel.QuarantineCapacityForTest();

    internal (int Slabs, int AvailableSlabs, int Bumps, int OwnerSegments) CurrentBankCapacitiesForTest =>
        _kernel.CurrentBankCapacitiesForTest();

    internal (int Ordinary, int Scoped, int SegmentCount) CurrentBumpTraversalForTest => _kernel.CurrentBumpTraversalForTest();

    internal long[] CurrentSegmentOrdinalsForTest => _kernel.CurrentSegmentOrdinalsForTest();

    internal NativeOwnerDiagnosticSnapshot CaptureDiagnosticSnapshot() =>
        _kernel.GetDiagnosticSnapshot();

    /// <summary>Reads the current logical and physical state of this owner.</summary>
    public NativeOwnerStatistics GetStatistics() => _kernel.GetStatistics();

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

    /// <summary>Initializes an ordinary heterogeneous range before publication.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ArenaLease<T> Scratch<T>(
        int length,
        NativeLeaseInitializer<T> initializer)
    {
        bool containsReferences =
            RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        int elementSize = containsReferences
            ? IntPtr.Size
            : Unsafe.SizeOf<T>();
        nuint alignment = containsReferences
            ? (nuint)IntPtr.Size
            : CalculateArenaAlignment(elementSize);
        if (!containsReferences
            && _kernel.IsArenaFastThread)
        {
            NativeArenaAllocation direct =
                _kernel.LeaseArenaBumpInitialized(
                    length,
                    elementSize,
                    alignment,
                    initializer);
            return new ArenaLease<T>(_kernel, direct);
        }

        NativeRegionAllocation allocation = _kernel.LeaseBumpInitialized(
            length,
            elementSize,
            alignment,
            scoped: false,
            containsReferences,
            initializer);
        return new ArenaLease<T>(_kernel, allocation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint CalculateArenaAlignment(int elementSize) =>
        elementSize >= IntPtr.Size
            ? (nuint)IntPtr.Size
            : elementSize >= 4
                ? 4u
                : elementSize >= 2
                    ? 2u
                    : 1u;

    /// <summary>Concurrently reserves and initializes an unmanaged arena range for destructive ownership transfer.</summary>
    public NativeTransfer<T> ScratchTransferable<T>(
        int length,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged
    {
        NativeRegionAllocation allocation = _kernel.LeaseConcurrentBumpInitialized(
            length,
            NativeTypeLayout.StorageSize<T>(),
            NativeTypeLayout.Alignment<T>(),
            initializer);
        return NativeTransfer<T>.Create(
            _kernel,
            allocation,
            "NativeArena.ScratchTransferable");
    }

    /// <summary>Initializes a scoped heterogeneous range before publication.</summary>
    public ArenaLease<T> ScratchScoped<T>(
        int length,
        NativeLeaseInitializer<T> initializer)
    {
        NativeRegionAllocation allocation = _kernel.LeaseBumpInitialized(
            length,
            NativeTypeLayout.StorageSize<T>(),
            NativeTypeLayout.Alignment<T>(),
            scoped: true,
            NativeTypeLayout.ContainsReferences<T>(),
            initializer);
        return new ArenaLease<T>(_kernel, allocation);
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

    /// <summary>Adds one exact retained byte segment for later heterogeneous leases.</summary>
    public nuint ReserveRetainedMemory(nuint byteLength) =>
        _kernel.ReserveRetainedMemory(byteLength);

    /// <summary>Adds one aligned external buffer range for heterogeneous leases.</summary>
    public nuint ReserveExternalMemory(
        SafeBuffer buffer,
        nuint byteOffset,
        nuint byteLength) =>
        _kernel.ReserveExternalMemory(
            buffer,
            byteOffset,
            byteLength);

    /// <summary>Releases every eligible idle segment.</summary>
    public nuint TrimRetainedMemory() => _kernel.TrimRetainedMemory();

    /// <summary>Releases whole idle segments until the requested physical byte budget is met.</summary>
    public nuint TrimRetainedMemoryByBytes(nuint bytesToRelease) => _kernel.TrimRetainedMemoryByBytes(bytesToRelease);

    /// <summary>Trims by the exact physical footprint requested by a heterogeneous scratch shape.</summary>
    public nuint TrimRetainedMemoryByLeaseSize<T>(int leaseLength = 1) =>
        _kernel.TrimRetainedMemoryByLeaseSize(
            leaseLength,
            NativeTypeLayout.StorageSize<T>(),
            NativeTypeLayout.Alignment<T>());

    /// <summary>Permanently closes the arena and applies its configured memory policy.</summary>
    public void Dispose() => _kernel.Dispose();
}

/// <summary>A heterogeneous generation-bound arena handle.</summary>
/// <typeparam name="T">The value or reference type stored by the arena.</typeparam>
public readonly ref struct ArenaLease<T>
{
    private readonly NativeOwnerKernel? _kernel;
    private readonly long _generation;
    private readonly NativeGeneration? _generationState;
    private readonly NativeAllocation? _allocationState;
    private readonly long _payload;
    private readonly int _length;
    private readonly int _capacity;

    internal ArenaLease(
        NativeOwnerKernel kernel,
        NativeRegionAllocation allocation)
    {
        _kernel = kernel;
        _generation = allocation.Generation;
        _generationState = allocation.GenerationState;
        _allocationState = allocation.AllocationState;
        _payload = allocation.AllocationId;
        _length = allocation.Length;
        _capacity = allocation.Capacity;
    }

    internal ArenaLease(
        NativeOwnerKernel kernel,
        NativeArenaAllocation allocation)
    {
        _kernel = kernel;
        _generation = allocation.Generation;
        _generationState = allocation.GenerationState;
        _allocationState = null;
        _payload = allocation.Pointer.ToInt64();
        _length = allocation.Length;
        _capacity = allocation.Capacity;
    }

    internal NativeOwnerKernel KernelForComposite =>
        GetKernel("NativeLeaseOperations.Access");

    internal long GenerationForComposite => _generation;

    internal long AllocationIdForComposite =>
        GetAllocationState(
            "NativeLeaseOperations.Access").Id;

    internal NativeGeneration GenerationStateForComposite =>
        GetGenerationState("NativeLeaseOperations.Access");

    internal NativeAllocation AllocationStateForComposite =>
        GetAllocationState("NativeLeaseOperations.Access");

    internal NativeOperationToken EnterForComposite(string operation) =>
        EnterCompositeOperation(operation);

    /// <summary>Gets the logical element count.</summary>
    public int Length
    {
        get
        {
            _ = GetMetadata(nameof(Length));
            return _length;
        }
    }

    /// <summary>Gets the physical capacity represented by this handle.</summary>
    public int Capacity
    {
        get
        {
            _ = GetMetadata(nameof(Capacity));
            return _capacity;
        }
    }

    /// <summary>Reads or writes one value through the owner operation gate.</summary>
    public T this[int index]
    {
        get
        {
            NativeArenaOperationToken token = EnterOperation("get_Item");
            try
            {
                ValidateIndex(index);
                return token.GetValue<T>(index);
            }
            finally
            {
                token.Dispose();
            }
        }
        set
        {
            NativeArenaOperationToken token = EnterOperation("set_Item");
            try
            {
                ValidateIndex(index);
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
        NativeArenaOperationToken token = EnterOperation(nameof(Clear));
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
        NativeArenaOperationToken token = EnterOperation(nameof(CopyFrom));
        try
        {
            if (source.Length != _length)
            {
                throw new ArgumentException(
                    "The source length must equal the arena logical length.",
                    nameof(source));
            }

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
        NativeArenaOperationToken token = EnterOperation(nameof(CopyTo));
        try
        {
            if (destination.Length < _length)
            {
                throw new ArgumentException(
                    "The destination must contain at least the arena logical length.",
                    nameof(destination));
            }

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
        NativeOwnerKernel kernel = GetKernel(nameof(Access));
        if (IsArenaFast
            && kernel.CanUseArenaFastLocalOperation)
        {
            NativeArenaLocalOperationToken localToken =
                kernel.EnterArenaFastLocalOperation(
                    GetGenerationState(nameof(Access)),
                    nameof(Access));
            try
            {
                action(new NativeLeaseView<T>(
                    GetArenaPointer(),
                    _length));
            }
            finally
            {
                localToken.Dispose();
            }

            return;
        }

        NativeArenaOperationToken token = EnterOperation(nameof(Access));
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TResult Read<TResult>(NativeLeaseFunc<T, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeOwnerKernel kernel = GetKernel(nameof(Read));
        if (IsArenaFast
            && kernel.CanUseArenaFastLocalOperation)
        {
            NativeArenaLocalOperationToken localToken =
                kernel.EnterArenaFastLocalOperation(
                    GetGenerationState(nameof(Read)),
                    nameof(Read));
            try
            {
                return action(new NativeLeaseView<T>(
                    GetArenaPointer(),
                    _length));
            }
            finally
            {
                localToken.Dispose();
            }
        }

        NativeArenaOperationToken token = EnterOperation(nameof(Read));
        try
        {
            return action(token.GetView<T>());
        }
        finally
        {
            token.Dispose();
        }
    }

    private void ValidateIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= _length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The index is outside the logical arena range.");
        }
    }

    private NativeOwnerKernel GetKernel(string operation) =>
        _kernel ?? throw new NativeAllocationUninitializedException(nameof(ArenaLease<T>), operation);

    private NativeGeneration GetGenerationState(string operation) =>
        _generationState
        ?? throw new NativeAllocationUninitializedException(
            nameof(ArenaLease<T>),
            operation);

    private NativeAllocation GetAllocationState(string operation) =>
        _allocationState
        ?? (IsArenaFast
            ? GetKernel(operation).MaterializeArenaAllocation(
                CreateArenaAllocation(),
                CalculateArenaStorageBytes(),
                operation)
            : throw new NativeAllocationUninitializedException(
                nameof(ArenaLease<T>),
                operation));

    private NativeArenaOperationToken EnterOperation(string operation) =>
        IsArenaFast
            ? GetKernel(operation).EnterArenaFastOperation(
                CreateArenaAllocation(),
                operation)
            : new NativeArenaOperationToken(
                GetKernel(operation).EnterOperation(
                    GetGenerationState(operation),
                    GetAllocationState(operation),
                    _generation,
                    _payload,
                    operation));

    private NativeOperationToken EnterCompositeOperation(
        string operation)
    {
        NativeAllocation allocation =
            GetAllocationState(operation);
        return GetKernel(operation).EnterOperation(
            GetGenerationState(operation),
            allocation,
            _generation,
            allocation.Id,
            operation);
    }

    private NativeHandleMetadata GetMetadata(string operation) =>
        IsArenaFast
            ? GetKernel(operation).ValidateArenaFastHandle(
                CreateArenaAllocation(),
                operation)
            : GetKernel(operation).ValidateHandle(
                GetGenerationState(operation),
                GetAllocationState(operation),
                _generation,
                _payload,
                operation);

    private bool IsArenaFast =>
        _allocationState is null
        && _generationState is not null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IntPtr GetArenaPointer() => new(_payload);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeArenaAllocation CreateArenaAllocation() =>
        new(
            GetGenerationState("NativeArena.Scratch"),
            GetArenaPointer(),
            OriginalCursor: 0,
            _length);

    private nuint CalculateArenaStorageBytes() =>
        checked(
            (nuint)(uint)_length
            * (nuint)NativeTypeLayout.StorageSize<T>());
}
