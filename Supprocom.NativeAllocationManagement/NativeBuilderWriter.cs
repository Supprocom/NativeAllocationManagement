using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Writes one bounded builder range before its prefix becomes visible.</summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
public readonly ref struct NativeBuilderWriter<T>
    where T : unmanaged
{
    private readonly Span<T> _values;
    private readonly ref int _committedCount;

    internal NativeBuilderWriter(
        Span<T> values,
        ref int committedCount)
    {
        _values = values;
        _committedCount = ref committedCount;
    }

    /// <summary>Gets the maximum element count for this write.</summary>
    public int Length => _values.Length;

    /// <summary>Gets the bounded writable native range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> AsSpan() => _values;

    /// <summary>Commits one initialized prefix of the bounded range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Commit(int actualCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(actualCount);
        if (actualCount > _values.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(actualCount),
                "The committed count exceeds the bounded builder range.");
        }

        if (_committedCount >= 0)
        {
            throw new InvalidOperationException(
                "The bounded builder write is already committed.");
        }

        _committedCount = actualCount;
    }
}

/// <summary>Writes and commits one bounded builder range.</summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
public delegate void NativeBuilderWriteAction<T>(
    scoped NativeBuilderWriter<T> writer)
    where T : unmanaged;
