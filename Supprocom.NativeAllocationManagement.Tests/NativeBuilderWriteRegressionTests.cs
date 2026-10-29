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
        AssertKeptOrFreed(append, options.WorkerCount);
        AssertKeptOrFreed(direct, options.WorkerCount);
        Assert.Equal(256, append.BuilderOperationCallCount);
        Assert.Equal(2, direct.BuilderOperationCallCount);
    }

    // A builder takes a block NAM kept for reuse when one fits, which records
    // no allocation, and its disposed transfer keeps the block again. So the
    // measured batch allocates at most one block per builder, every block it
    // allocated was freed or is kept, and kept storage is all that stays
    // retained.
    private static void AssertKeptOrFreed(NativeBuilderWriteWorkerEvidence evidence, int workerCount)
    {
        Assert.InRange(evidence.NativeAllocationCountDelta, 0, workerCount);
        Assert.Equal(
            evidence.NativeKeptCountAfter - evidence.NativeKeptCountBefore,
            evidence.NativeAllocationCountDelta - evidence.NativeFreeCountDelta);
        Assert.Equal(evidence.NativeKeptBytesBefore, evidence.NativeRetainedBytesBefore);
        Assert.Equal(evidence.NativeKeptBytesAfter, evidence.NativeRetainedBytesAfter);
        Assert.True(NativeBuilderWriteRegression.HasExpectedNativeOperations(evidence));
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
            boundedNativeAllocations: true,
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
            boundedNativeAllocations: true,
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
            boundedNativeAllocations: true,
            cleanup: true,
            productionShape: true,
            binaryIdentity: false,
            meanSpeedup: 2.00,
            aggregateSpeedup: 2.00,
            confidenceLower95: 1.50));
    }
}
