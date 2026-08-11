using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeBuilderPairRegressionTests
{
    [Fact]
    public void ProductionWorkloadMatchesTheRetainedCounts()
    {
        Assert.Equal(33_657_622, NativeBuilderPairRegression.OpaqueWords);
        Assert.Equal(14_800, NativeBuilderPairRegression.TransparentWords);
        Assert.Equal(1_179, NativeBuilderPairRegression.ChunkCount);
        Assert.Equal(6, NativeBuilderPairRegression.SampleCount);
    }

    [Fact]
    public void ImplementationOrderIsBalanced()
    {
        CompositeBuilderImplementation[] order = Enumerable.Range(
                0,
                NativeBuilderPairRegression.SampleCount)
            .Select(NativeBuilderPairRegression.GetFirstImplementation)
            .ToArray();

        Assert.Equal(
            NativeBuilderPairRegression.SampleCount / 2,
            order.Count(static item =>
                item == CompositeBuilderImplementation.ManagedStaging));
        Assert.Equal(
            NativeBuilderPairRegression.SampleCount / 2,
            order.Count(static item =>
                item == CompositeBuilderImplementation.CompositeBorrow));
    }

    [Theory]
    [InlineData(1.01, 1.01, 1.001, true)]
    [InlineData(1.00, 1.01, 1.001, false)]
    [InlineData(1.01, 1.00, 1.001, false)]
    [InlineData(1.01, 1.01, 1.000, false)]
    public void GateRequiresACompletePositivePairedResult(
        double mean,
        double aggregate,
        double lower,
        bool expected)
    {
        Assert.Equal(
            expected,
            NativeBuilderPairRegression.EvaluateGate(
                exactParity: true,
                balancedOrder: true,
                cleanup: true,
                runtimeConfiguration: true,
                exactWorkload: true,
                binaryIdentity: true,
                mean,
                aggregate,
                lower));
    }
}
