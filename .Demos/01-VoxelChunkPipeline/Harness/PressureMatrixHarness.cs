using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.Harness;

internal static class PressureMatrixHarness
{
    private const int WarmupProfilePercent = 1000;
    private const int StressProfilePercent = 10000;
    private const int StressProfileSamples = 1;
    private const int NonMeasuredOperationTimeoutSeconds = 60;
    private static readonly int[] ProfilePercents =
    [
        50,
        100,
        200,
        500,
        1000,
        10000
    ];

    internal static async Task<int> RunAsync(string[] args)
    {
        Options options = Options.Parse(args);
        DateTime startedUtc = DateTime.UtcNow;
        long endToEndStart = Stopwatch.GetTimestamp();
        string commit = (await RunCommandAsync(
            "git",
            ["-C", options.RepositoryRoot, "rev-parse", "HEAD"],
            TimeSpan.FromSeconds(10))).StandardOutput.Trim();
        string trackedChanges = (await RunCommandAsync(
            "git",
            [
                "-C",
                options.RepositoryRoot,
                "status",
                "--porcelain",
                "--untracked-files=no"
            ],
            TimeSpan.FromSeconds(10))).StandardOutput;
        if (!string.IsNullOrWhiteSpace(trackedChanges))
        {
            throw new InvalidOperationException(
                "The pressure matrix requires a clean tracked worktree.");
        }

        IReadOnlyList<PressureBinaryIdentity> binaryIdentities =
            CaptureBinaryIdentities(options.RepositoryRoot, commit);
        string imageId = (await RunCommandAsync(
            "docker",
            ["image", "inspect", "--format", "{{.Id}}", options.Image],
            TimeSpan.FromSeconds(20))).StandardOutput.Trim();
        string dockerInfo = (await RunCommandAsync(
            "docker",
            ["info", "--format", "{{json .}}"],
            TimeSpan.FromSeconds(20))).StandardOutput.Trim();

        List<string> commands = [];
        List<PressureProfilePair> profiles = new(
            options.ProfilePercents.Count);
        double totalMeasuredMilliseconds = 0;
        double totalInitializationMilliseconds = 0;
        int pairOrdinal = 0;
        for (int profileOrdinal = 0;
            profileOrdinal < options.ProfilePercents.Count;
            profileOrdinal++)
        {
            int percent = options.ProfilePercents[profileOrdinal];
            long target = checked(
                options.CgroupCapBytes * percent / 100);
            PressureProfileRequest request = new(
                percent,
                options.CgroupCapBytes,
                target,
                options.DeadlineMilliseconds,
                options.Seed,
                options.RetentionDepth,
                options.ProgressEveryChunks);
            DateTime initializationStartedUtc = DateTime.UtcNow;
            long initializationStart = Stopwatch.GetTimestamp();
            Task<DockerWorker> safeStart = DockerWorker.StartAsync(
                options,
                "SafeCSharp",
                options.SafeCpuSet,
                imageId,
                commands);
            Task<DockerWorker> namStart = DockerWorker.StartAsync(
                options,
                "NAM",
                options.NamCpuSet,
                imageId,
                commands);
            try
            {
                await Task.WhenAll(safeStart, namStart);
            }
            catch
            {
                if (safeStart.IsCompletedSuccessfully)
                {
                    await (await safeStart).DisposeAsync();
                }

                if (namStart.IsCompletedSuccessfully)
                {
                    await (await namStart).DisposeAsync();
                }

                throw;
            }
            await using DockerWorker safe = await safeStart;
            await using DockerWorker nam = await namStart;
            await Task.WhenAll(
                safe.WarmAsync(options),
                nam.WarmAsync(options));
            double initializationMilliseconds =
                Stopwatch.GetElapsedTime(
                    initializationStart).TotalMilliseconds;
            totalInitializationMilliseconds +=
                initializationMilliseconds;
            PressureProfileInitialization initialization = new(
                profileOrdinal,
                initializationStartedUtc,
                initializationMilliseconds,
                safe.ContainerName,
                nam.ContainerName,
                DockerWorker.WarmupPassCount,
                WarmupProfilePercent,
                checked(
                    options.CgroupCapBytes
                    * WarmupProfilePercent
                    / 100));
            int samplesInProfile =
                percent == StressProfilePercent
                    ? StressProfileSamples
                    : options.SamplesPerProfile;
            List<PressurePairedObservation> observations = new(
                samplesInProfile);
            for (int sampleIndex = 0;
                sampleIndex < samplesInProfile;
                sampleIndex++)
            {
                if (!safe.IsAlive || safe.RequiresRestart)
                {
                    await safe.RestartAsync(options, commands);
                    await safe.WarmAsync(options);
                }

                if (!nam.IsAlive || nam.RequiresRestart)
                {
                    await nam.RestartAsync(options, commands);
                    await nam.WarmAsync(options);
                }

                PressureImplementationObservation safeObservation;
                PressureImplementationObservation namObservation;
                bool safeFirst = ((pairOrdinal + profileOrdinal) & 1) == 0;
                if (safeFirst)
                {
                    safeObservation = await safe.RunProfileAsync(
                        request,
                        options);
                    namObservation = await nam.RunProfileAsync(
                        request,
                        options);
                }
                else
                {
                    namObservation = await nam.RunProfileAsync(
                        request,
                        options);
                    safeObservation = await safe.RunProfileAsync(
                        request,
                        options);
                }

                pairOrdinal++;
                observations.Add(new PressurePairedObservation(
                    sampleIndex,
                    safeObservation,
                    namObservation,
                    StructuralParity(
                        safeObservation.ChildResult,
                        namObservation.ChildResult)));
                totalMeasuredMilliseconds +=
                    safeObservation.ProfileElapsedMilliseconds
                        ?? safeObservation.ElapsedLowerBoundMilliseconds;
                totalMeasuredMilliseconds +=
                    namObservation.ProfileElapsedMilliseconds
                        ?? namObservation.ElapsedLowerBoundMilliseconds;
            }

            PressurePairedStatistics statistics = SummarizeProfile(
                percent,
                options,
                observations);
            profiles.Add(new PressureProfilePair(
                percent,
                options.CgroupCapBytes,
                target,
                initialization,
                observations,
                statistics));
        }

        int verificationPercent = options.ProfilePercents.Max();
        long verificationTarget = checked(
            options.CgroupCapBytes * verificationPercent / 100);
        PressureProfileRequest verificationRequest = new(
            verificationPercent,
            options.CgroupCapBytes,
            verificationTarget,
            options.DeadlineMilliseconds,
            options.Seed,
            options.RetentionDepth,
            int.MaxValue,
            ExecutionMode: PressureExecutionMode.Verification);
        DateTime verificationInitializationStartedUtc = DateTime.UtcNow;
        long verificationInitializationStart =
            Stopwatch.GetTimestamp();
        Task<DockerWorker> safeVerificationStart = DockerWorker.StartAsync(
            options,
            "SafeCSharp",
            options.SafeCpuSet,
            imageId,
            commands);
        Task<DockerWorker> namVerificationStart = DockerWorker.StartAsync(
            options,
            "NAM",
            options.NamCpuSet,
            imageId,
            commands);
        try
        {
            await Task.WhenAll(safeVerificationStart, namVerificationStart);
        }
        catch
        {
            if (safeVerificationStart.IsCompletedSuccessfully)
            {
                await (await safeVerificationStart).DisposeAsync();
            }

            if (namVerificationStart.IsCompletedSuccessfully)
            {
                await (await namVerificationStart).DisposeAsync();
            }

            throw;
        }
        await using DockerWorker safeVerificationWorker =
            await safeVerificationStart;
        await using DockerWorker namVerificationWorker =
            await namVerificationStart;
        await Task.WhenAll(
            safeVerificationWorker.WarmAsync(options),
            namVerificationWorker.WarmAsync(options));
        double verificationInitializationMilliseconds =
            Stopwatch.GetElapsedTime(
                verificationInitializationStart).TotalMilliseconds;
        PressureProfileInitialization verificationInitialization = new(
            options.ProfilePercents.Count,
            verificationInitializationStartedUtc,
            verificationInitializationMilliseconds,
            safeVerificationWorker.ContainerName,
            namVerificationWorker.ContainerName,
            DockerWorker.WarmupPassCount,
            WarmupProfilePercent,
            checked(
                options.CgroupCapBytes
                * WarmupProfilePercent
                / 100));
        Task<PressureImplementationObservation> safeVerificationTask =
            safeVerificationWorker.VerifyProfileAsync(
                verificationRequest,
                options);
        Task<PressureImplementationObservation> namVerificationTask =
            namVerificationWorker.VerifyProfileAsync(
                verificationRequest,
                options);
        await Task.WhenAll(safeVerificationTask, namVerificationTask);
        PressureImplementationObservation safeVerification =
            await safeVerificationTask;
        PressureImplementationObservation namVerification =
            await namVerificationTask;
        PressureVerificationPair verification = new(
            verificationPercent,
            verificationTarget,
            WarmupProfilePercent,
            checked(
                options.CgroupCapBytes
                * WarmupProfilePercent
                / 100),
            verificationInitialization,
            safeVerification,
            namVerification,
            ExactParity(
                safeVerification.ChildResult,
                namVerification.ChildResult));

        DateTime completedUtc = DateTime.UtcNow;
        bool profileIsolation = ProfileIsolationPassed(
            profiles,
            verification);
        bool measuredParity = profiles.All(
            profile => profile.Statistics.CorrectnessGatePassed);
        bool exactParity = measuredParity && verification.ExactParityPassed;
        bool performanceGate = profiles.All(
            profile => profile.Statistics.PerformanceGatePassed);
        PressureMatrixSummary summary = new(
            startedUtc,
            completedUtc,
            totalMeasuredMilliseconds,
            totalInitializationMilliseconds,
            Stopwatch.GetElapsedTime(endToEndStart).TotalMilliseconds,
            profiles.Count(ProfileCompleted),
            profiles.Count(profile => !ProfileCompleted(profile)),
            profileIsolation,
            exactParity,
            profiles.All(profile => profile.Statistics.DeadlineGatePassed),
            profiles.Where(profile => profile.ProfilePercent >= 200)
                .All(profile => profile.Statistics.PressureQualified),
            performanceGate,
            profileIsolation
                && exactParity
                && profiles.All(profile => profile.Statistics.GatePassed));
        Dictionary<string, string> hostConfiguration = new(StringComparer.Ordinal)
        {
            ["os"] = Environment.OSVersion.ToString(),
            ["framework"] = Environment.Version.ToString(),
            ["hostLogicalProcessors"] = Environment.ProcessorCount.ToString(
                CultureInfo.InvariantCulture),
            ["dockerInfo"] = dockerInfo,
            ["safeCpuSet"] = options.SafeCpuSet,
            ["namCpuSet"] = options.NamCpuSet,
            ["cpuQuota"] = "none",
            ["pairExecution"] =
                "sequential with per-profile alternating implementation order",
            ["profileInitialization"] =
                "fresh container pair, parallel worker start, and one fixed 1000-percent warmup per profile",
            ["crossProfileProcessState"] = "none",
            ["memorySwapPolicy"] = "memory-swap equals memory; swappiness 0",
            ["gcServer"] = "1",
            ["gcHeapCount"] = "4",
            ["pipelineWorkersPerImplementation"] = "1",
            ["safeTransientArrayPoolArraysPerBucket"] = "1",
            ["safeAdmission"] = "runtime tracked ArrayPool bucket budget",
            ["namAdmission"] = "runtime retained owner capacity",
            ["requestedRetentionDepth"] = options.RetentionDepth.ToString(
                CultureInfo.InvariantCulture),
            ["samplesPerProfile"] = options.SamplesPerProfile.ToString(
                CultureInfo.InvariantCulture),
            ["stressProfileSamples"] =
                $"{StressProfilePercent} percent uses "
                + $"{StressProfileSamples} complete sample",
            ["measurementEvidence"] =
                "predeclared plan and terminal completion without per-chunk evidence",
            ["measurementBoundary"] =
                "host starts before BeginProcessing and stops at ProcessingCompleted",
            ["cgroupPeakTelemetry"] =
                "one reset after warmup per worker, then cumulative peak reads",
            ["exactVerification"] =
                "maximum deterministic prefix after all measured profiles",
            ["gcHeapHardLimitPercent"] = options.GcHeapHardLimitPercent.ToString(
                CultureInfo.InvariantCulture),
            ["pressureQualification"] =
                "informational constrained-memory check; it requires the equal binary cap, no swap, and at least 2x cumulative demand; it does not require GC collections or a resident-peak threshold"
        };
        PressureMatrixReport report = new(
            commit,
            imageId,
            binaryIdentities,
            options.CgroupCapBytes,
            "binary bytes; 256 MiB = 268435456 bytes",
            options.DeadlineMilliseconds,
            options.RetentionDepth,
            options.ProgressEveryChunks,
            options.SamplesPerProfile,
            options.Seed,
            options.ProfilePercents,
            profiles,
            verification,
            summary,
            hostConfiguration,
            commands,
            [
                "The host samples Docker and cgroup metrics outside each worker.",
                "The child does not run a benchmark timer or scan allocator statistics during processing.",
                "Measured child results contain no per-chunk evidence.",
                "Each profile uses a new container pair and one fixed warmup before measurement.",
                "The 10000-percent stress profile uses one complete sample.",
                "Each measured profile materializes every output and completes its mapped handoff.",
                "One exact maximum-demand run follows measurement. It reads every output byte."
            ]);
        string? directory = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        JsonSerializerOptions outputOptions = new(VoxelJson.Options)
        {
            WriteIndented = true
        };
        await File.WriteAllTextAsync(
            options.OutputPath,
            JsonSerializer.Serialize(report, outputOptions));
        Console.WriteLine(JsonSerializer.Serialize(summary, VoxelJson.Options));
        Console.WriteLine(options.OutputPath);
        return options.Enforce && !summary.GatePassed ? 3 : 0;
    }

