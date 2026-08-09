using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement;

/// <summary>A token-bound capability for one typed native slab.</summary>
/// <typeparam name="T">The unmanaged value type in the slab.</typeparam>
public readonly ref struct Pooled<T>
    where T : unmanaged
{
    private readonly NativePoolKernel<T>? _kernel;
    private readonly int _slabIndex;
    private readonly long _token;
    private readonly IntPtr _pointer;
    private readonly int _length;
    private readonly int _capacity;

    internal Pooled(
        NativePoolKernel<T> kernel,
        int slabIndex,
        long token,
        IntPtr pointer,
        int length,
        int capacity)
    {
        _kernel = kernel;
        _slabIndex = slabIndex;
        _token = token;
        _pointer = pointer;
        _length = length;
        _capacity = capacity;
    }

    /// <summary>Gets the immutable logical element count.</summary>
    public int Length => _length;

    /// <summary>Gets the immutable physical element capacity.</summary>
    public int Capacity => _capacity;

    /// <summary>Clears the logical range during one validated borrow.</summary>
    public void Clear()
    {
        PooledBorrow<T> borrow = EnterBorrow(nameof(Clear));
        try
        {
            borrow.View.Clear();
        }
        finally
        {
            borrow.Dispose();
        }
    }

    /// <summary>Copies one exact source during one validated borrow.</summary>
    public void CopyFrom(scoped ReadOnlySpan<T> source)
    {
        if (source.Length != _length)
        {
            throw new ArgumentException(
                "The source length must equal the pooled logical length.",
                nameof(source));
        }

        PooledBorrow<T> borrow = EnterBorrow(nameof(CopyFrom));
        try
        {
            borrow.View.CopyFrom(source);
        }
        finally
        {
            borrow.Dispose();
        }
    }

    /// <summary>Copies the logical range during one validated borrow.</summary>
    public void CopyTo(scoped Span<T> destination)
    {
        if (destination.Length < _length)
        {
            throw new ArgumentException(
                "The destination must contain the pooled logical length.",
                nameof(destination));
        }

        PooledBorrow<T> borrow = EnterBorrow(nameof(CopyTo));
        try
        {
            borrow.View.CopyTo(destination);
        }
        finally
        {
            borrow.Dispose();
        }
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
        NativePoolKernel<T> kernel = GetKernel(nameof(Process));
        IntPtr pointer = kernel.EnterBorrow(
            _slabIndex,
            _token,
            logicalLength,
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
            kernel.ExitBorrow(_slabIndex);
        }
    }

    /// <summary>Returns this slab exactly once.</summary>
    public void Dispose() => GetKernel(nameof(Dispose)).Return(
        _slabIndex,
        _token,
        _length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PooledBorrow<T> EnterBorrow(string operation)
    {
        NativePoolKernel<T> kernel = GetKernel(operation);
        IntPtr pointer = kernel.EnterBorrow(
            _slabIndex,
            _token,
            _length,
            operation);
        return new PooledBorrow<T>(
            kernel,
            _slabIndex,
            pointer,
            _length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativePoolKernel<T> GetKernel(string operation)
    {
        NativePoolKernel<T>? kernel = _kernel;
        if (kernel is null)
        {
            ThrowUninitialized(operation);
        }

        return kernel;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUninitialized(string operation) =>
        throw new NativeAllocationUninitializedException(
            nameof(Pooled<T>),
            operation);
}

internal readonly ref struct PooledBorrow<T>
    where T : unmanaged
{
    private readonly NativePoolKernel<T> _kernel;
    private readonly int _slabIndex;

    internal PooledBorrow(
        NativePoolKernel<T> kernel,
        int slabIndex,
        IntPtr pointer,
        int length)
    {
        _kernel = kernel;
        _slabIndex = slabIndex;
        View = new NativeLeaseView<T>(pointer, length);
    }

    internal NativeLeaseView<T> View { get; }

    internal void Dispose() => _kernel.ExitBorrow(_slabIndex);
}
