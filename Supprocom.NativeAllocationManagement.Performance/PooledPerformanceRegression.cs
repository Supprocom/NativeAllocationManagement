using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class PooledPerformanceRegression
{
    internal const double RequiredSpeedup = 1.10d;
    private static readonly JsonSerializerOptions CompactJson =
        CreateJsonOptions(writeIndented: false);
    private static readonly JsonSerializerOptions IndentedJson =
        CreateJsonOptions(writeIndented: true);

    internal static PooledRegressionOptions DefaultOptions => new(
        Iterations: 8_192,
        WarmupIterations: 1_024,
        SampleCount: 8,
        WorkerCount: 24,
        ChunkCount: 15_625,
        RenderedChunkCount: 1_785,
        OpaqueArrayCount: 15_203,
        RenderedOpaqueArrayCount: 9_142,
        TransparentArrayCount: 3_480,
        RenderedTransparentArrayCount: 2_961,
        OpaquePlaneLength: 8,
        TransparentPlaneLength: 32,
        MaximumPlanes: 6);

    internal static PooledRegressionOptions BoundaryOptions =>
        DefaultOptions with
        {
            OpaquePlaneLength = 400,
            TransparentPlaneLength = 25_600
        };

    internal static async Task<int> RunCommandAsync(string[] args)
    {
        PooledRegressionOptions options = ParseOptions(args);
        if (args[0] == "--pooled-regression-worker")
        {
            PooledRegressionImplementation implementation = Enum.Parse<
                PooledRegressionImplementation>(
                ReadRequiredOption(args, "--implementation"),
                ignoreCase: true);
            PooledRegressionWorkerEvidence evidence = RunWorker(
                implementation,
                options);
            Console.WriteLine(JsonSerializer.Serialize(
                evidence,
                CompactJson));
            return evidence.ExactParity ? 0 : 3;
        }

        PooledRegressionReport report = await RunPairedAsync(options);
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

    internal static async Task<PooledRegressionReport> RunPairedAsync(
        PooledRegressionOptions options)
    {
        ValidateOptions(options);
        PooledRegressionPairEvidence[] pairs =
            new PooledRegressionPairEvidence[options.SampleCount];
        Stopwatch totalClock = Stopwatch.StartNew();
        for (int sampleIndex = 0;
            sampleIndex < options.SampleCount;
            sampleIndex++)
        {
            PooledRegressionImplementation first =
                GetFirstImplementation(sampleIndex);
            PooledRegressionWorkerEvidence firstEvidence =
                await RunIsolatedWorkerAsync(first, options);
            PooledRegressionImplementation second =
                first == PooledRegressionImplementation.ArrayPool
                    ? PooledRegressionImplementation.Pooled
                    : PooledRegressionImplementation.ArrayPool;
            PooledRegressionWorkerEvidence secondEvidence =
                await RunIsolatedWorkerAsync(second, options);
            PooledRegressionWorkerEvidence managed =
                first == PooledRegressionImplementation.ArrayPool
                    ? firstEvidence
                    : secondEvidence;
            PooledRegressionWorkerEvidence native =
                first == PooledRegressionImplementation.Pooled
                    ? firstEvidence
                    : secondEvidence;
            ValidatePair(managed, native);
            pairs[sampleIndex] = new PooledRegressionPairEvidence(
                sampleIndex,
                first,
                managed,
                native,
                managed.ElapsedMilliseconds
                    / native.ElapsedMilliseconds);
        }

        totalClock.Stop();
        double[] ratios = pairs
            .Select(static pair => pair.ManagedToNativeSpeedup)
            .ToArray();
        double mean = ratios.Average();
        double lower =
            PairedBenchmarkStatistics.ConfidenceLower95(ratios);
        double aggregate = pairs.Sum(static pair =>
                pair.ArrayPool.ElapsedMilliseconds)
            / pairs.Sum(static pair =>
                pair.Pooled.ElapsedMilliseconds);
        bool parity = pairs.All(static pair =>
            pair.ArrayPool.ExactParity
            && pair.Pooled.ExactParity
            && pair.ArrayPool.OutputSha256
                == pair.Pooled.OutputSha256
            && pair.ArrayPool.Checksum == pair.Pooled.Checksum
            && pair.ArrayPool.LogicalBytes
                == pair.Pooled.LogicalBytes);
        bool balanced = pairs.Count(static pair =>
                pair.FirstImplementation
                    == PooledRegressionImplementation.ArrayPool)
            == options.SampleCount / 2
            && pairs.Count(static pair =>
                pair.FirstImplementation
                    == PooledRegressionImplementation.Pooled)
                == options.SampleCount / 2;
        bool runtimeConfiguration = pairs.All(static pair =>
            pair.ArrayPool.TieredCompilationDisabled
            && pair.ArrayPool.TieredPgoDisabled
            && pair.Pooled.TieredCompilationDisabled
            && pair.Pooled.TieredPgoDisabled);
        bool zeroFreshSegments = pairs.All(static pair =>
            pair.Pooled.NativeFreshSegmentAllocationDelta == 0);
        bool gatePassed = EvaluateGate(
            parity,
            balanced,
            runtimeConfiguration,
            zeroFreshSegments,
            mean,
            aggregate,
            lower);
        return new PooledRegressionReport(
            options,
            pairs,
            pairs.Average(static pair =>
                pair.ArrayPool.ElapsedMilliseconds),
            pairs.Average(static pair =>
                pair.Pooled.ElapsedMilliseconds),
            mean,
            aggregate,
            lower,
            pairs.Average(static pair =>
                (double)pair.ArrayPool.ManagedAllocatedBytes),
            pairs.Average(static pair =>
                (double)pair.Pooled.ManagedAllocatedBytes),
            parity,
            balanced,
            runtimeConfiguration,
            zeroFreshSegments,
            gatePassed,
            totalClock.Elapsed.TotalMilliseconds,
            DateTimeOffset.UtcNow);
    }

    internal static bool EvaluateGate(
        bool exactParity,
        bool balancedOrder,
        bool runtimeConfiguration,
        bool zeroFreshSegments,
        double meanSpeedup,
        double aggregateSpeedup,
        double confidenceLower95) =>
        exactParity
        && balancedOrder
        && runtimeConfiguration
        && zeroFreshSegments
        && meanSpeedup >= RequiredSpeedup
        && aggregateSpeedup >= RequiredSpeedup
        && confidenceLower95 > 1d;

    internal static PooledRegressionImplementation GetFirstImplementation(
        int sampleIndex) =>
        (sampleIndex & 1) == 0
            ? PooledRegressionImplementation.ArrayPool
            : PooledRegressionImplementation.Pooled;

    internal static PooledRegressionWorkerEvidence RunWorker(
        PooledRegressionImplementation implementation,
        PooledRegressionOptions options)
    {
        ValidateOptions(options);
        BoundaryWorkload workload = new(options);
        string expectedHash = workload.ComputeExpectedHash();
        long expectedChecksum = workload.ComputeExpectedChecksum();
        Stopwatch setupClock = Stopwatch.StartNew();
        using var execution = new BoundaryExecution(
            implementation,
            workload,
            options);
        setupClock.Stop();

        Stopwatch verificationClock = Stopwatch.StartNew();
        string outputHash = execution.VerifyExact();
        verificationClock.Stop();
        bool exactParity = outputHash == expectedHash;

        Stopwatch warmupClock = Stopwatch.StartNew();
        execution.Prepare();
        warmupClock.Stop();

        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        (long freshBefore, long retainedBefore) =
            execution.ReadNativeStatistics();
        (double elapsedMilliseconds, long checksum) =
            execution.RunMeasured();
        (long freshAfter, long retainedAfter) =
            execution.ReadNativeStatistics();
        process.Refresh();
        long workingSetAfter = process.WorkingSet64;
        long allocated = GC.GetTotalAllocatedBytes(precise: true)
            - allocatedBefore;
        long requiredChecksum = unchecked(
            expectedChecksum * options.Iterations);
        exactParity &= checksum == requiredChecksum;

        Stopwatch disposalClock = Stopwatch.StartNew();
        execution.DisposeOwners();
        disposalClock.Stop();
        long logicalBytes = checked(
            workload.LogicalBytesPerBatch * options.Iterations);
        return new PooledRegressionWorkerEvidence(
            implementation,
            options.WorkerCount,
            options.Iterations,
            options.WarmupIterations,
            logicalBytes,
            setupClock.Elapsed.TotalMilliseconds,
            verificationClock.Elapsed.TotalMilliseconds,
            warmupClock.Elapsed.TotalMilliseconds,
            elapsedMilliseconds,
            disposalClock.Elapsed.TotalMilliseconds,
            PairedBenchmarkStatistics.LogicalGigabytesPerSecond(
                logicalBytes,
                elapsedMilliseconds),
            allocated,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            workingSetBefore,
            workingSetAfter,
            Math.Max(workingSetBefore, workingSetAfter),
            freshAfter - freshBefore,
            retainedBefore,
            retainedAfter,
            outputHash,
            checksum,
            exactParity,
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

    private static async Task<PooledRegressionWorkerEvidence>
        RunIsolatedWorkerAsync(
            PooledRegressionImplementation implementation,
            PooledRegressionOptions options)
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
            typeof(PooledPerformanceRegression).Assembly.Location);

        process.StartInfo.ArgumentList.Add(
            "--pooled-regression-worker");
        process.StartInfo.ArgumentList.Add("--implementation");
        process.StartInfo.ArgumentList.Add(
            implementation.ToString());
        AddOption(process.StartInfo, "--iterations", options.Iterations);
        AddOption(
            process.StartInfo,
            "--warmup-iterations",
            options.WarmupIterations);
        AddOption(
            process.StartInfo,
            "--samples",
            options.SampleCount);
        AddOption(
            process.StartInfo,
            "--workers",
            options.WorkerCount);
        AddOption(
            process.StartInfo,
            "--chunk-count",
            options.ChunkCount);
        AddOption(
            process.StartInfo,
            "--rendered-chunk-count",
            options.RenderedChunkCount);
        AddOption(
            process.StartInfo,
            "--opaque-array-count",
            options.OpaqueArrayCount);
        AddOption(
            process.StartInfo,
            "--rendered-opaque-array-count",
            options.RenderedOpaqueArrayCount);
        AddOption(
            process.StartInfo,
            "--transparent-array-count",
            options.TransparentArrayCount);
        AddOption(
            process.StartInfo,
            "--rendered-transparent-array-count",
            options.RenderedTransparentArrayCount);
        AddOption(
            process.StartInfo,
            "--opaque-plane-length",
            options.OpaquePlaneLength);
        AddOption(
            process.StartInfo,
            "--transparent-plane-length",
            options.TransparentPlaneLength);
        AddOption(
            process.StartInfo,
            "--maximum-planes",
            options.MaximumPlanes);
        process.StartInfo.Environment["DOTNET_TieredCompilation"] = "0";
        process.StartInfo.Environment["DOTNET_TieredPGO"] = "0";
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "The benchmark worker did not start.");
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
                "The benchmark worker exceeded 60 seconds.");
        }

        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The benchmark worker failed with exit code {process.ExitCode}: {error}");
        }

        string json = output.Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()
            ?? throw new InvalidDataException(
                "The benchmark worker produced no evidence.");
        return JsonSerializer.Deserialize<
            PooledRegressionWorkerEvidence>(json, CompactJson);
    }

    private static void ValidatePair(
        PooledRegressionWorkerEvidence managed,
        PooledRegressionWorkerEvidence native)
    {
        if (!managed.ExactParity
            || !native.ExactParity
            || managed.OutputSha256 != native.OutputSha256
            || managed.Checksum != native.Checksum
            || managed.LogicalBytes != native.LogicalBytes)
        {
            throw new InvalidDataException(
                "The paired outputs are not equal.");
        }

        if (!managed.TieredCompilationDisabled
            || !managed.TieredPgoDisabled
            || !native.TieredCompilationDisabled
            || !native.TieredPgoDisabled)
        {
            throw new InvalidOperationException(
                "The benchmark runtime settings are invalid.");
        }
    }

    private static PooledRegressionOptions ParseOptions(string[] args)
    {
        PooledRegressionOptions options = HasOption(
            args,
            "--boundary-demonstration")
                ? BoundaryOptions
                : DefaultOptions;
        return options with
        {
            Iterations = ReadIntOption(
                args,
                "--iterations",
                options.Iterations),
            WarmupIterations = ReadIntOption(
                args,
                "--warmup-iterations",
                options.WarmupIterations),
            SampleCount = ReadIntOption(
                args,
                "--samples",
                options.SampleCount),
            WorkerCount = ReadIntOption(
                args,
                "--workers",
                options.WorkerCount),
            ChunkCount = ReadIntOption(
                args,
                "--chunk-count",
                options.ChunkCount),
            RenderedChunkCount = ReadIntOption(
                args,
                "--rendered-chunk-count",
                options.RenderedChunkCount),
            OpaqueArrayCount = ReadIntOption(
                args,
                "--opaque-array-count",
                options.OpaqueArrayCount),
            RenderedOpaqueArrayCount = ReadIntOption(
                args,
                "--rendered-opaque-array-count",
                options.RenderedOpaqueArrayCount),
            TransparentArrayCount = ReadIntOption(
                args,
                "--transparent-array-count",
                options.TransparentArrayCount),
            RenderedTransparentArrayCount = ReadIntOption(
                args,
                "--rendered-transparent-array-count",
                options.RenderedTransparentArrayCount),
            OpaquePlaneLength = ReadIntOption(
                args,
                "--opaque-plane-length",
                options.OpaquePlaneLength),
            TransparentPlaneLength = ReadIntOption(
                args,
                "--transparent-plane-length",
                options.TransparentPlaneLength),
            MaximumPlanes = ReadIntOption(
                args,
                "--maximum-planes",
                options.MaximumPlanes)
        };
    }

    private static void ValidateOptions(
        PooledRegressionOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.Iterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.WarmupIterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.SampleCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.WorkerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.ChunkCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.OpaquePlaneLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.TransparentPlaneLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MaximumPlanes);
        if ((options.SampleCount & 1) != 0)
        {
            throw new ArgumentException(
                "The sample count must be positive and even.",
                nameof(options));
        }

        if (options.RenderedChunkCount < 0
            || options.RenderedChunkCount > options.ChunkCount
            || options.OpaqueArrayCount < 0
            || options.TransparentArrayCount < 0
            || options.RenderedOpaqueArrayCount < 0
            || options.RenderedTransparentArrayCount < 0
            || options.RenderedOpaqueArrayCount
                > options.OpaqueArrayCount
            || options.RenderedTransparentArrayCount
                > options.TransparentArrayCount)
        {
            throw new ArgumentException(
                "The workload counts are invalid.",
                nameof(options));
        }
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
            $"The required option {name} is missing.",
            nameof(args));

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
        bool writeIndented) => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented,
            Converters = { new JsonStringEnumConverter() }
        };

    private sealed class BoundaryExecution : IDisposable
    {
        private readonly PooledRegressionImplementation _implementation;
        private readonly BoundaryWorkload _workload;
        private readonly PooledRegressionOptions _options;
        private readonly BoundaryWorker[] _workers;
        private readonly ManualResetEventSlim _start = new(false);
        private readonly CountdownEvent _ready;
        private readonly CountdownEvent _done;
        private readonly Thread[] _threads;
        private int _prepared;
        private int _ownersDisposed;

        internal BoundaryExecution(
            PooledRegressionImplementation implementation,
            BoundaryWorkload workload,
            PooledRegressionOptions options)
        {
            _implementation = implementation;
            _workload = workload;
            _options = options;
            _workers = new BoundaryWorker[options.WorkerCount];
            _threads = new Thread[options.WorkerCount];
            _ready = new CountdownEvent(options.WorkerCount);
            _done = new CountdownEvent(options.WorkerCount);
            for (int index = 0;
                index < options.WorkerCount;
                index++)
            {
                int start = checked(
                    (int)((long)index
                        * options.ChunkCount
                        / options.WorkerCount));
                int end = checked(
                    (int)((long)(index + 1)
                        * options.ChunkCount
                        / options.WorkerCount));
                _workers[index] = new BoundaryWorker(
                    implementation,
                    workload,
                    start,
                    end);
                int workerIndex = index;
                _threads[index] = new Thread(
                    () => RunWorker(workerIndex))
                {
                    IsBackground = true,
                    Name = $"NAM pool regression {index}"
                };
            }
        }

        internal string VerifyExact()
        {
            using IncrementalHash hash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (BoundaryWorker worker in _workers)
            {
                worker.VerifyExact(hash);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        internal void Prepare()
        {
            if (Interlocked.Exchange(ref _prepared, 1) != 0)
            {
                throw new InvalidOperationException(
                    "The benchmark workers are already prepared.");
            }

            foreach (Thread thread in _threads)
            {
                thread.Start();
            }

            if (!_ready.Wait(TimeSpan.FromSeconds(30)))
            {
                _start.Set();
                throw new TimeoutException(
                    "The benchmark warmup exceeded 30 seconds.");
            }

            ThrowWorkerFailure();
            long expected = _workload.ComputeExpectedChecksum();
            foreach (BoundaryWorker worker in _workers)
            {
                long required = unchecked(
                    worker.ExpectedChecksum
                    * _options.WarmupIterations);
                if (worker.WarmupChecksum != required)
                {
                    throw new InvalidDataException(
                        "A warmup checksum changed.");
                }
            }

            GC.KeepAlive(expected);
        }

        internal (double ElapsedMilliseconds, long Checksum)
            RunMeasured()
        {
            Stopwatch clock = Stopwatch.StartNew();
            _start.Set();
            if (!_done.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException(
                    "The benchmark measurement exceeded 30 seconds.");
            }

            clock.Stop();
            foreach (Thread thread in _threads)
            {
                thread.Join();
            }

            ThrowWorkerFailure();
            long checksum = 0;
            foreach (BoundaryWorker worker in _workers)
            {
                checksum = unchecked(
                    checksum + worker.MeasuredChecksum);
            }

            return (clock.Elapsed.TotalMilliseconds, checksum);
        }

        internal (long FreshSegments, long RetainedBytes)
            ReadNativeStatistics()
        {
            long fresh = 0;
            long retained = 0;
            foreach (BoundaryWorker worker in _workers)
            {
                (long workerFresh, long workerRetained) =
                    worker.ReadNativeStatistics();
                fresh = checked(fresh + workerFresh);
                retained = checked(retained + workerRetained);
            }

            return (fresh, retained);
        }

        internal void DisposeOwners()
        {
            if (Interlocked.Exchange(
                    ref _ownersDisposed,
                    1) != 0)
            {
                return;
            }

            foreach (BoundaryWorker worker in _workers)
            {
                worker.Dispose();
            }
        }

        public void Dispose()
        {
            _start.Set();
            DisposeOwners();
            _start.Dispose();
            _ready.Dispose();
            _done.Dispose();
        }

        private void RunWorker(int index)
        {
            BoundaryWorker worker = _workers[index];
            bool readySignaled = false;
            try
            {
                if (_implementation
                    == PooledRegressionImplementation.Pooled)
                {
                    worker.InitializePooledOwners();
                    using NativeWorkspace<ulong> opaque =
                        worker.CreateOpaqueWorkspace();
                    using NativeWorkspace<ushort> transparent =
                        worker.CreateTransparentWorkspace();
                    worker.WarmupChecksum = worker.RunPersistent(
                        _options.WarmupIterations,
                        in opaque,
                        in transparent);
                    _ready.Signal();
                    readySignaled = true;
                    _start.Wait();
                    worker.MeasuredChecksum = worker.RunPersistent(
                        _options.Iterations,
                        in opaque,
                        in transparent);
                    return;
                }

                worker.WarmupChecksum = worker.RunArrayPool(
                    _options.WarmupIterations);
                _ready.Signal();
                readySignaled = true;
                _start.Wait();
                worker.MeasuredChecksum = worker.RunArrayPool(
                    _options.Iterations);
            }
            catch (Exception exception)
            {
                worker.Failure = exception;
            }
            finally
            {
                if (!readySignaled)
                {
                    _ready.Signal();
                }

                _done.Signal();
            }
        }

        private void ThrowWorkerFailure()
        {
            Exception[] failures = _workers
                .Where(static worker => worker.Failure is not null)
                .Select(static worker => worker.Failure!)
                .ToArray();
            if (failures.Length != 0)
            {
                _start.Set();
                throw new AggregateException(
                    "A benchmark worker failed.",
                    failures);
            }
        }
    }

    private sealed class BoundaryWorker : IDisposable
    {
        private readonly PooledRegressionImplementation _implementation;
        private readonly BoundaryWorkload _workload;
        private readonly int _start;
        private readonly int _end;
        private NativePool<ulong>? _opaquePool;
        private NativePool<ushort>? _transparentPool;
        private readonly OpaqueInitializer _opaqueInitializer;
        private readonly TransparentInitializer _transparentInitializer;
        private readonly NativeLeaseFunc<ulong, long> _opaqueConsumer;
        private readonly NativeLeaseFunc<ushort, long> _transparentConsumer;
        private readonly NativeSpanReader<ulong, long>
            _opaqueSpanConsumer;
        private readonly NativeSpanReader<ushort, long>
            _transparentSpanConsumer;

        internal BoundaryWorker(
            PooledRegressionImplementation implementation,
            BoundaryWorkload workload,
            int start,
            int end)
        {
            _implementation = implementation;
            _workload = workload;
            _start = start;
            _end = end;
            _opaqueInitializer = new OpaqueInitializer(
                workload.OpaqueSource);
            _transparentInitializer = new TransparentInitializer(
                workload.TransparentSource);
            _opaqueConsumer = ConsumeOpaqueView;
            _transparentConsumer = ConsumeTransparentView;
            _opaqueSpanConsumer = ConsumeOpaque;
            _transparentSpanConsumer = ConsumeTransparent;
            ExpectedChecksum = workload.ComputeExpectedChecksum(
                start,
                end);
        }

        internal long ExpectedChecksum { get; }

        internal long WarmupChecksum { get; set; }

        internal long MeasuredChecksum { get; set; }

        internal Exception? Failure { get; set; }

        internal long RunArrayPool(int iterations)
        {
            long checksum = 0;
            for (int iteration = 0;
                iteration < iterations;
                iteration++)
            {
                checksum = unchecked(
                    checksum + RunArrayPoolOnce());
            }

            return checksum;
        }

        internal long RunPersistent(
            int iterations,
            scoped in NativeWorkspace<ulong> opaque,
            scoped in NativeWorkspace<ushort> transparent)
        {
            long checksum = 0;
            for (int iteration = 0;
                iteration < iterations;
                iteration++)
            {
                checksum = unchecked(
                    checksum + RunPersistentOnce(
                        in opaque,
                        in transparent));
            }

            return checksum;
        }

        internal NativeWorkspace<ulong> CreateOpaqueWorkspace() =>
            _opaquePool!.CreateWorkspace(
                _workload.MaximumOpaqueLength);

        internal NativeWorkspace<ushort>
            CreateTransparentWorkspace() =>
                _transparentPool!.CreateWorkspace(
                    _workload.MaximumTransparentLength);

        internal void VerifyExact(IncrementalHash hash)
        {
            if (_implementation
                == PooledRegressionImplementation.Pooled)
            {
                using NativePool<ulong> opaque =
                    CreateOpaquePool();
                using NativePool<ushort> transparent =
                    CreateTransparentPool();
                for (int index = _start; index < _end; index++)
                {
                    BoundaryShape shape = _workload.Shapes[index];
                    VerifyOpaque(
                        hash,
                        shape.OpaquePlanes,
                        opaque);
                    VerifyTransparent(
                        hash,
                        shape.TransparentPlanes,
                        transparent);
                }

                return;
            }

            for (int index = _start; index < _end; index++)
            {
                BoundaryShape shape = _workload.Shapes[index];
                VerifyOpaque(
                    hash,
                    shape.OpaquePlanes,
                    pool: null);
                VerifyTransparent(
                    hash,
                    shape.TransparentPlanes,
                    pool: null);
            }
        }

        internal void InitializePooledOwners()
        {
            if (_implementation
                    != PooledRegressionImplementation.Pooled
                || _opaquePool is not null
                || _transparentPool is not null)
            {
                throw new InvalidOperationException(
                    "The worker-local pools have an invalid setup state.");
            }

            _opaquePool = CreateOpaquePool();
            try
            {
                _transparentPool = CreateTransparentPool();
            }
            catch
            {
                _opaquePool.Dispose();
                _opaquePool = null;
                throw;
            }
        }

        internal (long FreshSegments, long RetainedBytes)
            ReadNativeStatistics()
        {
            if (_opaquePool is null
                || _transparentPool is null)
            {
                return (0, 0);
            }

            NativeOwnerStatistics opaque =
                _opaquePool.GetStatistics();
            NativeOwnerStatistics transparent =
                _transparentPool.GetStatistics();
            return (
                checked(
                    opaque.FreshSegmentAllocationCount
                    + transparent.FreshSegmentAllocationCount),
                checked(
                    opaque.RetainedBytes
                    + transparent.RetainedBytes));
        }

        public void Dispose()
        {
            _opaquePool?.Dispose();
            _transparentPool?.Dispose();
        }

        private long RunArrayPoolOnce()
        {
            long checksum = 0;
            for (int index = _start; index < _end; index++)
            {
                BoundaryShape shape = _workload.Shapes[index];
                int opaqueLength = checked(
                    shape.OpaquePlanes
                    * _workload.OpaqueSource.Length);
                int transparentLength = checked(
                    shape.TransparentPlanes
                    * _workload.TransparentSource.Length);
                ulong[]? opaque = null;
                ushort[]? transparent = null;
                try
                {
                    if (opaqueLength != 0)
                    {
                        opaque = ArrayPool<ulong>.Shared.Rent(
                            opaqueLength);
                        CopyPlanes(
                            _workload.OpaqueSource,
                            opaque.AsSpan(0, opaqueLength),
                            shape.OpaquePlanes);
                        checksum = unchecked(
                            checksum + ConsumeOpaque(
                                opaque.AsSpan(0, opaqueLength)));
                    }

                    if (transparentLength != 0)
                    {
                        transparent =
                            ArrayPool<ushort>.Shared.Rent(
                                transparentLength);
                        CopyPlanes(
                            _workload.TransparentSource,
                            transparent.AsSpan(
                                0,
                                transparentLength),
                            shape.TransparentPlanes);
                        checksum = unchecked(
                            checksum + ConsumeTransparent(
                                transparent.AsSpan(
                                    0,
                                    transparentLength)));
                    }
                }
                finally
                {
                    if (opaque is not null)
                    {
                        ArrayPool<ulong>.Shared.Return(
                            opaque,
                            clearArray: false);
                    }

                    if (transparent is not null)
                    {
                        ArrayPool<ushort>.Shared.Return(
                            transparent,
                            clearArray: false);
                    }
                }
            }

            return checksum;
        }

        private long RunPersistentOnce(
            scoped in NativeWorkspace<ulong> opaque,
            scoped in NativeWorkspace<ushort> transparent)
        {
            long checksum = 0;
            for (int index = _start; index < _end; index++)
            {
                BoundaryShape shape = _workload.Shapes[index];
                if (shape.OpaquePlanes != 0)
                {
                    _opaqueInitializer.PlaneCount =
                        shape.OpaquePlanes;
                    checksum = unchecked(
                        checksum + opaque.Process(
                        checked(
                            shape.OpaquePlanes
                            * _workload.OpaqueSource.Length),
                        _opaqueInitializer.SpanAction,
                        _opaqueSpanConsumer));
                }

                if (shape.TransparentPlanes != 0)
                {
                    _transparentInitializer.PlaneCount =
                        shape.TransparentPlanes;
                    checksum = unchecked(
                        checksum + transparent.Process(
                            checked(
                                shape.TransparentPlanes
                                * _workload.TransparentSource.Length),
                            _transparentInitializer.SpanAction,
                            _transparentSpanConsumer));
                }
            }

            return checksum;
        }

        private void VerifyOpaque(
            IncrementalHash hash,
            int planeCount,
            NativePool<ulong>? pool)
        {
            int length = checked(
                planeCount * _workload.OpaqueSource.Length);
            AppendLength(hash, length);
            if (length == 0)
            {
                return;
            }

            if (_implementation
                == PooledRegressionImplementation.ArrayPool)
            {
                ulong[] values = ArrayPool<ulong>.Shared.Rent(
                    length);
                try
                {
                    Span<ulong> logical = values.AsSpan(0, length);
                    CopyPlanes(
                        _workload.OpaqueSource,
                        logical,
                        planeCount);
                    VerifyPlanes(
                        logical,
                        _workload.OpaqueSource,
                        planeCount);
                    hash.AppendData(MemoryMarshal.AsBytes(logical));
                }
                finally
                {
                    ArrayPool<ulong>.Shared.Return(
                        values,
                        clearArray: false);
                }

                return;
            }

            _opaqueInitializer.PlaneCount = planeCount;
            Pooled<ulong> lease = pool!.Rent(
                length,
                _opaqueInitializer.Action);
            try
            {
                lease.Read(view =>
                {
                    ReadOnlySpan<ulong> logical = view.AsSpan();
                    VerifyPlanes(
                        logical,
                        _workload.OpaqueSource,
                        planeCount);
                    hash.AppendData(MemoryMarshal.AsBytes(logical));
                    return 0;
                });
            }
            finally
            {
                lease.Dispose();
            }
        }

        private void VerifyTransparent(
            IncrementalHash hash,
            int planeCount,
            NativePool<ushort>? pool)
        {
            int length = checked(
                planeCount
                * _workload.TransparentSource.Length);
            AppendLength(hash, length);
            if (length == 0)
            {
                return;
            }

            if (_implementation
                == PooledRegressionImplementation.ArrayPool)
            {
                ushort[] values = ArrayPool<ushort>.Shared.Rent(
                    length);
                try
                {
                    Span<ushort> logical = values.AsSpan(0, length);
                    CopyPlanes(
                        _workload.TransparentSource,
                        logical,
                        planeCount);
                    VerifyPlanes(
                        logical,
                        _workload.TransparentSource,
                        planeCount);
                    hash.AppendData(MemoryMarshal.AsBytes(logical));
                }
                finally
                {
                    ArrayPool<ushort>.Shared.Return(
                        values,
                        clearArray: false);
                }

                return;
            }

            _transparentInitializer.PlaneCount = planeCount;
            Pooled<ushort> lease = pool!.Rent(
                length,
                _transparentInitializer.Action);
            try
            {
                lease.Read(view =>
                {
                    ReadOnlySpan<ushort> logical = view.AsSpan();
                    VerifyPlanes(
                        logical,
                        _workload.TransparentSource,
                        planeCount);
                    hash.AppendData(MemoryMarshal.AsBytes(logical));
                    return 0;
                });
            }
            finally
            {
                lease.Dispose();
            }
        }

        private static long ConsumeOpaqueView(
            scoped NativeLeaseView<ulong> view) =>
            ConsumeOpaque(view.AsSpan());

        private static long ConsumeTransparentView(
            scoped NativeLeaseView<ushort> view) =>
            ConsumeTransparent(view.AsSpan());

        private NativePool<ulong> CreateOpaquePool() =>
            new(
                preLease: _workload.MaximumOpaqueLength,
                returnMemoryOnDispose:
                    NativeMemoryReturn.ToNativeMemory);

        private NativePool<ushort> CreateTransparentPool() =>
            new(
                preLease: _workload.MaximumTransparentLength,
                returnMemoryOnDispose:
                    NativeMemoryReturn.ToNativeMemory);
    }

    private sealed class BoundaryWorkload
    {
        internal BoundaryWorkload(PooledRegressionOptions options)
        {
            Options = options;
            Shapes = CreateShapes(options);
            OpaqueSource = Enumerable.Range(
                    0,
                    options.OpaquePlaneLength)
                .Select(static index =>
                    0x9E3779B97F4A7C15UL ^ (ulong)index)
                .ToArray();
            TransparentSource = Enumerable.Range(
                    0,
                    options.TransparentPlaneLength)
                .Select(static index =>
                    (ushort)((index * 17 + 3) % 65_521))
                .ToArray();
            MaximumOpaqueLength = checked(
                options.MaximumPlanes
                * options.OpaquePlaneLength);
            MaximumTransparentLength = checked(
                options.MaximumPlanes
                * options.TransparentPlaneLength);
            long bytes = 0;
            foreach (BoundaryShape shape in Shapes)
            {
                bytes = checked(
                    bytes
                    + (long)shape.OpaquePlanes
                        * OpaqueSource.Length
                        * sizeof(ulong)
                    + (long)shape.TransparentPlanes
                        * TransparentSource.Length
                        * sizeof(ushort));
            }

            LogicalBytesPerBatch = bytes;
        }

        internal PooledRegressionOptions Options { get; }

        internal BoundaryShape[] Shapes { get; }

        internal ulong[] OpaqueSource { get; }

        internal ushort[] TransparentSource { get; }

        internal int MaximumOpaqueLength { get; }

        internal int MaximumTransparentLength { get; }

        internal long LogicalBytesPerBatch { get; }

        internal string ComputeExpectedHash()
        {
            using IncrementalHash hash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (BoundaryShape shape in Shapes)
            {
                AppendExpected(
                    hash,
                    OpaqueSource,
                    shape.OpaquePlanes);
                AppendExpected(
                    hash,
                    TransparentSource,
                    shape.TransparentPlanes);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        internal long ComputeExpectedChecksum() =>
            ComputeExpectedChecksum(0, Shapes.Length);

        internal long ComputeExpectedChecksum(
            int start,
            int end)
        {
            long checksum = 0;
            for (int index = start; index < end; index++)
            {
                BoundaryShape shape = Shapes[index];
                if (shape.OpaquePlanes != 0)
                {
                    checksum = unchecked(
                        checksum + ConsumeRepeated(
                            OpaqueSource,
                            shape.OpaquePlanes));
                }

                if (shape.TransparentPlanes != 0)
                {
                    checksum = unchecked(
                        checksum + ConsumeRepeated(
                            TransparentSource,
                            shape.TransparentPlanes));
                }
            }

            return checksum;
        }

        private static BoundaryShape[] CreateShapes(
            PooledRegressionOptions options)
        {
            var result = new BoundaryShape[options.ChunkCount];
            Assign(
                result,
                0,
                options.RenderedChunkCount,
                options.RenderedOpaqueArrayCount,
                options.MaximumPlanes,
                opaque: true);
            Assign(
                result,
                options.RenderedChunkCount,
                options.ChunkCount - options.RenderedChunkCount,
                options.OpaqueArrayCount
                    - options.RenderedOpaqueArrayCount,
                options.MaximumPlanes,
                opaque: true);
            Assign(
                result,
                0,
                options.RenderedChunkCount,
                options.RenderedTransparentArrayCount,
                options.MaximumPlanes,
                opaque: false);
            Assign(
                result,
                options.RenderedChunkCount,
                options.ChunkCount - options.RenderedChunkCount,
                options.TransparentArrayCount
                    - options.RenderedTransparentArrayCount,
                options.MaximumPlanes,
                opaque: false);
            return result;
        }

        private static void Assign(
            BoundaryShape[] values,
            int start,
            int length,
            int planes,
            int maximumPlanes,
            bool opaque)
        {
            if (planes == 0)
            {
                return;
            }

            if (length == 0
                || planes > checked(length * maximumPlanes))
            {
                throw new ArgumentException(
                    "The plane count exceeds its chunk range.");
            }

            int full = planes / maximumPlanes;
            int remainder = planes % maximumPlanes;
            for (int packet = 0; packet < full; packet++)
            {
                int index = start + checked(
                    (int)((long)packet * length / full));
                values[index] = opaque
                    ? values[index] with
                    {
                        OpaquePlanes = maximumPlanes
                    }
                    : values[index] with
                    {
                        TransparentPlanes = maximumPlanes
                    };
            }

            if (remainder == 0)
            {
                return;
            }

            for (int relative = length - 1;
                relative >= 0;
                relative--)
            {
                int index = start + relative;
                int current = opaque
                    ? values[index].OpaquePlanes
                    : values[index].TransparentPlanes;
                if (current != 0)
                {
                    continue;
                }

                values[index] = opaque
                    ? values[index] with
                    {
                        OpaquePlanes = remainder
                    }
                    : values[index] with
                    {
                        TransparentPlanes = remainder
                    };
                return;
            }

            throw new InvalidOperationException(
                "The remainder has no available chunk.");
        }
    }

    private sealed class OpaqueInitializer
    {
        private readonly ulong[] _source;
        private readonly NativeSpanInitializer<ulong> _spanAction;

        internal OpaqueInitializer(ulong[] source)
        {
            _source = source;
            Action = Initialize;
            _spanAction = InitializeSpan;
        }

        internal int PlaneCount { get; set; }

        internal NativeLeaseInitializer<ulong> Action { get; }

        internal NativeSpanInitializer<ulong> SpanAction =>
            _spanAction;

        private void Initialize(
            scoped NativeLeaseWriter<ulong> writer)
        {
            for (int plane = 0; plane < PlaneCount; plane++)
            {
                writer.Write(_source);
            }
        }

        private void InitializeSpan(scoped Span<ulong> destination) =>
            CopyPlanes(_source, destination, PlaneCount);
    }

    private sealed class TransparentInitializer
    {
        private readonly ushort[] _source;
        private readonly NativeSpanInitializer<ushort> _spanAction;

        internal TransparentInitializer(ushort[] source)
        {
            _source = source;
            Action = Initialize;
            _spanAction = InitializeSpan;
        }

        internal int PlaneCount { get; set; }

        internal NativeLeaseInitializer<ushort> Action { get; }

        internal NativeSpanInitializer<ushort> SpanAction =>
            _spanAction;

        private void Initialize(
            scoped NativeLeaseWriter<ushort> writer)
        {
            for (int plane = 0; plane < PlaneCount; plane++)
            {
                writer.Write(_source);
            }
        }

        private void InitializeSpan(scoped Span<ushort> destination) =>
            CopyPlanes(_source, destination, PlaneCount);
    }

    private static void CopyPlanes<T>(
        T[] source,
        Span<T> destination,
        int planeCount)
    {
        for (int plane = 0; plane < planeCount; plane++)
        {
            source.CopyTo(destination[(plane * source.Length)..]);
        }
    }

    private static void VerifyPlanes<T>(
        ReadOnlySpan<T> values,
        T[] source,
        int planeCount)
        where T : IEquatable<T>
    {
        if (values.Length != checked(source.Length * planeCount))
        {
            throw new InvalidDataException(
                "The output length changed.");
        }

        for (int plane = 0; plane < planeCount; plane++)
        {
            if (!values.Slice(plane * source.Length, source.Length)
                .SequenceEqual(source))
            {
                throw new InvalidDataException(
                    "The output values changed.");
            }
        }
    }

    private static void AppendExpected<T>(
        IncrementalHash hash,
        T[] source,
        int planeCount)
        where T : unmanaged
    {
        int length = checked(source.Length * planeCount);
        AppendLength(hash, length);
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(
            source.AsSpan());
        for (int plane = 0; plane < planeCount; plane++)
        {
            hash.AppendData(bytes);
        }
    }

    private static void AppendLength(
        IncrementalHash hash,
        int length)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        hash.AppendData(bytes);
    }

    private static long ConsumeOpaque(
        scoped ReadOnlySpan<ulong> values)
    {
        long checksum = values.Length;
        for (int index = 0; index < values.Length; index += 64)
        {
            checksum = unchecked(
                (checksum * 397) ^ (long)values[index]);
        }

        return values.Length == 0
            ? checksum
            : checksum ^ (long)values[^1];
    }

    private static long ConsumeTransparent(
        scoped ReadOnlySpan<ushort> values)
    {
        long checksum = values.Length;
        for (int index = 0; index < values.Length; index += 64)
        {
            checksum = unchecked(
                (checksum * 397) ^ values[index]);
        }

        return values.Length == 0
            ? checksum
            : checksum ^ values[^1];
    }

    private static long ConsumeRepeated<T>(
        T[] source,
        int planeCount)
        where T : unmanaged
    {
        int length = checked(source.Length * planeCount);
        if (typeof(T) == typeof(ulong))
        {
            ReadOnlySpan<ulong> typed =
                MemoryMarshal.Cast<T, ulong>(source);
            long checksum = length;
            for (int index = 0; index < length; index += 64)
            {
                checksum = unchecked(
                    (checksum * 397)
                    ^ (long)typed[index % typed.Length]);
            }

            return checksum ^ (long)typed[(length - 1) % typed.Length];
        }

        ReadOnlySpan<ushort> shortValues =
            MemoryMarshal.Cast<T, ushort>(source);
        long shortChecksum = length;
        for (int index = 0; index < length; index += 64)
        {
            shortChecksum = unchecked(
                (shortChecksum * 397)
                ^ shortValues[index % shortValues.Length]);
        }

        return shortChecksum
            ^ shortValues[(length - 1) % shortValues.Length];
    }
}

internal enum PooledRegressionImplementation
{
    ArrayPool,
    Pooled
}

internal readonly record struct PooledRegressionOptions(
    int Iterations,
    int WarmupIterations,
    int SampleCount,
    int WorkerCount,
    int ChunkCount,
    int RenderedChunkCount,
    int OpaqueArrayCount,
    int RenderedOpaqueArrayCount,
    int TransparentArrayCount,
    int RenderedTransparentArrayCount,
    int OpaquePlaneLength,
    int TransparentPlaneLength,
    int MaximumPlanes);

internal readonly record struct PooledRegressionWorkerEvidence(
    PooledRegressionImplementation Implementation,
    int WorkerCount,
    int Iterations,
    int WarmupIterations,
    long LogicalBytes,
    double SetupMilliseconds,
    double VerificationMilliseconds,
    double WarmupMilliseconds,
    double ElapsedMilliseconds,
    double DisposalMilliseconds,
    double LogicalGigabytesPerSecond,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    long PeakObservedWorkingSetBytes,
    long NativeFreshSegmentAllocationDelta,
    long NativeRetainedBytesBefore,
    long NativeRetainedBytesAfter,
    string OutputSha256,
    long Checksum,
    bool ExactParity,
    bool TieredCompilationDisabled,
    bool TieredPgoDisabled);

internal readonly record struct PooledRegressionPairEvidence(
    int SampleIndex,
    PooledRegressionImplementation FirstImplementation,
    PooledRegressionWorkerEvidence ArrayPool,
    PooledRegressionWorkerEvidence Pooled,
    double ManagedToNativeSpeedup);

internal readonly record struct PooledRegressionReport(
    PooledRegressionOptions Options,
    IReadOnlyList<PooledRegressionPairEvidence> Pairs,
    double ArrayPoolMeanMilliseconds,
    double PooledMeanMilliseconds,
    double PairedMeanSpeedup,
    double AggregateSpeedup,
    double ConfidenceLower95,
    double ArrayPoolMeanManagedAllocatedBytes,
    double PooledMeanManagedAllocatedBytes,
    bool ExactParity,
    bool BalancedOrder,
    bool RuntimeConfigurationPassed,
    bool ZeroFreshSegments,
    bool GatePassed,
    double TotalElapsedMilliseconds,
    DateTimeOffset RecordedAtUtc);

internal readonly record struct BoundaryShape(
    int OpaquePlanes,
    int TransparentPlanes);