    private static bool ProfileCompleted(PressureProfilePair profile) =>
        profile.Observations.All(
            static observation =>
                observation.Safe.Outcome
                    == PressureProfileOutcome.Completed
                && observation.Nam.Outcome
                    == PressureProfileOutcome.Completed
                && observation.Safe.CorrectnessPassed
                && observation.Nam.CorrectnessPassed);

    private static bool ProfileIsolationPassed(
        IReadOnlyList<PressureProfilePair> profiles,
        PressureVerificationPair verification)
    {
        Dictionary<string, int> containerOwners =
            new(StringComparer.Ordinal);
        foreach (PressureProfilePair profile in profiles)
        {
            if (!RegisterContainer(
                    containerOwners,
                    profile.Initialization.SafeContainerName,
                    profile.ProfilePercent)
                || !RegisterContainer(
                    containerOwners,
                    profile.Initialization.NamContainerName,
                    profile.ProfilePercent))
            {
                return false;
            }

            foreach (PressurePairedObservation observation
                in profile.Observations)
            {
                if (!RegisterContainer(
                        containerOwners,
                        observation.Safe.Isolation.ContainerName,
                        profile.ProfilePercent)
                    || !RegisterContainer(
                        containerOwners,
                        observation.Nam.Isolation.ContainerName,
                        profile.ProfilePercent))
                {
                    return false;
                }
            }
        }

        int verificationOwner = -verification.ProfilePercent;
        return RegisterContainer(
                containerOwners,
                verification.Initialization.SafeContainerName,
                verificationOwner)
            && RegisterContainer(
                containerOwners,
                verification.Initialization.NamContainerName,
                verificationOwner)
            && RegisterContainer(
                containerOwners,
                verification.Safe.Isolation.ContainerName,
                verificationOwner)
            && RegisterContainer(
                containerOwners,
                verification.Nam.Isolation.ContainerName,
                verificationOwner);
    }

