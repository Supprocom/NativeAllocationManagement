using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class VoxelHandoffBenchmark
{
    private const int DefaultWordCount = 262_144;
    private const int DefaultIterations = 256;
    private const int DefaultWarmupIterations = 32;
    private const int DefaultSampleCount = 10;
    private const int DefaultSeed = 0x51A7;
    private static readonly JsonSerializerOptions CompactJsonOptions = CreateJsonOptions(
        writeIndented: false);
    private static readonly JsonSerializerOptions IndentedJsonOptions = CreateJsonOptions(
        writeIndented: true);
    private static long _sink;

    internal static async Task<int> RunCommandAsync(string[] args)
    {
        if (args[0] == "--voxel-handoff-worker")
        {
            VoxelHandoffImplementation implementation = Enum.Parse<VoxelHandoffImplementation>(
                ReadRequiredOption(args, "--implementation"),
                ignoreCase: true);
            VoxelHandoffBenchmarkOptions options = ParseOptions(args);
            VoxelHandoffWorkerEvidence evidence = await RunWorkerAsync(
                implementation,
                options);
            Console.WriteLine(JsonSerializer.Serialize(
                evidence,
                CompactJsonOptions));
            return evidence.ExactParity ? 0 : 3;
        }

        VoxelHandoffBenchmarkOptions benchmarkOptions = ParseOptions(args);
        string? outputPath = ReadOptionalOption(args, "--output");
        VoxelHandoffBenchmarkReport report = await RunPairedAsync(
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

    internal static async Task<VoxelHandoffBenchmarkReport> RunPairedAsync(
        VoxelHandoffBenchmarkOptions options)
    {
        ValidateOptions(options);
        Stopwatch totalClock = Stopwatch.StartNew();
        VoxelHandoffPairEvidence[] pairs = new VoxelHandoffPairEvidence[
            options.SampleCount];
        for (int sampleIndex = 0; sampleIndex < options.SampleCount; sampleIndex++)
        {
            VoxelHandoffImplementation first = GetFirstImplementation(
                sampleIndex);
            VoxelHandoffWorkerEvidence firstEvidence = await RunIsolatedWorkerAsync(
                first,
                options);
            VoxelHandoffImplementation second = first == VoxelHandoffImplementation.Managed
                ? VoxelHandoffImplementation.Native
                : VoxelHandoffImplementation.Managed;
            VoxelHandoffWorkerEvidence secondEvidence = await RunIsolatedWorkerAsync(
                second,
                options);
            VoxelHandoffWorkerEvidence managed = first == VoxelHandoffImplementation.Managed
                ? firstEvidence
                : secondEvidence;
            VoxelHandoffWorkerEvidence native = first == VoxelHandoffImplementation.Native
                ? firstEvidence
                : secondEvidence;
            ValidatePair(managed, native, options);
            pairs[sampleIndex] = new VoxelHandoffPairEvidence(
                sampleIndex,
                first,
                managed,
                native,
                managed.ElapsedMilliseconds / native.ElapsedMilliseconds);
        }

        totalClock.Stop();
        double managedMean = pairs.Average(pair => pair.Managed.ElapsedMilliseconds);
        double nativeMean = pairs.Average(pair => pair.Native.ElapsedMilliseconds);
        double[] speedups = pairs.Select(pair => pair.ManagedToNativeSpeedup).ToArray();
        double speedupMean = speedups.Average();
        double confidenceLower = ConfidenceLower95(speedups);
        long logicalBytes = pairs[0].Managed.LogicalBytes;
        bool parity = pairs.All(pair => pair.Managed.ExactParity
            && pair.Native.ExactParity
            && pair.Managed.ExactOutputSha256 == pair.Native.ExactOutputSha256
            && pair.Managed.Checksum == pair.Native.Checksum);
        bool balancedOrder = pairs.Count(pair => pair.FirstImplementation == VoxelHandoffImplementation.Managed)
            == options.SampleCount / 2
            && pairs.Count(pair => pair.FirstImplementation == VoxelHandoffImplementation.Native)
                == options.SampleCount / 2;
        return new VoxelHandoffBenchmarkReport(
            options,
            pairs,
            managedMean,
            nativeMean,
            speedupMean,
            confidenceLower,
            LogicalGigabytesPerSecond(logicalBytes, managedMean),
            LogicalGigabytesPerSecond(logicalBytes, nativeMean),
            parity,
            balancedOrder,
            speedupMean > 1d && confidenceLower > 1d,
            totalClock.Elapsed.TotalMilliseconds,
            DateTimeOffset.UtcNow);
    }

    internal static async Task<VoxelHandoffWorkerEvidence> RunWorkerAsync(
        VoxelHandoffImplementation implementation,
        VoxelHandoffBenchmarkOptions options)
    {
        ValidateOptions(options);
        Stopwatch setupClock = Stopwatch.StartNew();
        List<uint> source = CreateVoxelWords(
            options.WordCount,
            options.Seed);
        byte[] exactOutput = CreateManagedUpload(source);
        string exactHash = Convert.ToHexString(
            SHA256.HashData(exactOutput));
        long perItemChecksum = ConsumeUpload(exactOutput);
        bool exactParity;
        NativePool<uint>? pool = null;
        TransferInitializer? initializer = null;
        if (implementation == VoxelHandoffImplementation.Native)
        {
            pool = new NativePool<uint>(
                options.WordCount,
                NativeMemoryReturn.ToNativeMemory);
            initializer = new TransferInitializer(source);
            NativeTransfer<uint> verification = pool.RentTransferable(
                options.WordCount,
                initializer.Action);
            exactParity = verification.Read(view =>
                MemoryMarshal.AsBytes(view.AsSpan()).SequenceEqual(exactOutput));
            verification.Dispose();
        }
        else
        {
            exactParity = CreateManagedUpload(source).AsSpan().SequenceEqual(exactOutput);
        }

        setupClock.Stop();
        Stopwatch warmupClock = Stopwatch.StartNew();
        BatchResult warmup = implementation == VoxelHandoffImplementation.Managed
            ? await RunManagedBatchAsync(source, options.WarmupIterations)
            : await RunNativeBatchAsync(
                pool!,
                initializer!,
                options.WordCount,
                options.WarmupIterations);
        warmupClock.Stop();
        long expectedWarmupChecksum = unchecked(
            perItemChecksum * options.WarmupIterations);
        if (warmup.Checksum != expectedWarmupChecksum)
        {
            throw new InvalidDataException(
                "The handoff warmup changed the output checksum.");
        }

        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        using Process process = Process.GetCurrentProcess();
        long workingSetBefore = process.WorkingSet64;
        NativeOwnerStatistics statisticsBefore = pool?.GetStatistics() ?? default;
        BatchResult measured = implementation == VoxelHandoffImplementation.Managed
            ? await RunManagedBatchAsync(source, options.Iterations)
            : await RunNativeBatchAsync(
                pool!,
                initializer!,
                options.WordCount,
                options.Iterations);
        process.Refresh();
        long workingSetAfter = process.WorkingSet64;
        long managedAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        long expectedChecksum = unchecked(perItemChecksum * options.Iterations);
        if (measured.Checksum != expectedChecksum)
        {
            throw new InvalidDataException(
                "The measured handoff changed the output checksum.");
        }

        NativeOwnerStatistics statistics = pool?.GetStatistics() ?? default;
        pool?.Dispose();
        long logicalBytes = checked(
            (long)options.WordCount
            * sizeof(uint)
            * options.Iterations);
        Volatile.Write(ref _sink, measured.Checksum);
        return new VoxelHandoffWorkerEvidence(
            implementation,
            options.WordCount,
            options.Iterations,
            options.WarmupIterations,
            options.Seed,
            logicalBytes,
            setupClock.Elapsed.TotalMilliseconds,
            warmupClock.Elapsed.TotalMilliseconds,
            measured.ElapsedMilliseconds,
            managedAllocated,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            workingSetBefore,
            workingSetAfter,
            statistics.RetainedBytes,
            statistics.FreshSegmentAllocationCount,
            statistics.FreshSegmentAllocationCount
                - statisticsBefore.FreshSegmentAllocationCount,
            measured.Checksum,
            exactHash,
            GetInformationalVersion(typeof(NativePool<>).Assembly),
            GetInformationalVersion(typeof(VoxelHandoffBenchmark).Assembly),
            Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")
                ?? "unset",
            Environment.GetEnvironmentVariable("DOTNET_TieredPGO")
                ?? "unset",
            Environment.ProcessorCount,
            System.Runtime.GCSettings.IsServerGC,
            exactParity);
    }

    internal static List<uint> CreateVoxelWords(
        int wordCount,
        int seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(wordCount);
        List<uint> words = new(wordCount);
        for (int index = 0; index < wordCount; index++)
        {
            int cellIndex = (index / 8) % VoxelMath.CellsPerChunk;
            int x = cellIndex % VoxelMath.ChunkDimension;
            int y = (cellIndex / VoxelMath.ChunkDimension) % VoxelMath.ChunkDimension;
            int z = cellIndex / (VoxelMath.ChunkDimension * VoxelMath.ChunkDimension);
            int blockId = VoxelMath.BlockIdForCell(
                seed,
                index / (VoxelMath.CellsPerChunk * 8),
                x,
                y,
                z);
            int value = (index & 7) switch
            {
                0 => VoxelMath.VertexValue(cellIndex, index % 6, index & 3, 0, blockId),
                1 => VoxelMath.VertexValue(cellIndex, index % 6, index & 3, 1, blockId),
                2 => VoxelMath.VertexValue(cellIndex, index % 6, index & 3, 2, blockId),
                3 => blockId,
                4 => VoxelMath.IndexValue(cellIndex * 4, index % 6),
                5 => cellIndex,
                6 => index,
                _ => seed ^ index
            };
            words.Add(unchecked((uint)value));
        }

        return words;
    }

    internal static VoxelHandoffImplementation GetFirstImplementation(
        int sampleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleIndex);
        return (sampleIndex & 1) == 0
            ? VoxelHandoffImplementation.Managed
            : VoxelHandoffImplementation.Native;
    }

    internal static byte[] CreateManagedUpload(List<uint> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        uint[] materialized = source.ToArray();
        byte[] upload = new byte[checked(materialized.Length * sizeof(uint))];
        Buffer.BlockCopy(
            materialized,
            0,
            upload,
            0,
            upload.Length);
        return upload;
    }

    internal static bool VerifyNativeUpload(
        List<uint> source,
        byte[] expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        using NativePool<uint> pool = new(
            source.Count,
            NativeMemoryReturn.ToNativeMemory);
        TransferInitializer initializer = new(source);
        NativeTransfer<uint> transfer = pool.RentTransferable(
            source.Count,
            initializer.Action);
        bool equal = transfer.Read(view =>
            MemoryMarshal.AsBytes(view.AsSpan()).SequenceEqual(expected));
        transfer.Dispose();
        return equal;
    }

    private static async Task<BatchResult> RunManagedBatchAsync(
        List<uint> source,
        int iterations)
    {
        Channel<byte[]> channel = Channel.CreateBounded<byte[]>(
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
            await foreach (byte[] upload in channel.Reader.ReadAllAsync())
            {
                checksum = unchecked(checksum + ConsumeUpload(upload));
            }

            return checksum;
        });
        await ready.Task;
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                await channel.Writer.WriteAsync(
                    CreateManagedUpload(source));
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        long result = await consumer;
        clock.Stop();
        return new BatchResult(
            result,
            clock.Elapsed.TotalMilliseconds);
    }

    private static async Task<BatchResult> RunNativeBatchAsync(
        NativePool<uint> pool,
        TransferInitializer initializer,
        int wordCount,
        int iterations)
    {
        Channel<NativeTransfer<uint>> channel =
            Channel.CreateBounded<NativeTransfer<uint>>(
                new BoundedChannelOptions(1)
                {
                    SingleReader = true,
                    SingleWriter = true
                });
        TaskCompletionSource ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        NativeConsumer nativeConsumer = new();
        Task<long> consumer = Task.Run(async () =>
        {
            ready.SetResult();
            await foreach (NativeTransfer<uint> transfer in channel.Reader.ReadAllAsync())
            {
                try
                {
                    transfer.Access(nativeConsumer.Action);
                }
                finally
                {
                    transfer.Dispose();
                }
            }

            return nativeConsumer.Checksum;
        });
        await ready.Task;
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                NativeTransfer<uint>? source = pool.RentTransferable(
                    wordCount,
                    initializer.Action);
                await channel.Writer.WriteAsync(
                    NativeTransfer<uint>.Move(ref source));
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        long result = await consumer;
        clock.Stop();
        return new BatchResult(
            result,
            clock.Elapsed.TotalMilliseconds);
    }

    private static long ConsumeUpload(ReadOnlySpan<byte> upload)
    {
        if (upload.Length < 32)
        {
            long small = upload.Length;
            foreach (byte value in upload)
            {
                small = unchecked((small * 397) ^ value);
            }

            return small;
        }

        int middle = (upload.Length / 2) & ~7;
        return unchecked((long)(
            BinaryPrimitives.ReadUInt64LittleEndian(upload)
            ^ BinaryPrimitives.ReadUInt64LittleEndian(upload.Slice(middle, 8))
            ^ BinaryPrimitives.ReadUInt64LittleEndian(upload[^16..])
            ^ BinaryPrimitives.ReadUInt64LittleEndian(upload[^8..])
            ^ (ulong)upload.Length));
    }

    private static async Task<VoxelHandoffWorkerEvidence> RunIsolatedWorkerAsync(
        VoxelHandoffImplementation implementation,
        VoxelHandoffBenchmarkOptions options)
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
                typeof(VoxelHandoffBenchmark).Assembly.Location);
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
                "The Voxel handoff worker did not start.");
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        Task exitTask = process.WaitForExitAsync();
        if (await Task.WhenAny(
            exitTask,
            Task.Delay(TimeSpan.FromSeconds(60))) != exitTask)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"The {implementation} Voxel handoff worker exceeded 60 seconds.");
        }

        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The {implementation} Voxel handoff worker failed with exit code {process.ExitCode}: {error}");
        }

        return JsonSerializer.Deserialize<VoxelHandoffWorkerEvidence>(
            output.Trim(),
            CompactJsonOptions)
            ?? throw new InvalidDataException(
                "The Voxel handoff worker did not return evidence.");
    }

    private static void AddWorkerArguments(
        ICollection<string> arguments,
        VoxelHandoffImplementation implementation,
        VoxelHandoffBenchmarkOptions options)
    {
        arguments.Add("--voxel-handoff-worker");
        arguments.Add("--implementation");
        arguments.Add(implementation.ToString());
        arguments.Add("--words");
        arguments.Add(options.WordCount.ToString(CultureInfo.InvariantCulture));
        arguments.Add("--iterations");
        arguments.Add(options.Iterations.ToString(CultureInfo.InvariantCulture));
        arguments.Add("--warmup");
        arguments.Add(options.WarmupIterations.ToString(CultureInfo.InvariantCulture));
        arguments.Add("--samples");
        arguments.Add(options.SampleCount.ToString(CultureInfo.InvariantCulture));
        arguments.Add("--seed");
        arguments.Add(options.Seed.ToString(CultureInfo.InvariantCulture));
    }

    private static void ValidatePair(
        VoxelHandoffWorkerEvidence managed,
        VoxelHandoffWorkerEvidence native,
        VoxelHandoffBenchmarkOptions options)
    {
        if (managed.Implementation != VoxelHandoffImplementation.Managed
            || native.Implementation != VoxelHandoffImplementation.Native
            || managed.WordCount != options.WordCount
            || native.WordCount != options.WordCount
            || managed.Iterations != options.Iterations
            || native.Iterations != options.Iterations
            || !managed.ExactParity
            || !native.ExactParity
            || managed.ExactOutputSha256 != native.ExactOutputSha256
            || managed.Checksum != native.Checksum)
        {
            throw new InvalidDataException(
                "The paired Voxel handoff evidence is not equivalent.");
        }
    }

    private static VoxelHandoffBenchmarkOptions ParseOptions(string[] args)
    {
        VoxelHandoffBenchmarkOptions options = new(
            ReadInt32Option(args, "--words", DefaultWordCount),
            ReadInt32Option(args, "--iterations", DefaultIterations),
            ReadInt32Option(args, "--warmup", DefaultWarmupIterations),
            ReadInt32Option(args, "--samples", DefaultSampleCount),
            ReadInt32Option(args, "--seed", DefaultSeed));
        ValidateOptions(options);
        return options;
    }

    private static void ValidateOptions(VoxelHandoffBenchmarkOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.WordCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Iterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.WarmupIterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SampleCount);
        if ((options.SampleCount & 1) != 0)
        {
            throw new ArgumentException(
                "The Voxel handoff sample count must be even.",
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
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == name)
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static double ConfidenceLower95(double[] samples)
    {
        if (samples.Length < 2)
        {
            return double.NaN;
        }

        double mean = samples.Average();
        double sumSquares = samples.Sum(value =>
            (value - mean) * (value - mean));
        double standardDeviation = Math.Sqrt(
            sumSquares / (samples.Length - 1));
        return mean - StudentCritical95(samples.Length - 1)
            * standardDeviation
            / Math.Sqrt(samples.Length);
    }

    private static double StudentCritical95(int degreesOfFreedom) =>
        degreesOfFreedom switch
        {
            1 => 12.706,
            2 => 4.303,
            3 => 3.182,
            4 => 2.776,
            5 => 2.571,
            6 => 2.447,
            7 => 2.365,
            8 => 2.306,
            9 => 2.262,
            10 => 2.228,
            11 => 2.201,
            12 => 2.179,
            13 => 2.160,
            14 => 2.145,
            15 => 2.131,
            16 => 2.120,
            17 => 2.110,
            18 => 2.101,
            19 => 2.093,
            20 => 2.086,
            21 => 2.080,
            22 => 2.074,
            23 => 2.069,
            24 => 2.064,
            25 => 2.060,
            26 => 2.056,
            27 => 2.052,
            28 => 2.048,
            29 => 2.045,
            _ => 1.96
        };

    private static double LogicalGigabytesPerSecond(
        long logicalBytes,
        double elapsedMilliseconds) =>
        logicalBytes
        / (1024d * 1024d * 1024d)
        / (elapsedMilliseconds / 1000d);

    private static string GetInformationalVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
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

    private sealed class TransferInitializer
    {
        private readonly List<uint> _source;

        internal TransferInitializer(List<uint> source)
        {
            _source = source;
            Action = Initialize;
        }

        internal NativeLeaseInitializer<uint> Action { get; }

        private void Initialize(scoped NativeLeaseWriter<uint> writer)
        {
            writer.Write(CollectionsMarshal.AsSpan(_source));
        }
    }

    private sealed class NativeConsumer
    {
        internal NativeConsumer()
        {
            Action = Consume;
        }

        internal NativeLeaseAction<uint> Action { get; }

        internal long Checksum { get; private set; }

        private void Consume(scoped NativeLeaseView<uint> view)
        {
            Checksum = unchecked(
                Checksum
                + ConsumeUpload(
                    MemoryMarshal.AsBytes(view.AsSpan())));
        }
    }

    private readonly record struct BatchResult(
        long Checksum,
        double ElapsedMilliseconds);
}
