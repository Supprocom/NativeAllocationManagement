using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>A synchronized generation-bound typed lease.</summary>
/// <typeparam name="T">The value or reference type in the lease.</typeparam>
public readonly ref struct ConcurrentPooled<T>
{
    private readonly NativeOwnerKernel? _kernel;
    private readonly long _generation;
    private readonly long _allocationId;
    private readonly NativeGeneration? _generationState;
    private readonly NativeAllocation? _allocationState;
    // A lease's shape is fixed when it is published, so the handle carries it
    // like an array carries its length: reading it needs no validated operation.
    private readonly int _length;
    private readonly int _capacity;

    internal ConcurrentPooled(
        NativeOwnerKernel kernel,
        NativePoolLease lease)
    {
        NativeAllocation allocation = lease.AllocationState;
        _kernel = kernel;
        _generation = allocation.GenerationState.Number;
        _allocationId = lease.AllocationId;
        _generationState = allocation.GenerationState;
        _allocationState = allocation;
        _length = allocation.Length;
        _capacity = allocation.Capacity;
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
    /// <remarks>The count the lease was published with; reading it does not validate the lease.</remarks>
    public int Length
    {
        get
        {
            _ = GetKernel(nameof(Length));
            return _length;
        }
    }

    /// <summary>Gets the physical element capacity.</summary>
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
        if (TryEnterCached(out NativePoolThreadCache? cache, out int epoch))
        {
            try
            {
                new NativeLeaseView<T>(_allocationState!).Clear();
            }
            finally
            {
                ExitCached(cache, epoch);
            }

            return;
        }

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

    /// <summary>Copies one exact bounded source.</summary>
    public void CopyFrom(scoped ReadOnlySpan<T> source)
    {
        if (TryEnterCached(out NativePoolThreadCache? cache, out int epoch))
        {
            try
            {
                new NativeLeaseView<T>(_allocationState!).CopyFrom(source);
            }
            finally
            {
                ExitCached(cache, epoch);
            }

            return;
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

    /// <summary>Copies the logical range into one bounded destination.</summary>
    public void CopyTo(scoped Span<T> destination)
    {
        if (TryEnterCached(out NativePoolThreadCache? cache, out int epoch))
        {
            try
            {
                new NativeLeaseView<T>(_allocationState!).CopyTo(destination);
            }
            finally
            {
                ExitCached(cache, epoch);
            }

            return;
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

    /// <summary>Runs one bounded synchronized write callback.</summary>
    public void Access(NativeLeaseAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TryEnterCached(out NativePoolThreadCache? cache, out int epoch))
        {
            try
            {
                action(new NativeLeaseView<T>(_allocationState!));
            }
            finally
            {
                ExitCached(cache, epoch);
            }

            return;
        }

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

    /// <summary>Runs one bounded synchronized read callback.</summary>
    public TResult Read<TResult>(NativeLeaseFunc<T, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TryEnterCached(out NativePoolThreadCache? cache, out int epoch))
        {
            try
            {
                return action(new NativeLeaseView<T>(_allocationState!));
            }
            finally
            {
                ExitCached(cache, epoch);
            }
        }

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

    /// <summary>Processes one bounded prefix with explicit caller state.</summary>
    public TResult Process<TState, TResult>(
        int length,
        scoped in TState state,
        NativeLeaseStateFunc<T, TState, TResult> callback)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (TryEnterCached(out NativePoolThreadCache? cache, out int epoch))
        {
            try
            {
                CheckPrefix(length);
                return callback(new NativeLeaseView<T>(_allocationState!, length), state);
            }
            finally
            {
                ExitCached(cache, epoch);
            }
        }

        NativeOperationToken token = EnterOperation(nameof(Process));
        try
        {
            return callback(
                token.GetView<T>(length),
                state);
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Processes one logical prefix with a compile-time processor during one admitted operation.</summary>
    /// <typeparam name="TState">The caller state type.</typeparam>
    /// <typeparam name="TResult">The result type, which cannot contain the borrowed span.</typeparam>
    /// <typeparam name="TProcessor">The processor; its static member is called without a delegate.</typeparam>
    /// <remarks>Requires storage without managed references.</remarks>
    public TResult Process<TState, TResult, TProcessor>(
        int length,
        scoped in TState state)
        where TState : allows ref struct
        where TProcessor : struct, INativeSpanProcessor<T, TState, TResult>
    {
        if (TryEnterCached(out NativePoolThreadCache? cache, out int epoch))
        {
            TResult result;
            try
            {
                CheckPrefix(length);
                result = TProcessor.Invoke(NativeOwnerKernel.CachedSpan<T>(_allocationState!, length), in state);
            }
            catch
            {
                ExitCachedAfterFailure(_kernel!, _generationState!, _allocationState!, cache, epoch);
                throw;
            }

            ExitCached(cache, epoch);
            return result;
        }

        return ProcessThroughToken<TState, TResult, TProcessor>(length, in state);
    }

    // Out of line, so a caller inlining Process inlines the processor once.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private TResult ProcessThroughToken<TState, TResult, TProcessor>(
        int length,
        scoped in TState state)
        where TState : allows ref struct
        where TProcessor : struct, INativeSpanProcessor<T, TState, TResult>
    {
        NativeOperationToken token = EnterOperation(nameof(Process));
        try
        {
            return TProcessor.Invoke(token.GetView<T>(length).AsSpan(), in state);
        }
        finally
        {
            token.Dispose();
        }
    }

    /// <summary>Returns this synchronized lease exactly once.</summary>
    public void Dispose() =>
        Return(_kernel, _generationState, _allocationState, _generation, _allocationId);

    // One call, so the finally of a using statement over the lease stays small
    // enough for the JIT to run inline rather than as a funclet.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Return(
        NativeOwnerKernel? kernel,
        NativeGeneration? generation,
        NativeAllocation? allocation,
        long generationNumber,
        long allocationId)
    {
        if (allocation?.CacheOwner is NativePoolThreadCache cache
            && NativeOwnerKernel.TryReturnCachedLease(cache, generation!, allocation, generationNumber, allocationId))
        {
            return;
        }

        if (kernel is null || generation is null || allocation is null)
        {
            throw new NativeAllocationUninitializedException(nameof(ConcurrentPooled<T>), nameof(Dispose));
        }

        kernel.ReturnLease(generation, allocation, generationNumber, allocationId);
    }

    // This thread's cached lease enters and exits its operations without an
    // operation token; any other lease takes the kernel's admission path.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryEnterCached([NotNullWhen(true)] out NativePoolThreadCache? cache, out int epoch)
    {
        NativeAllocation? allocation = _allocationState;
        if (allocation?.CacheOwner is NativePoolThreadCache owner
            && NativeOwnerKernel.TryEnterCachedLeaseOperation(owner, _generationState!, allocation, _generation, _allocationId, out epoch))
        {
            cache = owner;
            return true;
        }

        cache = null;
        epoch = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExitCached(NativePoolThreadCache cache, int epoch) =>
        _kernel!.ExitCachedLeaseOperation(_generationState!, _allocationState!, cache, epoch);

    // A cached operation leaves inline after a normal return; only a failed
    // callback comes here, so no finally runs on every operation. Static, so
    // the handle's address is never taken.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExitCachedAfterFailure(
        NativeOwnerKernel kernel,
        NativeGeneration generation,
        NativeAllocation allocation,
        NativePoolThreadCache cache,
        int epoch) =>
        kernel.ExitCachedLeaseOperation(generation, allocation, cache, epoch);

    // The same checks as an operation token's prefix view.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckPrefix(int length)
    {
        if ((uint)length > (uint)_allocationState!.Length)
        {
            ThrowPrefixLength(length);
        }
    }

    [DoesNotReturn]
    private static void ThrowPrefixLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        throw new ArgumentOutOfRangeException(
            nameof(length),
            length,
            "The prefix length exceeds the lease length.");
    }

    private NativeOwnerKernel GetKernel(string operation) =>
        _kernel
        ?? throw new NativeAllocationUninitializedException(
            nameof(ConcurrentPooled<T>),
            operation);

    private NativeGeneration GetGenerationState(string operation) =>
        _generationState
        ?? throw new NativeAllocationUninitializedException(
            nameof(ConcurrentPooled<T>),
            operation);

    private NativeAllocation GetAllocationState(string operation) =>
        _allocationState
        ?? throw new NativeAllocationUninitializedException(
            nameof(ConcurrentPooled<T>),
            operation);

    private NativeOperationToken EnterOperation(string operation) =>
        GetKernel(operation).EnterOperation(
            GetGenerationState(operation),
            GetAllocationState(operation),
            _generation,
            _allocationId,
            operation);
}
