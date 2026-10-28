namespace Supprocom.NativeAllocationManagement;

/// <summary>Owns synchronized typed storage for transferable native values.</summary>
/// <typeparam name="T">The unmanaged value type in owned storage.</typeparam>
public sealed class NativeConcurrentPool<T> : IDisposable
{
    private readonly NativeOwnerKernel _kernel;

    /// <summary>Gets the stable process-local allocator identity, independent of generation.</summary>
    public long Id => _kernel.Id;

    internal NativeOwnerKernel KernelForTransfer => _kernel;

    internal NativeOwnerLifecycle CurrentLifecycle => _kernel.Lifecycle;

    internal int CurrentAllocationRecordCountForTest =>
        _kernel.CurrentAllocationRecordCountForTest();

    internal int CurrentInitializationCountForTest =>
        _kernel.CurrentInitializationCountForTest();

    internal int CurrentGenerationActiveOperationsForTest =>
        _kernel.CurrentGenerationActiveOperationsForTest();

    internal int CurrentReferenceRootCountForTest =>
        _kernel.CurrentReferenceRootCountForTest();

    internal int QuarantinedSegmentCountForTest =>
        _kernel.QuarantinedSegmentCountForTest();

    internal int QuarantinedGenerationCountForTest =>
        _kernel.QuarantinedGenerationCountForTest();

    internal int RetiredGenerationCountForTest =>
        _kernel.RetiredGenerationCountForTest();

    internal int QuarantineCapacityForTest =>
        _kernel.QuarantineCapacityForTest();

    internal (int Slabs, int AvailableSlabs, int Bumps, int OwnerSegments)
        CurrentBankCapacitiesForTest =>
            _kernel.CurrentBankCapacitiesForTest();

    internal long CurrentScopeEpochForTest =>
        _kernel.CurrentScopeEpochForTest();

    internal long GenerationCounterForTest =>
        _kernel.GenerationCounterForTest();

    internal long[] CurrentSegmentOrdinalsForTest =>
        _kernel.CurrentSegmentOrdinalsForTest();

    internal void SetScopeEpochForTest(long value) =>
        _kernel.SetScopeEpochForTest(value);

    internal void SetGenerationCounterForTest(long value) =>
        _kernel.SetGenerationCounterForTest(value);

    /// <summary>Creates one active synchronized typed owner.</summary>
    /// <param name="preLease">The typed element capacity to reserve.</param>
    /// <param name="returnMemoryOnDispose">The final storage cleanup policy.</param>
    /// <param name="doNotLeaseOnDeclaration">Defers the first generation when true.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "RS0027", Justification = "Preserve the published optional signature; the budget-first overload has only required arguments and strictly greater arity, so it cannot capture any existing call.")]
    public NativeConcurrentPool(
        int preLease = 0,
        NativeMemoryReturn returnMemoryOnDispose =
            NativeMemoryReturn.ToGarbageCollector,
        bool doNotLeaseOnDeclaration = false)
        : this(
            preLease,
            preAllocateBytes: 0,
            returnMemoryOnDispose,
            doNotLeaseOnDeclaration)
    {
    }

    /// <summary>Creates one active owner with typed and raw reservations.</summary>
    /// <param name="preLease">The typed element capacity to reserve.</param>
    /// <param name="preAllocateBytes">The exact raw byte capacity to reserve.</param>
    /// <param name="returnMemoryOnDispose">The final storage cleanup policy.</param>
    /// <param name="doNotLeaseOnDeclaration">Defers the first generation when true.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "RS0027", Justification = "Preserve the published optional signature; the budget-first overload has only required arguments and strictly greater arity, so it cannot capture any existing call.")]
    public NativeConcurrentPool(
        int preLease,
        nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose =
            NativeMemoryReturn.ToGarbageCollector,
        bool doNotLeaseOnDeclaration = false)
    {
        NativeMemoryReturnValidation.Validate(
            returnMemoryOnDispose,
            nameof(returnMemoryOnDispose));
        _kernel = NativeOwnerKernel.CreatePool(
            preLease,
            preAllocateBytes,
            NativeTypeLayout.StorageSize<T>(),
            $"NativeConcurrentPool<{typeof(T).FullName ?? typeof(T).Name}>",
            returnMemoryOnDispose,
            NativeTypeLayout.ContainsReferences<T>(),
            doNotLeaseOnDeclaration);
    }

