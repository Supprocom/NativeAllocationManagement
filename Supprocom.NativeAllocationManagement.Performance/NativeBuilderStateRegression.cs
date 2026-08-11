using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class NativeBuilderStateRegression
{
    internal const int TotalWords = 33_657_622;
    internal const int ChunkCount = 1_179;
    internal const int TightBatchCount = 319;
    internal const int MaximumOtherBatchCount = 486;
    internal const int SampleCount = 6;
    internal const int MeasurementIterations = 4;
    private const uint Salt = 0x3C6EF372U;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    internal static async Task<int> RunCommandAsync(string[] args)
    {
        if (args[0] == "--native-builder-state-worker")
        {
            StateBuilderImplementation implementation = Enum.Parse<
                StateBuilderImplementation>(
                ReadRequiredOption(args, "--implementation"),
                ignoreCase: true);
            StateBuilderWorkerEvidence evidence = RunWorker(
                implementation);
            Console.WriteLine(JsonSerializer.Serialize(evidence));
            return evidence.ExactParity
                && evidence.ExactlyOnceCleanupPassed
                && evidence.NativeRetainedBytesAfter == 0
                && evidence.TieredCompilationDisabled
                && evidence.TieredPgoDisabled
                    ? 0
                    : 3;
        }

        StateBuilderReport report = await RunBalancedAsync();
        string json = JsonSerializer.Serialize(report, JsonOptions);
        string? output = ReadOptionalOption(args, "--output");
        if (output is not null)
        {
            string fullPath = Path.GetFullPath(output);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, json);
        }

        Console.WriteLine(json);
        return HasOption(args, "--enforce") && !report.GatePassed
            ? 4
            : report.ExactParity && report.BalancedOrder
                ? 0
                : 3;
    }

    internal static async Task<StateBuilderReport> RunBalancedAsync()
    {
        StateBuilderSampleEvidence[] samples =
            new StateBuilderSampleEvidence[SampleCount];
        Stopwatch totalClock = Stopwatch.StartNew();
        for (int sampleIndex = 0;
            sampleIndex < SampleCount;
            sampleIndex++)
        {
            StateBuilderImplementation[] order = GetOrder(sampleIndex);
            StateBuilderWorkerEvidence[] results =
                new StateBuilderWorkerEvidence[order.Length];
            for (int position = 0; position < order.Length; position++)
            {
                results[position] = await RunIsolatedWorkerAsync(
                    order[position]);
            }

            StateBuilderWorkerEvidence managed = results.Single(
                static result => result.Implementation
                    == StateBuilderImplementation.ManagedArray);
            StateBuilderWorkerEvidence capturing = results.Single(
                static result => result.Implementation
                    == StateBuilderImplementation.CapturingBorrow);
            StateBuilderWorkerEvidence state = results.Single(
                static result => result.Implementation
                    == StateBuilderImplementation.StateBorrow);
            ValidateSample(managed, capturing, state);
            samples[sampleIndex] = new StateBuilderSampleEvidence(
                sampleIndex,
                order,
                managed,
                capturing,
                state,
                managed.ElapsedMilliseconds
                    / state.ElapsedMilliseconds,
                capturing.ElapsedMilliseconds
                    / state.ElapsedMilliseconds);
        }

        totalClock.Stop();
        double[] managedSpeedups = samples
            .Select(static sample =>
                sample.ManagedToStateSpeedup)
            .ToArray();
        double mean = managedSpeedups.Average();
        double aggregate = samples.Sum(static sample =>
                sample.ManagedArray.ElapsedMilliseconds)
            / samples.Sum(static sample =>
                sample.StateBorrow.ElapsedMilliseconds);
        double lower = PairedBenchmarkStatistics.ConfidenceLower95(
            managedSpeedups);
        bool parity = samples.All(static sample =>
            sample.ManagedArray.ExactParity
            && sample.CapturingBorrow.ExactParity
            && sample.StateBorrow.ExactParity
            && sample.ManagedArray.OutputSha256
                == sample.CapturingBorrow.OutputSha256
            && sample.ManagedArray.OutputSha256
                == sample.StateBorrow.OutputSha256);
        bool balanced = Enum.GetValues<StateBuilderImplementation>()
            .All(implementation =>
                samples.Count(sample =>
                    sample.Order[0] == implementation)
                == SampleCount / 3
                && samples.Count(sample =>
                    sample.Order[1] == implementation)
                == SampleCount / 3
                && samples.Count(sample =>
                    sample.Order[2] == implementation)
                == SampleCount / 3);
        bool runtimeConfiguration = samples.All(static sample =>
            HasRuntimeConfiguration(sample.ManagedArray)
            && HasRuntimeConfiguration(sample.CapturingBorrow)
            && HasRuntimeConfiguration(sample.StateBorrow));
        bool exactWorkload = samples.All(static sample =>
            HasExactWorkload(sample.ManagedArray)
            && HasExactWorkload(sample.CapturingBorrow)
            && HasExactWorkload(sample.StateBorrow));
        bool cleanup = samples.All(static sample =>
            sample.ManagedArray.ExactlyOnceCleanupPassed
            && sample.CapturingBorrow.ExactlyOnceCleanupPassed
            && sample.StateBorrow.ExactlyOnceCleanupPassed);
        bool zeroStateCallbackAllocations = samples.All(static sample =>
            sample.StateBorrow.CallbackAllocatedBytes == 0);
        bool binaryIdentity = samples.All(sample =>
            sample.ManagedArray.SourceCommit.Length == 40
            && sample.ManagedArray.SourceCommit
                == sample.CapturingBorrow.SourceCommit
            && sample.ManagedArray.SourceCommit
                == sample.StateBorrow.SourceCommit);
        bool gatePassed = EvaluateGate(
            parity,
            balanced,
            runtimeConfiguration,
            exactWorkload,
            cleanup,
            zeroStateCallbackAllocations,
            binaryIdentity,
            mean,
            aggregate,
            lower);
        return new StateBuilderReport(
            samples[0].ManagedArray.SourceCommit,
            TotalWords,
            ChunkCount,
            TightBatchCount,
            MaximumOtherBatchCount,
            SampleCount,
            samples,
            samples.Average(static sample =>
                sample.ManagedArray.ElapsedMilliseconds),
            samples.Average(static sample =>
                sample.CapturingBorrow.ElapsedMilliseconds),
            samples.Average(static sample =>
                sample.StateBorrow.ElapsedMilliseconds),
            mean,
            aggregate,
            lower,
            samples.Average(static sample =>
                sample.CapturingToStateSpeedup),
            samples.Average(static sample =>
                (double)sample.ManagedArray.ManagedAllocatedBytes),
            samples.Average(static sample =>
                (double)sample.CapturingBorrow.ManagedAllocatedBytes),
            samples.Average(static sample =>
                (double)sample.StateBorrow.ManagedAllocatedBytes),
            samples.Max(static sample =>
                sample.ManagedArray.PeakWorkingSetBytes),
            samples.Max(static sample =>
                sample.CapturingBorrow.PeakWorkingSetBytes),
            samples.Max(static sample =>
                sample.StateBorrow.PeakWorkingSetBytes),
            parity,
            balanced,
            runtimeConfiguration,
            exactWorkload,
            cleanup,
            zeroStateCallbackAllocations,
            binaryIdentity,
            gatePassed,
            totalClock.Elapsed.TotalMilliseconds,
            DateTimeOffset.UtcNow);
    }

    internal static StateBuilderWorkerEvidence RunWorker(
        StateBuilderImplementation implementation)
    {
        Stopwatch warmupClock = Stopwatch.StartNew();
        RunExecution(
            implementation,
            TotalWords,
            ChunkCount);
        warmupClock.Stop();
        NativeMemoryTestHooks.Reset();
        long allocatedBefore = GC.GetTotalAllocatedBytes(
            precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        StateBuilderExecution[] measurements =
            new StateBuilderExecution[MeasurementIterations];
        for (int iteration = 0;
            iteration < measurements.Length;
            iteration++)
        {
            measurements[iteration] = RunExecution(
                implementation,
                TotalWords,
                ChunkCount);
        }

        StateBuilderExecution execution = Average(measurements);
        long allocated = GC.GetTotalAllocatedBytes(
            precise: true) - allocatedBefore;
        allocated /= MeasurementIterations;
        process.Refresh();
        NativeMemoryTestMetrics metrics =
            NativeMemoryTestHooks.Snapshot();
        bool exactlyOnce = metrics.AllocationCount
                == metrics.FreeCount
            && metrics.OutstandingNativeBytes == 0;
        return new StateBuilderWorkerEvidence(
            implementation,
            ReadSourceCommit(),
            TotalWords,
            ChunkCount,
            execution.TotalBatchCount,
            execution.MaximumBatchCount,
            MeasurementIterations,
            warmupClock.Elapsed.TotalMilliseconds,
            measurements,
            execution.ElapsedMilliseconds,
            execution.SetupMilliseconds,
            execution.BuilderAllocationMilliseconds,
            execution.InitializationMilliseconds,
            execution.AppendMilliseconds,
            execution.CompletionMilliseconds,
            execution.TransferMilliseconds,
            execution.DisposalMilliseconds,
            execution.VerificationMilliseconds,
            allocated,
            execution.SetupAllocatedBytes,
            execution.CallbackAllocatedBytes,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            workingSetBefore,
            process.WorkingSet64,
            process.PeakWorkingSet64,
            metrics.AllocationCount,
            metrics.FreeCount,
            Math.Max(
                0,
                metrics.AllocationCount
                    - ((long)ChunkCount * MeasurementIterations)),
            execution.CopiedBytes,
            metrics.OutstandingNativeBytes,
            execution.OutputSha256,
            execution.ExactParity,
            exactlyOnce,
            IsDisabled("DOTNET_TieredCompilation"),
            IsDisabled("DOTNET_TieredPGO"));
    }

    internal static StateBuilderImplementation[] GetOrder(
        int sampleIndex) =>
        (sampleIndex % SampleCount) switch
        {
            0 =>
            [
                StateBuilderImplementation.ManagedArray,
                StateBuilderImplementation.CapturingBorrow,
                StateBuilderImplementation.StateBorrow
            ],
            1 =>
            [
                StateBuilderImplementation.CapturingBorrow,
                StateBuilderImplementation.StateBorrow,
                StateBuilderImplementation.ManagedArray
            ],
            2 =>
            [
                StateBuilderImplementation.StateBorrow,
                StateBuilderImplementation.ManagedArray,
                StateBuilderImplementation.CapturingBorrow
            ],
            3 =>
            [
                StateBuilderImplementation.ManagedArray,
                StateBuilderImplementation.StateBorrow,
                StateBuilderImplementation.CapturingBorrow
            ],
            4 =>
            [
                StateBuilderImplementation.CapturingBorrow,
                StateBuilderImplementation.ManagedArray,
                StateBuilderImplementation.StateBorrow
            ],
            _ =>
            [
                StateBuilderImplementation.StateBorrow,
                StateBuilderImplementation.CapturingBorrow,
                StateBuilderImplementation.ManagedArray
            ]
        };

    private static StateBuilderExecution Average(
        StateBuilderExecution[] measurements)
    {
        StateBuilderExecution first = measurements[0];
        if (measurements.Any(measurement =>
                measurement.TotalBatchCount != first.TotalBatchCount
                || measurement.MaximumBatchCount
                    != first.MaximumBatchCount
                || measurement.OutputSha256 != first.OutputSha256))
        {
            throw new InvalidOperationException(
                "The repeated state builder measurements changed their work or output.");
        }

        return new StateBuilderExecution(
            first.TotalBatchCount,
            first.MaximumBatchCount,
            measurements.Average(static item =>
                item.ElapsedMilliseconds),
            measurements.Average(static item =>
                item.SetupMilliseconds),
            measurements.Average(static item =>
                item.BuilderAllocationMilliseconds),
            measurements.Average(static item =>
                item.InitializationMilliseconds),
            measurements.Average(static item =>
                item.AppendMilliseconds),
            measurements.Average(static item =>
                item.CompletionMilliseconds),
            measurements.Average(static item =>
                item.TransferMilliseconds),
            measurements.Average(static item =>
                item.DisposalMilliseconds),
            measurements.Average(static item =>
                item.VerificationMilliseconds),
            checked((long)measurements.Average(static item =>
                item.SetupAllocatedBytes)),
            checked((long)measurements.Average(static item =>
                item.CallbackAllocatedBytes)),
            checked((long)measurements.Average(static item =>
                item.CopiedBytes)),
            first.OutputSha256,
            measurements.All(static item => item.ExactParity));
    }

    internal static bool EvaluateGate(
        bool exactParity,
        bool balancedOrder,
        bool runtimeConfiguration,
        bool exactWorkload,
        bool cleanup,
        bool zeroStateCallbackAllocations,
        bool binaryIdentity,
        double meanSpeedup,
        double aggregateSpeedup,
        double confidenceLower95) =>
        exactParity
        && balancedOrder
        && runtimeConfiguration
        && exactWorkload
        && cleanup
        && zeroStateCallbackAllocations
        && binaryIdentity
        && meanSpeedup > 1d
        && aggregateSpeedup > 1d
        && confidenceLower95 > 1d;

    private static StateBuilderExecution RunExecution(
        StateBuilderImplementation implementation,
        int totalWords,
        int chunkCount)
    {
        long setupAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long setupStart = Stopwatch.GetTimestamp();
        int maximumChunkWords = PartitionLength(
            totalWords,
            chunkCount,
            index: 0);
        uint[]? managedBuffer = implementation
                == StateBuilderImplementation.ManagedArray
            ? new uint[maximumChunkWords]
            : null;
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        long setupTicks = Stopwatch.GetTimestamp() - setupStart;
        long setupAllocated = GC.GetAllocatedBytesForCurrentThread()
            - setupAllocatedBefore;
        long builderTicks = 0;
        long initializationTicks = 0;
        long appendTicks = 0;
        long completionTicks = 0;
        long transferTicks = 0;
        long disposalTicks = 0;
        long verificationTicks = 0;
        long callbackAllocated = 0;
        long copiedBytes = 0;
        int totalBatches = 0;
        int maximumBatches = 0;
        int globalStart = 0;
        bool parity = true;

        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            int wordCount = PartitionLength(
                totalWords,
                chunkCount,
                chunk);
            int batchCount = GetBatchCount(chunk);
            totalBatches = checked(totalBatches + batchCount);
            maximumBatches = Math.Max(maximumBatches, batchCount);
            long phaseStart = Stopwatch.GetTimestamp();
            NativeBuilder<uint> builder = new(preLease: wordCount);
            builderTicks += Stopwatch.GetTimestamp() - phaseStart;

            phaseStart = Stopwatch.GetTimestamp();
            if (implementation == StateBuilderImplementation.StateBorrow)
            {
                ChunkState state = new(
                    globalStart,
                    wordCount,
                    batchCount);
                long callbackBefore =
                    GC.GetAllocatedBytesForCurrentThread();
                builder.Borrow(
                    in state,
                    RunStateChunk);
                callbackAllocated +=
                    GC.GetAllocatedBytesForCurrentThread()
                    - callbackBefore;
            }
            else if (implementation
                == StateBuilderImplementation.CapturingBorrow)
            {
                int capturedStart = globalStart;
                builder.Borrow(
                    (scoped ref NativeBuilderBorrow<uint> borrow) =>
                        RunCapturingChunk(
                            ref borrow,
                            capturedStart,
                            wordCount,
                            batchCount));
            }
            else
            {
                FillManagedChunks(
                    managedBuffer!.AsSpan(0, wordCount),
                    globalStart,
                    batchCount);
            }

            initializationTicks +=
                Stopwatch.GetTimestamp() - phaseStart;
            phaseStart = Stopwatch.GetTimestamp();
            if (implementation == StateBuilderImplementation.ManagedArray)
            {
                builder.Append(
                    managedBuffer!.AsSpan(0, wordCount));
                copiedBytes = checked(
                    copiedBytes + ((long)wordCount * sizeof(uint)));
            }

            appendTicks += Stopwatch.GetTimestamp() - phaseStart;
            phaseStart = Stopwatch.GetTimestamp();
            NativeTransfer<uint>? source = builder.Complete();
            completionTicks += Stopwatch.GetTimestamp() - phaseStart;
            phaseStart = Stopwatch.GetTimestamp();
            NativeTransfer<uint> transfer =
                NativeTransfer<uint>.Move(ref source);
            transferTicks += Stopwatch.GetTimestamp() - phaseStart;
            phaseStart = Stopwatch.GetTimestamp();
            parity &= VerifyAndHash(
                transfer,
                hash,
                globalStart,
                wordCount);
            verificationTicks +=
                Stopwatch.GetTimestamp() - phaseStart;
            phaseStart = Stopwatch.GetTimestamp();
            transfer.Dispose();
            builder.Dispose();
            disposalTicks += Stopwatch.GetTimestamp() - phaseStart;
            globalStart += wordCount;
        }

        string outputHash = Convert.ToHexString(
            hash.GetHashAndReset());
        long measuredTicks = builderTicks
            + initializationTicks
            + appendTicks
            + completionTicks
            + transferTicks
            + disposalTicks;
        return new StateBuilderExecution(
            totalBatches,
            maximumBatches,
            Milliseconds(measuredTicks),
            Milliseconds(setupTicks),
            Milliseconds(builderTicks),
            Milliseconds(initializationTicks),
            Milliseconds(appendTicks),
            Milliseconds(completionTicks),
            Milliseconds(transferTicks),
            Milliseconds(disposalTicks),
            Milliseconds(verificationTicks),
            setupAllocated,
            callbackAllocated,
            copiedBytes,
            outputHash,
            parity && globalStart == totalWords);
    }

    private static void RunStateChunk(
        scoped ref NativeBuilderBorrow<uint> builder,
        scoped in ChunkState state)
    {
        int offset = 0;
        for (int batch = 0; batch < state.BatchCount; batch++)
        {
            int count = PartitionLength(
                state.WordCount,
                state.BatchCount,
                batch);
            BatchState batchState = new(
                state.GlobalStart + offset,
                count);
            builder.Write<BatchState, StateBatchWriter>(
                count,
                in batchState);
            offset += count;
        }
    }

    private readonly struct StateBatchWriter
        : INativeBuilderWriteAction<uint, BatchState>
    {
        public static void Invoke(
            scoped NativeBuilderWriter<uint> writer,
            scoped in BatchState state)
        {
            Fill(writer.AsSpan(), state.GlobalStart);
            writer.Commit(state.Count);
        }
    }

    private static void RunCapturingChunk(
        scoped ref NativeBuilderBorrow<uint> builder,
        int globalStart,
        int wordCount,
        int batchCount)
    {
        int offset = 0;
        for (int batch = 0; batch < batchCount; batch++)
        {
            int count = PartitionLength(
                wordCount,
                batchCount,
                batch);
            int capturedStart = globalStart + offset;
            builder.Write(
                count,
                writer =>
                {
                    Fill(writer.AsSpan(), capturedStart);
                    writer.Commit(count);
                });
            offset += count;
        }
    }

    private static void FillManagedChunks(
        Span<uint> destination,
        int globalStart,
        int batchCount)
    {
        int offset = 0;
        for (int batch = 0; batch < batchCount; batch++)
        {
            int count = PartitionLength(
                destination.Length,
                batchCount,
                batch);
            Fill(
                destination.Slice(offset, count),
                globalStart + offset);
            offset += count;
        }
    }

    private static void Fill(
        Span<uint> destination,
        int globalStart)
    {
        for (int index = 0; index < destination.Length; index++)
        {
            destination[index] = ValueAt(globalStart + index);
        }
    }

    private static bool VerifyAndHash(
        NativeTransfer<uint> transfer,
        IncrementalHash hash,
        int globalStart,
        int expectedLength) =>
        transfer.Read(view =>
        {
            ReadOnlySpan<uint> values = view.AsSpan();
            if (values.Length != expectedLength)
            {
                return false;
            }

            for (int index = 0; index < values.Length; index++)
            {
                if (values[index] != ValueAt(globalStart + index))
                {
                    return false;
                }
            }

            hash.AppendData(MemoryMarshal.AsBytes(values));
            return true;
        });

    private static uint ValueAt(int index) =>
        unchecked(((uint)index * 2_654_435_761U) + Salt);

    private static int GetBatchCount(int chunk) =>
        checked(
            TightBatchCount
            + (chunk % (MaximumOtherBatchCount + 1)));

    private static int PartitionLength(
        int total,
        int partitions,
        int index) =>
        total / partitions
        + (index < total % partitions ? 1 : 0);

    private static double Milliseconds(long ticks) =>
        ticks * 1000d / Stopwatch.Frequency;

    private static bool HasRuntimeConfiguration(
        StateBuilderWorkerEvidence evidence) =>
        evidence.TieredCompilationDisabled
        && evidence.TieredPgoDisabled;

    private static bool HasExactWorkload(
        StateBuilderWorkerEvidence evidence) =>
        evidence.TotalWords == TotalWords
        && evidence.ChunkCount == ChunkCount
        && evidence.MeasurementIterations == MeasurementIterations
        && evidence.Measurements.Length == MeasurementIterations
        && evidence.Measurements.All(static measurement =>
            measurement.ExactParity)
        && evidence.MaximumBatchCount
            == TightBatchCount + MaximumOtherBatchCount;

    private static async Task<StateBuilderWorkerEvidence>
        RunIsolatedWorkerAsync(
            StateBuilderImplementation implementation)
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
            typeof(NativeBuilderStateRegression).Assembly.Location);
        process.StartInfo.ArgumentList.Add(
            "--native-builder-state-worker");
        process.StartInfo.ArgumentList.Add("--implementation");
        process.StartInfo.ArgumentList.Add(implementation.ToString());
        process.StartInfo.Environment["DOTNET_TieredCompilation"] = "0";
        process.StartInfo.Environment["DOTNET_TieredPGO"] = "0";
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "The state builder benchmark worker did not start.");
        }

        Task<string> outputTask =
            process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask =
            process.StandardError.ReadToEndAsync();
        Task exitTask = process.WaitForExitAsync();
        if (await Task.WhenAny(
                exitTask,
                Task.Delay(TimeSpan.FromSeconds(60))) != exitTask)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "The state builder benchmark worker exceeded 60 seconds.");
        }

        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The state builder benchmark worker failed with exit code {process.ExitCode}: {error}");
        }

        return JsonSerializer.Deserialize<StateBuilderWorkerEvidence>(
            output.Trim())
            ?? throw new InvalidOperationException(
                "The state builder benchmark worker returned no evidence.");
    }

    private static void ValidateSample(
        StateBuilderWorkerEvidence managed,
        StateBuilderWorkerEvidence capturing,
        StateBuilderWorkerEvidence state)
    {
        if (managed.TotalWords != capturing.TotalWords
            || managed.TotalWords != state.TotalWords
            || managed.ChunkCount != capturing.ChunkCount
            || managed.ChunkCount != state.ChunkCount
            || managed.TotalBatchCount != capturing.TotalBatchCount
            || managed.TotalBatchCount != state.TotalBatchCount
            || managed.OutputSha256 != capturing.OutputSha256
            || managed.OutputSha256 != state.OutputSha256)
        {
            throw new InvalidOperationException(
                "The state builder sample used different work or output.");
        }
    }

    private static string ReadSourceCommit()
    {
        string informational = typeof(NativeBuilderStateRegression).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? string.Empty;
        int separator = informational.LastIndexOf('+');
        return separator >= 0
            ? informational[(separator + 1)..]
            : string.Empty;
    }

    private static bool IsDisabled(string variable) =>
        string.Equals(
            Environment.GetEnvironmentVariable(variable),
            "0",
            StringComparison.Ordinal);

    private static string ReadRequiredOption(
        string[] args,
        string name) =>
        ReadOptionalOption(args, name)
        ?? throw new ArgumentException(
            $"Missing required option {name}.");

    private static string? ReadOptionalOption(
        string[] args,
        string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == name)
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static bool HasOption(string[] args, string name) =>
        args.Contains(name, StringComparer.Ordinal);

    private readonly record struct ChunkState(
        int GlobalStart,
        int WordCount,
        int BatchCount);

    private readonly record struct BatchState(
        int GlobalStart,
        int Count);
}

