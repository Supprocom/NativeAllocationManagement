using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

// More of a thread's disposed default-policy owners of one kind, kept for the
// next owners its thread creates, newest first, behind the kind's single
// reuse slot. Nested scopes each reuse one: with only the slot the outer
// scope's storage was freed and allocated again on every pass (two nested
// regions per pass went from 18 to 730 ns). A single scope still uses only
// the slot. Bounded by the bytes the kept owners retain, and in count only
// loosely: recursion with a region per level stayed at 21-30 ns per region to
// seventeen levels and then rose to 84 ns at eighteen and 465 ns at
// sixty-four while sixteen owners was the limit.
//
// Entries are structs: a store into a reference-type array of a shared
// generic takes a covariance-check helper call, which cost more than the
// reuse saved.
internal sealed class NativeReuseStack<T>
    where T : class
{
    internal const int MaxCount = 256;
    internal const long LimitBytes = 512 * 1024;

    private Entry[] _entries = new Entry[16];
    private int _count;
    private long _retainedBytes;

    internal bool IsEmpty => _count == 0;

    // Whether an owner retaining this many bytes can be kept.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool HasRoom(long bytes) =>
        _count != MaxCount && bytes <= LimitBytes - _retainedBytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Push(T item, long bytes)
    {
        int count = _count;
        Entry[] entries = _entries;
        if ((uint)count >= (uint)entries.Length)
        {
            entries = Grow();
        }

        ref Entry entry = ref entries[count];
        entry.Item = item;
        entry.Bytes = bytes;
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
        ref Entry entry = ref _entries[count];
        T? item = entry.Item;
        entry.Item = null;
        _retainedBytes -= entry.Bytes;
        _count = count;
        return item;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Entry[] Grow()
    {
        Array.Resize(ref _entries, _entries.Length * 2);
        return _entries;
    }

    private struct Entry
    {
        internal T? Item;
        internal long Bytes;
    }
}
