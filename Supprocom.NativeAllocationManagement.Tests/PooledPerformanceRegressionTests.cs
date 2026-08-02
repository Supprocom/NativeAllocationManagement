using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

[Collection(PerformanceRegressionCollection.Name)]
public sealed class PooledPerformanceRegressionTests
{
    [Fact]
    public void GatePolicyUsesTheRequiredTopologyAndThreshold()
    {
        PooledRegressionOptions options =
            PooledPerformanceRegression.DefaultOptions;

        Assert.Equal(24, options.WorkerCount);
        Assert.Equal(8, options.SampleCount);
        Assert.Equal(8_192, options.Iterations);
        Assert.Equal(1_024, options.WarmupIterations);
        Assert.Equal(8, options.OpaquePlaneLength);
        Assert.Equal(32, options.TransparentPlaneLength);
        Assert.Equal(1.10d, PooledPerformanceRegression.RequiredSpeedup);
        Assert.Equal(
            400,
            PooledPerformanceRegression.BoundaryOptions
                .OpaquePlaneLength);
        Assert.Equal(
            25_600,
            PooledPerformanceRegression.BoundaryOptions
                .TransparentPlaneLength);
    }

    [Fact]
    public void GatePolicyRequiresEveryCorrectnessAndSpeedCondition()
    {
        Assert.True(PooledPerformanceRegression.EvaluateGate(
            exactParity: true,
            balancedOrder: true,
            runtimeConfiguration: true,
            zeroFreshSegments: true,
            meanSpeedup: 1.10d,
            aggregateSpeedup: 1.10d,
            confidenceLower95: 1.0001d));
        Assert.False(PooledPerformanceRegression.EvaluateGate(
            exactParity: false,
            balancedOrder: true,
            runtimeConfiguration: true,
            zeroFreshSegments: true,
            meanSpeedup: 1.10d,
            aggregateSpeedup: 1.10d,
            confidenceLower95: 1.0001d));
        Assert.False(PooledPerformanceRegression.EvaluateGate(
            exactParity: true,
            balancedOrder: true,
            runtimeConfiguration: true,
            zeroFreshSegments: true,
            meanSpeedup: 1.0999d,
            aggregateSpeedup: 1.10d,
            confidenceLower95: 1.0001d));
        Assert.False(PooledPerformanceRegression.EvaluateGate(
            exactParity: true,
            balancedOrder: true,
            runtimeConfiguration: true,
            zeroFreshSegments: true,
            meanSpeedup: 1.10d,
            aggregateSpeedup: 1.10d,
            confidenceLower95: 1d));
    }

    [Fact]
    public void EightSamplesUseFourFirstPositionsForEachImplementation()
    {
        PooledRegressionImplementation[] order = Enumerable.Range(0, 8)
            .Select(PooledPerformanceRegression.GetFirstImplementation)
            .ToArray();

        Assert.Equal(
            4,
            order.Count(static item =>
                item == PooledRegressionImplementation.ArrayPool));
        Assert.Equal(
            4,
            order.Count(static item =>
                item == PooledRegressionImplementation.Pooled));
    }

    [Fact]
    [Trait("Category", "PerformanceRegression")]
    public async Task PersistentWorkspaceBeatsArrayPoolShared()
    {
        PooledRegressionReport report =
            await PooledPerformanceRegression.RunPairedAsync(
                PooledPerformanceRegression.DefaultOptions);

        Assert.True(report.ExactParity);
        Assert.True(report.BalancedOrder);
        Assert.True(report.RuntimeConfigurationPassed);
        Assert.True(report.ZeroFreshSegments);
        Assert.True(
            report.GatePassed,
            $"Pooled gate failed. Mean: {report.PairedMeanSpeedup:R}. Aggregate: {report.AggregateSpeedup:R}. Lower: {report.ConfidenceLower95:R}.");
    }
}
