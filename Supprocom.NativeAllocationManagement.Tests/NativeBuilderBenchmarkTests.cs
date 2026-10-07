using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;
using Supprocom.NativeAllocationManagement.Performance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeBuilderBenchmarkTests
{
    [Theory]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedList)]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedListPrefix)]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedExactArray)]
    [InlineData(NativeBuilderBenchmarkImplementation.NativeBuilder)]
    public async Task LifecycleCountersCoverActualDistinctWorkerInvocations(NativeBuilderBenchmarkImplementation implementation)
    {
        NativeBuilderBenchmarkOptions options = CreateOptions() with
        {
            ElementCount = 1024,
            PreLease = 1024,
            Iterations = 3,
            WarmupIterations = 2
        };
        NativeBuilderWorkerEvidence evidence = await NativeBuilderBenchmark.RunWorkerAsync(implementation, options);
        NativeBuilderLifecycleEvidence lifecycle = evidence.LifecycleEvidence;
        Assert.True(evidence.ExactParity);
        AssertPhase(lifecycle.PreparationAndValidation);
        AssertPhase(lifecycle.Warmup);
        AssertPhase(lifecycle.MeasuredBatch);
        AssertPhase(lifecycle.SeparatePhaseProbe);
        Assert.True(lifecycle.PreparationAndValidation.After.Timestamp <= lifecycle.Warmup.Before.Timestamp);
        Assert.True(lifecycle.Warmup.After.Timestamp <= lifecycle.MeasuredBatch.Before.Timestamp);
        Assert.True(lifecycle.MeasuredBatch.After.Timestamp <= lifecycle.SeparatePhaseProbe.Before.Timestamp);
        NativeBuilderWorkerObservation before = lifecycle.MeasuredBatch.Before;
        NativeBuilderWorkerObservation after = lifecycle.MeasuredBatch.After;
        Assert.Equal(after.ManagedAllocatedBytes - before.ManagedAllocatedBytes, evidence.ManagedAllocatedBytes);
        Assert.Equal(after.Gen0Collections - before.Gen0Collections, evidence.Gen0Collections);
        Assert.Equal(after.Gen1Collections - before.Gen1Collections, evidence.Gen1Collections);
        Assert.Equal(after.Gen2Collections - before.Gen2Collections, evidence.Gen2Collections);
        Assert.Equal(before.ManagedHeapBytes, evidence.ManagedHeapBeforeBytes);
        Assert.Equal(after.ManagedHeapBytes, evidence.ManagedHeapAfterBytes);
        Assert.Equal(after.Native.OutstandingBytes, evidence.NativeRetainedBytes);
        Assert.Equal(after.Native.AllocationCount - before.Native.AllocationCount, evidence.NativeFreshSegmentAllocationDelta);
        Assert.True(lifecycle.MeasuredBatch.ElapsedMilliseconds >= evidence.ElapsedMilliseconds);
        long nativeOwnersPerPacket = implementation == NativeBuilderBenchmarkImplementation.NativeBuilder ? 2 : 0;
        Assert.Equal(nativeOwnersPerPacket, NativeAcquisitions(lifecycle.PreparationAndValidation));
        Assert.Equal(nativeOwnersPerPacket * options.WarmupIterations, NativeAcquisitions(lifecycle.Warmup));
        Assert.Equal(nativeOwnersPerPacket * options.Iterations, NativeAcquisitions(lifecycle.MeasuredBatch));
        Assert.Equal(nativeOwnersPerPacket, NativeAcquisitions(lifecycle.SeparatePhaseProbe));
        NativeBuilderWorkerEvidence restored = JsonSerializer.Deserialize<NativeBuilderWorkerEvidence>(JsonSerializer.Serialize(evidence))!;
        Assert.Equal(lifecycle, restored.LifecycleEvidence);
    }

    [Fact]
    public async Task MissingLifecycleEvidenceCannotDeserializeAsZeroMeasurements()
    {
        NativeBuilderWorkerEvidence evidence = await NativeBuilderBenchmark.RunWorkerAsync(
            NativeBuilderBenchmarkImplementation.ManagedExactArray, CreateOptions() with { ElementCount = 16 });
        JsonObject document = JsonSerializer.SerializeToNode(evidence)!.AsObject();
        Assert.True(document.Remove(nameof(NativeBuilderWorkerEvidence.LifecycleEvidence)));
        string missingEvidence = document.ToJsonString();
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<NativeBuilderWorkerEvidence>(missingEvidence));
    }

    [Fact]
    public async Task EveryNestedLifecycleMeasurementIsRequiredOnTheWire()
    {
        NativeBuilderWorkerEvidence evidence = await NativeBuilderBenchmark.RunWorkerAsync(
            NativeBuilderBenchmarkImplementation.NativeBuilder, CreateOptions() with { ElementCount = 16 });
        JsonObject document = JsonSerializer.SerializeToNode(evidence)!.AsObject();
        List<string> paths = [];
        CollectMeasurementPaths(document[nameof(NativeBuilderWorkerEvidence.LifecycleEvidence)]!.AsObject(),
            nameof(NativeBuilderWorkerEvidence.LifecycleEvidence), paths);
        Assert.Equal(152, paths.Count);
        foreach (ref readonly string path in CollectionsMarshal.AsSpan(paths))
        {
            JsonObject missing = document.DeepClone().AsObject();
            string[] segments = path.Split('.');
            JsonObject parent = missing;
            foreach (ref readonly string segment in segments.AsSpan()[..^1])
                parent = parent[segment]!.AsObject();
            Assert.True(parent.Remove(segments[^1]));
            string incomplete = missing.ToJsonString();
            Assert.Throws<JsonException>(() => NativeBuilderBenchmark.DeserializeWorkerEvidence(incomplete));
        }
    }

    [Fact]
    public async Task ExplicitNullLifecycleCannotEnterTheActualWorkerReader()
    {
        NativeBuilderWorkerEvidence evidence = await NativeBuilderBenchmark.RunWorkerAsync(
            NativeBuilderBenchmarkImplementation.ManagedExactArray, CreateOptions() with { ElementCount = 16 });
        JsonObject document = JsonSerializer.SerializeToNode(evidence)!.AsObject();
        document[nameof(NativeBuilderWorkerEvidence.LifecycleEvidence)] = null;
        string missingEvidence = document.ToJsonString();
        Assert.Throws<InvalidDataException>(() => NativeBuilderBenchmark.DeserializeWorkerEvidence(missingEvidence));
    }

    private static void CollectMeasurementPaths(JsonObject value, string prefix, List<string> paths)
    {
        foreach ((string name, JsonNode? node) in value)
        {
            string path = $"{prefix}.{name}";
            paths.Add(path);
            if (node is JsonObject nested)
                CollectMeasurementPaths(nested, path, paths);
        }
    }

    private static long NativeAcquisitions(NativeBuilderLifecyclePhaseEvidence phase)
        => phase.After.Native.AllocationCount - phase.Before.Native.AllocationCount;

    private static void AssertPhase(NativeBuilderLifecyclePhaseEvidence phase)
    {
        Assert.True(phase.ElapsedMilliseconds > 0);
        Assert.True(phase.After.Timestamp >= phase.Before.Timestamp);
        Assert.True(phase.After.ProcessCpuTicks >= phase.Before.ProcessCpuTicks);
        Assert.True(phase.After.ManagedAllocatedBytes >= phase.Before.ManagedAllocatedBytes);
        Assert.Equal(phase.Before.Native.MetricsEpoch, phase.After.Native.MetricsEpoch);
        Assert.False(phase.Before.Native.HistoryOverflowed);
        Assert.False(phase.After.Native.HistoryOverflowed);
        Assert.Equal(phase.Before.Native.OutstandingBytes, phase.After.Native.OutstandingBytes);
        Assert.Equal(phase.Before.Native.DetachedBytes, phase.After.Native.DetachedBytes);
        Assert.Equal(phase.Before.Native.RetiredBytes, phase.After.Native.RetiredBytes);
        Assert.Equal(phase.Before.Native.ReallocationCount, phase.After.Native.ReallocationCount);
        Assert.Equal(NativeAcquisitions(phase), phase.After.Native.FreeCount - phase.Before.Native.FreeCount);
    }

    [Theory]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedListPrefix, 1)]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedListPrefix, 3)]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedListPrefix, 17)]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedListPrefix, 8192)]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedExactArray, 1)]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedExactArray, 3)]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedExactArray, 17)]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedExactArray, 8192)]
    public void CopyAvoidingManagedOutputsMatchTheIndependentMaterializedOracle(
        NativeBuilderBenchmarkImplementation implementation, int count)
    {
        NativeBuilderBenchmarkOptions options = CreateOptions() with { ElementCount = count, PreLease = 0 };
        NativeBuilderExactOutput expected = NativeBuilderBenchmark.BuildManagedOutput(options);
        NativeBuilderExactOutput actual = NativeBuilderBenchmark.BuildManagedOutput(options, implementation);
        Assert.Equal(expected.Opaque, actual.Opaque);
        Assert.Equal(expected.Transparent, actual.Transparent);
    }

    [Theory]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedList)]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedListPrefix)]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedExactArray)]
    public void SelectedManagedBaselineOwnsEveryOtherFirstPosition(NativeBuilderBenchmarkImplementation implementation)
    {
        for (int index = 0; index < 10; index++)
            Assert.Equal((index & 1) == 0 ? implementation : NativeBuilderBenchmarkImplementation.NativeBuilder,
                NativeBuilderBenchmark.GetFirstImplementation(index, implementation));
    }

    [Fact]
    public void KnownSizingIsTheExplicitDefaultAndInvalidSelectionCannotStartWorkers()
    {
        Assert.Equal(NativeBuilderBenchmarkImplementation.ManagedExactArray, CreateOptions().ManagedBaseline);
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeBuilderBenchmark.GetFirstImplementation(
            0, NativeBuilderBenchmarkImplementation.NativeBuilder));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeBuilderBenchmark.GetFirstImplementation(
            0, (NativeBuilderBenchmarkImplementation)999));
    }

    [Theory]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedListPrefix)]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedExactArray)]
    public async Task CopyAvoidingWorkersPublishTheSameActualChannelOutput(NativeBuilderBenchmarkImplementation implementation)
    {
        NativeBuilderBenchmarkOptions options = CreateOptions() with { ManagedBaseline = implementation };
        NativeBuilderWorkerEvidence managed = await NativeBuilderBenchmark.RunIsolatedWorkerAsync(implementation, options);
        NativeBuilderWorkerEvidence native = await NativeBuilderBenchmark.RunIsolatedWorkerAsync(
            NativeBuilderBenchmarkImplementation.NativeBuilder, options);
        Assert.Equal(implementation, managed.Implementation);
        Assert.True(managed.ExactParity);
        Assert.True(native.ExactParity);
        Assert.Equal(managed.ExactOutputSha256, native.ExactOutputSha256);
        Assert.Equal(managed.Checksum, native.Checksum);
        Assert.Equal(managed.LogicalBytes, native.LogicalBytes);
        Assert.Equal(0, managed.NativeFreshSegmentAllocationDelta);
        Assert.Equal(0, managed.NativeRetainedBytes);
        Assert.Equal(0, native.NativeRetainedBytes);
    }

    [Fact]
    public async Task PairedDefaultActuallySelectsTheCopyAvoidingKnownSizeWorker()
    {
        NativeBuilderBenchmarkReport report = await NativeBuilderBenchmark.RunPairedAsync(CreateOptions());
        Assert.True(report.ExactParity);
        Assert.True(report.BalancedOrder);
        Assert.All(report.Pairs, pair => Assert.Equal(NativeBuilderBenchmarkImplementation.ManagedExactArray, pair.Managed.Implementation));
    }

    [Fact]
    public void NativeBuilderOutputMatchesListAndToArray()
    {
        NativeBuilderBenchmarkOptions options = CreateOptions();
        NativeBuilderExactOutput managed =
            NativeBuilderBenchmark.BuildManagedOutput(options);
        NativeBuilderExactOutput native =
            NativeBuilderBenchmark.BuildNativeOutput(options);

        Assert.Equal(
            options.ElementCount,
            managed.Opaque.Length + managed.Transparent.Length);
        Assert.Equal(managed.Opaque, native.Opaque);
        Assert.Equal(managed.Transparent, native.Transparent);
    }

    [Fact]
    public async Task WorkersProduceEquivalentMeasuredEvidence()
    {
        NativeBuilderBenchmarkOptions options = CreateOptions();

        NativeBuilderWorkerEvidence managed =
            await NativeBuilderBenchmark.RunIsolatedWorkerAsync(
                NativeBuilderBenchmarkImplementation.ManagedList,
                options);
        NativeBuilderWorkerEvidence native =
            await NativeBuilderBenchmark.RunIsolatedWorkerAsync(
                NativeBuilderBenchmarkImplementation.NativeBuilder,
                options);

        Assert.True(managed.ExactParity);
        Assert.True(native.ExactParity);
        Assert.Equal(
            managed.ExactOutputSha256,
            native.ExactOutputSha256);
        Assert.Equal(managed.Checksum, native.Checksum);
        Assert.Equal(managed.LogicalBytes, native.LogicalBytes);
        Assert.True(
            native.ManagedAllocatedBytes
                < managed.ManagedAllocatedBytes);
        Assert.True(native.NativeFreshSegmentAllocationDelta > 0);
        Assert.Equal(0, native.NativeRetainedBytes);
        Assert.Equal(
            options.ElementCount,
            native.OpaqueElementCount
                + native.TransparentElementCount);
        Assert.True(native.PhaseEvidence.TotalMilliseconds > 0);
        Assert.True(managed.PhaseEvidence.TotalMilliseconds > 0);
    }

    [Fact]
    public void EmptyTransparentOutputKeepsExactParity()
    {
        NativeBuilderBenchmarkOptions options = CreateOptions() with
        {
            ElementCount = 3,
            PreLease = 1,
            BatchSize = 1
        };
        NativeBuilderExactOutput managed =
            NativeBuilderBenchmark.BuildManagedOutput(options);
        NativeBuilderExactOutput native =
            NativeBuilderBenchmark.BuildNativeOutput(options);

        Assert.Equal(3, managed.Opaque.Length);
        Assert.Empty(managed.Transparent);
        Assert.Equal(managed.Opaque, native.Opaque);
        Assert.Equal(managed.Transparent, native.Transparent);
    }

    [Fact]
    public async Task PairedBenchmarkRejectsAnOddSampleCount()
    {
        NativeBuilderBenchmarkOptions options = CreateOptions() with
        {
            SampleCount = 3
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => NativeBuilderBenchmark.RunPairedAsync(options));
    }

    [Fact]
    public void PairedBenchmarkBalancesFirstPosition()
    {
        NativeBuilderBenchmarkImplementation[] order =
            Enumerable.Range(0, 10)
                .Select(NativeBuilderBenchmark.GetFirstImplementation)
                .ToArray();

        Assert.Equal(
            5,
            order.Count(value =>
                value
                    == NativeBuilderBenchmarkImplementation.ManagedList));
        Assert.Equal(
            5,
            order.Count(value =>
                value
                    == NativeBuilderBenchmarkImplementation.NativeBuilder));
        Assert.Equal(
            NativeBuilderBenchmarkImplementation.ManagedList,
            order[0]);
        Assert.Equal(
            NativeBuilderBenchmarkImplementation.NativeBuilder,
            order[1]);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861", Justification = "The one-shot statistical fixture stays local to its assertion.")]
    public void ConfidenceLowerUsesEveryPairedObservation()
    {
        double lower = PairedBenchmarkStatistics.ConfidenceLower95(
            new[] { 1.1, 1.2, 1.3, 1.4, 1.5, 1.6 });

        Assert.True(lower > 1d);
        Assert.True(lower < 1.35d);
    }

    private static NativeBuilderBenchmarkOptions CreateOptions() =>
        new(
            ElementCount: 8_192,
            PreLease: 64,
            BatchSize: 64,
            Iterations: 4,
            WarmupIterations: 8,
            SampleCount: 2,
            Seed: 0x71C3);
}
