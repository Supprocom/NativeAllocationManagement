using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;
using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class VoxelHandoffBenchmarkTests
{
    [Fact]
    public void NativeUploadMatchesListMaterializationAndBlockCopy()
    {
        List<uint> source = VoxelHandoffBenchmark.CreateVoxelWords(
            8_192,
            0x51A7);
        byte[] managed = VoxelHandoffBenchmark.CreateManagedUpload(source);

        Assert.Equal(source.Count * sizeof(uint), managed.Length);
        Assert.True(VoxelHandoffBenchmark.VerifyNativeUpload(source, managed));
    }

    [Fact]
    public async Task ManagedAndNativeWorkersProduceEquivalentEvidence()
    {
        VoxelHandoffBenchmarkOptions options = new(
            WordCount: 8_192,
            Iterations: 4,
            WarmupIterations: 8,
            SampleCount: 2,
            Seed: 0x51A7);

        VoxelHandoffWorkerEvidence managed =
            await VoxelHandoffBenchmark.RunWorkerAsync(
                VoxelHandoffImplementation.Managed,
                options);
        VoxelHandoffWorkerEvidence native =
            await VoxelHandoffBenchmark.RunWorkerAsync(
                VoxelHandoffImplementation.Native,
                options);

        Assert.True(managed.ExactParity);
        Assert.True(native.ExactParity);
        Assert.Equal(managed.ExactOutputSha256, native.ExactOutputSha256);
        Assert.Equal(managed.Checksum, native.Checksum);
        Assert.Equal(managed.LogicalBytes, native.LogicalBytes);
        Assert.True(native.NativeRetainedBytes >= options.WordCount * sizeof(uint));
        Assert.Equal(0, native.NativeFreshSegmentAllocationDelta);
    }

    [Fact]
    public async Task PairedBenchmarkRejectsAnOddSampleCount()
    {
        VoxelHandoffBenchmarkOptions options = new(
            WordCount: 1_024,
            Iterations: 1,
            WarmupIterations: 1,
            SampleCount: 3,
            Seed: 1);

        await Assert.ThrowsAsync<ArgumentException>(
            () => VoxelHandoffBenchmark.RunPairedAsync(options));
    }

    [Fact]
    public void PairedBenchmarkBalancesFirstPosition()
    {
        VoxelHandoffImplementation[] order = Enumerable.Range(0, 6)
            .Select(VoxelHandoffBenchmark.GetFirstImplementation)
            .ToArray();

        Assert.Equal(3, order.Count(value => value == VoxelHandoffImplementation.Managed));
        Assert.Equal(3, order.Count(value => value == VoxelHandoffImplementation.Native));
        Assert.Equal(VoxelHandoffImplementation.Managed, order[0]);
        Assert.Equal(VoxelHandoffImplementation.Native, order[1]);
    }
}