    private static bool RegisterContainer(
        IDictionary<string, int> containerOwners,
        string containerName,
        int owner)
    {
        if (string.IsNullOrWhiteSpace(containerName))
        {
            return false;
        }

        if (containerOwners.TryGetValue(
                containerName,
                out int existingOwner))
        {
            return existingOwner == owner;
        }

        containerOwners.Add(containerName, owner);
        return true;
    }

    private static PressurePairedStatistics SummarizeProfile(
        int percent,
        Options options,
        IReadOnlyList<PressurePairedObservation> observations)
    {
        if (observations.Count == 0)
        {
            throw new ArgumentException(
                "A pressure profile requires paired observations.",
                nameof(observations));
        }

        double safeElapsedTotal = observations.Sum(
            static observation =>
                observation.Safe.ProfileElapsedMilliseconds
                ?? observation.Safe.ElapsedLowerBoundMilliseconds);
        double namElapsedTotal = observations.Sum(
            static observation =>
                observation.Nam.ProfileElapsedMilliseconds
                ?? observation.Nam.ElapsedLowerBoundMilliseconds);
        bool namCompleted = observations.All(
                static observation =>
                    IsCompleted(observation.Nam))
            && namElapsedTotal <= options.DeadlineMilliseconds;
        bool safeCompleted = observations.All(
                static observation =>
                    IsCompleted(observation.Safe))
            && safeElapsedTotal <= options.DeadlineMilliseconds;
        bool parity = safeCompleted
            && namCompleted
            && observations.All(
                static observation =>
                    observation.StructuralParityPassed);
        bool completedPairOutputMismatch = observations.Any(
            static observation =>
                IsCompleted(observation.Safe)
                && IsCompleted(observation.Nam)
                && !observation.StructuralParityPassed);
        PressureOutcomeDecision outcomeDecision =
            PressureOutcomePolicy.Evaluate(
                percent,
                safeCompleted,
                namCompleted,
                parity,
                completedPairOutputMismatch,
                observations.Select(
                        static observation => observation.Safe.Outcome)
                    .ToArray(),
                observations.Select(
                        static observation => observation.Nam.Outcome)
                    .ToArray());
        bool decisiveNam = outcomeDecision.DecisiveNam;
        double[] speedups = observations
            .Where(
                static observation =>
                    IsCompleted(observation.Safe)
                    && IsCompleted(observation.Nam)
                    && observation.StructuralParityPassed)
            .Select(
                static observation =>
                    observation.Safe.ProfileElapsedMilliseconds!.Value
                    / observation.Nam.ProfileElapsedMilliseconds!.Value)
            .Where(double.IsFinite)
            .ToArray();
        int sampleCount = speedups.Length;
        double? mean = sampleCount == 0 ? null : speedups.Average();
        double? lower = null;
        double? upper = null;
        if (sampleCount >= 2 && mean.HasValue)
        {
            double deviation = StandardDeviation(speedups);
            double halfWidth = StudentT95Critical(sampleCount - 1)
                * deviation
                / Math.Sqrt(sampleCount);
            lower = mean.Value - halfWidth;
            upper = mean.Value + halfWidth;
        }

        double[] safeRates = observations
            .Where(
                static observation =>
                    IsCompleted(observation.Safe))
            .Select(
                static observation =>
                    MillisecondsPerGiB(observation.Safe))
            .ToArray();
        double[] namRates = observations
            .Where(
                static observation =>
                    IsCompleted(observation.Nam))
            .Select(
                static observation =>
                    MillisecondsPerGiB(observation.Nam))
            .ToArray();
        double? safeMeanRate = safeRates.Length == 0
            ? null
            : safeRates.Average();
        double? namMeanRate = namRates.Length == 0
            ? null
            : namRates.Average();
        double? safeP95 = Percentile(
            safeRates,
            0.95);
        double? safeP99 = Percentile(
            safeRates,
            0.99);
        double? namP95 = Percentile(
            namRates,
            0.95);
        double? namP99 = Percentile(
            namRates,
            0.99);
        bool pressureQualified = percent < 200
            || observations.All(
                observation =>
                    IsConstrainedMemoryObservation(
                        observation.Safe,
                        options)
                    && IsConstrainedMemoryObservation(
                        observation.Nam,
                        options));
        bool deadlineGate = outcomeDecision.DeadlineGatePassed;
        bool correctnessGate = outcomeDecision.CorrectnessGatePassed;
        bool performanceGate;
        string interpretation;
        if (decisiveNam)
        {
            performanceGate = true;
            interpretation =
                "NAM completed every sample. SafeCSharp recorded a hard failure.";
            mean = null;
            lower = null;
            upper = null;
        }
        else if (!safeCompleted || !namCompleted || !parity)
        {
            performanceGate = false;
            interpretation =
                "The paired profile did not complete its sample series with exact parity.";
        }
        else
        {
            if (percent <= 100)
            {
                performanceGate = mean > 1.00 && lower > 1.00;
                interpretation =
                    "Both control implementations completed. NAM must exceed "
                    + "SafeCSharp with a positive confidence lower bound.";
            }
            else
            {
                double requiredSpeedup = RequiredSpeedup(percent);
                bool confidenceGate =
                    percent == StressProfilePercent
                        ? sampleCount == StressProfileSamples
                        : lower > 1.00;
                performanceGate = mean >= requiredSpeedup
                    && confidenceGate;
                interpretation = percent == StressProfilePercent
                    ? $"Both implementations completed the stress profile. "
                        + $"This profile requires {requiredSpeedup:F2}x "
                        + "speedup from one complete sample."
                    : $"Both implementations completed. This profile requires "
                        + $"{requiredSpeedup:F2}x mean speedup and a positive "
                        + "confidence lower bound.";
            }
        }

        bool gate = deadlineGate
            && correctnessGate
            && performanceGate;
        return new PressurePairedStatistics(
            sampleCount,
            mean,
            lower,
            upper,
            safeMeanRate,
            namMeanRate,
            safeP95,
            safeP99,
            namP95,
            namP99,
            pressureQualified,
            deadlineGate,
            correctnessGate,
            performanceGate,
            gate,
            interpretation);
    }

    private static double MillisecondsPerGiB(
        PressureImplementationObservation observation) =>
        observation.ProfileElapsedMilliseconds!.Value
        / (observation.RealizedCumulativeDemandBytes
            / (double)(1L << 30));

    private static bool IsConstrainedMemoryObservation(
        PressureImplementationObservation observation,
        Options options) =>
        observation.CgroupCapBytes == options.CgroupCapBytes
        && observation.Isolation.MemoryLimitBytes == options.CgroupCapBytes
        && observation.Isolation.MemorySwapLimitBytes == options.CgroupCapBytes
        && observation.Isolation.MemorySwappiness == 0
        && observation.RequestedCumulativeDemandBytes
            >= checked(options.CgroupCapBytes * 2);

