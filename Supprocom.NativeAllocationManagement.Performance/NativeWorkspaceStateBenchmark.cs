using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class NativeWorkspaceStateBenchmark
{
    internal const int RequiredMapCount = 729;
    internal const int RequiredMapSize = 160;
    internal const int RequiredValueCount =
        RequiredMapSize * RequiredMapSize;
    internal const int RequiredWorkspaceLength =
        RequiredValueCount * 2;
    internal const int RequiredWorkerCount = 24;
    internal const long RequiredSeed = 123_456;
    internal const int RequiredSampleCount = 6;
    internal const int RequiredWarmupCount = 2;
    internal const int RequiredMeasurementPassCount = 12;
    internal const double RequiredConfidenceLower95 = 0.98d;

    private const ulong FnvOffset = 14_695_981_039_346_656_037UL;
    private const ulong FnvPrime = 1_099_511_628_211UL;
    private static readonly JsonSerializerOptions CompactJson = new();
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true
    };

    internal static NativeWorkspaceStateOptions DefaultOptions => new(
        MapCount: RequiredMapCount,
        MapSize: RequiredMapSize,
        WorkspaceLength: RequiredWorkspaceLength,
        WorkerCount: RequiredWorkerCount,
        SampleCount: RequiredSampleCount,
        WarmupCount: RequiredWarmupCount,
        MeasurementPassCount: RequiredMeasurementPassCount,
        Seed: RequiredSeed);

    internal static async Task<int> RunCommandAsync(string[] args)
    {
        NativeWorkspaceStateOptions options = ParseOptions(args);
        if (args[0] == "--native-workspace-state-worker")
        {
            NativeWorkspaceStateImplementation implementation = Enum.Parse<
                NativeWorkspaceStateImplementation>(
                ReadRequiredOption(args, "--implementation"),
                ignoreCase: true);
            NativeWorkspaceStateWorkerEvidence evidence = RunWorker(
                implementation,
                options);
            Console.WriteLine(JsonSerializer.Serialize(
                evidence,
                CompactJson));
            return evidence.Completed
                && evidence.CancellationCleanupPassed
                && evidence.ExactlyOnceCleanupPassed
                && evidence.TieredCompilationDisabled
                && evidence.TieredPgoDisabled
                    ? 0
                    : 3;
        }


        if (args[0] == "--native-workspace-state-pair-worker")
        {
            int sampleIndex = ReadIntOption(
                args,
                "--sample-index",
                defaultValue: -1);
            NativeWorkspaceStateImplementation[] order =
                GetImplementationOrder(sampleIndex);
            NativeWorkspaceStateWorkerEvidence[] evidence = order
                .Select(implementation => RunWorker(
                    implementation,
                    options))
                .ToArray();
            Console.WriteLine(JsonSerializer.Serialize(
                new NativeWorkspaceStateIsolatedPairEvidence(
                    sampleIndex,
                    order,
                    evidence),
                CompactJson));
            return evidence.All(static result =>
                result.Completed
                && result.CancellationCleanupPassed
                && result.ExactlyOnceCleanupPassed
                && result.TieredCompilationDisabled
                && result.TieredPgoDisabled)
                    ? 0
                    : 3;
        }

        NativeWorkspaceStateReport report = await RunPairedAsync(options);
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

    internal static async Task<NativeWorkspaceStateReport> RunPairedAsync(
        NativeWorkspaceStateOptions options)
    {
        ValidateOptions(options);
        NativeWorkspaceStatePairEvidence[] pairs =
            new NativeWorkspaceStatePairEvidence[options.SampleCount];
        Stopwatch totalClock = Stopwatch.StartNew();
        for (int sampleIndex = 0;
            sampleIndex < options.SampleCount;
            sampleIndex++)
        {
            NativeWorkspaceStateIsolatedPairEvidence isolated =
                await RunIsolatedPairAsync(sampleIndex, options);
            NativeWorkspaceStateImplementation[] order =
                isolated.ImplementationOrder.ToArray();
            Dictionary<
                NativeWorkspaceStateImplementation,
                NativeWorkspaceStateWorkerEvidence> evidence = isolated
                    .Evidence
                    .ToDictionary(static result =>
                        result.Implementation);

            NativeWorkspaceStateWorkerEvidence managed = evidence[
                NativeWorkspaceStateImplementation.ManagedArray];
            NativeWorkspaceStateWorkerEvidence capturing = evidence[
                NativeWorkspaceStateImplementation.CapturingWorkspace];
            NativeWorkspaceStateWorkerEvidence explicitState = evidence[
                NativeWorkspaceStateImplementation.ExplicitStateWorkspace];
            ValidatePair(managed, capturing, explicitState);
            pairs[sampleIndex] = new NativeWorkspaceStatePairEvidence(
                sampleIndex,
                order,
                managed,
                capturing,
                explicitState,
                managed.ElapsedMilliseconds
                    / explicitState.ElapsedMilliseconds,
                capturing.ElapsedMilliseconds
                    / explicitState.ElapsedMilliseconds);
        }

        totalClock.Stop();
        NativeWorkspaceStateBinaryIdentity runtimeIdentity =
            CreateBinaryIdentity(typeof(NativeWorkspace<>).Assembly);
        NativeWorkspaceStateBinaryIdentity benchmarkIdentity =
            CreateBinaryIdentity(
                typeof(NativeWorkspaceStateBenchmark).Assembly);
        bool binaryIdentity = runtimeIdentity.SourceCommit
                == benchmarkIdentity.SourceCommit
            && runtimeIdentity.SourceCommit.Length == 40;
        double[] managedSpeedups = pairs
            .Select(static pair => pair.ManagedToExplicitStateSpeedup)
            .ToArray();
        double meanSpeedup = managedSpeedups.Average();
        double aggregateSpeedup = pairs.Sum(static pair =>
                pair.Managed.ElapsedMilliseconds)
            / pairs.Sum(static pair =>
                pair.ExplicitState.ElapsedMilliseconds);
        double confidenceLower95 =
            PairedBenchmarkStatistics.ConfidenceLower95(managedSpeedups);
        bool exactParity = pairs.All(static pair =>
            pair.Managed.OutputSha256 == pair.Capturing.OutputSha256
            && pair.Managed.OutputSha256
                == pair.ExplicitState.OutputSha256
            && pair.Managed.WorkerChecksums.SequenceEqual(
                pair.Capturing.WorkerChecksums)
            && pair.Managed.WorkerChecksums.SequenceEqual(
                pair.ExplicitState.WorkerChecksums));
        bool balancedOrder = IsBalancedOrder(pairs);
        bool runtimeConfiguration = pairs.All(static pair =>
            pair.Managed.TieredCompilationDisabled
            && pair.Managed.TieredPgoDisabled
            && pair.Capturing.TieredCompilationDisabled
            && pair.Capturing.TieredPgoDisabled
            && pair.ExplicitState.TieredCompilationDisabled
            && pair.ExplicitState.TieredPgoDisabled);
        bool zeroFreshSegments = pairs.All(static pair =>
            pair.Capturing.NativeFreshSegmentAllocationDelta == 0
            && pair.ExplicitState.NativeFreshSegmentAllocationDelta == 0);
        bool cleanup = pairs.All(static pair =>
            pair.Capturing.CancellationCleanupPassed
            && pair.Capturing.ExactlyOnceCleanupPassed
            && pair.ExplicitState.CancellationCleanupPassed
            && pair.ExplicitState.ExactlyOnceCleanupPassed);
        double managedTotalAllocationMean = pairs.Average(static pair =>
            (double)pair.Managed.SetupManagedAllocatedBytes
                + pair.Managed.ManagedAllocatedBytes);
        double explicitStateTotalAllocationMean = pairs.Average(
            static pair =>
                (double)pair.ExplicitState.SetupManagedAllocatedBytes
                    + pair.ExplicitState.ManagedAllocatedBytes);
        bool allocationAdvantage = explicitStateTotalAllocationMean
            < managedTotalAllocationMean;
        bool productionShape = options == DefaultOptions;
        bool gatePassed = EvaluateGate(
            exactParity,
            balancedOrder,
            runtimeConfiguration,
            zeroFreshSegments,
            cleanup,
            allocationAdvantage,
            productionShape,
            binaryIdentity,
            meanSpeedup,
            aggregateSpeedup,
            confidenceLower95);
        return new NativeWorkspaceStateReport(
            runtimeIdentity.SourceCommit,
            runtimeIdentity,
            benchmarkIdentity,
            options,
            pairs,
            pairs.Average(static pair =>
                pair.Managed.ElapsedMilliseconds),
            pairs.Average(static pair =>
                pair.Capturing.ElapsedMilliseconds),
            pairs.Average(static pair =>
                pair.ExplicitState.ElapsedMilliseconds),
            meanSpeedup,
            aggregateSpeedup,
            confidenceLower95,
            pairs.Average(static pair =>
                pair.CapturingToExplicitStateSpeedup),
            managedTotalAllocationMean,
            pairs.Average(static pair =>
                (double)pair.Capturing.SetupManagedAllocatedBytes
                    + pair.Capturing.ManagedAllocatedBytes),
            explicitStateTotalAllocationMean,
            pairs.Max(static pair =>
                pair.Managed.PeakWorkingSetBytes),
            pairs.Max(static pair =>
                pair.Capturing.PeakWorkingSetBytes),
            pairs.Max(static pair =>
                pair.ExplicitState.PeakWorkingSetBytes),
            exactParity,
            balancedOrder,
            runtimeConfiguration,
            zeroFreshSegments,
            cleanup,
            allocationAdvantage,
            productionShape,
            binaryIdentity,
            gatePassed,
            totalClock.Elapsed.TotalMilliseconds,
            DateTimeOffset.UtcNow);
    }

    internal static NativeWorkspaceStateWorkerEvidence RunWorker(
        NativeWorkspaceStateImplementation implementation,
        NativeWorkspaceStateOptions options)
    {
        ValidateOptions(options);
        NativeMemoryStatistics nativeBaseline =
            NativeMemoryDiagnostics.Snapshot();
        long setupAllocationBefore =
            GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch setupClock = Stopwatch.StartNew();
        var execution = new WorkspaceExecution(
            implementation,
            options);
        setupClock.Stop();
        long setupManagedAllocatedBytes =
            GC.GetTotalAllocatedBytes(precise: true)
                - setupAllocationBefore;
        double warmupMilliseconds = 0d;
        double elapsedMilliseconds = 0d;
        double[] measurementPassMilliseconds =
            new double[options.MeasurementPassCount];
        long managedAllocatedBytes = 0;
        int gen0Collections = 0;
        int gen1Collections = 0;
        int gen2Collections = 0;
        long workingSetBeforeBytes = 0;
        long workingSetAfterBytes = 0;
        long peakWorkingSetBytes = 0;
        long freshSegmentsBefore = 0;
        long freshSegmentsAfter = 0;
        ulong[] checksums = [];
        bool cancellationCleanupPassed = true;
        try
        {
            Stopwatch warmupClock = Stopwatch.StartNew();
            for (int warmupIndex = 0;
                warmupIndex < options.WarmupCount;
                warmupIndex++)
            {
                execution.RunBatch();
            }

            warmupClock.Stop();
            warmupMilliseconds = warmupClock.Elapsed.TotalMilliseconds;
            freshSegmentsBefore = execution.ReadFreshSegmentCount();
            Process process = Process.GetCurrentProcess();
            process.Refresh();
            workingSetBeforeBytes = process.WorkingSet64;
            long allocationBefore =
                GC.GetTotalAllocatedBytes(precise: true);
            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);
            for (int passIndex = 0;
                passIndex < measurementPassMilliseconds.Length;
                passIndex++)
            {
                measurementPassMilliseconds[passIndex] =
                    execution.RunBatch();
            }

            elapsedMilliseconds =
                measurementPassMilliseconds.Average();
            managedAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true)
                    - allocationBefore;
            gen0Collections = GC.CollectionCount(0) - gen0Before;
            gen1Collections = GC.CollectionCount(1) - gen1Before;
            gen2Collections = GC.CollectionCount(2) - gen2Before;
            checksums = execution.ReadChecksums();
            process.Refresh();
            workingSetAfterBytes = process.WorkingSet64;
            peakWorkingSetBytes = process.PeakWorkingSet64;
            freshSegmentsAfter = execution.ReadFreshSegmentCount();
            cancellationCleanupPassed =
                execution.VerifyCancellationCleanup();
        }
        finally
        {
            execution.Dispose();
        }

        NativeMemoryStatistics nativeFinal =
            NativeMemoryDiagnostics.Snapshot();
        bool exactlyOnceCleanupPassed =
            nativeFinal.OutstandingNativeBytes
                == nativeBaseline.OutstandingNativeBytes;
        return new NativeWorkspaceStateWorkerEvidence(
            implementation,
            options.WorkerCount,
            options.MapCount,
            options.MapSize,
            setupClock.Elapsed.TotalMilliseconds,
            warmupMilliseconds,
            elapsedMilliseconds,
            measurementPassMilliseconds,
            checked((double)options.MapCount
                / (elapsedMilliseconds / 1_000d)),
            setupManagedAllocatedBytes,
            managedAllocatedBytes,
            gen0Collections,
            gen1Collections,
            gen2Collections,
            workingSetBeforeBytes,
            workingSetAfterBytes,
            peakWorkingSetBytes,
            freshSegmentsAfter - freshSegmentsBefore,
            ComputeOutputSha256(checksums),
            checksums,
            cancellationCleanupPassed,
            exactlyOnceCleanupPassed,
            string.Equals(
                Environment.GetEnvironmentVariable(
                    "DOTNET_TieredCompilation"),
                "0",
                StringComparison.Ordinal),
            string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
                "0",
                StringComparison.Ordinal),
            Completed: true);
    }

    internal static NativeWorkspaceStateImplementation[]
        GetImplementationOrder(int sampleIndex) =>
        (sampleIndex % RequiredSampleCount) switch
        {
            0 =>
            [
                NativeWorkspaceStateImplementation.ManagedArray,
                NativeWorkspaceStateImplementation.CapturingWorkspace,
                NativeWorkspaceStateImplementation.ExplicitStateWorkspace
            ],
            1 =>
            [
                NativeWorkspaceStateImplementation.ExplicitStateWorkspace,
                NativeWorkspaceStateImplementation.CapturingWorkspace,
                NativeWorkspaceStateImplementation.ManagedArray
            ],
            2 =>
            [
                NativeWorkspaceStateImplementation.CapturingWorkspace,
                NativeWorkspaceStateImplementation.ManagedArray,
                NativeWorkspaceStateImplementation.ExplicitStateWorkspace
            ],
            3 =>
            [
                NativeWorkspaceStateImplementation.CapturingWorkspace,
                NativeWorkspaceStateImplementation.ExplicitStateWorkspace,
                NativeWorkspaceStateImplementation.ManagedArray
            ],
            4 =>
            [
                NativeWorkspaceStateImplementation.ManagedArray,
                NativeWorkspaceStateImplementation.ExplicitStateWorkspace,
                NativeWorkspaceStateImplementation.CapturingWorkspace
            ],
            _ =>
            [
                NativeWorkspaceStateImplementation.ExplicitStateWorkspace,
                NativeWorkspaceStateImplementation.ManagedArray,
                NativeWorkspaceStateImplementation.CapturingWorkspace
            ]
        };

    internal static bool EvaluateGate(
        bool exactParity,
        bool balancedOrder,
        bool runtimeConfiguration,
        bool zeroFreshSegments,
        bool cleanup,
        bool allocationAdvantage,
        bool productionShape,
        bool binaryIdentity,
        double meanSpeedup,
        double aggregateSpeedup,
        double confidenceLower95) =>
        exactParity
        && balancedOrder
        && runtimeConfiguration
        && zeroFreshSegments
        && cleanup
        && allocationAdvantage
        && productionShape
        && binaryIdentity
        && meanSpeedup >= 1d
        && aggregateSpeedup >= 1d
        && confidenceLower95 >= RequiredConfidenceLower95;

    private static bool IsBalancedOrder(
        IReadOnlyList<NativeWorkspaceStatePairEvidence> pairs)
    {
        foreach (NativeWorkspaceStateImplementation implementation
            in Enum.GetValues<NativeWorkspaceStateImplementation>())
        {
            for (int position = 0; position < 3; position++)
            {
                if (pairs.Count(pair =>
                        pair.ImplementationOrder[position]
                            == implementation)
                    != pairs.Count / 3)
                {
                    return false;
                }
            }
        }

        return pairs.Count(pair =>
                Array.IndexOf(
                    pair.ImplementationOrder.ToArray(),
                    NativeWorkspaceStateImplementation.ManagedArray)
                < Array.IndexOf(
                    pair.ImplementationOrder.ToArray(),
                    NativeWorkspaceStateImplementation
                        .ExplicitStateWorkspace))
            == pairs.Count / 2;
    }

    private static async Task<NativeWorkspaceStateIsolatedPairEvidence>
        RunIsolatedPairAsync(
            int sampleIndex,
            NativeWorkspaceStateOptions options)
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
            typeof(NativeWorkspaceStateBenchmark).Assembly.Location);
        process.StartInfo.ArgumentList.Add(
            "--native-workspace-state-pair-worker");
        AddOption(process.StartInfo, "--sample-index", sampleIndex);
        AddOption(process.StartInfo, "--maps", options.MapCount);
        AddOption(process.StartInfo, "--map-size", options.MapSize);
        AddOption(
            process.StartInfo,
            "--workspace-length",
            options.WorkspaceLength);
        AddOption(process.StartInfo, "--workers", options.WorkerCount);
        AddOption(process.StartInfo, "--samples", options.SampleCount);
        AddOption(process.StartInfo, "--warmups", options.WarmupCount);
        AddOption(
            process.StartInfo,
            "--measurement-passes",
            options.MeasurementPassCount);
        AddOption(process.StartInfo, "--seed", options.Seed);
        process.StartInfo.Environment["DOTNET_TieredCompilation"] = "0";
        process.StartInfo.Environment["DOTNET_TieredPGO"] = "0";
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "The workspace pair worker did not start.");
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
                "The workspace pair worker exceeded 60 seconds.");
        }

        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The workspace pair worker failed with exit code {process.ExitCode}: {error}");
        }

        string json = output.Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()
            ?? throw new InvalidDataException(
                "The workspace pair worker produced no evidence.");
        return JsonSerializer.Deserialize<
                NativeWorkspaceStateIsolatedPairEvidence>(json, CompactJson)
            ?? throw new InvalidDataException(
                "The workspace pair evidence is invalid.");
    }

    private static void ValidatePair(
        NativeWorkspaceStateWorkerEvidence managed,
        NativeWorkspaceStateWorkerEvidence capturing,
        NativeWorkspaceStateWorkerEvidence explicitState)
    {
        if (!managed.Completed
            || !capturing.Completed
            || !explicitState.Completed
            || managed.OutputSha256 != capturing.OutputSha256
            || managed.OutputSha256 != explicitState.OutputSha256
            || !managed.WorkerChecksums.SequenceEqual(
                capturing.WorkerChecksums)
            || !managed.WorkerChecksums.SequenceEqual(
                explicitState.WorkerChecksums))
        {
            throw new InvalidDataException(
                "The paired workspace outputs are not equal.");
        }
    }

    private static string ComputeOutputSha256(
        IReadOnlyList<ulong> checksums)
    {
        byte[] bytes = new byte[checked(
            checksums.Count * sizeof(ulong))];
        for (int index = 0; index < checksums.Count; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan(
                    index * sizeof(ulong),
                    sizeof(ulong)),
                checksums[index]);
        }

        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void FillHeightMap(
        Span<float> destination,
        HeightProcessState state)
    {
        const float scale = 0.001f;
        int mapSize = state.MapSize;
        float seedPhase = (float)(state.Seed & 0xffff) * 0.000_001f;
        for (int x = 0; x < mapSize; x++)
        {
            int rowOffset = x * mapSize;
            float worldX = unchecked(x + state.BaseX) * scale;
            for (int z = 0; z < mapSize; z++)
            {
                float worldZ = unchecked(z + state.BaseZ) * scale;
                float wave = MathF.Sin(
                    worldX * 7.13f + worldZ * 3.71f + seedPhase);
                float ridge = MathF.Cos(
                    worldX * 2.17f - worldZ * 5.03f - seedPhase);
                destination[rowOffset + z] =
                    (wave + ridge + 2f) * 249.75f + 1f;
            }
        }
    }

    private static ulong ConsumeHeights(
        ulong checksum,
        ReadOnlySpan<float> values)
    {
        foreach (float value in values)
        {
            checksum ^= unchecked(
                (uint)BitConverter.SingleToInt32Bits(value));
            checksum *= FnvPrime;
        }

        return checksum;
    }

    private static NativeWorkspaceStateBinaryIdentity CreateBinaryIdentity(
        Assembly assembly)
    {
        string informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? string.Empty;
        int separator = informationalVersion.LastIndexOf('+');
        string sourceCommit = separator >= 0
            ? informationalVersion[(separator + 1)..]
            : string.Empty;
        string location = assembly.Location;
        return new NativeWorkspaceStateBinaryIdentity(
            assembly.GetName().Name ?? string.Empty,
            informationalVersion,
            sourceCommit,
            Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(location))));
    }

    private static NativeWorkspaceStateOptions ParseOptions(string[] args)
    {
        NativeWorkspaceStateOptions defaults = DefaultOptions;
        return new NativeWorkspaceStateOptions(
            ReadIntOption(args, "--maps", defaults.MapCount),
            ReadIntOption(args, "--map-size", defaults.MapSize),
            ReadIntOption(
                args,
                "--workspace-length",
                defaults.WorkspaceLength),
            ReadIntOption(args, "--workers", defaults.WorkerCount),
            ReadIntOption(args, "--samples", defaults.SampleCount),
            ReadIntOption(args, "--warmups", defaults.WarmupCount),
            ReadIntOption(
                args,
                "--measurement-passes",
                defaults.MeasurementPassCount),
            ReadLongOption(args, "--seed", defaults.Seed));
    }

    private static void ValidateOptions(
        NativeWorkspaceStateOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MapCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MapSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.WorkspaceLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.WorkerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.SampleCount);
        ArgumentOutOfRangeException.ThrowIfNegative(
            options.WarmupCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MeasurementPassCount);
        if ((options.SampleCount & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The sample count must be even.");
        }

        int valueCount = checked(options.MapSize * options.MapSize);
        if (options.WorkspaceLength < valueCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The workspace is smaller than one map.");
        }
    }

    private static int ReadIntOption(
        string[] args,
        string name,
        int defaultValue) =>
        int.TryParse(
            ReadOptionalOption(args, name),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int value)
                ? value
                : defaultValue;

    private static long ReadLongOption(
        string[] args,
        string name,
        long defaultValue) =>
        long.TryParse(
            ReadOptionalOption(args, name),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long value)
                ? value
                : defaultValue;

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

    private static void AddOption(
        ProcessStartInfo startInfo,
        string name,
        IFormattable value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(
            value.ToString(null, CultureInfo.InvariantCulture));
    }

    private sealed class WorkspaceExecution : IDisposable
    {
        private readonly NativeWorkspaceStateOptions _options;
        private readonly CountdownEvent _ready;
        private readonly CountdownEvent _completed;
        private readonly WorkspaceWorker[] _workers;
        private int _disposed;

        internal WorkspaceExecution(
            NativeWorkspaceStateImplementation implementation,
            NativeWorkspaceStateOptions options)
        {
            _options = options;
            _ready = new CountdownEvent(options.WorkerCount);
            _completed = new CountdownEvent(options.WorkerCount);
            _workers = new WorkspaceWorker[options.WorkerCount];
            for (int workerIndex = 0;
                workerIndex < _workers.Length;
                workerIndex++)
            {
                _workers[workerIndex] = new WorkspaceWorker(
                    implementation,
                    options,
                    workerIndex,
                    _ready,
                    _completed);
            }

            if (!_ready.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException(
                    "The persistent workspace workers did not start.");
            }

            ThrowWorkerFailure();
        }

        internal double RunBatch()
        {
            Stopwatch clock = Stopwatch.StartNew();
            RunCommand(WorkspaceWorkerCommand.Run);
            clock.Stop();
            return clock.Elapsed.TotalMilliseconds;
        }

        internal long ReadFreshSegmentCount()
        {
            RunCommand(WorkspaceWorkerCommand.Snapshot);
            return _workers.Sum(static worker =>
                worker.FreshSegmentAllocationCount);
        }

        internal ulong[] ReadChecksums()
        {
            var checksums = new ulong[_workers.Length];
            for (int index = 0; index < checksums.Length; index++)
            {
                checksums[index] = _workers[index].Checksum;
            }

            return checksums;
        }

        internal bool VerifyCancellationCleanup()
        {
            RunCommand(WorkspaceWorkerCommand.CancellationProbe);
            return _workers.All(static worker =>
                worker.CancellationCleanupPassed);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            RunCommand(WorkspaceWorkerCommand.Stop);
            foreach (WorkspaceWorker worker in _workers)
            {
                if (!worker.Join(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException(
                        "A persistent workspace worker did not stop.");
                }
            }

            ThrowWorkerFailure();
            _ready.Dispose();
            _completed.Dispose();
            foreach (WorkspaceWorker worker in _workers)
            {
                worker.Dispose();
            }
        }

        private void RunCommand(WorkspaceWorkerCommand command)
        {
            _completed.Reset(_workers.Length);
            foreach (WorkspaceWorker worker in _workers)
            {
                worker.Begin(command);
            }

            if (!_completed.Wait(TimeSpan.FromSeconds(60)))
            {
                throw new TimeoutException(
                    "The persistent workspace batch exceeded 60 seconds.");
            }

            ThrowWorkerFailure();
        }

        private void ThrowWorkerFailure()
        {
            Exception? failure = _workers
                .Select(static worker => worker.Failure)
                .FirstOrDefault(static exception => exception is not null);
            if (failure is not null)
            {
                throw new InvalidOperationException(
                    "A persistent workspace worker failed.",
                    failure);
            }
        }
    }

    private sealed class WorkspaceWorker : IDisposable
    {
        private readonly NativeWorkspaceStateImplementation _implementation;
        private readonly NativeWorkspaceStateOptions _options;
        private readonly int _workerIndex;
        private readonly CountdownEvent _ready;
        private readonly CountdownEvent _completed;
        private readonly AutoResetEvent _start = new(initialState: false);
        private readonly Thread _thread;
        private int _command;
        private int _readySignaled;

        internal WorkspaceWorker(
            NativeWorkspaceStateImplementation implementation,
            NativeWorkspaceStateOptions options,
            int workerIndex,
            CountdownEvent ready,
            CountdownEvent completed)
        {
            _implementation = implementation;
            _options = options;
            _workerIndex = workerIndex;
            _ready = ready;
            _completed = completed;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"NAM workspace benchmark {workerIndex}"
            };
            _thread.Start();
        }

        internal Exception? Failure { get; private set; }

        internal ulong Checksum { get; private set; }

        internal long FreshSegmentAllocationCount { get; private set; }

        internal bool CancellationCleanupPassed { get; private set; } = true;

        internal void Begin(WorkspaceWorkerCommand command)
        {
            Failure = null;
            Volatile.Write(ref _command, (int)command);
            _start.Set();
        }

        internal bool Join(TimeSpan timeout) => _thread.Join(timeout);

        public void Dispose() => _start.Dispose();

        private void Run()
        {
            try
            {
                if (_implementation
                    == NativeWorkspaceStateImplementation.ManagedArray)
                {
                    RunManaged();
                }
                else
                {
                    RunNative();
                }
            }
            catch (Exception exception)
            {
                Failure = exception;
                SignalReady();
            }
        }

        private void RunManaged()
        {
            var workspace = new float[_options.WorkspaceLength];
            SignalReady();
            while (WaitForCommand() is WorkspaceWorkerCommand command)
            {
                if (command == WorkspaceWorkerCommand.Stop)
                {
                    CompleteCommand();
                    break;
                }

                try
                {
                    switch (command)
                    {
                        case WorkspaceWorkerCommand.Run:
                            Checksum = RunManagedPartition(workspace);
                            break;
                        case WorkspaceWorkerCommand.Snapshot:
                            FreshSegmentAllocationCount = 0;
                            break;
                        case WorkspaceWorkerCommand.CancellationProbe:
                            CancellationCleanupPassed = true;
                            break;
                    }
                }
                catch (Exception exception)
                {
                    Failure = exception;
                }
                finally
                {
                    CompleteCommand();
                }
            }
        }

        private void RunNative()
        {
            using NativePool<float> pool = new(
                preLease: _options.WorkspaceLength,
                returnMemoryOnDispose:
                    NativeMemoryReturn.ToNativeMemory);
            using NativeWorkspace<float> workspace =
                pool.CreateWorkspace(_options.WorkspaceLength);
            SignalReady();
            while (WaitForCommand() is WorkspaceWorkerCommand command)
            {
                if (command == WorkspaceWorkerCommand.Stop)
                {
                    CompleteCommand();
                    break;
                }

                try
                {
                    switch (command)
                    {
                        case WorkspaceWorkerCommand.Run:
                            Checksum = _implementation
                                == NativeWorkspaceStateImplementation
                                    .CapturingWorkspace
                                    ? RunCapturingPartition(in workspace)
                                    : RunExplicitStatePartition(in workspace);
                            break;
                        case WorkspaceWorkerCommand.Snapshot:
                            FreshSegmentAllocationCount = pool
                                .GetStatistics()
                                .FreshSegmentAllocationCount;
                            break;
                        case WorkspaceWorkerCommand.CancellationProbe:
                            CancellationCleanupPassed =
                                VerifyCancellationCleanup(in workspace);
                            break;
                    }
                }
                catch (Exception exception)
                {
                    Failure = exception;
                }
                finally
                {
                    CompleteCommand();
                }
            }
        }

        private WorkspaceWorkerCommand? WaitForCommand()
        {
            _start.WaitOne();
            return (WorkspaceWorkerCommand)Volatile.Read(ref _command);
        }

        private void SignalReady()
        {
            if (Interlocked.Exchange(ref _readySignaled, 1) == 0)
            {
                _ready.Signal();
            }
        }

        private void CompleteCommand() => _completed.Signal();

        private ulong RunManagedPartition(float[] workspace)
        {
            int start = GetWorkerStart(_workerIndex);
            int end = GetWorkerStart(_workerIndex + 1);
            Span<float> values = workspace.AsSpan(
                0,
                checked(_options.MapSize * _options.MapSize));
            ulong checksum = FnvOffset;
            for (int mapIndex = start; mapIndex < end; mapIndex++)
            {
                HeightProcessState state = CreateState(
                    mapIndex,
                    checksum);
                FillHeightMap(values, state);
                checksum = ConsumeHeights(checksum, values);
            }

            return checksum;
        }

        private ulong RunCapturingPartition(
            scoped in NativeWorkspace<float> workspace)
        {
            int start = GetWorkerStart(_workerIndex);
            int end = GetWorkerStart(_workerIndex + 1);
            int valueCount = checked(
                _options.MapSize * _options.MapSize);
            ulong checksum = FnvOffset;
            for (int mapIndex = start; mapIndex < end; mapIndex++)
            {
                HeightProcessState state = CreateState(
                    mapIndex,
                    checksum);
                checksum = workspace.Process(
                    valueCount,
                    values => FillHeightMap(values, state),
                    values => ConsumeHeights(checksum, values));
            }

            return checksum;
        }

        private ulong RunExplicitStatePartition(
            scoped in NativeWorkspace<float> workspace)
        {
            int start = GetWorkerStart(_workerIndex);
            int end = GetWorkerStart(_workerIndex + 1);
            int valueCount = checked(
                _options.MapSize * _options.MapSize);
            ulong checksum = FnvOffset;
            for (int mapIndex = start; mapIndex < end; mapIndex++)
            {
                HeightProcessState state = CreateState(
                    mapIndex,
                    checksum);
                checksum = workspace.Process(
                    valueCount,
                    state,
                    static (values, callbackState) =>
                    {
                        FillHeightMap(values, callbackState);
                        return ConsumeHeights(
                            callbackState.Checksum,
                            values);
                    });
            }

            return checksum;
        }

        private bool VerifyCancellationCleanup(
            scoped in NativeWorkspace<float> workspace)
        {
            using CancellationTokenSource cancellation = new();
            bool canceled = false;
            try
            {
                workspace.Process(
                    1,
                    cancellation,
                    static (values, state) =>
                    {
                        values[0] = 17f;
                        state.Cancel();
                        return values[0];
                    },
                    cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }

            float result = workspace.Process(
                1,
                19f,
                static (values, state) =>
                {
                    values[0] = state;
                    return values[0];
                });
            return canceled && result == 19f;
        }

        private HeightProcessState CreateState(
            int mapIndex,
            ulong checksum)
        {
            int side = (int)Math.Ceiling(
                Math.Sqrt(_options.MapCount));
            int mapX = mapIndex / side;
            int mapZ = mapIndex % side;
            return new HeightProcessState(
                _options.Seed,
                (mapX - side / 2) * _options.MapSize,
                (mapZ - side / 2) * _options.MapSize,
                _options.MapSize,
                checksum);
        }

        private int GetWorkerStart(int workerIndex) =>
            (int)((long)workerIndex
                * _options.MapCount
                / _options.WorkerCount);
    }

    private readonly record struct HeightProcessState(
        long Seed,
        int BaseX,
        int BaseZ,
        int MapSize,
        ulong Checksum);

    private enum WorkspaceWorkerCommand
    {
        Run,
        Snapshot,
        CancellationProbe,
        Stop
    }
}

