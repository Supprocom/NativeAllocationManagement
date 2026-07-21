using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

internal static class NativeTypeLayout
{
    internal static bool ContainsReferences<T>() => RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    internal static int StorageSize<T>() => ContainsReferences<T>() ? IntPtr.Size : Unsafe.SizeOf<T>();

    internal static nuint Alignment<T>()
    {
        if (ContainsReferences<T>())
        {
            return (nuint)IntPtr.Size;
        }

        int size = Unsafe.SizeOf<T>();
        nuint alignment = 1;
        nuint limit = (nuint)Math.Min(size, IntPtr.Size);
        while (alignment <= limit / 2)
        {
            alignment *= 2;
        }

        return alignment;
    }
}
