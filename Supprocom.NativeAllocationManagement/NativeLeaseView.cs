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
        _length = 0;
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
    public int Length => _allocation?.Length ?? _length;

    /// <summary>Gets the physical capacity in elements.</summary>
    public int Capacity => _allocation?.Capacity ?? _length;

    /// <summary>Gets a direct span for storage without managed references.</summary>
    public Span<T> AsSpan() => _allocation is null
        ? GetDirectSpan()
        : _allocation.AsSpan<T>();

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

        _allocation.ClearValues();
    }

    /// <summary>Writes one value to the logical range.</summary>
    public void Fill(T value)
    {
        if (_allocation is null)
        {
            GetDirectSpan().Fill(value);
            return;
        }

        _allocation.Fill(value);
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

        _allocation.CopyTo(destination);
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
