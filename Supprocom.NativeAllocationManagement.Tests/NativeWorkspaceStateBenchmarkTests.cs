using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeWorkspaceStateBenchmarkTests
{
    [Fact]
    public void DefaultShapeMatchesTheRequiredProductionWorkload()
    {
        NativeWorkspaceStateOptions options =
            NativeWorkspaceStateBenchmark.DefaultOptions;

        Assert.Equal(729, options.MapCount);
        Assert.Equal(160, options.MapSize);
        Assert.Equal(51_200, options.WorkspaceLength);
        Assert.Equal(24, options.WorkerCount);
        Assert.Equal(6, options.SampleCount);
        Assert.Equal(12, options.MeasurementPassCount);
        Assert.Equal(123_456, options.Seed);
        Assert.Equal(
            0.98d,
            NativeWorkspaceStateBenchmark.RequiredConfidenceLower95);
    }

    [Fact]
    public void SixSampleScheduleBalancesTheAcceptancePair()
    {
        NativeWorkspaceStateImplementation[][] orders = Enumerable
            .Range(0, 6)
            .Select(NativeWorkspaceStateBenchmark.GetImplementationOrder)
            .ToArray();

        Assert.Equal(
            3,
            orders.Count(order => order[0]
                == NativeWorkspaceStateImplementation.ManagedArray));
        Assert.Equal(
            3,
            orders.Count(order => order[0]
                == NativeWorkspaceStateImplementation
                    .ExplicitStateWorkspace));
        Assert.All(
            orders,
            order => Assert.Equal(
                NativeWorkspaceStateImplementation.CapturingWorkspace,
                order[2]));
    }

    [Fact]
    public void GateRequiresParityPerformanceAllocationAndCleanup()
    {
        Assert.True(NativeWorkspaceStateBenchmark.EvaluateGate(
            exactParity: true,
            balancedOrder: true,
            runtimeConfiguration: true,
            zeroFreshSegments: true,
            cleanup: true,
            allocationAdvantage: true,
            productionShape: true,
            binaryIdentity: true,
            meanSpeedup: 1.01,
            aggregateSpeedup: 1.01,
            confidenceLower95: 0.981));
        Assert.False(NativeWorkspaceStateBenchmark.EvaluateGate(
            exactParity: true,
            balancedOrder: true,
            runtimeConfiguration: true,
            zeroFreshSegments: true,
            cleanup: true,
            allocationAdvantage: true,
            productionShape: true,
            binaryIdentity: true,
            meanSpeedup: 0.999,
            aggregateSpeedup: 1.01,
            confidenceLower95: 0.981));
        Assert.False(NativeWorkspaceStateBenchmark.EvaluateGate(
            exactParity: true,
            balancedOrder: true,
            runtimeConfiguration: true,
            zeroFreshSegments: true,
            cleanup: true,
            allocationAdvantage: false,
            productionShape: true,
            binaryIdentity: true,
            meanSpeedup: 1.01,
            aggregateSpeedup: 1.01,
            confidenceLower95: 0.981));
        Assert.False(NativeWorkspaceStateBenchmark.EvaluateGate(
            exactParity: true,
            balancedOrder: true,
            runtimeConfiguration: true,
            zeroFreshSegments: true,
            cleanup: true,
            allocationAdvantage: true,
            productionShape: true,
            binaryIdentity: true,
            meanSpeedup: 1.01,
            aggregateSpeedup: 1.01,
            confidenceLower95: 0.979));
    }

    [Fact]
    public void ReducedWorkersProduceExactParityAndCleanup()
    {
        var options = new NativeWorkspaceStateOptions(
            MapCount: 24,
            MapSize: 16,
            WorkspaceLength: 512,
            WorkerCount: 4,
            SampleCount: 6,
            WarmupCount: 1,
            MeasurementPassCount: 1,
            Seed: 123_456);

        NativeWorkspaceStateWorkerEvidence managed =
            NativeWorkspaceStateBenchmark.RunWorker(
                NativeWorkspaceStateImplementation.ManagedArray,
                options);
        NativeWorkspaceStateWorkerEvidence capturing =
            NativeWorkspaceStateBenchmark.RunWorker(
                NativeWorkspaceStateImplementation.CapturingWorkspace,
                options);
        NativeWorkspaceStateWorkerEvidence explicitState =
            NativeWorkspaceStateBenchmark.RunWorker(
                NativeWorkspaceStateImplementation
                    .ExplicitStateWorkspace,
                options);

        Assert.Equal(managed.OutputSha256, capturing.OutputSha256);
        Assert.Equal(managed.OutputSha256, explicitState.OutputSha256);
        Assert.Equal(
            managed.WorkerChecksums,
            capturing.WorkerChecksums);
        Assert.Equal(
            managed.WorkerChecksums,
            explicitState.WorkerChecksums);
        Assert.Equal(
            0,
            explicitState.NativeFreshSegmentAllocationDelta);
        Assert.True(capturing.ExactlyOnceCleanupPassed);
        Assert.True(explicitState.ExactlyOnceCleanupPassed);
        Assert.True(explicitState.CancellationCleanupPassed);
        Assert.True(explicitState.SetupManagedAllocatedBytes
            < managed.SetupManagedAllocatedBytes);
    }

    [Fact]
    public void ReducedPairUsesTheSamePersistentWorkerThreads()
    {
        var options = new NativeWorkspaceStateOptions(
            MapCount: 24,
            MapSize: 16,
            WorkspaceLength: 512,
            WorkerCount: 4,
            SampleCount: 6,
            WarmupCount: 1,
            MeasurementPassCount: 2,
            Seed: 123_456);

        NativeWorkspaceStateIsolatedPairEvidence pair =
            NativeWorkspaceStateBenchmark.RunSharedPairWorker(
                sampleIndex: 0,
                options);
        NativeWorkspaceStateWorkerEvidence managed = pair.Evidence
            .Single(evidence => evidence.Implementation
                == NativeWorkspaceStateImplementation.ManagedArray);
        NativeWorkspaceStateWorkerEvidence native = pair.Evidence
            .Single(evidence => evidence.Implementation
                == NativeWorkspaceStateImplementation
                    .ExplicitStateWorkspace);

        Assert.Equal(4, managed.WorkerThreadIds.Distinct().Count());
        Assert.Equal(
            managed.WorkerThreadIds,
            native.WorkerThreadIds);
        Assert.Equal(managed.OutputSha256, native.OutputSha256);
        Assert.True(native.ExactlyOnceCleanupPassed);
        Assert.True(native.CancellationCleanupPassed);
    }
}
