using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

// Caller serializes explicit ownership transitions. No heap wrapper per binding.
internal sealed class NativeSharingBindingBank
{
    // No slot can ever be acquired or released from this immutable empty bank.
    internal static NativeSharingBindingBank Empty { get; } = new(0);
    [StructLayout(LayoutKind.Sequential)]
    private struct Slot
    {
        internal long Version;
        internal int Next;
        internal bool Live;
    }

    private readonly Slot[] _slots;
    private int _firstAvailable;
    internal int Occupied { get; private set; }
    internal int Capacity => _slots.Length;
    internal long ElementBytes => (long)_slots.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<Slot>();

    internal NativeSharingBindingBank(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _slots = capacity == 0 ? [] : new Slot[capacity];
        for (int index = 0; index < capacity; index++)
        {
            _slots[index].Next = index + 1 < capacity ? index + 1 : -1;
        }
        _firstAvailable = capacity == 0 ? -1 : 0;
    }

    internal bool TryAcquire(out int index, out long version)
    {
        index = _firstAvailable;
        if (index < 0)
        {
            version = 0;
            return false;
        }
        ref Slot slot = ref _slots[index];
        // Reserve a non-reusable identity before changing the free list or count.
        version = checked(slot.Version + 1);
        _firstAvailable = slot.Next;
        slot.Version = version;
        slot.Next = -1;
        slot.Live = true;
        Occupied++;
        return true;
    }

    internal bool IsActive(int index, long version) => (uint)index < (uint)_slots.Length
        && version > 0 && _slots[index].Live && _slots[index].Version == version;

    internal void Release(int index, long version)
    {
        if (!IsActive(index, version))
        {
            throw new InvalidOperationException("This ownership binding is stale or already released.");
        }
        ref Slot slot = ref _slots[index];
        slot.Live = false;
        slot.Next = _firstAvailable;
        _firstAvailable = index;
        Occupied--;
    }

    internal void ExpireAll()
    {
        foreach (ref Slot slot in _slots.AsSpan())
        {
            slot.Live = false;
        }
        Occupied = 0;
        _firstAvailable = -1;
    }
}
