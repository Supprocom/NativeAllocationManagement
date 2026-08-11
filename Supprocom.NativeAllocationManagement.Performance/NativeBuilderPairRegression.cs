using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class NativeBuilderPairRegression
{
    internal const int OpaqueWords = 33_657_622;
    internal const int TransparentWords = 14_800;
    internal const int ChunkCount = 1_179;
    internal const int SampleCount = 6;
    private const uint OpaqueSalt = 0x6A09E667U;
    private const uint TransparentSalt = 0xBB67AE85U;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    internal static async Task<int> RunCommandAsync(string[] args)
    {
        if (args[0] == "--native-builder-pair-worker")
        {
            CompositeBuilderImplementation implementation = Enum.Parse<
                CompositeBuilderImplementation>(
                ReadRequiredOption(args, "--implementation"),
                ignoreCase: true);
            CompositeBuilderWorkerEvidence evidence = RunWorker(
                implementation);
            Console.WriteLine(JsonSerializer.Serialize(evidence));
            return evidence.ExactParity
                && evidence.FailureCleanupPassed
                && evidence.CancellationCleanupPassed
                && evidence.ExactlyOnceCleanupPassed
                && evidence.NativeRetainedBytesAfter == 0
                && evidence.TieredCompilationDisabled
                && evidence.TieredPgoDisabled
                    ? 0
                    : 3;
        }

        CompositeBuilderReport report = await RunPairedAsync();
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

    internal static async Task<CompositeBuilderReport> RunPairedAsync()
    {
        CompositeBuilderPairEvidence[] pairs =
            new CompositeBuilderPairEvidence[SampleCount];
        Stopwatch totalClock = Stopwatch.StartNew();
        for (int sampleIndex = 0;
            sampleIndex < SampleCount;
            sampleIndex++)
        {
            CompositeBuilderImplementation first =
                GetFirstImplementation(sampleIndex);
            CompositeBuilderWorkerEvidence firstEvidence =
                await RunIsolatedWorkerAsync(first);
            CompositeBuilderImplementation second = first
                == CompositeBuilderImplementation.ManagedStaging
                    ? CompositeBuilderImplementation.CompositeBorrow
                    : CompositeBuilderImplementation.ManagedStaging;
            CompositeBuilderWorkerEvidence secondEvidence =
                await RunIsolatedWorkerAsync(second);
            CompositeBuilderWorkerEvidence managed = first
                == CompositeBuilderImplementation.ManagedStaging
                    ? firstEvidence
                    : secondEvidence;
            CompositeBuilderWorkerEvidence composite = first
                == CompositeBuilderImplementation.CompositeBorrow
                    ? firstEvidence
                    : secondEvidence;
            ValidatePair(managed, composite);
            pairs[sampleIndex] = new CompositeBuilderPairEvidence(
                sampleIndex,
                first,
                managed,
                composite,
                managed.ElapsedMilliseconds
                    / composite.ElapsedMilliseconds);
        }

        totalClock.Stop();
        double[] speedups = pairs
            .Select(static pair => pair.ManagedToCompositeSpeedup)
            .ToArray();
        double mean = speedups.Average();
        double aggregate = pairs.Sum(static pair =>
                pair.ManagedStaging.ElapsedMilliseconds)
            / pairs.Sum(static pair =>
                pair.CompositeBorrow.ElapsedMilliseconds);
        double lower =
            PairedBenchmarkStatistics.ConfidenceLower95(speedups);
        bool parity = pairs.All(static pair =>
            pair.ManagedStaging.ExactParity
            && pair.CompositeBorrow.ExactParity
            && pair.ManagedStaging.OutputSha256
                == pair.CompositeBorrow.OutputSha256);
        bool balanced = pairs.Count(static pair =>
                pair.FirstImplementation
                    == CompositeBuilderImplementation.ManagedStaging)
            == SampleCount / 2
            && pairs.Count(static pair =>
                pair.FirstImplementation
                    == CompositeBuilderImplementation.CompositeBorrow)
                == SampleCount / 2;
        bool cleanup = pairs.All(static pair =>
            pair.ManagedStaging.FailureCleanupPassed
            && pair.ManagedStaging.CancellationCleanupPassed
            && pair.ManagedStaging.ExactlyOnceCleanupPassed
            && pair.CompositeBorrow.FailureCleanupPassed
            && pair.CompositeBorrow.CancellationCleanupPassed
            && pair.CompositeBorrow.ExactlyOnceCleanupPassed);
        bool runtimeConfiguration = pairs.All(static pair =>
            pair.ManagedStaging.TieredCompilationDisabled
            && pair.ManagedStaging.TieredPgoDisabled
            && pair.CompositeBorrow.TieredCompilationDisabled
            && pair.CompositeBorrow.TieredPgoDisabled);
        bool exactWorkload = pairs.All(static pair =>
            pair.ManagedStaging.OpaqueWords == OpaqueWords
            && pair.ManagedStaging.TransparentWords == TransparentWords
            && pair.ManagedStaging.ChunkCount == ChunkCount
            && pair.CompositeBorrow.OpaqueWords == OpaqueWords
            && pair.CompositeBorrow.TransparentWords == TransparentWords
            && pair.CompositeBorrow.ChunkCount == ChunkCount);
        bool binaryIdentity = pairs.All(static pair =>
            pair.ManagedStaging.SourceCommit.Length == 40
            && pair.ManagedStaging.SourceCommit
                == pair.CompositeBorrow.SourceCommit);
        bool gatePassed = EvaluateGate(
            parity,
            balanced,
            cleanup,
            runtimeConfiguration,
            exactWorkload,
            binaryIdentity,
            mean,
            aggregate,
            lower);
        return new CompositeBuilderReport(
            pairs[0].ManagedStaging.SourceCommit,
            OpaqueWords,
            TransparentWords,
            ChunkCount,
            SampleCount,
            pairs,
            pairs.Average(static pair =>
                pair.ManagedStaging.ElapsedMilliseconds),
            pairs.Average(static pair =>
                pair.CompositeBorrow.ElapsedMilliseconds),
            mean,
            aggregate,
            lower,
            pairs.Average(static pair =>
                (double)pair.ManagedStaging.ManagedAllocatedBytes),
            pairs.Average(static pair =>
                (double)pair.CompositeBorrow.ManagedAllocatedBytes),
            pairs.Max(static pair =>
                pair.ManagedStaging.PeakWorkingSetBytes),
            pairs.Max(static pair =>
                pair.CompositeBorrow.PeakWorkingSetBytes),
            parity,
            balanced,
            cleanup,
            runtimeConfiguration,
            exactWorkload,
            binaryIdentity,
            gatePassed,
            totalClock.Elapsed.TotalMilliseconds,
            DateTimeOffset.UtcNow);
    }

    internal static CompositeBuilderWorkerEvidence RunWorker(
        CompositeBuilderImplementation implementation)
    {
        RunExecution(
            implementation,
            opaqueWords: 65_536,
            transparentWords: 256,
            chunkCount: 8);
        bool failureCleanup = ProbeFailureCleanup();
        bool cancellationCleanup = ProbeCancellationCleanup();

        NativeMemoryTestHooks.Reset();
        long allocatedBefore = GC.GetTotalAllocatedBytes(
            precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        CompositeBuilderExecution execution = RunExecution(
            implementation,
            OpaqueWords,
            TransparentWords,
            ChunkCount);
        long allocated = GC.GetTotalAllocatedBytes(
            precise: true) - allocatedBefore;
        process.Refresh();
        NativeMemoryTestMetrics metrics =
            NativeMemoryTestHooks.Snapshot();
        bool exactlyOnce = metrics.AllocationCount
                == metrics.FreeCount
            && metrics.OutstandingNativeBytes == 0;
        return new CompositeBuilderWorkerEvidence(
            implementation,
            ReadSourceCommit(),
            OpaqueWords,
            TransparentWords,
            ChunkCount,
            execution.CallbackCount,
            execution.ElapsedMilliseconds,
            execution.AllocationMilliseconds,
            execution.InitializationMilliseconds,
            execution.AppendMilliseconds,
            execution.CompletionMilliseconds,
            execution.TransferMilliseconds,
            execution.DisposalMilliseconds,
            allocated,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            workingSetBefore,
            process.WorkingSet64,
            process.PeakWorkingSet64,
            metrics.AllocationCount,
            metrics.FreeCount,
            Math.Max(0, metrics.AllocationCount - 2),
            execution.CopiedBytes,
            metrics.OutstandingNativeBytes,
            execution.OutputSha256,
            execution.ExactParity,
            failureCleanup,
            cancellationCleanup,
            exactlyOnce,
            IsDisabled("DOTNET_TieredCompilation"),
            IsDisabled("DOTNET_TieredPGO"));
    }

    internal static CompositeBuilderImplementation GetFirstImplementation(
        int sampleIndex) =>
        (sampleIndex & 1) == 0
            ? CompositeBuilderImplementation.ManagedStaging
            : CompositeBuilderImplementation.CompositeBorrow;

    internal static bool EvaluateGate(
        bool exactParity,
        bool balancedOrder,
        bool cleanup,
        bool runtimeConfiguration,
        bool exactWorkload,
        bool binaryIdentity,
        double meanSpeedup,
        double aggregateSpeedup,
        double confidenceLower95) =>
        exactParity
        && balancedOrder
        && cleanup
        && runtimeConfiguration
        && exactWorkload
        && binaryIdentity
        && meanSpeedup > 1d
        && aggregateSpeedup > 1d
        && confidenceLower95 > 1d;

    private static CompositeBuilderExecution RunExecution(
        CompositeBuilderImplementation implementation,
        int opaqueWords,
        int transparentWords,
        int chunkCount)
    {
        NativeBuilder<uint>? opaqueBuilder = null;
        NativeBuilder<uint>? transparentBuilder = null;
        ArrayBufferWriter<uint>? opaqueManaged = null;
        ArrayBufferWriter<uint>? transparentManaged = null;
        NativeTransfer<uint>? opaqueSource = null;
        NativeTransfer<uint>? transparentSource = null;
        NativeTransfer<uint>? opaqueTransfer = null;
        NativeTransfer<uint>? transparentTransfer = null;
        Stopwatch phase = Stopwatch.StartNew();
        opaqueBuilder = new NativeBuilder<uint>(preLease: opaqueWords);
        transparentBuilder = new NativeBuilder<uint>(
            preLease: transparentWords);
        if (implementation == CompositeBuilderImplementation.ManagedStaging)
        {
            opaqueManaged = new ArrayBufferWriter<uint>(opaqueWords);
            transparentManaged = new ArrayBufferWriter<uint>(
                transparentWords);
        }

        phase.Stop();
        double allocation = phase.Elapsed.TotalMilliseconds;
        phase.Restart();
        int callbackCount;
        if (implementation == CompositeBuilderImplementation.CompositeBorrow)
        {
            opaqueBuilder.Borrow(
                transparentBuilder,
                (
                    scoped ref NativeBuilderBorrow<uint> opaque,
                    scoped ref NativeBuilderBorrow<uint> transparent) =>
                    GenerateNative(
                        ref opaque,
                        ref transparent,
                        opaqueWords,
                        transparentWords,
                        chunkCount));
            callbackCount = checked(chunkCount * 2);
        }
        else
        {
            GenerateManaged(
                opaqueManaged!,
                transparentManaged!,
                opaqueWords,
                transparentWords,
                chunkCount);
            callbackCount = 0;
        }

        phase.Stop();
        double initialization = phase.Elapsed.TotalMilliseconds;
        phase.Restart();
        long copiedBytes = 0;
        if (implementation == CompositeBuilderImplementation.ManagedStaging)
        {
            opaqueBuilder.Append(opaqueManaged!.WrittenSpan);
            transparentBuilder.Append(transparentManaged!.WrittenSpan);
            copiedBytes = checked(
                ((long)opaqueWords + transparentWords)
                * sizeof(uint));
        }

        phase.Stop();
        double append = phase.Elapsed.TotalMilliseconds;
        phase.Restart();
        opaqueSource = opaqueBuilder.Complete();
        transparentSource = transparentBuilder.Complete();
        phase.Stop();
        double completion = phase.Elapsed.TotalMilliseconds;
        phase.Restart();
        opaqueTransfer = NativeTransfer<uint>.Move(ref opaqueSource);
        transparentTransfer = NativeTransfer<uint>.Move(
            ref transparentSource);
        phase.Stop();
        double transfer = phase.Elapsed.TotalMilliseconds;
        string hash = ComputeHash(opaqueTransfer, transparentTransfer);
        bool parity = VerifyValues(
            opaqueTransfer,
            opaqueWords,
            OpaqueSalt)
            && VerifyValues(
                transparentTransfer,
                transparentWords,
                TransparentSalt);
        phase.Restart();
        opaqueTransfer.Dispose();
        transparentTransfer.Dispose();
        opaqueBuilder.Dispose();
        transparentBuilder.Dispose();
        phase.Stop();
        double disposal = phase.Elapsed.TotalMilliseconds;
        return new CompositeBuilderExecution(
            callbackCount,
            allocation
                + initialization
                + append
                + completion
                + transfer
                + disposal,
            allocation,
            initialization,
            append,
            completion,
            transfer,
            disposal,
            copiedBytes,
            hash,
            parity);
    }

    private static void GenerateNative(
        scoped ref NativeBuilderBorrow<uint> opaque,
        scoped ref NativeBuilderBorrow<uint> transparent,
        int opaqueWords,
        int transparentWords,
        int chunkCount)
    {
        int opaqueOffset = 0;
        int transparentOffset = 0;
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            int opaqueCount = PartitionLength(
                opaqueWords,
                chunkCount,
                chunk);
            int opaqueStart = opaqueOffset;
            opaque.Write(
                opaqueCount,
                writer =>
                {
                    Fill(
                        writer.AsSpan(),
                        opaqueStart,
                        OpaqueSalt);
                    writer.Commit(opaqueCount);
                });
            opaqueOffset += opaqueCount;

            int transparentCount = PartitionLength(
                transparentWords,
                chunkCount,
                chunk);
            int transparentStart = transparentOffset;
            transparent.Write(
                transparentCount,
                writer =>
                {
                    Fill(
                        writer.AsSpan(),
                        transparentStart,
                        TransparentSalt);
                    writer.Commit(transparentCount);
                });
            transparentOffset += transparentCount;
        }
    }

    private static void GenerateManaged(
        ArrayBufferWriter<uint> opaque,
        ArrayBufferWriter<uint> transparent,
        int opaqueWords,
        int transparentWords,
        int chunkCount)
    {
        int opaqueOffset = 0;
        int transparentOffset = 0;
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            int opaqueCount = PartitionLength(
                opaqueWords,
                chunkCount,
                chunk);
            Fill(
                opaque.GetSpan(opaqueCount).Slice(0, opaqueCount),
                opaqueOffset,
                OpaqueSalt);
            opaque.Advance(opaqueCount);
            opaqueOffset += opaqueCount;

            int transparentCount = PartitionLength(
                transparentWords,
                chunkCount,
                chunk);
            Fill(
                transparent.GetSpan(transparentCount)
                    .Slice(0, transparentCount),
                transparentOffset,
                TransparentSalt);
            transparent.Advance(transparentCount);
            transparentOffset += transparentCount;
        }
    }

    private static int PartitionLength(
        int total,
        int partitions,
        int index) =>
        total / partitions
        + (index < total % partitions ? 1 : 0);

    private static void Fill(
        Span<uint> destination,
        int start,
        uint salt)
    {
        for (int index = 0; index < destination.Length; index++)
        {
            destination[index] = ValueAt(start + index, salt);
        }
    }

    private static uint ValueAt(int index, uint salt) =>
        unchecked(((uint)index * 2_654_435_761U) + salt);

    private static bool VerifyValues(
        NativeTransfer<uint> transfer,
        int expectedLength,
        uint salt) =>
        transfer.Read(view =>
        {
            ReadOnlySpan<uint> values = view.AsSpan();
            if (values.Length != expectedLength)
            {
                return false;
            }

            for (int index = 0; index < values.Length; index++)
            {
                if (values[index] != ValueAt(index, salt))
                {
                    return false;
                }
            }

            return true;
        });

    private static string ComputeHash(
        NativeTransfer<uint> opaque,
        NativeTransfer<uint> transparent)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        opaque.Read(view =>
        {
            hash.AppendData(MemoryMarshal.AsBytes(view.AsSpan()));
            return 0;
        });
        transparent.Read(view =>
        {
            hash.AppendData(MemoryMarshal.AsBytes(view.AsSpan()));
            return 0;
        });
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool ProbeFailureCleanup()
    {
        NativeMemoryTestHooks.Reset();
        NativeBuilder<int> first = new(preLease: 1);
        NativeBuilder<int> second = new(preLease: 1);
        try
        {
            first.Borrow(
                second,
                static (
                    scoped ref NativeBuilderBorrow<int> firstBorrow,
                    scoped ref NativeBuilderBorrow<int> secondBorrow) =>
                {
                    firstBorrow.Append(1);
                    secondBorrow.Append(2);
                    throw new FormatException("Expected benchmark probe.");
                });
            return false;
        }
        catch (FormatException)
        {
            first.Dispose();
            second.Dispose();
            NativeMemoryTestMetrics metrics =
                NativeMemoryTestHooks.Snapshot();
            return metrics.AllocationCount == metrics.FreeCount
                && metrics.OutstandingNativeBytes == 0;
        }
    }

    private static bool ProbeCancellationCleanup()
    {
        NativeMemoryTestHooks.Reset();
        NativeBuilder<int> first = new(preLease: 1);
        NativeBuilder<int> second = new(preLease: 1);
        using CancellationTokenSource cancellation = new();
        try
        {
            first.Borrow(
                second,
                (
                    scoped ref NativeBuilderBorrow<int> firstBorrow,
                    scoped ref NativeBuilderBorrow<int> secondBorrow) =>
                {
                    firstBorrow.Append(1);
                    secondBorrow.Append(2);
                    cancellation.Cancel();
                },
                cancellation.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            first.Dispose();
            second.Dispose();
            NativeMemoryTestMetrics metrics =
                NativeMemoryTestHooks.Snapshot();
            return metrics.AllocationCount == metrics.FreeCount
                && metrics.OutstandingNativeBytes == 0;
        }
    }

    private static async Task<CompositeBuilderWorkerEvidence>
        RunIsolatedWorkerAsync(
            CompositeBuilderImplementation implementation)
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
            typeof(NativeBuilderPairRegression).Assembly.Location);
        process.StartInfo.ArgumentList.Add(
            "--native-builder-pair-worker");
        process.StartInfo.ArgumentList.Add("--implementation");
        process.StartInfo.ArgumentList.Add(implementation.ToString());
        process.StartInfo.Environment["DOTNET_TieredCompilation"] = "0";
        process.StartInfo.Environment["DOTNET_TieredPGO"] = "0";
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "The composite builder benchmark worker did not start.");
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
                "The composite builder benchmark worker exceeded 60 seconds.");
        }

        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The composite builder benchmark worker failed with exit code {process.ExitCode}: {error}");
        }

        return JsonSerializer.Deserialize<CompositeBuilderWorkerEvidence>(
            output.Trim())
            ?? throw new InvalidOperationException(
                "The composite builder benchmark worker returned no evidence.");
    }

    private static void ValidatePair(
        CompositeBuilderWorkerEvidence managed,
        CompositeBuilderWorkerEvidence composite)
    {
        if (managed.OpaqueWords != composite.OpaqueWords
            || managed.TransparentWords != composite.TransparentWords
            || managed.ChunkCount != composite.ChunkCount
            || managed.OutputSha256 != composite.OutputSha256)
        {
            throw new InvalidOperationException(
                "The composite builder benchmark pair used different work or output.");
        }
    }

    private static string ReadSourceCommit()
    {
        string informational = typeof(NativeBuilderPairRegression).Assembly
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
}

