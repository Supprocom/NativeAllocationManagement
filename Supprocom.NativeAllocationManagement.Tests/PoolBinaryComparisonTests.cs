using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class PoolBinaryComparisonTests
{
    [Fact]
    public void EightSamplesUseFourFirstPositionsForEachBinary()
    {
        PoolBinaryVariant[] order = Enumerable.Range(0, 8)
            .Select(PoolBinaryComparison.GetFirstVariant)
            .ToArray();

        Assert.Equal(
            4,
            order.Count(static item =>
                item == PoolBinaryVariant.Baseline));
        Assert.Equal(
            4,
            order.Count(static item =>
                item == PoolBinaryVariant.Candidate));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(1)]
    [InlineData(7)]
    public void InvalidSampleCountsAreRejected(int sampleCount)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => PoolBinaryComparison.ValidateSampleCount(sampleCount));
    }

    [Fact]
    public void OneAssemblyComparedWithItselfProducesValidEvidence()
    {
        string assemblyPath = typeof(PoolBinaryComparison)
            .Assembly.Location;

        PoolBinaryComparisonReport report = PoolBinaryComparison.Run(
            assemblyPath,
            assemblyPath,
            sampleCount: 2,
            warmupIterations: 100_000,
            measuredIterations: 1_000_000);

        Assert.True(report.ExactParity);
        Assert.True(report.BalancedOrder);
        Assert.True(report.ZeroManagedAllocation);
        Assert.True(report.ZeroFreshSegments);
        Assert.True(report.ProcessorMeasurementsPassed);
        Assert.Equal(
            report.BaselinePerformanceSha256,
            report.CandidatePerformanceSha256);
        Assert.Equal(
            report.BaselineRuntimeSha256,
            report.CandidateRuntimeSha256);
    }
}
