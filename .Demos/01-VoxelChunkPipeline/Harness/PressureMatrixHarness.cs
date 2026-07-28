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
    private const int StressProfileSamples = 6;
    private const int MinimumPreparationPassCount = 6;
    private const int MaximumPreparationPassCount = 50;
    private const int RequiredPreparationFluctuationCount = 4;
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
        List<PressureWorkerLifecycle> workerLifecycles = [];
        List<PressureProfilePair> profiles = new(
            options.ProfilePercents.Count);
        MatrixCheckpointWriter checkpointWriter = new(
            options,
            commit,
            imageId,
            binaryIdentities,
            startedUtc);
        await checkpointWriter.WriteCheckpointAsync(
            profiles,
            currentProfile: null,
            currentPair: null,
            commands,
            workerLifecycles,
            activeWorkers: []);
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
            int samplesInProfile =
                percent == StressProfilePercent
                    ? StressProfileSamples
                    : options.SamplesPerProfile;
            List<PressurePairedObservation> observations = new(
                samplesInProfile);
            List<PressureProfileInitialization> sampleInitializations =
                new(samplesInProfile);
            await checkpointWriter.WriteCheckpointAsync(
                profiles,
                CreateCurrentProfileCheckpoint(
                    request,
                    observations,
                    sampleInitializations),
                currentPair: null,
                commands,
                workerLifecycles,
                activeWorkers: []);
            for (int sampleIndex = 0;
                sampleIndex < samplesInProfile;
                sampleIndex++)
            {
                if (workerLifecycles.Any(
                        static lifecycle =>
                            !lifecycle.DisposalCompleted
                            || !lifecycle.ContainerAbsentAfterDisposal))
                {
                    throw new InvalidOperationException(
                        "A prior sample container remains active.");
                }

                bool safeFirst = PressureSamplePolicy.SafeRunsFirst(
                    sampleIndex);
                IsolatedPairRun pair = await RunIsolatedPairAsync(
                    options,
                    request,
                    imageId,
                    commands,
                    workerLifecycles,
                    pairOrdinal,
                    sampleIndex,
                    safeFirst,
                    checkpointWriter,
                    profiles,
                    observations,
                    sampleInitializations);
                pairOrdinal++;
                observations.Add(pair.Observation);
                sampleInitializations.Add(pair.Initialization);
                await checkpointWriter.WriteCheckpointAsync(
                    profiles,
                    CreateCurrentProfileCheckpoint(
                        request,
                        observations,
                        sampleInitializations),
                    currentPair: null,
                    commands,
                    workerLifecycles,
                    activeWorkers: []);
                totalInitializationMilliseconds +=
                    pair.Initialization.ElapsedMilliseconds;
                totalMeasuredMilliseconds +=
                    pair.Observation.Safe.ProfileElapsedMilliseconds
                        ?? pair.Observation.Safe.ElapsedLowerBoundMilliseconds;
                totalMeasuredMilliseconds +=
                    pair.Observation.Nam.ProfileElapsedMilliseconds
                        ?? pair.Observation.Nam.ElapsedLowerBoundMilliseconds;
                if (pair.PreparationFailure is { } preparationFailure)
                {
                    PressurePairedStatistics failureStatistics =
                        SummarizeProfile(
                            percent,
                            options,
                            observations);
                    PressureProfilePair failedProfile = new(
                        percent,
                        options.CgroupCapBytes,
                        target,
                        sampleInitializations[0],
                        observations,
                        failureStatistics,
                        sampleInitializations);
                    await WritePreparationFailureReportAsync(
                        options,
                        commit,
                        imageId,
                        binaryIdentities,
                        profiles,
                        failedProfile,
                        preparationFailure,
                        commands,
                        workerLifecycles,
                        startedUtc,
                        endToEndStart,
                        checkpointWriter);
                    Console.WriteLine(
                        JsonSerializer.Serialize(
                            preparationFailure,
                            VoxelJson.Options));
                    Console.WriteLine(options.OutputPath);
                    return 4;
                }
            }

            if (!PressureSamplePolicy.HasBalancedOrder(observations))
            {
                throw new InvalidOperationException(
                    "The profile does not have an even paired run order.");
            }

            PressurePairedStatistics statistics = SummarizeProfile(
                percent,
                options,
                observations);
            profiles.Add(new PressureProfilePair(
                percent,
                options.CgroupCapBytes,
                target,
                sampleInitializations[0],
                observations,
                statistics,
                sampleInitializations));
            await checkpointWriter.WriteCheckpointAsync(
                profiles,
                currentProfile: null,
                currentPair: null,
                commands,
                workerLifecycles,
                activeWorkers: []);
        }

        if (workerLifecycles.Any(
                static lifecycle =>
                    !lifecycle.DisposalCompleted
                    || !lifecycle.ContainerAbsentAfterDisposal))
        {
            throw new InvalidOperationException(
                "A measured profile container remains active.");
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
            commands,
            workerLifecycles);
        Task<DockerWorker> namVerificationStart = DockerWorker.StartAsync(
            options,
            "NAM",
            options.NamCpuSet,
            imageId,
            commands,
            workerLifecycles);
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
            safeVerificationWorker.WarmAsync(
                options,
                checkpointWriter.SignalActivity),
            namVerificationWorker.WarmAsync(
                options,
                checkpointWriter.SignalActivity));
        long verificationPreparationStart = Stopwatch.GetTimestamp();
        Task<PressureImplementationObservation>
            safeVerificationPreparationTask =
                safeVerificationWorker.PrepareMeasurementAsync(
                    verificationRequest,
                    options);
        Task<PressureImplementationObservation>
            namVerificationPreparationTask =
                namVerificationWorker.PrepareMeasurementAsync(
                    verificationRequest,
                    options);
        await Task.WhenAll(
            safeVerificationPreparationTask,
            namVerificationPreparationTask);
        checkpointWriter.SignalActivity();
        PressureImplementationObservation safeVerificationPreparation =
            await safeVerificationPreparationTask;
        PressureImplementationObservation namVerificationPreparation =
            await namVerificationPreparationTask;
        double verificationPreparationMilliseconds =
            Stopwatch.GetElapsedTime(
                verificationPreparationStart).TotalMilliseconds;
        bool verificationPreparationPath =
            PreparationPathPassed(
                verificationRequest,
                safeVerificationPreparation,
                namVerificationPreparation);
        bool verificationPreparationReset =
            PreparationResetPassed(
                safeVerificationPreparation,
                namVerificationPreparation);
        if (!verificationPreparationPath
            || !verificationPreparationReset)
        {
            throw new InvalidOperationException(
                "The verification preparation did not produce an equal reset state.");
        }

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
                / 100),
            new PressureMeasurementPreparation(
                verificationPercent,
                verificationTarget,
                verificationPreparationMilliseconds,
                safeVerificationPreparation,
                namVerificationPreparation,
                verificationPreparationPath,
                verificationPreparationReset));
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
        await safeVerificationWorker.DisposeAsync();
        await namVerificationWorker.DisposeAsync();

        DateTime completedUtc = DateTime.UtcNow;
        bool profileIsolation = ProfileIsolationPassed(
            profiles,
            verification,
            workerLifecycles);
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
                "sequential with an even alternating order in each profile",
            ["profileInitialization"] =
                "fresh containers per pair, four fixed warmups, and a bounded measured-path preparation series per implementation",
            ["crossProfileProcessState"] = "none",
            ["crossSampleProcessState"] = "none",
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
                + $"{StressProfileSamples} complete paired samples",
            ["measurementPreparation"] =
                "each timed request starts only after four preparation fluctuations. Exhaustion at 50 writes a failure artifact",
            ["measurementEvidence"] =
                "predeclared plan and terminal completion without per-chunk evidence",
            ["measurementBoundary"] =
                "host starts before BeginProcessing and stops at ProcessingCompleted",
            ["cgroupPeakTelemetry"] =
                "one reset before preparation, then a recorded cumulative peak before each request",
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
                "Each paired sample uses new containers, four fixed warmups, and from six through 50 measured-path preparations.",
                "Preparation exhaustion writes every attempt and does not start the timed request.",
                "The 10000-percent stress profile uses six complete paired samples.",
                "Each measured profile materializes every output and completes its mapped handoff.",
                "One exact maximum-demand run follows measurement. It reads every output byte."
            ],
            workerLifecycles);
        await checkpointWriter.WriteFinalReportAsync(report);
        Console.WriteLine(JsonSerializer.Serialize(summary, VoxelJson.Options));
        Console.WriteLine(options.OutputPath);
        return options.Enforce && !summary.GatePassed ? 3 : 0;
    }

    private static PressureCurrentProfileCheckpoint
        CreateCurrentProfileCheckpoint(
            PressureProfileRequest request,
            IReadOnlyList<PressurePairedObservation> observations,
            IReadOnlyList<PressureProfileInitialization> initializations) =>
        new(
            request.ProfilePercent,
            request.CgroupCapBytes,
            request.RequestedCumulativeDemandBytes,
            observations.ToArray(),
            initializations.ToArray());

    private static async Task WritePreparationFailureReportAsync(
        Options options,
        string commit,
        string imageId,
        IReadOnlyList<PressureBinaryIdentity> binaryIdentities,
        IReadOnlyList<PressureProfilePair> completedProfiles,
        PressureProfilePair failedProfile,
        PressurePreparationFailure failure,
        IReadOnlyList<string> commands,
        IReadOnlyList<PressureWorkerLifecycle> workerLifecycles,
        DateTime startedUtc,
        long endToEndStart,
        MatrixCheckpointWriter checkpointWriter)
    {
        PressureMatrixFailureReport report = new(
            commit,
            imageId,
            binaryIdentities,
            options.CgroupCapBytes,
            options.DeadlineMilliseconds,
            options.Seed,
            options.ProfilePercents,
            completedProfiles.ToArray(),
            failedProfile,
            failure,
            commands.ToArray(),
            workerLifecycles.ToArray(),
            startedUtc,
            DateTime.UtcNow,
            Stopwatch.GetElapsedTime(
                endToEndStart).TotalMilliseconds);
        await checkpointWriter.WriteFinalReportAsync(report);
    }

    private static async Task<IsolatedPairRun> RunIsolatedPairAsync(
        Options options,
        PressureProfileRequest request,
        string imageId,
        List<string> commands,
        List<PressureWorkerLifecycle> workerLifecycles,
        int pairOrdinal,
        int sampleIndex,
        bool safeFirst,
        MatrixCheckpointWriter checkpointWriter,
        IReadOnlyList<PressureProfilePair> completedProfiles,
        IReadOnlyList<PressurePairedObservation> completedObservations,
        IReadOnlyList<PressureProfileInitialization> completedInitializations)
    {
        DateTime initializationStartedUtc = DateTime.UtcNow;
        long initializationStart = Stopwatch.GetTimestamp();
        Task<DockerWorker> safeStart = DockerWorker.StartAsync(
            options,
            "SafeCSharp",
            options.SafeCpuSet,
            imageId,
            commands,
            workerLifecycles);
        Task<DockerWorker> namStart = DockerWorker.StartAsync(
            options,
            "NAM",
            options.NamCpuSet,
            imageId,
            commands,
            workerLifecycles);
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

        DockerWorker safe = await safeStart;
        DockerWorker nam = await namStart;
        string safeContainerName = safe.ContainerName;
        string namContainerName = nam.ContainerName;
        PreparationSeries safePreparation = PreparationSeries.Empty;
        PreparationSeries namPreparation = PreparationSeries.Empty;
        PressureImplementationObservation safeObservation = default;
        PressureImplementationObservation namObservation = default;
        PressurePreparationFailure? preparationFailure = null;
        bool safeTimedRequestStarted = false;
        bool namTimedRequestStarted = false;
        bool equivalentMeasuredPath = false;
        bool preparationReset = false;
        double initializationMilliseconds = 0;
        double preparationMilliseconds = 0;
        List<PressurePreparationSeriesCheckpoint> completedPreparations = [];

        async Task CheckpointCurrentPairAsync()
        {
            PressureCurrentPairCheckpoint currentPair = new(
                pairOrdinal,
                sampleIndex,
                safeFirst,
                safeTimedRequestStarted ? safeObservation : null,
                namTimedRequestStarted ? namObservation : null,
                safeTimedRequestStarted,
                namTimedRequestStarted,
                completedPreparations.ToArray());
            await checkpointWriter.WriteCheckpointAsync(
                completedProfiles,
                CreateCurrentProfileCheckpoint(
                    request,
                    completedObservations,
                    completedInitializations),
                currentPair,
                commands,
                workerLifecycles,
                [safe.CaptureCheckpoint(), nam.CaptureCheckpoint()]);
        }

        async Task CheckpointPreparationAsync(
            DockerWorker worker,
            PreparationSeries series)
        {
            PressurePreparationSeriesCheckpoint preparation = new(
                pairOrdinal,
                sampleIndex,
                safeFirst,
                worker.Implementation,
                request.ProfilePercent,
                request.RequestedCumulativeDemandBytes,
                series.ElapsedMilliseconds,
                series.Attempts,
                series.Assessment);
            completedPreparations.Add(preparation);
            await CheckpointCurrentPairAsync();
        }

        try
        {
            await Task.WhenAll(
                safe.WarmAsync(
                    options,
                    checkpointWriter.SignalActivity),
                nam.WarmAsync(
                    options,
                    checkpointWriter.SignalActivity));
            initializationMilliseconds = Stopwatch.GetElapsedTime(
                initializationStart).TotalMilliseconds;
            if (safeFirst)
            {
                safePreparation = await PrepareMeasurementSeriesAsync(
                    safe,
                    request,
                    options,
                    checkpointWriter.SignalActivity);
                preparationMilliseconds +=
                    safePreparation.ElapsedMilliseconds;
                await CheckpointPreparationAsync(
                    safe,
                    safePreparation);
                if (safePreparation.Assessment.Accepted)
                {
                    safeObservation = await safe.RunProfileAsync(
                        request,
                        options);
                    safeTimedRequestStarted = true;
                    await CheckpointCurrentPairAsync();
                }
                else
                {
                    preparationFailure = CreatePreparationFailure(
                        safe,
                        request,
                        sampleIndex,
                        safePreparation);
                }

                if (preparationFailure is null)
                {
                    namPreparation = await PrepareMeasurementSeriesAsync(
                        nam,
                        request,
                        options,
                        checkpointWriter.SignalActivity);
                    preparationMilliseconds +=
                        namPreparation.ElapsedMilliseconds;
                    await CheckpointPreparationAsync(
                        nam,
                        namPreparation);
                    if (namPreparation.Assessment.Accepted)
                    {
                        namObservation = await nam.RunProfileAsync(
                            request,
                            options);
                        namTimedRequestStarted = true;
                        await CheckpointCurrentPairAsync();
                    }
                    else
                    {
                        preparationFailure = CreatePreparationFailure(
                            nam,
                            request,
                            sampleIndex,
                            namPreparation);
                    }
                }
            }
            else
            {
                namPreparation = await PrepareMeasurementSeriesAsync(
                    nam,
                    request,
                    options,
                    checkpointWriter.SignalActivity);
                preparationMilliseconds +=
                    namPreparation.ElapsedMilliseconds;
                await CheckpointPreparationAsync(
                    nam,
                    namPreparation);
                if (namPreparation.Assessment.Accepted)
                {
                    namObservation = await nam.RunProfileAsync(
                        request,
                        options);
                    namTimedRequestStarted = true;
                    await CheckpointCurrentPairAsync();
                }
                else
                {
                    preparationFailure = CreatePreparationFailure(
                        nam,
                        request,
                        sampleIndex,
                        namPreparation);
                }

                if (preparationFailure is null)
                {
                    safePreparation = await PrepareMeasurementSeriesAsync(
                        safe,
                        request,
                        options,
                        checkpointWriter.SignalActivity);
                    preparationMilliseconds +=
                        safePreparation.ElapsedMilliseconds;
                    await CheckpointPreparationAsync(
                        safe,
                        safePreparation);
                    if (safePreparation.Assessment.Accepted)
                    {
                        safeObservation = await safe.RunProfileAsync(
                            request,
                            options);
                        safeTimedRequestStarted = true;
                        await CheckpointCurrentPairAsync();
                    }
                    else
                    {
                        preparationFailure = CreatePreparationFailure(
                            safe,
                            request,
                            sampleIndex,
                            safePreparation);
                    }
                }
            }

            initializationMilliseconds += preparationMilliseconds;
            equivalentMeasuredPath =
                safePreparation.Attempts.Count > 0
                && namPreparation.Attempts.Count > 0
                && PreparationPathPassed(
                    request,
                    safePreparation.Attempts[^1],
                    namPreparation.Attempts[^1]);
            preparationReset =
                PreparationSeriesResetPassed(safePreparation)
                && PreparationSeriesResetPassed(namPreparation);
            if (preparationFailure is null
                && (!equivalentMeasuredPath
                    || !preparationReset
                    || !safePreparation.Assessment.Accepted
                    || !namPreparation.Assessment.Accepted))
            {
                throw new InvalidOperationException(
                    "The isolated measurement preparation is not valid.");
            }

            if (preparationFailure is { } failure)
            {
                if (!safeTimedRequestStarted)
                {
                    safeObservation =
                        CreatePreparationFailureObservation(
                            safe,
                            request,
                            failure);
                }

                if (!namTimedRequestStarted)
                {
                    namObservation =
                        CreatePreparationFailureObservation(
                            nam,
                            request,
                            failure);
                }
            }
        }
        finally
        {
            try
            {
                await safe.DisposeAsync();
            }
            finally
            {
                await nam.DisposeAsync();
            }
        }

        if (!CompletedLifecyclePassed(
                workerLifecycles,
                safeContainerName,
                DockerWorker.WarmupPassCount
                    + safePreparation.Attempts.Count
                    + Convert.ToInt32(safeTimedRequestStarted))
            || !CompletedLifecyclePassed(
                workerLifecycles,
                namContainerName,
                DockerWorker.WarmupPassCount
                    + namPreparation.Attempts.Count
                    + Convert.ToInt32(namTimedRequestStarted)))
        {
            throw new InvalidOperationException(
                "An isolated sample container did not complete disposal.");
        }

        PressureProfileInitialization initialization = new(
            pairOrdinal,
            initializationStartedUtc,
            initializationMilliseconds,
            safeContainerName,
            namContainerName,
            DockerWorker.WarmupPassCount,
            WarmupProfilePercent,
            checked(
                options.CgroupCapBytes
                * WarmupProfilePercent
                / 100),
            new PressureMeasurementPreparation(
                request.ProfilePercent,
                request.RequestedCumulativeDemandBytes,
                preparationMilliseconds,
                safePreparation.Attempts.LastOrDefault(),
                namPreparation.Attempts.LastOrDefault(),
                equivalentMeasuredPath,
                preparationReset,
                safePreparation.Attempts,
                namPreparation.Attempts,
                safePreparation.Assessment,
                namPreparation.Assessment,
                MinimumPreparationPassCount,
                MaximumPreparationPassCount,
                RequiredPreparationFluctuationCount,
                safeTimedRequestStarted,
                namTimedRequestStarted,
                preparationFailure?.Message));
        PressurePairedObservation observation = new(
            sampleIndex,
            safeObservation,
            namObservation,
            StructuralParity(
                safeObservation.ChildResult,
                namObservation.ChildResult),
            safeFirst);
        return new IsolatedPairRun(
            observation,
            initialization,
            preparationFailure);
    }

    private static async Task<PreparationSeries>
        PrepareMeasurementSeriesAsync(
            DockerWorker worker,
            PressureProfileRequest request,
            Options options,
            Action signalActivity)
    {
        long preparationStart = Stopwatch.GetTimestamp();
        List<PressureImplementationObservation> attempts =
            new(MaximumPreparationPassCount);
        List<double> elapsedMilliseconds =
            new(MaximumPreparationPassCount);
        PressurePreparationAssessment assessment = default;
        for (int attemptIndex = 0;
            attemptIndex < MaximumPreparationPassCount;
            attemptIndex++)
        {
            PressureImplementationObservation observation =
                await worker.PrepareMeasurementAsync(
                    request,
                    options);
            int expectedOrdinal =
                DockerWorker.WarmupPassCount + attemptIndex + 1;
            if (!PreparationObservationPassed(
                    request,
                    observation)
                || !ResetStatePassed(
                    observation.ChildResult?.StateAfterReset)
                || observation.ChildResult?.StateAfterReset?.RequestOrdinal
                    != expectedOrdinal
                || observation.ProfileElapsedMilliseconds is not > 0)
            {
                throw new InvalidOperationException(
                    "A measurement preparation attempt is not valid.");
            }

            attempts.Add(observation);
            elapsedMilliseconds.Add(
                observation.ProfileElapsedMilliseconds.Value);
            assessment = PressurePreparationPolicy.Evaluate(
                elapsedMilliseconds,
                MinimumPreparationPassCount,
                MaximumPreparationPassCount,
                RequiredPreparationFluctuationCount);
            signalActivity();
            if (!assessment.ShouldContinue)
            {
                break;
            }
        }

        return new PreparationSeries(
            attempts.ToArray(),
            assessment,
            Stopwatch.GetElapsedTime(
                preparationStart).TotalMilliseconds);
    }

    private static PressurePreparationFailure CreatePreparationFailure(
        DockerWorker worker,
        PressureProfileRequest request,
        int sampleIndex,
        PreparationSeries preparation)
    {
        if (!preparation.Assessment.Exhausted
            || preparation.Attempts.Count
                != MaximumPreparationPassCount)
        {
            throw new InvalidOperationException(
                "Preparation failure requires maximum exhaustion.");
        }

        return new PressurePreparationFailure(
            worker.Implementation,
            request.ProfilePercent,
            sampleIndex,
            $"{worker.Implementation} preparation reached "
                + $"{MaximumPreparationPassCount} attempts without "
                + $"{RequiredPreparationFluctuationCount} fluctuations.");
    }

    private static PressureImplementationObservation
        CreatePreparationFailureObservation(
            DockerWorker worker,
            PressureProfileRequest request,
            PressurePreparationFailure failure)
    {
        string message = worker.Implementation == failure.Implementation
            ? failure.Message
            : $"The timed request did not start because "
                + $"{failure.Implementation} preparation failed.";
        return new PressureImplementationObservation(
            Implementation: worker.Implementation,
            ProfilePercent: request.ProfilePercent,
            Outcome: PressureProfileOutcome.HarnessFailure,
            FailureAttribution:
                PressureFailureAttribution.HarnessInfrastructure,
            CgroupCapBytes: request.CgroupCapBytes,
            RequestedCumulativeDemandBytes:
                request.RequestedCumulativeDemandBytes,
            RealizedCumulativeDemandBytes: 0,
            DeadlineMilliseconds: request.DeadlineMilliseconds,
            ProfileElapsedMilliseconds: null,
            ElapsedLowerBoundMilliseconds: 0,
            SetupMilliseconds: 0,
            ResultTransferMilliseconds: 0,
            CompletedChunks: 0,
            CompletedLogicalBytes: 0,
            LastCompletedStage: VoxelPipelineStage.None,
            LastCompletedChunkId: -1,
            CorrectnessPassed: false,
            ExitCode: null,
            ExceptionType: typeof(InvalidOperationException).FullName,
            ExceptionMessage: message,
            ChildResult: null,
            ManagedAllocatedSinceWorkerStart: 0,
            Gen2CollectionsSinceWorkerStart: 0,
            CpuMillisecondsSinceWorkerStart: 0,
            Progress: [],
            HostSamples: [],
            InitialCgroup: default,
            FinalCgroup: default,
            CgroupPeakReset: false,
            ExternalCgroupPeakBytes: 0,
            ExternalCpuPercentMean: 0,
            ExternalCpuPercentPeak: 0,
            Isolation: worker.Isolation);
    }

    private static bool PreparationSeriesResetPassed(
        PreparationSeries preparation) =>
        preparation.Attempts.Count > 0
        && preparation.Attempts.All(
            static attempt => ResetStatePassed(
                attempt.ChildResult?.StateAfterReset));

    private static bool CompletedLifecyclePassed(
        IReadOnlyList<PressureWorkerLifecycle> lifecycles,
        string containerName,
        int expectedRequestCount)
    {
        PressureWorkerLifecycle[] matches = lifecycles
            .Where(
                lifecycle => lifecycle.ContainerName == containerName)
            .ToArray();
        return matches.Length == 1
            && matches[0].DisposalCompleted
            && matches[0].ContainerAbsentAfterDisposal
            && matches[0].FirstRequestOrdinal == 1
            && matches[0].LastRequestOrdinal
                == expectedRequestCount
            && matches[0].RequestCount
                == expectedRequestCount;
    }

    private static int ExpectedSampleRequestCount(
        PressureProfileInitialization initialization,
        bool safe)
    {
        if (initialization.MeasurementPreparation is not { } preparation)
        {
            return -1;
        }

        IReadOnlyList<PressureImplementationObservation>? attempts =
            safe
                ? preparation.SafeAttempts
                : preparation.NamAttempts;
        bool timedRequestStarted = safe
            ? preparation.SafeTimedRequestStarted
            : preparation.NamTimedRequestStarted;
        return initialization.WarmupPasses
            + (attempts?.Count ?? 1)
            + Convert.ToInt32(timedRequestStarted);
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

    private static bool PreparationPathPassed(
        PressureProfileRequest request,
        PressureImplementationObservation safe,
        PressureImplementationObservation nam) =>
        PreparationObservationPassed(request, safe)
        && PreparationObservationPassed(request, nam)
        && StructuralParity(
            safe.ChildResult,
            nam.ChildResult);

    private static bool PreparationObservationPassed(
        PressureProfileRequest request,
        PressureImplementationObservation observation) =>
        observation.Outcome == PressureProfileOutcome.Completed
        && observation.CorrectnessPassed
        && observation.ChildResult is { } result
        && result.ExecutionMode == PressureExecutionMode.Measurement
        && result.ProfilePercent == request.ProfilePercent
        && result.RequestedCumulativeDemandBytes
            == request.RequestedCumulativeDemandBytes;

    private static bool PreparationResetPassed(
        PressureImplementationObservation safe,
        PressureImplementationObservation nam) =>
        ResetStatePassed(safe.ChildResult?.StateAfterReset)
        && ResetStatePassed(nam.ChildResult?.StateAfterReset);

    private static bool ResetStatePassed(
        PressureSessionState? state) =>
        state is { } captured
        && captured.RequestOrdinal > 0
        && captured.CompletedRequestCount
            == captured.RequestOrdinal
        && captured.AllocationPlanFingerprint > 0
        && captured.LogicalResetPassed;

    private static bool ProfileIsolationPassed(
        IReadOnlyList<PressureProfilePair> profiles,
        PressureVerificationPair verification,
        IReadOnlyList<PressureWorkerLifecycle> lifecycles)
    {
        Dictionary<string, int> containerOwners =
            new(StringComparer.Ordinal);
        for (int profileIndex = 0;
            profileIndex < profiles.Count;
            profileIndex++)
        {
            PressureProfilePair profile = profiles[profileIndex];
            IReadOnlyList<PressureProfileInitialization>? initializations =
                profile.SampleInitializations;
            if (initializations is null
                || initializations.Count != profile.Observations.Count
                || !PressureSamplePolicy.HasBalancedOrder(
                    profile.Observations))
            {
                return false;
            }

            for (int sampleIndex = 0;
                sampleIndex < profile.Observations.Count;
                sampleIndex++)
            {
                PressurePairedObservation observation =
                    profile.Observations[sampleIndex];
                PressureProfileInitialization initialization =
                    initializations[sampleIndex];
                int sampleOwner = checked(
                    ((profileIndex + 1) * 100_000) + sampleIndex);
                if (initialization.SafeContainerName
                        != observation.Safe.Isolation.ContainerName
                    || initialization.NamContainerName
                        != observation.Nam.Isolation.ContainerName)
                {
                    return false;
                }

                if (!RegisterContainer(
                        containerOwners,
                        initialization.SafeContainerName,
                        sampleOwner)
                    || !RegisterContainer(
                        containerOwners,
                        initialization.NamContainerName,
                        sampleOwner)
                    || !CompletedLifecyclePassed(
                        lifecycles,
                        initialization.SafeContainerName,
                        ExpectedSampleRequestCount(
                            initialization,
                            safe: true))
                    || !CompletedLifecyclePassed(
                        lifecycles,
                        initialization.NamContainerName,
                        ExpectedSampleRequestCount(
                            initialization,
                            safe: false)))
                {
                    return false;
                }
            }
        }

        int verificationOwner = -verification.ProfilePercent;
        bool namedContainersPassed = RegisterContainer(
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
        int measuredPairCount = profiles.Sum(
            static profile => profile.Observations.Count);
        if (!namedContainersPassed
            || lifecycles.Count != checked(
                (measuredPairCount + 1) * 2)
            || lifecycles.Any(
                static lifecycle =>
                    !lifecycle.DisposalCompleted
                    || !lifecycle.ContainerAbsentAfterDisposal
                    || string.IsNullOrWhiteSpace(
                        lifecycle.ContainerId)
                    || lifecycle.ContainerProcessId <= 0
                    || string.IsNullOrWhiteSpace(
                        lifecycle.CgroupIdentity)
                    || lifecycle.FirstRequestOrdinal != 1
                    || lifecycle.LastRequestOrdinal
                        != lifecycle.RequestCount))
        {
            return false;
        }

        return lifecycles
                .Select(static lifecycle => lifecycle.ContainerName)
                .Distinct(StringComparer.Ordinal)
                .Count()
            == lifecycles.Count
            && lifecycles
                .Select(static lifecycle => lifecycle.ContainerId)
                .Distinct(StringComparer.Ordinal)
                .Count()
            == lifecycles.Count;
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

        PressureImplementationObservation[] safeObservations = observations
            .Select(static observation => observation.Safe)
            .ToArray();
        PressureImplementationObservation[] namObservations = observations
            .Select(static observation => observation.Nam)
            .ToArray();
        bool namCompleted =
            PressureOutcomePolicy.AllSamplesCompletedWithinDeadline(
                namObservations);
        bool safeCompleted =
            PressureOutcomePolicy.AllSamplesCompletedWithinDeadline(
                safeObservations);
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
        bool correctnessGate = outcomeDecision.CorrectnessGatePassed
            && PressureSamplePolicy.HasBalancedOrder(observations);
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
                        ? PressureSamplePolicy.StressConfidencePassed(
                            sampleCount,
                            StressProfileSamples,
                            lower ?? double.NegativeInfinity)
                        : lower > 1.00;
                performanceGate = mean >= requiredSpeedup
                    && confidenceGate;
                interpretation = percent == StressProfilePercent
                    ? $"Both implementations completed the stress profile. "
                        + $"This profile requires {requiredSpeedup:F2}x "
                        + $"mean speedup from {StressProfileSamples} pairs "
                        + "and a positive confidence lower bound."
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
        TimeSpan timeout,
        bool requireSuccess = true)
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
        if (requireSuccess && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} exited {process.ExitCode}: {standardError}");
        }

        return new CommandResult(process.ExitCode, standardOutput, standardError);
    }

    private sealed class MatrixCheckpointWriter
    {
        private const int FormatVersion = 1;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly object _activityGate = new();
        private readonly Options _options;
        private readonly string _commit;
        private readonly string _imageId;
        private readonly IReadOnlyList<PressureBinaryIdentity>
            _binaryIdentities;
        private readonly DateTime _startedUtc;
        private long _sequence;

        internal MatrixCheckpointWriter(
            Options options,
            string commit,
            string imageId,
            IReadOnlyList<PressureBinaryIdentity> binaryIdentities,
            DateTime startedUtc)
        {
            _options = options;
            _commit = commit;
            _imageId = imageId;
            _binaryIdentities = binaryIdentities.ToArray();
            _startedUtc = startedUtc;
            string? activityDirectory =
                Path.GetDirectoryName(options.ActivityPath);
            if (!string.IsNullOrEmpty(activityDirectory))
            {
                Directory.CreateDirectory(activityDirectory);
            }

            SignalActivity();
        }

        internal void SignalActivity()
        {
            lock (_activityGate)
            {
                File.WriteAllText(
                    _options.ActivityPath,
                    DateTime.UtcNow.ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                    Encoding.UTF8);
            }
        }

        internal async Task WriteCheckpointAsync(
            IReadOnlyList<PressureProfilePair> completedProfiles,
            PressureCurrentProfileCheckpoint? currentProfile,
            PressureCurrentPairCheckpoint? currentPair,
            IReadOnlyList<string> commands,
            IReadOnlyList<PressureWorkerLifecycle> workerLifecycles,
            IReadOnlyList<PressureWorkerCheckpoint> activeWorkers)
        {
            await _writeGate.WaitAsync();
            try
            {
                string[] commandSnapshot;
                lock (commands)
                {
                    commandSnapshot = commands.ToArray();
                }

                PressureWorkerLifecycle[] lifecycleSnapshot;
                lock (workerLifecycles)
                {
                    lifecycleSnapshot = workerLifecycles
                        .Where(
                            static lifecycle =>
                                lifecycle.DisposalCompleted
                                && lifecycle.ContainerAbsentAfterDisposal)
                        .ToArray();
                }

                PressureMatrixCheckpoint checkpoint = new(
                    FormatVersion,
                    checked(++_sequence),
                    _commit,
                    _imageId,
                    _binaryIdentities,
                    _options.ToSnapshot(),
                    completedProfiles.ToArray(),
                    currentProfile,
                    currentPair,
                    commandSnapshot,
                    lifecycleSnapshot,
                    activeWorkers.ToArray(),
                    _startedUtc,
                    DateTime.UtcNow);
                await AtomicPressureArtifactFile.WriteJsonAsync(
                    _options.OutputPath,
                    checkpoint,
                    CreateOutputOptions());
                SignalActivity();
            }
            finally
            {
                _writeGate.Release();
            }
        }

        internal async Task WriteFinalReportAsync<T>(T report)
        {
            await _writeGate.WaitAsync();
            try
            {
                await AtomicPressureArtifactFile.WriteJsonAsync(
                    _options.OutputPath,
                    report,
                    CreateOutputOptions());
                SignalActivity();
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private static JsonSerializerOptions CreateOutputOptions() =>
            new(VoxelJson.Options)
            {
                WriteIndented = true
            };
    }

    private sealed class DockerWorker : IAsyncDisposable
    {
        internal const int WarmupPassCount = 4;
        private readonly object _sampleGate = new();
        private readonly List<RawHostSample> _samples = [];
        private readonly string _implementation;
        private readonly string _cpuSet;
        private readonly string _imageId;
        private readonly List<PressureWorkerLifecycle> _lifecycles;
        private Process? _process;
        private Process? _statsProcess;
        private CancellationTokenSource? _statsCancellation;
        private Task? _statsReader;
        private Task<string>? _statsErrorReader;
        private Task<string>? _stderrReader;
        private bool _peakResetAttempted;
        private long _requestOrdinal;

        private DockerWorker(
            string implementation,
            string cpuSet,
            string imageId,
            List<PressureWorkerLifecycle> lifecycles)
        {
            _implementation = implementation;
            _cpuSet = cpuSet;
            _imageId = imageId;
            _lifecycles = lifecycles;
        }

        internal bool IsAlive => _process is { HasExited: false };

        internal string Implementation => _implementation;

        internal string ContainerName { get; private set; } = string.Empty;

        internal PressureRuntimeSnapshot StartupRuntime { get; private set; }

        internal PressureEffectiveIsolation Isolation { get; private set; }

        internal PressureWorkerCheckpoint CaptureCheckpoint() =>
            new(
                _implementation,
                ContainerName,
                Isolation.ContainerId,
                Isolation.ContainerProcessId,
                Isolation.CgroupIdentity,
                StartupRuntime,
                Isolation,
                _requestOrdinal,
                IsAlive);

        internal static async Task<DockerWorker> StartAsync(
            Options options,
            string implementation,
            string cpuSet,
            string imageId,
            List<string> commands,
            List<PressureWorkerLifecycle> lifecycles)
        {
            DockerWorker worker = new(
                implementation,
                cpuSet,
                imageId,
                lifecycles);
            await worker.StartProcessAsync(options, commands);
            return worker;
        }

        internal async Task WarmAsync(
            Options options,
            Action signalActivity)
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

                signalActivity();
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

        internal Task<PressureImplementationObservation>
            PrepareMeasurementAsync(
                PressureProfileRequest request,
                Options options) =>
                RunProfileCoreAsync(
                    request,
                    options,
                    PressureCommandKind.RunProfile,
                    enforceDeadline: false,
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
            _requestOrdinal = 0;
            ContainerName =
                $"nam-voxel-{_implementation.ToLowerInvariant()}-{Guid.NewGuid():N}";
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
            long commandOrdinal = checked(++_requestOrdinal);
            request = request with
            {
                RequestOrdinal = commandOrdinal
            };
            string requestId =
                $"{_implementation}-{request.ProfilePercent}-{commandOrdinal}-{Guid.NewGuid():N}";
            PressureCommand command = new(
                requestId,
                commandKind,
                request,
                commandOrdinal);
            string beginProcessing = JsonSerializer.Serialize(
                new PressureCommand(
                    requestId,
                    PressureCommandKind.BeginProcessing,
                    CommandOrdinal: commandOrdinal),
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
            long cgroupCpuMicroseconds = Math.Max(
                0,
                finalCgroup.CpuUsageMicroseconds
                    - initialCgroup.CpuUsageMicroseconds);
            double effectiveCpuCores =
                elapsedMilliseconds is > 0
                    ? (cgroupCpuMicroseconds / 1000.0)
                        / elapsedMilliseconds.Value
                    : 0;
            long pageFaultsDelta = Math.Max(
                0,
                finalCgroup.PageFaults - initialCgroup.PageFaults);
            long majorPageFaultsDelta = Math.Max(
                0,
                finalCgroup.MajorPageFaults
                    - initialCgroup.MajorPageFaults);
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
                Isolation,
                effectiveCpuCores,
                pageFaultsDelta,
                majorPageFaultsDelta);
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
            CommandResult identity = await RunCommandAsync(
                "docker",
                [
                    "inspect",
                    "--format",
                    "{{.Id}}|{{.State.Pid}}",
                    ContainerName
                ],
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
            CommandResult cgroupIdentity = await RunCommandAsync(
                "docker",
                [
                    "exec",
                    ContainerName,
                    "sh",
                    "-c",
                    "cat /proc/1/cgroup; readlink /proc/1/ns/cgroup"
                ],
                TimeSpan.FromSeconds(10));
            string[] lines = effective.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            string[] identityParts = identity.StandardOutput
                .Trim()
                .Split('|', StringSplitOptions.TrimEntries);
            string containerId =
                identityParts.ElementAtOrDefault(0)
                ?? string.Empty;
            _ = int.TryParse(
                identityParts.ElementAtOrDefault(1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int containerProcessId);
            string effectiveCgroupIdentity = string.Join(
                ';',
                cgroupIdentity.StandardOutput.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries
                        | StringSplitOptions.TrimEntries));
            Dictionary<string, string> inspectValues = new(StringComparer.Ordinal)
            {
                ["hostConfig"] = inspect.StandardOutput.Trim(),
                ["effectiveMemoryMax"] = lines.ElementAtOrDefault(2) ?? string.Empty,
                ["effectiveMemorySwapMax"] = lines.ElementAtOrDefault(3) ?? string.Empty,
                ["containerId"] = containerId,
                ["containerProcessId"] = containerProcessId.ToString(
                    CultureInfo.InvariantCulture),
                ["cgroupIdentity"] = effectiveCgroupIdentity
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
                inspectValues,
                containerId,
                containerProcessId,
                effectiveCgroupIdentity,
                StartupRuntime);
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
                            "cat /sys/fs/cgroup/memory.max; cat /sys/fs/cgroup/memory.current; cat /sys/fs/cgroup/memory.peak; cat /sys/fs/cgroup/memory.events; cat /sys/fs/cgroup/memory.stat; printf 'swap_current '; cat /sys/fs/cgroup/memory.swap.current; printf 'swap_peak '; cat /sys/fs/cgroup/memory.swap.peak 2>/dev/null || echo 0; cat /sys/fs/cgroup/cpu.stat"
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
                long swapCurrent = FindCounter(
                    lines,
                    "swap_current");
                long swapPeak = FindCounter(lines, "swap_peak");
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
                    file,
                    swapCurrent,
                    swapPeak,
                    FindCounter(lines, "usage_usec"),
                    FindCounter(lines, "user_usec"),
                    FindCounter(lines, "system_usec"),
                    FindCounter(lines, "nr_periods"),
                    FindCounter(lines, "nr_throttled"),
                    FindCounter(lines, "throttled_usec"),
                    FindCounter(lines, "pgfault"),
                    FindCounter(lines, "pgmajfault"));
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
            if (_process is null)
            {
                return;
            }

            string stoppedContainerName = ContainerName;
            PressureEffectiveIsolation stoppedIsolation = Isolation;
            PressureRuntimeSnapshot stoppedRuntime = StartupRuntime;
            long stoppedRequestCount = _requestOrdinal;
            if (!_process.HasExited)
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

            if (!_process.HasExited)
            {
                try
                {
                    using CancellationTokenSource timeout =
                        new(TimeSpan.FromSeconds(3));
                    await _process.WaitForExitAsync(timeout.Token);
                }
                catch
                {
                }
            }

            bool processStopped = _process.HasExited;
            _process?.Dispose();
            _process = null;
            bool containerAbsent = await WaitForContainerAbsenceAsync(
                stoppedContainerName);
            PressureWorkerLifecycle lifecycle = new(
                _implementation,
                stoppedContainerName,
                stoppedIsolation.ContainerId,
                stoppedIsolation.ContainerProcessId,
                stoppedIsolation.CgroupIdentity,
                stoppedRuntime,
                stoppedRequestCount == 0 ? 0 : 1,
                stoppedRequestCount,
                checked((int)stoppedRequestCount),
                processStopped,
                containerAbsent,
                DateTime.UtcNow);
            lock (_lifecycles)
            {
                _lifecycles.Add(lifecycle);
            }

            lock (_sampleGate)
            {
                _samples.Clear();
            }
        }

        private static async Task<bool> WaitForContainerAbsenceAsync(
            string containerName)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                CommandResult inspect = await RunCommandAsync(
                    "docker",
                    ["inspect", containerName],
                    TimeSpan.FromSeconds(3),
                    requireSuccess: false);
                if (inspect.ExitCode != 0)
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            return false;
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
        string ActivityPath,
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
        int InactivityTimeoutSeconds,
        long AbsoluteFailSafeTimeoutSeconds,
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
            string activity = values.GetValueOrDefault(
                "--activity",
                output + ".activity");
            return new Options(
                Path.GetFullPath(repository),
                image,
                Path.GetFullPath(output),
                Path.GetFullPath(activity),
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
                ParseEvenPositiveInt(
                    values.GetValueOrDefault(
                        "--samples-per-profile",
                        "6"),
                    "--samples-per-profile"),
                ParseProfiles(values.GetValueOrDefault(
                    "--profiles",
                    string.Join(',', PressureMatrixHarness.ProfilePercents))),
                ParseInt(values.GetValueOrDefault(
                    "--inactivity-timeout-seconds",
                    "120")),
                ParseLong(values.GetValueOrDefault(
                    "--absolute-fail-safe-timeout-seconds",
                    "0")),
                enforce);
        }

        internal PressureMatrixOptionsSnapshot ToSnapshot() =>
            new(
                RepositoryRoot,
                Image,
                OutputPath,
                ActivityPath,
                SafeCpuSet,
                NamCpuSet,
                CgroupCapBytes,
                DeadlineMilliseconds,
                RetentionDepth,
                ProgressEveryChunks,
                Seed,
                PidsLimit,
                GcHeapHardLimitPercent,
                SamplesPerProfile,
                ProfilePercents.ToArray(),
                InactivityTimeoutSeconds,
                AbsoluteFailSafeTimeoutSeconds,
                Enforce);

        private static string Required(
            IReadOnlyDictionary<string, string> values,
            string key) =>
            values.TryGetValue(key, out string? value)
                ? value
                : throw new ArgumentException($"Missing required argument {key}.");

        private static int ParseInt(string value) =>
            int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

        private static int ParseEvenPositiveInt(
            string value,
            string name)
        {
            int result = ParseInt(value);
            if (result <= 0 || (result & 1) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    name,
                    "The sample count must be positive and even.");
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
            if (!PressureProfileOrderPolicy.FollowsCanonicalOrder(
                    profiles,
                    PressureMatrixHarness.ProfilePercents))
            {
                throw new ArgumentException(
                    "Profiles must be a canonical-order subset of 50,100,200,500,1000,10000.");
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

    private readonly record struct IsolatedPairRun(
        PressurePairedObservation Observation,
        PressureProfileInitialization Initialization,
        PressurePreparationFailure? PreparationFailure);

    private readonly record struct PreparationSeries(
        IReadOnlyList<PressureImplementationObservation> Attempts,
        PressurePreparationAssessment Assessment,
        double ElapsedMilliseconds)
    {
        internal static PreparationSeries Empty { get; } =
            new([], default, 0);
    }
}