internal enum StateBuilderImplementation
{
    ManagedArray,
    CapturingBorrow,
    StateBorrow
}

internal sealed record StateBuilderExecution(
    int TotalBatchCount,
    int MaximumBatchCount,
    double ElapsedMilliseconds,
    double SetupMilliseconds,
    double BuilderAllocationMilliseconds,
    double InitializationMilliseconds,
    double AppendMilliseconds,
    double CompletionMilliseconds,
    double TransferMilliseconds,
    double DisposalMilliseconds,
    double VerificationMilliseconds,
    long SetupAllocatedBytes,
    long CallbackAllocatedBytes,
    long CopiedBytes,
    string OutputSha256,
    bool ExactParity);

internal sealed record StateBuilderWorkerEvidence(
    StateBuilderImplementation Implementation,
    string SourceCommit,
    int TotalWords,
    int ChunkCount,
    int TotalBatchCount,
    int MaximumBatchCount,
    int MeasurementIterations,
    double WarmupMilliseconds,
    StateBuilderExecution[] Measurements,
    double ElapsedMilliseconds,
    double SetupMilliseconds,
    double BuilderAllocationMilliseconds,
    double InitializationMilliseconds,
    double AppendMilliseconds,
    double CompletionMilliseconds,
    double TransferMilliseconds,
    double DisposalMilliseconds,
    double VerificationMilliseconds,
    long ManagedAllocatedBytes,
    long SetupAllocatedBytes,
    long CallbackAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    long PeakWorkingSetBytes,
    long NativeAllocationCount,
    long NativeFreeCount,
    long NativeGrowthCount,
    long CopiedBytes,
    long NativeRetainedBytesAfter,
    string OutputSha256,
    bool ExactParity,
    bool ExactlyOnceCleanupPassed,
    bool TieredCompilationDisabled,
    bool TieredPgoDisabled);

