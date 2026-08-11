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
}
