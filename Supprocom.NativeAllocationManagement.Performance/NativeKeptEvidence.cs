namespace Supprocom.NativeAllocationManagement.Performance;

// Storage NAM keeps for reuse (parked builder and transfer blocks, the
// large-block cache) stays counted as outstanding, and no free is recorded
// for it until NAM frees it. Read these once the measured threads are
// quiescent.
internal static class NativeKeptEvidence
{
    internal static (int Count, long Bytes) InCurrentEpoch<T>()
        where T : unmanaged =>
        NativeBuilderControl<T>.KeptInCurrentEpochForTest();

    // Every allocation of the epoch was freed or is kept for reuse, and the
    // kept storage is all that stays outstanding.
    internal static bool IsBalanced<T>(NativeMemoryTestMetrics metrics)
        where T : unmanaged
    {
        (int count, long bytes) = InCurrentEpoch<T>();
        return metrics.AllocationCount - count == metrics.FreeCount
            && metrics.OutstandingNativeBytes == bytes;
    }
}
