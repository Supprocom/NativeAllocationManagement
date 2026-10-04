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
        if (delta > long.MaxValue - counter)
        {
            counter = long.MaxValue;
            overflowed = true;
        }
        else
        {
            counter += delta;
        }
    }
}
