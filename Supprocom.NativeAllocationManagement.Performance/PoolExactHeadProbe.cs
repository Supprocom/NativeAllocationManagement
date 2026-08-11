using System.Diagnostics;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class PoolExactHeadProbe
{
    internal const int DefaultWarmupIterations = 5_000_000;
    internal const int DefaultMeasuredIterations = 100_000_000;
    private static readonly NativeLeaseInitializer<byte> InitializeOne =
        static writer => writer.Write(1);

    internal static PoolExactHeadProbeReport Run(
        int warmupIterations,
        int measuredIterations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            warmupIterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            measuredIterations);

        using NativePool<byte> pool = new(
            preLease: 1,
            preAllocateBytes: 0,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

        long warmupChecksum = RunIterations(pool, warmupIterations);
        NativeOwnerStatistics before = pool.GetStatistics();
        using Process process = Process.GetCurrentProcess();
        TimeSpan processorBefore = process.TotalProcessorTime;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long timestampBefore = Stopwatch.GetTimestamp();
        long checksum = RunIterations(pool, measuredIterations);
        long timestampAfter = Stopwatch.GetTimestamp();
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        TimeSpan processorAfter = process.TotalProcessorTime;
        TimeSpan elapsed = Stopwatch.GetElapsedTime(
            timestampBefore,
            timestampAfter);
        long allocated = allocatedAfter - allocatedBefore;
        NativeOwnerStatistics after = pool.GetStatistics();

        double processorMilliseconds =
            (processorAfter - processorBefore).TotalMilliseconds;
        return new PoolExactHeadProbeReport(
            warmupIterations,
            measuredIterations,
            ReservedBytes: 1,
            elapsed.TotalMilliseconds,
            processorMilliseconds,
            MeasuredOperationsPerSecond:
                measuredIterations / elapsed.TotalSeconds,
            MeasuredProcessorOperationsPerSecond:
                measuredIterations
                / (processorMilliseconds / 1_000d),
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

internal readonly record struct PoolExactHeadProbeReport(
    int WarmupIterations,
    int MeasuredIterations,
    ulong ReservedBytes,
    double ElapsedMilliseconds,
    double ProcessorMilliseconds,
    double MeasuredOperationsPerSecond,
    double MeasuredProcessorOperationsPerSecond,
    long ManagedAllocatedBytes,
    long FreshSegmentAllocationDelta,
    long RetainedBytes,
    long WarmupChecksum,
    long MeasuredChecksum);
