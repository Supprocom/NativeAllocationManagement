using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

internal static class NativeOwnerIdentity
{
    private const long BlockSize = 1024;
    private static long _claimedIds;

    // Each thread claims a block of identities with one atomic add and hands
    // them out without touching shared state, so owner construction on many
    // threads does not contend on one cache line.
    //
    // One construction-time number, never a registry or an address. Numbers
    // increase within a thread; the checked claim cannot wrap and turn an old
    // identity into a new owner.
    internal static long Next()
    {
        NativeMemoryAccounting.PrepareThread();
        return Next(NativeThreadState.Current);
    }

    internal static long NextWithoutPreparation() => Next(NativeThreadState.Current);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Next(NativeThreadState thread)
    {
        long next = thread.NextOwnerId;
        if (next != thread.OwnerIdEnd)
        {
            thread.NextOwnerId = next + 1;
            return next;
        }

        return ClaimBlock(thread);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long ClaimBlock(NativeThreadState thread)
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
        thread.NextOwnerId = first + 1;
        thread.OwnerIdEnd = end + 1;
        return first;
    }
}
