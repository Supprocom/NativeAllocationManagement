using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class ConcurrentArenaBenchmark
{
    internal const int RequiredMapCount = 729;
    internal const int RequiredValuesPerMap = 25_600;
    internal const int RequiredWorkerCount = 24;
    internal const int RequiredSeed = 123_456;
    private const int DefaultWarmupIterations = 1;
    private const int DefaultIterations = 1;
    private const int DefaultSampleCount = 8;
    private static readonly JsonSerializerOptions CompactJsonOptions =
        CreateJsonOptions(writeIndented: false);
    private static readonly JsonSerializerOptions IndentedJsonOptions =
        CreateJsonOptions(writeIndented: true);
    private static long _sink;

    internal static async Task<int> RunCommandAsync(string[] args)
    {
        if (args[0] == "--concurrent-arena-worker")
        {
            ConcurrentArenaBenchmarkImplementation implementation =
                Enum.Parse<ConcurrentArenaBenchmarkImplementation>(
                    ReadRequiredOption(args, "--implementation"),
                    ignoreCase: true);
            ConcurrentArenaBenchmarkOptions options = ParseOptions(args);
            ConcurrentArenaWorkerEvidence evidence = RunWorker(
                implementation,
                options);
            Console.WriteLine(JsonSerializer.Serialize(
                evidence,
                CompactJsonOptions));
            return evidence.ExactParity ? 0 : 3;
        }

        ConcurrentArenaBenchmarkOptions benchmarkOptions =
            ParseOptions(args);
        string? outputPath = ReadOptionalOption(args, "--output");
        ConcurrentArenaBenchmarkReport report = await RunPairedAsync(
            benchmarkOptions);
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

            await File.WriteAllTextAsync(fullPath, json);
        }

        Console.WriteLine(json);
        return report.ExactParity
            && report.BalancedOrder
            && report.RuntimeSettingsValid
            && report.PerformanceAdvantage
                ? 0
                : 3;
    }

    internal static async Task<ConcurrentArenaBenchmarkReport>
        RunPairedAsync(ConcurrentArenaBenchmarkOptions options)
    {
        ValidateOptions(options);
        Stopwatch totalClock = Stopwatch.StartNew();
        ConcurrentArenaPairEvidence[] pairs =
            new ConcurrentArenaPairEvidence[options.SampleCount];
        for (int sampleIndex = 0;
            sampleIndex < options.SampleCount;
            sampleIndex++)
        {
            ConcurrentArenaBenchmarkImplementation[] order =
                GetImplementationOrder(sampleIndex);
            Dictionary<
                ConcurrentArenaBenchmarkImplementation,
                ConcurrentArenaWorkerEvidence> evidence = [];
            foreach (ConcurrentArenaBenchmarkImplementation implementation
                in order)
            {
                evidence.Add(
                    implementation,
                    await RunIsolatedWorkerAsync(
                        implementation,
                        options));
            }

            ConcurrentArenaWorkerEvidence managed = evidence[
                ConcurrentArenaBenchmarkImplementation.ManagedArrays];
            ConcurrentArenaWorkerEvidence pool = evidence[
                ConcurrentArenaBenchmarkImplementation.NativePool];
            ConcurrentArenaWorkerEvidence sharded = evidence[
                ConcurrentArenaBenchmarkImplementation.ShardedArenas];
            ConcurrentArenaWorkerEvidence arena = evidence[
                ConcurrentArenaBenchmarkImplementation.ConcurrentArena];
            ValidatePair(managed, pool, sharded, arena, options);
            pairs[sampleIndex] = new ConcurrentArenaPairEvidence(
                sampleIndex,
                order,
                managed,
                pool,
                sharded,
                arena,
                managed.FullPathMilliseconds
                    / arena.FullPathMilliseconds,
                pool.FullPathMilliseconds
                    / arena.FullPathMilliseconds,
                sharded.FullPathMilliseconds
                    / arena.FullPathMilliseconds);
        }

        totalClock.Stop();
        ConcurrentArenaComparisonEvidence[] comparisons =
        [
            CreateComparison(
                ConcurrentArenaBenchmarkImplementation.ManagedArrays,
                pairs,
                static pair => pair.ManagedArrays.FullPathMilliseconds,
                static pair => pair.ManagedToConcurrentArenaSpeedup),
            CreateComparison(
                ConcurrentArenaBenchmarkImplementation.NativePool,
                pairs,
                static pair => pair.NativePool.FullPathMilliseconds,
                static pair => pair.NativePoolToConcurrentArenaSpeedup),
            CreateComparison(
                ConcurrentArenaBenchmarkImplementation.ShardedArenas,
                pairs,
                static pair => pair.ShardedArenas.FullPathMilliseconds,
                static pair => pair.ShardedToConcurrentArenaSpeedup)
        ];
        bool parity = pairs.All(static pair =>
            pair.ManagedArrays.ExactParity
            && pair.NativePool.ExactParity
            && pair.ShardedArenas.ExactParity
            && pair.ConcurrentArena.ExactParity
            && pair.ManagedArrays.ExactOutputSha256
                == pair.NativePool.ExactOutputSha256
            && pair.ManagedArrays.ExactOutputSha256
                == pair.ShardedArenas.ExactOutputSha256
            && pair.ManagedArrays.ExactOutputSha256
                == pair.ConcurrentArena.ExactOutputSha256
            && pair.ManagedArrays.Checksum == pair.NativePool.Checksum
            && pair.ManagedArrays.Checksum == pair.ShardedArenas.Checksum
            && pair.ManagedArrays.Checksum == pair.ConcurrentArena.Checksum);
        bool balancedOrder = HasBalancedOrder(pairs);
        bool runtimeSettingsValid = pairs.All(static pair =>
            HasRequiredRuntimeSettings(pair.ManagedArrays)
            && HasRequiredRuntimeSettings(pair.NativePool)
            && HasRequiredRuntimeSettings(pair.ShardedArenas)
            && HasRequiredRuntimeSettings(pair.ConcurrentArena));
        ConcurrentArenaComparisonEvidence managedComparison =
            comparisons[0];
        return new ConcurrentArenaBenchmarkReport(
            options,
            pairs,
            comparisons,
            pairs.Average(static pair =>
                (double)pair.ManagedArrays.ManagedAllocatedBytes),
            pairs.Average(static pair =>
                (double)pair.ConcurrentArena.ManagedAllocatedBytes),
            pairs.Average(static pair =>
                (double)pair.ManagedArrays.PeakWorkingSetBytes),
            pairs.Average(static pair =>
                (double)pair.ConcurrentArena.PeakWorkingSetBytes),
            parity,
            balancedOrder,
            runtimeSettingsValid,
            managedComparison.MeanPairedSpeedup > 1d
                && managedComparison.PairedSpeedupConfidenceLower95 > 1d,
            totalClock.Elapsed.TotalMilliseconds,
            DateTimeOffset.UtcNow);
    }

    internal static ConcurrentArenaWorkerEvidence RunWorker(
        ConcurrentArenaBenchmarkImplementation implementation,
        ConcurrentArenaBenchmarkOptions options)
    {
        ValidateOptions(options);
        string expectedHash = CreateExpectedHash(options);
        Stopwatch backingClock = Stopwatch.StartNew();
        using MapWorkload workload = CreateWorkload(
            implementation,
            options);
        backingClock.Stop();

        Stopwatch verificationClock = Stopwatch.StartNew();
        MapPassEvidence verification = workload.RunPass(
            createExactHash: true);
        verificationClock.Stop();
        bool exactParity = string.Equals(
            expectedHash,
            verification.ExactHash,
            StringComparison.Ordinal);
        if (!exactParity)
        {
            throw new InvalidDataException(
                "The concurrent arena verification output changed.");
        }

        Stopwatch warmupClock = Stopwatch.StartNew();
        long warmupChecksum = 0;
        for (int iteration = 0;
            iteration < options.WarmupIterations;
            iteration++)
        {
            warmupChecksum = unchecked(
                warmupChecksum
                + workload.RunPass(createExactHash: false).Checksum);
        }

        warmupClock.Stop();
        NativeOwnerSnapshot statisticsBefore =
            workload.GetNativeStatistics();
        long allocatedBefore = GC.GetTotalAllocatedBytes(
            precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        long heapBefore = GC.GetGCMemoryInfo().HeapSizeBytes;
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        double initializationMilliseconds = 0;
        double publicationMilliseconds = 0;
        double accessMilliseconds = 0;
        double disposalMilliseconds = 0;
        double fullPathMilliseconds = 0;
        long checksum = 0;
        for (int iteration = 0;
            iteration < options.Iterations;
            iteration++)
        {
            MapPassEvidence pass = workload.RunPass(
                createExactHash: false);
            initializationMilliseconds +=
                pass.InitializationMilliseconds;
            publicationMilliseconds += pass.PublicationMilliseconds;
            accessMilliseconds += pass.AccessMilliseconds;
            disposalMilliseconds += pass.DisposalMilliseconds;
            fullPathMilliseconds += pass.FullPathMilliseconds;
            checksum = unchecked(checksum + pass.Checksum);
        }

        process.Refresh();
        long workingSetAfter = process.WorkingSet64;
        long allocated = GC.GetTotalAllocatedBytes(
            precise: true) - allocatedBefore;
        NativeOwnerSnapshot statistics =
            workload.GetNativeStatistics();
        long logicalBytes = checked(
            (long)options.MapCount
            * options.ValuesPerMap
            * sizeof(float)
            * options.Iterations);
        Volatile.Write(
            ref _sink,
            unchecked(checksum + warmupChecksum));
        return new ConcurrentArenaWorkerEvidence(
            implementation,
            options.ValuePattern,
            options.MapCount,
            options.ValuesPerMap,
            checked((long)options.MapCount * options.ValuesPerMap),
            options.WorkerCount,
            options.WarmupIterations,
            options.Iterations,
            options.Seed,
            logicalBytes,
            backingClock.Elapsed.TotalMilliseconds,
            verificationClock.Elapsed.TotalMilliseconds,
            warmupClock.Elapsed.TotalMilliseconds,
            initializationMilliseconds,
            publicationMilliseconds,
            accessMilliseconds,
            disposalMilliseconds,
            fullPathMilliseconds,
            PairedBenchmarkStatistics.LogicalGigabytesPerSecond(
                logicalBytes,
                fullPathMilliseconds),
            allocated,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            heapBefore,
            GC.GetGCMemoryInfo().HeapSizeBytes,
            workingSetBefore,
            workingSetAfter,
            Math.Max(workingSetBefore, workingSetAfter),
            statistics.RetainedBytes,
            statistics.FreshSegmentAllocations,
            statistics.FreshSegmentAllocations
                - statisticsBefore.FreshSegmentAllocations,
            checksum,
            expectedHash,
            GetInformationalVersion(typeof(NativeArena).Assembly),
            GetInformationalVersion(typeof(ConcurrentArenaBenchmark).Assembly),
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredCompilation") ?? "unset",
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredPGO") ?? "unset",
            Environment.ProcessorCount,
            System.Runtime.GCSettings.IsServerGC,
            exactParity);
    }

    internal static ConcurrentArenaBenchmarkImplementation[]
        GetImplementationOrder(int sampleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleIndex);
        ConcurrentArenaBenchmarkImplementation[] implementations =
            Enum.GetValues<ConcurrentArenaBenchmarkImplementation>();
        ConcurrentArenaBenchmarkImplementation[] order =
            new ConcurrentArenaBenchmarkImplementation[
                implementations.Length];
        int offset = sampleIndex % implementations.Length;
        for (int position = 0;
            position < implementations.Length;
            position++)
        {
            order[position] = implementations[
                (position + offset) % implementations.Length];
        }

        return order;
    }

    private static MapWorkload CreateWorkload(
        ConcurrentArenaBenchmarkImplementation implementation,
        ConcurrentArenaBenchmarkOptions options) =>
        implementation switch
        {
            ConcurrentArenaBenchmarkImplementation.ManagedArrays =>
                new ManagedArrayWorkload(options),
            ConcurrentArenaBenchmarkImplementation.NativePool =>
                new NativePoolWorkload(options),
            ConcurrentArenaBenchmarkImplementation.ShardedArenas =>
                new ShardedArenaWorkload(options),
            ConcurrentArenaBenchmarkImplementation.ConcurrentArena =>
                new SingleArenaWorkload(options),
            _ => throw new ArgumentOutOfRangeException(
                nameof(implementation),
                implementation,
                "The concurrent arena implementation is not known.")
        };

    private static ConcurrentArenaComparisonEvidence CreateComparison(
        ConcurrentArenaBenchmarkImplementation baseline,
        IReadOnlyCollection<ConcurrentArenaPairEvidence> pairs,
        Func<ConcurrentArenaPairEvidence, double> baselineTime,
        Func<ConcurrentArenaPairEvidence, double> speedup)
    {
        double[] speedups = pairs.Select(speedup).ToArray();
        return new ConcurrentArenaComparisonEvidence(
            baseline,
            pairs.Average(baselineTime),
            pairs.Average(static pair =>
                pair.ConcurrentArena.FullPathMilliseconds),
            speedups.Average(),
            PairedBenchmarkStatistics.ConfidenceLower95(speedups));
    }

    private static bool HasBalancedOrder(
        IReadOnlyCollection<ConcurrentArenaPairEvidence> pairs)
    {
        int implementationCount = Enum.GetValues<
            ConcurrentArenaBenchmarkImplementation>().Length;
        if (pairs.Count == 0
            || pairs.Count % implementationCount != 0)
        {
            return false;
        }

        int expected = pairs.Count / implementationCount;
        foreach (ConcurrentArenaBenchmarkImplementation implementation
            in Enum.GetValues<ConcurrentArenaBenchmarkImplementation>())
        {
            for (int position = 0;
                position < implementationCount;
                position++)
            {
                if (pairs.Count(pair =>
                    pair.ImplementationOrder[position]
                        == implementation) != expected)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool HasRequiredRuntimeSettings(
        ConcurrentArenaWorkerEvidence evidence) =>
        evidence.TieredCompilation == "0"
        && evidence.TieredPgo == "0";

    private static void ValidatePair(
        ConcurrentArenaWorkerEvidence managed,
        ConcurrentArenaWorkerEvidence pool,
        ConcurrentArenaWorkerEvidence sharded,
        ConcurrentArenaWorkerEvidence arena,
        ConcurrentArenaBenchmarkOptions options)
    {
        ConcurrentArenaWorkerEvidence[] workers =
            [managed, pool, sharded, arena];
        if (managed.Implementation
                != ConcurrentArenaBenchmarkImplementation.ManagedArrays
            || pool.Implementation
                != ConcurrentArenaBenchmarkImplementation.NativePool
            || sharded.Implementation
                != ConcurrentArenaBenchmarkImplementation.ShardedArenas
            || arena.Implementation
                != ConcurrentArenaBenchmarkImplementation.ConcurrentArena
            || workers.Any(worker =>
                worker.MapCount != options.MapCount
                || worker.ValuesPerMap != options.ValuesPerMap
                || worker.WorkerCount != options.WorkerCount
                || worker.Iterations != options.Iterations
                || worker.Seed != options.Seed
                || worker.ValuePattern != options.ValuePattern
                || !worker.ExactParity)
            || workers.Select(static worker => worker.ExactOutputSha256)
                    .Distinct(StringComparer.Ordinal).Count() != 1
            || workers.Select(static worker => worker.Checksum)
                    .Distinct().Count() != 1)
        {
            throw new InvalidDataException(
                "The concurrent arena pair is not equivalent.");
        }
    }

    private static string CreateExpectedHash(
        ConcurrentArenaBenchmarkOptions options)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        float[] values = new float[options.ValuesPerMap];
        for (int mapIndex = 0;
            mapIndex < options.MapCount;
            mapIndex++)
        {
            FillValues(values, mapIndex, options);
            hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void FillValues(
        Span<float> values,
        int mapIndex,
        ConcurrentArenaBenchmarkOptions options)
    {
        float first = CreateValue(mapIndex, 0, options.Seed);
        if (options.ValuePattern == ConcurrentArenaValuePattern.Constant)
        {
            values.Fill(first);
            return;
        }

        for (int valueIndex = 0;
            valueIndex < values.Length;
            valueIndex++)
        {
            values[valueIndex] = CreateValue(
                mapIndex,
                valueIndex,
                options.Seed);
        }
    }

    private static NativeLeaseInitializer<float> CreateNativeInitializer(
        int mapIndex,
        ConcurrentArenaBenchmarkOptions options)
    {
        float first = CreateValue(mapIndex, 0, options.Seed);
        if (options.ValuePattern == ConcurrentArenaValuePattern.Constant)
        {
            return writer => writer.Fill(first);
        }

        NativeSpanInitializer<float> fill = values =>
        {
            FillValues(
                values,
                mapIndex,
                options);
        };

        return writer => writer.InitializeRemaining(fill);
    }

    private static float CreateValue(
        int mapIndex,
        int valueIndex,
        int seed)
    {
        uint value = unchecked(
            (uint)seed
            + ((uint)mapIndex * 0x9E3779B9U)
            + (uint)valueIndex);
        return BitConverter.UInt32BitsToSingle(
            0x3F000000U | (value & 0x007FFFFFU));
    }

    private static long ConsumeValues(ReadOnlySpan<float> values)
    {
        ulong hash = 0xCBF29CE484222325UL;
        foreach (float value in values)
        {
            hash = BitOperations.RotateLeft(hash, 7)
                ^ BitConverter.SingleToUInt32Bits(value);
        }

        return unchecked((long)hash);
    }

    private static long CombineChecksums(ReadOnlySpan<long> checksums)
    {
        ulong combined = 0x9E3779B97F4A7C15UL;
        foreach (long checksum in checksums)
        {
            combined = BitOperations.RotateLeft(combined, 11)
                ^ unchecked((ulong)checksum);
        }

        return unchecked((long)combined);
    }

    private static async Task<ConcurrentArenaWorkerEvidence>
        RunIsolatedWorkerAsync(
            ConcurrentArenaBenchmarkImplementation implementation,
            ConcurrentArenaBenchmarkOptions options)
    {
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The performance process path is not available.");
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = processPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase))
        {
            process.StartInfo.ArgumentList.Add(
                typeof(ConcurrentArenaBenchmark).Assembly.Location);
        }

        AddWorkerArguments(
            process.StartInfo.ArgumentList,
            implementation,
            options);
        process.StartInfo.Environment["DOTNET_TieredCompilation"] = "0";
        process.StartInfo.Environment["DOTNET_TieredPGO"] = "0";
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "The concurrent arena worker did not start.");
        }

        Task<string> outputTask =
            process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask =
            process.StandardError.ReadToEndAsync();
        Task exitTask = process.WaitForExitAsync();
        long peakWorkingSet = 0;
        Stopwatch timeoutClock = Stopwatch.StartNew();
        while (!exitTask.IsCompleted)
        {
            if (timeoutClock.Elapsed > TimeSpan.FromSeconds(120))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException(
                    $"The {implementation} concurrent arena worker exceeded 120 seconds.");
            }

            try
            {
                process.Refresh();
                peakWorkingSet = Math.Max(
                    peakWorkingSet,
                    process.WorkingSet64);
            }
            catch (InvalidOperationException)
            {
                break;
            }

            await Task.WhenAny(
                exitTask,
                Task.Delay(TimeSpan.FromMilliseconds(2)));
        }

        await exitTask;
        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The {implementation} concurrent arena worker failed with exit code {process.ExitCode}: {error}");
        }

        ConcurrentArenaWorkerEvidence evidence =
            JsonSerializer.Deserialize<ConcurrentArenaWorkerEvidence>(
                output.Trim(),
                CompactJsonOptions)
            ?? throw new InvalidDataException(
                "The concurrent arena worker did not return evidence.");
        return evidence with
        {
            PeakWorkingSetBytes = Math.Max(
                peakWorkingSet,
                evidence.PeakWorkingSetBytes)
        };
    }

    private static void AddWorkerArguments(
        ICollection<string> arguments,
        ConcurrentArenaBenchmarkImplementation implementation,
        ConcurrentArenaBenchmarkOptions options)
    {
        arguments.Add("--concurrent-arena-worker");
        arguments.Add("--implementation");
        arguments.Add(implementation.ToString());
        arguments.Add("--maps");
        arguments.Add(options.MapCount.ToString(
            CultureInfo.InvariantCulture));
        arguments.Add("--values");
        arguments.Add(options.ValuesPerMap.ToString(
            CultureInfo.InvariantCulture));
        arguments.Add("--workers");
        arguments.Add(options.WorkerCount.ToString(
            CultureInfo.InvariantCulture));
        arguments.Add("--warmup");
        arguments.Add(options.WarmupIterations.ToString(
            CultureInfo.InvariantCulture));
        arguments.Add("--iterations");
        arguments.Add(options.Iterations.ToString(
            CultureInfo.InvariantCulture));
        arguments.Add("--samples");
        arguments.Add(options.SampleCount.ToString(
            CultureInfo.InvariantCulture));
        arguments.Add("--seed");
        arguments.Add(options.Seed.ToString(
            CultureInfo.InvariantCulture));
        arguments.Add("--pattern");
        arguments.Add(options.ValuePattern.ToString());
    }

    private static ConcurrentArenaBenchmarkOptions ParseOptions(
        string[] args)
    {
        ConcurrentArenaBenchmarkOptions options = new(
            ReadInt32Option(args, "--maps", RequiredMapCount),
            ReadInt32Option(args, "--values", RequiredValuesPerMap),
            ReadInt32Option(args, "--workers", RequiredWorkerCount),
            ReadInt32Option(
                args,
                "--warmup",
                DefaultWarmupIterations),
            ReadInt32Option(
                args,
                "--iterations",
                DefaultIterations),
            ReadInt32Option(
                args,
                "--samples",
                DefaultSampleCount),
            ReadInt32Option(args, "--seed", RequiredSeed),
            Enum.Parse<ConcurrentArenaValuePattern>(
                ReadOptionalOption(args, "--pattern")
                    ?? nameof(ConcurrentArenaValuePattern.Sequential),
                ignoreCase: true));
        ValidateOptions(options);
        return options;
    }

    private static void ValidateOptions(
        ConcurrentArenaBenchmarkOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MapCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.ValuesPerMap);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.WorkerCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            options.WorkerCount,
            options.MapCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.WarmupIterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.Iterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.SampleCount);
        int implementationCount = Enum.GetValues<
            ConcurrentArenaBenchmarkImplementation>().Length;
        if (options.SampleCount % implementationCount != 0)
        {
            throw new ArgumentException(
                "The concurrent arena sample count must balance all positions.",
                nameof(options));
        }
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

    private static string GetInformationalVersion(Assembly assembly) =>
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

    private abstract class MapWorkload : IDisposable
    {
        protected MapWorkload(ConcurrentArenaBenchmarkOptions options)
        {
            Options = options;
            ParallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.WorkerCount
            };
            Checksums = new long[options.MapCount];
        }

        protected ConcurrentArenaBenchmarkOptions Options { get; }

        protected ParallelOptions ParallelOptions { get; }

        protected long[] Checksums { get; }

        internal MapPassEvidence RunPass(bool createExactHash)
        {
            Stopwatch fullClock = Stopwatch.StartNew();
            Stopwatch phaseClock = Stopwatch.StartNew();
            Initialize();
            phaseClock.Stop();
            double initialization = phaseClock.Elapsed.TotalMilliseconds;

            phaseClock.Restart();
            Publish();
            phaseClock.Stop();
            double publication = phaseClock.Elapsed.TotalMilliseconds;

            phaseClock.Restart();
            long checksum = Access();
            string? exactHash = createExactHash
                ? CreateExactHash()
                : null;
            phaseClock.Stop();
            double access = phaseClock.Elapsed.TotalMilliseconds;

            phaseClock.Restart();
            DisposeOutputs();
            phaseClock.Stop();
            double disposal = phaseClock.Elapsed.TotalMilliseconds;
            fullClock.Stop();
            return new MapPassEvidence(
                initialization,
                publication,
                access,
                disposal,
                fullClock.Elapsed.TotalMilliseconds,
                checksum,
                exactHash);
        }

        internal virtual NativeOwnerSnapshot GetNativeStatistics() =>
            default;

        protected void ForEachMap(Action<int, int> action)
        {
            Parallel.For(
                0,
                Options.WorkerCount,
                ParallelOptions,
                workerIndex =>
                {
                    for (int mapIndex = workerIndex;
                        mapIndex < Options.MapCount;
                        mapIndex += Options.WorkerCount)
                    {
                        action(workerIndex, mapIndex);
                    }
                });
        }

        protected abstract void Initialize();

        protected abstract void Publish();

        protected abstract long Access();

        protected abstract string CreateExactHash();

        protected abstract void DisposeOutputs();

        public abstract void Dispose();
    }

    private sealed class ManagedArrayWorkload : MapWorkload
    {
        private readonly ManagedArrayBank[] _banks;
        private readonly ManagedMapSlot[] _producers;
        private readonly ManagedMapSlot[] _consumers;

        internal ManagedArrayWorkload(
            ConcurrentArenaBenchmarkOptions options)
            : base(options)
        {
            _banks = Enumerable.Range(0, options.WorkerCount)
                .Select(workerIndex => new ManagedArrayBank(
                    GetWorkerMapCount(options, workerIndex),
                    options.ValuesPerMap))
                .ToArray();
            _producers = Enumerable.Range(0, options.MapCount)
                .Select(static _ => new ManagedMapSlot())
                .ToArray();
            _consumers = Enumerable.Range(0, options.MapCount)
                .Select(static _ => new ManagedMapSlot())
                .ToArray();
        }

        protected override void Initialize()
        {
            ForEachMap((workerIndex, mapIndex) =>
            {
                float[] values = _banks[workerIndex].Rent();
                FillValues(
                    values.AsSpan(0, Options.ValuesPerMap),
                    mapIndex,
                    Options);
                _producers[mapIndex].Receive(
                    values,
                    workerIndex,
                    Options.ValuesPerMap);
            });
        }

        protected override void Publish()
        {
            ForEachMap((_, mapIndex) =>
            {
                ManagedMapTransfer transfer =
                    _producers[mapIndex].Take();
                _consumers[mapIndex].Receive(transfer);
            });
        }

        protected override long Access()
        {
            ForEachMap((_, mapIndex) =>
            {
                Checksums[mapIndex] =
                    _consumers[mapIndex].Consume();
            });
            return CombineChecksums(Checksums);
        }

        protected override string CreateExactHash()
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            foreach (ManagedMapSlot slot in _consumers)
            {
                slot.AppendHash(hash);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        protected override void DisposeOutputs()
        {
            ForEachMap((_, mapIndex) =>
            {
                _consumers[mapIndex].Return(_banks);
            });
        }

        public override void Dispose()
        {
            foreach (ManagedMapSlot slot in _producers)
            {
                slot.Return(_banks);
            }

            foreach (ManagedMapSlot slot in _consumers)
            {
                slot.Return(_banks);
            }
        }
    }

    private abstract class NativeMapWorkload : MapWorkload
    {
        private readonly NativeMapSlot[] _producers;
        private readonly NativeMapSlot[] _consumers;
        private readonly NativeLeaseInitializer<float>[] _initializers;

        protected NativeMapWorkload(
            ConcurrentArenaBenchmarkOptions options)
            : base(options)
        {
            _producers = Enumerable.Range(0, options.MapCount)
                .Select(static _ => new NativeMapSlot())
                .ToArray();
            _consumers = Enumerable.Range(0, options.MapCount)
                .Select(static _ => new NativeMapSlot())
                .ToArray();
            _initializers = Enumerable.Range(0, options.MapCount)
                .Select(mapIndex => CreateNativeInitializer(
                    mapIndex,
                    options))
                .ToArray();
        }

        protected sealed override void Initialize()
        {
            ForEachMap((workerIndex, mapIndex) =>
            {
                NativeTransfer<float>? transfer = Rent(
                    workerIndex,
                    mapIndex,
                    _initializers[mapIndex]);
                try
                {
                    _producers[mapIndex].Receive(
                        NativeTransfer<float>.Move(ref transfer));
                }
                finally
                {
                    transfer?.Dispose();
                }
            });
        }

        protected sealed override void Publish()
        {
            ForEachMap((_, mapIndex) =>
            {
                _consumers[mapIndex].Receive(
                    _producers[mapIndex].Take());
            });
        }

        protected sealed override long Access()
        {
            ForEachMap((_, mapIndex) =>
            {
                Checksums[mapIndex] =
                    _consumers[mapIndex].Consume();
            });
            return CombineChecksums(Checksums);
        }

        protected sealed override string CreateExactHash()
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            foreach (NativeMapSlot slot in _consumers)
            {
                slot.AppendHash(hash);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        protected sealed override void DisposeOutputs()
        {
            ForEachMap((_, mapIndex) =>
            {
                _consumers[mapIndex].DisposeTransfer();
            });
        }

        protected abstract NativeTransfer<float> Rent(
            int workerIndex,
            int mapIndex,
            NativeLeaseInitializer<float> initializer);

        protected void DisposeSlots()
        {
            foreach (NativeMapSlot slot in _producers)
            {
                slot.DisposeTransfer();
            }

            foreach (NativeMapSlot slot in _consumers)
            {
                slot.DisposeTransfer();
            }
        }
    }

    private sealed class NativePoolWorkload : NativeMapWorkload
    {
        private readonly NativePool<float> _pool;

        internal NativePoolWorkload(
            ConcurrentArenaBenchmarkOptions options)
            : base(options)
        {
            _pool = new NativePool<float>(
                options.ValuesPerMap,
                NativeMemoryReturn.ToNativeMemory);
        }

        protected override NativeTransfer<float> Rent(
            int workerIndex,
            int mapIndex,
            NativeLeaseInitializer<float> initializer) =>
            _pool.RentTransferable(
                Options.ValuesPerMap,
                initializer);

        internal override NativeOwnerSnapshot GetNativeStatistics()
        {
            NativeOwnerStatistics statistics = _pool.GetStatistics();
            return new(
                statistics.RetainedBytes,
                statistics.FreshSegmentAllocationCount);
        }

        public override void Dispose()
        {
            DisposeSlots();
            _pool.Dispose();
        }
    }

    private sealed class SingleArenaWorkload : NativeMapWorkload
    {
        private readonly NativeArena _arena;

        internal SingleArenaWorkload(
            ConcurrentArenaBenchmarkOptions options)
            : base(options)
        {
            _arena = new NativeArena(
                checked(
                    (nuint)options.MapCount
                    * (nuint)options.ValuesPerMap
                    * sizeof(float)),
                NativeMemoryReturn.ToNativeMemory);
        }

        protected override NativeTransfer<float> Rent(
            int workerIndex,
            int mapIndex,
            NativeLeaseInitializer<float> initializer) =>
            _arena.ScratchTransferable<float>(
                Options.ValuesPerMap,
                initializer);

        internal override NativeOwnerSnapshot GetNativeStatistics()
        {
            NativeOwnerStatistics statistics = _arena.GetStatistics();
            return new(
                statistics.RetainedBytes,
                statistics.FreshSegmentAllocationCount);
        }

        public override void Dispose()
        {
            DisposeSlots();
            _arena.Dispose();
        }
    }

    private sealed class ShardedArenaWorkload : NativeMapWorkload
    {
        private readonly NativeArena[] _arenas;

        internal ShardedArenaWorkload(
            ConcurrentArenaBenchmarkOptions options)
            : base(options)
        {
            _arenas = Enumerable.Range(0, options.WorkerCount)
                .Select(workerIndex => new NativeArena(
                    checked(
                        (nuint)GetWorkerMapCount(options, workerIndex)
                        * (nuint)options.ValuesPerMap
                        * sizeof(float)),
                    NativeMemoryReturn.ToNativeMemory))
                .ToArray();
        }

        protected override NativeTransfer<float> Rent(
            int workerIndex,
            int mapIndex,
            NativeLeaseInitializer<float> initializer) =>
            _arenas[workerIndex].ScratchTransferable<float>(
                Options.ValuesPerMap,
                initializer);

        internal override NativeOwnerSnapshot GetNativeStatistics()
        {
            long retainedBytes = 0;
            long freshAllocations = 0;
            foreach (NativeArena arena in _arenas)
            {
                NativeOwnerStatistics statistics = arena.GetStatistics();
                retainedBytes = checked(
                    retainedBytes + statistics.RetainedBytes);
                freshAllocations = checked(
                    freshAllocations
                    + statistics.FreshSegmentAllocationCount);
            }

            return new(retainedBytes, freshAllocations);
        }

        public override void Dispose()
        {
            DisposeSlots();
            foreach (NativeArena arena in _arenas)
            {
                arena.Dispose();
            }
        }
    }

    private sealed class NativeMapSlot
    {
        private NativeTransfer<float>? _transfer;

        internal void Receive(NativeTransfer<float>? transfer)
        {
            if (_transfer is not null)
            {
                throw new InvalidOperationException(
                    "The native map slot already owns a transfer.");
            }

            _transfer = NativeTransfer<float>.Move(ref transfer);
        }

        internal NativeTransfer<float> Take()
        {
            return NativeTransfer<float>.Move(ref _transfer);
        }

        internal long Consume()
        {
            NativeTransfer<float> transfer = _transfer
                ?? throw new InvalidOperationException(
                    "The native map slot has no transfer.");
            return transfer.Read(
                static view => ConsumeValues(view.AsSpan()));
        }

        internal void AppendHash(IncrementalHash hash)
        {
            NativeTransfer<float> transfer = _transfer
                ?? throw new InvalidOperationException(
                    "The native map slot has no transfer.");
            transfer.Access(view => hash.AppendData(
                MemoryMarshal.AsBytes(view.AsSpan())));
        }

        internal void DisposeTransfer()
        {
            NativeTransfer<float>? transfer = _transfer;
            _transfer = null;
            transfer?.Dispose();
        }
    }

    private sealed class ManagedMapSlot
    {
        private float[]? _values;
        private int _workerIndex;
        private int _length;

        internal void Receive(
            float[] values,
            int workerIndex,
            int length)
        {
            if (_values is not null)
            {
                throw new InvalidOperationException(
                    "The managed map slot already owns an array.");
            }

            _values = values;
            _workerIndex = workerIndex;
            _length = length;
        }

        internal void Receive(ManagedMapTransfer transfer)
        {
            Receive(
                transfer.Values,
                transfer.WorkerIndex,
                transfer.Length);
        }

        internal ManagedMapTransfer Take()
        {
            float[] values = _values
                ?? throw new InvalidOperationException(
                    "The managed map slot has no array.");
            ManagedMapTransfer transfer = new(
                values,
                _workerIndex,
                _length);
            _values = null;
            _workerIndex = 0;
            _length = 0;
            return transfer;
        }

        internal long Consume()
        {
            float[] values = _values
                ?? throw new InvalidOperationException(
                    "The managed map slot has no array.");
            return ConsumeValues(values.AsSpan(0, _length));
        }

        internal void AppendHash(IncrementalHash hash)
        {
            float[] values = _values
                ?? throw new InvalidOperationException(
                    "The managed map slot has no array.");
            hash.AppendData(MemoryMarshal.AsBytes(
                values.AsSpan(0, _length)));
        }

        internal void Return(ManagedArrayBank[] banks)
        {
            float[]? values = _values;
            if (values is null)
            {
                return;
            }

            int workerIndex = _workerIndex;
            _values = null;
            _workerIndex = 0;
            _length = 0;
            banks[workerIndex].Return(values);
        }
    }

    private sealed class ManagedArrayBank
    {
        private readonly Stack<float[]> _values;

        internal ManagedArrayBank(int count, int length)
        {
            _values = new Stack<float[]>(count);
            for (int index = 0; index < count; index++)
            {
                _values.Push(new float[length]);
            }
        }

        internal float[] Rent() => _values.Pop();

        internal void Return(float[] values) => _values.Push(values);
    }

    private static int GetWorkerMapCount(
        ConcurrentArenaBenchmarkOptions options,
        int workerIndex) =>
        workerIndex >= options.MapCount
            ? 0
            : ((options.MapCount - 1 - workerIndex)
                / options.WorkerCount) + 1;

    private readonly record struct ManagedMapTransfer(
        float[] Values,
        int WorkerIndex,
        int Length);

    private readonly record struct MapPassEvidence(
        double InitializationMilliseconds,
        double PublicationMilliseconds,
        double AccessMilliseconds,
        double DisposalMilliseconds,
        double FullPathMilliseconds,
        long Checksum,
        string? ExactHash);

    private readonly record struct NativeOwnerSnapshot(
        long RetainedBytes,
        long FreshSegmentAllocations);
}
