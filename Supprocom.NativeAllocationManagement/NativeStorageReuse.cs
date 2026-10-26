namespace Supprocom.NativeAllocationManagement;

// Per-thread reuse slots (parked builder controls, the default region kernel)
// record this epoch when they park; a slot from an older epoch is discarded
// instead of reused. Tests advance it so each test starts with empty slots.
internal static class NativeStorageReuse
{
    [ThreadStatic]
    private static int t_epoch;

    internal static int CurrentEpoch => t_epoch;

    internal static void InvalidateCurrentThreadForTest() => t_epoch++;
}
