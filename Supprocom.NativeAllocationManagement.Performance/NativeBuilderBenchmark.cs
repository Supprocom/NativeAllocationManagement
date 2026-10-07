using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class NativeBuilderBenchmark
{
    private const int DefaultElementCount = 262_144;
    private const int DefaultPreLease = 1_024;
    private const int DefaultBatchSize = 256;
    private const int DefaultIterations = 128;
    private const int DefaultWarmupIterations = 16;
    private const int DefaultSampleCount = 10;
    private const int DefaultSeed = 0x71C3;
    private static readonly JsonSerializerOptions CompactJsonOptions =
        CreateJsonOptions(writeIndented: false);
    private static readonly JsonSerializerOptions IndentedJsonOptions =
        CreateJsonOptions(writeIndented: true);
    private static long _sink;

    internal static async Task<int> RunCommandAsync(string[] args)
    {
        if (string.Equals(args[0], "--native-builder-worker", StringComparison.Ordinal))
        {
            NativeBuilderBenchmarkImplementation implementation =
                Enum.Parse<NativeBuilderBenchmarkImplementation>(
                    ReadRequiredOption(args, "--implementation"),
                    ignoreCase: true);
            NativeBuilderBenchmarkOptions options = ParseOptions(args);
            NativeBuilderWorkerEvidence evidence = await RunWorkerAsync(
                implementation,
                options).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(
                evidence,
                CompactJsonOptions));
            return evidence.ExactParity ? 0 : 3;
        }

        NativeBuilderBenchmarkOptions benchmarkOptions =
            ParseOptions(args);
        string? outputPath = ReadOptionalOption(args, "--output");
        NativeBuilderBenchmarkReport report = await RunPairedAsync(
            benchmarkOptions).ConfigureAwait(false);
        string json = JsonSerializer.Serialize(
            report,
            IndentedJsonOptions);
        if (outputPath is not null)
        {
            string fullPath = Path.GetFullPath(outputPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, json).ConfigureAwait(false);
        }

        Console.WriteLine(json);
        return report.ExactParity && report.BalancedOrder ? 0 : 3;
    }

    internal static async Task<NativeBuilderBenchmarkReport> RunPairedAsync(
        NativeBuilderBenchmarkOptions options)
    {
        ValidateOptions(options);
        Stopwatch totalClock = Stopwatch.StartNew();
        NativeBuilderPairEvidence[] pairs =
            new NativeBuilderPairEvidence[options.SampleCount];
        for (int sampleIndex = 0;
            sampleIndex < options.SampleCount;
            sampleIndex++)
        {
            NativeBuilderBenchmarkImplementation first =
                GetFirstImplementation(sampleIndex, options.ManagedBaseline, options.NativeBaseline);
            NativeBuilderWorkerEvidence firstEvidence =
                await RunIsolatedWorkerAsync(first, options).ConfigureAwait(false);
            NativeBuilderBenchmarkImplementation second =
                first == options.ManagedBaseline
                    ? options.NativeBaseline
                    : options.ManagedBaseline;
            NativeBuilderWorkerEvidence secondEvidence =
                await RunIsolatedWorkerAsync(second, options).ConfigureAwait(false);
            NativeBuilderWorkerEvidence managed =
                first == options.ManagedBaseline
                    ? firstEvidence
                    : secondEvidence;
            NativeBuilderWorkerEvidence native =
                first == options.NativeBaseline
                    ? firstEvidence
                    : secondEvidence;
            ValidatePair(managed, native, options);
            pairs[sampleIndex] = new NativeBuilderPairEvidence(
                sampleIndex,
                first,
                managed,
                native,
                managed.ElapsedMilliseconds
                    / native.ElapsedMilliseconds);
        }

        totalClock.Stop();
        double managedMean = pairs.Average(
            pair => pair.Managed.ElapsedMilliseconds);
        double nativeMean = pairs.Average(
            pair => pair.Native.ElapsedMilliseconds);
        double[] speedups = pairs.Select(
            pair => pair.ManagedToNativeSpeedup).ToArray();
        double speedupMean = speedups.Average();
        double confidenceLower =
            PairedBenchmarkStatistics.ConfidenceLower95(speedups);
        bool parity = pairs.All(pair =>
            pair.Managed.ExactParity
            && pair.Native.ExactParity
            && string.Equals(pair.Managed.ExactOutputSha256
, pair.Native.ExactOutputSha256, StringComparison.Ordinal) && pair.Managed.Checksum == pair.Native.Checksum);
        bool balancedOrder = pairs.Count(pair =>
                pair.FirstImplementation
                    == options.ManagedBaseline)
            == options.SampleCount / 2
            && pairs.Count(pair =>
                pair.FirstImplementation
                    == options.NativeBaseline)
                == options.SampleCount / 2;
        return new NativeBuilderBenchmarkReport(
            options,
            pairs,
            managedMean,
            nativeMean,
            speedupMean,
            confidenceLower,
            pairs.Average(pair =>
                pair.Managed.LogicalGigabytesPerSecond),
            pairs.Average(pair =>
                pair.Native.LogicalGigabytesPerSecond),
            pairs.Average(pair =>
                (double)pair.Managed.ManagedAllocatedBytes),
            pairs.Average(pair =>
                (double)pair.Native.ManagedAllocatedBytes),
            pairs.Average(pair =>
                (double)pair.Managed.PeakWorkingSetBytes),
            pairs.Average(pair =>
                (double)pair.Native.PeakWorkingSetBytes),
            parity,
            balancedOrder,
            speedupMean > 1d && confidenceLower > 1d,
            totalClock.Elapsed.TotalMilliseconds,
            DateTimeOffset.UtcNow);
    }

    internal static async Task<NativeBuilderWorkerEvidence> RunWorkerAsync(
        NativeBuilderBenchmarkImplementation implementation,
        NativeBuilderBenchmarkOptions options)
    {
        ValidateOptions(options);
        if (!Enum.IsDefined(implementation))
            throw new ArgumentOutOfRangeException(nameof(implementation));
        using Process process = Process.GetCurrentProcess();
        // Initialize process/accounting observers before the first boundary on both sides.
        _ = CaptureWorkerObservation(process);
        NativeBuilderWorkerObservation setupBefore = CaptureWorkerObservation(process);
        Stopwatch setupClock = Stopwatch.StartNew();
        NativeMemoryBudget? budget = IsBudgetedImplementation(implementation)
            ? new NativeMemoryBudget(options.NativeBudgetCapacityBytes)
            : null;
        NativeBuilderExactOutput expected = BuildManagedOutput(options);
        string exactHash = ComputeExactHash(expected);
        long expectedChecksum = Consume(expected);
        bool exactParity;
        if (IsNativeImplementation(implementation))
        {
            NativeBuilderExactOutput nativeOutput =
                BuildNativeOutput(options, budget, implementation);
            exactParity = OutputsEqual(nativeOutput, expected);
        }
        else
        {
            exactParity = OutputsEqual(
                BuildManagedOutput(options, implementation),
                expected);
        }

        setupClock.Stop();
        NativeBuilderWorkerObservation setupAfter = CaptureWorkerObservation(process, afterWork: true, budget);
        NativeBuilderWorkerObservation warmupBefore = CaptureWorkerObservation(process, budget: budget);
        Stopwatch warmupClock = Stopwatch.StartNew();
        NativeBuilderBatchResult warmup = !IsNativeImplementation(implementation)
                ? await RunManagedBatchAsync(
                    options,
                    options.WarmupIterations,
                    implementation).ConfigureAwait(false)
                : await RunNativeBatchAsync(
                    options,
                    options.WarmupIterations, budget, implementation).ConfigureAwait(false);
        warmupClock.Stop();
        NativeBuilderWorkerObservation warmupAfter = CaptureWorkerObservation(process, afterWork: true, budget);
        if (warmup.Checksum != unchecked(
            expectedChecksum * options.WarmupIterations))
        {
            throw new InvalidDataException(
                "The native builder warmup changed the output checksum.");
        }

        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        NativeMemoryTestMetrics statisticsBefore =
            NativeMemoryTestHooks.Snapshot();
        NativeBuilderWorkerObservation measuredBefore = CaptureWorkerObservation(process, budget: budget);
        long batchStart = Stopwatch.GetTimestamp();
        NativeBuilderBatchResult measured = !IsNativeImplementation(implementation)
                ? await RunManagedBatchAsync(
                    options,
                    options.Iterations,
                    implementation).ConfigureAwait(false)
                : await RunNativeBatchAsync(
                    options,
                    options.Iterations, budget, implementation).ConfigureAwait(false);
        double batchMilliseconds = ElapsedMilliseconds(batchStart);
        NativeBuilderWorkerObservation measuredAfter = CaptureWorkerObservation(process, afterWork: true, budget);
        NativeMemoryTestMetrics statistics = NativeMemoryTestHooks.Snapshot();
        process.Refresh();
        long workingSetAfter = process.WorkingSet64;
        long peakWorkingSet = process.PeakWorkingSet64;
        if (peakWorkingSet <= 0)
            throw new InvalidDataException("The runtime did not provide a usable process working-set high-water mark.");
        long managedAllocated = measuredAfter.ManagedAllocatedBytes - measuredBefore.ManagedAllocatedBytes;
        long expectedMeasuredChecksum = unchecked(
            expectedChecksum * options.Iterations);
        if (measured.Checksum != expectedMeasuredChecksum)
        {
            throw new InvalidDataException(
                "The measured native builder output checksum changed.");
        }

        NativeBuilderWorkerObservation probeBefore = CaptureWorkerObservation(process, budget: budget);
        long probeStart = Stopwatch.GetTimestamp();
        NativeBuilderPhaseEvidence phaseEvidence = !IsNativeImplementation(implementation)
                ? MeasureManagedPhases(options, implementation)
                : MeasureNativePhases(options, budget, implementation);
        double probeMilliseconds = ElapsedMilliseconds(probeStart);
        NativeBuilderWorkerObservation probeAfter = CaptureWorkerObservation(process, afterWork: true, budget);
        long logicalBytes = checked(
            (long)options.ElementCount
            * sizeof(uint)
            * options.Iterations);
        double elapsedMilliseconds = measured.ElapsedMilliseconds;
        Volatile.Write(ref _sink, measured.Checksum);
        (int opaqueCount, int transparentCount) =
            GetOutputCounts(options.ElementCount);
        return new NativeBuilderWorkerEvidence(
            implementation,
            options.ElementCount,
            opaqueCount,
            transparentCount,
            options.PreLease,
            options.BatchSize,
            options.Iterations,
            options.WarmupIterations,
            options.Seed,
            logicalBytes,
            setupClock.Elapsed.TotalMilliseconds,
            warmupClock.Elapsed.TotalMilliseconds,
            elapsedMilliseconds,
            PairedBenchmarkStatistics.LogicalGigabytesPerSecond(
                logicalBytes,
                elapsedMilliseconds),
            managedAllocated,
            measuredAfter.Gen0Collections - measuredBefore.Gen0Collections,
            measuredAfter.Gen1Collections - measuredBefore.Gen1Collections,
            measuredAfter.Gen2Collections - measuredBefore.Gen2Collections,
            measuredBefore.ManagedHeapBytes,
            measuredAfter.ManagedHeapBytes,
            workingSetBefore,
            workingSetAfter,
            peakWorkingSet,
            statistics.OutstandingNativeBytes,
            statistics.AllocationCount,
            statistics.AllocationCount
                - statisticsBefore.AllocationCount,
            measured.Checksum,
            exactHash,
            GetInformationalVersion(typeof(NativeConcurrentPool<>).Assembly),
            GetInformationalVersion(typeof(NativeBuilderBenchmark).Assembly),
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredCompilation") ?? "unset",
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredPGO") ?? "unset",
            Environment.ProcessorCount,
            System.Runtime.GCSettings.IsServerGC,
            phaseEvidence,
            exactParity)
        {
            LifecycleEvidence = new NativeBuilderLifecycleEvidence(
                new(setupBefore, setupAfter, setupClock.Elapsed.TotalMilliseconds),
                new(warmupBefore, warmupAfter, warmupClock.Elapsed.TotalMilliseconds),
                new(measuredBefore, measuredAfter, batchMilliseconds),
                new(probeBefore, probeAfter, probeMilliseconds))
        };
    }

    private static NativeBuilderWorkerObservation CaptureWorkerObservation(
        Process process, bool afterWork = false, NativeMemoryBudget? budget = null)
    {
        // CPU queries may allocate: begin GC after the query, end GC before it.
        // The process-wide counter also covers async work resumed on another thread.
        long allocated = afterWork ? GC.GetTotalAllocatedBytes(precise: true) : 0;
        long timestamp = afterWork ? Stopwatch.GetTimestamp() : 0;
        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        long heap = GC.GetGCMemoryInfo().HeapSizeBytes;
        NativeMemoryStatistics native = NativeMemoryDiagnostics.Snapshot();
        NativeBuilderBudgetObservation? budgetObservation = CaptureBudgetObservation(budget);
        long cpuTicks = process.TotalProcessorTime.Ticks;
        if (!afterWork)
        {
            allocated = GC.GetTotalAllocatedBytes(precise: true);
            timestamp = Stopwatch.GetTimestamp();
            gen0 = GC.CollectionCount(0);
            gen1 = GC.CollectionCount(1);
            gen2 = GC.CollectionCount(2);
            heap = GC.GetGCMemoryInfo().HeapSizeBytes;
        }
        return new NativeBuilderWorkerObservation(
            timestamp, cpuTicks, allocated, gen0, gen1, gen2, heap,
            new NativeBuilderNativeObservation(
                native.MetricsEpoch, native.AllocationCount, native.ReallocationCount,
                native.FreeCount, native.OutstandingNativeBytes, native.DetachedNativeBytes,
                native.RetiredNativeBytes, native.CopiedBytes, native.HistoryOverflowed))
        {
            Budget = budgetObservation
        };
    }

    private static NativeBuilderBudgetObservation? CaptureBudgetObservation(NativeMemoryBudget? budget)
    {
        if (budget is null)
            return null;
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        return new NativeBuilderBudgetObservation(
            snapshot.Id, snapshot.CapacityBytes, snapshot.CommittedBytes, snapshot.ReservedBytes,
            snapshot.PeakCommittedBytes, snapshot.PeakAdmittedBytes, snapshot.AllocationCount,
            snapshot.ReallocationCount, snapshot.FreeCount, snapshot.ActiveAllocationCount,
            snapshot.RejectedAllocationCount, snapshot.FailedAllocationCount, snapshot.TraceCapacity,
            snapshot.TraceCount, snapshot.DroppedTraceEventCount, snapshot.TraceOverflowed,
            snapshot.HistoryOverflowed, snapshot.AcquiredBackingBytes, snapshot.ReplacementBackingBytes);
    }

    internal static NativeBuilderBenchmarkImplementation
        GetFirstImplementation(int sampleIndex)
        => GetFirstImplementation(sampleIndex, NativeBuilderBenchmarkImplementation.ManagedList);

    internal static NativeBuilderBenchmarkImplementation
        GetFirstImplementation(int sampleIndex, NativeBuilderBenchmarkImplementation managedBaseline)
        => GetFirstImplementation(sampleIndex, managedBaseline, NativeBuilderBenchmarkImplementation.NativeBuilder);

    internal static NativeBuilderBenchmarkImplementation GetFirstImplementation(int sampleIndex,
        NativeBuilderBenchmarkImplementation managedBaseline, NativeBuilderBenchmarkImplementation nativeBaseline)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleIndex);
        ValidateManagedBaseline(managedBaseline);
        ValidateNativeBaseline(nativeBaseline);
        return (sampleIndex & 1) == 0
            ? managedBaseline
            : nativeBaseline;
    }

    internal static NativeBuilderExactOutput BuildManagedOutput(
        NativeBuilderBenchmarkOptions options)
    {
        (List<uint> opaque, List<uint> transparent) =
            CreateManagedLists(options);
        return new NativeBuilderExactOutput(
            opaque.ToArray(),
            transparent.ToArray());
    }

    internal static NativeBuilderExactOutput BuildNativeOutput(
        NativeBuilderBenchmarkOptions options, NativeMemoryBudget? budget = null,
        NativeBuilderBenchmarkImplementation implementation = NativeBuilderBenchmarkImplementation.NativeBuilder)
    {
        NativeBuilderVoxelPacket packet = CreateNativePacket(options, budget, implementation);
        try
        {
            return packet.CopyExactOutput();
        }
        finally
        {
            packet.Dispose();
        }
    }

    internal static NativeBuilderExactOutput BuildManagedOutput(
        NativeBuilderBenchmarkOptions options,
        NativeBuilderBenchmarkImplementation implementation)
    {
        ValidateManagedBaseline(implementation);
        return CreateManagedPacket(options, implementation).CopyExactOutput();
    }

    private static async Task<NativeBuilderBatchResult>
        RunManagedBatchAsync(
            NativeBuilderBenchmarkOptions options,
            int iterations,
            NativeBuilderBenchmarkImplementation implementation)
    {
        Channel<ManagedBuilderVoxelPacket> channel =
            Channel.CreateBounded<ManagedBuilderVoxelPacket>(
                new BoundedChannelOptions(1)
                {
                    SingleReader = true,
                    SingleWriter = true
                });
        TaskCompletionSource ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<long> consumer = Task.Run(async () =>
        {
            long checksum = 0;
            ready.SetResult();
            await foreach (ManagedBuilderVoxelPacket packet
                in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                checksum = unchecked(checksum + packet.Consume());
            }

            return checksum;
        });
        await ready.Task.ConfigureAwait(false);
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            for (int iteration = 0;
                iteration < iterations;
                iteration++)
            {
                ManagedBuilderVoxelPacket packet = CreateManagedPacket(options, implementation);
                await channel.Writer.WriteAsync(packet).ConfigureAwait(false);
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        long checksum = await consumer.ConfigureAwait(false);
        clock.Stop();
        return new NativeBuilderBatchResult(
            checksum,
            clock.Elapsed.TotalMilliseconds);
    }

    internal static async Task<NativeBuilderBatchResult>
        RunNativeBatchAsync(
            NativeBuilderBenchmarkOptions options,
            int iterations, NativeMemoryBudget? budget,
            NativeBuilderBenchmarkImplementation implementation = NativeBuilderBenchmarkImplementation.NativeBuilder)
    {
        Channel<NativeBuilderVoxelPacket> channel =
            Channel.CreateBounded<NativeBuilderVoxelPacket>(
                new BoundedChannelOptions(1)
                {
                    SingleReader = true,
                    SingleWriter = true
                });
        TaskCompletionSource ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<long> consumer = Task.Run(async () =>
        {
            long checksum = 0;
            ready.SetResult();
            await foreach (NativeBuilderVoxelPacket packet
                in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    checksum = unchecked(
                        checksum + packet.Consume());
                }
                finally
                {
                    packet.Dispose();
                }
            }

            return checksum;
        });
        await ready.Task.ConfigureAwait(false);
        Stopwatch clock = Stopwatch.StartNew();
        long checksum;
        try
        {
            for (int iteration = 0;
                iteration < iterations;
                iteration++)
            {
                NativeBuilderVoxelPacket? packet =
                    CreateNativePacket(options, budget, implementation);
                try
                {
                    await channel.Writer.WriteAsync(packet).ConfigureAwait(false);
                    packet = null;
                }
                finally
                {
                    packet?.Dispose();
                }
            }
        }
        finally
        {
            channel.Writer.TryComplete();
            // A producer refusal must not return while the consumer still owns queued packets.
            checksum = await consumer.ConfigureAwait(false);
        }

        clock.Stop();
        return new NativeBuilderBatchResult(
            checksum,
            clock.Elapsed.TotalMilliseconds);
    }

    private static ManagedBuilderVoxelPacket CreateManagedPacket(
        NativeBuilderBenchmarkOptions options,
        NativeBuilderBenchmarkImplementation implementation)
    {
        (int opaqueCount, int transparentCount) = GetOutputCounts(options.ElementCount);
        object opaque = AllocateManagedStorage(implementation, opaqueCount, options.PreLease);
        object transparent = AllocateManagedStorage(implementation, transparentCount, options.PreLease);
        InitializeManagedStorage(opaque, implementation, opaqueCount, options, transparentOutput: false);
        InitializeManagedStorage(transparent, implementation, transparentCount, options, transparentOutput: true);
        return PublishManagedPacket(opaque, transparent, implementation);
    }

    private static object AllocateManagedStorage(
        NativeBuilderBenchmarkImplementation implementation, int count, int preLease) =>
        implementation == NativeBuilderBenchmarkImplementation.ManagedExactArray
            ? GC.AllocateUninitializedArray<uint>(count)
            : new List<uint>(preLease);

    private static void InitializeManagedStorage(object storage,
        NativeBuilderBenchmarkImplementation implementation, int count,
        NativeBuilderBenchmarkOptions options, bool transparentOutput)
    {
        if (implementation != NativeBuilderBenchmarkImplementation.ManagedExactArray)
        {
            AppendToList((List<uint>)storage, count, options, transparentOutput);
            return;
        }

        Span<uint> values = (uint[])storage;
        for (int offset = 0; offset < count; offset += options.BatchSize)
        {
            int length = Math.Min(options.BatchSize, count - offset);
            FillBatch(values.Slice(offset, length), offset, options.Seed, transparentOutput);
        }
    }

    private static ManagedBuilderVoxelPacket PublishManagedPacket(object opaque,
        object transparent, NativeBuilderBenchmarkImplementation implementation) =>
        implementation == NativeBuilderBenchmarkImplementation.ManagedList
            ? new(ManagedBuilderVoxelPacket.ToUpload(((List<uint>)opaque).ToArray()),
                ManagedBuilderVoxelPacket.ToUpload(((List<uint>)transparent).ToArray()), implementation)
            : new(opaque, transparent, implementation);

    private static NativeBuilderVoxelPacket CreateNativePacket(
        NativeBuilderBenchmarkOptions options, NativeMemoryBudget? budget,
        NativeBuilderBenchmarkImplementation implementation)
    {
        (int opaqueCount, int transparentCount) = GetOutputCounts(options.ElementCount);
        bool direct = implementation == NativeBuilderBenchmarkImplementation.NativeBuilderBudgetedDirect;
        int opaqueCapacity = direct ? opaqueCount : options.PreLease;
        int transparentCapacity = direct ? transparentCount : options.PreLease;
        using NativeBuilder<uint> opaque = budget is null
            ? new(preLease: opaqueCapacity) : new(budget, preLease: opaqueCapacity);
        using NativeBuilder<uint> transparent = budget is null
            ? new(preLease: transparentCapacity) : new(budget, preLease: transparentCapacity);
        InitializeBuilder(
            opaque,
            opaqueCount,
            options, direct,
            transparentOutput: false);
        InitializeBuilder(
            transparent,
            transparentCount,
            options, direct,
            transparentOutput: true);
        return PublishNativePacket(opaque, transparent);
    }

    private static NativeBuilderVoxelPacket PublishNativePacket(
        NativeBuilder<uint> opaqueBuilder,
        NativeBuilder<uint> transparentBuilder)
    {
        NativeTransfer<uint>? opaque = null;
        NativeTransfer<uint>? transparent = null;
        try
        {
            opaque = opaqueBuilder.Complete();
            transparent = transparentBuilder.Complete();
            return new NativeBuilderVoxelPacket(
                NativeTransfer<uint>.Move(ref opaque),
                NativeTransfer<uint>.Move(ref transparent));
        }
        finally
        {
            opaque?.Dispose();
            transparent?.Dispose();
        }
    }

    private static (List<uint> Opaque, List<uint> Transparent)
        CreateManagedLists(NativeBuilderBenchmarkOptions options)
    {
        (int opaqueCount, int transparentCount) =
            GetOutputCounts(options.ElementCount);
        List<uint> opaque = new(options.PreLease);
        List<uint> transparent = new(options.PreLease);
        AppendToList(
            opaque,
            opaqueCount,
            options,
            transparentOutput: false);
        AppendToList(
            transparent,
            transparentCount,
            options,
            transparentOutput: true);
        return (opaque, transparent);
    }

    private static void AppendToList(
        List<uint> values,
        int elementCount,
        NativeBuilderBenchmarkOptions options,
        bool transparentOutput)
    {
        Span<uint> batch = stackalloc uint[options.BatchSize];
        for (int offset = 0;
            offset < elementCount;
            offset += options.BatchSize)
        {
            int length = Math.Min(
                options.BatchSize,
                elementCount - offset);
            Span<uint> current = batch[..length];
            FillBatch(
                current,
                offset,
                options.Seed,
                transparentOutput);
            AppendToList(values, current);
        }
    }

    private static void InitializeBuilder(
        NativeBuilder<uint> builder, int elementCount,
        NativeBuilderBenchmarkOptions options, bool direct, bool transparentOutput)
    {
        if (direct)
        {
            DirectBuilderWriteState state = new(options.BatchSize, options.Seed, transparentOutput);
            builder.Write<DirectBuilderWriteState, DirectBuilderWriteAction>(elementCount, in state);
            return;
        }

        AppendToBuilder(builder, elementCount, options, transparentOutput);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DirectBuilderWriteState(int BatchSize, int Seed, bool TransparentOutput);

    private readonly struct DirectBuilderWriteAction : INativeBuilderWriteAction<uint, DirectBuilderWriteState>
    {
        public static void Invoke(scoped NativeBuilderWriter<uint> writer, scoped in DirectBuilderWriteState state)
        {
            Span<uint> values = writer.AsSpan();
            for (int offset = 0; offset < values.Length; offset += state.BatchSize)
            {
                int length = Math.Min(state.BatchSize, values.Length - offset);
                FillBatch(values.Slice(offset, length), offset, state.Seed, state.TransparentOutput);
            }

            writer.Commit(values.Length);
        }
    }

    private static void AppendToBuilder(
        NativeBuilder<uint> builder,
        int elementCount,
        NativeBuilderBenchmarkOptions options,
        bool transparentOutput)
    {
        Span<uint> batch = stackalloc uint[options.BatchSize];
        for (int offset = 0;
            offset < elementCount;
            offset += options.BatchSize)
        {
            int length = Math.Min(
                options.BatchSize,
                elementCount - offset);
            Span<uint> current = batch[..length];
            FillBatch(
                current,
                offset,
                options.Seed,
                transparentOutput);
            builder.Append(current);
        }
    }

    private static void AppendToList(
        List<uint> values,
        ReadOnlySpan<uint> source)
    {
        int start = values.Count;
        CollectionsMarshal.SetCount(
            values,
            checked(start + source.Length));
        source.CopyTo(CollectionsMarshal.AsSpan(values)[start..]);
    }

    private static void FillBatch(
        Span<uint> destination,
        int offset,
        int seed,
        bool transparentOutput)
    {
        uint state = unchecked((uint)seed)
            ^ unchecked((uint)offset * 0x9E3779B9U)
            ^ (transparentOutput ? 0xA24BAED4U : 0x51A7C3E1U);
        for (int index = 0;
            index < destination.Length;
            index++)
        {
            uint absolute = unchecked((uint)(offset + index));
            state = BitOperations.RotateLeft(
                state ^ absolute ^ 0xA511E9B3U,
                13);
            state = unchecked(state * 0x85EBCA6BU + 0xC2B2AE35U);
            destination[index] = state ^ BitOperations.RotateRight(
                absolute,
                index & 31);
        }
    }

    private static (int Opaque, int Transparent) GetOutputCounts(
        int elementCount)
    {
        int transparent = elementCount / 4;
        return (elementCount - transparent, transparent);
    }

    private static bool OutputsEqual(
        NativeBuilderExactOutput left,
        NativeBuilderExactOutput right) =>
        left.Opaque.AsSpan().SequenceEqual(right.Opaque)
        && left.Transparent.AsSpan().SequenceEqual(right.Transparent);

    private static string ComputeExactHash(
        NativeBuilderExactOutput output)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            length,
            output.Opaque.Length);
        hash.AppendData(length);
        hash.AppendData(MemoryMarshal.AsBytes(output.Opaque.AsSpan()));
        BinaryPrimitives.WriteInt32LittleEndian(
            length,
            output.Transparent.Length);
        hash.AppendData(length);
        hash.AppendData(MemoryMarshal.AsBytes(
            output.Transparent.AsSpan()));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static long Consume(NativeBuilderExactOutput output) =>
        ConsumeUploads(
            MemoryMarshal.AsBytes(output.Opaque.AsSpan()),
            MemoryMarshal.AsBytes(output.Transparent.AsSpan()));

    private static long ConsumeUploads(
        ReadOnlySpan<byte> opaque,
        ReadOnlySpan<byte> transparent) =>
        unchecked(
            ConsumeUpload(opaque)
            + RotateLeft(
                ConsumeUpload(transparent),
                17)
            + opaque.Length
            + ((long)transparent.Length << 32));

    private static long ConsumeUpload(ReadOnlySpan<byte> upload)
    {
        ulong hash = 14695981039346656037UL;
        int offset = 0;
        for (;
            offset <= upload.Length - sizeof(ulong);
            offset += sizeof(ulong))
        {
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(
                upload.Slice(offset, sizeof(ulong)));
            hash = unchecked(
                BitOperations.RotateLeft(hash ^ value, 11)
                * 1099511628211UL);
        }

        for (; offset < upload.Length; offset++)
        {
            hash = unchecked(
                (hash ^ upload[offset]) * 1099511628211UL);
        }

        return unchecked((long)(hash ^ (ulong)upload.Length));
    }

    private static long RotateLeft(long value, int offset) =>
        unchecked((long)BitOperations.RotateLeft(
            unchecked((ulong)value),
            offset));

    private static NativeBuilderPhaseEvidence MeasureManagedPhases(
        NativeBuilderBenchmarkOptions options,
        NativeBuilderBenchmarkImplementation implementation)
    {
        long totalStart = Stopwatch.GetTimestamp();
        long phaseStart = Stopwatch.GetTimestamp();
        (int opaqueCount, int transparentCount) =
            GetOutputCounts(options.ElementCount);
        object opaque = AllocateManagedStorage(implementation, opaqueCount, options.PreLease);
        object transparent = AllocateManagedStorage(implementation, transparentCount, options.PreLease);
        Channel<ManagedBuilderVoxelPacket> channel =
            Channel.CreateBounded<ManagedBuilderVoxelPacket>(1);
        double allocation = ElapsedMilliseconds(phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        InitializeManagedStorage(opaque, implementation, opaqueCount, options, transparentOutput: false);
        InitializeManagedStorage(transparent, implementation, transparentCount, options, transparentOutput: true);
        double initialization = ElapsedMilliseconds(phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        ManagedBuilderVoxelPacket packet = PublishManagedPacket(opaque, transparent, implementation);
        double publication = ElapsedMilliseconds(phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        if (!channel.Writer.TryWrite(packet)
            || !channel.Reader.TryRead(out ManagedBuilderVoxelPacket received))
        {
            throw new InvalidOperationException(
                "The managed builder phase handoff failed.");
        }

        double handoff = ElapsedMilliseconds(phaseStart);
        phaseStart = Stopwatch.GetTimestamp();
        long checksum = received.Consume();
        double access = ElapsedMilliseconds(phaseStart);
        phaseStart = Stopwatch.GetTimestamp();
        GC.KeepAlive(received.Opaque);
        GC.KeepAlive(received.Transparent);
        double disposal = ElapsedMilliseconds(phaseStart);
        Volatile.Write(ref _sink, checksum);
        return new NativeBuilderPhaseEvidence(
            allocation,
            initialization,
            publication,
            handoff,
            access,
            disposal,
            ElapsedMilliseconds(totalStart));
    }

    private static NativeBuilderPhaseEvidence MeasureNativePhases(
        NativeBuilderBenchmarkOptions options, NativeMemoryBudget? budget,
        NativeBuilderBenchmarkImplementation implementation)
    {
        long totalStart = Stopwatch.GetTimestamp();
        long phaseStart = Stopwatch.GetTimestamp();
        (int opaqueCount, int transparentCount) = GetOutputCounts(options.ElementCount);
        bool direct = implementation == NativeBuilderBenchmarkImplementation.NativeBuilderBudgetedDirect;
        int opaqueCapacity = direct ? opaqueCount : options.PreLease;
        int transparentCapacity = direct ? transparentCount : options.PreLease;
        using NativeBuilder<uint> opaque = budget is null
            ? new(preLease: opaqueCapacity) : new(budget, preLease: opaqueCapacity);
        using NativeBuilder<uint> transparent = budget is null
            ? new(preLease: transparentCapacity) : new(budget, preLease: transparentCapacity);
        Channel<NativeBuilderVoxelPacket> channel =
            Channel.CreateBounded<NativeBuilderVoxelPacket>(1);
        double allocation = ElapsedMilliseconds(phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        InitializeBuilder(
            opaque,
            opaqueCount,
            options, direct,
            transparentOutput: false);
        InitializeBuilder(
            transparent,
            transparentCount,
            options, direct,
            transparentOutput: true);
        double initialization = ElapsedMilliseconds(phaseStart);

        NativeBuilderVoxelPacket? packet = null;
        NativeBuilderVoxelPacket? received = null;
        try
        {
            phaseStart = Stopwatch.GetTimestamp();
            packet = PublishNativePacket(opaque, transparent);
            double publication = ElapsedMilliseconds(phaseStart);

            phaseStart = Stopwatch.GetTimestamp();
            if (!channel.Writer.TryWrite(packet)
                || !channel.Reader.TryRead(out received))
            {
                throw new InvalidOperationException(
                    "The native builder phase handoff failed.");
            }

            packet = null;
            double handoff = ElapsedMilliseconds(phaseStart);
            phaseStart = Stopwatch.GetTimestamp();
            long checksum = received.Consume();
            double access = ElapsedMilliseconds(phaseStart);
            phaseStart = Stopwatch.GetTimestamp();
            received.Dispose();
            received = null;
            opaque.Dispose();
            transparent.Dispose();
            double disposal = ElapsedMilliseconds(phaseStart);
            Volatile.Write(ref _sink, checksum);
            return new NativeBuilderPhaseEvidence(
                allocation,
                initialization,
                publication,
                handoff,
                access,
                disposal,
                ElapsedMilliseconds(totalStart));
        }
        finally
        {
            packet?.Dispose();
            received?.Dispose();
        }
    }

    private static double ElapsedMilliseconds(long start) =>
        (Stopwatch.GetTimestamp() - start)
        * 1_000d
        / Stopwatch.Frequency;

    internal static async Task<NativeBuilderWorkerEvidence>
        RunIsolatedWorkerAsync(
            NativeBuilderBenchmarkImplementation implementation,
            NativeBuilderBenchmarkOptions options)
    {
        string processPath =
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? "dotnet";
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = processPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add(
            typeof(NativeBuilderBenchmark).Assembly.Location);

        AddWorkerArguments(
            process.StartInfo.ArgumentList,
            implementation,
            options);
        process.StartInfo.Environment["DOTNET_TieredCompilation"] = "0";
        process.StartInfo.Environment["DOTNET_TieredPGO"] = "0";
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "The native builder benchmark worker did not start.");
        }

        Task<string> outputTask =
            process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask =
            process.StandardError.ReadToEndAsync();
        Task exitTask = process.WaitForExitAsync();
        try
        {
            await exitTask.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await exitTask.ConfigureAwait(false);
            throw new TimeoutException(
                $"The {implementation} native builder worker exceeded 60 seconds.", exception);
        }

        await exitTask.ConfigureAwait(false);
        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The {implementation} native builder worker failed with exit code {process.ExitCode}: {error}");
        }

        return DeserializeWorkerEvidence(output.Trim());
    }

    internal static NativeBuilderWorkerEvidence DeserializeWorkerEvidence(string output)
    {
        NativeBuilderWorkerEvidence evidence = JsonSerializer.Deserialize<NativeBuilderWorkerEvidence>(output, CompactJsonOptions)
            ?? throw new InvalidDataException(
                "The native builder worker did not return evidence.");
        if (evidence.LifecycleEvidence is null)
            throw new InvalidDataException("The native builder worker did not report lifecycle evidence.");
        if (!Enum.IsDefined(evidence.Implementation))
            throw new InvalidDataException("The native builder worker reported an unknown implementation.");
        NativeBuilderLifecycleEvidence lifecycle = evidence.LifecycleEvidence;
        if (lifecycle.PreparationAndValidation.Before.Budget is not null)
            throw new InvalidDataException("The native builder budget was prepared outside the measured preparation phase.");
        bool budgeted = IsBudgetedImplementation(evidence.Implementation);
        NativeBuilderBudgetObservation? prepared = lifecycle.PreparationAndValidation.After.Budget;
        ValidateBudgetObservation(prepared, budgeted, previous: null);
        NativeBuilderBudgetObservation? warmed = ValidateBudgetPhase(lifecycle.Warmup, budgeted, prepared);
        NativeBuilderBudgetObservation? measured = ValidateBudgetPhase(lifecycle.MeasuredBatch, budgeted, warmed);
        _ = ValidateBudgetPhase(lifecycle.SeparatePhaseProbe, budgeted, measured);
        return evidence;
    }

    private static NativeBuilderBudgetObservation? ValidateBudgetPhase(
        NativeBuilderLifecyclePhaseEvidence phase, bool budgeted, NativeBuilderBudgetObservation? previous)
    {
        ValidateBudgetObservation(phase.Before.Budget, budgeted, previous);
        if (phase.Before.Budget != previous)
            throw new InvalidDataException("The native builder budget changed between quiescent phases.");
        ValidateBudgetObservation(phase.After.Budget, budgeted, phase.Before.Budget);
        return phase.After.Budget;
    }

    private static void ValidateBudgetObservation(NativeBuilderBudgetObservation? observation,
        bool budgeted, NativeBuilderBudgetObservation? previous)
    {
        if (!budgeted)
        {
            if (observation is not null)
                throw new InvalidDataException("The unbudgeted worker reported an invented budget domain.");
            return;
        }
        if (observation is not { } snapshot)
            throw new InvalidDataException("The budgeted native builder worker did not report its budget domain.");
        if (snapshot.BudgetId <= 0 || snapshot.CapacityBytes < 0
            || snapshot.CommittedBytes != 0 || snapshot.ReservedBytes != 0 || snapshot.ActiveAllocationCount != 0
            || snapshot.PeakCommittedBytes < 0 || snapshot.PeakAdmittedBytes < snapshot.PeakCommittedBytes
            || snapshot.PeakAdmittedBytes > snapshot.CapacityBytes
            || snapshot.AllocationCount < 0 || snapshot.ReallocationCount < 0 || snapshot.FreeCount != snapshot.AllocationCount
            || snapshot.AcquiredBackingBytes < 0 || snapshot.ReplacementBackingBytes < 0
            || snapshot.RejectedAllocationCount != 0 || snapshot.FailedAllocationCount != 0
            || snapshot.TraceCapacity != 0 || snapshot.TraceCount != 0 || snapshot.DroppedTraceEventCount != 0
            || snapshot.TraceOverflowed || snapshot.HistoryOverflowed)
            throw new InvalidDataException("The native builder budget observation is not a complete successful quiescent measurement.");
        if (previous is { } prior && (snapshot.BudgetId != prior.BudgetId
            || snapshot.CapacityBytes != prior.CapacityBytes || snapshot.PeakCommittedBytes < prior.PeakCommittedBytes
            || snapshot.PeakAdmittedBytes < prior.PeakAdmittedBytes || snapshot.AllocationCount < prior.AllocationCount
            || snapshot.ReallocationCount < prior.ReallocationCount || snapshot.FreeCount < prior.FreeCount
            || snapshot.AcquiredBackingBytes < prior.AcquiredBackingBytes
            || snapshot.ReplacementBackingBytes < prior.ReplacementBackingBytes))
            throw new InvalidDataException("The native builder budget identity or history changed incompatibly.");
    }

    private static void AddWorkerArguments(
        Collection<string> arguments,
        NativeBuilderBenchmarkImplementation implementation,
        NativeBuilderBenchmarkOptions options)
    {
        arguments.Add("--native-builder-worker");
        arguments.Add("--implementation");
        arguments.Add(implementation.ToString());
        arguments.Add("--elements");
        arguments.Add(options.ElementCount.ToString(
            CultureInfo.InvariantCulture));
        arguments.Add("--prelease");
        arguments.Add(options.PreLease.ToString(
            CultureInfo.InvariantCulture));
        arguments.Add("--batch-size");
        arguments.Add(options.BatchSize.ToString(
            CultureInfo.InvariantCulture));
        arguments.Add("--iterations");
        arguments.Add(options.Iterations.ToString(
            CultureInfo.InvariantCulture));
        arguments.Add("--warmup");
        arguments.Add(options.WarmupIterations.ToString(
            CultureInfo.InvariantCulture));
        arguments.Add("--samples");
        arguments.Add(options.SampleCount.ToString(
            CultureInfo.InvariantCulture));
        arguments.Add("--seed");
        arguments.Add(options.Seed.ToString(
            CultureInfo.InvariantCulture));
        arguments.Add("--managed-baseline");
        arguments.Add(options.ManagedBaseline.ToString());
        arguments.Add("--native-baseline");
        arguments.Add(options.NativeBaseline.ToString());
        arguments.Add("--native-budget-bytes");
        arguments.Add(options.NativeBudgetCapacityBytes.ToString(CultureInfo.InvariantCulture));
    }

    private static void ValidatePair(
        NativeBuilderWorkerEvidence managed,
        NativeBuilderWorkerEvidence native,
        NativeBuilderBenchmarkOptions options)
    {
        if (managed.Implementation
                != options.ManagedBaseline
            || native.Implementation
                != options.NativeBaseline
            || managed.ElementCount != options.ElementCount
            || native.ElementCount != options.ElementCount
            || managed.PreLease != options.PreLease
            || native.PreLease != options.PreLease
            || managed.BatchSize != options.BatchSize
            || native.BatchSize != options.BatchSize
            || managed.Iterations != options.Iterations
            || native.Iterations != options.Iterations
            || (IsBudgetedImplementation(options.NativeBaseline)
                && native.LifecycleEvidence.PreparationAndValidation.After.Budget?.CapacityBytes != options.NativeBudgetCapacityBytes)
            || !managed.ExactParity
            || !native.ExactParity
            || !string.Equals(managed.ExactOutputSha256
, native.ExactOutputSha256, StringComparison.Ordinal) || managed.Checksum != native.Checksum)
        {
            throw new InvalidDataException(
                "The paired native builder evidence is not equivalent.");
        }
    }

    private static NativeBuilderBenchmarkOptions ParseOptions(
        string[] args)
    {
        NativeBuilderBenchmarkOptions options = new(
            ReadInt32Option(
                args,
                "--elements",
                DefaultElementCount),
            ReadInt32Option(
                args,
                "--prelease",
                DefaultPreLease),
            ReadInt32Option(
                args,
                "--batch-size",
                DefaultBatchSize),
            ReadInt32Option(
                args,
                "--iterations",
                DefaultIterations),
            ReadInt32Option(
                args,
                "--warmup",
                DefaultWarmupIterations),
            ReadInt32Option(
                args,
                "--samples",
                DefaultSampleCount),
            ReadInt32Option(
                args,
                "--seed",
                DefaultSeed))
        {
            ManagedBaseline = Enum.Parse<NativeBuilderBenchmarkImplementation>(
                ReadOptionalOption(args, "--managed-baseline")
                    ?? nameof(NativeBuilderBenchmarkImplementation.ManagedExactArray),
                ignoreCase: true),
            NativeBaseline = Enum.Parse<NativeBuilderBenchmarkImplementation>(
                ReadOptionalOption(args, "--native-baseline")
                    ?? nameof(NativeBuilderBenchmarkImplementation.NativeBuilderBudgeted),
                ignoreCase: true),
            NativeBudgetCapacityBytes = ReadInt64Option(args, "--native-budget-bytes", long.MaxValue)
        };
        ValidateOptions(options);
        return options;
    }

    private static void ValidateOptions(
        NativeBuilderBenchmarkOptions options)
    {
        ValidateManagedBaseline(options.ManagedBaseline);
        ValidateNativeBaseline(options.NativeBaseline);
        ArgumentOutOfRangeException.ThrowIfNegative(options.NativeBudgetCapacityBytes, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.ElementCount, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(
            options.PreLease, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.BatchSize, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            options.BatchSize,
            1_024, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.Iterations, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.WarmupIterations, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.SampleCount, nameof(options));
        if ((options.SampleCount & 1) != 0)
        {
            throw new ArgumentException(
                "The native builder sample count must be even.",
                nameof(options));
        }
    }

    private static void ValidateManagedBaseline(NativeBuilderBenchmarkImplementation implementation)
    {
        if (implementation is not (NativeBuilderBenchmarkImplementation.ManagedList
            or NativeBuilderBenchmarkImplementation.ManagedListPrefix
            or NativeBuilderBenchmarkImplementation.ManagedExactArray))
            throw new ArgumentOutOfRangeException(nameof(implementation), implementation, "Select a managed builder baseline.");
    }

    private static bool IsNativeImplementation(NativeBuilderBenchmarkImplementation implementation)
        => implementation == NativeBuilderBenchmarkImplementation.NativeBuilder || IsBudgetedImplementation(implementation);

    private static bool IsBudgetedImplementation(NativeBuilderBenchmarkImplementation implementation)
        => implementation is NativeBuilderBenchmarkImplementation.NativeBuilderBudgeted
            or NativeBuilderBenchmarkImplementation.NativeBuilderBudgetedDirect;

    private static void ValidateNativeBaseline(NativeBuilderBenchmarkImplementation implementation)
    {
        if (!IsNativeImplementation(implementation))
            throw new ArgumentOutOfRangeException(nameof(implementation), implementation, "Select a native builder baseline.");
    }

    private static long ReadInt64Option(string[] args, string name, long defaultValue)
    {
        string? value = ReadOptionalOption(args, name);
        return value is null ? defaultValue : long.Parse(value, CultureInfo.InvariantCulture);
    }

    private static int ReadInt32Option(
        string[] args,
        string name,
        int defaultValue)
    {
        string? value = ReadOptionalOption(args, name);
        return value is null
            ? defaultValue
            : int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string ReadRequiredOption(
        string[] args,
        string name) =>
        ReadOptionalOption(args, name)
        ?? throw new ArgumentException(
            $"The required option '{name}' is missing.",
            nameof(args));

    private static string? ReadOptionalOption(
        string[] args,
        string name)
    {
        string? value = null;
        for (int index = 0;
            index < args.Length;
            index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                if (value is not null)
                    throw new ArgumentException($"The option '{name}' is duplicated.", nameof(args));
                if (index + 1 == args.Length || string.IsNullOrWhiteSpace(args[index + 1])
                    || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"The option '{name}' requires a value.", nameof(args));
                value = args[++index];
            }
        }

        return value;
    }

    private static string GetInformationalVersion(
        Assembly assembly) =>
        assembly.GetCustomAttribute<
            AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static JsonSerializerOptions CreateJsonOptions(
        bool writeIndented)
    {
        JsonSerializerOptions options = new()
        {
            WriteIndented = writeIndented
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ManagedBuilderVoxelPacket(object Opaque, object Transparent,
        NativeBuilderBenchmarkImplementation Implementation)
    {
        internal long Consume() => ConsumeUploads(GetUpload(Opaque), GetUpload(Transparent));

        internal NativeBuilderExactOutput CopyExactOutput() => new(
            MemoryMarshal.Cast<byte, uint>(GetUpload(Opaque)).ToArray(),
            MemoryMarshal.Cast<byte, uint>(GetUpload(Transparent)).ToArray());

        private ReadOnlySpan<byte> GetUpload(object storage) => Implementation switch
        {
            NativeBuilderBenchmarkImplementation.ManagedList => (byte[])storage,
            NativeBuilderBenchmarkImplementation.ManagedListPrefix => MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan((List<uint>)storage)),
            NativeBuilderBenchmarkImplementation.ManagedExactArray => MemoryMarshal.AsBytes(((uint[])storage).AsSpan()),
            _ => throw new InvalidOperationException("The managed packet contains an unsupported storage kind.")
        };

        internal static byte[] ToUpload(uint[] source)
        {
            byte[] upload = new byte[checked(
                source.Length * sizeof(uint))];
            Buffer.BlockCopy(
                source,
                0,
                upload,
                0,
                upload.Length);
            return upload;
        }
    }

    private sealed class NativeBuilderVoxelPacket : IDisposable
    {
        private NativeTransfer<uint>? _opaque;
        private NativeTransfer<uint>? _transparent;
        private int _disposed;

        internal NativeBuilderVoxelPacket(
            NativeTransfer<uint>? opaque,
            NativeTransfer<uint>? transparent)
        {
            try
            {
                _opaque = NativeTransfer<uint>.Move(ref opaque);
                _transparent = NativeTransfer<uint>.Move(
                    ref transparent);
            }
            catch
            {
                try
                {
                    _opaque?.Dispose();
                }
                finally
                {
                    _transparent?.Dispose();
                }

                throw;
            }
            finally
            {
                opaque?.Dispose();
                transparent?.Dispose();
            }
        }

        internal NativeBuilderExactOutput CopyExactOutput() => new(
            _opaque!.Value.Read(
                static view => view.AsSpan().ToArray()),
            _transparent!.Value.Read(
                static view => view.AsSpan().ToArray()));

        internal long Consume()
        {
            long opaque = _opaque!.Value.Read(
                static view => ConsumeUpload(
                    MemoryMarshal.AsBytes(view.AsSpan())));
            long transparent = _transparent!.Value.Read(
                static view => ConsumeUpload(
                    MemoryMarshal.AsBytes(view.AsSpan())));
            return unchecked(
                opaque
                + RotateLeft(transparent, 17)
                + _opaque.Value.Length * sizeof(uint)
                + ((long)_transparent.Value.Length * sizeof(uint) << 32));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _opaque?.Dispose();
            }
            finally
            {
                _transparent?.Dispose();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativeBuilderBatchResult(
        long Checksum,
        double ElapsedMilliseconds);
}

internal sealed record NativeBuilderExactOutput(
    uint[] Opaque,
    uint[] Transparent);