internal sealed record StateBuilderSampleEvidence(
    int SampleIndex,
    StateBuilderImplementation[] Order,
    StateBuilderWorkerEvidence ManagedArray,
    StateBuilderWorkerEvidence CapturingBorrow,
    StateBuilderWorkerEvidence StateBorrow,
    double ManagedToStateSpeedup,
    double CapturingToStateSpeedup);

internal sealed record StateBuilderReport(
    string SourceCommit,
    int TotalWords,
    int ChunkCount,
    int TightBatchCount,
    int MaximumOtherBatchCount,
    int SampleCount,
    StateBuilderSampleEvidence[] Samples,
    double ManagedMeanMilliseconds,
    double CapturingMeanMilliseconds,
    double StateMeanMilliseconds,
    double ManagedToStateMeanSpeedup,
    double ManagedToStateAggregateSpeedup,
    double ManagedToStateConfidenceLower95,
    double CapturingToStateMeanSpeedup,
    double ManagedMeanAllocatedBytes,
    double CapturingMeanAllocatedBytes,
    double StateMeanAllocatedBytes,
    long ManagedPeakWorkingSetBytes,
    long CapturingPeakWorkingSetBytes,
    long StatePeakWorkingSetBytes,
    bool ExactParity,
    bool BalancedOrder,
    bool RuntimeConfigurationPassed,
    bool ExactWorkloadPassed,
    bool CleanupPassed,
    bool ZeroStateCallbackAllocations,
    bool BinaryIdentityPassed,
    bool GatePassed,
    double TotalElapsedMilliseconds,
    DateTimeOffset CreatedUtc);
