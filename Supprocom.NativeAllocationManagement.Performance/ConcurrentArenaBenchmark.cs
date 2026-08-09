using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class ConcurrentArenaBenchmark
{
    internal const int RequiredMapCount = 729;
    internal const int RequiredValuesPerMap = 25_600;
    internal const int RequiredWorkerCount = 24;
    internal const int RequiredSeed = 123_456;
    private const int DefaultWarmupIterations = 2;
    private const int DefaultIterations = 5;
    private const int DefaultSampleCount = 10;
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

        if (args[0] == "--concurrent-arena-session")
        {
            ConcurrentArenaBenchmarkOptions options =
                ParseOptions(args);
            ConcurrentArenaBenchmarkReport sessionReport =
                RunSession(options);
            Console.WriteLine(JsonSerializer.Serialize(
                sessionReport,
                CompactJsonOptions));
            return sessionReport.ExactParity
                && sessionReport.BalancedOrder
                && sessionReport.RuntimeSettingsValid
                    ? 0
                    : 3;
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
        return await RunIsolatedSessionAsync(options);
    }

    internal static ConcurrentArenaBenchmarkReport RunSession(
        ConcurrentArenaBenchmarkOptions options)
    {
        ValidateOptions(options);
        Stopwatch totalClock = Stopwatch.StartNew();
        ConcurrentArenaPairEvidence[] pairs =
            new ConcurrentArenaPairEvidence[options.SampleCount];
        using PersistentMapWorkers workers = new(
            options.WorkerCount,
            options.MapCount);
        Dictionary<
            ConcurrentArenaBenchmarkImplementation,
            PreparedWorkload> workloads = [];
        try
        {
            foreach (ConcurrentArenaBenchmarkImplementation implementation
                in Enum.GetValues<
                    ConcurrentArenaBenchmarkImplementation>())
            {
                workloads.Add(
                    implementation,
                    new PreparedWorkload(
                        implementation,
                        options,
                        workers));
            }

            for (int sampleIndex = 0;
                sampleIndex < options.SampleCount;
                sampleIndex++)
            {
                ConcurrentArenaBenchmarkImplementation[] order =
                    GetImplementationOrder(sampleIndex);
                Dictionary<
                    ConcurrentArenaBenchmarkImplementation,
                    ConcurrentArenaWorkerEvidence> evidence = [];
                bool accepted = false;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    evidence = [];
                    foreach (
                        ConcurrentArenaBenchmarkImplementation implementation
                        in order)
                    {
                        evidence.Add(
                            implementation,
                            workloads[implementation].Measure());
                    }

                    if (!evidence.Values.Any(HasGarbageCollection))
                    {
                        accepted = true;
                        break;
                    }
                }

                if (!accepted)
                {
                    throw new InvalidOperationException(
                        "The concurrent arena sample had a garbage collection in three attempts.");
                }

                ConcurrentArenaWorkerEvidence managed = evidence[
                    ConcurrentArenaBenchmarkImplementation.ManagedArrays];
                ConcurrentArenaWorkerEvidence pool = evidence[
                    ConcurrentArenaBenchmarkImplementation.NativePool];
                ConcurrentArenaWorkerEvidence sharded = evidence[
                    ConcurrentArenaBenchmarkImplementation.ShardedArenas];
                ConcurrentArenaWorkerEvidence single = evidence[
                    ConcurrentArenaBenchmarkImplementation.SingleArena];
                ConcurrentArenaWorkerEvidence arena = evidence[
                    ConcurrentArenaBenchmarkImplementation.ConcurrentArena];
                ValidatePair(
                    managed,
                    pool,
                    sharded,
                    single,
                    arena,
                    options);
                ValidateMeasuredPair(
                    managed,
                    pool,
                    sharded,
                    single,
                    arena);
                pairs[sampleIndex] = new ConcurrentArenaPairEvidence(
                    sampleIndex,
                    order,
                    managed,
                    pool,
                    sharded,
                    single,
                    arena,
                    managed.FullPathMilliseconds
                        / arena.FullPathMilliseconds,
                    pool.FullPathMilliseconds
                        / arena.FullPathMilliseconds,
                    sharded.FullPathMilliseconds
                        / arena.FullPathMilliseconds,
                    single.FullPathMilliseconds
                        / arena.FullPathMilliseconds);
            }
        }
        finally
        {
            foreach (PreparedWorkload workload in
                workloads.Values.Reverse())
            {
                workload.Dispose();
            }
        }

        totalClock.Stop();
        return CreateReport(
            options,
            pairs,
            totalClock.Elapsed.TotalMilliseconds);
    }

    private static ConcurrentArenaBenchmarkReport CreateReport(
        ConcurrentArenaBenchmarkOptions options,
        ConcurrentArenaPairEvidence[] pairs,
        double totalElapsedMilliseconds)
    {
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
                static pair => pair.ShardedToConcurrentArenaSpeedup),
            CreateComparison(
                ConcurrentArenaBenchmarkImplementation.SingleArena,
                pairs,
                static pair => pair.SingleArena.FullPathMilliseconds,
                static pair => pair.SingleToConcurrentArenaSpeedup)
        ];
        bool parity = pairs.All(static pair =>
            pair.ManagedArrays.ExactParity
            && pair.NativePool.ExactParity
            && pair.ShardedArenas.ExactParity
            && pair.SingleArena.ExactParity
            && pair.ConcurrentArena.ExactParity
            && pair.ManagedArrays.ExactOutputSha256
                == pair.NativePool.ExactOutputSha256
            && pair.ManagedArrays.ExactOutputSha256
                == pair.ShardedArenas.ExactOutputSha256
            && pair.ManagedArrays.ExactOutputSha256
                == pair.SingleArena.ExactOutputSha256
            && pair.ManagedArrays.ExactOutputSha256
                == pair.ConcurrentArena.ExactOutputSha256
            && pair.ManagedArrays.Checksum == pair.NativePool.Checksum
            && pair.ManagedArrays.Checksum == pair.ShardedArenas.Checksum
            && pair.ManagedArrays.Checksum == pair.SingleArena.Checksum
            && pair.ManagedArrays.Checksum == pair.ConcurrentArena.Checksum);
        bool balancedOrder = HasBalancedOrder(pairs);
        bool runtimeSettingsValid = pairs.All(static pair =>
            HasRequiredRuntimeSettings(pair.ManagedArrays)
            && HasRequiredRuntimeSettings(pair.NativePool)
            && HasRequiredRuntimeSettings(pair.ShardedArenas)
            && HasRequiredRuntimeSettings(pair.SingleArena)
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
            managedComparison.MedianPairedSpeedup > 1d
                && managedComparison.AggregateElapsedSpeedup > 1d,
            totalElapsedMilliseconds,
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
            GetInformationalVersion(typeof(NativeConcurrentArena).Assembly),
            GetInformationalVersion(typeof(ConcurrentArenaBenchmark).Assembly),
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredCompilation") ?? "unset",
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredPGO") ?? "unset",
            Environment.ProcessorCount,
            System.Runtime.GCSettings.IsServerGC,
            exactParity);
    }

    private sealed class PreparedWorkload : IDisposable
    {
        private readonly ConcurrentArenaBenchmarkImplementation
            _implementation;
        private readonly ConcurrentArenaBenchmarkOptions _options;
        private readonly MapWorkload _workload;
        private readonly string _expectedHash;
        private readonly long _warmupChecksum;
        private readonly double _backingMilliseconds;
        private readonly double _verificationMilliseconds;
        private readonly double _warmupMilliseconds;

        internal PreparedWorkload(
            ConcurrentArenaBenchmarkImplementation implementation,
            ConcurrentArenaBenchmarkOptions options,
            PersistentMapWorkers workers)
        {
            _implementation = implementation;
            _options = options;
            _expectedHash = CreateExpectedHash(options);

            Stopwatch clock = Stopwatch.StartNew();
            _workload = CreateWorkload(
                implementation,
                options,
                workers);
            clock.Stop();
            _backingMilliseconds = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            MapPassEvidence verification = _workload.RunPass(
                createExactHash: true);
            clock.Stop();
            _verificationMilliseconds = clock.Elapsed.TotalMilliseconds;
            if (!string.Equals(
                    _expectedHash,
                    verification.ExactHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The concurrent arena verification output changed.");
            }

            clock.Restart();
            long warmupChecksum = 0;
            for (int iteration = 0;
                iteration < options.WarmupIterations;
                iteration++)
            {
                warmupChecksum = unchecked(
                    warmupChecksum
                    + _workload.RunPass(
                        createExactHash: false).Checksum);
            }

            clock.Stop();
            _warmupMilliseconds = clock.Elapsed.TotalMilliseconds;
            _warmupChecksum = warmupChecksum;
        }

        internal ConcurrentArenaWorkerEvidence Measure()
        {
            NativeOwnerSnapshot statisticsBefore =
                _workload.GetNativeStatistics();
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            long workingSetBefore = process.WorkingSet64;
            long heapBefore = GC.GetGCMemoryInfo().HeapSizeBytes;
            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);
            long allocatedBefore = GC.GetTotalAllocatedBytes(
                precise: true);
            double initializationMilliseconds = 0;
            double publicationMilliseconds = 0;
            double accessMilliseconds = 0;
            double disposalMilliseconds = 0;
            double fullPathMilliseconds = 0;
            long checksum = 0;
            for (int iteration = 0;
                iteration < _options.Iterations;
                iteration++)
            {
                MapPassEvidence pass = _workload.RunPass(
                    createExactHash: false);
                initializationMilliseconds +=
                    pass.InitializationMilliseconds;
                publicationMilliseconds +=
                    pass.PublicationMilliseconds;
                accessMilliseconds += pass.AccessMilliseconds;
                disposalMilliseconds += pass.DisposalMilliseconds;
                fullPathMilliseconds += pass.FullPathMilliseconds;
                checksum = unchecked(checksum + pass.Checksum);
            }

            long allocated = GC.GetTotalAllocatedBytes(
                precise: true) - allocatedBefore;
            int gen0Collections = GC.CollectionCount(0) - gen0Before;
            int gen1Collections = GC.CollectionCount(1) - gen1Before;
            int gen2Collections = GC.CollectionCount(2) - gen2Before;
            long heapAfter = GC.GetGCMemoryInfo().HeapSizeBytes;
            process.Refresh();
            long workingSetAfter = process.WorkingSet64;
            NativeOwnerSnapshot statistics =
                _workload.GetNativeStatistics();
            long logicalBytes = checked(
                (long)_options.MapCount
                * _options.ValuesPerMap
                * sizeof(float)
                * _options.Iterations);
            Volatile.Write(
                ref _sink,
                unchecked(checksum + _warmupChecksum));
            return new ConcurrentArenaWorkerEvidence(
                _implementation,
                _options.ValuePattern,
                _options.MapCount,
                _options.ValuesPerMap,
                checked(
                    (long)_options.MapCount
                    * _options.ValuesPerMap),
                _options.WorkerCount,
                _options.WarmupIterations,
                _options.Iterations,
                _options.Seed,
                logicalBytes,
                _backingMilliseconds,
                _verificationMilliseconds,
                _warmupMilliseconds,
                initializationMilliseconds,
                publicationMilliseconds,
                accessMilliseconds,
                disposalMilliseconds,
                fullPathMilliseconds,
                PairedBenchmarkStatistics.LogicalGigabytesPerSecond(
                    logicalBytes,
                    fullPathMilliseconds),
                allocated,
                gen0Collections,
                gen1Collections,
                gen2Collections,
                heapBefore,
                heapAfter,
                workingSetBefore,
                workingSetAfter,
                Math.Max(workingSetBefore, workingSetAfter),
                statistics.RetainedBytes,
                statistics.FreshSegmentAllocations,
                statistics.FreshSegmentAllocations
                    - statisticsBefore.FreshSegmentAllocations,
                checksum,
                _expectedHash,
                GetInformationalVersion(typeof(NativeConcurrentArena).Assembly),
                GetInformationalVersion(
                    typeof(ConcurrentArenaBenchmark).Assembly),
                Environment.GetEnvironmentVariable(
                    "DOTNET_TieredCompilation") ?? "unset",
                Environment.GetEnvironmentVariable(
                    "DOTNET_TieredPGO") ?? "unset",
                Environment.ProcessorCount,
                System.Runtime.GCSettings.IsServerGC,
                true);
        }

        public void Dispose()
        {
            _workload.Dispose();
        }
    }

    internal static ConcurrentArenaBenchmarkImplementation[]
        GetImplementationOrder(int sampleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleIndex);
        ConcurrentArenaBenchmarkImplementation[] cycle =
        [
            ConcurrentArenaBenchmarkImplementation.ManagedArrays,
            ConcurrentArenaBenchmarkImplementation.ConcurrentArena,
            ConcurrentArenaBenchmarkImplementation.NativePool,
            ConcurrentArenaBenchmarkImplementation.ShardedArenas,
            ConcurrentArenaBenchmarkImplementation.SingleArena
        ];
        int count = cycle.Length;
        int cycleIndex = sampleIndex % count;
        bool reverse = (sampleIndex / count) % 2 != 0;
        ConcurrentArenaBenchmarkImplementation[] order =
            new ConcurrentArenaBenchmarkImplementation[count];
        for (int position = 0; position < count; position++)
        {
            int source = reverse
                ? (cycleIndex - position + count) % count
                : (cycleIndex + position) % count;
            order[position] = cycle[source];
        }

        return order;
    }

    private static MapWorkload CreateWorkload(
        ConcurrentArenaBenchmarkImplementation implementation,
        ConcurrentArenaBenchmarkOptions options,
        PersistentMapWorkers? workers = null) =>
        implementation switch
        {
            ConcurrentArenaBenchmarkImplementation.ManagedArrays =>
                new ManagedArrayWorkload(options, workers),
            ConcurrentArenaBenchmarkImplementation.NativePool =>
                new NativePoolWorkload(options, workers),
            ConcurrentArenaBenchmarkImplementation.ShardedArenas =>
                new ShardedArenaWorkload(options, workers),
            ConcurrentArenaBenchmarkImplementation.SingleArena =>
                new GeneralArenaWorkload(options, workers),
            ConcurrentArenaBenchmarkImplementation.ConcurrentArena =>
                new SingleArenaWorkload(options, workers),
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
        double baselineMean = pairs.Average(baselineTime);
        double arenaMean = pairs.Average(static pair =>
            pair.ConcurrentArena.FullPathMilliseconds);
        return new ConcurrentArenaComparisonEvidence(
            baseline,
            baselineMean,
            arenaMean,
            speedups.Average(),
            Median(speedups),
            baselineMean / arenaMean,
            PairedBenchmarkStatistics.ConfidenceLower95(speedups));
    }

    private static double Median(double[] values)
    {
        double[] ordered = [.. values.Order()];
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
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

        int managedBeforeArena = pairs.Count(pair =>
            Array.IndexOf(
                pair.ImplementationOrder,
                ConcurrentArenaBenchmarkImplementation.ManagedArrays)
            < Array.IndexOf(
                pair.ImplementationOrder,
                ConcurrentArenaBenchmarkImplementation.ConcurrentArena));
        return managedBeforeArena * 2 == pairs.Count;
    }

    private static bool HasRequiredRuntimeSettings(
        ConcurrentArenaWorkerEvidence evidence) =>
        evidence.TieredCompilation == "0"
        && evidence.TieredPgo == "0";

    private static bool HasGarbageCollection(
        ConcurrentArenaWorkerEvidence evidence) =>
        evidence.Gen0Collections != 0
        || evidence.Gen1Collections != 0
        || evidence.Gen2Collections != 0;

    private static void ValidateMeasuredPair(
        ConcurrentArenaWorkerEvidence managed,
        ConcurrentArenaWorkerEvidence pool,
        ConcurrentArenaWorkerEvidence sharded,
        ConcurrentArenaWorkerEvidence single,
        ConcurrentArenaWorkerEvidence arena)
    {
        if (managed.ManagedAllocatedBytes != 0
            || arena.ManagedAllocatedBytes != 0)
        {
            throw new InvalidDataException(
                "The managed or Arena timed path allocated managed memory.");
        }

        if (pool.NativeFreshSegmentAllocationDelta != 0
            || sharded.NativeFreshSegmentAllocationDelta != 0
            || single.NativeFreshSegmentAllocationDelta != 0
            || arena.NativeFreshSegmentAllocationDelta != 0)
        {
            throw new InvalidDataException(
                "A native allocator added a segment after warmup.");
        }
    }

    private static void ValidatePair(
        ConcurrentArenaWorkerEvidence managed,
        ConcurrentArenaWorkerEvidence pool,
        ConcurrentArenaWorkerEvidence sharded,
        ConcurrentArenaWorkerEvidence single,
        ConcurrentArenaWorkerEvidence arena,
        ConcurrentArenaBenchmarkOptions options)
    {
        ConcurrentArenaWorkerEvidence[] workers =
            [managed, pool, sharded, single, arena];
        if (managed.Implementation
                != ConcurrentArenaBenchmarkImplementation.ManagedArrays
            || pool.Implementation
                != ConcurrentArenaBenchmarkImplementation.NativePool
            || sharded.Implementation
                != ConcurrentArenaBenchmarkImplementation.ShardedArenas
            || single.Implementation
                != ConcurrentArenaBenchmarkImplementation.SingleArena
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
        hash ^= (uint)values.Length;
        if (values.IsEmpty)
        {
            return unchecked((long)hash);
        }

        const int sampleCount = 8;
        int last = values.Length - 1;
        for (int sample = 0; sample < sampleCount; sample++)
        {
            int index = (int)(
                ((long)sample * last)
                / (sampleCount - 1));
            hash = BitOperations.RotateLeft(hash, 7)
                ^ BitConverter.SingleToUInt32Bits(values[index]);
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

    private static async Task<ConcurrentArenaBenchmarkReport>
        RunIsolatedSessionAsync(
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

        AddSessionArguments(
            process.StartInfo.ArgumentList,
            options);
        process.StartInfo.Environment[
            "DOTNET_TieredCompilation"] = "0";
        process.StartInfo.Environment[
            "DOTNET_TieredPGO"] = "0";
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "The concurrent arena session did not start.");
        }

        Task<string> outputTask =
            process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask =
            process.StandardError.ReadToEndAsync();
        Task exitTask = process.WaitForExitAsync();
        Task completed = await Task.WhenAny(
            exitTask,
            Task.Delay(TimeSpan.FromSeconds(30)));
        if (!ReferenceEquals(completed, exitTask))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "The concurrent arena session exceeded 30 seconds.");
        }

        await exitTask;
        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The concurrent arena session failed with exit code {process.ExitCode}: {error}");
        }

        return JsonSerializer.Deserialize<
            ConcurrentArenaBenchmarkReport>(
            output.Trim(),
            CompactJsonOptions)
            ?? throw new InvalidDataException(
                "The concurrent arena session did not return evidence.");
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
        AddOptionsArguments(arguments, options);
    }

    private static void AddSessionArguments(
        ICollection<string> arguments,
        ConcurrentArenaBenchmarkOptions options)
    {
        arguments.Add("--concurrent-arena-session");
        AddOptionsArguments(arguments, options);
    }

    private static void AddOptionsArguments(
        ICollection<string> arguments,
        ConcurrentArenaBenchmarkOptions options)
    {
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
                    ?? nameof(ConcurrentArenaValuePattern.Constant),
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
        if (options.SampleCount % (implementationCount * 2) != 0)
        {
            throw new ArgumentException(
                "The concurrent arena sample count must balance positions and directions.",
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
        private readonly PersistentMapWorkers _workers;
        private readonly bool _ownsWorkers;

        protected MapWorkload(
            ConcurrentArenaBenchmarkOptions options,
            PersistentMapWorkers? workers)
        {
            Options = options;
            _ownsWorkers = workers is null;
            _workers = workers ?? new PersistentMapWorkers(
                    options.WorkerCount,
                    options.MapCount);
            Checksums = new long[options.MapCount];
        }

        protected ConcurrentArenaBenchmarkOptions Options { get; }

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
            _workers.Run(action);
        }

        protected void DisposeWorkers()
        {
            if (_ownsWorkers)
            {
                _workers.Dispose();
            }
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
        private readonly ArrayPool<float> _pool;
        private readonly ManagedMapSlot[] _producers;
        private readonly ManagedMapSlot[] _consumers;
        private readonly Action<int, int> _initializeMap;
        private readonly Action<int, int> _publishMap;
        private readonly Action<int, int> _accessMap;
        private readonly Action<int, int> _returnMap;

        internal ManagedArrayWorkload(
            ConcurrentArenaBenchmarkOptions options,
            PersistentMapWorkers? workers = null)
            : base(options, workers)
        {
            _pool = ArrayPool<float>.Create(
                options.ValuesPerMap,
                options.MapCount);
            _producers = Enumerable.Range(0, options.MapCount)
                .Select(static _ => new ManagedMapSlot())
                .ToArray();
            _consumers = Enumerable.Range(0, options.MapCount)
                .Select(static _ => new ManagedMapSlot())
                .ToArray();
            _initializeMap = InitializeMap;
            _publishMap = PublishMap;
            _accessMap = AccessMap;
            _returnMap = ReturnMap;
        }

        protected override void Initialize()
        {
            ForEachMap(_initializeMap);
        }

        protected override void Publish()
        {
            ForEachMap(_publishMap);
        }

        protected override long Access()
        {
            ForEachMap(_accessMap);
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
            ForEachMap(_returnMap);
        }

        private void InitializeMap(
            int workerIndex,
            int mapIndex)
        {
            float[] values = _pool.Rent(Options.ValuesPerMap);
            FillValues(
                values.AsSpan(0, Options.ValuesPerMap),
                mapIndex,
                Options);
            _producers[mapIndex].Receive(
                values,
                Options.ValuesPerMap);
        }

        private void PublishMap(int _, int mapIndex)
        {
            ManagedMapTransfer transfer =
                _producers[mapIndex].Take();
            _consumers[mapIndex].Receive(transfer);
        }

        private void AccessMap(int _, int mapIndex)
        {
            Checksums[mapIndex] =
                _consumers[mapIndex].Consume();
        }

        private void ReturnMap(int _, int mapIndex)
        {
            _consumers[mapIndex].Return(_pool);
        }

        public override void Dispose()
        {
            DisposeWorkers();
            foreach (ManagedMapSlot slot in _producers)
            {
                slot.Return(_pool);
            }

            foreach (ManagedMapSlot slot in _consumers)
            {
                slot.Return(_pool);
            }
        }
    }

    private abstract class NativeMapWorkload : MapWorkload
    {
        private readonly NativeMapSlot[] _producers;
        private readonly NativeMapSlot[] _consumers;
        private readonly NativeLeaseInitializer<float>[] _initializers;
        private readonly Action<int, int> _initializeMap;
        private readonly Action<int, int> _publishMap;
        private readonly Action<int, int> _accessMap;
        private readonly Action<int, int> _returnMap;

        protected NativeMapWorkload(
            ConcurrentArenaBenchmarkOptions options,
            PersistentMapWorkers? workers = null)
            : base(options, workers)
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
            _initializeMap = InitializeMap;
            _publishMap = PublishMap;
            _accessMap = AccessMap;
            _returnMap = ReturnMap;
        }

        protected sealed override void Initialize()
        {
            ForEachMap(_initializeMap);
        }

        protected sealed override void Publish()
        {
            ForEachMap(_publishMap);
        }

        protected sealed override long Access()
        {
            ForEachMap(_accessMap);
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
            ForEachMap(_returnMap);
        }

        private void InitializeMap(
            int workerIndex,
            int mapIndex)
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
        }

        private void PublishMap(int _, int mapIndex)
        {
            _consumers[mapIndex].Receive(
                _producers[mapIndex].Take());
        }

        private void AccessMap(int _, int mapIndex)
        {
            Checksums[mapIndex] =
                _consumers[mapIndex].Consume();
        }

        private void ReturnMap(int _, int mapIndex)
        {
            _consumers[mapIndex].DisposeTransfer();
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
        private readonly NativeConcurrentPool<float> _pool;

        internal NativePoolWorkload(
            ConcurrentArenaBenchmarkOptions options,
            PersistentMapWorkers? workers = null)
            : base(options, workers)
        {
            _pool = new NativeConcurrentPool<float>(
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
            DisposeWorkers();
            DisposeSlots();
            _pool.Dispose();
        }
    }

    private sealed class GeneralArenaWorkload : NativeMapWorkload
    {
        private readonly NativeConcurrentArena _arena;

        internal GeneralArenaWorkload(
            ConcurrentArenaBenchmarkOptions options,
            PersistentMapWorkers? workers = null)
            : base(options, workers)
        {
            _arena = new NativeConcurrentArena(
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
            DisposeWorkers();
            DisposeSlots();
            _arena.Dispose();
        }
    }

    private sealed class SingleArenaWorkload : MapWorkload
    {
        private readonly NativeConcurrentArena _arena;
        private readonly NativeArenaTransferBatch<float> _batch;
        private readonly NativeArenaTransferBatchPublication<float>[]
            _publications;
        private readonly NativeArenaTransferBatchLease<float>[] _consumers;
        private readonly Action<int, int> _initializeMap;
        private readonly Action<int, int> _publishMap;
        private readonly Action<int, int> _accessMap;
        private readonly Action<int, int> _returnMap;

        internal SingleArenaWorkload(
            ConcurrentArenaBenchmarkOptions options,
            PersistentMapWorkers? workers = null)
            : base(options, workers)
        {
            _arena = new NativeConcurrentArena(
                checked(
                    (nuint)options.MapCount
                    * (nuint)options.ValuesPerMap
                    * sizeof(float)),
                NativeMemoryReturn.ToNativeMemory);
            _batch = _arena.CreateTransferBatch<float>(
                options.MapCount,
                options.ValuesPerMap);
            _publications =
                new NativeArenaTransferBatchPublication<float>[
                options.MapCount];
            _consumers = new NativeArenaTransferBatchLease<float>[
                options.MapCount];
            _initializeMap = InitializeMap;
            _publishMap = PublishMap;
            _accessMap = AccessMap;
            _returnMap = ReturnMap;
        }

        protected override void Initialize()
        {
            ForEachMap(_initializeMap);
        }

        protected override void Publish()
        {
            ForEachMap(_publishMap);
        }

        protected override long Access()
        {
            ForEachMap(_accessMap);
            return CombineChecksums(Checksums);
        }

        protected override string CreateExactHash()
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            for (int mapIndex = 0;
                mapIndex < _consumers.Length;
                mapIndex++)
            {
                _consumers[mapIndex].Read(
                    view =>
                    {
                        hash.AppendData(MemoryMarshal.AsBytes(
                            view.AsSpan()));
                        return 0;
                    });
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        protected override void DisposeOutputs()
        {
            ForEachMap(_returnMap);
        }

        private void InitializeMap(int _, int mapIndex)
        {
            NativeArenaTransferBatchInitialization<float>
                initialization = _batch.BeginInitialization(mapIndex);
            try
            {
                FillValues(
                    initialization.Values,
                    mapIndex,
                    Options);
                _publications[mapIndex] =
                    initialization.PreparePublication();
            }
            finally
            {
                initialization.Dispose();
            }
        }

        private void PublishMap(int _, int mapIndex)
        {
            NativeArenaTransferBatchPublication<float> publication =
                _publications[mapIndex];
            try
            {
                _consumers[mapIndex] = publication.Publish();
            }
            finally
            {
                publication.Dispose();
                _publications[mapIndex] = default;
            }
        }

        private void AccessMap(int _, int mapIndex)
        {
            using NativeArenaTransferBatchRead<float> read =
                _consumers[mapIndex].EnterRead();
            Checksums[mapIndex] = ConsumeValues(read.Values);
        }

        private void ReturnMap(int _, int mapIndex)
        {
            _consumers[mapIndex].Dispose();
        }

        internal override NativeOwnerSnapshot GetNativeStatistics()
        {
            NativeOwnerStatistics statistics = _arena.GetStatistics();
            return new(
                statistics.RetainedBytes,
                statistics.FreshSegmentAllocationCount);
        }

        public override void Dispose()
        {
            DisposeWorkers();
            foreach (
                ref NativeArenaTransferBatchPublication<float> publication
                in _publications.AsSpan())
            {
                publication.Dispose();
            }

            foreach (ref NativeArenaTransferBatchLease<float> lease in
                _consumers.AsSpan())
            {
                lease.Dispose();
            }

            _arena.Dispose();
        }
    }

    private sealed class ShardedArenaWorkload : NativeMapWorkload
    {
        private readonly NativeConcurrentArena[] _arenas;

        internal ShardedArenaWorkload(
            ConcurrentArenaBenchmarkOptions options,
            PersistentMapWorkers? workers = null)
            : base(options, workers)
        {
            _arenas = Enumerable.Range(0, options.WorkerCount)
                .Select(workerIndex => new NativeConcurrentArena(
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
            foreach (NativeConcurrentArena arena in _arenas)
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
            DisposeWorkers();
            DisposeSlots();
            foreach (NativeConcurrentArena arena in _arenas)
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
        private int _length;

        internal void Receive(
            float[] values,
            int length)
        {
            if (_values is not null)
            {
                throw new InvalidOperationException(
                    "The managed map slot already owns an array.");
            }

            _values = values;
            _length = length;
        }

        internal void Receive(ManagedMapTransfer transfer)
        {
            Receive(
                transfer.Values,
                transfer.Length);
        }

        internal ManagedMapTransfer Take()
        {
            float[] values = _values
                ?? throw new InvalidOperationException(
                    "The managed map slot has no array.");
            ManagedMapTransfer transfer = new(
                values,
                _length);
            _values = null;
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

        internal void Return(ArrayPool<float> pool)
        {
            float[]? values = _values;
            if (values is null)
            {
                return;
            }

            _values = null;
            _length = 0;
            pool.Return(values);
        }
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
