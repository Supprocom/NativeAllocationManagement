using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;
using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class VoxelHandoffBenchmarkTests
{
    [VoxelDemonstrationFact]
    public void NativeUploadMatchesListMaterializationAndBlockCopy()
    {
        List<uint> source = VoxelHandoffBenchmark.CreateVoxelWords(
            8_192,
            0x51A7);
        byte[] managed = VoxelHandoffBenchmark.CreateManagedUpload(source);

        Assert.Equal(source.Count * sizeof(uint), managed.Length);
        Assert.True(VoxelHandoffBenchmark.VerifyNativeUpload(source, managed));
    }

    [VoxelDemonstrationFact]
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
                options).ConfigureAwait(false);
        VoxelHandoffWorkerEvidence native =
            await VoxelHandoffBenchmark.RunWorkerAsync(
                VoxelHandoffImplementation.Native,
                options).ConfigureAwait(false);

        Assert.True(managed.ExactParity);
        Assert.True(native.ExactParity);
        Assert.Equal(managed.ExactOutputSha256, native.ExactOutputSha256);
        Assert.Equal(managed.Checksum, native.Checksum);
        Assert.Equal(managed.LogicalBytes, native.LogicalBytes);
        Assert.True(native.NativeRetainedBytes >= options.WordCount * sizeof(uint));
        Assert.Equal(0, native.NativeFreshSegmentAllocationDelta);
    }

    [VoxelDemonstrationFact]
    public void NativePreparationCoversEverySimultaneousChannelOwner()
    {
        using NativeConcurrentPool<uint> pool = new(8_192, NativeMemoryReturn.ToNativeMemory);
        VoxelHandoffBenchmark.PrepareNativeHandoffStorage(pool, 8_192, static writer => writer.Fill(42));
        long preparedAllocations = pool.GetStatistics().FreshSegmentAllocationCount;
        using (NativeTransfer<uint> consumed = pool.RentTransferable(8_192, static writer => writer.Fill(43)))
        using (NativeTransfer<uint> queued = pool.RentTransferable(8_192, static writer => writer.Fill(47)))
        using (NativeTransfer<uint> produced = pool.RentTransferable(8_192, static writer => writer.Fill(53)))
        {
            Assert.Equal(preparedAllocations, pool.GetStatistics().FreshSegmentAllocationCount);
            Assert.Equal(43u, consumed.Read(static view => view[8_191]));
            Assert.Equal(47u, queued.Read(static view => view[8_191]));
            Assert.Equal(53u, produced.Read(static view => view[8_191]));
        }
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    [VoxelDemonstrationFact]
    public void OnePreleasedPayloadDoesNotCoverTheChannelOwnershipBound()
    {
        using NativeConcurrentPool<uint> pool = new(8_192, NativeMemoryReturn.ToNativeMemory);
        long initialAllocations = pool.GetStatistics().FreshSegmentAllocationCount;
        using NativeTransfer<uint> consumed = pool.RentTransferable(8_192, static writer => writer.Fill(43));
        using NativeTransfer<uint> queued = pool.RentTransferable(8_192, static writer => writer.Fill(47));
        using NativeTransfer<uint> produced = pool.RentTransferable(8_192, static writer => writer.Fill(53));
        Assert.True(pool.GetStatistics().FreshSegmentAllocationCount > initialAllocations);
    }

    [VoxelDemonstrationFact]
    public async Task NativeWorkerWithMinimalWarmupHasNoMeasuredBackingGrowth()
    {
        VoxelHandoffWorkerEvidence evidence = await VoxelHandoffBenchmark.RunWorkerAsync(
            VoxelHandoffImplementation.Native,
            new VoxelHandoffBenchmarkOptions(WordCount: 8_192, Iterations: 32, WarmupIterations: 1, SampleCount: 2, Seed: 0x51A7)).ConfigureAwait(false);
        Assert.True(evidence.ExactParity);
        Assert.Equal(0, evidence.NativeFreshSegmentAllocationDelta);
        Assert.True(evidence.SetupMilliseconds > 0);
        Assert.True(evidence.NativeRetainedBytes >= 3L * 8_192 * sizeof(uint));
    }

    [VoxelDemonstrationFact]
    public async Task PairedBenchmarkRejectsAnOddSampleCount()
    {
        VoxelHandoffBenchmarkOptions options = new(
            WordCount: 1_024,
            Iterations: 1,
            WarmupIterations: 1,
            SampleCount: 3,
            Seed: 1);

        await Assert.ThrowsAsync<ArgumentException>(
            () => VoxelHandoffBenchmark.RunPairedAsync(options)).ConfigureAwait(false);
    }

    [VoxelDemonstrationFact]
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

[AttributeUsage(AttributeTargets.Method)]
internal sealed class VoxelDemonstrationFactAttribute : FactAttribute
{
    public VoxelDemonstrationFactAttribute()
    {
        if (!VoxelDemonstration.IsEnabled)
        {
            Skip = VoxelDemonstration.SkipMessage;
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class VoxelDemonstrationTheoryAttribute : TheoryAttribute
{
    public VoxelDemonstrationTheoryAttribute()
    {
        if (!VoxelDemonstration.IsEnabled)
        {
            Skip = VoxelDemonstration.SkipMessage;
        }
    }
}

internal static class VoxelDemonstration
{
    internal const string SkipMessage =
        "Set NAM_RUN_VOXEL_DEMO=1 to run the optional voxel demonstration.";

    internal static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable(
                "NAM_RUN_VOXEL_DEMO"),
            "1",
            StringComparison.Ordinal);
}
