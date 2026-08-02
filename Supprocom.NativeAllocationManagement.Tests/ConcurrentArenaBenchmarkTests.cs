using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;
using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class ConcurrentArenaBenchmarkTests
{
    [Fact]
    public void RequiredWorkloadMatchesTheVoxelContract()
    {
        Assert.Equal(729, ConcurrentArenaBenchmark.RequiredMapCount);
        Assert.Equal(25_600, ConcurrentArenaBenchmark.RequiredValuesPerMap);
        Assert.Equal(24, ConcurrentArenaBenchmark.RequiredWorkerCount);
        Assert.Equal(123_456, ConcurrentArenaBenchmark.RequiredSeed);
        Assert.Equal(18_662_400L, checked(
            (long)ConcurrentArenaBenchmark.RequiredMapCount
            * ConcurrentArenaBenchmark.RequiredValuesPerMap));
    }

    [Theory]
    [InlineData(ConcurrentArenaValuePattern.Constant)]
    [InlineData(ConcurrentArenaValuePattern.Sequential)]
    public void AllImplementationsProduceTheExactOutput(
        ConcurrentArenaValuePattern pattern)
    {
        ConcurrentArenaBenchmarkOptions options = CreateOptions(pattern);
        ConcurrentArenaWorkerEvidence[] evidence = Enum.GetValues<
                ConcurrentArenaBenchmarkImplementation>()
            .Select(implementation => ConcurrentArenaBenchmark.RunWorker(
                implementation,
                options))
            .ToArray();

        Assert.All(evidence, item => Assert.True(item.ExactParity));
        Assert.Single(evidence.Select(item => item.ExactOutputSha256).Distinct());
        Assert.Single(evidence.Select(item => item.Checksum).Distinct());
        Assert.Equal(0, evidence.Single(item =>
            item.Implementation
                == ConcurrentArenaBenchmarkImplementation.ConcurrentArena)
            .NativeFreshSegmentAllocationDelta);
    }

    [Fact]
    public void FourWayOrderBalancesEveryPosition()
    {
        ConcurrentArenaBenchmarkImplementation[][] orders =
            Enumerable.Range(0, 8)
                .Select(ConcurrentArenaBenchmark.GetImplementationOrder)
                .ToArray();

        foreach (ConcurrentArenaBenchmarkImplementation implementation
            in Enum.GetValues<ConcurrentArenaBenchmarkImplementation>())
        {
            for (int position = 0; position < 4; position++)
            {
                Assert.Equal(
                    2,
                    orders.Count(order => order[position] == implementation));
            }
        }
    }

    [Fact]
    public async Task PairedBenchmarkRejectsAnUnbalancedSampleCount()
    {
        ConcurrentArenaBenchmarkOptions options = CreateOptions(
            ConcurrentArenaValuePattern.Sequential) with
        {
            SampleCount = 6
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => ConcurrentArenaBenchmark.RunPairedAsync(options));
    }

    private static ConcurrentArenaBenchmarkOptions CreateOptions(
        ConcurrentArenaValuePattern pattern) =>
        new(
            MapCount: 16,
            ValuesPerMap: 1_024,
            WorkerCount: 4,
            WarmupIterations: 1,
            Iterations: 1,
            SampleCount: 4,
            Seed: ConcurrentArenaBenchmark.RequiredSeed,
            ValuePattern: pattern);
}
