using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Provides checked sequential access during atomic lease initialization.</summary>
/// <typeparam name="T">The element type in the new lease.</typeparam>
public readonly ref struct NativeLeaseWriter<T>
{
    private readonly NativeAllocation _allocation;
    private readonly Span<T> _directValues;
    private readonly ref int _initializedLength;

    internal NativeLeaseWriter(
        NativeAllocation allocation,
        ref int initializedLength)
    {
        _allocation = allocation;
        _initializedLength = ref initializedLength;
        _directValues = !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            ? allocation.AsSpan<T>()
            : default;
    }

    /// <summary>Gets the number of elements that the initializer must write.</summary>
    public int Length => _allocation.Length;

    /// <summary>Gets the number of elements that do not have a value.</summary>
    public int Remaining => Length - _initializedLength;

    /// <summary>Writes the next element in the logical range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(T value)
    {
        int index = _initializedLength;
        if ((uint)index >= (uint)Length)
        {
            throw new InvalidOperationException(
                "The native lease initializer wrote more elements than the lease contains.");
        }

        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            _directValues[index] = value;
        }
        else
        {
            _allocation.SetInitialValue(index, value);
        }

        _initializedLength = index + 1;
    }

    /// <summary>Reads an element that this initializer already wrote.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T ReadInitialized(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= _initializedLength)
        {
            throw new InvalidOperationException(
                "The native lease initializer cannot read an element before it writes that element.");
        }

        return !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            ? _directValues[index]
            : _allocation.GetValue<T>(index);
    }

    /// <summary>Starts one sequential writer for a contiguous lease range.</summary>
    public NativeSequentialLeaseWriter<T> BeginSequentialRange(
        int start,
        int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start > Length - length)
        {
            throw new ArgumentException(
                "The sequential range is outside the native lease.",
                nameof(length));
        }

        if (start > _initializedLength)
        {
            throw new InvalidOperationException(
                "The native lease initializer cannot leave an uninitialized gap.");
        }

        return new NativeSequentialLeaseWriter<T>(
            _allocation,
            _directValues,
            ref _initializedLength,
            start,
            length);
    }

    /// <summary>Reads one contiguous range from the initialized value prefix.</summary>
    public ReadOnlySpan<T> ReadInitializedSpan(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start > _initializedLength - length)
        {
            throw new InvalidOperationException(
                "The native lease initializer cannot read beyond its initialized prefix.");
        }

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            throw new NotSupportedException(
                "A direct initialized span is available only for value storage without managed references.");
        }

        return _directValues.Slice(start, length);
    }

    /// <summary>Gets a writable span for values that this initializer already wrote.</summary>
    public Span<T> RewriteInitializedSpan(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start > _initializedLength - length)
        {
            throw new InvalidOperationException(
                "The native lease initializer cannot rewrite an uninitialized value.");
        }

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            throw new NotSupportedException(
                "A writable initialized span requires direct value storage.");
        }

        return _directValues.Slice(start, length);
    }

    /// <summary>Writes the next element or replaces an element that is already initialized.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteAt(int index, T value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        int initializedLength = _initializedLength;
        if (index > initializedLength || index >= Length)
        {
            throw new InvalidOperationException(
                "The native lease initializer must write elements in order before it replaces them.");
        }

        if (index == initializedLength)
        {
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                _directValues[index] = value;
            }
            else
            {
                _allocation.SetInitialValue(index, value);
            }

            _initializedLength = index + 1;
            return;
        }

        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            _directValues[index] = value;
        }
        else
        {
            _allocation.SetValue(index, value);
        }
    }

    /// <summary>Writes one contiguous range without leaving an uninitialized gap.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteRangeAt(int start, scoped ReadOnlySpan<T> source)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (start > Length - source.Length)
        {
            throw new ArgumentException(
                "The source is outside the native lease.",
                nameof(source));
        }

        int initializedLength = _initializedLength;
        if (start > initializedLength)
        {
            throw new InvalidOperationException(
                "The native lease initializer cannot leave an uninitialized gap.");
        }

        int end = start + source.Length;
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            source.CopyTo(_directValues[start..]);
            _initializedLength = Math.Max(
                initializedLength,
                end);
            return;
        }

        for (int index = 0; index < source.Length; index++)
        {
            int destinationIndex = start + index;
            if (destinationIndex < initializedLength)
            {
                _allocation.SetValue(
                    destinationIndex,
                    source[index]);
                continue;
            }

            _allocation.SetInitialValue(
                destinationIndex,
                source[index]);
            _initializedLength = destinationIndex + 1;
        }
    }

    /// <summary>Writes the next contiguous values in the logical range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(scoped ReadOnlySpan<T> source)
    {
        int start = _initializedLength;
        if (source.Length > Length - start)
        {
            throw new ArgumentException(
                "The source does not fit in the remaining native lease range.",
                nameof(source));
        }

        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            source.CopyTo(_directValues[start..]);
            _initializedLength = start + source.Length;
            return;
        }

        for (int index = 0; index < source.Length; index++)
        {
            _allocation.SetInitialValue(
                start + index,
                source[index]);
            _initializedLength = start + index + 1;
        }
    }

    /// <summary>Writes one value to all remaining elements.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Fill(T value)
    {
        int start = _initializedLength;
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            _directValues[start..].Fill(value);
            _initializedLength = Length;
            return;
        }

        for (int index = start; index < Length; index++)
        {
            _allocation.SetInitialValue(index, value);
            _initializedLength = index + 1;
        }
    }

    /// <summary>Fills a range without creating a gap in the initialized prefix.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Fill(int start, int length, T value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start > Length - length)
        {
            throw new ArgumentException(
                "The fill range is outside the native lease.",
                nameof(length));
        }

        int initializedLength = _initializedLength;
        if (start > initializedLength)
        {
            throw new InvalidOperationException(
                "The native lease initializer cannot leave an uninitialized gap.");
        }

        int end = start + length;
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            _directValues.Slice(start, length).Fill(value);
            _initializedLength = Math.Max(
                initializedLength,
                end);
            return;
        }

        for (int index = start; index < end; index++)
        {
            if (index < initializedLength)
            {
                _allocation.SetValue(index, value);
                continue;
            }

            _allocation.SetInitialValue(index, value);
            _initializedLength = index + 1;
        }
    }
}
