using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeBuilderStateRegressionTests
{
    [Fact]
    public void ProductionWorkloadMatchesTheRequiredShape()
    {
        Assert.Equal(33_657_622, NativeBuilderStateRegression.TotalWords);
        Assert.Equal(1_179, NativeBuilderStateRegression.ChunkCount);
        Assert.Equal(319, NativeBuilderStateRegression.TightBatchCount);
        Assert.Equal(
            486,
            NativeBuilderStateRegression.MaximumOtherBatchCount);
        Assert.Equal(6, NativeBuilderStateRegression.SampleCount);
        Assert.Equal(
            4,
            NativeBuilderStateRegression.MeasurementIterations);
    }

    [Fact]
    public void EveryImplementationUsesEveryOrderPositionTwice()
    {
        StateBuilderImplementation[][] orders = Enumerable.Range(
                0,
                NativeBuilderStateRegression.SampleCount)
            .Select(NativeBuilderStateRegression.GetOrder)
            .ToArray();

        foreach (StateBuilderImplementation implementation
            in Enum.GetValues<StateBuilderImplementation>())
        {
            for (int position = 0; position < 3; position++)
            {
                Assert.Equal(
                    2,
                    orders.Count(order =>
                        order[position] == implementation));
            }
        }
    }

    [Theory]
    [InlineData(1.01, 1.01, 1.001, true)]
    [InlineData(1.00, 1.01, 1.001, false)]
    [InlineData(1.01, 1.00, 1.001, false)]
    [InlineData(1.01, 1.01, 1.000, false)]
    public void GateRequiresPositiveConfidenceAndZeroClosures(
        double mean,
        double aggregate,
        double lower,
        bool expected)
    {
        Assert.Equal(
            expected,
            NativeBuilderStateRegression.EvaluateGate(
                exactParity: true,
                balancedOrder: true,
                runtimeConfiguration: true,
                exactWorkload: true,
                cleanup: true,
                zeroStateCallbackAllocations: true,
                binaryIdentity: true,
                mean,
                aggregate,
                lower));
        Assert.False(
            NativeBuilderStateRegression.EvaluateGate(
                exactParity: true,
                balancedOrder: true,
                runtimeConfiguration: true,
                exactWorkload: true,
                cleanup: true,
                zeroStateCallbackAllocations: false,
                binaryIdentity: true,
                meanSpeedup: 1.1,
                aggregateSpeedup: 1.1,
                confidenceLower95: 1.01));
    }
}
