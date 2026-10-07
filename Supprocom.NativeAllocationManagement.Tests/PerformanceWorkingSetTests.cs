using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;
using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class PerformanceWorkingSetTests
{
    private const long TransientBytes = 128 * 1024 * 1024;
    // OS RSS/high-water reads are separately sampled and can differ slightly.
    private const long ObservationToleranceBytes = 4 * 1024 * 1024;

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task WorkerCapturesReleasedTransientPeak(bool builder, bool native)
    {
        using Process process = Process.GetCurrentProcess();
        long priorPeak = CreateReleasedTransientPeak(process);
        long reportedPeak;
        long before;
        long after;
        if (builder)
        {
            NativeBuilderWorkerEvidence evidence = await NativeBuilderBenchmark.RunWorkerAsync(
                native ? NativeBuilderBenchmarkImplementation.NativeBuilder : NativeBuilderBenchmarkImplementation.ManagedExactArray,
                new(1024, 1024, 256, 2, 1, 2, 29123));
            Assert.True(evidence.ExactParity);
            reportedPeak = evidence.PeakWorkingSetBytes;
            before = evidence.WorkingSetBeforeBytes;
            after = evidence.WorkingSetAfterBytes;
        }
        else
        {
            PooledRegressionWorkerEvidence evidence = PooledPerformanceRegression.RunWorker(
                native ? PooledRegressionImplementation.Pooled : PooledRegressionImplementation.ArrayPool,
                PooledPerformanceRegression.DefaultOptions with { WorkerCount = 1, Iterations = 2, WarmupIterations = 1, SampleCount = 2 });
            Assert.True(evidence.ExactParity);
            reportedPeak = evidence.PeakObservedWorkingSetBytes;
            before = evidence.WorkingSetBeforeBytes;
            after = evidence.WorkingSetAfterBytes;
        }

        Assert.True(priorPeak > Math.Max(before, after) + TransientBytes / 2,
            "The actual released transient extent must exceed both worker endpoints.");
        Assert.True(reportedPeak > Math.Max(before, after) + TransientBytes / 2,
            "Endpoint maxima cannot satisfy the process-lifetime peak contract.");
        Assert.InRange(reportedPeak, priorPeak - ObservationToleranceBytes, long.MaxValue);
    }

    [Theory]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedExactArray)]
    [InlineData(NativeBuilderBenchmarkImplementation.NativeBuilder)]
    public async Task IsolatedBuilderPreservesPositiveWorkerHighWater(NativeBuilderBenchmarkImplementation implementation)
    {
        NativeBuilderWorkerEvidence evidence = await NativeBuilderBenchmark.RunIsolatedWorkerAsync(
            implementation, new(1024, 1024, 256, 2, 1, 2, 29123));
        Assert.True(evidence.ExactParity);
        Assert.True(evidence.PeakWorkingSetBytes > 0);
        Assert.True(evidence.PeakWorkingSetBytes + ObservationToleranceBytes
            >= Math.Max(evidence.WorkingSetBeforeBytes, evidence.WorkingSetAfterBytes));
    }

    private static long CreateReleasedTransientPeak(Process process)
    {
        using (MemoryMappedFile mapping = MemoryMappedFile.CreateNew(null, TransientBytes))
        using (MemoryMappedViewAccessor view = mapping.CreateViewAccessor(0, TransientBytes, MemoryMappedFileAccess.ReadWrite))
        {
            for (long offset = 0; offset < TransientBytes; offset += Environment.SystemPageSize)
                view.Write(offset, (byte)0x5a);
        }

        process.Refresh();
        return process.PeakWorkingSet64;
    }
}
