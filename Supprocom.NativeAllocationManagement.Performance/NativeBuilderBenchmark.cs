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

internal static class NativeBuilderBenchmark
{
    private const int DefaultElementCount = 262_144;
    private const int DefaultInitialCapacity = 1_024;
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
        if (args[0] == "--native-builder-worker")
        {
            NativeBuilderBenchmarkImplementation implementation =
                Enum.Parse<NativeBuilderBenchmarkImplementation>(
                    ReadRequiredOption(args, "--implementation"),
                    ignoreCase: true);
            NativeBuilderBenchmarkOptions options = ParseOptions(args);
            NativeBuilderWorkerEvidence evidence = RunWorker(
                implementation,
                options);
            Console.WriteLine(JsonSerializer.Serialize(
                evidence,
                CompactJsonOptions));
            return evidence.ExactParity ? 0 : 3;
        }

        NativeBuilderBenchmarkOptions benchmarkOptions =
            ParseOptions(args);
        string? outputPath = ReadOptionalOption(args, "--output");
        NativeBuilderBenchmarkReport report = await RunPairedAsync(
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
                GetFirstImplementation(sampleIndex);
            NativeBuilderWorkerEvidence firstEvidence =
                await RunIsolatedWorkerAsync(first, options);
            NativeBuilderBenchmarkImplementation second =
                first == NativeBuilderBenchmarkImplementation.ManagedList
                    ? NativeBuilderBenchmarkImplementation.NativeBuilder
                    : NativeBuilderBenchmarkImplementation.ManagedList;
            NativeBuilderWorkerEvidence secondEvidence =
                await RunIsolatedWorkerAsync(second, options);
            NativeBuilderWorkerEvidence managed =
                first == NativeBuilderBenchmarkImplementation.ManagedList
                    ? firstEvidence
                    : secondEvidence;
            NativeBuilderWorkerEvidence native =
                first == NativeBuilderBenchmarkImplementation.NativeBuilder
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
            && pair.Managed.ExactOutputSha256
                == pair.Native.ExactOutputSha256
            && pair.Managed.Checksum == pair.Native.Checksum);
        bool balancedOrder = pairs.Count(pair =>
                pair.FirstImplementation
                    == NativeBuilderBenchmarkImplementation.ManagedList)
            == options.SampleCount / 2
            && pairs.Count(pair =>
                pair.FirstImplementation
                    == NativeBuilderBenchmarkImplementation.NativeBuilder)
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