    private static bool IsCompleted(
        PressureImplementationObservation observation) =>
        observation.Outcome == PressureProfileOutcome.Completed
        && observation.CorrectnessPassed
        && observation.ProfileElapsedMilliseconds is > 0;

    private static bool ExactParity(
        PressureProfileResult? safe,
        PressureProfileResult? nam)
    {
        if (safe is not { } left
            || nam is not { } right)
        {
            return false;
        }

        return CompletionParity(left, right)
            && left.ExecutionMode
                == PressureExecutionMode.Verification
            && right.ExecutionMode
                == PressureExecutionMode.Verification
            && left.ChunkEvidence.Count == left.CompletedChunks
            && right.ChunkEvidence.Count == right.CompletedChunks
            && left.ChunkEvidence.Count != 0
            && left.ChunkEvidence.SequenceEqual(
                right.ChunkEvidence)
            && left.CanonicalEvidenceHash.Length != 0
            && string.Equals(
                left.CanonicalEvidenceHash,
                right.CanonicalEvidenceHash,
                StringComparison.Ordinal)
            && left.ChunkEvidence.All(
                static chunk => chunk.ExactVerificationPassed)
            && right.ChunkEvidence.All(
                static chunk => chunk.ExactVerificationPassed);
    }

    private static bool StructuralParity(
        PressureProfileResult? safe,
        PressureProfileResult? nam)
    {
        if (safe is not { } left || nam is not { } right)
        {
            return false;
        }

        return CompletionParity(left, right)
            && left.ExecutionMode
                == PressureExecutionMode.Measurement
            && right.ExecutionMode
                == PressureExecutionMode.Measurement
            && left.CanonicalEvidenceHash.Length == 0
            && right.CanonicalEvidenceHash.Length == 0
            && left.ChunkEvidence.Count == 0
            && right.ChunkEvidence.Count == 0;
    }

    private static bool CompletionParity(
        PressureProfileResult left,
        PressureProfileResult right) =>
        left.CorrectnessPassed
            && right.CorrectnessPassed
            && left.ProfilePercent == right.ProfilePercent
            && left.RequestedCumulativeDemandBytes == right.RequestedCumulativeDemandBytes
            && left.RealizedCumulativeDemandBytes == right.RealizedCumulativeDemandBytes
            && left.DemandOvershootBytes == right.DemandOvershootBytes
            && left.SourceInputBytes == right.SourceInputBytes
            && left.CompletedLogicalBytes == right.CompletedLogicalBytes
            && left.CompletedChunks == right.CompletedChunks
            && left.LastCompletedStage == right.LastCompletedStage
            && left.LastCompletedChunkId == right.LastCompletedChunkId;

    private static double RequiredSpeedup(int percent) =>
        percent switch
        {
            200 => 1.75,
            >= 1000 => 2.00,
            _ => 1.75 + (percent - 200) / 800.0 * 0.25
        };

