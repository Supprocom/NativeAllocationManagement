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
    [InlineData(NativeBuilderBenchmarkImplementation.NativeBuilderBudgeted)]
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
        long nativeOwnersPerPacket = implementation is NativeBuilderBenchmarkImplementation.NativeBuilder
            or NativeBuilderBenchmarkImplementation.NativeBuilderBudgeted ? 2 : 0;
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
            NativeBuilderBenchmarkImplementation.NativeBuilderBudgeted, CreateOptions() with { ElementCount = 16 });
        JsonObject document = JsonSerializer.SerializeToNode(evidence)!.AsObject();
        List<string> paths = [];
        CollectMeasurementPaths(document[nameof(NativeBuilderWorkerEvidence.LifecycleEvidence)]!.AsObject(),
            nameof(NativeBuilderWorkerEvidence.LifecycleEvidence), paths);
        Assert.Equal(293, paths.Count);
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
        Assert.Equal(NativeBuilderBenchmarkImplementation.NativeBuilderBudgeted, CreateOptions().NativeBaseline);
        Assert.Equal(long.MaxValue, CreateOptions().NativeBudgetCapacityBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeBuilderBenchmark.GetFirstImplementation(
            0, NativeBuilderBenchmarkImplementation.NativeBuilder));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeBuilderBenchmark.GetFirstImplementation(
            0, (NativeBuilderBenchmarkImplementation)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeBuilderBenchmark.GetFirstImplementation(
            0, NativeBuilderBenchmarkImplementation.ManagedExactArray, NativeBuilderBenchmarkImplementation.ManagedList));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeBuilderBenchmark.GetFirstImplementation(
            0, NativeBuilderBenchmarkImplementation.ManagedExactArray, (NativeBuilderBenchmarkImplementation)999));
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
        Assert.All(report.Pairs, pair =>
        {
            Assert.Equal(NativeBuilderBenchmarkImplementation.NativeBuilderBudgeted, pair.Native.Implementation);
            Assert.NotNull(pair.Native.LifecycleEvidence.MeasuredBatch.After.Budget);
        });
    }

    [Theory]
    [InlineData(1024, 1024, 256, 8192L, 0L, 0L)]
    [InlineData(17, 1, 4, 8L, 128L, 4L)]
    [InlineData(1, 0, 1, 16L, 0L, 1L)]
    public async Task BudgetedWorkerReportsActualDisjointRequestHistories(
        int elements, int preLease, int batchSize, long acquiredPerPacket, long replacementPerPacket, long reallocationsPerPacket)
    {
        NativeBuilderBenchmarkOptions options = CreateOptions() with
        {
            ElementCount = elements,
            PreLease = preLease,
            BatchSize = batchSize,
            WarmupIterations = 2,
            Iterations = 3,
            NativeBudgetCapacityBytes = 16384
        };
        NativeBuilderWorkerEvidence evidence = await NativeBuilderBenchmark.RunWorkerAsync(
            NativeBuilderBenchmarkImplementation.NativeBuilderBudgeted, options);
        NativeBuilderLifecycleEvidence lifecycle = evidence.LifecycleEvidence;
        Assert.Null(lifecycle.PreparationAndValidation.Before.Budget);
        Assert.NotNull(lifecycle.PreparationAndValidation.After.Budget);
        NativeBuilderBudgetObservation prepared = lifecycle.PreparationAndValidation.After.Budget.Value;
        Assert.True(prepared.BudgetId > 0);
        Assert.Equal(options.NativeBudgetCapacityBytes, prepared.CapacityBytes);
        AssertBudgetRequests(prepared, acquiredPerPacket, replacementPerPacket, reallocationsPerPacket);
        AssertBudgetPhaseRequests(lifecycle.Warmup, prepared, acquiredPerPacket, replacementPerPacket, reallocationsPerPacket, 2);
        AssertBudgetPhaseRequests(lifecycle.MeasuredBatch, lifecycle.Warmup.After.Budget!.Value,
            acquiredPerPacket, replacementPerPacket, reallocationsPerPacket, 3);
        AssertBudgetPhaseRequests(lifecycle.SeparatePhaseProbe, lifecycle.MeasuredBatch.After.Budget!.Value,
            acquiredPerPacket, replacementPerPacket, reallocationsPerPacket, 1);
        NativeBuilderWorkerEvidence restored = NativeBuilderBenchmark.DeserializeWorkerEvidence(JsonSerializer.Serialize(evidence));
        Assert.Equal(lifecycle, restored.LifecycleEvidence);
    }

    [Theory]
    [InlineData(NativeBuilderBenchmarkImplementation.NativeBuilder)]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedList)]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedListPrefix)]
    [InlineData(NativeBuilderBenchmarkImplementation.ManagedExactArray)]
    public async Task UnbudgetedWorkersExplicitlyReportUnavailableInsteadOfZero(NativeBuilderBenchmarkImplementation implementation)
    {
        NativeBuilderWorkerEvidence evidence = await NativeBuilderBenchmark.RunWorkerAsync(implementation,
            CreateOptions() with { ElementCount = 16 });
        NativeBuilderLifecycleEvidence lifecycle = evidence.LifecycleEvidence;
        AssertUnavailableBudget(lifecycle.PreparationAndValidation);
        AssertUnavailableBudget(lifecycle.Warmup);
        AssertUnavailableBudget(lifecycle.MeasuredBatch);
        AssertUnavailableBudget(lifecycle.SeparatePhaseProbe);
        Assert.Equal(lifecycle, NativeBuilderBenchmark.DeserializeWorkerEvidence(JsonSerializer.Serialize(evidence)).LifecycleEvidence);
    }

    [Fact]
    public async Task ActualIsolatedWorkerReceivesTheRequestedBudgetCeiling()
    {
        NativeBuilderBenchmarkOptions options = CreateOptions() with
        {
            ElementCount = 17,
            PreLease = 1,
            BatchSize = 4,
            NativeBudgetCapacityBytes = 1024
        };
        NativeBuilderWorkerEvidence evidence = await NativeBuilderBenchmark.RunIsolatedWorkerAsync(
            NativeBuilderBenchmarkImplementation.NativeBuilderBudgeted, options);
        Assert.Equal(NativeBuilderBenchmarkImplementation.NativeBuilderBudgeted, evidence.Implementation);
        Assert.NotNull(evidence.LifecycleEvidence.MeasuredBatch.After.Budget);
        Assert.Equal(1024, evidence.LifecycleEvidence.MeasuredBatch.After.Budget.Value.CapacityBytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeBuilderBenchmark.RunIsolatedWorkerAsync(
            NativeBuilderBenchmarkImplementation.NativeBuilderBudgeted, options with { NativeBudgetCapacityBytes = 0 }));
    }

    [Fact]
    public async Task BudgetRefusalDuringPreparationReleasesTheEarlierBuilder()
    {
        NativeBuilderBenchmarkOptions options = CreateOptions() with
        {
            ElementCount = 17,
            PreLease = 4,
            BatchSize = 4,
            NativeBudgetCapacityBytes = 16
        };
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        NativeMemoryBudgetExceededException failure = await Assert.ThrowsAsync<NativeMemoryBudgetExceededException>(
            () => NativeBuilderBenchmark.RunWorkerAsync(NativeBuilderBenchmarkImplementation.NativeBuilderBudgeted, options));
        NativeMemoryStatistics after = NativeMemoryDiagnostics.Snapshot();
        Assert.True(failure.BudgetId > 0);
        Assert.Equal(16, failure.CapacityBytes);
        Assert.Equal((nuint)16, failure.RequestedBytes);
        Assert.Equal(0, failure.AvailableBytes);
        Assert.Equal(before.OutstandingNativeBytes, after.OutstandingNativeBytes);
        Assert.Equal(before.DetachedNativeBytes, after.DetachedNativeBytes);
        Assert.Equal(before.RetiredNativeBytes, after.RetiredNativeBytes);
        Assert.Equal(1, after.AllocationCount - before.AllocationCount);
        Assert.Equal(1, after.FreeCount - before.FreeCount);
    }

    [Theory]
    [InlineData("BudgetId", 0L)]
    [InlineData("CapacityBytes", -1L)]
    [InlineData("CommittedBytes", 1L)]
    [InlineData("ReservedBytes", 1L)]
    [InlineData("ActiveAllocationCount", 1L)]
    [InlineData("PeakCommittedBytes", -1L)]
    [InlineData("PeakAdmittedBytes", -1L)]
    [InlineData("AllocationCount", -1L)]
    [InlineData("ReallocationCount", -1L)]
    [InlineData("FreeCount", -1L)]
    [InlineData("AcquiredBackingBytes", -1L)]
    [InlineData("ReplacementBackingBytes", -1L)]
    [InlineData("RejectedAllocationCount", 1L)]
    [InlineData("FailedAllocationCount", 1L)]
    [InlineData("TraceCapacity", 1L)]
    [InlineData("TraceCount", 1L)]
    [InlineData("DroppedTraceEventCount", 1L)]
    public async Task InvalidBudgetMeasurementsCannotEnterTheActualReader(string field, long invalidValue)
    {
        NativeBuilderWorkerEvidence evidence = await NativeBuilderBenchmark.RunWorkerAsync(
            NativeBuilderBenchmarkImplementation.NativeBuilderBudgeted, CreateOptions() with { ElementCount = 16 });
        JsonObject document = JsonSerializer.SerializeToNode(evidence)!.AsObject();
        JsonObject budget = document[nameof(NativeBuilderWorkerEvidence.LifecycleEvidence)]!
            [nameof(NativeBuilderLifecycleEvidence.PreparationAndValidation)]!
            [nameof(NativeBuilderLifecyclePhaseEvidence.After)]![nameof(NativeBuilderWorkerObservation.Budget)]!.AsObject();
        budget[field] = invalidValue;
        string invalid = document.ToJsonString();
        Assert.Throws<InvalidDataException>(() => NativeBuilderBenchmark.DeserializeWorkerEvidence(invalid));
    }

    [Fact]
    public async Task FalseAvailabilityAndSaturationCannotEnterTheActualReader()
    {
        NativeBuilderWorkerEvidence evidence = await NativeBuilderBenchmark.RunWorkerAsync(
            NativeBuilderBenchmarkImplementation.NativeBuilderBudgeted, CreateOptions() with { ElementCount = 16 });
        NativeBuilderLifecycleEvidence lifecycle = evidence.LifecycleEvidence;
        NativeBuilderWorkerObservation prepared = lifecycle.PreparationAndValidation.After;
        Assert.NotNull(prepared.Budget);
        NativeBuilderBudgetObservation budget = prepared.Budget.Value;
        AssertInvalidWorker(evidence with { Implementation = (NativeBuilderBenchmarkImplementation)999 });
        AssertInvalidWorker(evidence with { Implementation = NativeBuilderBenchmarkImplementation.NativeBuilder });
        AssertInvalidPreparedBudget(evidence, null);
        AssertInvalidPreparedBudget(evidence, budget with { TraceOverflowed = true });
        AssertInvalidPreparedBudget(evidence, budget with { HistoryOverflowed = true });
        AssertInvalidWorker(evidence with
        {
            LifecycleEvidence = lifecycle with
            {
                PreparationAndValidation = lifecycle.PreparationAndValidation with
                {
                    Before = lifecycle.PreparationAndValidation.Before with { Budget = budget }
                }
            }
        });
        AssertInvalidWorker(evidence with
        {
            LifecycleEvidence = lifecycle with
            {
                MeasuredBatch = lifecycle.MeasuredBatch with
                {
                    After = lifecycle.MeasuredBatch.After with { Budget = budget with { BudgetId = budget.BudgetId + 1 } }
                }
            }
        });
        AssertInvalidWorker(evidence with
        {
            LifecycleEvidence = lifecycle with
            {
                MeasuredBatch = lifecycle.MeasuredBatch with
                {
                    After = lifecycle.MeasuredBatch.After with { Budget = budget with { CapacityBytes = 1024 } }
                }
            }
        });
    }

    private static void AssertInvalidPreparedBudget(NativeBuilderWorkerEvidence evidence, NativeBuilderBudgetObservation? budget)
        => AssertInvalidWorker(evidence with
        {
            LifecycleEvidence = evidence.LifecycleEvidence with
            {
                PreparationAndValidation = evidence.LifecycleEvidence.PreparationAndValidation with
                {
                    After = evidence.LifecycleEvidence.PreparationAndValidation.After with { Budget = budget }
                }
            }
        });

    private static void AssertInvalidWorker(NativeBuilderWorkerEvidence evidence)
    {
        string invalid = JsonSerializer.Serialize(evidence);
        Assert.Throws<InvalidDataException>(() => NativeBuilderBenchmark.DeserializeWorkerEvidence(invalid));
    }

    private static void AssertUnavailableBudget(NativeBuilderLifecyclePhaseEvidence phase)
    {
        Assert.Null(phase.Before.Budget);
        Assert.Null(phase.After.Budget);
    }

    private static void AssertBudgetPhaseRequests(NativeBuilderLifecyclePhaseEvidence phase, NativeBuilderBudgetObservation previous,
        long acquiredPerPacket, long replacementPerPacket, long reallocationsPerPacket, int packetCount)
    {
        Assert.Equal(previous, phase.Before.Budget);
        Assert.NotNull(phase.After.Budget);
        NativeBuilderBudgetObservation after = phase.After.Budget.Value;
        Assert.Equal(previous.BudgetId, after.BudgetId);
        Assert.Equal(previous.CapacityBytes, after.CapacityBytes);
        AssertBudgetRequests(after, previous.AcquiredBackingBytes + acquiredPerPacket * packetCount,
            previous.ReplacementBackingBytes + replacementPerPacket * packetCount,
            previous.ReallocationCount + reallocationsPerPacket * packetCount);
    }

    private static void AssertBudgetRequests(NativeBuilderBudgetObservation budget, long acquired, long replacements, long reallocations)
    {
        Assert.Equal(acquired, budget.AcquiredBackingBytes);
        Assert.Equal(replacements, budget.ReplacementBackingBytes);
        Assert.Equal(reallocations, budget.ReallocationCount);
        Assert.Equal(0, budget.CommittedBytes);
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Equal(0, budget.ActiveAllocationCount);
        Assert.Equal(budget.AllocationCount, budget.FreeCount);
        Assert.Equal(0, budget.RejectedAllocationCount);
        Assert.Equal(0, budget.FailedAllocationCount);
        Assert.Equal(0, budget.TraceCapacity);
        Assert.Equal(0, budget.TraceCount);
        Assert.Equal(0, budget.DroppedTraceEventCount);
        Assert.False(budget.TraceOverflowed);
        Assert.False(budget.HistoryOverflowed);
        Assert.True(budget.PeakCommittedBytes > 0);
        Assert.True(budget.PeakAdmittedBytes >= budget.PeakCommittedBytes);
        Assert.True(budget.PeakAdmittedBytes <= budget.CapacityBytes);
    }

    [Fact]
    public void WorkerBudgetSchemaIncludesEveryImplementedRuntimeMeasurement()
    {
        string[] runtime = typeof(NativeMemoryBudgetStatistics).GetProperties()
            .Select(property => string.Equals(property.Name, nameof(NativeMemoryBudgetStatistics.Id), StringComparison.Ordinal)
                ? nameof(NativeBuilderBudgetObservation.BudgetId) : property.Name)
            .Order(StringComparer.Ordinal).ToArray();
        string[] worker = typeof(NativeBuilderBudgetObservation).GetProperties()
            .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(19, runtime.Length);
        Assert.Equal(runtime, worker);
    }

    [Theory]
    [InlineData(4, 16)]
    [InlineData(1, 8)]
    public async Task RefusedNativeBatchAwaitsConsumerCleanupBeforeReturning(int preLease, long capacity)
    {
        NativeBuilderBenchmarkOptions options = CreateOptions() with { ElementCount = 17, PreLease = preLease, BatchSize = 4 };
        NativeMemoryBudget budget = new(capacity);
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        await Assert.ThrowsAsync<NativeMemoryBudgetExceededException>(
            () => NativeBuilderBenchmark.RunNativeBatchAsync(options, 2, budget));
        NativeMemoryStatistics after = NativeMemoryDiagnostics.Snapshot();
        NativeMemoryBudgetStatistics ended = budget.CaptureStatistics();
        Assert.Equal(before.OutstandingNativeBytes, after.OutstandingNativeBytes);
        Assert.Equal(before.DetachedNativeBytes, after.DetachedNativeBytes);
        Assert.Equal(before.RetiredNativeBytes, after.RetiredNativeBytes);
        Assert.Equal(after.AllocationCount - before.AllocationCount, after.FreeCount - before.FreeCount);
        Assert.Equal(0, ended.CommittedBytes);
        Assert.Equal(0, ended.ReservedBytes);
        Assert.Equal(0, ended.ActiveAllocationCount);
        Assert.Equal(ended.AllocationCount, ended.FreeCount);
        Assert.Equal(preLease == 4 ? 1 : 2, ended.AllocationCount);
        Assert.Equal(1, ended.RejectedAllocationCount);
        Assert.Equal(0, ended.FailedAllocationCount);
        Assert.Equal(0, ended.ReplacementBackingBytes);
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