internal enum CompositeBuilderImplementation
{
    ManagedStaging,
    CompositeBorrow
}

internal sealed record CompositeBuilderExecution(
    int CallbackCount,
    double ElapsedMilliseconds,
    double AllocationMilliseconds,
    double InitializationMilliseconds,
    double AppendMilliseconds,
    double CompletionMilliseconds,
    double TransferMilliseconds,
    double DisposalMilliseconds,
    long CopiedBytes,
    string OutputSha256,
    bool ExactParity);

internal sealed record CompositeBuilderWorkerEvidence(
    CompositeBuilderImplementation Implementation,
    string SourceCommit,
    int OpaqueWords,
    int TransparentWords,
    int ChunkCount,
    int CallbackCount,
    double ElapsedMilliseconds,
    double AllocationMilliseconds,
    double InitializationMilliseconds,
    double AppendMilliseconds,
    double CompletionMilliseconds,
    double TransferMilliseconds,
    double DisposalMilliseconds,
    long ManagedAllocatedBytes,
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
    bool FailureCleanupPassed,
    bool CancellationCleanupPassed,
    bool ExactlyOnceCleanupPassed,
    bool TieredCompilationDisabled,
    bool TieredPgoDisabled);

internal sealed record CompositeBuilderPairEvidence(
    int SampleIndex,
    CompositeBuilderImplementation FirstImplementation,
    CompositeBuilderWorkerEvidence ManagedStaging,
    CompositeBuilderWorkerEvidence CompositeBorrow,
    double ManagedToCompositeSpeedup);

internal sealed record CompositeBuilderReport(
    string SourceCommit,
    int OpaqueWords,
    int TransparentWords,
    int ChunkCount,
    int SampleCount,
    CompositeBuilderPairEvidence[] Pairs,
    double ManagedMeanMilliseconds,
    double CompositeMeanMilliseconds,
    double MeanSpeedup,
    double AggregateSpeedup,
    double ConfidenceLower95,
    double ManagedMeanAllocatedBytes,
    double CompositeMeanAllocatedBytes,
    long ManagedPeakWorkingSetBytes,
    long CompositePeakWorkingSetBytes,
    bool ExactParity,
    bool BalancedOrder,
    bool CleanupPassed,
    bool RuntimeConfigurationPassed,
    bool ExactWorkloadPassed,
    bool BinaryIdentityPassed,
    bool GatePassed,
    double TotalElapsedMilliseconds,
    DateTimeOffset CreatedUtc);