internal enum NativeWorkspaceStateImplementation
{
    ManagedArray,
    CapturingWorkspace,
    ExplicitStateWorkspace
}

internal sealed record NativeWorkspaceStateOptions(
    int MapCount,
    int MapSize,
    int WorkspaceLength,
    int WorkerCount,
    int SampleCount,
    int WarmupCount,
    int MeasurementPassCount,
    long Seed);

internal sealed record NativeWorkspaceStateWorkerEvidence(
    NativeWorkspaceStateImplementation Implementation,
    int WorkerCount,
    int MapCount,
    int MapSize,
    double SetupMilliseconds,
    double WarmupMilliseconds,
    double ElapsedMilliseconds,
    IReadOnlyList<double> MeasurementPassMilliseconds,
    double MapsPerSecond,
    long SetupManagedAllocatedBytes,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    long PeakWorkingSetBytes,
    long NativeFreshSegmentAllocationDelta,
    string OutputSha256,
    IReadOnlyList<ulong> WorkerChecksums,
    bool CancellationCleanupPassed,
    bool ExactlyOnceCleanupPassed,
    bool TieredCompilationDisabled,
    bool TieredPgoDisabled,
    bool Completed);

internal sealed record NativeWorkspaceStatePairEvidence(
    int SampleIndex,
    IReadOnlyList<NativeWorkspaceStateImplementation> ImplementationOrder,
    NativeWorkspaceStateWorkerEvidence Managed,
    NativeWorkspaceStateWorkerEvidence Capturing,
    NativeWorkspaceStateWorkerEvidence ExplicitState,
    double ManagedToExplicitStateSpeedup,
    double CapturingToExplicitStateSpeedup);

