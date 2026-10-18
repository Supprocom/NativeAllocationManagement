using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

// Observations never grant allocation/lifecycle authority. Current storage
// gauges stay exact; only completed event/byte histories use saturation.
internal static class NativeOwnerHistory
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Increment(ref long counter, ref bool overflowed)
    {
        if (counter == long.MaxValue)
        {
            overflowed = true;
        }
        else
        {
            counter++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Add(ref long counter, long delta, ref bool overflowed)
    {
        // Completed histories and their deltas are nonnegative. Their sum
        // crosses the sign bit exactly when it exceeds the signed maximum.
        long next = unchecked(counter + delta);
        if (next < 0)
        {
            counter = long.MaxValue;
            overflowed = true;
        }
        else
        {
            counter = next;
        }
    }
}
