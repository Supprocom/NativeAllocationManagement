using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

// A generation's idle pool slabs, sorted by capacity and then allocation
// ordinal, both descending. A rent takes the smallest slab that holds its
// length and the oldest of those; here that slab ends the run of slabs that
// hold the length, found by binary search, and is usually the last entry.
// The ordinal-ordered list this replaces took a linear scan for every rent
// the thread caches did not serve and an ordered insert for every return
// they spilled: with 1,024 leases outstanding on one thread a rent and its
// return cost 926 ns, with 4,096 4.7 us. Capacity is reserved ahead the way
// a List's is, so a commit that reserved it adds without allocating.
internal sealed class NativeAvailableSlabs
{
    private Entry[] _entries = [];
    private int _count;

    internal int Count => _count;

    internal int Capacity => _entries.Length;

    internal NativeSlab this[int index] =>
        (uint)index < (uint)_count
            ? _entries[index].Slab
            : throw new ArgumentOutOfRangeException(nameof(index));

    internal void EnsureCapacity(int capacity)
    {
        if (_entries.Length < capacity)
        {
            Grow(capacity);
        }
    }

    internal void Add(NativeSlab slab)
    {
        if (_count == _entries.Length)
        {
            Grow(_count + 1);
        }

        int index = InsertionIndex(slab.Capacity, slab.AllocationOrdinal);
        Array.Copy(_entries, index, _entries, index + 1, _count - index);
        _entries[index] = new Entry(slab);
        _count++;
    }

    // Whether any slab holds this many elements: the largest comes first.
    internal bool HasCapacity(int length) => _count != 0 && _entries[0].Capacity >= length;

    // The smallest slab that holds the length, the oldest of those, or null.
    internal NativeSlab? TakeSmallest(int length)
    {
        int holding = CountHolding(length);
        if (holding == 0)
        {
            return null;
        }

        NativeSlab slab = _entries[holding - 1].Slab;
        RemoveAt(holding - 1);
        return slab;
    }

    internal bool Remove(NativeSlab slab)
    {
        // The slab, if here, is in the run of entries with its key, which
        // starts at the first entry not ordered before that key.
        int capacity = slab.Capacity;
        long ordinal = slab.AllocationOrdinal;
        for (int index = InsertionIndex(capacity, ordinal);
            index < _count && _entries[index].Capacity == capacity && _entries[index].Ordinal == ordinal;
            index++)
        {
            if (ReferenceEquals(_entries[index].Slab, slab))
            {
                RemoveAt(index);
                return true;
            }
        }

        return false;
    }

    internal void RemoveAt(int index)
    {
        if ((uint)index >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _count--;
        Array.Copy(_entries, index + 1, _entries, index, _count - index);
        _entries[_count] = default;
    }

    internal void Clear()
    {
        Array.Clear(_entries, 0, _count);
        _count = 0;
    }

    // Entries before the result order before (capacity, ordinal): a larger
    // capacity, or the same with a larger ordinal.
    private int InsertionIndex(int capacity, long ordinal)
    {
        int low = 0;
        int high = _count;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            ref Entry entry = ref _entries[middle];
            if (entry.Capacity > capacity || (entry.Capacity == capacity && entry.Ordinal > ordinal))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    // How many slabs, from the first, hold the length.
    private int CountHolding(int length)
    {
        int low = 0;
        int high = _count;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            if (_entries[middle].Capacity >= length)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    // Grows as List<T> does: double, at least four, at least the request.
    private void Grow(int capacity)
    {
        int next = _entries.Length == 0 ? 4 : 2 * _entries.Length;
        if ((uint)next > (uint)Array.MaxLength)
        {
            next = Array.MaxLength;
        }

        Array.Resize(ref _entries, Math.Max(next, capacity));
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly struct Entry(NativeSlab slab)
    {
        internal readonly NativeSlab Slab = slab;
        internal readonly long Ordinal = slab.AllocationOrdinal;
        internal readonly int Capacity = slab.Capacity;
    }
}
