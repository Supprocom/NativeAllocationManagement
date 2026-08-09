using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>A compact unmanaged handle for one active NativeRegion.</summary>
/// <typeparam name="T">The unmanaged value type in the Region.</typeparam>
public readonly ref struct Local<T>
    where T : unmanaged
{
    private readonly NativeRegionKernel? _kernel;
    private readonly IntPtr _pointer;
    private readonly int _length;

    internal Local(
        NativeRegionKernel kernel,
        IntPtr pointer,
        int length)
    {
        _kernel = kernel;
        _pointer = pointer;
        _length = length;
    }

    /// <summary>Gets the immutable logical element count.</summary>
    public int Length => _length;

    /// <summary>Gets the immutable physical capacity in elements.</summary>
    public int Capacity => _length;

    /// <summary>Clears the logical range after one Region state check.</summary>
    public void Clear()
    {
        NativeRegionKernel kernel = EnterBorrow(nameof(Clear));
        try
        {
            CreateView().Clear();
        }
        finally
        {
            kernel.ExitBorrow();
        }
    }

    /// <summary>Copies an exact source range after one Region state check.</summary>
    public void CopyFrom(scoped ReadOnlySpan<T> source)
    {
        if (source.Length != _length)
        {
            throw new ArgumentException(
                "The source length must equal the local logical length.",
                nameof(source));
        }

        NativeRegionKernel kernel = EnterBorrow(nameof(CopyFrom));
        try
        {
            CreateView().CopyFrom(source);
        }
        finally
        {
            kernel.ExitBorrow();
        }
    }

    /// <summary>Copies the logical range after one Region state check.</summary>
    public void CopyTo(scoped Span<T> destination)
    {
        if (destination.Length < _length)
        {
            throw new ArgumentException(
                "The destination must contain the complete local range.",
                nameof(destination));
        }

        NativeRegionKernel kernel = EnterBorrow(nameof(CopyTo));
        try
        {
            CreateView().CopyTo(destination);
        }
        finally
        {
            kernel.ExitBorrow();
        }
    }

    /// <summary>Runs one synchronous bounded write callback.</summary>
    public void Access(NativeLeaseAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeRegionKernel kernel = EnterBorrow(nameof(Access));
        try
        {
            action(CreateView());
        }
        finally
        {
            kernel.ExitBorrow();
        }
    }

    /// <summary>Runs one synchronous bounded read callback.</summary>
    public TResult Read<TResult>(NativeLeaseFunc<T, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeRegionKernel kernel = EnterBorrow(nameof(Read));
        try
        {
            return action(CreateView());
        }
        finally
        {
            kernel.ExitBorrow();
        }
    }

    /// <summary>Processes the logical range with explicit caller state.</summary>
    public TResult Process<TState, TResult>(
        TState state,
        NativeSpanStateProcessor<T, TState, TResult> processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        NativeRegionKernel kernel = EnterBorrow(nameof(Process));
        try
        {
            unsafe
            {
                return processor(
                    new Span<T>((void*)_pointer, _length),
                    state);
            }
        }
        finally
        {
            kernel.ExitBorrow();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeRegionKernel EnterBorrow(string operation)
    {
        NativeRegionKernel kernel = GetKernel(operation);
        kernel.EnterBorrow(operation);
        return kernel;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeRegionKernel GetKernel(string operation) =>
        _kernel
        ?? throw new NativeAllocationUninitializedException(
            nameof(Local<T>),
            operation);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeLeaseView<T> CreateView() =>
        new(_pointer, _length);

}
