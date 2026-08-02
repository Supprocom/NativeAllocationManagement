using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;
using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeBuilderBenchmarkTests
{
    [Fact]
    public void NativeBuilderOutputMatchesListAndToArray()
    {
        NativeBuilderBenchmarkOptions options = CreateOptions();
        uint[] managed = NativeBuilderBenchmark.BuildManagedOutput(options);
        using NativePool<uint> pool = new(
            options.InitialCapacity,
            NativeMemoryReturn.ToNativeMemory);
        uint[] native = NativeBuilderBenchmark.BuildNativeOutput(
            pool,
            options);

        Assert.Equal(options.ElementCount, managed.Length);
        Assert.Equal(managed, native);
    }

    [Fact]
    public void WorkersProduceEquivalentMeasuredEvidence()
    {
        NativeBuilderBenchmarkOptions options = CreateOptions();

        NativeBuilderWorkerEvidence managed =
            NativeBuilderBenchmark.RunWorker(
                NativeBuilderBenchmarkImplementation.ManagedList,
                options);
        NativeBuilderWorkerEvidence native =
            NativeBuilderBenchmark.RunWorker(
                NativeBuilderBenchmarkImplementation.NativeBuilder,
                options);

        Assert.True(managed.ExactParity);
        Assert.True(native.ExactParity);
        Assert.Equal(
            managed.ExactOutputSha256,
            native.ExactOutputSha256);
        Assert.Equal(managed.Checksum, native.Checksum);
        Assert.Equal(managed.LogicalBytes, native.LogicalBytes);
        Assert.True(
            native.ManagedAllocatedBytes
                < managed.ManagedAllocatedBytes);
        Assert.Equal(0, native.NativeFreshSegmentAllocationDelta);
        Assert.True(native.NativeRetainedBytes > 0);
    }

    [Fact]
    public async Task PairedBenchmarkRejectsAnOddSampleCount()
    {
        NativeBuilderBenchmarkOptions options = CreateOptions() with
        {
            SampleCount = 3
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => NativeBuilderBenchmark.RunPairedAsync(options));
    }

    [Fact]
    public void PairedBenchmarkBalancesFirstPosition()
    {
        NativeBuilderBenchmarkImplementation[] order =
            Enumerable.Range(0, 10)
                .Select(NativeBuilderBenchmark.GetFirstImplementation)
                .ToArray();

        Assert.Equal(
            5,
            order.Count(value =>
                value
                    == NativeBuilderBenchmarkImplementation.ManagedList));
        Assert.Equal(
            5,
            order.Count(value =>
                value
                    == NativeBuilderBenchmarkImplementation.NativeBuilder));
        Assert.Equal(
            NativeBuilderBenchmarkImplementation.ManagedList,
            order[0]);
        Assert.Equal(
            NativeBuilderBenchmarkImplementation.NativeBuilder,
            order[1]);
    }

    [Fact]
    public void ConfidenceLowerUsesEveryPairedObservation()
    {
        double lower = PairedBenchmarkStatistics.ConfidenceLower95(
            new[] { 1.1, 1.2, 1.3, 1.4, 1.5, 1.6 });

        Assert.True(lower > 1d);
        Assert.True(lower < 1.35d);
    }

    private static NativeBuilderBenchmarkOptions CreateOptions() =>
        new(
            ElementCount: 8_192,
            InitialCapacity: 64,
            BatchSize: 64,
            Iterations: 4,
            WarmupIterations: 2,
            SampleCount: 2,
            Seed: 0x71C3);
}
