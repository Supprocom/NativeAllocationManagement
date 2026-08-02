using System.Buffers.Binary;
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
        if (args[0] == "--native-builder-worker")
        {
            NativeBuilderBenchmarkImplementation implementation =
                Enum.Parse<NativeBuilderBenchmarkImplementation>(
                    ReadRequiredOption(args, "--implementation"),
                    ignoreCase: true);
            NativeBuilderBenchmarkOptions options = ParseOptions(args);
            NativeBuilderWorkerEvidence evidence = await RunWorkerAsync(
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

    internal static async Task<NativeBuilderWorkerEvidence> RunWorkerAsync(
        NativeBuilderBenchmarkImplementation implementation,
        NativeBuilderBenchmarkOptions options)
    {
        ValidateOptions(options);
        Stopwatch setupClock = Stopwatch.StartNew();
        NativeBuilderExactOutput expected = BuildManagedOutput(options);
        string exactHash = ComputeExactHash(expected);
        long expectedChecksum = Consume(expected);
        NativePool<uint>? pool = null;
        bool exactParity;
        if (implementation
            == NativeBuilderBenchmarkImplementation.NativeBuilder)
        {
            pool = new NativePool<uint>(
                preLease: options.PreLease,
                returnMemoryOnDispose:
                    NativeMemoryReturn.ToNativeMemory);
            NativeBuilderExactOutput nativeOutput =
                BuildNativeOutput(pool, options);
            exactParity = OutputsEqual(nativeOutput, expected);
        }
        else
        {
            exactParity = OutputsEqual(
                BuildManagedOutput(options),
                expected);
        }

        setupClock.Stop();
        Stopwatch warmupClock = Stopwatch.StartNew();
        NativeBuilderBatchResult warmup = implementation
            == NativeBuilderBenchmarkImplementation.ManagedList
                ? await RunManagedBatchAsync(
                    options,
                    options.WarmupIterations)
                : await RunNativeBatchAsync(
                    pool!,
                    options,
                    options.WarmupIterations);
        warmupClock.Stop();
        if (warmup.Checksum != unchecked(
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
        NativeBuilderBatchResult measured = implementation
            == NativeBuilderBenchmarkImplementation.ManagedList
                ? await RunManagedBatchAsync(
                    options,
                    options.Iterations)
                : await RunNativeBatchAsync(
                    pool!,
                    options,
                    options.Iterations);
        process.Refresh();
        long workingSetAfter = process.WorkingSet64;
        long managedAllocated = GC.GetTotalAllocatedBytes(
            precise: true) - allocatedBefore;
        long expectedMeasuredChecksum = unchecked(
            expectedChecksum * options.Iterations);
        if (measured.Checksum != expectedMeasuredChecksum)
        {
            throw new InvalidDataException(
                "The measured native builder output checksum changed.");
        }

        NativeOwnerStatistics statistics =
            pool?.GetStatistics() ?? default;
        NativeBuilderPhaseEvidence phaseEvidence = implementation
            == NativeBuilderBenchmarkImplementation.ManagedList
                ? MeasureManagedPhases(options)
                : MeasureNativePhases(pool!, options);
        long logicalBytes = checked(
            (long)options.ElementCount
            * sizeof(uint)
            * options.Iterations);
        double elapsedMilliseconds = measured.ElapsedMilliseconds;
        pool?.Dispose();
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
            measured.Checksum,
            exactHash,
            GetInformationalVersion(typeof(NativePool<>).Assembly),
            GetInformationalVersion(typeof(NativeBuilderBenchmark).Assembly),
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredCompilation") ?? "unset",
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredPGO") ?? "unset",
            Environment.ProcessorCount,
            System.Runtime.GCSettings.IsServerGC,
            phaseEvidence,
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
        NativePool<uint> pool,
        NativeBuilderBenchmarkOptions options)
    {
        NativeBuilderVoxelPacket packet = CreateNativePacket(
            pool,
            options);
        try
        {
            return packet.CopyExactOutput();
        }
        finally
        {
            packet.Dispose();
        }
    }

    private static async Task<NativeBuilderBatchResult>
        RunManagedBatchAsync(
            NativeBuilderBenchmarkOptions options,
            int iterations)
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
                in channel.Reader.ReadAllAsync())
            {
                checksum = unchecked(checksum + packet.Consume());
                packet.Dispose();
            }

            return checksum;
        });
        await ready.Task;
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            for (int iteration = 0;
                iteration < iterations;
                iteration++)
            {
                await channel.Writer.WriteAsync(
                    CreateManagedPacket(options));
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        long checksum = await consumer;
        clock.Stop();
        return new NativeBuilderBatchResult(
            checksum,
            clock.Elapsed.TotalMilliseconds);
    }

    private static async Task<NativeBuilderBatchResult>
        RunNativeBatchAsync(
            NativePool<uint> pool,
            NativeBuilderBenchmarkOptions options,
            int iterations)
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
                in channel.Reader.ReadAllAsync())
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
        await ready.Task;
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            for (int iteration = 0;
                iteration < iterations;
                iteration++)
            {
                NativeBuilderVoxelPacket? packet =
                    CreateNativePacket(pool, options);
                try
                {
                    await channel.Writer.WriteAsync(packet);
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
        }

        long checksum = await consumer;
        clock.Stop();
        return new NativeBuilderBatchResult(
            checksum,
            clock.Elapsed.TotalMilliseconds);
    }

    private static ManagedBuilderVoxelPacket CreateManagedPacket(
        NativeBuilderBenchmarkOptions options) =>
        new(BuildManagedOutput(options));

    private static NativeBuilderVoxelPacket CreateNativePacket(
        NativePool<uint> pool,
        NativeBuilderBenchmarkOptions options)
    {
        using NativeBuilder<uint> opaque = pool.CreateBuilder(
            preLease: options.PreLease);
        using NativeBuilder<uint> transparent = pool.CreateBuilder(
            preLease: options.PreLease);
        (int opaqueCount, int transparentCount) =
            GetOutputCounts(options.ElementCount);
        AppendToBuilder(
            opaque,
            opaqueCount,
            options,
            transparentOutput: false);
        AppendToBuilder(
            transparent,
            transparentCount,
            options,
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
        NativeBuilderBenchmarkOptions options)
    {
        long totalStart = Stopwatch.GetTimestamp();
        long phaseStart = Stopwatch.GetTimestamp();
        (int opaqueCount, int transparentCount) =
            GetOutputCounts(options.ElementCount);
        List<uint> opaque = new(options.PreLease);
        List<uint> transparent = new(options.PreLease);
        Channel<ManagedBuilderVoxelPacket> channel =
            Channel.CreateBounded<ManagedBuilderVoxelPacket>(1);
        double allocation = ElapsedMilliseconds(phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
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
        double initialization = ElapsedMilliseconds(phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        ManagedBuilderVoxelPacket packet = new(
            new NativeBuilderExactOutput(
                opaque.ToArray(),
                transparent.ToArray()));
        double publication = ElapsedMilliseconds(phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        if (!channel.Writer.TryWrite(packet)
            || !channel.Reader.TryRead(out ManagedBuilderVoxelPacket? received))
        {
            packet.Dispose();
            throw new InvalidOperationException(
                "The managed builder phase handoff failed.");
        }

        double handoff = ElapsedMilliseconds(phaseStart);
        phaseStart = Stopwatch.GetTimestamp();
        long checksum = received.Consume();
        double access = ElapsedMilliseconds(phaseStart);
        phaseStart = Stopwatch.GetTimestamp();
        received.Dispose();
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
        NativePool<uint> pool,
        NativeBuilderBenchmarkOptions options)
    {
        long totalStart = Stopwatch.GetTimestamp();
        long phaseStart = Stopwatch.GetTimestamp();
        using NativeBuilder<uint> opaque = pool.CreateBuilder(
            preLease: options.PreLease);
        using NativeBuilder<uint> transparent = pool.CreateBuilder(
            preLease: options.PreLease);
        Channel<NativeBuilderVoxelPacket> channel =
            Channel.CreateBounded<NativeBuilderVoxelPacket>(1);
        double allocation = ElapsedMilliseconds(phaseStart);

        (int opaqueCount, int transparentCount) =
            GetOutputCounts(options.ElementCount);
        phaseStart = Stopwatch.GetTimestamp();
        AppendToBuilder(
            opaque,
            opaqueCount,
            options,
            transparentOutput: false);
        AppendToBuilder(
            transparent,
            transparentCount,
            options,
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
            || managed.PreLease != options.PreLease
            || native.PreLease != options.PreLease
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
            options.PreLease);
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

    private sealed class ManagedBuilderVoxelPacket : IDisposable
    {
        private readonly byte[] _opaqueUpload;
        private readonly byte[] _transparentUpload;

        internal ManagedBuilderVoxelPacket(
            NativeBuilderExactOutput output)
        {
            _opaqueUpload = ToUpload(output.Opaque);
            _transparentUpload = ToUpload(output.Transparent);
        }

        internal long Consume() => ConsumeUploads(
            _opaqueUpload,
            _transparentUpload);

        public void Dispose()
        {
            GC.KeepAlive(this);
        }

        private static byte[] ToUpload(uint[] source)
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
            _opaque!.Read(
                static view => view.AsSpan().ToArray()),
            _transparent!.Read(
                static view => view.AsSpan().ToArray()));

        internal long Consume()
        {
            long opaque = _opaque!.Read(
                static view => ConsumeUpload(
                    MemoryMarshal.AsBytes(view.AsSpan())));
            long transparent = _transparent!.Read(
                static view => ConsumeUpload(
                    MemoryMarshal.AsBytes(view.AsSpan())));
            return unchecked(
                opaque
                + RotateLeft(transparent, 17)
                + _opaque.Length * sizeof(uint)
                + ((long)_transparent.Length * sizeof(uint) << 32));
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

    private readonly record struct NativeBuilderBatchResult(
        long Checksum,
        double ElapsedMilliseconds);
}

internal sealed record NativeBuilderExactOutput(
    uint[] Opaque,
    uint[] Transparent);
