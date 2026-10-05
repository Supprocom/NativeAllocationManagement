namespace Supprocom.NativeAllocationManagement;

internal static class NativeOwnerIdentity
{
    private static long _nextId;

    // One construction-time number, never a registry or an address. The
    // checked sequence cannot wrap and turn an old identity into a new owner.
    internal static long Next()
    {
        NativeMemoryAccounting.PrepareThread();
        return NextWithoutPreparation();
    }

    internal static long NextWithoutPreparation()
    {
        while (true)
        {
            long current = Volatile.Read(ref _nextId);
            long next = checked(current + 1);
            if (Interlocked.CompareExchange(ref _nextId, next, current) == current)
            {
                return next;
            }
        }
    }
}