    internal static NativeBuilderWorkerEvidence RunWorker(
        NativeBuilderBenchmarkImplementation implementation,
        NativeBuilderBenchmarkOptions options)
    {
        ValidateOptions(options);
        Stopwatch setupClock = Stopwatch.StartNew();
        uint[] expected = BuildManagedOutput(options);
        string exactHash = Convert.ToHexString(
            SHA256.HashData(MemoryMarshal.AsBytes(
                expected.AsSpan())));
        long expectedChecksum = Consume(expected);
        NativePool<uint>? pool = null;
        bool exactParity;
        if (implementation
            == NativeBuilderBenchmarkImplementation.NativeBuilder)
        {
            pool = new NativePool<uint>(
                options.InitialCapacity,
                NativeMemoryReturn.ToNativeMemory);
            uint[] nativeOutput = BuildNativeOutput(pool, options);
            exactParity = nativeOutput.AsSpan().SequenceEqual(expected);
        }
        else
        {
            exactParity = BuildManagedOutput(options)
                .AsSpan()
                .SequenceEqual(expected);
        }

        setupClock.Stop();
        Stopwatch warmupClock = Stopwatch.StartNew();
        long warmupChecksum = implementation
            == NativeBuilderBenchmarkImplementation.ManagedList
                ? RunManagedBatch(options, options.WarmupIterations)
                : RunNativeBatch(
                    pool!,
                    options,
                    options.WarmupIterations);
        warmupClock.Stop();
        if (warmupChecksum != unchecked(
            expectedChecksum * options.WarmupIterations))
        {
            throw new InvalidDataException(
                "The native builder warmup changed the output checksum.");
        }

        long allocatedBefore = GC.GetTotalAllocatedBytes(
            precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        long heapBefore = GC.GetGCMemoryInfo().HeapSizeBytes;
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        NativeOwnerStatistics statisticsBefore =
            pool?.GetStatistics() ?? default;
        Stopwatch measuredClock = Stopwatch.StartNew();
        long measuredChecksum = implementation
            == NativeBuilderBenchmarkImplementation.ManagedList
                ? RunManagedBatch(options, options.Iterations)
                : RunNativeBatch(
                    pool!,
                    options,
                    options.Iterations);
        measuredClock.Stop();
        process.Refresh();
        long workingSetAfter = process.WorkingSet64;
        long managedAllocated = GC.GetTotalAllocatedBytes(
            precise: true) - allocatedBefore;
        long expectedMeasuredChecksum = unchecked(
            expectedChecksum * options.Iterations);
        if (measuredChecksum != expectedMeasuredChecksum)
        {
            throw new InvalidDataException(
                "The measured native builder output checksum changed.");
        }

        NativeOwnerStatistics statistics =
            pool?.GetStatistics() ?? default;
        long logicalBytes = checked(
            (long)options.ElementCount
            * sizeof(uint)
            * options.Iterations);
        double elapsedMilliseconds =
            measuredClock.Elapsed.TotalMilliseconds;
        pool?.Dispose();
        Volatile.Write(ref _sink, measuredChecksum);
        return new NativeBuilderWorkerEvidence(
            implementation,
            options.ElementCount,
            options.InitialCapacity,
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
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            heapBefore,
            GC.GetGCMemoryInfo().HeapSizeBytes,
            workingSetBefore,
            workingSetAfter,
            Math.Max(workingSetBefore, workingSetAfter),
            statistics.RetainedBytes,
            statistics.FreshSegmentAllocationCount,
            statistics.FreshSegmentAllocationCount
                - statisticsBefore.FreshSegmentAllocationCount,
            measuredChecksum,
            exactHash,
            GetInformationalVersion(typeof(NativePool<>).Assembly),
            GetInformationalVersion(typeof(NativeBuilderBenchmark).Assembly),
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredCompilation") ?? "unset",
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredPGO") ?? "unset",
            Environment.ProcessorCount,
            System.Runtime.GCSettings.IsServerGC,
            exactParity);
    }

    internal static NativeBuilderBenchmarkImplementation
        GetFirstImplementation(int sampleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleIndex);
        return (sampleIndex & 1) == 0
            ? NativeBuilderBenchmarkImplementation.ManagedList
            : NativeBuilderBenchmarkImplementation.NativeBuilder;
    }

    internal static uint[] BuildManagedOutput(
        NativeBuilderBenchmarkOptions options)
    {
        List<uint> values = new(options.InitialCapacity);
        Span<uint> batch = stackalloc uint[options.BatchSize];
        for (int offset = 0;
            offset < options.ElementCount;
            offset += options.BatchSize)
        {
            int length = Math.Min(
                options.BatchSize,
                options.ElementCount - offset);
            Span<uint> current = batch[..length];
            FillBatch(current, offset, options.Seed);
            AppendToList(values, current);
        }

        return values.ToArray();
    }