    private static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        double mean = values.Average();
        double sum = values.Sum(value => (value - mean) * (value - mean));
        return Math.Sqrt(sum / (values.Count - 1));
    }

    private static double StudentT95Critical(int degreesOfFreedom) => degreesOfFreedom switch
    {
        <= 0 => double.PositiveInfinity,
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
        <= 12 => 2.201,
        <= 15 => 2.131,
        <= 20 => 2.086,
        <= 30 => 2.042,
        <= 60 => 2.000,
        _ => 1.960
    };

    private static double? Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return null;
        }

        double[] sorted = [.. values.Order()];
        double position = (sorted.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static IReadOnlyList<PressureBinaryIdentity>
        CaptureBinaryIdentities(
            string repositoryRoot,
            string expectedCommit)
    {
        (string Component, string RelativePath)[] required =
        [
            (
                "Harness",
                ".Demos/01-VoxelChunkPipeline/Harness/bin/Release/"
                    + "net10.0/VoxelChunkPipeline.Harness.dll"),
            (
                "SafeCSharp",
                ".Demos/01-VoxelChunkPipeline/SafeCSharp/bin/Release/"
                    + "net10.0/linux-x64/publish/"
                    + "VoxelChunkPipeline.SafeCSharp.dll"),
            (
                "SafeSharedContract",
                ".Demos/01-VoxelChunkPipeline/SafeCSharp/bin/Release/"
                    + "net10.0/linux-x64/publish/"
                    + "VoxelChunkPipeline.SharedContract.dll"),
            (
                "NAM",
                ".Demos/01-VoxelChunkPipeline/NAM/bin/Release/"
                    + "net10.0/linux-x64/publish/"
                    + "VoxelChunkPipeline.NAM.dll"),
            (
                "NamSharedContract",
                ".Demos/01-VoxelChunkPipeline/NAM/bin/Release/"
                    + "net10.0/linux-x64/publish/"
                    + "VoxelChunkPipeline.SharedContract.dll"),
            (
                "NativeAllocationManagement",
                ".Demos/01-VoxelChunkPipeline/NAM/bin/Release/"
                    + "net10.0/linux-x64/publish/"
                    + "Supprocom.NativeAllocationManagement.dll")
        ];
        List<PressureBinaryIdentity> identities = new(required.Length);
        foreach ((string component, string relativePath) in required)
        {
            string path = Path.Combine(
                repositoryRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"The required {component} binary does not exist.",
                    path);
            }

            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            string informationalVersion = version.ProductVersion
                ?? string.Empty;
            int separator = informationalVersion.LastIndexOf('+');
            string informationalCommit = separator >= 0
                ? informationalVersion[(separator + 1)..]
                : string.Empty;
            if (!string.Equals(
                    informationalCommit,
                    expectedCommit,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The {component} binary reports commit "
                    + $"'{informationalCommit}', but HEAD is "
                    + $"'{expectedCommit}'.");
            }

            using FileStream stream = File.OpenRead(path);
            identities.Add(new PressureBinaryIdentity(
                component,
                relativePath,
                Convert.ToHexString(SHA256.HashData(stream)),
                informationalVersion,
                informationalCommit));
        }

        PressureBinaryIdentity safeContract = identities.Single(
            static identity =>
                identity.Component == "SafeSharedContract");
        PressureBinaryIdentity namContract = identities.Single(
            static identity =>
                identity.Component == "NamSharedContract");
        if (!string.Equals(
                safeContract.Sha256,
                namContract.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Safe and NAM workers contain different SharedContract binaries.");
        }

        return identities;
    }

    private static async Task<CommandResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        ProcessStartInfo start = new(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource cancellation = new(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"{fileName} {string.Join(' ', arguments)} exceeded {timeout}.");
        }

        string standardOutput = await stdout;
        string standardError = await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} exited {process.ExitCode}: {standardError}");
        }

        return new CommandResult(process.ExitCode, standardOutput, standardError);
    }

    private sealed class DockerWorker : IAsyncDisposable
    {
        internal const int WarmupPassCount = 1;
        private readonly object _sampleGate = new();
        private readonly List<RawHostSample> _samples = [];
        private readonly string _implementation;
        private readonly string _cpuSet;
        private readonly string _imageId;
        private Process? _process;
        private Process? _statsProcess;
        private CancellationTokenSource? _statsCancellation;
        private Task? _statsReader;
        private Task<string>? _statsErrorReader;
        private Task<string>? _stderrReader;
        private int _restart;
        private bool _peakResetAttempted;

        private DockerWorker(string implementation, string cpuSet, string imageId)
        {
            _implementation = implementation;
            _cpuSet = cpuSet;
            _imageId = imageId;
        }

        internal bool IsAlive => _process is { HasExited: false };

        internal bool RequiresRestart { get; private set; }

        internal string ContainerName { get; private set; } = string.Empty;

        internal PressureRuntimeSnapshot StartupRuntime { get; private set; }

        internal PressureEffectiveIsolation Isolation { get; private set; }

        internal static async Task<DockerWorker> StartAsync(
            Options options,
            string implementation,
            string cpuSet,
            string imageId,
            List<string> commands)
        {
            DockerWorker worker = new(implementation, cpuSet, imageId);
            await worker.StartProcessAsync(options, commands);
            return worker;
        }

        internal async Task RestartAsync(Options options, List<string> commands)
        {
            await StopAsync();
            _restart++;
            RequiresRestart = false;
            await StartProcessAsync(options, commands);
        }

        internal async Task WarmAsync(Options options)
        {
            int warmupPercent = WarmupProfilePercent;
            long warmupDemand = checked(
                options.CgroupCapBytes * warmupPercent / 100);
            PressureProfileRequest request = new(
                warmupPercent,
                options.CgroupCapBytes,
                warmupDemand,
                20_000,
                options.Seed,
                options.RetentionDepth,
                int.MaxValue,
                Warmup: true);
            for (int pass = 0; pass < WarmupPassCount; pass++)
            {
                PressureImplementationObservation observation =
                    await RunProfileCoreAsync(
                        request,
                        options,
                        PressureCommandKind.Warmup,
                        enforceDeadline: false,
                        collectTelemetry: false);
                if (observation.Outcome
                        != PressureProfileOutcome.Completed
                    || !observation.CorrectnessPassed)
                {
                    throw new InvalidOperationException(
                        $"{_implementation} warmup failed as "
                        + $"{observation.Outcome}: "
                        + observation.ExceptionMessage);
                }
            }
        }

        internal Task<PressureImplementationObservation> RunProfileAsync(
            PressureProfileRequest request,
            Options options) =>
            RunProfileCoreAsync(
                request,
                options,
                PressureCommandKind.RunProfile,
                enforceDeadline: true,
                collectTelemetry: true);

        internal Task<PressureImplementationObservation> VerifyProfileAsync(
            PressureProfileRequest request,
            Options options) =>
            RunProfileCoreAsync(
                request,
                options,
                PressureCommandKind.VerifyProfile,
                enforceDeadline: false,
                collectTelemetry: true);

        private async Task StartProcessAsync(Options options, List<string> commands)
        {
            _peakResetAttempted = false;
            string suffix = _restart == 0 ? "initial" : $"restart-{_restart}";
            ContainerName = $"nam-voxel-{_implementation.ToLowerInvariant()}-{suffix}-{Guid.NewGuid():N}";
            string assembly = _implementation == "NAM"
                ? "/workspace/.Demos/01-VoxelChunkPipeline/NAM/bin/Release/net10.0/linux-x64/publish/VoxelChunkPipeline.NAM.dll"
                : "/workspace/.Demos/01-VoxelChunkPipeline/SafeCSharp/bin/Release/net10.0/linux-x64/publish/VoxelChunkPipeline.SafeCSharp.dll";
            string[] arguments =
            [
                "run",
                "--rm",
                "-i",
                "--name",
                ContainerName,
                "--memory",
                options.CgroupCapBytes.ToString(CultureInfo.InvariantCulture),
                "--memory-swap",
                options.CgroupCapBytes.ToString(CultureInfo.InvariantCulture),
                "--memory-swappiness",
                "0",
                "--cpuset-cpus",
                _cpuSet,
                "--pids-limit",
                options.PidsLimit.ToString(CultureInfo.InvariantCulture),
                "--env",
                "DOTNET_gcServer=1",
                "--env",
                "DOTNET_GCHeapCount=4",
                "--env",
                $"DOTNET_GCHeapHardLimitPercent={options.GcHeapHardLimitPercent:X}",
                "--volume",
                $"{options.RepositoryRoot}:/workspace:ro",
                "--workdir",
                "/workspace",
                options.Image,
                assembly,
                "--server"
            ];
            lock (commands)
            {
                commands.Add($"docker {string.Join(' ', arguments)}");
            }
            ProcessStartInfo start = new("docker")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            _process = Process.Start(start)
                ?? throw new InvalidOperationException($"Could not start {_implementation}.");
            _stderrReader = _process.StandardError.ReadToEndAsync();
            PressureEnvelope startup = await ReadEnvelopeAsync(TimeSpan.FromSeconds(20));
            if (startup.Kind != PressureEnvelopeKind.Ready || startup.Runtime is not { } runtime)
            {
                throw new InvalidDataException(
                    $"{_implementation} did not send its startup envelope.");
            }

            StartupRuntime = runtime;
            StartStats();
            Isolation = await ReadIsolationAsync(options);
        }

        private async Task<PressureImplementationObservation> RunProfileCoreAsync(
            PressureProfileRequest request,
            Options options,
            PressureCommandKind commandKind,
            bool enforceDeadline,
            bool collectTelemetry)
        {
            string requestId = $"{_implementation}-{request.ProfilePercent}-{Guid.NewGuid():N}";
            PressureCommand command = new(requestId, commandKind, request);
            string beginProcessing = JsonSerializer.Serialize(
                new PressureCommand(
                    requestId,
                    PressureCommandKind.BeginProcessing),
                VoxelJson.Options);
            (CgroupMemorySnapshot initialCgroup, bool peakReset) = collectTelemetry
                ? await PrepareCgroupProfileAsync()
                : (default, false);
            long sentTick = Stopwatch.GetTimestamp();
            await _process!.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(command, VoxelJson.Options));
            await _process.StandardInput.FlushAsync();

            long? startTick = null;
            long? completionTick = null;
            long resultTick = 0;
            PressureProfileResult? childResult = null;
            PressureProgress? lastProgress = null;
            List<(PressureProgress Progress, long Tick)> progress = [];
            PressureProfileOutcome outcome = PressureProfileOutcome.HarnessFailure;
            string? exceptionType = null;
            string? exceptionMessage = null;
            CgroupMemorySnapshot? terminalCgroup = null;
            IReadOnlyList<PressureHostSample>? preservedSamples = null;
            long? forcedObservationEnd = null;
            while (true)
            {
                TimeSpan timeout;
                if (completionTick.HasValue)
                {
                    timeout = TimeSpan.FromSeconds(5);
                }
                else if (startTick.HasValue && enforceDeadline)
                {
                    TimeSpan elapsed = Stopwatch.GetElapsedTime(startTick.Value);
                    timeout = TimeSpan.FromMilliseconds(request.DeadlineMilliseconds) - elapsed;
                    if (timeout <= TimeSpan.Zero)
                    {
                        outcome = PressureProfileOutcome.DeadlineExceeded;
                        exceptionType = typeof(TimeoutException).FullName;
                        exceptionMessage = "The external six-second processing deadline expired.";
                        break;
                    }
                }
                else
                {
                    timeout = TimeSpan.FromSeconds(
                        NonMeasuredOperationTimeoutSeconds);
                }

                PressureEnvelope envelope;
                try
                {
                    envelope = await ReadEnvelopeAsync(timeout);
                }
                catch (TimeoutException)
                {
                    outcome = completionTick.HasValue
                        ? PressureProfileOutcome.HarnessFailure
                        : startTick.HasValue && enforceDeadline
                            ? PressureProfileOutcome.DeadlineExceeded
                            : PressureProfileOutcome.HarnessFailure;
                    exceptionType = typeof(TimeoutException).FullName;
                    exceptionMessage = completionTick.HasValue
                        ? "The worker completed processing but did not transfer its result."
                        : startTick.HasValue && enforceDeadline
                            ? "The external six-second processing deadline expired."
                            : startTick.HasValue
                                ? "The non-measured worker operation exceeded its hard timeout."
                            : "The worker did not enter its processing boundary.";
                    break;
                }
                catch (EndOfStreamException exception)
                {
                    CgroupMemorySnapshot endedCgroup = await ReadCgroupAsync();
                    terminalCgroup = endedCgroup;
                    outcome = endedCgroup.OomKillEvents > 0
                        ? PressureProfileOutcome.OutOfMemory
                        : PressureProfileOutcome.Crash;
                    exceptionType = exception.GetType().FullName;
                    exceptionMessage = exception.Message;
                    break;
                }

                if (!string.Equals(envelope.RequestId, requestId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (envelope.Kind == PressureEnvelopeKind.Progress
                    && envelope.Progress is { } current)
                {
                    long tick = Stopwatch.GetTimestamp();
                    lastProgress = current;
                    if (current.Kind == PressureProgressKind.ProcessingReady)
                    {
                        startTick = tick;
                        await _process!.StandardInput.WriteLineAsync(
                            beginProcessing);
                        await _process.StandardInput.FlushAsync();
                    }
                    else if (startTick.HasValue)
                    {
                        progress.Add((current, tick));
                        if (current.Kind == PressureProgressKind.ProcessingCompleted)
                        {
                            completionTick = tick;
                        }
                    }

                    continue;
                }

                if (envelope.Kind == PressureEnvelopeKind.Result
                    && envelope.Result is { } result)
                {
                    resultTick = Stopwatch.GetTimestamp();
                    childResult = result;
                    outcome = result.Outcome;
                    exceptionType = result.ExceptionType;
                    exceptionMessage = result.ExceptionMessage;
                    break;
                }

                if (envelope.Kind == PressureEnvelopeKind.Failure)
                {
                    resultTick = Stopwatch.GetTimestamp();
                    outcome = PressureProfileOutcome.HarnessFailure;
                    exceptionType = envelope.ErrorType;
                    exceptionMessage = envelope.ErrorMessage;
                    break;
                }
            }

            if (outcome == PressureProfileOutcome.DeadlineExceeded)
            {
                completionTick = null;
                forcedObservationEnd = Stopwatch.GetTimestamp();
                if (collectTelemetry)
                {
                    terminalCgroup ??= await ReadCgroupAsync();
                    preservedSamples = SelectSamples(
                        startTick ?? sentTick,
                        forcedObservationEnd.Value);
                }
                await KillAsync();
            }
            else if (outcome == PressureProfileOutcome.HarnessFailure
                && completionTick.HasValue
                && childResult is null)
            {
                forcedObservationEnd = completionTick.Value;
                if (collectTelemetry)
                {
                    terminalCgroup ??= await ReadCgroupAsync();
                    preservedSamples = SelectSamples(
                        startTick ?? sentTick,
                        forcedObservationEnd.Value);
                }
                await KillAsync();
            }

            long observationEnd = completionTick
                ?? forcedObservationEnd
                ?? Stopwatch.GetTimestamp();
            double? elapsedMilliseconds = startTick.HasValue && completionTick.HasValue
                ? Stopwatch.GetElapsedTime(startTick.Value, completionTick.Value).TotalMilliseconds
                : null;
            if (elapsedMilliseconds > request.DeadlineMilliseconds && enforceDeadline)
            {
                outcome = PressureProfileOutcome.DeadlineExceeded;
                exceptionType = typeof(TimeoutException).FullName;
                exceptionMessage = "Processing completed after the external six-second deadline.";
            }

            CgroupMemorySnapshot finalCgroup = collectTelemetry
                ? terminalCgroup ?? await ReadCgroupAsync()
                : default;
            IReadOnlyList<PressureHostSample> samples = collectTelemetry
                ? preservedSamples
                    ?? SelectSamples(
                        startTick ?? sentTick,
                        observationEnd)
                : [];
            long externalPeak = Math.Max(
                initialCgroup.CurrentBytes,
                Math.Max(
                    finalCgroup.CurrentBytes,
                    samples.Count == 0
                        ? 0
                        : samples.Max(sample => sample.CgroupMemoryBytes)));
            if (peakReset
                || finalCgroup.PeakBytes > initialCgroup.PeakBytes)
            {
                externalPeak = Math.Max(
                    externalPeak,
                    finalCgroup.PeakBytes);
            }
            double cpuMean = samples.Count == 0
                ? 0
                : samples.Average(sample => sample.CpuPercent);
            double cpuPeak = samples.Count == 0
                ? 0
                : samples.Max(sample => sample.CpuPercent);
            PressureFailureAttribution attribution = outcome == PressureProfileOutcome.Completed
                ? PressureFailureAttribution.None
                : outcome == PressureProfileOutcome.HarnessFailure
                    ? PressureFailureAttribution.HarnessInfrastructure
                    : _implementation == "NAM"
                        ? PressureFailureAttribution.NAM
                        : PressureFailureAttribution.SafeCSharp;
            int completedChunks = childResult?.CompletedChunks
                ?? lastProgress?.CompletedChunks
                ?? 0;
            long completedBytes = childResult?.CompletedLogicalBytes
                ?? lastProgress?.CompletedLogicalBytes
                ?? 0;
            VoxelPipelineStage stage = childResult?.LastCompletedStage
                ?? lastProgress?.LastCompletedStage
                ?? VoxelPipelineStage.None;
            int lastChunk = childResult?.LastCompletedChunkId
                ?? lastProgress?.LastCompletedChunkId
                ?? -1;
            long managedSinceStart = childResult is { } completed
                ? Math.Max(
                    0,
                    completed.After.TotalAllocatedBytes - StartupRuntime.TotalAllocatedBytes)
                : 0;
            int gen2SinceStart = childResult is { } completedForGc
                ? Math.Max(
                    0,
                    completedForGc.After.Gen2Collections - StartupRuntime.Gen2Collections)
                : 0;
            double cpuSinceStart = childResult is { } completedForCpu
                ? Math.Max(
                    0,
                    completedForCpu.After.ProcessCpuMilliseconds
                        - StartupRuntime.ProcessCpuMilliseconds)
                : 0;
            IReadOnlyList<PressureHostProgress> hostProgress = startTick.HasValue
                ? progress.Select(item => new PressureHostProgress(
                    item.Progress,
                    Stopwatch.GetElapsedTime(startTick.Value, item.Tick).TotalMilliseconds))
                    .ToArray()
                : [];
            RequiresRestart = enforceDeadline
                && (outcome != PressureProfileOutcome.Completed
                    || childResult?.CorrectnessPassed != true);
            return new PressureImplementationObservation(
                _implementation,
                request.ProfilePercent,
                outcome,
                attribution,
                request.CgroupCapBytes,
                request.RequestedCumulativeDemandBytes,
                childResult?.RealizedCumulativeDemandBytes ?? completedBytes,
                request.DeadlineMilliseconds,
                elapsedMilliseconds,
                startTick.HasValue
                    ? Stopwatch.GetElapsedTime(startTick.Value, observationEnd).TotalMilliseconds
                    : Stopwatch.GetElapsedTime(sentTick, observationEnd).TotalMilliseconds,
                startTick.HasValue
                    ? Stopwatch.GetElapsedTime(sentTick, startTick.Value).TotalMilliseconds
                    : Stopwatch.GetElapsedTime(sentTick, observationEnd).TotalMilliseconds,
                completionTick.HasValue && resultTick != 0
                    ? Stopwatch.GetElapsedTime(completionTick.Value, resultTick).TotalMilliseconds
                    : 0,
                completedChunks,
                completedBytes,
                stage,
                lastChunk,
                childResult?.CorrectnessPassed == true
                    && outcome == PressureProfileOutcome.Completed,
                _process is { HasExited: true } ? _process.ExitCode : null,
                exceptionType,
                exceptionMessage,
                childResult,
                managedSinceStart,
                gen2SinceStart,
                cpuSinceStart,
                hostProgress,
                samples,
                initialCgroup,
                finalCgroup,
                peakReset,
                externalPeak,
                cpuMean,
                cpuPeak,
                Isolation);
        }

        private async Task<(CgroupMemorySnapshot Snapshot, bool PeakReset)>
            PrepareCgroupProfileAsync()
        {
            bool reset = false;
            if (IsAlive && !_peakResetAttempted)
            {
                _peakResetAttempted = true;
                try
                {
                    CommandResult result = await RunCommandAsync(
                        "docker",
                        [
                            "exec",
                            ContainerName,
                            "sh",
                            "-c",
                            "if echo 0 > /sys/fs/cgroup/memory.peak 2>/dev/null; then echo reset; else echo cumulative; fi"
                        ],
                        TimeSpan.FromSeconds(3));
                    reset = result.StandardOutput
                        .Contains("reset", StringComparison.Ordinal);
                }
                catch
                {
                }
            }

            return (await ReadCgroupAsync(), reset);
        }

        private async Task<PressureEnvelope> ReadEnvelopeAsync(TimeSpan timeout)
        {
            using CancellationTokenSource cancellation = new(timeout);
            string? line;
            try
            {
                line = await _process!.StandardOutput.ReadLineAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"{_implementation} did not produce a protocol envelope within {timeout}.");
            }

            if (line is null)
            {
                string stderr = _stderrReader is null ? string.Empty : await _stderrReader;
                throw new EndOfStreamException(
                    $"{_implementation} ended its protocol stream. {stderr}");
            }

            return JsonSerializer.Deserialize<PressureEnvelope>(line, VoxelJson.Options);
        }

        private void StartStats()
        {
            try
            {
                ProcessStartInfo start = new("docker")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add("stats");
                start.ArgumentList.Add("--format");
                start.ArgumentList.Add("{{json .}}");
                start.ArgumentList.Add(ContainerName);
                _statsProcess = Process.Start(start);
                if (_statsProcess is null)
                {
                    return;
                }

                _statsCancellation = new CancellationTokenSource();
                CancellationToken cancellation = _statsCancellation.Token;
                _statsErrorReader = _statsProcess.StandardError.ReadToEndAsync();
                _statsReader = Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            string? line = await _statsProcess.StandardOutput
                                .ReadLineAsync(cancellation);
                            if (line is null)
                            {
                                return;
                            }

                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                RecordStatsLine(line);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch
                    {
                    }
                });
            }
            catch
            {
                _statsProcess?.Dispose();
                _statsProcess = null;
            }
        }

        private void RecordStatsLine(string line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            long tick = Stopwatch.GetTimestamp();
            DateTime utc = DateTime.UtcNow;
            (long memory, long limit) = ParseUsage(
                root.GetProperty("MemUsage").GetString());
            double cpu = ParsePercent(root.GetProperty("CPUPerc").GetString());
            int pids = ParseInt(root.GetProperty("PIDs").GetString());
            (long networkInput, long networkOutput) = ParseUsage(
                root.GetProperty("NetIO").GetString());
            (long blockRead, long blockWrite) = ParseUsage(
                root.GetProperty("BlockIO").GetString());
            lock (_sampleGate)
            {
                _samples.Add(new RawHostSample(
                    tick,
                    utc,
                    memory,
                    limit,
                    cpu,
                    pids,
                    networkInput,
                    networkOutput,
                    blockRead,
                    blockWrite));
            }
        }

        private IReadOnlyList<PressureHostSample> SelectSamples(long startTick, long endTick)
        {
            lock (_sampleGate)
            {
                return _samples
                    .Where(sample => sample.Tick >= startTick && sample.Tick <= endTick)
                    .Select(sample => new PressureHostSample(
                        sample.Utc,
                        Stopwatch.GetElapsedTime(startTick, sample.Tick).TotalMilliseconds,
                        sample.MemoryBytes,
                        sample.MemoryLimitBytes,
                        sample.CpuPercent,
                        sample.Pids,
                        sample.NetworkInputBytes,
                        sample.NetworkOutputBytes,
                        sample.BlockReadBytes,
                        sample.BlockWriteBytes))
                    .ToArray();
            }
        }

        private async Task<PressureEffectiveIsolation> ReadIsolationAsync(Options options)
        {
            CommandResult inspect = await RunCommandAsync(
                "docker",
                ["inspect", "--format", "{{json .HostConfig}}", ContainerName],
                TimeSpan.FromSeconds(10));
            CommandResult effective = await RunCommandAsync(
                "docker",
                [
                    "exec",
                    ContainerName,
                    "sh",
                    "-c",
                    "cat /sys/fs/cgroup/cpuset.cpus.effective; cat /sys/fs/cgroup/cpu.max; cat /sys/fs/cgroup/memory.max; cat /sys/fs/cgroup/memory.swap.max"
                ],
                TimeSpan.FromSeconds(10));
            string[] lines = effective.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Dictionary<string, string> inspectValues = new(StringComparer.Ordinal)
            {
                ["hostConfig"] = inspect.StandardOutput.Trim(),
                ["effectiveMemoryMax"] = lines.ElementAtOrDefault(2) ?? string.Empty,
                ["effectiveMemorySwapMax"] = lines.ElementAtOrDefault(3) ?? string.Empty
            };
            return new PressureEffectiveIsolation(
                ContainerName,
                _imageId,
                options.CgroupCapBytes,
                options.CgroupCapBytes,
                0,
                _cpuSet,
                lines.ElementAtOrDefault(0) ?? string.Empty,
                lines.ElementAtOrDefault(1) ?? string.Empty,
                options.PidsLimit,
                StartupRuntime.ProcessorCount,
                StartupRuntime.GcConfiguration,
                inspectValues);
        }

        private async Task<CgroupMemorySnapshot> ReadCgroupAsync()
        {
            if (!IsAlive)
            {
                return default;
            }

            try
            {
                CommandResult result = await RunCommandAsync(
                    "docker",
                    [
                        "exec",
                        ContainerName,
                        "sh",
                        "-c",
                        "cat /sys/fs/cgroup/memory.max; cat /sys/fs/cgroup/memory.current; cat /sys/fs/cgroup/memory.peak; cat /sys/fs/cgroup/memory.events; cat /sys/fs/cgroup/memory.stat"
                    ],
                    TimeSpan.FromSeconds(3));
                string[] lines = result.StandardOutput
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                long limit = ParseLong(lines.ElementAtOrDefault(0));
                long current = ParseLong(lines.ElementAtOrDefault(1));
                long peak = ParseLong(lines.ElementAtOrDefault(2));
                long low = FindCounter(lines, "low");
                long high = FindCounter(lines, "high");
                long max = FindCounter(lines, "max");
                long oom = FindCounter(lines, "oom");
                long oomKill = FindCounter(lines, "oom_kill");
                long oomGroupKill = FindCounter(lines, "oom_group_kill");
                long anon = FindCounter(lines, "anon");
                long file = FindCounter(lines, "file");
                return new CgroupMemorySnapshot(
                    true,
                    limit,
                    current,
                    peak,
                    low,
                    high,
                    max,
                    oom,
                    oomKill,
                    oomGroupKill,
                    anon,
                    file);
            }
            catch
            {
                return default;
            }
        }

        private static long FindCounter(IEnumerable<string> lines, string name)
        {
            foreach (string line in lines)
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && parts[0] == name
                    && long.TryParse(
                        parts[1],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long value))
                {
                    return value;
                }
            }

            return 0;
        }

        private static (long First, long Second) ParseUsage(string? text)
        {
            string[] parts = (text ?? string.Empty).Split(
                '/',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return (
                ParseByteSize(parts.ElementAtOrDefault(0)),
                ParseByteSize(parts.ElementAtOrDefault(1)));
        }

        private static long ParseByteSize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            string value = text.Trim();
            int split = 0;
            while (split < value.Length
                && (char.IsDigit(value[split])
                    || value[split] is '.' or ','))
            {
                split++;
            }

            if (!double.TryParse(
                value[..split].Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double number))
            {
                return 0;
            }

            string unit = value[split..].Trim();
            double multiplier = unit switch
            {
                "B" or "" => 1,
                "kB" or "KB" => 1_000,
                "KiB" => 1 << 10,
                "MB" => 1_000_000,
                "MiB" => 1 << 20,
                "GB" => 1_000_000_000,
                "GiB" => 1L << 30,
                _ => 1
            };
            return checked((long)(number * multiplier));
        }

        private static double ParsePercent(string? text) =>
            double.TryParse(
                text?.Trim().TrimEnd('%'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value)
                ? value
                : 0;

        private static int ParseInt(string? text) =>
            int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value)
                ? value
                : 0;

        private static long ParseLong(string? text) =>
            long.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long value)
                ? value
                : 0;

        private async Task KillAsync()
        {
            if (string.IsNullOrEmpty(ContainerName))
            {
                return;
            }

            try
            {
                await RunCommandAsync(
                    "docker",
                    ["kill", ContainerName],
                    TimeSpan.FromSeconds(3));
            }
            catch
            {
            }

            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        private async Task StopAsync()
        {
            await StopStatsAsync();

            if (_process is { HasExited: false })
            {
                try
                {
                    PressureCommand shutdown = new(
                        $"shutdown-{Guid.NewGuid():N}",
                        PressureCommandKind.Shutdown);
                    await _process.StandardInput.WriteLineAsync(
                        JsonSerializer.Serialize(shutdown, VoxelJson.Options));
                    await _process.StandardInput.FlushAsync();
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
                    await _process.WaitForExitAsync(timeout.Token);
                }
                catch
                {
                    await KillAsync();
                }
            }

            _process?.Dispose();
            _process = null;
            lock (_sampleGate)
            {
                _samples.Clear();
            }
        }

        private async Task StopStatsAsync()
        {
            _statsCancellation?.Cancel();
            if (_statsProcess is { HasExited: false })
            {
                try
                {
                    _statsProcess.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }

            if (_statsReader is not null)
            {
                try
                {
                    await _statsReader.WaitAsync(TimeSpan.FromMilliseconds(250));
                }
                catch
                {
                }
            }

            if (_statsErrorReader is not null)
            {
                try
                {
                    await _statsErrorReader.WaitAsync(TimeSpan.FromMilliseconds(250));
                }
                catch
                {
                }
            }

            _statsCancellation?.Dispose();
            _statsCancellation = null;
            _statsReader = null;
            _statsErrorReader = null;
            _statsProcess?.Dispose();
            _statsProcess = null;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }

    private sealed record Options(
        string RepositoryRoot,
        string Image,
        string OutputPath,
        string SafeCpuSet,
        string NamCpuSet,
        long CgroupCapBytes,
        double DeadlineMilliseconds,
        int RetentionDepth,
        int ProgressEveryChunks,
        int Seed,
        int PidsLimit,
        int GcHeapHardLimitPercent,
        int SamplesPerProfile,
        IReadOnlyList<int> ProfilePercents,
        bool Enforce)
    {
        internal static Options Parse(IReadOnlyList<string> args)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            bool enforce = false;
            for (int index = 0; index < args.Count; index++)
            {
                string argument = args[index];
                if (argument == "--pressure-matrix")
                {
                    continue;
                }

                if (argument == "--enforce")
                {
                    enforce = true;
                    continue;
                }

                if (!argument.StartsWith("--", StringComparison.Ordinal)
                    || index + 1 >= args.Count)
                {
                    throw new ArgumentException($"Invalid pressure harness argument '{argument}'.");
                }

                values[argument] = args[++index];
            }

            string repository = Required(values, "--repo");
            string image = Required(values, "--image");
            string output = Required(values, "--output");
            return new Options(
                Path.GetFullPath(repository),
                image,
                Path.GetFullPath(output),
                values.GetValueOrDefault("--safe-cpuset", "0-3"),
                values.GetValueOrDefault("--nam-cpuset", "4-7"),
                ParseLong(values.GetValueOrDefault("--cap-bytes", "268435456")),
                ParseDouble(values.GetValueOrDefault("--deadline-ms", "6000")),
                ParseInt(values.GetValueOrDefault(
                    "--retention",
                    PressureWorkContract.DefaultRetentionDepth.ToString(
                        CultureInfo.InvariantCulture))),
                ParseInt(values.GetValueOrDefault(
                    "--progress-every",
                    PressureWorkContract.DefaultProgressEveryChunks.ToString(
                        CultureInfo.InvariantCulture))),
                ParseInt(values.GetValueOrDefault("--seed", "17")),
                ParseInt(values.GetValueOrDefault("--pids-limit", "128")),
                ParseInt(values.GetValueOrDefault("--gc-hard-limit-percent", "90")),
                ParsePositiveInt(
                    values.GetValueOrDefault(
                        "--samples-per-profile",
                        "5"),
                    "--samples-per-profile"),
                ParseProfiles(values.GetValueOrDefault(
                    "--profiles",
                    string.Join(',', PressureMatrixHarness.ProfilePercents))),
                enforce);
        }

        private static string Required(
            IReadOnlyDictionary<string, string> values,
            string key) =>
            values.TryGetValue(key, out string? value)
                ? value
                : throw new ArgumentException($"Missing required argument {key}.");

        private static int ParseInt(string value) =>
            int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

        private static int ParsePositiveInt(
            string value,
            string name)
        {
            int result = ParseInt(value);
            if (result <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    name,
                    "The sample count must be positive.");
            }

            return result;
        }

        private static long ParseLong(string value) =>
            long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

        private static double ParseDouble(string value) =>
            double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

        private static IReadOnlyList<int> ParseProfiles(string value)
        {
            int[] profiles = value.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseInt)
                .ToArray();
            if (profiles.Length == 0
                || profiles.Any(profile => !PressureMatrixHarness.ProfilePercents.Contains(profile))
                || profiles.Distinct().Count() != profiles.Length)
            {
                throw new ArgumentException(
                    "Profiles must be a unique subset of 50,100,200,500,1000,10000.");
            }

            return profiles;
        }
    }

    private readonly record struct RawHostSample(
        long Tick,
        DateTime Utc,
        long MemoryBytes,
        long MemoryLimitBytes,
        double CpuPercent,
        int Pids,
        long NetworkInputBytes,
        long NetworkOutputBytes,
        long BlockReadBytes,
        long BlockWriteBytes);

    private readonly record struct CommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
