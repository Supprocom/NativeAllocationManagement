namespace Supprocom.NativeAllocationManagement;

/// <summary>
/// A scoped, bounded view supplied only while a native lease operation is executing.
/// </summary>
/// <typeparam name="T">The value type or reference type stored by the owner.</typeparam>
public readonly ref struct NativeLeaseView<T>
{
    private readonly NativeAllocation _allocation;

    internal NativeLeaseView(NativeAllocation allocation)
    {
        _allocation = allocation;
    }

    /// <summary>Gets the logical element count.</summary>
    public int Length => _allocation.Length;

    /// <summary>Gets the physical capacity represented by this view.</summary>
    public int Capacity => _allocation.Capacity;

    /// <summary>Reads or writes one element inside the bounded logical range.</summary>
    public T this[int index]
    {
        get
        {
            ValidateIndex(index);
            return _allocation.GetValue<T>(index);
        }
        set
        {
            ValidateIndex(index);
            _allocation.SetValue(index, value);
        }
    }

    /// <summary>Clears the logical view.</summary>
    public void Clear()
    {
        _allocation.ClearValues();
    }

    /// <summary>Fills the logical view with one value.</summary>
    public void Fill(T value)
    {
        _allocation.Fill(value);
    }

    /// <summary>Copies an exactly sized source into the view.</summary>
    public void CopyFrom(scoped ReadOnlySpan<T> source)
    {
        if (source.Length != Length)
        {
            throw new ArgumentException("The source length must equal the lease logical length.", nameof(source));
        }

        _allocation.CopyFrom(source);
    }

    /// <summary>Copies the logical view into a destination with sufficient capacity.</summary>
    public void CopyTo(scoped Span<T> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException("The destination must contain at least the lease logical length.", nameof(destination));
        }

        _allocation.CopyTo(destination);
    }

    private void ValidateIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The index is outside the logical lease range.");
        }
    }
}
