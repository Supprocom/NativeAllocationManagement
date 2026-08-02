using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class NativeBuilderWriteRegression
{
    internal const double RequiredSpeedup = 1.20d;
    private const int WordsPerRecord = 2;
    private static readonly JsonSerializerOptions CompactJson =
        CreateJsonOptions(writeIndented: false);
    private static readonly JsonSerializerOptions IndentedJson =
        CreateJsonOptions(writeIndented: true);

    internal static NativeBuilderWriteOptions DefaultOptions => new(
        RecordsPerBuilder: 100_000,
        WorkerCount: 24,
        SampleCount: 6);

    internal static async Task<int> RunCommandAsync(string[] args)
    {
        NativeBuilderWriteOptions options = ParseOptions(args);
        if (args[0] == "--native-builder-write-worker")
        {
            NativeBuilderWriteImplementation implementation = Enum.Parse<
                NativeBuilderWriteImplementation>(
                ReadRequiredOption(args, "--implementation"),
                ignoreCase: true);
            NativeBuilderWriteWorkerEvidence evidence = RunWorker(
                implementation,
                options);
            Console.WriteLine(JsonSerializer.Serialize(
                evidence,
                CompactJson));
            return evidence.ExactParity
                && evidence.CancellationCleanupPassed
                && evidence.ExactlyOnceCleanupPassed
                && evidence.NativeFreshSegmentAllocationDelta == 0
                && evidence.TieredCompilationDisabled
                && evidence.TieredPgoDisabled
                    ? 0
                    : 3;
        }

        NativeBuilderWriteReport report = await RunPairedAsync(options);
        string json = JsonSerializer.Serialize(report, IndentedJson);
        string? outputPath = ReadOptionalOption(args, "--output");
        if (outputPath is not null)
        {
            string fullPath = Path.GetFullPath(outputPath);
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

    internal static async Task<NativeBuilderWriteReport> RunPairedAsync(
        NativeBuilderWriteOptions options)
    {
        ValidateOptions(options);
        NativeBuilderWritePairEvidence[] pairs =
            new NativeBuilderWritePairEvidence[options.SampleCount];
        Stopwatch totalClock = Stopwatch.StartNew();
        for (int sampleIndex = 0;
            sampleIndex < options.SampleCount;
            sampleIndex++)
        {
            NativeBuilderWriteImplementation first =
                GetFirstImplementation(sampleIndex);
            NativeBuilderWriteWorkerEvidence firstEvidence =
                await RunIsolatedWorkerAsync(first, options);
            NativeBuilderWriteImplementation second = first
                == NativeBuilderWriteImplementation.RepeatedAppend
                    ? NativeBuilderWriteImplementation.BoundedWrite
                    : NativeBuilderWriteImplementation.RepeatedAppend;
            NativeBuilderWriteWorkerEvidence secondEvidence =
                await RunIsolatedWorkerAsync(second, options);
            NativeBuilderWriteWorkerEvidence append = first
                == NativeBuilderWriteImplementation.RepeatedAppend
                    ? firstEvidence
                    : secondEvidence;
            NativeBuilderWriteWorkerEvidence direct = first
                == NativeBuilderWriteImplementation.BoundedWrite
                    ? firstEvidence
                    : secondEvidence;
            ValidatePair(append, direct);
            pairs[sampleIndex] = new NativeBuilderWritePairEvidence(
                sampleIndex,
                first,
                append,
                direct,
                append.ElapsedMilliseconds
                    / direct.ElapsedMilliseconds);
        }

        totalClock.Stop();
        double[] speedups = pairs
            .Select(static pair => pair.AppendToWriteSpeedup)
            .ToArray();
        double mean = speedups.Average();
        double aggregate = pairs.Sum(static pair =>
                pair.RepeatedAppend.ElapsedMilliseconds)
            / pairs.Sum(static pair =>
                pair.BoundedWrite.ElapsedMilliseconds);
        double lower =
            PairedBenchmarkStatistics.ConfidenceLower95(speedups);
        bool parity = pairs.All(static pair =>
            pair.RepeatedAppend.ExactParity
            && pair.BoundedWrite.ExactParity
            && pair.RepeatedAppend.OutputSha256
                == pair.BoundedWrite.OutputSha256
            && pair.RepeatedAppend.Checksum
                == pair.BoundedWrite.Checksum);
        bool balanced = pairs.Count(static pair =>
                pair.FirstImplementation
                    == NativeBuilderWriteImplementation.RepeatedAppend)
            == options.SampleCount / 2
            && pairs.Count(static pair =>
                pair.FirstImplementation
                    == NativeBuilderWriteImplementation.BoundedWrite)
                == options.SampleCount / 2;
        bool runtimeConfiguration = pairs.All(static pair =>
            pair.RepeatedAppend.TieredCompilationDisabled
            && pair.RepeatedAppend.TieredPgoDisabled
            && pair.BoundedWrite.TieredCompilationDisabled
            && pair.BoundedWrite.TieredPgoDisabled);
        bool zeroFreshSegments = pairs.All(static pair =>
            pair.RepeatedAppend.NativeFreshSegmentAllocationDelta == 0
            && pair.BoundedWrite.NativeFreshSegmentAllocationDelta == 0);
        bool cleanup = pairs.All(static pair =>
            pair.RepeatedAppend.CancellationCleanupPassed
            && pair.RepeatedAppend.ExactlyOnceCleanupPassed
            && pair.BoundedWrite.CancellationCleanupPassed
            && pair.BoundedWrite.ExactlyOnceCleanupPassed);
        bool productionShape = options.WorkerCount == 24
            && checked((long)options.WorkerCount
                * options.RecordsPerBuilder) >= 2_000_000;
        bool gatePassed = EvaluateGate(
            parity,
            balanced,
            runtimeConfiguration,
            zeroFreshSegments,
            cleanup,
            productionShape,
            mean,
            aggregate,
            lower);
        return new NativeBuilderWriteReport(
            options,
            pairs,
            pairs.Average(static pair =>
                pair.RepeatedAppend.ElapsedMilliseconds),
            pairs.Average(static pair =>
                pair.BoundedWrite.ElapsedMilliseconds),
            mean,
            aggregate,
            lower,
            pairs.Average(static pair =>
                pair.RepeatedAppend.RecordsPerSecond),
            pairs.Average(static pair =>
                pair.BoundedWrite.RecordsPerSecond),
            pairs.Average(static pair =>
                (double)pair.RepeatedAppend.ManagedAllocatedBytes),
            pairs.Average(static pair =>
                (double)pair.BoundedWrite.ManagedAllocatedBytes),
            pairs.Max(static pair =>
                pair.RepeatedAppend.PeakWorkingSetBytes),
            pairs.Max(static pair =>
                pair.BoundedWrite.PeakWorkingSetBytes),
            parity,
            balanced,
            runtimeConfiguration,
            zeroFreshSegments,
            cleanup,
            productionShape,
            gatePassed,
            totalClock.Elapsed.TotalMilliseconds,
            DateTimeOffset.UtcNow);
    }

    internal static NativeBuilderWriteWorkerEvidence RunWorker(
        NativeBuilderWriteImplementation implementation,
        NativeBuilderWriteOptions options)
    {
        ValidateOptions(options);
        Stopwatch setupClock = Stopwatch.StartNew();
        using BuilderWriteExecution execution = new(options);
        setupClock.Stop();

        Stopwatch verificationClock = Stopwatch.StartNew();
        string expectedHash = ComputeExpectedHash(options);
        string outputHash = execution.ComputeExactHash(
            implementation);
        long expectedChecksum = ComputeExpectedChecksum(options);
        verificationClock.Stop();
        bool exactParity = outputHash == expectedHash;

        Stopwatch warmupClock = Stopwatch.StartNew();
        long warmupChecksum = execution.RunBatch(implementation);
        warmupClock.Stop();
        exactParity &= warmupChecksum == expectedChecksum;
        bool cancellationCleanup = execution.ProbeCancellation();
        bool exactlyOnceCleanup = execution.ProbeExactlyOnceCleanup();

        long freshBefore = execution.FreshSegmentAllocationCount;
        long retainedBefore = execution.RetainedBytes;
        long allocatedBefore = GC.GetTotalAllocatedBytes(
            precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        Stopwatch measuredClock = Stopwatch.StartNew();
        long measuredChecksum = execution.RunBatch(implementation);
        measuredClock.Stop();
        long allocated = GC.GetTotalAllocatedBytes(
            precise: true) - allocatedBefore;
        process.Refresh();
        long workingSetAfter = process.WorkingSet64;
        long peakWorkingSet = process.PeakWorkingSet64;
        long freshAfter = execution.FreshSegmentAllocationCount;
        long retainedAfter = execution.RetainedBytes;
        exactParity &= measuredChecksum == expectedChecksum;
        long records = checked(
            (long)options.WorkerCount
            * options.RecordsPerBuilder);
        long callCount = implementation
            == NativeBuilderWriteImplementation.RepeatedAppend
                ? records
                : options.WorkerCount;
        double elapsedMilliseconds =
            measuredClock.Elapsed.TotalMilliseconds;
        return new NativeBuilderWriteWorkerEvidence(
            implementation,
            options.WorkerCount,
            options.RecordsPerBuilder,
            records,
            checked(records * WordsPerRecord),
            callCount,
            setupClock.Elapsed.TotalMilliseconds,
            verificationClock.Elapsed.TotalMilliseconds,
            warmupClock.Elapsed.TotalMilliseconds,
            elapsedMilliseconds,
            records / (elapsedMilliseconds / 1000d),
            allocated,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            workingSetBefore,
            workingSetAfter,
            peakWorkingSet,
            GC.GetGCMemoryInfo().HeapSizeBytes,
            freshAfter - freshBefore,
            retainedBefore,
            retainedAfter,
            outputHash,
            measuredChecksum,
            exactParity,
            cancellationCleanup,
            exactlyOnceCleanup,
            string.Equals(
                Environment.GetEnvironmentVariable(
                    "DOTNET_TieredCompilation"),
                "0",
                StringComparison.Ordinal),
            string.Equals(
                Environment.GetEnvironmentVariable(
                    "DOTNET_TieredPGO"),
                "0",
                StringComparison.Ordinal));
    }

    internal static NativeBuilderWriteImplementation GetFirstImplementation(
        int sampleIndex) =>
        (sampleIndex & 1) == 0
            ? NativeBuilderWriteImplementation.RepeatedAppend
            : NativeBuilderWriteImplementation.BoundedWrite;

    internal static bool EvaluateGate(
        bool exactParity,
        bool balancedOrder,
        bool runtimeConfiguration,
        bool zeroFreshSegments,
        bool cleanup,
        bool productionShape,
        double meanSpeedup,
        double aggregateSpeedup,
        double confidenceLower95) =>
        exactParity
        && balancedOrder
        && runtimeConfiguration
        && zeroFreshSegments
        && cleanup
        && productionShape
        && meanSpeedup >= RequiredSpeedup
        && aggregateSpeedup >= RequiredSpeedup
        && confidenceLower95 > 1d;

    private static async Task<NativeBuilderWriteWorkerEvidence>
        RunIsolatedWorkerAsync(
            NativeBuilderWriteImplementation implementation,
            NativeBuilderWriteOptions options)
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
            typeof(NativeBuilderWriteRegression).Assembly.Location);
        process.StartInfo.ArgumentList.Add(
            "--native-builder-write-worker");
        process.StartInfo.ArgumentList.Add("--implementation");
        process.StartInfo.ArgumentList.Add(
            implementation.ToString());
        AddOption(
            process.StartInfo,
            "--records-per-builder",
            options.RecordsPerBuilder);
        AddOption(
            process.StartInfo,
            "--workers",
            options.WorkerCount);
        AddOption(
            process.StartInfo,
            "--samples",
            options.SampleCount);
        process.StartInfo.Environment["DOTNET_TieredCompilation"] = "0";
        process.StartInfo.Environment["DOTNET_TieredPGO"] = "0";
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "The builder write benchmark worker did not start.");
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
                "The builder write benchmark worker exceeded 60 seconds.");
        }

        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The builder write benchmark worker failed with exit code {process.ExitCode}: {error}");
        }

        string json = output.Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()
            ?? throw new InvalidDataException(
                "The builder write benchmark worker produced no evidence.");
        return JsonSerializer.Deserialize<
                NativeBuilderWriteWorkerEvidence>(json, CompactJson)
            ?? throw new InvalidDataException(
                "The builder write benchmark evidence is invalid.");
    }

    private static void ValidatePair(
        NativeBuilderWriteWorkerEvidence append,
        NativeBuilderWriteWorkerEvidence direct)
    {
        if (!append.ExactParity
            || !direct.ExactParity
            || append.OutputSha256 != direct.OutputSha256
            || append.Checksum != direct.Checksum
            || append.RecordCount != direct.RecordCount
            || append.WordCount != direct.WordCount)
        {
            throw new InvalidDataException(
                "The paired builder write outputs are not equal.");
        }

        if (!append.TieredCompilationDisabled
            || !append.TieredPgoDisabled
            || !direct.TieredCompilationDisabled
            || !direct.TieredPgoDisabled)
        {
            throw new InvalidOperationException(
                "The builder write runtime settings are invalid.");
        }
    }

    private static string ComputeExpectedHash(
        NativeBuilderWriteOptions options)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        uint[] values = new uint[checked(
            options.RecordsPerBuilder * WordsPerRecord)];
        for (int workerIndex = 0;
            workerIndex < options.WorkerCount;
            workerIndex++)
        {
            FillValues(values, workerIndex);
            hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static long ComputeExpectedChecksum(
        NativeBuilderWriteOptions options)
    {
        uint[] values = new uint[checked(
            options.RecordsPerBuilder * WordsPerRecord)];
        long checksum = 0;
        for (int workerIndex = 0;
            workerIndex < options.WorkerCount;
            workerIndex++)
        {
            FillValues(values, workerIndex);
            checksum = CombineChecksum(
                checksum,
                ComputeChecksum(values));
        }

        return checksum;
    }

    private static void FillValues(
        Span<uint> values,
        int workerIndex)
    {
        int recordCount = values.Length / WordsPerRecord;
        for (int recordIndex = 0;
            recordIndex < recordCount;
            recordIndex++)
        {
            WriteRecord(
                values,
                recordIndex,
                recordIndex,
                workerIndex);
        }
    }

    private static void WriteRecord(
        Span<uint> values,
        int destinationRecordIndex,
        int recordIndex,
        int workerIndex)
    {
        uint first = unchecked(
            (uint)recordIndex * 747_796_405U
            + (uint)workerIndex * 2_891_336_453U
            + 0x9E37_79B9U);
        int offset = destinationRecordIndex * WordsPerRecord;
        values[offset] = first;
        values[offset + 1] = unchecked(
            (first << 13)
            | (first >> 19)) ^ 0x85EB_CA6BU;
    }

    private static long ComputeChecksum(ReadOnlySpan<uint> values)
    {
        ulong checksum = 14_695_981_039_346_656_037UL;
        foreach (uint value in values)
        {
            checksum = unchecked(
                (checksum ^ value)
                * 1_099_511_628_211UL);
        }

        return unchecked((long)checksum);
    }

    private static long CombineChecksum(
        long current,
        long next) =>
        unchecked(current * 31 + next);

    private static NativeBuilderWriteOptions ParseOptions(
        string[] args)
    {
        NativeBuilderWriteOptions options = DefaultOptions;
        return options with
        {
            RecordsPerBuilder = ReadIntOption(
                args,
                "--records-per-builder",
                options.RecordsPerBuilder),
            WorkerCount = ReadIntOption(
                args,
                "--workers",
                options.WorkerCount),
            SampleCount = ReadIntOption(
                args,
                "--samples",
                options.SampleCount)
        };
    }

    private static void ValidateOptions(
        NativeBuilderWriteOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.RecordsPerBuilder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.WorkerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.SampleCount);
        if ((options.SampleCount & 1) != 0)
        {
            throw new ArgumentException(
                "The sample count must be positive and even.",
                nameof(options));
        }

        _ = checked(
            options.RecordsPerBuilder * WordsPerRecord);
    }

    private static int ReadIntOption(
        string[] args,
        string name,
        int fallback)
    {
        string? value = ReadOptionalOption(args, name);
        return value is null
            ? fallback
            : int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string ReadRequiredOption(
        string[] args,
        string name) =>
        ReadOptionalOption(args, name)
        ?? throw new ArgumentException(
            $"The required option {name} is missing.");

    private static string? ReadOptionalOption(
        string[] args,
        string name)
    {
        for (int index = 0;
            index < args.Length - 1;
            index++)
        {
            if (args[index] == name)
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static bool HasOption(
        string[] args,
        string name) =>
        args.Contains(name, StringComparer.Ordinal);

    private static void AddOption(
        ProcessStartInfo startInfo,
        string name,
        int value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(
            value.ToString(CultureInfo.InvariantCulture));
    }

    private static JsonSerializerOptions CreateJsonOptions(
        bool writeIndented)
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class BuilderWriteExecution : IDisposable
    {
        private readonly BuilderWriteWorker[] _workers;
        private readonly long[] _checksums;
        private readonly ParallelOptions _parallelOptions;

        internal BuilderWriteExecution(
            NativeBuilderWriteOptions options)
        {
            _workers = Enumerable.Range(0, options.WorkerCount)
                .Select(index => new BuilderWriteWorker(
                    index,
                    options.RecordsPerBuilder))
                .ToArray();
            _checksums = new long[options.WorkerCount];
            _parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.WorkerCount
            };
        }

        internal long FreshSegmentAllocationCount =>
            _workers.Sum(static worker =>
                worker.Statistics.FreshSegmentAllocationCount);

        internal long RetainedBytes =>
            _workers.Sum(static worker =>
                worker.Statistics.RetainedBytes);

        internal string ComputeExactHash(
            NativeBuilderWriteImplementation implementation)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            foreach (BuilderWriteWorker worker in _workers)
            {
                uint[] values = worker.BuildCopy(implementation);
                hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        internal long RunBatch(
            NativeBuilderWriteImplementation implementation)
        {
            Parallel.For(
                0,
                _workers.Length,
                _parallelOptions,
                index =>
                {
                    _checksums[index] =
                        _workers[index].Run(implementation);
                });
            long checksum = 0;
            foreach (long value in _checksums)
            {
                checksum = CombineChecksum(checksum, value);
            }

            return checksum;
        }

        internal bool ProbeCancellation() =>
            _workers[0].ProbeCancellation();

        internal bool ProbeExactlyOnceCleanup() =>
            _workers[0].ProbeExactlyOnceCleanup();

        public void Dispose()
        {
            foreach (BuilderWriteWorker worker in _workers)
            {
                worker.Dispose();
            }
        }
    }

    private sealed class BuilderWriteWorker : IDisposable
    {
        private readonly int _workerIndex;
        private readonly int _recordCount;
        private readonly int _wordCount;
        private readonly NativePool<uint> _pool;

        internal BuilderWriteWorker(
            int workerIndex,
            int recordCount)
        {
            _workerIndex = workerIndex;
            _recordCount = recordCount;
            _wordCount = checked(recordCount * WordsPerRecord);
            _pool = new NativePool<uint>(
                preLease: _wordCount,
                returnMemoryOnDispose:
                    NativeMemoryReturn.ToNativeMemory);
        }

        internal NativeOwnerStatistics Statistics =>
            _pool.GetStatistics();

        internal uint[] BuildCopy(
            NativeBuilderWriteImplementation implementation)
        {
            NativeTransfer<uint> transfer = Build(implementation);
            try
            {
                return transfer.Read(
                    static view => view.AsSpan().ToArray());
            }
            finally
            {
                transfer.Dispose();
            }
        }

        internal long Run(
            NativeBuilderWriteImplementation implementation)
        {
            NativeTransfer<uint> transfer = Build(implementation);
            try
            {
                return transfer.Read(static view =>
                    ComputeChecksum(view.AsSpan()));
            }
            finally
            {
                transfer.Dispose();
            }
        }

        internal bool ProbeCancellation()
        {
            using CancellationTokenSource cancellation = new();
            NativeBuilder<uint> builder = _pool.CreateBuilder(
                preLease: 2);
            bool canceled = false;
            try
            {
                builder.Borrow(
                    (scoped ref NativeBuilderBorrow<uint> borrow) =>
                    {
                        borrow.Write(
                            2,
                            static writer =>
                            {
                                writer.AsSpan()[0] = 1;
                                writer.Commit(1);
                            });
                        cancellation.Cancel();
                    },
                    cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            finally
            {
                builder.Dispose();
            }

            return canceled
                && _pool.GetStatistics().RequestedBytes == 0;
        }

        internal bool ProbeExactlyOnceCleanup()
        {
            using NativeBuilder<uint> builder =
                _pool.CreateBuilder(preLease: 2);
            builder.Borrow(
                static (scoped ref NativeBuilderBorrow<uint> borrow) =>
                {
                    borrow.Write(
                        2,
                        static writer =>
                        {
                            writer.AsSpan().Fill(1);
                            writer.Commit(2);
                        });
                });
            NativeTransfer<uint> transfer = builder.Complete();
            transfer.Dispose();
            bool secondDisposeFailed = false;
            try
            {
                transfer.Dispose();
            }
            catch (ObjectDisposedException)
            {
                secondDisposeFailed = true;
            }

            return secondDisposeFailed
                && _pool.GetStatistics().RequestedBytes == 0;
        }

        public void Dispose() => _pool.Dispose();

        private NativeTransfer<uint> Build(
            NativeBuilderWriteImplementation implementation)
        {
            using NativeBuilder<uint> builder =
                _pool.CreateBuilder(preLease: _wordCount);
            if (implementation
                == NativeBuilderWriteImplementation.RepeatedAppend)
            {
                Span<uint> record = stackalloc uint[WordsPerRecord];
                for (int recordIndex = 0;
                    recordIndex < _recordCount;
                    recordIndex++)
                {
                    WriteRecord(
                        record,
                        destinationRecordIndex: 0,
                        recordIndex,
                        _workerIndex);
                    builder.Append(record);
                }
            }
            else
            {
                builder.Borrow(EmitThroughNestedHelpers);
            }

            return builder.Complete();
        }

        private void EmitThroughNestedHelpers(
            scoped ref NativeBuilderBorrow<uint> borrow) =>
            EmitThroughNestedBorrow(
                ref borrow,
                _wordCount,
                _workerIndex);

        private static void EmitThroughNestedBorrow(
            scoped ref NativeBuilderBorrow<uint> borrow,
            int wordCount,
            int workerIndex)
        {
            borrow.Write(
                wordCount,
                writer => WritePackedWords(
                    ref writer,
                    workerIndex));
        }

        private static void WritePackedWords(
            scoped ref NativeBuilderWriter<uint> writer,
            int workerIndex)
        {
            Span<uint> values = writer.AsSpan();
            FillValues(values, workerIndex);
            writer.Commit(values.Length);
        }
    }
}

internal enum NativeBuilderWriteImplementation
{
    RepeatedAppend,
    BoundedWrite
}

internal sealed record NativeBuilderWriteOptions(
    int RecordsPerBuilder,
    int WorkerCount,
    int SampleCount);

internal sealed record NativeBuilderWriteWorkerEvidence(
    NativeBuilderWriteImplementation Implementation,
    int WorkerCount,
    int RecordsPerBuilder,
    long RecordCount,
    long WordCount,
    long BuilderOperationCallCount,
    double SetupMilliseconds,
    double VerificationMilliseconds,
    double WarmupMilliseconds,
    double ElapsedMilliseconds,
    double RecordsPerSecond,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    long PeakWorkingSetBytes,
    long ManagedHeapBytes,
    long NativeFreshSegmentAllocationDelta,
    long NativeRetainedBytesBefore,
    long NativeRetainedBytesAfter,
    string OutputSha256,
    long Checksum,
    bool ExactParity,
    bool CancellationCleanupPassed,
    bool ExactlyOnceCleanupPassed,
    bool TieredCompilationDisabled,
    bool TieredPgoDisabled);

internal sealed record NativeBuilderWritePairEvidence(
    int SampleIndex,
    NativeBuilderWriteImplementation FirstImplementation,
    NativeBuilderWriteWorkerEvidence RepeatedAppend,
    NativeBuilderWriteWorkerEvidence BoundedWrite,
    double AppendToWriteSpeedup);

internal sealed record NativeBuilderWriteReport(
    NativeBuilderWriteOptions Options,
    IReadOnlyList<NativeBuilderWritePairEvidence> Pairs,
    double RepeatedAppendMeanMilliseconds,
    double BoundedWriteMeanMilliseconds,
    double MeanSpeedup,
    double AggregateSpeedup,
    double ConfidenceLower95,
    double RepeatedAppendMeanRecordsPerSecond,
    double BoundedWriteMeanRecordsPerSecond,
    double RepeatedAppendMeanManagedAllocatedBytes,
    double BoundedWriteMeanManagedAllocatedBytes,
    long RepeatedAppendMaximumPeakWorkingSetBytes,
    long BoundedWriteMaximumPeakWorkingSetBytes,
    bool ExactParity,
    bool BalancedOrder,
    bool RuntimeConfigurationPassed,
    bool ZeroFreshSegmentsPassed,
    bool CleanupPassed,
    bool ProductionShapePassed,
    bool GatePassed,
    double TotalElapsedMilliseconds,
    DateTimeOffset CompletedAtUtc);
