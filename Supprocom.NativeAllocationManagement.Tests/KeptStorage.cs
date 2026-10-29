namespace Supprocom.NativeAllocationManagement.Tests;

// Storage NAM keeps for reuse stays counted as outstanding, and no free is
// recorded for it until NAM frees it: parked builder and transfer blocks and
// the large-block cache (NativeBuilderControl<T>.KeptInCurrentEpochForTest).
// Call these once the test's threads are done.
internal static class KeptStorage
{
    internal static (int Count, long Bytes) InCurrentEpoch<T>()
        where T : unmanaged =>
        NativeBuilderControl<T>.KeptInCurrentEpochForTest();

    // Every allocation of the epoch was freed or is kept for reuse, and the
    // kept storage is all that stays outstanding.
    internal static void AssertBalanced<T>(NativeMemoryTestMetrics metrics)
        where T : unmanaged =>
        AssertBalanced<T>(metrics.AllocationCount, metrics.FreeCount, metrics.OutstandingNativeBytes);

    internal static void AssertBalanced<T>(NativeMemoryStatistics statistics)
        where T : unmanaged =>
        AssertBalanced<T>(statistics.AllocationCount, statistics.FreeCount, statistics.OutstandingNativeBytes);

    private static void AssertBalanced<T>(long allocations, long frees, long outstanding)
        where T : unmanaged
    {
        (int kept, long keptBytes) = InCurrentEpoch<T>();
        Assert.Equal(allocations - kept, frees);
        Assert.Equal(keptBytes, outstanding);
    }
}