    /// <summary>Creates a synchronized pool with backing admitted by a shared ceiling.</summary>
    /// <param name="budget">The backing admission domain.</param>
    /// <param name="preLease">The typed element capacity to reserve.</param>
    /// <param name="preAllocateBytes">The exact raw byte capacity to reserve.</param>
    /// <param name="returnMemoryOnDispose">The final storage cleanup policy.</param>
    /// <param name="doNotLeaseOnDeclaration">Defers the first generation when true.</param>
    public NativeConcurrentPool(
        NativeMemoryBudget budget, int preLease, nuint preAllocateBytes,
        NativeMemoryReturn returnMemoryOnDispose, bool doNotLeaseOnDeclaration)
    {
        ArgumentNullException.ThrowIfNull(budget);
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        _kernel = NativeOwnerKernel.CreatePool(
            preLease, preAllocateBytes, NativeTypeLayout.StorageSize<T>(),
            $"NativeConcurrentPool<{typeof(T).FullName ?? typeof(T).Name}>",
            returnMemoryOnDispose, NativeTypeLayout.ContainsReferences<T>(),
            doNotLeaseOnDeclaration, budget);
    }

    /// <summary>Reads the synchronized owner state.</summary>
    public NativeOwnerStatistics GetStatistics() =>
        _kernel.GetStatistics();

    /// <summary>Captures actual structural counters without granting native access.</summary>
    /// <remarks>Retired counts include quarantined segments; do not add overlapping categories.</remarks>
    public NativeOwnerDiagnosticSnapshot CaptureDiagnosticSnapshot() =>
        _kernel.GetDiagnosticSnapshot();

    /// <summary>Initializes and publishes one synchronized lease.</summary>
    public ConcurrentPooled<T> Rent(
        int length,
        NativeLeaseInitializer<T> initializer)
    {
        NativePoolLease lease = _kernel.RentInitialized(
            length,
            scoped: false,
            initializer);
        return new ConcurrentPooled<T>(_kernel, lease);
    }

    /// <summary>Fills and publishes one synchronized lease without an initializer callback.</summary>
    /// <param name="length">The complete initialized element count.</param>
    /// <param name="value">The value written to every element before publication.</param>
    public ConcurrentPooled<T> Rent(int length, T value) =>
        new(_kernel, _kernel.RentFilled(length, value, fill: true));

    /// <summary>Fills a temporary rental, runs a compile-time processor, and returns it before this call completes.</summary>
    /// <param name="length">The complete initialized element count exposed to the processor.</param>
    /// <param name="value">The value written to every element before processing.</param>
    /// <param name="state">Explicit state passed to the processor.</param>
    /// <typeparam name="TState">The caller state type.</typeparam>
    /// <typeparam name="TResult">The result type, which cannot contain the borrowed span.</typeparam>
    /// <typeparam name="TProcessor">The processor; its static member is called without a delegate.</typeparam>
    /// <returns>The processor result after the rental has been returned.</returns>
    /// <remarks>
    /// Equivalent to <see cref="Rent(int, T)"/>, <see cref="ConcurrentPooled{T}.Process{TState, TResult, TProcessor}(int, in TState)"/>
    /// and disposing the lease, in fewer steps. Processor failure also returns the rental. Requires storage without managed references.
    /// </remarks>
    public TResult Process<TState, TResult, TProcessor>(int length, T value, scoped in TState state)
        where TState : allows ref struct
        where TProcessor : struct, INativeSpanProcessor<T, TState, TResult>
    {
        if (_kernel.TryRentCachedOperation(length, value, out NativePoolThreadCache? cache, out NativeAllocation? allocation, out long allocationId, out int epoch))
        {
            // The rental is left inline after a normal return; only a failed
            // processor takes the out-of-line exit.
            TResult result;
            try
            {
                result = TProcessor.Invoke(NativeOwnerKernel.CachedSpan<T>(allocation, length), in state);
            }
            catch
            {
                ExitAfterFailure(_kernel, cache, allocation, allocationId, epoch);
                throw;
            }

            _kernel.ExitCachedOperationAndReturn(cache, allocation, allocationId, epoch);
            return result;
        }

        return ProcessThroughLease<TState, TResult, TProcessor>(length, value, in state);
    }

    /// <summary>Fills a temporary rental, processes it with explicit caller state, and returns it before this call completes.</summary>
    /// <param name="length">The complete initialized element count exposed to the processor.</param>
    /// <param name="value">The value written to every element before processing.</param>
    /// <param name="state">Explicit state passed to the synchronous processor.</param>
    /// <param name="processor">The bounded operation; its span cannot escape the callback.</param>
    /// <typeparam name="TState">The caller state type.</typeparam>
    /// <typeparam name="TResult">The result type, which cannot contain the borrowed span.</typeparam>
    /// <returns>The processor result after the rental has been returned.</returns>
    /// <remarks>Processor failure also returns the rental. Requires storage without managed references.</remarks>
    public TResult Process<TState, TResult>(
        int length,
        T value,
        TState state,
        NativeSpanStateProcessor<T, TState, TResult> processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        if (_kernel.TryRentCachedOperation(length, value, out NativePoolThreadCache? cache, out NativeAllocation? allocation, out long allocationId, out int epoch))
        {
            TResult result;
            try
            {
                result = processor(NativeOwnerKernel.CachedSpan<T>(allocation, length), state);
            }
            catch
            {
                ExitAfterFailure(_kernel, cache, allocation, allocationId, epoch);
                throw;
            }

            _kernel.ExitCachedOperationAndReturn(cache, allocation, allocationId, epoch);
            return result;
        }

        return ProcessThroughLease<DelegateCall<TState, TResult>, TResult, DelegateProcessor<TState, TResult>>(
            length,
            value,
            new DelegateCall<TState, TResult>(processor, state));
    }

