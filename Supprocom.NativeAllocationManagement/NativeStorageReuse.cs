namespace Supprocom.NativeAllocationManagement;

// Reuse slots (parked builder controls, the default region kernel) record an
// epoch when they park; a slot from an older epoch is discarded instead of
// reused. Per-thread slots use the thread's epoch, slots shared between threads
// the process-wide one. Tests advance both so each test starts with empty slots.
internal static class NativeStorageReuse
{
    [ThreadStatic]
    private static int t_epoch;

    private static int s_sharedEpoch;

    internal static int CurrentEpoch => t_epoch;

    internal static int SharedEpoch => Volatile.Read(ref s_sharedEpoch);

    internal static void InvalidateCurrentThreadForTest()
    {
        t_epoch++;
        Interlocked.Increment(ref s_sharedEpoch);
    }
}
