namespace Supprocom.NativeAllocationManagement;

/// <summary>A callback-bounded immutable view; it grants no owning or writable authority.</summary>
/// <typeparam name="T">The initialized unmanaged element type.</typeparam>
public readonly ref struct NativeReadOnlyLeaseView<T>
    where T : unmanaged
{
    private readonly ReadOnlySpan<T> _values;

    internal NativeReadOnlyLeaseView(ReadOnlySpan<T> values) => _values = values;

    /// <summary>Gets the logical element count of this bounded range.</summary>
    public int Length => _values.Length;

    /// <summary>Reads one value with the range's normal bounds check.</summary>
    public T this[int index] => _values[index];

    /// <summary>Gets a read-only span valid only within the entered callback.</summary>
    public ReadOnlySpan<T> AsSpan() => _values;

    /// <summary>Copies this range into caller-provided bounded storage.</summary>
    public void CopyTo(scoped Span<T> destination)
    {
        _values.CopyTo(destination);
        NativeMemoryAccounting.RecordCopiedRange(_values, destination);
    }
}

/// <summary>Consumes immutable native data within one entered bounded read.</summary>
public delegate void NativeReadOnlyLeaseAction<T>(scoped NativeReadOnlyLeaseView<T> view)
    where T : unmanaged;

/// <summary>Computes a non-ref-struct result from one immutable bounded native read.</summary>
public delegate TResult NativeReadOnlyLeaseFunc<T, out TResult>(scoped NativeReadOnlyLeaseView<T> view)
    where T : unmanaged;
