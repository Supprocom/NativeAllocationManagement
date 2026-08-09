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

    /// <summary>Gets the logical element count.</summary>
    public int Length => GetValidatedLength(nameof(Length));

    /// <summary>Gets the physical capacity in elements.</summary>
    public int Capacity => GetValidatedLength(nameof(Capacity));

    /// <summary>Reads or writes one value after one Region state check.</summary>
    public T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            NativeRegionKernel kernel = GetKernel("get_Item");
            kernel.ValidateActive("get_Item");
            try
            {
                ValidateIndex(index, _length);
                unsafe
                {
                    return Unsafe.Add(
                        ref Unsafe.AsRef<T>((void*)_pointer),
                        index);
                }
            }
            finally
            {
                GC.KeepAlive(kernel);
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            NativeRegionKernel kernel = GetKernel("set_Item");
            kernel.ValidateActive("set_Item");
            try
            {
                ValidateIndex(index, _length);
                unsafe
                {
                    Unsafe.Add(
                        ref Unsafe.AsRef<T>((void*)_pointer),
                        index) = value;
                }
            }
            finally
            {
                GC.KeepAlive(kernel);
            }
        }
    }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetValidatedLength(string operation)
    {
        ValidateActive(operation);
        return _length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateActive(string operation)
    {
        GetKernel(operation).ValidateActive(operation);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateIndex(int index, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                "The index is outside the logical local range.");
        }
    }
}
