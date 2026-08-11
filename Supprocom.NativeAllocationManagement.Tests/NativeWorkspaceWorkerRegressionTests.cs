using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeWorkspaceWorkerRegressionTests
{
    [Fact]
    public void FixedScheduleBalancesEveryImplementationPosition()
    {
        NativeWorkspaceWorkerImplementation[] implementations =
            Enum.GetValues<NativeWorkspaceWorkerImplementation>();

        foreach (NativeWorkspaceWorkerImplementation implementation
            in implementations)
        {
            int[] positions = Enumerable.Range(0, 3)
                .Select(position => Enumerable.Range(
                        0,
                        NativeWorkspaceWorkerRegression.SampleCount)
                    .Count(sampleIndex => Array.IndexOf(
                        NativeWorkspaceWorkerRegression.GetOrder(
                            sampleIndex),
                        implementation) == position))
                .ToArray();

            Assert.Equal([2, 2, 2], positions);
        }
    }

    [Fact]
    public void ProductionShapeConstantsRemainFixed()
    {
        Assert.Equal(
            1_179,
            NativeWorkspaceWorkerRegression.BuildCount);
        Assert.Equal(
            153_600,
            NativeWorkspaceWorkerRegression.WorkspaceLength);
        Assert.Equal(
            6,
            NativeWorkspaceWorkerRegression.SampleCount);
    }
}