internal sealed record NativeWorkspaceStateIsolatedPairEvidence(
    int SampleIndex,
    IReadOnlyList<NativeWorkspaceStateImplementation> ImplementationOrder,
    IReadOnlyList<NativeWorkspaceStateWorkerEvidence> Evidence);

internal sealed record NativeWorkspaceStateBinaryIdentity(
    string AssemblyName,
    string InformationalVersion,
    string SourceCommit,
    string Sha256);

internal sealed record NativeWorkspaceStateReport(
    string SourceCommit,
    NativeWorkspaceStateBinaryIdentity RuntimeBinary,
    NativeWorkspaceStateBinaryIdentity BenchmarkBinary,
    NativeWorkspaceStateOptions Options,
    IReadOnlyList<NativeWorkspaceStatePairEvidence> Pairs,
    double ManagedMeanMilliseconds,
    double CapturingMeanMilliseconds,
    double ExplicitStateMeanMilliseconds,
    double ManagedToExplicitStateMeanSpeedup,
    double ManagedToExplicitStateAggregateSpeedup,
    double ManagedToExplicitStateConfidenceLower95,
    double CapturingToExplicitStateMeanSpeedup,
    double ManagedMeanTotalAllocatedBytes,
    double CapturingMeanTotalAllocatedBytes,
    double ExplicitStateMeanTotalAllocatedBytes,
    long ManagedMaximumPeakWorkingSetBytes,
    long CapturingMaximumPeakWorkingSetBytes,
    long ExplicitStateMaximumPeakWorkingSetBytes,
    bool ExactParity,
    bool BalancedOrder,
    bool RuntimeConfigurationValid,
    bool ZeroFreshSegments,
    bool CleanupPassed,
    bool AllocationAdvantage,
    bool ProductionShape,
    bool BinaryIdentityValid,
    bool GatePassed,
    double TotalElapsedMilliseconds,
    DateTimeOffset RecordedUtc);