    internal static uint[] BuildNativeOutput(
        NativePool<uint> pool,
        NativeBuilderBenchmarkOptions options)
    {
        using NativeBuilder<uint> builder = pool.CreateBuilder(
            options.InitialCapacity);
        Span<uint> batch = stackalloc uint[options.BatchSize];
        for (int offset = 0;
            offset < options.ElementCount;
            offset += options.BatchSize)
        {
            int length = Math.Min(
                options.BatchSize,
                options.ElementCount - offset);
            Span<uint> current = batch[..length];
            FillBatch(current, offset, options.Seed);
            builder.Append(current);
        }

        NativeTransfer<uint> transfer = builder.Complete();
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

    private static long RunManagedBatch(
        NativeBuilderBenchmarkOptions options,
        int iterations)
    {
        long checksum = 0;
        Span<uint> batch = stackalloc uint[options.BatchSize];
        for (int iteration = 0;
            iteration < iterations;
            iteration++)
        {
            List<uint> values = new(options.InitialCapacity);
            for (int offset = 0;
                offset < options.ElementCount;
                offset += options.BatchSize)
            {
                int length = Math.Min(
                    options.BatchSize,
                    options.ElementCount - offset);
                Span<uint> current = batch[..length];
                FillBatch(current, offset, options.Seed);
                AppendToList(values, current);
            }

            uint[] output = values.ToArray();
            checksum = unchecked(checksum + Consume(output));
        }

        return checksum;
    }

    private static long RunNativeBatch(
        NativePool<uint> pool,
        NativeBuilderBenchmarkOptions options,
        int iterations)
    {
        long checksum = 0;
        Span<uint> batch = stackalloc uint[options.BatchSize];
        for (int iteration = 0;
            iteration < iterations;
            iteration++)
        {
            using NativeBuilder<uint> builder = pool.CreateBuilder(
                options.InitialCapacity);
            for (int offset = 0;
                offset < options.ElementCount;
                offset += options.BatchSize)
            {
                int length = Math.Min(
                    options.BatchSize,
                    options.ElementCount - offset);
                Span<uint> current = batch[..length];
                FillBatch(current, offset, options.Seed);
                builder.Append(current);
            }

            NativeTransfer<uint> transfer = builder.Complete();
            try
            {
                checksum = unchecked(
                    checksum
                    + transfer.Read(
                        static view => Consume(view.AsSpan())));
            }
            finally
            {
                transfer.Dispose();
            }
        }

        return checksum;
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
        int seed)
    {
        uint state = unchecked((uint)seed)
            ^ unchecked((uint)offset * 0x9E3779B9U);
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

    private static long Consume(ReadOnlySpan<uint> values)
    {
        if (values.IsEmpty)
        {
            return 0;
        }

        int middle = values.Length / 2;
        return unchecked((long)(
            values[0]
            ^ BitOperations.RotateLeft(values[middle], 7)
            ^ BitOperations.RotateLeft(values[^1], 17)
            ^ (uint)values.Length));
    }

    private static async Task<NativeBuilderWorkerEvidence>
        RunIsolatedWorkerAsync(
            NativeBuilderBenchmarkImplementation implementation,
            NativeBuilderBenchmarkOptions options)
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
                typeof(NativeBuilderBenchmark).Assembly.Location);
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
                "The native builder benchmark worker did not start.");
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
            if (timeoutClock.Elapsed > TimeSpan.FromSeconds(60))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException(
                    $"The {implementation} native builder worker exceeded 60 seconds.");
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
                $"The {implementation} native builder worker failed with exit code {process.ExitCode}: {error}");
        }

        NativeBuilderWorkerEvidence evidence =
            JsonSerializer.Deserialize<NativeBuilderWorkerEvidence>(
                output.Trim(),
                CompactJsonOptions)
            ?? throw new InvalidDataException(
                "The native builder worker did not return evidence.");
        return evidence with
        {
            PeakWorkingSetBytes = Math.Max(
                peakWorkingSet,
                evidence.PeakWorkingSetBytes)
        };
    }

    private static void AddWorkerArguments(
        ICollection<string> arguments,
        NativeBuilderBenchmarkImplementation implementation,
        NativeBuilderBenchmarkOptions options)
    {
        arguments.Add("--native-builder-worker");
        arguments.Add("--implementation");
        arguments.Add(implementation.ToString());
        arguments.Add("--elements");
        arguments.Add(options.ElementCount.ToString(
            CultureInfo.InvariantCulture));
        arguments.Add("--initial-capacity");
        arguments.Add(options.InitialCapacity.ToString(
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
    }

    private static void ValidatePair(
        NativeBuilderWorkerEvidence managed,
        NativeBuilderWorkerEvidence native,
        NativeBuilderBenchmarkOptions options)
    {
        if (managed.Implementation
                != NativeBuilderBenchmarkImplementation.ManagedList
            || native.Implementation
                != NativeBuilderBenchmarkImplementation.NativeBuilder
            || managed.ElementCount != options.ElementCount
            || native.ElementCount != options.ElementCount
            || managed.InitialCapacity != options.InitialCapacity
            || native.InitialCapacity != options.InitialCapacity
            || managed.BatchSize != options.BatchSize
            || native.BatchSize != options.BatchSize
            || managed.Iterations != options.Iterations
            || native.Iterations != options.Iterations
            || !managed.ExactParity
            || !native.ExactParity
            || managed.ExactOutputSha256
                != native.ExactOutputSha256
            || managed.Checksum != native.Checksum)
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
                "--initial-capacity",
                DefaultInitialCapacity),
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
                DefaultSeed));
        ValidateOptions(options);
        return options;
    }

    private static void ValidateOptions(
        NativeBuilderBenchmarkOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.ElementCount);
        ArgumentOutOfRangeException.ThrowIfNegative(
            options.InitialCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.BatchSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            options.BatchSize,
            1_024);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.Iterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.WarmupIterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.SampleCount);
        if ((options.SampleCount & 1) != 0)
        {
            throw new ArgumentException(
                "The native builder sample count must be even.",
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
}
