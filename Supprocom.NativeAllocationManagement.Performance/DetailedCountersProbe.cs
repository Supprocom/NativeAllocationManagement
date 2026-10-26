using System.Globalization;

namespace Supprocom.NativeAllocationManagement.Performance;

// Reports which counters a fresh process records with its startup switch state.
internal static class DetailedCountersProbe
{
    internal static int Run()
    {
        long liveOutstanding;
        using (NativeBuilder<int> builder = new(preLease: 64))
        {
            builder.Append(1);
            liveOutstanding = NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes;
        }

        using (NativePool<int> pool = new(preLease: 8, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory))
        {
            using Pooled<int> lease = pool.Rent(8, 1);
            lease.Clear();
            lease.CopyFrom([1, 2, 3, 4, 5, 6, 7, 8]);
        }

        NativeMemoryStatistics snapshot = NativeMemoryDiagnostics.Snapshot();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"enabled={NativeMemoryDiagnostics.DetailedCountersEnabled} allocations={snapshot.AllocationCount} frees={snapshot.FreeCount} clears={snapshot.StorageClearCount} copied={snapshot.CopiedBytes} live={liveOutstanding} outstanding={snapshot.OutstandingNativeBytes}"));
        return 0;
    }
}