    private readonly struct DelegateCall<TState, TResult>(NativeSpanStateProcessor<T, TState, TResult> processor, TState state)
    {
        internal NativeSpanStateProcessor<T, TState, TResult> Processor { get; } = processor;

        internal TState State { get; } = state;
    }

    private readonly struct DelegateProcessor<TState, TResult>
        : INativeSpanProcessor<T, DelegateCall<TState, TResult>, TResult>
    {
        public static TResult Invoke(Span<T> span, in DelegateCall<TState, TResult> state) =>
            state.Processor(span, state.State);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private TResult ProcessThroughLease<TState, TResult, TProcessor>(int length, T value, scoped in TState state)
        where TState : allows ref struct
        where TProcessor : struct, INativeSpanProcessor<T, TState, TResult>
    {
        using ConcurrentPooled<T> lease = Rent(length, value);
        return lease.Process<TState, TResult, TProcessor>(length, in state);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ExitAfterFailure(
        NativeOwnerKernel kernel,
        NativePoolThreadCache cache,
        NativeAllocation allocation,
        long allocationId,
        int epoch) =>
        kernel.ExitCachedOperationAndReturn(cache, allocation, allocationId, epoch);

    /// <summary>Rents one synchronized lease without writing its elements.</summary>
    /// <param name="length">The element count.</param>
    /// <remarks>
    /// Elements without references are unspecified: they may hold values an earlier lease in this process wrote.
    /// Write every element before reading it, or use <see cref="Rent(int, T)"/>. Elements that are or contain
    /// references start as their default value.
    /// </remarks>
    public ConcurrentPooled<T> RentUninitialized(int length) =>
        new(_kernel, _kernel.RentFilled(length, default(T)!, fill: System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<T>()));

    /// <summary>Initializes one synchronized scoped lease.</summary>
    public ConcurrentPooled<T> LeaseScoped(
        int length,
        NativeLeaseInitializer<T> initializer)
    {
        NativePoolLease lease = _kernel.RentInitialized(
            length,
            scoped: true,
            initializer);
        return new ConcurrentPooled<T>(_kernel, lease);
    }

    /// <summary>Ends the active generation and releases its storage.</summary>
    public void ReturnMemoryToNativeMemory() =>
        _kernel.ReturnMemoryToNativeMemory();

    /// <summary>Ends the active generation and detaches its storage.</summary>
    public void ReturnMemoryToGarbageCollector() =>
        _kernel.ReturnMemoryToGarbageCollector();

    /// <summary>Ends all leases and reuses retained storage.</summary>
    public void ReleaseLeasesToNativeMemory() =>
        _kernel.ReleaseLeasesToNativeMemory();

    /// <summary>Ends all leases after active callbacks drain.</summary>
    public void ReleaseLeasesToGarbageCollector() =>
        _kernel.ReleaseLeasesToGarbageCollector();

    /// <summary>Starts the next synchronized memory generation.</summary>
    public void LeaseFromMemory() =>
        _kernel.LeaseFromMemory();

    /// <summary>Recycles the analyzer-proven scoped lease set.</summary>
    public void RecycleScoped() =>
        _kernel.RecycleScoped();

    /// <summary>Frees all eligible idle storage.</summary>
    public nuint TrimRetainedMemory() =>
        _kernel.TrimRetainedMemory();

    /// <summary>Frees idle storage until the byte budget is met.</summary>
    public nuint TrimRetainedMemoryByBytes(nuint bytesToRelease) =>
        _kernel.TrimRetainedMemoryByBytes(bytesToRelease);

    /// <summary>Frees idle storage for one typed request budget.</summary>
    public nuint TrimRetainedMemoryByLeaseSize(int leaseLength = 1) =>
        _kernel.TrimRetainedMemoryByLeaseSize(
            leaseLength,
            NativeTypeLayout.StorageSize<T>(),
            NativeTypeLayout.Alignment<T>());

    /// <summary>Closes the owner after all native ownership returns.</summary>
    public void Dispose() => _kernel.Dispose();
}
