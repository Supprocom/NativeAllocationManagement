using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement;

/// <summary>A token-bound capability for one typed native slab.</summary>
/// <typeparam name="T">The unmanaged value type in the slab.</typeparam>
public readonly ref struct Pooled<T>
    where T : unmanaged
{
    private readonly NativePool<T>? _kernel;
    private readonly NativePool<T>.Slab? _slab;
    private readonly long _token;
    private readonly int _length;
    private readonly int _capacity;

    internal Pooled(
        NativePool<T> kernel,
        NativePool<T>.Slab slab,
        long token,
        int length,
        int capacity)
    {
        _kernel = kernel;
        _slab = slab;
        _token = token;
        _length = length;
        _capacity = capacity;
    }

    /// <summary>Gets the immutable logical element count.</summary>
    public int Length => _length;

    /// <summary>Gets the immutable physical element capacity.</summary>
    public int Capacity => _capacity;

    /// <summary>Moves this lexical lease without allocating or copying its payload and clears the source.</summary>
    /// <remarks>
    /// Previous aliases become stale. Unlike NativeTransfer.Move, failure preserves
    /// the source so it can still return its slot. Movement requires the owner thread
    /// and no entered borrow; it does not grant heap storage or cross-thread access.
    /// </remarks>
    [SuppressMessage("Design", "CA1000", Justification = "Destructive lexical movement consumes the exact element type through a ref source.")]
    public static Pooled<T> Move(scoped ref Pooled<T> source)
    {
        NativePool<T> kernel = source.GetKernel(nameof(Move));
        long token = kernel.Move(source._slab!, source._token);
        Pooled<T> destination = new(kernel, source._slab!, token,
            source._length, source._capacity);
        source = default;
        return destination;
    }

    /// <summary>Returns the logical range for direct use after one owner and lease check.</summary>
    /// <remarks>
    /// The span is confined to this lease's scope and carries no ownership. Use it only
    /// while the lease is current: not after Dispose or Move. The bundled analyzer reports later use.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe Span<T> AsSpan() =>
        new((void*)GetKernel(nameof(AsSpan)).GetLeasePointer(_slab!, _token, nameof(AsSpan)), _length);

    /// <summary>Clears the logical range after validating this lease.</summary>
    public void Clear() => GetKernel(nameof(Clear)).Clear(_slab!, _token, _length);

    /// <summary>Copies one exact source after validating this lease.</summary>
    public void CopyFrom(scoped ReadOnlySpan<T> source)
    {
        if (source.Length != _length)
        {
            throw new ArgumentException(
                "The source length must equal the pooled logical length.",
                nameof(source));
        }

        GetKernel(nameof(CopyFrom)).CopyFrom(_slab!, _token, source);
    }

    /// <summary>Copies the logical range after validating this lease.</summary>
    public void CopyTo(scoped Span<T> destination)
    {
        if (destination.Length < _length)
        {
            throw new ArgumentException(
                "The destination must contain the pooled logical length.",
                nameof(destination));
        }

        GetKernel(nameof(CopyTo)).CopyTo(_slab!, _token, _length, destination);
    }

    /// <summary>Runs one bounded write callback after one token check.</summary>
    public void Access(NativeLeaseAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        PooledBorrow<T> borrow = EnterBorrow(nameof(Access));
        try
        {
            action(borrow.View);
        }
        finally
        {
            borrow.Dispose();
        }
    }

    /// <summary>Runs one bounded read callback after one token check.</summary>
    public TResult Read<TResult>(NativeLeaseFunc<T, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        PooledBorrow<T> borrow = EnterBorrow(nameof(Read));
        try
        {
            return action(borrow.View);
        }
        finally
        {
            borrow.Dispose();
        }
    }

    /// <summary>Processes one logical prefix during one validated borrow.</summary>
    public TResult Process<TState, TResult>(
        int logicalLength,
        TState state,
        NativeSpanStateProcessor<T, TState, TResult> processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)logicalLength, (uint)_length, nameof(logicalLength));
        NativePool<T> kernel = GetKernel(nameof(Process));
        IntPtr pointer = kernel.EnterBorrow(
            _slab!,
            _token,
            nameof(Process));
        try
        {
            unsafe
            {
                return processor(
                    new Span<T>((void*)pointer, logicalLength),
                    state);
            }
        }
        finally
        {
            kernel.ExitBorrow(_slab!);
        }
    }

    /// <summary>Processes one logical prefix with a compile-time processor during one validated borrow.</summary>
    /// <typeparam name="TState">The caller state type.</typeparam>
    /// <typeparam name="TResult">The result type, which cannot contain the borrowed span.</typeparam>
    /// <typeparam name="TProcessor">The processor; its static member is called without a delegate.</typeparam>
    public TResult Process<TState, TResult, TProcessor>(
        int logicalLength,
        scoped in TState state)
        where TState : allows ref struct
        where TProcessor : struct, INativeSpanProcessor<T, TState, TResult>
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)logicalLength, (uint)_length, nameof(logicalLength));
        NativePool<T> kernel = GetKernel(nameof(Process));
        IntPtr pointer = kernel.EnterBorrow(
            _slab!,
            _token,
            nameof(Process));
        try
        {
            unsafe
            {
                return TProcessor.Invoke(
                    new Span<T>((void*)pointer, logicalLength),
                    in state);
            }
        }
        finally
        {
            kernel.ExitBorrow(_slab!);
        }
    }

    /// <summary>Returns this slab exactly once.</summary>
    public void Dispose() => GetKernel(nameof(Dispose)).Return(
        _slab!,
        _token,
        _length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PooledBorrow<T> EnterBorrow(string operation)
    {
        NativePool<T> kernel = GetKernel(operation);
        IntPtr pointer = kernel.EnterBorrow(
            _slab!,
            _token,
            operation);
        return new PooledBorrow<T>(
            kernel,
            _slab!,
            pointer,
            _length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativePool<T> GetKernel(string operation)
    {
        NativePool<T>? kernel = _kernel;
        if (kernel is null)
        {
            ThrowUninitialized(operation);
        }

        return kernel;
    }

    [DoesNotReturn]
    private static void ThrowUninitialized(string operation) =>
        throw new NativeAllocationUninitializedException(
            nameof(Pooled<T>),
            operation);
}

internal readonly ref struct PooledBorrow<T>
    where T : unmanaged
{
    private readonly NativePool<T> _kernel;
    private readonly NativePool<T>.Slab _slab;

    internal PooledBorrow(
        NativePool<T> kernel,
        NativePool<T>.Slab slab,
        IntPtr pointer,
        int length)
    {
        _kernel = kernel;
        _slab = slab;
        View = new NativeLeaseView<T>(pointer, length);
    }

    internal NativeLeaseView<T> View { get; }

    internal void Dispose() => _kernel.ExitBorrow(_slab);
}
