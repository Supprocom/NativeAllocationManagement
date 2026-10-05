using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolRetirementRegressionTests
{
    [Fact]
    public void ProductionShapeConstantsRemainFixed()
    {
        Assert.Equal(24, NativePoolRetirementRegression.WorkerCount);
        Assert.Equal(1_179, NativePoolRetirementRegression.BuildCount);
        Assert.Equal(153_600, NativePoolRetirementRegression.Capacity);
        Assert.Equal(6, NativePoolRetirementRegression.SampleCount);
    }

    [Fact]
    public void FixedScheduleBalancesEveryImplementationPosition()
    {
        foreach (NativePoolRetirementImplementation implementation
            in Enum.GetValues<NativePoolRetirementImplementation>())
        {
            int[] positions = Enumerable.Range(0, 3)
                .Select(position => Enumerable.Range(
                        0,
                        NativePoolRetirementRegression.SampleCount)
                    .Count(sampleIndex => Array.IndexOf(
                        NativePoolRetirementRegression.GetOrder(
                            sampleIndex),
                        implementation) == position))
                .ToArray();

            Assert.Equal([2, 2, 2], positions);
        }
    }

    [Fact]
    public void FailedInitializationReleasesEverySuccessfullyCreatedWorkerPool()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => new NativePoolRetirementRegression.PoolRetirementWorkload());
            NativeMemoryTestMetrics metrics = NativeMemoryTestHooks.Snapshot();
            Assert.Equal(metrics.AllocationCount, metrics.FreeCount);
            Assert.Equal(0, metrics.OutstandingNativeBytes);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void NormalTeardownReleasesEachNativePoolExactlyOnce()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativePoolRetirementRegression.PoolRetirementWorkload workload = new();
            NativeMemoryTestMetrics before = NativeMemoryTestHooks.Snapshot();
            Assert.Equal(NativePoolRetirementRegression.WorkerCount, before.AllocationCount);
            Assert.Equal(0, before.FreeCount);
            workload.Dispose();
            workload.Dispose();
            NativeMemoryTestMetrics after = NativeMemoryTestHooks.Snapshot();
            Assert.Equal(before.AllocationCount, after.AllocationCount);
            Assert.Equal(before.AllocationCount, after.FreeCount);
            Assert.Equal(0, after.OutstandingNativeBytes);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }
}
