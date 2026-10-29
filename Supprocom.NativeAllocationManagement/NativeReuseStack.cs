using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

// More of a thread's disposed default-policy owners of one kind, kept for the
// next owners its thread creates, newest first, behind the kind's single
// reuse slot. Nested scopes each reuse one: with only the slot the outer
// scope's storage was freed and allocated again on every pass (two nested
// regions per pass went from 18 to 730 ns). A single scope still uses only
// the slot. Bounded in count and in the bytes the kept owners retain.
//
// Inline arrays: a store into a reference-type array of a shared generic
// takes a covariance-check helper call, which cost more than the reuse saved.
internal sealed class NativeReuseStack<T>
    where T : class
{
    private const int Capacity = 16;
    internal const long LimitBytes = 512 * 1024;

    private Items _items;
    private Lengths _bytes;
    private int _count;
    private long _retainedBytes;

    internal bool IsEmpty => _count == 0;

    // Whether an owner retaining this many bytes can be kept.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool HasRoom(long bytes) =>
        _count != Capacity && bytes <= LimitBytes - _retainedBytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Push(T item, long bytes)
    {
        int count = _count;
        _items[count] = item;
        _bytes[count] = bytes;
        _retainedBytes += bytes;
        _count = count + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T? Pop()
    {
        int count = _count;
        if (count == 0)
        {
            return null;
        }

        count--;
        T? item = _items[count];
        _items[count] = null;
        _retainedBytes -= _bytes[count];
        _count = count;
        return item;
    }

    [InlineArray(Capacity)]
    private struct Items
    {
        private T? _element;
    }

    [InlineArray(Capacity)]
    private struct Lengths
    {
        private long _element;
    }
}
