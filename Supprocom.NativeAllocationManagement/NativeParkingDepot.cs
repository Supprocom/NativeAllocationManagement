namespace Supprocom.NativeAllocationManagement;

// Parked objects travelling between threads a stack at a time: a thread whose
// stack is full hands all of it over as one magazine, and a thread whose stack
// is empty takes one back, so a producer-consumer pipeline pays one lock per
// stack instead of one per object. Callers hand over objects that are already
// parked: nothing in them changes after they enter a magazine.
internal sealed class NativeParkingDepot<TItem>
    where TItem : class
{
    private readonly Lock _gate = new();
    private readonly TItem?[]?[] _full;
    private readonly int[] _fullCounts;
    private readonly TItem?[]?[] _empty;
    private readonly int _magazineSize;
    private int _fullCount;
    private int _emptyCount;
    private int _epoch = NativeStorageReuse.SharedEpoch;

    internal NativeParkingDepot(int magazineSize, int magazineLimit)
    {
        _magazineSize = magazineSize;
        _full = new TItem?[]?[magazineLimit];
        _fullCounts = new int[magazineLimit];
        _empty = new TItem?[]?[magazineLimit];
    }

    internal bool HasFull => Volatile.Read(ref _fullCount) != 0;

    // Moves the first count items of a full stack into the depot and clears them.
    internal bool TrySpill(TItem?[] stack, int count)
    {
        if (count == 0 || count > _magazineSize || Volatile.Read(ref _fullCount) == _full.Length)
        {
            return false;
        }

        lock (_gate)
        {
            DiscardIfStale();
            if (_fullCount == _full.Length)
            {
                return false;
            }

            TItem?[] magazine = _emptyCount != 0 ? _empty[--_emptyCount]! : new TItem?[_magazineSize];
            _empty[_emptyCount] = null;
            Array.Copy(stack, magazine, count);
            Array.Clear(stack, 0, count);
            _full[_fullCount] = magazine;
            _fullCounts[_fullCount] = count;
            Volatile.Write(ref _fullCount, _fullCount + 1);
            return true;
        }
    }

    // Fills an empty stack from the depot; returns how many items it holds now.
    internal int TryRefill(TItem?[] stack)
    {
        lock (_gate)
        {
            DiscardIfStale();
            if (_fullCount == 0)
            {
                return 0;
            }

            int last = _fullCount - 1;
            TItem?[] magazine = _full[last]!;
            int count = _fullCounts[last];
            _full[last] = null;
            Volatile.Write(ref _fullCount, last);
            Array.Copy(magazine, stack, count);
            Array.Clear(magazine, 0, count);
            if (_emptyCount != _empty.Length)
            {
                _empty[_emptyCount++] = magazine;
            }

            return count;
        }
    }

    // Tests advance the shared epoch so each starts with an empty depot.
    private void DiscardIfStale()
    {
        int epoch = NativeStorageReuse.SharedEpoch;
        if (_epoch == epoch)
        {
            return;
        }

        Array.Clear(_full);
        _epoch = epoch;
        Volatile.Write(ref _fullCount, 0);
    }
}
