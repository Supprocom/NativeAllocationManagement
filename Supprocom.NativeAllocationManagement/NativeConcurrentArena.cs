using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Owns synchronized transferable native ranges.</summary>
public sealed class NativeConcurrentArena : IDisposable
{
    private readonly NativeOwnerKernel _kernel;

    /// <summary>Gets the stable process-local allocator identity, independent of generation.</summary>
    public long Id => _kernel.Id;

    internal NativeOwnerKernel KernelForInitialization => _kernel;

    internal NativeOwnerLifecycle CurrentLifecycle => _kernel.Lifecycle;

    internal int CurrentAllocationRecordCountForTest => _kernel.CurrentAllocationRecordCountForTest();

    internal int CurrentInitializationCountForTest =>
        _kernel.CurrentInitializationCountForTest();

    internal int CurrentGenerationActiveOperationsForTest =>
        _kernel.CurrentGenerationActiveOperationsForTest();

    internal int CurrentReferenceRootCountForTest => _kernel.CurrentReferenceRootCountForTest();

    internal int CurrentConcurrentReservationCountForTest =>
        _kernel.CurrentConcurrentReservationCountForTest();

    internal long CurrentFastLaneSlowPathCountForTest =>
        _kernel.ArenaFastSlowPathCountForTest();

    internal (
        long SlowPaths,
        long SlotCreations,
        int DictionaryRecords) CurrentTransferMetricsForTest =>
        _kernel.ArenaTransferMetricsForTest();

    internal int QuarantinedSegmentCountForTest => _kernel.QuarantinedSegmentCountForTest();

    internal int QuarantinedGenerationCountForTest => _kernel.QuarantinedGenerationCountForTest();

    internal int RetiredGenerationCountForTest => _kernel.RetiredGenerationCountForTest();

    internal int QuarantineCapacityForTest => _kernel.QuarantineCapacityForTest();

    internal (int Slabs, int AvailableSlabs, int Bumps, int OwnerSegments) CurrentBankCapacitiesForTest =>
        _kernel.CurrentBankCapacitiesForTest();

    internal (int Ordinary, int Scoped, int SegmentCount) CurrentBumpTraversalForTest => _kernel.CurrentBumpTraversalForTest();

    internal long[] CurrentSegmentOrdinalsForTest => _kernel.CurrentSegmentOrdinalsForTest();

    /// <summary>Captures actual structural counters without granting native access.</summary>
    /// <remarks>Use a quiescent maintenance boundary to reconcile concurrent fast-lane progress.</remarks>
    public NativeOwnerDiagnosticSnapshot CaptureDiagnosticSnapshot() =>
        _kernel.GetDiagnosticSnapshot();

    /// <summary>Reads the current logical and physical state of this owner.</summary>
    public NativeOwnerStatistics GetStatistics() => _kernel.GetStatistics();

    /// <summary>Creates an active arena unless the first generation is explicitly deferred.</summary>
    /// <param name="preAllocateBytes">Optional initial byte reservation.</param>
    /// <param name="returnMemoryOnDispose">The physical cleanup policy used by <see cref="Dispose"/>.</param>
    /// <param name="doNotLeaseOnDeclaration">When true, defer the first generation until <see cref="LeaseFromMemory"/>.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "RS0027", Justification = "Preserve the published optional signature; the budget-first overload has only required arguments and strictly greater arity, so it cannot capture any existing call.")]
    public NativeConcurrentArena(
        nuint preAllocateBytes = 0,
        NativeMemoryReturn returnMemoryOnDispose = NativeMemoryReturn.Reuse,
        bool doNotLeaseOnDeclaration = false)
    {
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        _kernel = NativeOwnerKernel.CreateArena(
            preAllocateBytes,
            "NativeConcurrentArena",
            returnMemoryOnDispose,
            doNotLeaseOnDeclaration);
    }

