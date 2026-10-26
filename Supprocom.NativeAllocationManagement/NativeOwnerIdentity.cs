namespace Supprocom.NativeAllocationManagement;

internal static class NativeOwnerIdentity
{
    private const long BlockSize = 1024;
    private static long _claimedIds;

    // Each thread claims a block of identities with one atomic add and hands
    // them out without touching shared state, so owner construction on many
    // threads does not contend on one cache line.
    [ThreadStatic]
    private static long t_next;

    [ThreadStatic]
    private static long t_end;

    // One construction-time number, never a registry or an address. Numbers
    // increase within a thread; the checked claim cannot wrap and turn an old
    // identity into a new owner.
    internal static long Next()
    {
        NativeMemoryAccounting.PrepareThread();
        return NextWithoutPreparation();
    }

    internal static long NextWithoutPreparation()
    {
        long next = t_next;
        if (next != t_end)
        {
            t_next = next + 1;
            return next;
        }

        return ClaimBlock();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long ClaimBlock()
    {
        long end;
        while (true)
        {
            long current = Volatile.Read(ref _claimedIds);
            end = checked(current + BlockSize);
            if (Interlocked.CompareExchange(ref _claimedIds, end, current) == current)
            {
                break;
            }
        }

        long first = end - BlockSize + 1;
        t_next = first + 1;
        t_end = end + 1;
        return first;
    }
}
