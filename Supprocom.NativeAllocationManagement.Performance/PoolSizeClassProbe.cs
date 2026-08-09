using System.Diagnostics;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class PoolSizeClassProbe
{
    private const int WarmupIterations = 100_000;
    private const int MeasuredIterations = 2_000_000;
    private const nuint ReservedBytes = 1_048_576;
    private static readonly NativeLeaseInitializer<byte> InitializeOne =
        static writer => writer.Write(1);

    internal static PoolSizeClassProbeReport Run()
    {
        using NativePool<byte> pool = new(
            preLease: 0,
            preAllocateBytes: ReservedBytes,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        long warmupChecksum = RunIterations(pool, WarmupIterations);
        NativeOwnerStatistics before = pool.GetStatistics();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch clock = Stopwatch.StartNew();
        long checksum = RunIterations(pool, MeasuredIterations);
        clock.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread()
            - allocatedBefore;
        NativeOwnerStatistics after = pool.GetStatistics();

        return new PoolSizeClassProbeReport(
            WarmupIterations,
            MeasuredIterations,
            (ulong)ReservedBytes,
            clock.Elapsed.TotalMilliseconds,
            MeasuredOperationsPerSecond:
                MeasuredIterations / clock.Elapsed.TotalSeconds,
            allocated,
            after.FreshSegmentAllocationCount
                - before.FreshSegmentAllocationCount,
            after.RetainedBytes,
            warmupChecksum,
            checksum);
    }

    private static long RunIterations(
        NativePool<byte> pool,
        int iterations)
    {
        long checksum = 0;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            Pooled<byte> lease = pool.Rent(1, InitializeOne);
            try
            {
                checksum += lease.Length;
            }
            finally
            {
                lease.Dispose();
            }
        }

        return checksum;
    }
}

internal readonly record struct PoolSizeClassProbeReport(
    int WarmupIterations,
    int MeasuredIterations,
    ulong ReservedBytes,
    double ElapsedMilliseconds,
    double MeasuredOperationsPerSecond,
    long ManagedAllocatedBytes,
    long FreshSegmentAllocationDelta,
    long RetainedBytes,
    long WarmupChecksum,
    long MeasuredChecksum);