    /// <summary>Creates a synchronized arena with backing admitted by a shared ceiling.</summary>
    /// <param name="budget">The backing admission domain.</param>
    /// <param name="preAllocateBytes">The initial byte reservation.</param>
    /// <param name="returnMemoryOnDispose">The final storage cleanup policy.</param>
    /// <param name="doNotLeaseOnDeclaration">Defers the first generation when true.</param>
    public NativeConcurrentArena(
        NativeMemoryBudget budget, nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose, bool doNotLeaseOnDeclaration)
    {
        ArgumentNullException.ThrowIfNull(budget);
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        _kernel = NativeOwnerKernel.CreateArena(
            preAllocateBytes, "NativeConcurrentArena", returnMemoryOnDispose,
            doNotLeaseOnDeclaration, budget);
    }

    /// <summary>Initializes an ordinary heterogeneous range before publication.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ConcurrentArenaLease<T> Scratch<T>(
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
            return new ConcurrentArenaLease<T>(_kernel, direct);
        }

        if (!containsReferences
            && _kernel.TryLeaseArenaLaneInitialized(
                length,
                elementSize,
                alignment,
                initializer,
                out NativeArenaAllocation lane))
        {
            return new ConcurrentArenaLease<T>(_kernel, lane);
        }

        NativeRegionAllocation allocation = _kernel.LeaseBumpInitialized(
            length,
            elementSize,
            alignment,
            scoped: false,
            containsReferences,
            initializer);
        return new ConcurrentArenaLease<T>(_kernel, allocation);
    }

    /// <summary>Fills an ordinary range with one value before publication, without an initializer callback.</summary>
    /// <param name="length">The complete initialized element count.</param>
    /// <param name="value">The value written to every element before publication.</param>
    public ConcurrentArenaLease<T> Scratch<T>(int length, T value)
        where T : unmanaged =>
        ScratchFilled(length, value, fill: true);

    /// <summary>Publishes an ordinary range without writing its elements.</summary>
    /// <param name="length">The element count.</param>
    /// <remarks>The contents are unspecified: they may hold values an earlier lease in this process wrote. Write every element before reading it, or use <see cref="Scratch{T}(int, T)"/> for a filled range.</remarks>
    public ConcurrentArenaLease<T> ScratchUninitialized<T>(int length)
        where T : unmanaged =>
        ScratchFilled(length, default(T), fill: false);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ConcurrentArenaLease<T> ScratchFilled<T>(int length, T value, bool fill)
        where T : unmanaged
    {
        int elementSize = Unsafe.SizeOf<T>();
        nuint alignment = CalculateArenaAlignment(elementSize);
        if (_kernel.IsArenaFastThread)
        {
            return new ConcurrentArenaLease<T>(
                _kernel,
                _kernel.LeaseArenaBumpFilled(length, elementSize, alignment, value, fill));
        }

        if (_kernel.TryLeaseArenaLaneFilled(length, elementSize, alignment, value, fill, out NativeArenaAllocation lane, out NativePoolThreadCache? laneCache))
        {
            return new ConcurrentArenaLease<T>(_kernel, lane, laneCache);
        }

        NativeRegionAllocation allocation = _kernel.LeaseBumpInitialized(
            length,
            elementSize,
            alignment,
            scoped: false,
            containsReferences: false,
            NativeOwnerKernel.FillInitializer(value));
        return new ConcurrentArenaLease<T>(_kernel, allocation);
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

    internal NativeArenaTransferBatch<T> CreateTransferBatch<T>(
        int count,
        int length)
        where T : unmanaged
    {
        NativeArenaTransferBatchState state =
            _kernel.CreateArenaTransferBatch(
                count,
                length,
                NativeTypeLayout.StorageSize<T>(),
                NativeTypeLayout.Alignment<T>());
        return new NativeArenaTransferBatch<T>(state);
    }

    /// <summary>Initializes a scoped heterogeneous range before publication.</summary>
    public ConcurrentArenaLease<T> ScratchScoped<T>(
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
                _kernel.LeaseArenaScopedBumpInitialized(
                    length,
                    elementSize,
                    alignment,
                    initializer);
            return new ConcurrentArenaLease<T>(_kernel, direct);
        }

        NativeRegionAllocation allocation = _kernel.LeaseBumpInitialized(
            length,
            elementSize,
            alignment,
            scoped: true,
            containsReferences,
            initializer);
        return new ConcurrentArenaLease<T>(_kernel, allocation);
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
public readonly ref struct ConcurrentArenaLease<T>
{
    private readonly NativeOwnerKernel? _kernel;
    private readonly long _generation;
    private readonly NativeGeneration? _generationState;
    private readonly NativeAllocation? _allocationState;
    private readonly long _payload;
    private readonly int _length;
    private readonly int _capacity;
    private readonly long _scopeEpoch;
    // The scratching thread's cache, for a range from its lane: the lease is a
    // ref struct, so its operations run on that thread and skip the lookup.
    private readonly NativePoolThreadCache? _laneCache;

    internal ConcurrentArenaLease(
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
        _scopeEpoch = long.MinValue;
    }

    internal ConcurrentArenaLease(
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
        _scopeEpoch = allocation.ScopeEpoch;
    }

    internal ConcurrentArenaLease(
        NativeOwnerKernel kernel,
        NativeArenaAllocation allocation,
        NativePoolThreadCache? laneCache)
        : this(kernel, allocation) =>
        _laneCache = laneCache;

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
    /// <remarks>The count the lease was published with; reading it does not validate the lease.</remarks>
    public int Length
    {
        get
        {
            _ = GetKernel(nameof(Length));
            return _length;
        }
    }

    /// <summary>Gets the physical capacity represented by this handle.</summary>
    /// <remarks>The capacity the lease was published with; reading it does not validate the lease.</remarks>
    public int Capacity
    {
        get
        {
            _ = GetKernel(nameof(Capacity));
            return _capacity;
        }
    }

    /// <summary>Clears the logical range.</summary>
    public void Clear()
    {
        if (_kernel is NativeOwnerKernel kernel && TryEnterLane(kernel, out NativePoolThreadCache? cache, out int epoch))
        {
            try
            {
                new NativeLeaseView<T>(GetArenaPointer(), _length).Clear();
            }
            finally
            {
                kernel.ExitCacheCountedOperation(_generationState!, cache, epoch);
            }

            return;
        }

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
        if (_kernel is NativeOwnerKernel kernel && TryEnterLane(kernel, out NativePoolThreadCache? cache, out int epoch))
        {
            try
            {
                if (source.Length != _length)
                {
                    throw new ArgumentException(
                        "The source length must equal the arena logical length.",
                        nameof(source));
                }

                new NativeLeaseView<T>(GetArenaPointer(), _length).CopyFrom(source);
            }
            finally
            {
                kernel.ExitCacheCountedOperation(_generationState!, cache, epoch);
            }

            return;
        }

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
        if (_kernel is NativeOwnerKernel kernel && TryEnterLane(kernel, out NativePoolThreadCache? cache, out int epoch))
        {
            try
            {
                if (destination.Length < _length)
                {
                    throw new ArgumentException(
                        "The destination must contain at least the arena logical length.",
                        nameof(destination));
                }

                new NativeLeaseView<T>(GetArenaPointer(), _length).CopyTo(destination);
            }
            finally
            {
                kernel.ExitCacheCountedOperation(_generationState!, cache, epoch);
            }

            return;
        }

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
                    CreateArenaAllocation(),
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

        if (TryEnterLane(kernel, out NativePoolThreadCache? cache, out int epoch))
        {
            try
            {
                action(new NativeLeaseView<T>(GetArenaPointer(), _length));
            }
            finally
            {
                kernel.ExitCacheCountedOperation(_generationState!, cache, epoch);
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
                    CreateArenaAllocation(),
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

        if (TryEnterLane(kernel, out NativePoolThreadCache? cache, out int epoch))
        {
            try
            {
                return action(new NativeLeaseView<T>(GetArenaPointer(), _length));
            }
            finally
            {
                kernel.ExitCacheCountedOperation(_generationState!, cache, epoch);
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

    /// <summary>Runs one compile-time processor over the logical range, without a delegate.</summary>
    /// <typeparam name="TState">The caller state type.</typeparam>
    /// <typeparam name="TResult">The result type, which cannot contain the borrowed span.</typeparam>
    /// <typeparam name="TProcessor">The processor; its static member is called without a delegate.</typeparam>
    /// <remarks>Ranges of elements that are or contain references have no span view; use <see cref="Access"/> or <see cref="Read{TResult}"/>.</remarks>
    public TResult Process<TState, TResult, TProcessor>(scoped in TState state)
        where TState : allows ref struct
        where TProcessor : struct, INativeSpanProcessor<T, TState, TResult>
    {
        // The creating thread and a lane's thread share one call of the
        // processor, so a caller inlining this method inlines the processor
        // once; anything else takes the general path out of line.
        NativeOwnerKernel kernel = GetKernel(nameof(Process));
        if (IsArenaFast)
        {
            NativeArenaLocalOperationToken localToken = default;
            NativePoolThreadCache? cache = null;
            int epoch = 0;
            if (kernel.CanUseArenaFastLocalOperation)
            {
                localToken = kernel.EnterArenaFastLocalOperation(CreateArenaAllocation(), nameof(Process));
            }
            else if (!TryEnterLane(kernel, out cache, out epoch))
            {
                return ProcessThroughToken<TState, TResult, TProcessor>(in state);
            }

            // The operation is left inline after a normal return; only a
            // failed processor takes the out-of-line exit, so no finally runs.
            TResult result;
            try
            {
                result = TProcessor.Invoke(new NativeLeaseView<T>(GetArenaPointer(), _length).AsSpan(), in state);
            }
            catch
            {
                ExitAfterFailure(kernel, _generationState!, cache, epoch, localToken);
                throw;
            }

            if (cache is not null)
            {
                kernel.ExitCacheCountedOperation(_generationState!, cache, epoch);
            }
            else
            {
                localToken.Dispose();
            }

            return result;
        }

        return ProcessThroughToken<TState, TResult, TProcessor>(in state);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private TResult ProcessThroughToken<TState, TResult, TProcessor>(scoped in TState state)
        where TState : allows ref struct
        where TProcessor : struct, INativeSpanProcessor<T, TState, TResult>
    {
        NativeArenaOperationToken token = EnterOperation(nameof(Process));
        try
        {
            return TProcessor.Invoke(token.GetView<T>().AsSpan(), in state);
        }
        finally
        {
            token.Dispose();
        }
    }

    // Leaves a lane or local operation after a failed callback. Static, so
    // the lease's address is never taken.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExitAfterFailure(
        NativeOwnerKernel kernel,
        NativeGeneration generation,
        NativePoolThreadCache? cache,
        int epoch,
        NativeArenaLocalOperationToken localToken)
    {
        if (cache is not null)
        {
            kernel.ExitCacheCountedOperation(generation, cache, epoch);
        }
        else
        {
            localToken.Dispose();
        }
    }

    private NativeOwnerKernel GetKernel(string operation) =>
        _kernel ?? throw new NativeAllocationUninitializedException(nameof(ConcurrentArenaLease<T>), operation);

    private NativeGeneration GetGenerationState(string operation) =>
        _generationState
        ?? throw new NativeAllocationUninitializedException(
            nameof(ConcurrentArenaLease<T>),
            operation);

    private NativeAllocation GetAllocationState(string operation) =>
        _allocationState
        ?? (IsArenaFast
            ? GetKernel(operation).MaterializeArenaAllocation(
                CreateArenaAllocation(),
                CalculateArenaStorageBytes(),
                operation)
            : throw new NativeAllocationUninitializedException(
                nameof(ConcurrentArenaLease<T>),
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

    // A scratch range from a lane of this thread's cache counts its operations
    // there; see NativeOwnerKernel.TryEnterArenaLaneOperation.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryEnterLane(
        NativeOwnerKernel kernel,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NativePoolThreadCache? cache,
        out int epoch)
    {
        if (IsArenaFast && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            return kernel.TryEnterArenaLaneOperation(_laneCache, CreateArenaAllocation(), out cache, out epoch);
        }

        cache = null;
        epoch = 0;
        return false;
    }

    private bool IsArenaFast =>
        _allocationState is null
        && _generationState is not null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IntPtr GetArenaPointer() => new(_payload);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeArenaAllocation CreateArenaAllocation() =>
        new(
            GetGenerationState("NativeConcurrentArena.Scratch"),
            GetArenaPointer(),
            OriginalCursor: 0,
            _length,
            _scopeEpoch);

    private nuint CalculateArenaStorageBytes() =>
        checked(
            (nuint)(uint)_length
            * (nuint)NativeTypeLayout.StorageSize<T>());
}
