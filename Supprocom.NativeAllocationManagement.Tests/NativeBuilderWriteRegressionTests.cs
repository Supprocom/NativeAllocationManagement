using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeBuilderWriteRegressionTests
{
    [Fact]
    public void BothBuilderPathsProduceExactOutputAndCleanup()
    {
        NativeBuilderWriteOptions options = new(
            RecordsPerBuilder: 128,
            WorkerCount: 2,
            SampleCount: 2);

        NativeBuilderWriteWorkerEvidence append =
            NativeBuilderWriteRegression.RunWorker(
                NativeBuilderWriteImplementation.RepeatedAppend,
                options);
        NativeBuilderWriteWorkerEvidence direct =
            NativeBuilderWriteRegression.RunWorker(
                NativeBuilderWriteImplementation.BoundedWrite,
                options);

        Assert.True(append.ExactParity);
        Assert.True(direct.ExactParity);
        Assert.Equal(append.OutputSha256, direct.OutputSha256);
        Assert.Equal(append.Checksum, direct.Checksum);
        Assert.True(append.CancellationCleanupPassed);
        Assert.True(direct.CancellationCleanupPassed);
        Assert.True(append.ExactlyOnceCleanupPassed);
        Assert.True(direct.ExactlyOnceCleanupPassed);
        Assert.Equal(0, append.NativeFreshSegmentAllocationDelta);
        Assert.Equal(0, direct.NativeFreshSegmentAllocationDelta);
        Assert.Equal(256, append.BuilderOperationCallCount);
        Assert.Equal(2, direct.BuilderOperationCallCount);
    }

    [Fact]
    public void MeasuredOrderIsBalanced()
    {
        NativeBuilderWriteImplementation[] order = Enumerable.Range(0, 6)
            .Select(NativeBuilderWriteRegression.GetFirstImplementation)
            .ToArray();

        Assert.Equal(
            3,
            order.Count(value => value
                == NativeBuilderWriteImplementation.RepeatedAppend));
        Assert.Equal(
            3,
            order.Count(value => value
                == NativeBuilderWriteImplementation.BoundedWrite));
    }

    [Fact]
    public void PerformanceGateKeepsTheMaterialImprovementFloor()
    {
        Assert.False(NativeBuilderWriteRegression.EvaluateGate(
            exactParity: true,
            balancedOrder: true,
            runtimeConfiguration: true,
            zeroFreshSegments: true,
            cleanup: true,
            productionShape: true,
            binaryIdentity: true,
            meanSpeedup: 1.19,
            aggregateSpeedup: 1.20,
            confidenceLower95: 1.10));
        Assert.True(NativeBuilderWriteRegression.EvaluateGate(
            exactParity: true,
            balancedOrder: true,
            runtimeConfiguration: true,
            zeroFreshSegments: true,
            cleanup: true,
            productionShape: true,
            binaryIdentity: true,
            meanSpeedup: 1.20,
            aggregateSpeedup: 1.20,
            confidenceLower95: 1.01));
        Assert.False(NativeBuilderWriteRegression.EvaluateGate(
            exactParity: true,
            balancedOrder: true,
            runtimeConfiguration: true,
            zeroFreshSegments: true,
            cleanup: true,
            productionShape: true,
            binaryIdentity: false,
            meanSpeedup: 2.00,
            aggregateSpeedup: 2.00,
            confidenceLower95: 1.50));
    }
}
