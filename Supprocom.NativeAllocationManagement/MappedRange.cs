using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

internal static class MappedRange
{
    internal static int SizeOf<T>()
        where T : unmanaged =>
        Unsafe.SizeOf<T>();

    // Natural alignment: the largest power of two dividing the size, capped at 8.
    internal static int AlignmentOf(int size) => Math.Min(size & -size, 8);

    internal static void CheckView<T>(long byteOffset, long count, long length)
        where T : unmanaged
    {
        int size = SizeOf<T>();
        ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (byteOffset > length || count > (length - byteOffset) / size)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                $"The range does not fit the {length}-byte file.");
        }

        if (byteOffset % AlignmentOf(size) != 0)
        {
            throw new ArgumentException(
                $"The byte offset must be a multiple of {AlignmentOf(size)}.",
                nameof(byteOffset));
        }
    }

    // A window is a start and an int length inside a 64-bit element range.
    internal static void CheckWindow(long start, int length, long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start > count || length > count - start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                $"The window does not fit the {count}-element view.");
        }
    }

    internal static int RestOf(long start, long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (start > count)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        long rest = count - start;
        if (rest > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start),
                $"The {rest} remaining elements do not fit one window; pass a length.");
        }

        return (int)rest;
    }
}
