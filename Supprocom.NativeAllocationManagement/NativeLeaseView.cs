using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>A scoped view for one active native lease operation.</summary>
/// <typeparam name="T">The value type or reference type in the owner.</typeparam>
public readonly ref struct NativeLeaseView<T>
{
    private readonly NativeAllocation? _allocation;
    private readonly IntPtr _directPointer;
    private readonly int _length;

    internal NativeLeaseView(NativeAllocation allocation)
    {
        _allocation = allocation;
        _directPointer = IntPtr.Zero;
        _length = -1;
    }

    internal NativeLeaseView(
        NativeAllocation allocation,
        int length)
    {
        _allocation = allocation;
        _directPointer = IntPtr.Zero;
        _length = length;
    }

    internal unsafe NativeLeaseView(
        IntPtr pointer,
        int length)
    {
        _allocation = null;
        _directPointer = pointer;
        _length = length;
    }

    /// <summary>Gets the logical element count.</summary>
    public int Length => _allocation is null
        ? _length
        : _length >= 0
            ? _length
            : _allocation.Length;

    /// <summary>Gets the physical capacity in elements.</summary>
    public int Capacity => _allocation is null
        ? _length
        : _length >= 0
            ? _length
            : _allocation.Capacity;

    /// <summary>Gets a direct span for storage without managed references.</summary>
    public Span<T> AsSpan() => _allocation is null
        ? GetDirectSpan()
        : _allocation.AsSpan<T>()[..Length];

    /// <summary>Reads or writes one value in the logical range.</summary>
    public T this[int index]
    {
        get
        {
            ValidateIndex(index);
            return _allocation is null
                ? GetDirectSpan()[index]
                : _allocation.GetValue<T>(index);
        }
        set
        {
            ValidateIndex(index);
            if (_allocation is null)
            {
                GetDirectSpan()[index] = value;
                return;
            }

            _allocation.SetValue(index, value);
        }
    }

    /// <summary>Clears the logical range.</summary>
    public void Clear()
    {
        if (_allocation is null)
        {
            GetDirectSpan().Clear();
            return;
        }

        if (_length < 0)
        {
            _allocation.ClearValues();
            return;
        }

        if (_allocation.ReferenceRoots is null)
        {
            _allocation.AsSpan<T>()[.._length].Clear();
            return;
        }

        for (int index = 0; index < _length; index++)
        {
            _allocation.SetValue(index, default(T)!);
        }
    }

    /// <summary>Writes one value to the logical range.</summary>
    public void Fill(T value)
    {
        if (_allocation is null)
        {
            GetDirectSpan().Fill(value);
            return;
        }

        if (_length < 0)
        {
            _allocation.Fill(value);
            return;
        }

        if (_allocation.ReferenceRoots is null)
        {
            _allocation.AsSpan<T>()[.._length].Fill(value);
            return;
        }

        for (int index = 0; index < _length; index++)
        {
            _allocation.SetValue(index, value);
        }
    }

    /// <summary>Copies an exact source range into this view.</summary>
    public void CopyFrom(scoped ReadOnlySpan<T> source)
    {
        if (source.Length != Length)
        {
            throw new ArgumentException(
                "The source length must equal the lease logical length.",
                nameof(source));
        }

        if (_allocation is null)
        {
            source.CopyTo(GetDirectSpan());
            return;
        }

        _allocation.CopyFrom(source);
    }

    /// <summary>Copies this view into a sufficient destination range.</summary>
    public void CopyTo(scoped Span<T> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException(
                "The destination must contain the complete lease range.",
                nameof(destination));
        }

        if (_allocation is null)
        {
            GetDirectSpan().CopyTo(destination);
            return;
        }

        if (_length < 0)
        {
            _allocation.CopyTo(destination);
            return;
        }

        if (_allocation.ReferenceRoots is null)
        {
            _allocation.AsSpan<T>()[.._length].CopyTo(destination);
            return;
        }

        for (int index = 0; index < _length; index++)
        {
            destination[index] = _allocation.GetValue<T>(index);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe Span<T> GetDirectSpan() =>
        new((void*)_directPointer, _length);

    private void ValidateIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                "The index is outside the logical lease range.");
        }
    }
}
