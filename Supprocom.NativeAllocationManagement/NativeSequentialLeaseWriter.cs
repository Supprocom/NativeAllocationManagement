using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Writes one checked contiguous lease range in sequence.</summary>
/// <typeparam name="T">The element type in the range.</typeparam>
public ref struct NativeSequentialLeaseWriter<T>
{
    private readonly NativeAllocation _allocation;
    private readonly Span<T> _directValues;
    private readonly ref int _initializedLength;
    private readonly int _start;
    private int _position;
    private bool _completed;

    internal NativeSequentialLeaseWriter(
        NativeAllocation allocation,
        Span<T> directValues,
        ref int initializedLength,
        int start,
        int length)
    {
        _allocation = allocation;
        _directValues = directValues;
        _initializedLength = ref initializedLength;
        _start = start;
        _position = 0;
        _completed = false;
        Length = length;
    }

    /// <summary>Gets the element count in this range.</summary>
    public int Length { get; }

    /// <summary>Gets the number of elements that have values.</summary>
    public int WrittenCount => _position;

    /// <summary>Reads one element that this range already wrote.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T ReadWritten(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= _position)
        {
            throw new InvalidOperationException(
                "The sequential native writer cannot read an unwritten element.");
        }

        int source = _start + index;
        return !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            ? _directValues[source]
            : _allocation.GetValue<T>(source);
    }

    /// <summary>Writes the next element.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(T value)
    {
        int position = _position;
        if ((uint)position >= (uint)Length)
        {
            throw new InvalidOperationException(
                "The sequential native writer exceeded its range.");
        }

        int destination = _start + position;
        WriteValue(destination, value);
        _position = position + 1;
    }

    /// <summary>Writes the next contiguous source range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(scoped ReadOnlySpan<T> source)
    {
        int position = _position;
        if (source.Length > Length - position)
        {
            throw new ArgumentException(
                "The source exceeds the sequential native range.",
                nameof(source));
        }

        int destination = _start + position;
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            source.CopyTo(_directValues[destination..]);
            _position = position + source.Length;
            return;
        }

        for (int index = 0; index < source.Length; index++)
        {
            WriteValue(
                destination + index,
                source[index]);
            _position = position + index + 1;
        }
    }

    /// <summary>Writes one value to the remaining range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Fill(T value)
    {
        Fill(Length - _position, value);
    }

    /// <summary>Writes one value to the next specified element count.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Fill(int length, T value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        int position = _position;
        if (length > Length - position)
        {
            throw new ArgumentException(
                "The fill exceeds the sequential native range.",
                nameof(length));
        }

        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            _directValues.Slice(
                _start + position,
                length).Fill(value);
            _position = position + length;
            return;
        }

        int end = position + length;
        while (_position < end)
        {
            Write(value);
        }
    }

    /// <summary>Completes this range after all elements have values.</summary>
    public void Complete()
    {
        if (_completed)
        {
            throw new InvalidOperationException(
                "The sequential native writer is already complete.");
        }

        if (_position != Length)
        {
            throw new InvalidOperationException(
                "The sequential native writer did not write its complete range.");
        }

        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            _initializedLength = Math.Max(
                _initializedLength,
                _start + Length);
        }

        _completed = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteValue(int destination, T value)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            _directValues[destination] = value;
            return;
        }

        if (destination < _initializedLength)
        {
            _allocation.SetValue(destination, value);
            return;
        }

        _allocation.SetInitialValue(destination, value);
        _initializedLength = destination + 1;
    }
}
