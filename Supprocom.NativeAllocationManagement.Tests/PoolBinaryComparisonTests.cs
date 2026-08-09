using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class PoolBinaryComparisonTests
{
    [Fact]
    public void EightSamplesUseTheBlockedCrossoverOrder()
    {
        PoolBinaryVariant[] order = Enumerable.Range(0, 8)
            .Select(PoolBinaryComparison.GetFirstVariant)
            .ToArray();

        Assert.Equal(
            [
                PoolBinaryVariant.Baseline,
                PoolBinaryVariant.Candidate,
                PoolBinaryVariant.Candidate,
                PoolBinaryVariant.Baseline,
                PoolBinaryVariant.Baseline,
                PoolBinaryVariant.Candidate,
                PoolBinaryVariant.Candidate,
                PoolBinaryVariant.Baseline
            ],
            order);
        Assert.Equal(64, PoolBinaryComparison.SubBatchCount);
    }

    [Theory]
    [InlineData(-8)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(10)]
    public void InvalidSampleCountsAreRejected(int sampleCount)
    {
        Assert.Throws<ArgumentException>(
            () => PoolBinaryComparison.ValidateSampleCount(sampleCount));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void PositiveMultiplesOfEightAreAccepted(int sampleCount)
    {
        PoolBinaryComparison.ValidateSampleCount(sampleCount);
    }

    [Fact]
    public void SchedulerContaminationInvalidatesTheMeasurement()
    {
        PoolBinaryPairEvidence[] pairs = CreatePairs(
            baselineWall: 2_000d,
            baselineProcessor: 1_700d,
            candidateWall: 1_600d,
            candidateProcessor: 1_580d);

        PoolBinaryNoiseAssessment result =
            PoolBinaryComparison.AssessNoise(pairs);

        Assert.False(result.ProcessorResidencyPassed);
        Assert.False(result.StrictHostStabilityPassed);
    }

    [Fact]
    public void ShortObservationsCannotProduceValidEvidence()
    {
        PoolBinaryPairEvidence[] pairs = CreatePairs(
            baselineWall: 500d,
            baselineProcessor: 500d,
            candidateWall: 400d,
            candidateProcessor: 400d);

        PoolBinaryNoiseAssessment result =
            PoolBinaryComparison.AssessNoise(pairs);

        Assert.False(result.MinimumDurationPassed);
        Assert.False(result.StrictHostStabilityPassed);
    }

    [Fact]
    public void StableMeasurementsPassEveryNoiseCheck()
    {
        PoolBinaryPairEvidence[] pairs = CreatePairs(
            baselineWall: 2_000d,
            baselineProcessor: 1_980d,
            candidateWall: 1_600d,
            candidateProcessor: 1_590d);

        PoolBinaryNoiseAssessment result =
            PoolBinaryComparison.AssessNoise(pairs);

        Assert.True(result.StrictHostStabilityPassed);
    }

    [Fact]
    public void CandidateMustExceedTheMeasuredControlEnvelope()
    {
        PoolBinaryMeasurementEvidence calibration =
            PoolBinaryComparison.Summarize(CreatePairs(
                baselineWall: 2_040d,
                baselineProcessor: 2_020d,
                candidateWall: 2_000d,
                candidateProcessor: 2_000d));
        PoolBinaryMeasurementEvidence comparison =
            PoolBinaryComparison.Summarize(CreatePairs(
                baselineWall: 2_400d,
                baselineProcessor: 2_380d,
                candidateWall: 2_000d,
                candidateProcessor: 2_000d));

        Assert.True(
            PoolBinaryComparison.IsCalibrationEquivalent(calibration));
        Assert.Equal(
            PoolBinaryDecision.Improvement,
            PoolBinaryComparison.Decide(
                validEvidence: true,
                calibration,
                comparison));
    }

    [Fact]
    public void ControlBiasCannotBecomeACandidateImprovement()
    {
        PoolBinaryMeasurementEvidence calibration =
            PoolBinaryComparison.Summarize(CreatePairs(
                baselineWall: 2_040d,
                baselineProcessor: 2_020d,
                candidateWall: 2_000d,
                candidateProcessor: 2_000d));
        PoolBinaryMeasurementEvidence comparison =
            PoolBinaryComparison.Summarize(CreatePairs(
                baselineWall: 2_040d,
                baselineProcessor: 2_020d,
                candidateWall: 2_000d,
                candidateProcessor: 2_000d));

        Assert.Equal(
            PoolBinaryDecision.Inconclusive,
            PoolBinaryComparison.Decide(
                validEvidence: true,
                calibration,
                comparison));
    }

    [Fact]
    public void OneAssemblyComparedWithItselfPreservesExactEvidence()
    {
        string assemblyPath = typeof(PoolBinaryComparison)
            .Assembly.Location;

        PoolBinaryComparisonReport report = PoolBinaryComparison.Run(
            assemblyPath,
            assemblyPath,
            sampleCount: 8,
            warmupIterations: 12_800,
            measuredIterations: 102_400);

        Assert.True(report.ExactParity);
        Assert.True(report.Calibration.BalancedOrder);
        Assert.True(report.Comparison.BalancedOrder);
        Assert.True(report.ZeroManagedAllocation);
        Assert.True(report.ZeroFreshSegments);
        Assert.True(report.HarnessIdentityPassed);
        Assert.True(report.RuntimeIdentityPassed);
        Assert.False(report.ValidEvidence);
        Assert.Equal(PoolBinaryDecision.Invalid, report.Decision);
    }

    private static PoolBinaryPairEvidence[] CreatePairs(
        double baselineWall,
        double baselineProcessor,
        double candidateWall,
        double candidateProcessor)
    {
        var result = new PoolBinaryPairEvidence[8];
        for (int index = 0; index < result.Length; index++)
        {
            PoolExactHeadProbeReport baseline = CreateResult(
                baselineWall,
                baselineProcessor);
            PoolExactHeadProbeReport candidate = CreateResult(
                candidateWall,
                candidateProcessor);
            result[index] = new PoolBinaryPairEvidence(
                index,
                PoolBinaryComparison.GetFirstVariant(index),
                baseline,
                candidate,
                ExactParity: true,
                baselineWall / candidateWall,
                baselineProcessor / candidateProcessor);
        }

        return result;
    }

    private static PoolExactHeadProbeReport CreateResult(
        double wall,
        double processor) =>
        new(
            WarmupIterations: 1,
            MeasuredIterations: 1,
            ReservedBytes: 1,
            ElapsedMilliseconds: wall,
            ProcessorMilliseconds: processor,
            MeasuredOperationsPerSecond: 1,
            MeasuredProcessorOperationsPerSecond: 1,
            ManagedAllocatedBytes: 0,
            FreshSegmentAllocationDelta: 0,
            RetainedBytes: 1,
            WarmupChecksum: 1,
            MeasuredChecksum: 1);
}
