using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class VoxelSharedContractTests
{
    [Fact]
    public void PressureProfilesUseFourWarmupsAndFreshContainers()
    {
        string root = FindRepositoryRoot();
        string harness = File.ReadAllText(
            Path.Combine(
                root,
                ".Demos",
                "01-VoxelChunkPipeline",
                "Harness",
                "PressureMatrixHarness.cs"));
        int profileLoop = harness.IndexOf(
            "for (int profileOrdinal = 0;",
            StringComparison.Ordinal);
        int safeStart = harness.IndexOf(
            "Task<DockerWorker> safeStart = DockerWorker.StartAsync(",
            profileLoop,
            StringComparison.Ordinal);
        int sampleLoop = harness.IndexOf(
            "for (int sampleIndex = 0;",
            profileLoop,
            StringComparison.Ordinal);

        Assert.True(profileLoop >= 0);
        Assert.True(sampleLoop > profileLoop);
        Assert.True(safeStart > sampleLoop);
        Assert.Contains(
            "internal const int WarmupPassCount = 4;",
            harness,
            StringComparison.Ordinal);
        Assert.Contains(
            "await safe.DisposeAsync();",
            harness,
            StringComparison.Ordinal);
        Assert.Contains(
            "await nam.DisposeAsync();",
            harness,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProfileIsolationPassed(",
            harness,
            StringComparison.Ordinal);
        Assert.Contains(
            "crossProfileProcessState\"] = \"none\"",
            harness,
            StringComparison.Ordinal);
        Assert.Contains(
            "crossSampleProcessState\"] = \"none\"",
            harness,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AllAlgorithmAndProtocolTypesComeFromOneSharedAssembly()
    {
        Type[] contractTypes =
        [
            typeof(VoxelWorkloadOptions),
            typeof(BlockTypeDescriptor),
            typeof(CanonicalInputCell),
            typeof(CanonicalInputContract),
            typeof(CanonicalInputFixture),
            typeof(HandAuthoredInputFixture),
            typeof(FaceRecord),
            typeof(Vertex),
            typeof(PayloadSlice),
            typeof(SectionRepresentationKind),
            typeof(SectionSummary),
            typeof(SectionPrerenderDescriptor),
            typeof(VoxelCell),
            typeof(OutputFixture),
            typeof(CanonicalOutputSummary),
            typeof(ChunkOutputSummary),
            typeof(NativeOwnerProfile),
            typeof(StreamResult),
            typeof(ChunkResult),
            typeof(WorkerResult),
            typeof(PipelineResult),
            typeof(ChildRunResult),
            typeof(PressureRunMetrics),
            typeof(CgroupMemorySnapshot),
            typeof(CorrectnessReport),
            typeof(PairedSample),
            typeof(BenchmarkReport),
            typeof(BenchmarkSummary),
            typeof(PressureProfileRequest),
            typeof(PressureCommand),
            typeof(PressureProgress),
            typeof(PressureChunkEvidence),
            typeof(PressureCompilationConfiguration),
            typeof(PressureCompilationPolicy),
            typeof(PressureRuntimeSnapshot),
            typeof(PressureProfileResult),
            typeof(PressureSessionState),
            typeof(PressureEnvelope),
            typeof(PressureOutputEvidence),
            typeof(PressureChunkShape),
            typeof(CompilationSample),
            typeof(CompilationCompilerDiagnostics),
            typeof(CompilationGateSummary),
            typeof(CompilationGateReport),
            typeof(CompilationGatePolicy),
            typeof(PressureHostProgress),
            typeof(PressureHostSample),
            typeof(PressureEffectiveIsolation),
            typeof(PressureImplementationObservation),
            typeof(PressurePairedStatistics),
            typeof(PressureOutcomeDecision),
            typeof(PressureOutcomePolicy),
            typeof(PressureProfileOrderPolicy),
            typeof(PressureProfileContinuationPolicy),
            typeof(PressureMeasurementPreparation),
            typeof(PressureWorkerLifecycle),
            typeof(PressureProfilePair),
            typeof(PressureVerificationPair),
            typeof(PressureBinaryIdentity),
            typeof(PressureMatrixSummary),
            typeof(PressureMatrixOptionsSnapshot),
            typeof(PressureWorkerCheckpoint),
            typeof(PressureCurrentProfileCheckpoint),
            typeof(PressurePreparationSeriesCheckpoint),
            typeof(PressureCurrentPairCheckpoint),
            typeof(PressureMatrixCheckpoint),
            typeof(AtomicPressureArtifactFile),
            typeof(PressureMatrixReport),
            typeof(PressureProfileGateFailure),
            typeof(PressureMatrixCleanupState),
            typeof(PressureMatrixGateFailureReport),
            typeof(PressureDiagnosticRequest),
            typeof(PressureDiagnosticPhase),
            typeof(PressurePhaseTimings),
            typeof(PressurePhaseRecorder),
            typeof(PressureAllocatorDiagnosticSnapshot),
            typeof(PressureWorkerDiagnostic),
            typeof(PressureRequestDiagnostics),
            typeof(PressureExternalProcessSnapshot),
            typeof(PressureHostProcessorSample),
            typeof(PressureHostStateGate),
            typeof(PressureHostStabilityPolicy),
            typeof(PressureSustainedDiagnosticOptionsSnapshot),
            typeof(PressureSustainedDiagnosticTrace),
            typeof(PressureSustainedDiagnosticCleanup),
            typeof(PressureSustainedDiagnosticHostGateFailure),
            typeof(PressureSustainedDiagnosticReport)
        ];

        Assembly assembly = typeof(FaceRecord).Assembly;
        Assert.All(contractTypes, type => Assert.Same(assembly, type.Assembly));
        Assert.Equal(VoxelMath.FaceRecordBytes, Unsafe.SizeOf<FaceRecord>());
        Assert.Equal(7 * sizeof(int), VoxelMath.FaceRecordBytes);
        Assert.Equal(
            VoxelMath.SectionPrerenderDescriptorBytes,
            Unsafe.SizeOf<SectionPrerenderDescriptor>());
        Assert.DoesNotContain(
            assembly.GetTypes(),
            type => type.Name == "NativeFaceOutput");
    }

    [Fact]
    public void BothImplementationProjectsReferenceTheSharedContractWithoutDuplicateDtos()
    {
        string root = FindRepositoryRoot();
        string demoRoot = Path.Combine(root, ".Demos", "01-VoxelChunkPipeline");
        string sharedReference = "..\\SharedContract\\SharedContract.csproj";
        foreach (string implementation in new[] { "SafeCSharp", "NAM" })
        {
            string projectPath = Path.Combine(demoRoot, implementation, implementation + ".csproj");
            string project = File.ReadAllText(projectPath);
            Assert.Contains(sharedReference, project, StringComparison.Ordinal);

            foreach (string sourcePath in Directory.EnumerateFiles(
                Path.Combine(demoRoot, implementation),
                "*.cs",
                SearchOption.AllDirectories))
            {
                string source = File.ReadAllText(sourcePath);
                Assert.DoesNotContain("NativeFaceOutput", source, StringComparison.Ordinal);
                Assert.DoesNotContain("record struct FaceRecord", source, StringComparison.Ordinal);
                Assert.DoesNotContain("record struct Vertex", source, StringComparison.Ordinal);
                Assert.DoesNotContain("record struct PayloadSlice", source, StringComparison.Ordinal);
                Assert.DoesNotContain("record struct ChunkResult", source, StringComparison.Ordinal);
                Assert.DoesNotContain("record struct PipelineResult", source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void CompilationGateUsesConsumerBinariesAndBlocksBeforePressureExecution()
    {
        string root = FindRepositoryRoot();
        string demoRoot = Path.Combine(root, ".Demos", "01-VoxelChunkPipeline");
        string nativeProject = File.ReadAllText(
            Path.Combine(demoRoot, "NAM", "NAM.csproj"));
        Assert.Contains(
            "CompilationBenchmark",
            nativeProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Reference Include=\"Supprocom.NativeAllocationManagement\">",
            nativeProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Analyzer Include=",
            nativeProject,
            StringComparison.Ordinal);
        string compilationHarness = File.ReadAllText(
            Path.Combine(demoRoot, "Harness", "CompilationGateHarness.cs"));
        Assert.Contains(
            "\"compilation-gate\"",
            compilationHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"-property:OutputPath=\"",
            compilationHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"-property:GenerateDependencyFile=false\"",
            compilationHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"-property:GenerateRuntimeConfigurationFiles=false\"",
            compilationHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"-property:UseAppHost=false\"",
            compilationHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "start.Environment[\"DOTNET_TieredCompilation\"]",
            compilationHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "start.Environment[\"DOTNET_TieredPGO\"]",
            compilationHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "for (int pair = 0; pair < options.MeasuredPairs; pair++)",
            compilationHarness,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "options.WarmupPairs + options.MeasuredPairs",
            compilationHarness,
            StringComparison.Ordinal);
        int warmupGuard = compilationHarness.IndexOf(
            "if (!CompilationGatePolicy.CanStartMeasuredBuilds(",
            StringComparison.Ordinal);
        Assert.True(warmupGuard >= 0);
        int failureWrite = compilationHarness.IndexOf(
            "await WriteReportAsync(",
            warmupGuard,
            StringComparison.Ordinal);
        int failureReturn = compilationHarness.IndexOf(
            "return 3;",
            failureWrite,
            StringComparison.Ordinal);
        int measuredLoop = compilationHarness.IndexOf(
            "for (int pair = 0; pair < options.MeasuredPairs; pair++)",
            StringComparison.Ordinal);
        Assert.True(failureWrite > warmupGuard);
        Assert.True(failureReturn > failureWrite);
        Assert.True(measuredLoop > failureReturn);

        CompilationGateSummary failedSummary = default;
        string failedJson = System.Text.Json.JsonSerializer.Serialize(
            failedSummary,
            VoxelJson.Options);
        Assert.DoesNotContain("NaN", failedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", failedJson, StringComparison.Ordinal);

        string pressureHarness = File.ReadAllText(
                Path.Combine(
                    demoRoot,
                    "Harness",
                    "PressureMatrixHarness.cs"))
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);
        Assert.Contains(
            "200 => 1.75",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            ">= 1000 => 2.00",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "performanceGate = mean > 1.00 && lower > 1.00",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NamScalingGatePassed",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "current.Value >= previous.Value",
            pressureHarness,
            StringComparison.Ordinal);
        int profileRound = pressureHarness.IndexOf(
            "for (int profileOrdinal = 0;",
            StringComparison.Ordinal);
        int safeStart = pressureHarness.IndexOf(
            "Task<DockerWorker> safeStart = DockerWorker.StartAsync(",
            profileRound,
            StringComparison.Ordinal);
        int sampleRound = pressureHarness.IndexOf(
            "for (int sampleIndex = 0;",
            profileRound,
            StringComparison.Ordinal);
        Assert.True(profileRound >= 0);
        Assert.True(sampleRound > profileRound);
        Assert.True(safeStart > sampleRound);
        Assert.Contains(
            "crossProfileProcessState\"] = \"none\"",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "crossSampleProcessState\"] = \"none\"",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProfileIsolationPassed(",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "profileIsolation",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "CaptureBinaryIdentities(options.RepositoryRoot, commit)",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "SHA256.HashData(stream)",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "informationalCommit,\n                    expectedCommit",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "int warmupPercent = WarmupProfilePercent;",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int WarmupProfilePercent = 1000;",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "WarmupProfilePercent,\n            checked(",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int StressProfileSamples = 6;",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "ParseEvenPositiveInt(",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "result <= 0 || (result & 1) != 0",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal const int WarmupPassCount = 4;",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int MeasurementPreparationPassCount = 6;",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int TimedMeasurementRequestOrdinal = 11;",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PressurePreparationPolicy",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "PrepareMeasurementSeriesAsync(",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "attemptIndex < MeasurementPreparationPassCount",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "safePreparation.Attempts.Count\n                    == MeasurementPreparationPassCount",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "namPreparation.Attempts.Count\n                    == MeasurementPreparationPassCount",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "safeTimedRequestOrdinal\n                    != TimedMeasurementRequestOrdinal",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "namTimedRequestOrdinal\n                    != TimedMeasurementRequestOrdinal",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "WriteProfileGateFailureReportAsync(",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "return ProfileGateFailureExitCode;",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "DOTNET_TieredCompilation=0",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "DOTNET_TieredPGO=0",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "PressureCompilationPolicy.HasEquivalentDisabledTiering(",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "PressureProfileOrderPolicy.FollowsCanonicalOrder(",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int NonMeasuredOperationTimeoutSeconds = 60;",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "percent == StressProfilePercent",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "PrepareMeasurementAsync(",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContainerAbsentAfterDisposal",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            pressureHarness.Split(
                "lifecycle.CgroupIdentity",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "StateAfterReset",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "bool safeFirst = PressureSamplePolicy.SafeRunsFirst(\n"
                + "                    sampleIndex);",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sampleIndex + profileOrdinal",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "pairOrdinal++;",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "cycleOwners[cyclePosition]",
            File.ReadAllText(
                Path.Combine(
                    demoRoot,
                    "SharedContract",
                    "WorkerLocalPressureSession.cs")),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "options.ProfilePercents.Reverse()",
            pressureHarness,
            StringComparison.Ordinal);
        int processingReady = pressureHarness.IndexOf(
            "PressureProgressKind.ProcessingReady",
            StringComparison.Ordinal);
        int startTick = pressureHarness.IndexOf(
            "startTick = tick;",
            processingReady,
            StringComparison.Ordinal);
        int beginProcessing = pressureHarness.IndexOf(
            "PressureCommandKind.BeginProcessing",
            StringComparison.Ordinal);
        int sendBegin = pressureHarness.IndexOf(
            "WriteLineAsync(",
            startTick,
            StringComparison.Ordinal);
        Assert.True(processingReady >= 0);
        Assert.True(beginProcessing >= 0);
        Assert.True(startTick > processingReady);
        Assert.True(sendBegin > startTick);
        Assert.DoesNotContain(
            "PressureProgressKind.ProcessingStarted",
            pressureHarness,
            StringComparison.Ordinal);

        string protocol = File.ReadAllText(
            Path.Combine(
                demoRoot,
                "SharedContract",
                "Contracts",
                "PressureProtocolContract.cs"));
        Assert.Contains(
            "progress.Kind != PressureProgressKind.ProcessingReady",
            protocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "Console.ReadLine()",
            protocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "begin.Kind != PressureCommandKind.BeginProcessing",
            protocol,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ProcessingStarted",
            protocol,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "consumes every upload byte",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "AtomicPressureArtifactFile.WriteJsonAsync(",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "File.WriteAllTextAsync(",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Equal(
            4,
            pressureHarness.Split(
                "await CheckpointPreparationAsync(",
                StringSplitOptions.None).Length - 1);
        int addObservation = pressureHarness.IndexOf(
            "observations.Add(pair.Observation);",
            StringComparison.Ordinal);
        int pairCheckpoint = pressureHarness.IndexOf(
            "await checkpointWriter.WriteCheckpointAsync(",
            addObservation,
            StringComparison.Ordinal);
        Assert.True(addObservation >= 0);
        Assert.True(pairCheckpoint > addObservation);
        Assert.Equal(
            2,
            pressureHarness.Split(
                "await checkpointWriter.WriteFinalReportAsync(report);",
                StringSplitOptions.None).Length - 1);

        string runner = File.ReadAllText(
            Path.Combine(demoRoot, "Pressure", "run-constrained.ps1"));
        Assert.Contains(
            "[string]$Profiles = \"50,100,200,500,1000,10000\"",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "[int]$SamplesPerProfile = 6",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "[switch]$SetupOnly",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "Run the matrix with -SkipBuild -SkipImageBuild",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Diagnostics.FileVersionInfo]::GetVersionInfo(",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "The $component binary does not match HEAD $commit.",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Invoke-HarnessWatchdog",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "LatestCheckpointSha256",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "Write-AtomicJson $timeoutEvidencePath $timeoutEvidence",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "[switch]$ValidateTimeoutsOnly",
            runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EndToEndTimeoutSeconds",
            runner,
            StringComparison.Ordinal);
        int compilation = runner.IndexOf(
            "\"--compile-gate\"",
            StringComparison.Ordinal);
        int publish = runner.IndexOf(
            "\"publish\"",
            StringComparison.Ordinal);
        int pressure = runner.IndexOf(
            "\"--pressure-matrix\"",
            StringComparison.Ordinal);
        Assert.True(compilation >= 0);
        Assert.True(publish > compilation);
        Assert.True(pressure > publish);

        string guide = File.ReadAllText(
            Path.Combine(demoRoot, "README.md"));
        const string derivedImage =
            "$image = \"nam-voxel-pressure:$($commit.Substring(0, 12))\"";
        Assert.Equal(
            2,
            guide.Split(
                derivedImage,
                StringSplitOptions.None).Length - 1);
        Assert.Equal(
            2,
            guide.Split(
                "-Image $image",
                StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain(
            "-Image nam-voxel-pressure:",
            guide,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompilationGateUsesSixMeasuredPairsAndUnchangedLimits()
    {
        Assert.Equal(
            1,
            CompilationGatePolicy.DefaultWarmupPairCount);
        Assert.Equal(
            6,
            CompilationGatePolicy.DefaultMeasuredPairCount);
        Assert.True(
            CompilationGatePolicy.IsValidMeasuredPairCount(6));
        Assert.False(
            CompilationGatePolicy.IsValidMeasuredPairCount(0));
        Assert.False(
            CompilationGatePolicy.IsValidMeasuredPairCount(5));
        Assert.Equal(
            1.10,
            CompilationGatePolicy.MaximumNamToSafeRatio);
        Assert.True(
            CompilationGatePolicy.IsWithinRatioLimit(1.10));
        Assert.False(
            CompilationGatePolicy.IsWithinRatioLimit(1.100_001));

        string root = FindRepositoryRoot();
        string runner = File.ReadAllText(
            Path.Combine(
                root,
                ".Demos",
                "01-VoxelChunkPipeline",
                "Pressure",
                "run-constrained.ps1"));
        Assert.Contains(
            "[int]$CompilationPairs = 6",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "$CompilationPairs -le 0",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "($CompilationPairs -band 1) -ne 0",
            runner,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompilationGateMeasuredOrderIsBalanced()
    {
        PressureCompilationConfiguration configuration =
            CompilationGatePolicy.RequiredChildCompilationConfiguration;
        List<CompilationSample> samples = [];
        for (int pair = 0;
            pair < CompilationGatePolicy.DefaultMeasuredPairCount;
            pair++)
        {
            bool safeFirst =
                CompilationGatePolicy.SafeRunsFirst(pair);
            samples.Add(
                CompilationSampleForTest(
                    pair,
                    0,
                    safeFirst ? "SafeCSharp" : "NAM",
                    configuration));
            samples.Add(
                CompilationSampleForTest(
                    pair,
                    1,
                    safeFirst ? "NAM" : "SafeCSharp",
                    configuration));
        }

        Assert.True(
            CompilationGatePolicy.HasBalancedMeasuredOrder(
                samples,
                CompilationGatePolicy.DefaultMeasuredPairCount));
        Assert.Equal(
            3,
            samples.Count(
                sample =>
                    sample.Position == 0
                    && sample.Implementation == "SafeCSharp"));
        Assert.Equal(
            3,
            samples.Count(
                sample =>
                    sample.Position == 0
                    && sample.Implementation == "NAM"));

        samples[0] = samples[0] with
        {
            Implementation = "NAM"
        };
        Assert.False(
            CompilationGatePolicy.HasBalancedMeasuredOrder(
                samples,
                CompilationGatePolicy.DefaultMeasuredPairCount));
    }

    [Fact]
    public void CompilationGateRequiresDisabledEqualChildSettings()
    {
        PressureCompilationConfiguration disabled =
            CompilationGatePolicy.RequiredChildCompilationConfiguration;
        List<CompilationSample> warmups =
        [
            CompilationSampleForTest(0, 0, "SafeCSharp", disabled),
            CompilationSampleForTest(0, 1, "NAM", disabled)
        ];
        List<CompilationSample> samples = [];
        for (int pair = 0;
            pair < CompilationGatePolicy.DefaultMeasuredPairCount;
            pair++)
        {
            samples.Add(
                CompilationSampleForTest(
                    pair,
                    0,
                    "SafeCSharp",
                    disabled));
            samples.Add(
                CompilationSampleForTest(
                    pair,
                    1,
                    "NAM",
                    disabled));
        }

        Assert.True(
            CompilationGatePolicy.HasRequiredChildConfiguration(
                warmups,
                samples,
                CompilationGatePolicy.DefaultWarmupPairCount,
                CompilationGatePolicy.DefaultMeasuredPairCount));
        string json = JsonSerializer.Serialize(
            samples[0],
            VoxelJson.Options);
        CompilationSample roundTrip =
            JsonSerializer.Deserialize<CompilationSample>(
                json,
                VoxelJson.Options);
        Assert.Equal(
            "0",
            roundTrip.ChildCompilationConfiguration.TieredCompilation);
        Assert.Equal(
            "0",
            roundTrip.ChildCompilationConfiguration.TieredPgo);

        samples[0] = samples[0] with
        {
            ChildCompilationConfiguration =
                new PressureCompilationConfiguration("1", "0")
        };
        Assert.False(
            CompilationGatePolicy.HasRequiredChildConfiguration(
                warmups,
                samples,
                CompilationGatePolicy.DefaultWarmupPairCount,
                CompilationGatePolicy.DefaultMeasuredPairCount));

        samples[0] = samples[0] with
        {
            ChildCompilationConfiguration =
                new PressureCompilationConfiguration(string.Empty, "0")
        };
        Assert.False(
            CompilationGatePolicy.HasRequiredChildConfiguration(
                warmups,
                samples,
                CompilationGatePolicy.DefaultWarmupPairCount,
                CompilationGatePolicy.DefaultMeasuredPairCount));
    }

    [Fact]
    public void CompilationWarmupFailureBlocksTheCompleteGate()
    {
        PressureCompilationConfiguration configuration =
            CompilationGatePolicy.RequiredChildCompilationConfiguration;
        List<CompilationSample> warmups =
        [
            CompilationSampleForTest(
                0,
                0,
                "SafeCSharp",
                configuration),
            CompilationSampleForTest(
                0,
                1,
                "NAM",
                configuration)
        ];
        List<CompilationSample> measured = [];
        for (int pair = 0;
            pair < CompilationGatePolicy.DefaultMeasuredPairCount;
            pair++)
        {
            bool safeFirst =
                CompilationGatePolicy.SafeRunsFirst(pair);
            measured.Add(
                CompilationSampleForTest(
                    pair,
                    0,
                    safeFirst ? "SafeCSharp" : "NAM",
                    configuration));
            measured.Add(
                CompilationSampleForTest(
                    pair,
                    1,
                    safeFirst ? "NAM" : "SafeCSharp",
                    configuration));
        }

        Assert.True(
            CompilationGatePolicy.HasCompletedMeasuredCompilations(
                measured,
                CompilationGatePolicy.DefaultMeasuredPairCount));

        warmups[0] = warmups[0] with
        {
            CompilerElapsedMilliseconds = null,
            Outcome = CompilationOutcome.BuildFailure,
            ExitCode = 1
        };
        bool warmupGate =
            CompilationGatePolicy.HasValidWarmups(
                warmups,
                CompilationGatePolicy.DefaultWarmupPairCount);

        Assert.False(warmupGate);
        Assert.False(
            CompilationGatePolicy.CanStartMeasuredBuilds(
                warmupGate));
        Assert.False(
            CompilationGatePolicy.AllCompilationsCompleted(
                warmupGate,
                measuredCompilationsCompleted: true));
        Assert.False(
            CompilationGatePolicy.AllGatesPassed(
                warmupGate,
                childConfigurationGatePassed: true,
                measuredOrderGatePassed: true,
                compilerGatePassed: true,
                wallGatePassed: true));
    }

    [Fact]
    public void CompilationWarmupPolicyRejectsMalformedPairsAndResults()
    {
        PressureCompilationConfiguration configuration =
            CompilationGatePolicy.RequiredChildCompilationConfiguration;
        CompilationSample safe = CompilationSampleForTest(
            0,
            0,
            "SafeCSharp",
            configuration);
        CompilationSample nam = CompilationSampleForTest(
            0,
            1,
            "NAM",
            configuration);

        Assert.True(
            CompilationGatePolicy.HasValidWarmups(
                [safe, nam],
                CompilationGatePolicy.DefaultWarmupPairCount));
        Assert.False(
            CompilationGatePolicy.HasValidWarmups(
                [safe],
                CompilationGatePolicy.DefaultWarmupPairCount));
        Assert.False(
            CompilationGatePolicy.HasValidWarmups(
                [safe, nam],
                warmupPairCount: 0));
        Assert.False(
            CompilationGatePolicy.HasValidWarmups(
                [safe with { Pair = 1 }, nam],
                CompilationGatePolicy.DefaultWarmupPairCount));
        Assert.False(
            CompilationGatePolicy.HasValidWarmups(
                [safe, nam with { Position = 0 }],
                CompilationGatePolicy.DefaultWarmupPairCount));
        Assert.False(
            CompilationGatePolicy.HasValidWarmups(
                [
                    safe with { Implementation = "NAM" },
                    nam with { Implementation = "SafeCSharp" }
                ],
                CompilationGatePolicy.DefaultWarmupPairCount));
        Assert.False(
            CompilationGatePolicy.HasValidWarmups(
                [
                    safe with
                    {
                        CompilerElapsedMilliseconds = null
                    },
                    nam
                ],
                CompilationGatePolicy.DefaultWarmupPairCount));
        Assert.False(
            CompilationGatePolicy.HasValidWarmups(
                [safe with { ExitCode = 1 }, nam],
                CompilationGatePolicy.DefaultWarmupPairCount));
    }

    [Fact]
    public void IncorrectSafeOutputNeverBecomesDecisiveNamResult()
    {
        PressureOutcomeDecision decision = PressureOutcomePolicy.Evaluate(
            200,
            safeCompleted: false,
            namCompleted: true,
            pairedParityPassed: false,
            completedPairOutputMismatch: false,
            [PressureProfileOutcome.IncorrectOutput],
            [PressureProfileOutcome.Completed]);

        Assert.True(decision.SafeOutputIncorrect);
        Assert.False(decision.SafeResourceFailure);
        Assert.False(decision.DecisiveNam);
        Assert.False(decision.CorrectnessGatePassed);
        Assert.False(decision.DeadlineGatePassed);
    }

    [Fact]
    public void SafeResourceFailureCanRemainDecisiveNamResult()
    {
        PressureOutcomeDecision decision = PressureOutcomePolicy.Evaluate(
            200,
            safeCompleted: false,
            namCompleted: true,
            pairedParityPassed: false,
            completedPairOutputMismatch: false,
            [PressureProfileOutcome.OutOfMemory],
            [PressureProfileOutcome.Completed]);

        Assert.True(decision.SafeResourceFailure);
        Assert.True(decision.DecisiveNam);
        Assert.True(decision.CorrectnessGatePassed);
        Assert.True(decision.DeadlineGatePassed);
    }

    [Fact]
    public void DeadlineAppliesToEachPressureSample()
    {
        PressureImplementationObservation completed =
            default(PressureImplementationObservation) with
            {
                Outcome = PressureProfileOutcome.Completed,
                CorrectnessPassed = true,
                DeadlineMilliseconds = 6000,
                ProfileElapsedMilliseconds = 3100
            };
        PressureImplementationObservation[] observations =
            Enumerable.Repeat(completed, 5).ToArray();

        Assert.True(
            observations.Sum(
                static observation =>
                    observation.ProfileElapsedMilliseconds!.Value)
                > completed.DeadlineMilliseconds);
        Assert.True(
            PressureOutcomePolicy.AllSamplesCompletedWithinDeadline(
                observations));

        observations[4] = completed with
        {
            ProfileElapsedMilliseconds = 6000.1
        };
        Assert.False(
            PressureOutcomePolicy.AllSamplesCompletedWithinDeadline(
                observations));
    }

    [Fact]
    public void PressureSampleOrderRequiresAnEvenSplit()
    {
        PressurePairedObservation safeFirst =
            default(PressurePairedObservation) with
            {
                SafeRanFirst = true
            };
        PressurePairedObservation namFirst =
            default(PressurePairedObservation) with
            {
                SafeRanFirst = false
            };

        Assert.True(
            PressureSamplePolicy.HasBalancedOrder(
                [
                    safeFirst,
                    namFirst,
                    safeFirst,
                    namFirst,
                    safeFirst,
                    namFirst
                ]));
        Assert.False(
            PressureSamplePolicy.HasBalancedOrder(
                [safeFirst, namFirst, safeFirst, namFirst, safeFirst]));
        Assert.False(
            PressureSamplePolicy.HasBalancedOrder(
                [
                    safeFirst,
                    safeFirst,
                    safeFirst,
                    safeFirst,
                    namFirst,
                    namFirst
                ]));
    }

    [Fact]
    public void ProfileOrdinalDoesNotChangeImplementationOrder()
    {
        bool[] firstProfileOrder = CreateProfileOrder(
            profileOrdinal: 0);
        bool[] laterProfileOrder = CreateProfileOrder(
            profileOrdinal: 5);

        Assert.Equal(firstProfileOrder, laterProfileOrder);
        Assert.Equal(
            [true, false, true, false, true, false],
            firstProfileOrder);
    }

    [Fact]
    public void StressConfidenceRequiresSixPairsAndPositiveLowerBound()
    {
        Assert.True(
            PressureSamplePolicy.StressConfidencePassed(
                6,
                6,
                1.01));
        Assert.False(
            PressureSamplePolicy.StressConfidencePassed(
                6,
                6,
                1.00));
        Assert.False(
            PressureSamplePolicy.StressConfidencePassed(
                5,
                6,
                1.20));
    }

    [Fact]
    public void ProfileOrderPolicyAcceptsOnlyCanonicalSubsets()
    {
        int[] canonical = [50, 100, 200, 500, 1000, 10000];

        Assert.True(
            PressureProfileOrderPolicy.FollowsCanonicalOrder(
                [1000, 10000],
                canonical));
        Assert.False(
            PressureProfileOrderPolicy.FollowsCanonicalOrder(
                [10000, 1000],
                canonical));
        Assert.False(
            PressureProfileOrderPolicy.FollowsCanonicalOrder(
                [1000, 1000],
                canonical));
    }

    [Fact]
    public void PreparationMetadataUsesEqualFixedCountsAndOrdinalEleven()
    {
        PressureImplementationObservation[] attempts =
            new PressureImplementationObservation[6];
        PressureMeasurementPreparation preparation = new(
            1000,
            2_684_354_560,
            60,
            default,
            default,
            true,
            true,
            attempts,
            attempts,
            6,
            11,
            11);

        string json = JsonSerializer.Serialize(
            preparation,
            VoxelJson.Options);
        PressureMeasurementPreparation roundTrip =
            JsonSerializer.Deserialize<PressureMeasurementPreparation>(
                json,
                VoxelJson.Options);

        Assert.Equal(6, roundTrip.SafeAttempts?.Count);
        Assert.Equal(6, roundTrip.NamAttempts?.Count);
        Assert.Equal(6, roundTrip.RequiredAttemptCount);
        Assert.Equal(11, roundTrip.SafeTimedRequestOrdinal);
        Assert.Equal(11, roundTrip.NamTimedRequestOrdinal);
        Assert.True(roundTrip.SafeTimedRequestStarted);
        Assert.True(roundTrip.NamTimedRequestStarted);
    }

    [Fact]
    public void CompilationPolicyRequiresEqualDisabledTiering()
    {
        PressureRuntimeSnapshot disabled =
            default(PressureRuntimeSnapshot) with
            {
                CompilationConfiguration =
                    new PressureCompilationConfiguration("0", "0")
            };
        PressureRuntimeSnapshot enabled = disabled with
        {
            CompilationConfiguration =
                new PressureCompilationConfiguration("1", "1")
        };
        PressureRuntimeSnapshot missing = disabled with
        {
            CompilationConfiguration =
                new PressureCompilationConfiguration(string.Empty, string.Empty)
        };
        PressureRuntimeSnapshot unequal = disabled with
        {
            CompilationConfiguration =
                new PressureCompilationConfiguration("0", "1")
        };

        Assert.True(
            PressureCompilationPolicy.HasEquivalentDisabledTiering(
                disabled,
                disabled));
        Assert.False(
            PressureCompilationPolicy.HasEquivalentDisabledTiering(
                enabled,
                disabled));
        Assert.False(
            PressureCompilationPolicy.HasEquivalentDisabledTiering(
                disabled,
                enabled));
        Assert.False(
            PressureCompilationPolicy.HasEquivalentDisabledTiering(
                missing,
                disabled));
        Assert.False(
            PressureCompilationPolicy.HasEquivalentDisabledTiering(
                disabled,
                missing));
        Assert.False(
            PressureCompilationPolicy.HasEquivalentDisabledTiering(
                disabled,
                unequal));

        string json = JsonSerializer.Serialize(disabled, VoxelJson.Options);
        PressureRuntimeSnapshot roundTrip =
            JsonSerializer.Deserialize<PressureRuntimeSnapshot>(
                json,
                VoxelJson.Options);
        Assert.Equal(
            "0",
            roundTrip.CompilationConfiguration.TieredCompilation);
        Assert.Equal(
            "0",
            roundTrip.CompilationConfiguration.TieredPgo);
    }

    [Fact]
    public void EnforcedProfileFailureStopsProfileContinuation()
    {
        PressurePairedStatistics failed = default;
        PressurePairedStatistics passed = failed with
        {
            GatePassed = true
        };

        Assert.False(
            PressureProfileContinuationPolicy.CanStartNextProfile(
                enforce: true,
                failed));
        Assert.True(
            PressureProfileContinuationPolicy.CanStartNextProfile(
                enforce: true,
                passed));
        Assert.True(
            PressureProfileContinuationPolicy.CanStartNextProfile(
                enforce: false,
                failed));
    }

    [Fact]
    public void GateFailurePathReturnsBeforeVerificationOrAnotherProfile()
    {
        string root = FindRepositoryRoot();
        string harness = File.ReadAllText(
            Path.Combine(
                root,
                ".Demos",
                "01-VoxelChunkPipeline",
                "Harness",
                "PressureMatrixHarness.cs"));
        int profileLoop = harness.IndexOf(
            "for (int profileOrdinal = 0;",
            StringComparison.Ordinal);
        int gateCheck = harness.IndexOf(
            "if (!PressureProfileContinuationPolicy.CanStartNextProfile(",
            profileLoop,
            StringComparison.Ordinal);
        int terminalWrite = harness.IndexOf(
            "await WriteProfileGateFailureReportAsync(",
            gateCheck,
            StringComparison.Ordinal);
        int terminalReturn = harness.IndexOf(
            "return ProfileGateFailureExitCode;",
            terminalWrite,
            StringComparison.Ordinal);
        int verification = harness.IndexOf(
            "int verificationPercent =",
            terminalReturn,
            StringComparison.Ordinal);

        Assert.True(profileLoop >= 0);
        Assert.True(gateCheck > profileLoop);
        Assert.True(terminalWrite > gateCheck);
        Assert.True(terminalReturn > terminalWrite);
        Assert.True(verification > terminalReturn);
    }

    [Fact]
    public async Task AtomicCheckpointReplacementKeepsOnlyTheLatestDocument()
    {
        string directory = CreateTestArtifactDirectory();
        string path = Path.Combine(directory, "checkpoint.json");
        try
        {
            PressureMatrixCheckpoint first =
                CreatePressureCheckpoint(sequence: 1);
            PressureMatrixCheckpoint second = first with
            {
                Sequence = 2,
                UpdatedUtc = DateTime.UnixEpoch.AddSeconds(2)
            };

            await AtomicPressureArtifactFile.WriteJsonAsync(path, first);
            await AtomicPressureArtifactFile.WriteJsonAsync(path, second);

            PressureMatrixCheckpoint roundTrip =
                JsonSerializer.Deserialize<PressureMatrixCheckpoint>(
                    await File.ReadAllTextAsync(path),
                    VoxelJson.Options);
            Assert.Equal(2, roundTrip.Sequence);
            Assert.Single(Directory.EnumerateFiles(directory));
            Assert.Empty(
                Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            DeleteTestArtifactDirectory(directory);
        }
    }

    [Fact]
    public void PreparationCheckpointKeepsAllAttemptsAndLiveWorkerState()
    {
        double[] elapsed = [10, 9, 10, 9, 10, 9];
        PressureImplementationObservation[] attempts = elapsed
            .Select(
                milliseconds =>
                    default(PressureImplementationObservation) with
                    {
                        Implementation = "NAM",
                        ProfilePercent = 10000,
                        ProfileElapsedMilliseconds = milliseconds
                    })
            .ToArray();
        PressurePreparationSeriesCheckpoint preparation = new(
            3,
            1,
            false,
            "NAM",
            10000,
            26_843_545_600,
            57,
            attempts,
            6,
            true);
        PressurePreparationSeriesCheckpoint safePreparation =
            preparation with
            {
                SafeRanFirst = true,
                Implementation = "SafeCSharp"
            };
        PressureImplementationObservation safeObservation =
            default(PressureImplementationObservation) with
            {
                Implementation = "SafeCSharp",
                ProfilePercent = 10000,
                ProfileElapsedMilliseconds = 20
            };
        PressureWorkerCheckpoint worker = new(
            "NAM",
            "nam-container",
            "container-id",
            42,
            "/cgroup/nam",
            default,
            default,
            10,
            true);
        PressureMatrixCheckpoint checkpoint =
            CreatePressureCheckpoint(sequence: 4) with
            {
                CurrentPair = new PressureCurrentPairCheckpoint(
                    3,
                    1,
                    false,
                    safeObservation,
                    null,
                    true,
                    false,
                    [safePreparation, preparation]),
                ActiveWorkers = [worker]
            };

        string json = JsonSerializer.Serialize(
            checkpoint,
            VoxelJson.Options);
        PressureMatrixCheckpoint roundTrip =
            JsonSerializer.Deserialize<PressureMatrixCheckpoint>(
                json,
                VoxelJson.Options);

        Assert.Equal(2, roundTrip.CurrentPair?.Preparations.Count);
        Assert.Equal(
            attempts.Length,
            roundTrip.CurrentPair?.Preparations[1].Attempts.Count);
        Assert.Equal(
            6,
            roundTrip.CurrentPair?.Preparations[1].RequiredAttemptCount);
        Assert.True(
            roundTrip.CurrentPair?.Preparations[1].Complete);
        Assert.Equal(
            "SafeCSharp",
            roundTrip.CurrentPair?.SafeObservation?.Implementation);
        PressureWorkerCheckpoint active = Assert.Single(
            roundTrip.ActiveWorkers);
        Assert.Equal(10, active.CurrentRequestOrdinal);
        Assert.True(active.IsAlive);
    }

    [Fact]
    public async Task CompletedPairCheckpointSurvivesSimulatedTermination()
    {
        string directory = CreateTestArtifactDirectory();
        string path = Path.Combine(directory, "interrupted.json");
        try
        {
            PressureProfileInitialization initialization = new(
                2,
                DateTime.UnixEpoch,
                120,
                "safe-container",
                "nam-container",
                4,
                1000,
                2_684_354_560);
            PressurePairedObservation observation = new(
                0,
                default,
                default,
                true,
                true);
            PressureCurrentProfileCheckpoint currentProfile = new(
                10000,
                268_435_456,
                26_843_545_600,
                [observation],
                [initialization]);
            PressureWorkerLifecycle lifecycle = new(
                "SafeCSharp",
                "safe-container",
                "safe-id",
                41,
                "/cgroup/safe",
                default,
                1,
                11,
                11,
                true,
                true,
                DateTime.UnixEpoch);
            PressureMatrixCheckpoint checkpoint =
                CreatePressureCheckpoint(sequence: 5) with
                {
                    CurrentProfile = currentProfile,
                    Commands = ["docker run safe"],
                    CompletedLifecycles = [lifecycle]
                };

            await AtomicPressureArtifactFile.WriteJsonAsync(
                path,
                checkpoint);

            PressureMatrixCheckpoint recovered =
                JsonSerializer.Deserialize<PressureMatrixCheckpoint>(
                    await File.ReadAllTextAsync(path),
                    VoxelJson.Options);
            Assert.Equal("commit", recovered.GitCommit);
            Assert.Equal("image", recovered.ImageId);
            Assert.Single(recovered.BinaryIdentities);
            Assert.Single(
                recovered.CurrentProfile?.CompletedPairs ?? []);
            Assert.Single(
                recovered.CurrentProfile?.Initializations ?? []);
            Assert.Equal(
                "docker run safe",
                Assert.Single(recovered.Commands));
            Assert.True(
                Assert.Single(
                    recovered.CompletedLifecycles)
                    .ContainerAbsentAfterDisposal);
        }
        finally
        {
            DeleteTestArtifactDirectory(directory);
        }
    }

    [Fact]
    public async Task FinalReportAtomicallyReplacesTheCheckpoint()
    {
        string directory = CreateTestArtifactDirectory();
        string path = Path.Combine(directory, "final.json");
        try
        {
            PressureMatrixCheckpoint checkpoint =
                CreatePressureCheckpoint(sequence: 6);
            await AtomicPressureArtifactFile.WriteJsonAsync(
                path,
                checkpoint);
            PressurePairedStatistics statistics = new(
                SampleCount: 6,
                MeanSpeedup: 1.9769,
                ConfidenceLower95: 1.3992,
                ConfidenceUpper95: 2.5546,
                SafeMeanMillisecondsPerGiB: 150,
                NamMeanMillisecondsPerGiB: 75,
                SafeP95MillisecondsPerGiB: 160,
                SafeP99MillisecondsPerGiB: 165,
                NamP95MillisecondsPerGiB: 80,
                NamP99MillisecondsPerGiB: 82,
                PressureQualified: true,
                DeadlineGatePassed: true,
                CorrectnessGatePassed: true,
                PerformanceGatePassed: false,
                GatePassed: false,
                Interpretation: "The profile failed its mean gate.");
            PressureProfilePair failedProfile = new(
                1000,
                268_435_456,
                2_684_354_560,
                default,
                [],
                statistics);
            PressureProfileGateFailure failure = new(
                1000,
                6,
                2.00,
                1.00,
                statistics);
            PressureWorkerLifecycle lifecycle = new(
                "SafeCSharp",
                "safe-container",
                "safe-id",
                41,
                "/cgroup/safe",
                default,
                1,
                11,
                11,
                true,
                true,
                DateTime.UnixEpoch);
            PressureMatrixGateFailureReport report = new(
                "final-commit",
                "final-image",
                checkpoint.BinaryIdentities,
                checkpoint.Options,
                [failedProfile],
                failedProfile,
                failure,
                ["docker run safe", "docker run nam"],
                [lifecycle],
                new PressureMatrixCleanupState(
                    1,
                    1,
                    true,
                    true,
                    true,
                    0),
                new PressureCurrentProfileCheckpoint(
                    1000,
                    268_435_456,
                    2_684_354_560,
                    [],
                    []),
                null,
                DateTime.UnixEpoch,
                DateTime.UnixEpoch.AddSeconds(1),
                500,
                250,
                1000);

            await AtomicPressureArtifactFile.WriteJsonAsync(path, report);

            string json = await File.ReadAllTextAsync(path);
            PressureMatrixGateFailureReport roundTrip =
                JsonSerializer.Deserialize<PressureMatrixGateFailureReport>(
                    json,
                    VoxelJson.Options);
            Assert.Equal("final-commit", roundTrip.GitCommit);
            Assert.Equal(1.9769, roundTrip.Failure.Actual.MeanSpeedup);
            Assert.Equal(2.00, roundTrip.Failure.RequiredMeanSpeedup);
            Assert.Equal(1, roundTrip.Cleanup.RecordedWorkerLifecycleCount);
            Assert.True(roundTrip.Cleanup.LifecycleCountPassed);
            Assert.True(roundTrip.Cleanup.EveryContainerAbsent);
            Assert.Equal(2, roundTrip.Commands.Count);
            Assert.Single(roundTrip.WorkerLifecycles);
            Assert.Single(roundTrip.BinaryIdentities);
            Assert.Equal(
                1000,
                roundTrip.PreservedCurrentProfile?.ProfilePercent);
            Assert.DoesNotContain(
                "\"formatVersion\"",
                json,
                StringComparison.Ordinal);
            Assert.Empty(
                Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            DeleteTestArtifactDirectory(directory);
        }
    }

    [Fact]
    public void WrapperTimeoutValidationRejectsUnsafeBounds()
    {
        string root = FindRepositoryRoot();
        string script = Path.Combine(
            root,
            ".Demos",
            "01-VoxelChunkPipeline",
            "Pressure",
            "run-constrained.ps1");
        (int validExitCode, string validOutput, _) =
            RunPowerShell(
                script,
                "-ValidateTimeoutsOnly",
                "-Profiles",
                "1000,10000",
                "-SamplesPerProfile",
                "6",
                "-InactivityTimeoutSeconds",
                "120");
        (int inactivityExitCode, _, string inactivityError) =
            RunPowerShell(
                script,
                "-ValidateTimeoutsOnly",
                "-Profiles",
                "1000,10000",
                "-SamplesPerProfile",
                "6",
                "-InactivityTimeoutSeconds",
                "60");
        (int absoluteExitCode, _, string absoluteError) =
            RunPowerShell(
                script,
                "-ValidateTimeoutsOnly",
                "-Profiles",
                "1000,10000",
                "-SamplesPerProfile",
                "6",
                "-InactivityTimeoutSeconds",
                "120",
                "-AbsoluteFailSafeTimeoutSeconds",
                "1");

        Assert.Equal(0, validExitCode);
        using JsonDocument valid = JsonDocument.Parse(validOutput);
        Assert.True(valid.RootElement.GetProperty("Valid").GetBoolean());
        Assert.True(
            valid.RootElement
                .GetProperty("AbsoluteFailSafeTimeoutSeconds")
                .GetInt64()
            >= valid.RootElement
                .GetProperty("MinimumAbsoluteFailSafeTimeoutSeconds")
                .GetInt64());
        Assert.NotEqual(0, inactivityExitCode);
        Assert.Contains(
            "must exceed the 60-second internal operation bound",
            inactivityError,
            StringComparison.Ordinal);
        Assert.NotEqual(0, absoluteExitCode);
        Assert.Contains(
            "below the derived minimum",
            absoluteError,
            StringComparison.Ordinal);
    }

    private static bool[] CreateProfileOrder(int profileOrdinal)
    {
        Assert.True(profileOrdinal >= 0);
        return Enumerable.Range(0, 6)
            .Select(PressureSamplePolicy.SafeRunsFirst)
            .ToArray();
    }

    [Fact]
    public void CompletedOutputMismatchBlocksAResourceFailureResult()
    {
        PressureOutcomeDecision decision = PressureOutcomePolicy.Evaluate(
            200,
            safeCompleted: false,
            namCompleted: true,
            pairedParityPassed: false,
            completedPairOutputMismatch: true,
            [
                PressureProfileOutcome.Completed,
                PressureProfileOutcome.OutOfMemory
            ],
            [
                PressureProfileOutcome.Completed,
                PressureProfileOutcome.Completed
            ]);

        Assert.True(decision.CompletedPairOutputMismatch);
        Assert.True(decision.SafeResourceFailure);
        Assert.False(decision.DecisiveNam);
        Assert.False(decision.CorrectnessGatePassed);
    }

    [Fact]
    public void ReleaseDocumentsUseTheDesignatedContactAndAbsoluteGuideLink()
    {
        string root = FindRepositoryRoot();
        string license = File.ReadAllText(
            Path.Combine(root, "LICENSE.md"));
        string readme = File.ReadAllText(
            Path.Combine(root, "README.md"));

        Assert.Contains(
            "Contact: supprocom@mkn8rn.com",
            license,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Contact: mkn8rn@hotmail.com",
            license,
            StringComparison.Ordinal);
        Assert.Contains(
            "[voxel-guide]: https://github.com/Supprocom/"
                + "NativeAllocationManagement/blob/main/"
                + ".Demos/01-VoxelChunkPipeline/README.md",
            readme,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "](.Demos/01-VoxelChunkPipeline/README.md)",
            readme,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseDocumentsDescribeBuilderAndTransferableOwnership()
    {
        string root = FindRepositoryRoot();
        string guide = File.ReadAllText(
            Path.Combine(root, "docs", "getting-started.md"));
        Assert.Contains(
            "Version=\"0.1.2\"",
            guide,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeTransfer<uint>.Move(ref source)",
            guide,
            StringComparison.Ordinal);
        Assert.Contains(
            "Do not use application `in`, `ref`, or `out NativeTransfer<T>` parameters.",
            guide,
            StringComparison.Ordinal);
        Assert.Contains(
            "--native-builder --samples 10",
            guide,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeBuilder<uint> builder = pool.CreateBuilder(",
            guide,
            StringComparison.Ordinal);
        Assert.Contains(
            "preLease: 64",
            guide,
            StringComparison.Ordinal);
        Assert.Contains(
            "`NAM1028` rejects ownership copies.",
            guide,
            StringComparison.Ordinal);
        Assert.Contains(
            "`NAM1034` requires `Complete`",
            guide,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PackageMetadataUsesBuilderPatchVersion()
    {
        string root = FindRepositoryRoot();
        string project = File.ReadAllText(
            Path.Combine(
                root,
                "Supprocom.NativeAllocationManagement",
                "Supprocom.NativeAllocationManagement.csproj"));

        Assert.Contains(
            "<Version>0.1.2</Version>",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "growable native builders",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "cross-thread transferable leases",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "native-builder",
            project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationReportStoresSelectedProfileAndActualWarmupSeparately()
    {
        const long capBytes = 268_435_456;
        PressureProfileInitialization initialization = new(
            6,
            DateTime.UnixEpoch,
            0,
            "safe",
            "nam",
            4,
            1000,
            capBytes * 10);
        PressureVerificationPair verification = new(
            10000,
            capBytes * 100,
            1000,
            capBytes * 10,
            initialization,
            default,
            default,
            true);
        PressureMatrixReport report = new(
            "commit",
            "image",
            [
                new PressureBinaryIdentity(
                    "Harness",
                    "harness.dll",
                    "HASH",
                    "1.0.0+commit",
                    "commit")
            ],
            capBytes,
            "bytes",
            6000,
            1,
            1,
            1,
            1,
            [10000],
            [],
            verification,
            new PressureMatrixSummary(
                DateTime.UnixEpoch,
                DateTime.UnixEpoch,
                0,
                0,
                0,
                0,
                0,
                true,
                true,
                true,
                true,
                true,
                true),
            new Dictionary<string, string>(),
            [],
            []);

        string json = JsonSerializer.Serialize(report, VoxelJson.Options);
        PressureMatrixReport roundTrip = JsonSerializer.Deserialize<PressureMatrixReport>(
            json,
            VoxelJson.Options);

        Assert.Equal(10000, roundTrip.Verification.ProfilePercent);
        Assert.Equal(1000, roundTrip.Verification.WarmupProfilePercent);
        Assert.Equal(capBytes * 10, roundTrip.Verification.WarmupCumulativeDemandBytes);
        Assert.Equal(1000, roundTrip.Verification.Initialization.WarmupProfilePercent);
        Assert.Equal(4, roundTrip.Verification.Initialization.WarmupPasses);
        Assert.Equal(capBytes * 10, roundTrip.Verification.Initialization.WarmupCumulativeDemandBytes);
        Assert.Equal(capBytes * 100, roundTrip.Verification.RequestedCumulativeDemandBytes);
        PressureBinaryIdentity identity = Assert.Single(
            roundTrip.BinaryIdentities);
        Assert.Equal("Harness", identity.Component);
        Assert.Equal("HASH", identity.Sha256);
        Assert.Equal("commit", identity.InformationalCommit);
    }

    [Fact]
    public void PressureQualificationIsInformationalAndDoesNotBlockTheGate()
    {
        PressureMatrixSummary summary = new(
            DateTime.UnixEpoch,
            DateTime.UnixEpoch,
            0,
            0,
            0,
            1,
            0,
            true,
            true,
            true,
            false,
            true,
            true);

        Assert.False(summary.PressureQualificationPassed);
        Assert.True(summary.GatePassed);
    }

    [Fact]
    public void MeasuredProfilesDoNotCreatePerChunkEvidence()
    {
        string root = FindRepositoryRoot();
        string demoRoot = Path.Combine(
            root,
            ".Demos",
            "01-VoxelChunkPipeline");
        string safeSource = File.ReadAllText(
            Path.Combine(
                demoRoot,
                "SafeCSharp",
                "SafePressureSession.cs"));
        string nativeSource = File.ReadAllText(
            Path.Combine(
                demoRoot,
                "NAM",
                "NativePressureSession.cs"));
        string workerSource = File.ReadAllText(
            Path.Combine(
                demoRoot,
                "SharedContract",
                "WorkerLocalPressureSession.cs"));
        string harnessSource = File.ReadAllText(
            Path.Combine(
                demoRoot,
                "Harness",
                "PressureMatrixHarness.cs"));

        Assert.Contains(
            "List<PressureChunkEvidence>? evidence = exactVerification",
            safeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Evidence = request.RequiresExactVerification",
            nativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "bool verification = request.RequiresExactVerification;",
            workerSource,
            StringComparison.Ordinal);
        int measuredHandoffStart = nativeSource.IndexOf(
            "internal void CompleteScatterHandoff()",
            StringComparison.Ordinal);
        int exactHandoffStart = nativeSource.IndexOf(
            "private void CompleteScatterHandoff(",
            StringComparison.Ordinal);
        Assert.True(measuredHandoffStart >= 0);
        Assert.True(exactHandoffStart > measuredHandoffStart);
        string measuredNativeHandoff = nativeSource[
            measuredHandoffStart..exactHandoffStart];
        Assert.DoesNotContain(
            "CreateChunkEvidence",
            measuredNativeHandoff,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DescribeOutput",
            measuredNativeHandoff,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompletedLogicalBytes + BatchDemand",
            measuredNativeHandoff,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_slots[batchIndex]",
            measuredNativeHandoff,
            StringComparison.Ordinal);
        Assert.Contains(
            "PressureChunkEvidence[] evidence = verification",
            workerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "executions.All(ResultMatchesWorkerPlan)",
            workerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "left.ChunkEvidence.Count == 0",
            harnessSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "left.ChunkEvidence.Count != 0",
            harnessSource,
            StringComparison.Ordinal);
        Assert.NotNull(
            typeof(PressureProfileResult).GetProperty(
                nameof(PressureProfileResult.ExecutionMode)));
    }

    [Fact]
    public void BothImplementationsSelectRuntimeCapacityWithinSharedBounds()
    {
        string root = FindRepositoryRoot();
        string safeSource = File.ReadAllText(Path.Combine(
            root,
            ".Demos",
            "01-VoxelChunkPipeline",
            "SafeCSharp",
            "SafePressureSession.cs"));
        string nativeSource = File.ReadAllText(Path.Combine(
            root,
            ".Demos",
            "01-VoxelChunkPipeline",
            "NAM",
            "NativePressureSession.cs"));
        string sharedSource = File.ReadAllText(Path.Combine(
            root,
            ".Demos",
            "01-VoxelChunkPipeline",
            "SharedContract",
            "PressureWorkContract.cs"));

        Assert.Contains(
            "PressureWorkContract.CanonicalRetainedArraysPerPoolBucket",
            safeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "TrackedArrayPool<",
            safeSource,
            StringComparison.Ordinal);
        Assert.Contains("CanAdmitBatch(", safeSource, StringComparison.Ordinal);
        Assert.Contains(
            "retainedBudgetBytes",
            safeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "PressureWorkContract.CanonicalResidentCellCapacity",
            safeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "PressureWorkContract.CanonicalResidentFaceRecordCapacity",
            safeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MappedGpuBuffer",
            safeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_mappedUploadStream",
            safeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CopyStagesToMappedUpload(",
            safeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "PressureWorkContract.PackStream(",
            safeSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PackAliasedScatterStream(",
            safeSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConsumeGpuUpload(",
            safeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateNativeCapacityPlan(",
            nativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "request.CgroupCapBytes",
            nativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "OutputCapacityPlan.Create(",
            nativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "phaseArena.ReserveExternalMemory(",
            nativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MappedGpuBuffer",
            nativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeLeaseSourceQuadSpanInitializer<",
            nativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "PressureWorkContract.PackAliasedScatterStream(",
            nativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "PayloadPatternTableBytes",
            nativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompleteScatterHandoff(",
            nativeSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PressureWorkContract.PackStream(",
            nativeSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConsumeGpuUpload(",
            nativeSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConsumeScatterGpuUpload(",
            nativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "outputCapacity.EnsureFits(_context)",
            nativeSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PressureWorkContract.CanonicalArenaReservationBytes",
            nativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "PressureChunkShape shape) =>",
            sharedSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_measurementConsumerSink",
            sharedSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConsumeGpuUpload(",
            sharedSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalInputIncludesTheRegistryOrderAndCompleteCellHashes()
    {
        VoxelMath.ValidateCanonicalInputFixture();
        VoxelMath.ValidateHandAuthoredInputFixture();
        VoxelWorkloadOptions options = VoxelWorkloadOptions.Default with
        {
            ChunkCount = 2,
            WorkerCount = 2,
            Iterations = 3,
            WarmupChunksPerWorker = 0
        };
        CanonicalInputContract contract = VoxelMath.ComputeCanonicalInput(options);
        Assert.Equal(options, contract.Options);
        Assert.Equal(VoxelMath.BlockTypes, contract.Registry);
        Assert.Equal((long)2 * 3 * VoxelMath.CellsPerChunk, contract.CellCount);
        Assert.NotEqual(0, contract.CellValueByteHash);
        Assert.NotEqual(0, contract.ByteHash);
        Assert.NotEqual(0, contract.ChunkOrderHash);
        Assert.Equal(64, contract.StrongHash.Length);
        Assert.NotEqual(contract.ByteHash, VoxelMath.ComputeCanonicalInput(options with { Seed = options.Seed + 1 }).ByteHash);
    }

    [Fact]
    public void CorrectnessInputCanCarryEveryPreMutationCellAndItsCanonicalHash()
    {
        VoxelWorkloadOptions options = VoxelWorkloadOptions.Default with
        {
            ChunkCount = 1,
            WorkerCount = 1,
            Iterations = 1,
            WarmupChunksPerWorker = 0
        };
        CanonicalInputContract contract = VoxelMath.ComputeCanonicalInput(options, includeCells: true);

        CanonicalInputCell[] cells = Assert.IsType<CanonicalInputCell[]>(contract.Cells);
        Assert.Equal(contract.CellCount, cells.LongLength);
        long cellHash = 17;
        for (int index = 0; index < cells.Length; index++)
        {
            cellHash = VoxelMath.DigestCanonicalInputCell(cellHash, cells[index]);
        }
        Assert.Equal(contract.CellValueByteHash, cellHash);
        Assert.Equal(0, cells[0].CellIndex);
        Assert.Equal(options.ChunkCount * options.Iterations - 1, cells[^1].ChunkId);
    }

    [Fact]
    public void ObservedInputUsesEveryMeasuredChunkAndStrongOutputHashCoversAllStreams()
    {
        VoxelWorkloadOptions options = VoxelWorkloadOptions.Default with
        {
            ChunkCount = 2,
            Iterations = 2,
            WorkerCount = 1,
            WarmupChunksPerWorker = 0
        };
        CanonicalInputContract expected = VoxelMath.ComputeCanonicalInput(options);
        List<ChunkOutputSummary> chunks = [];
        for (int chunk = 0; chunk < options.ChunkCount * options.Iterations; chunk++)
        {
            CanonicalInputCell[] cells = new CanonicalInputCell[VoxelMath.CellsPerChunk];
            for (int cell = 0; cell < cells.Length; cell++)
            {
                cells[cell] = VoxelMath.CreateCanonicalInputCell(options.Seed, chunk, cell);
            }

            chunks.Add(new ChunkOutputSummary(
                chunk,
                17,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                InputCellCount: cells.Length,
                InputCells: cells,
                StrongInputHash: VoxelMath.ComputeStrongCanonicalInputChunkHash(options.Seed, chunk, cells)));
        }

        CanonicalInputContract observed = VoxelMath.CreateObservedCanonicalInput(options, chunks);
        Assert.Equal(expected.StrongHash, observed.StrongHash);
        Assert.Equal(expected.CellCount, observed.CellCount);
        Assert.Equal(expected.CellCount, observed.Cells!.LongLength);
        Assert.True(observed.Observed);
    }

    [Fact]
    public void StrongOutputHashIncludesEveryMaterializedElementAndByte()
    {
        OutputFixture fixture = VoxelMath.ExpectedIndependentFixture;
        string original = VoxelMath.ComputeStrongOutputHash(
            fixture.OpaqueVertices,
            fixture.OpaqueIndices,
            fixture.OpaqueSlices,
            fixture.OpaqueUpload,
            fixture.TransparentVertices,
            fixture.TransparentIndices,
            fixture.TransparentSlices,
            fixture.TransparentUpload);
        byte[] changedUpload = fixture.TransparentUpload.ToArray();
        changedUpload[^1] ^= 0x01;
        string changed = VoxelMath.ComputeStrongOutputHash(
            fixture.OpaqueVertices,
            fixture.OpaqueIndices,
            fixture.OpaqueSlices,
            fixture.OpaqueUpload,
            fixture.TransparentVertices,
            fixture.TransparentIndices,
            fixture.TransparentSlices,
            changedUpload);
        Assert.Equal(64, original.Length);
        Assert.NotEqual(original, changed);
    }

    [Fact]
    public void HandAuthoredOutputFixtureIsCompleteAndCoversBothStreams()
    {
        OutputFixture fixture = VoxelMath.ExpectedIndependentFixture;
        Assert.Equal(20, fixture.OpaqueVertices.Length);
        Assert.Equal(30, fixture.OpaqueIndices.Length);
        Assert.Equal(5, fixture.OpaqueSlices.Length);
        Assert.True(fixture.OpaqueUpload.Length > 512);
        Assert.Equal(16, fixture.TransparentVertices.Length);
        Assert.Equal(24, fixture.TransparentIndices.Length);
        Assert.Equal(4, fixture.TransparentSlices.Length);
        Assert.True(fixture.TransparentUpload.Length > 512);
        Assert.Equal(new[] { 0, 1, 2, 3 }, fixture.OpaqueVertices.Select(value => value.Corner).Distinct().OrderBy(value => value));
        Assert.Equal(new[] { 0, 1, 2, 3 }, fixture.TransparentVertices.Select(value => value.Corner).Distinct().OrderBy(value => value));
        Assert.Contains(fixture.OpaqueSlices, slice => slice.Alignment > 1 && slice.Length > slice.Alignment);
        Assert.Contains(fixture.TransparentSlices, slice => slice.StageMask != 0 && slice.BlockId > 255);
    }

    [Fact]
    public void GpuStageInlineArraysHaveExactRecordSizes()
    {
        Assert.Equal(160, Unsafe.SizeOf<GpuStage160>());
        Assert.Equal(168, Unsafe.SizeOf<GpuStage168>());
        Assert.Equal(176, Unsafe.SizeOf<GpuStage176>());
        Assert.Equal(192, Unsafe.SizeOf<GpuStage192>());
        Assert.Equal(224, Unsafe.SizeOf<GpuStage224>());
    }

    [Fact]
    public void MappedGpuBufferSupportsSafeStreamAccess()
    {
        byte[] expected = new byte[4096];
        for (int index = 0; index < expected.Length; index++)
        {
            expected[index] = unchecked((byte)(index * 17));
        }

        using MappedGpuBuffer buffer = new(
            checked((nuint)expected.Length));
        using UnmanagedMemoryStream stream =
            buffer.OpenStream();
        stream.Write(expected);
        stream.Position = 0;
        byte[] actual = new byte[expected.Length];
        stream.ReadExactly(actual);

        Assert.Equal(expected, actual);
        Assert.Equal(
            checked((ulong)expected.Length),
            buffer.ByteLength);
    }

    [Theory]
    [InlineData(256, 160)]
    [InlineData(261, 168)]
    [InlineData(257, 176)]
    [InlineData(258, 192)]
    [InlineData(259, 224)]
    public void RetainedPressureVerificationRejectsChangedStageBytes(
        int blockId,
        int expectedStageBytes)
    {
        const int seed = 17;
        BlockTypeDescriptor type = VoxelMath.BlockTypeForId(blockId);
        int stageBytes = VoxelMath.AlignUp(
            checked(
                type.PayloadBytes
                + VoxelMath.VerticesPerFace * VoxelMath.VertexBytes
                + VoxelMath.IndicesPerFace * VoxelMath.IndexBytes
                + PressureWorkContract.GpuCommandPaddingBytesPerFace),
            type.Alignment);
        Assert.Equal(expectedStageBytes, stageBytes);
        FaceRecord[] records =
        [
            new FaceRecord(
                0,
                type.Id,
                1,
                type.PayloadBytes,
                type.Alignment,
                type.StageMask,
                stageBytes)
        ];
        Vertex[] vertices = new Vertex[VoxelMath.VerticesPerFace];
        int[] indices = new int[VoxelMath.IndicesPerFace];
        PayloadSlice[] slices = new PayloadSlice[1];
        GpuStage160[] stage160 =
            stageBytes == 160 ? new GpuStage160[1] : [];
        GpuStage168[] stage168 =
            stageBytes == 168 ? new GpuStage168[1] : [];
        GpuStage176[] stage176 =
            stageBytes == 176 ? new GpuStage176[1] : [];
        GpuStage192[] stage192 =
            stageBytes == 192 ? new GpuStage192[1] : [];
        GpuStage224[] stage224 =
            stageBytes == 224 ? new GpuStage224[1] : [];
        GpuStageBuffers stages = new(
            stage160,
            stage168,
            stage176,
            stage192,
            stage224);
        GpuStageBuffers emptyStages = new(
            [],
            [],
            [],
            [],
            []);
        PressureWorkContract.PackStream(
            seed,
            records,
            vertices,
            indices,
            slices,
            stages);
        PressureOutputEvidence evidence =
            PressureWorkContract.VerifyAndHashOutput(
                seed,
                records,
                vertices,
                indices,
                slices,
                [],
                [],
                [],
                [],
                stages,
                emptyStages);
        GpuStageShape stageShape = default;
        stageShape = stageShape.Add(
            stageBytes,
            faceCount: 1,
            payloadBytesPerFace: type.PayloadBytes,
            opaque: true);
        PressureChunkShape shape = new(
            OpaqueRecordCount: 1,
            TransparentRecordCount: 0,
            OpaqueFaceCount: 1,
            TransparentFaceCount: 0,
            stageShape,
            TransparentMaskCount: 0,
            TransparentMaskWords: 0,
            EmptySections: 0,
            UniformSections: 0,
            ExpandedSections: 0,
            PackedSections: 0,
            MultiPackedSections: 0,
            DominantTransparentSections: 0,
            ResidualTransparentSections: 0,
            SectionDescriptorCount: 0,
            SectionValueCount: 0,
            SectionWordCount: 0,
            SectionStateWordCount: 0);
        Assert.Equal(
            evidence with
            {
                CompleteHash = string.Empty
            },
            PressureWorkContract.DescribeOutput(shape));
        PressureWorkContract.VerifyRetainedOutput(
            evidence,
            seed,
            records,
            vertices,
            indices,
            slices,
            [],
            [],
            [],
            [],
            stages,
            emptyStages);

        Vertex[] scatterVertices =
            new Vertex[VoxelMath.VerticesPerFace];
        int[] scatterIndices =
            new int[VoxelMath.IndicesPerFace];
        PayloadSlice[] scatterSlices =
            new PayloadSlice[1];
        byte[] scatterPatterns =
            PressureWorkContract.PayloadPatternTable.ToArray();
        PressureWorkContract.PackAliasedScatterStream(
            records,
            scatterVertices,
            scatterIndices,
            scatterSlices);
        PressureOutputEvidence scatterEvidence =
            PressureWorkContract.VerifyAndHashScatterOutput(
                seed,
                scatterPatterns,
                records,
                scatterVertices,
                scatterIndices,
                scatterSlices,
                [],
                [],
                [],
                []);
        Assert.Equal(evidence, scatterEvidence);
        PressureWorkContract.VerifyRetainedScatterOutput(
            scatterEvidence,
            seed,
            scatterPatterns,
            records,
            scatterVertices,
            scatterIndices,
            scatterSlices,
            [],
            [],
            [],
            []);

        int payloadSeed = unchecked(
            seed
            + records[0].CellIndex * 11
            + records[0].BlockId * 37
            + records[0].StageMask * 13);
        int payloadPatternOffset =
            PressureWorkContract.PayloadPatternOffset(
                payloadSeed,
                records[0].StageMask);
        scatterPatterns[payloadPatternOffset] ^= 1;
        Assert.Throws<InvalidDataException>(
            () => PressureWorkContract.VerifyRetainedScatterOutput(
                scatterEvidence,
                seed,
                scatterPatterns,
                records,
                scatterVertices,
                scatterIndices,
                scatterSlices,
                [],
                [],
                [],
                []));
        scatterPatterns[payloadPatternOffset] ^= 1;
        scatterVertices[0] = scatterVertices[0] with
        {
            X = scatterVertices[0].X + 1
        };
        Assert.Throws<InvalidDataException>(
            () => PressureWorkContract.VerifyRetainedScatterOutput(
                scatterEvidence,
                seed,
                scatterPatterns,
                records,
                scatterVertices,
                scatterIndices,
                scatterSlices,
                [],
                [],
                [],
                []));
        scatterVertices[0] = scatterVertices[0] with
        {
            X = scatterVertices[0].X - 1
        };
        scatterIndices[0]++;
        Assert.Throws<InvalidDataException>(
            () => PressureWorkContract.VerifyRetainedScatterOutput(
                scatterEvidence,
                seed,
                scatterPatterns,
                records,
                scatterVertices,
                scatterIndices,
                scatterSlices,
                [],
                [],
                [],
                []));
        scatterIndices[0]--;
        scatterSlices[0] = scatterSlices[0] with
        {
            Length = scatterSlices[0].Length - 1
        };
        Assert.Throws<InvalidDataException>(
            () => PressureWorkContract.VerifyRetainedScatterOutput(
                scatterEvidence,
                seed,
                scatterPatterns,
                records,
                scatterVertices,
                scatterIndices,
                scatterSlices,
                [],
                [],
                [],
                []));

        stages.GetAllBytes(stageBytes)[^1] ^= 1;
        bool failed = false;
        try
        {
            PressureWorkContract.VerifyRetainedOutput(
                evidence,
                seed,
                records,
                vertices,
                indices,
                slices,
                [],
                [],
                [],
                [],
                stages,
                emptyStages);
        }
        catch (InvalidDataException)
        {
            failed = true;
        }

        Assert.True(failed);
    }

    [Fact]
    public void AliasedScatterPackingRejectsARecordThatExceedsOutputRanges()
    {
        FaceRecord[] records =
        [
            new FaceRecord(
                CellIndex: 0,
                BlockId: 259,
                Mask: 1,
                PayloadBytes: 0,
                Alignment: 1,
                StageMask: 1,
                StageBytes: 160)
        ];

        Assert.Throws<InvalidDataException>(
            () => PressureWorkContract.PackAliasedScatterStream(
                records,
                [],
                [],
                []));
    }

    [Fact]
    public void PressureCapacityCoversThePredeclaredCanonicalChunkRange()
    {
        VoxelCell[] cells = new VoxelCell[VoxelMath.CellsPerChunk];
        SectionSummary[] sections = new SectionSummary[VoxelMath.SectionsPerChunk];
        PressureChunkShape[] shapes = new PressureChunkShape[
            PressureWorkContract.CanonicalPressureCorpusLength];
        int maximumFaces = 0;
        int maximumRecords = 0;
        int maximumMaskWords = 0;
        int maximumUpload = 0;
        long maximumRetained = 0;
        for (int chunk = 0; chunk < shapes.Length; chunk++)
        {
            PressureWorkContract.GenerateCells(
                PressureWorkContract.CanonicalPressureSeed,
                chunk,
                cells);
            PressureChunkShape shape = PressureWorkContract.DeriveChunkShape(cells, sections);
            shapes[chunk] = shape;
            PressureWorkContract.EnsurePressureCapacity(shape);
            maximumRecords = Math.Max(maximumRecords, shape.RecordCount);
            maximumFaces = Math.Max(maximumFaces, shape.FaceCount);
            maximumMaskWords = Math.Max(
                maximumMaskWords,
                shape.TransparentMaskWords);
            maximumUpload = Math.Max(maximumUpload, shape.UploadBytes);
            maximumRetained = Math.Max(
                maximumRetained,
                PressureWorkContract.CalculateRetainedLogicalBytes(shape));
        }

        Assert.Equal(
            PressureWorkContract.MaximumFaceRecordsPerChunk,
            maximumRecords);
        Assert.Equal(
            PressureWorkContract.MaximumVisibleFacesPerChunk,
            maximumFaces);
        Assert.Equal(
            PressureWorkContract.MaximumTransparentMaskWordsPerChunk,
            maximumMaskWords);
        Assert.Equal(
            PressureWorkContract.MaximumUploadBytesPerChunk,
            maximumUpload);
        Assert.Equal(
            PressureWorkContract.MaximumRetainedBytesPerChunk,
            maximumRetained);

        AssertCanonicalResidentCapacities(shapes);
    }

    [Fact]
    public void CanonicalPressureChunkMaterializesEveryVoxelEngineSectionKind()
    {
        VoxelCell[] cells = new VoxelCell[VoxelMath.CellsPerChunk];
        SectionSummary[] summaries =
            new SectionSummary[VoxelMath.SectionsPerChunk];
        PressureWorkContract.GenerateCells(17, 0, cells);
        PressureChunkShape shape =
            PressureWorkContract.DeriveChunkShape(cells, summaries);

        Assert.True(shape.EmptySections > 0);
        Assert.True(shape.UniformSections > 0);
        Assert.True(shape.ExpandedSections > 0);
        Assert.True(shape.PackedSections > 0);
        Assert.True(shape.MultiPackedSections > 0);
        Assert.Equal(
            Enum.GetValues<SectionRepresentationKind>().Order(),
            summaries.Select(static value => value.Kind).Distinct().Order());
        Assert.Equal(
            VoxelMath.SectionsPerChunk,
            shape.SectionDescriptorCount);
        Assert.True(shape.SectionValueCount > VoxelMath.CellsPerSection);
        Assert.True(shape.SectionWordCount > 0);
        Assert.True(shape.SectionStateWordCount > 0);
    }

    [Fact]
    public void TransparentMasksUseTheSectionRepresentationCoordinateOrder()
    {
        const int x = 1;
        const int y = 2;
        const int z = 3;
        const ushort transparentBlockId = 257;
        int cellIndex =
            (z * VoxelMath.ChunkDimension + y)
                * VoxelMath.ChunkDimension
            + x;
        int localIndex =
            (z * VoxelMath.SectionDimension + x)
                * VoxelMath.SectionDimension
            + y;
        VoxelCell[] cells = new VoxelCell[VoxelMath.CellsPerChunk];
        ushort[] materials = new ushort[VoxelMath.CellsPerChunk];
        short[] densities = new short[VoxelMath.CellsPerChunk];
        cells[cellIndex].BlockId = transparentBlockId;
        materials[cellIndex] = transparentBlockId;
        ulong[] fromCells =
            new ulong[VoxelMath.TransparentMaskWordsPerId];
        ulong[] fromArrays =
            new ulong[VoxelMath.TransparentMaskWordsPerId];

        Assert.Equal(
            1,
            VoxelMath.BuildTransparentMasks(
                cells,
                sectionIndex: 0,
                fromCells));
        Assert.Equal(
            1,
            VoxelMath.BuildTransparentMasks(
                materials,
                densities,
                sectionIndex: 0,
                fromArrays));
        Assert.Equal(fromCells, fromArrays);
        Assert.Equal(
            1UL << (localIndex & 63),
            fromCells[localIndex >> 6]);
        Assert.Equal(1, fromCells.Count(static value => value != 0));
    }

    [Fact]
    public void TransparentMaskSlotsFollowStableMaterialOrder()
    {
        (int Index, ushort Id)[] transparentTypes =
            VoxelMath.BlockTypes
                .Select(
                    static (type, index) =>
                        (Index: index, Id: checked((ushort)type.Id)))
                .Where(
                    static type =>
                        VoxelMath.TransparentById[type.Id])
                .Take(2)
                .ToArray();
        Assert.Equal(2, transparentTypes.Length);
        (int lowerIndex, ushort lowerId) = transparentTypes[0];
        (int higherIndex, ushort higherId) =
            transparentTypes[1];
        VoxelCell[] cells = new VoxelCell[VoxelMath.CellsPerChunk];
        cells[0].BlockId = higherId;
        cells[1].BlockId = lowerId;
        ulong transparentTypeMask =
            (1UL << lowerIndex) | (1UL << higherIndex);
        ulong[] masks = new ulong[
            2 * VoxelMath.TransparentMaskWordsPerId];

        Assert.Equal(
            2,
            VoxelMath.BuildTransparentMasks(
                cells,
                sectionIndex: 0,
                transparentTypeMask,
                masks));

        Assert.Equal(1UL << VoxelMath.SectionDimension, masks[0]);
        Assert.Equal(
            1UL,
            masks[VoxelMath.TransparentMaskWordsPerId]);
        Assert.Equal(2, masks.Count(static value => value != 0));
    }

    [Fact]
    public void SeparateAndFusedSectionBuildersProduceIdenticalData()
    {
        VoxelCell[] cells = new VoxelCell[VoxelMath.CellsPerChunk];
        SectionSummary[] summaries =
            new SectionSummary[VoxelMath.SectionsPerChunk];
        PressureWorkContract.GenerateCells(17, 0, cells);
        PressureChunkShape shape =
            PressureWorkContract.DeriveChunkShape(cells, summaries);

        SectionPrerenderDescriptor[] separateDescriptors =
            new SectionPrerenderDescriptor[
                shape.SectionDescriptorCount];
        ushort[] separateValues =
            new ushort[shape.SectionValueCount];
        uint[] separateWords =
            new uint[shape.SectionWordCount];
        ulong[] separateStates =
            new ulong[shape.SectionStateWordCount];
        ulong[] separateMasks =
            new ulong[shape.TransparentMaskWords];
        PressureWorkContract.BuildSectionRepresentations(
            cells,
            summaries,
            separateDescriptors,
            separateValues,
            separateWords,
            separateStates);
        PressureWorkContract.BuildTransparentMasks(
            cells,
            summaries,
            separateMasks);

        SectionPrerenderDescriptor[] fusedDescriptors =
            new SectionPrerenderDescriptor[
                shape.SectionDescriptorCount];
        ushort[] fusedValues =
            new ushort[shape.SectionValueCount];
        uint[] fusedWords =
            new uint[shape.SectionWordCount];
        ulong[] fusedStates =
            new ulong[shape.SectionStateWordCount];
        ulong[] fusedMasks =
            new ulong[shape.TransparentMaskWords];
        PressureWorkContract.BuildSectionRepresentations(
            cells,
            summaries,
            fusedDescriptors,
            fusedValues,
            fusedWords,
            fusedStates,
            fusedMasks);

        Assert.Equal(separateDescriptors, fusedDescriptors);
        Assert.Equal(separateValues, fusedValues);
        Assert.Equal(separateWords, fusedWords);
        Assert.Equal(separateStates, fusedStates);
        Assert.Equal(separateMasks, fusedMasks);
        Assert.All(
            summaries,
            static summary =>
                Assert.Equal(
                    summary.TransparentIds,
                    VoxelMath.BlockTypes
                        .Select(
                            static (_, index) => index)
                        .Count(
                            index =>
                                (summary.TransparentTypeMask
                                    & (1UL << index)) != 0)));
    }

    [Fact]
    public void SectionBuilderCoversEveryVisibleCellAndMaskBit()
    {
        const int seed = 17;
        const int chunkId = 0;
        VoxelCell[] cells = new VoxelCell[VoxelMath.CellsPerChunk];
        SectionSummary[] summaries =
            new SectionSummary[VoxelMath.SectionsPerChunk];
        PressureWorkContract.GenerateCells(seed, chunkId, cells);
        PressureChunkShape shape =
            PressureWorkContract.DeriveChunkShape(cells, summaries);
        SectionPrerenderDescriptor[] descriptors =
            new SectionPrerenderDescriptor[shape.SectionDescriptorCount];
        ushort[] values = new ushort[shape.SectionValueCount];
        uint[] words = new uint[shape.SectionWordCount];
        ulong[] states = new ulong[shape.SectionStateWordCount];
        FaceRecord[] records =
            new FaceRecord[Math.Max(1, shape.RecordCount)];
        ulong[] masks = new ulong[shape.TransparentMaskWords];
        PressureWorkContract.BuildSectionRepresentations(
            cells,
            summaries,
            descriptors,
            values,
            words,
            states,
            masks);

        PressureWorkContract.PopulateFaceRecords(
            cells,
            descriptors,
            shape,
            records);

        HashSet<int> recordCells = [];
        for (int index = 0; index < shape.RecordCount; index++)
        {
            FaceRecord record = records[index];
            Assert.True(recordCells.Add(record.CellIndex));
            Assert.Equal(cells[record.CellIndex].BlockId, record.BlockId);
            Assert.Equal(cells[record.CellIndex].FaceMask, record.Mask);
            Assert.Equal(
                index >= shape.OpaqueRecordCount,
                VoxelMath.TransparentById[record.BlockId]);
        }

        Assert.Equal(
            cells.Count(static cell => cell.FaceMask != 0),
            recordCells.Count);
        int maskOffset = 0;
        Span<ulong> union =
            stackalloc ulong[
                PressureWorkContract.OccupancyWordsPerSection];
        for (int sectionIndex = 0;
            sectionIndex < descriptors.Length;
            sectionIndex++)
        {
            SectionSummary summary = summaries[sectionIndex];
            SectionPrerenderDescriptor descriptor =
                descriptors[sectionIndex];
            union.Clear();
            for (int maskIndex = 0;
                maskIndex < summary.TransparentIds;
                maskIndex++)
            {
                ReadOnlySpan<ulong> mask = masks.AsSpan(
                    maskOffset
                        + maskIndex
                            * VoxelMath.TransparentMaskWordsPerId,
                    VoxelMath.TransparentMaskWordsPerId);
                Assert.Contains(mask.ToArray(), static value => value != 0);
                for (int word = 0; word < mask.Length; word++)
                {
                    Assert.Equal(0UL, union[word] & mask[word]);
                    union[word] |= mask[word];
                }
            }

            if (summary.TransparentCount > 0)
            {
                Assert.Equal(
                    states.AsSpan(
                        descriptor.TransparentBitsOffset,
                        PressureWorkContract
                            .OccupancyWordsPerSection).ToArray(),
                    union.ToArray());
            }
            else
            {
                Assert.DoesNotContain(
                    union.ToArray(),
                    static value => value != 0);
            }

            maskOffset += checked(
                summary.TransparentIds
                    * VoxelMath.TransparentMaskWordsPerId);
        }

        Assert.Equal(masks.Length, maskOffset);
        string maskHash =
            PressureWorkContract.HashTransparentMasks(chunkId, masks);
        masks[0] ^= 1;
        Assert.NotEqual(
            maskHash,
            PressureWorkContract.HashTransparentMasks(chunkId, masks));
    }

    [Fact]
    public void CanonicalPressureStreamContainsDistinctChunkAndSectionClasses()
    {
        VoxelCell[] cells = new VoxelCell[VoxelMath.CellsPerChunk];
        SectionSummary[] summaries =
            new SectionSummary[VoxelMath.SectionsPerChunk];
        PressureChunkShape[] shapes = new PressureChunkShape[8];
        for (int chunk = 0; chunk < shapes.Length; chunk++)
        {
            PressureWorkContract.GenerateCells(
                PressureWorkContract.CanonicalPressureSeed,
                chunk,
                cells);
            shapes[chunk] =
                PressureWorkContract.DeriveChunkShape(cells, summaries);
        }

        Assert.Equal(6, shapes[1].EmptySections);
        Assert.Equal(7, shapes[2].UniformSections);
        Assert.Equal(8, shapes[3].PackedSections);
        Assert.Equal(8, shapes[4].ExpandedSections);
        Assert.Equal(8, shapes[5].MultiPackedSections);
        Assert.Equal(8, shapes[6].MultiPackedSections);
        Assert.Equal(8, shapes[7].MultiPackedSections);
        Assert.True(shapes.Min(static shape => shape.UploadBytes) < 200_000);
        Assert.True(shapes.Max(static shape => shape.UploadBytes) > 6_000_000);
        Assert.Equal(
            8,
            shapes.Select(static shape => shape.UploadBytes).Distinct().Count());
    }

    [Fact]
    public void PressureCorpusRepeatsOneHeterogeneousCycle()
    {
        int cycleLength =
            PressureWorkContract.CanonicalPressureCycleLength;
        int[] firstCycle = Enumerable.Range(0, cycleLength)
            .Select(
                PressureWorkContract.PressureSourceChunkIndex)
            .ToArray();

        Assert.Equal(16, cycleLength);
        Assert.Equal(
            8,
            firstCycle
                .Select(static source => source & 7)
                .Distinct()
                .Count());
        for (int archetype = 0; archetype < 8; archetype++)
        {
            Assert.Contains(
                firstCycle,
                source => (source & 7) == archetype);
        }

        for (int chunk = 0;
            chunk
                < PressureWorkContract
                    .CanonicalPressureCorpusLength;
            chunk++)
        {
            Assert.Equal(
                firstCycle[chunk % cycleLength],
                PressureWorkContract.PressureSourceChunkIndex(
                    chunk));
        }
    }

    [Fact]
    public void CanonicalPressureProfilesAddCompleteEqualCycles()
    {
        const long capBytes = 268_435_456;
        int cycleLength =
            PressureWorkContract.CanonicalPressureCycleLength;
        PressureChunkPlanEntry[] largest =
            PressureWorkContract.CreateCanonicalChunkPlan(
                PressureWorkContract.CanonicalPressureSeed,
                capBytes * 100,
                minimumChunks: 0);
        PressureChunkPlanEntry[] firstCycle =
            largest[..cycleLength];
        long cycleDemand = firstCycle.Sum(
            static chunk => chunk.LogicalDemandBytes);

        Assert.InRange(
            cycleDemand,
            capBytes / 2,
            capBytes / 2 + capBytes / 200);
        foreach (int percent in new[]
            {
                50,
                100,
                200,
                500,
                1000,
                10000
            })
        {
            int cycleCount = percent / 50;
            PressureChunkPlanEntry[] plan =
                PressureWorkContract.CreateCanonicalChunkPlan(
                    PressureWorkContract.CanonicalPressureSeed,
                    checked(capBytes * percent / 100),
                    minimumChunks: 0);

            Assert.Equal(
                checked(cycleCount * cycleLength),
                plan.Length);
            Assert.Equal(
                checked(cycleDemand * cycleCount),
                plan.Sum(
                    static chunk =>
                        chunk.LogicalDemandBytes));
            for (int index = 0; index < plan.Length; index++)
            {
                PressureChunkPlanEntry expected =
                    firstCycle[index % cycleLength];
                Assert.Equal(
                    expected.LogicalDemandBytes,
                    plan[index].LogicalDemandBytes);
                Assert.Equal(
                    expected.EstimatedWorkUnits,
                    plan[index].EstimatedWorkUnits);
                Assert.Equal(
                    expected.Shape,
                    plan[index].Shape);
            }
        }
    }

    [Fact]
    public void SectionPrerenderEvidenceCoversEveryTypedValueAndRetainedMutation()
    {
        const int seed = 17;
        const int chunkId = 0;
        VoxelCell[] cells = new VoxelCell[VoxelMath.CellsPerChunk];
        SectionSummary[] summaries =
            new SectionSummary[VoxelMath.SectionsPerChunk];
        PressureWorkContract.GenerateCells(seed, chunkId, cells);
        PressureChunkShape shape =
            PressureWorkContract.DeriveChunkShape(cells, summaries);
        SectionPrerenderDescriptor[] descriptors =
            new SectionPrerenderDescriptor[shape.SectionDescriptorCount];
        ushort[] values = new ushort[shape.SectionValueCount];
        uint[] words = new uint[shape.SectionWordCount];
        ulong[] states = new ulong[shape.SectionStateWordCount];
        ulong[] masks = new ulong[shape.TransparentMaskWords];

        string evidence =
            PressureWorkContract.BuildAndVerifySectionRepresentations(
                chunkId,
                cells,
                summaries,
                descriptors,
                values,
                words,
                states,
                masks);
        Assert.Equal(64, evidence.Length);
        Assert.Equal(
            evidence,
            PressureWorkContract.VerifyAndHashSectionRepresentations(
                chunkId,
                cells,
                summaries,
                descriptors,
                values,
                words,
                states));

        SectionPrerenderDescriptor[] changedDescriptors =
            descriptors.ToArray();
        changedDescriptors[0] = changedDescriptors[0] with
        {
            EmptyCount = changedDescriptors[0].EmptyCount - 1
        };
        Assert.Throws<InvalidDataException>(() =>
            PressureWorkContract.VerifyAndHashSectionRepresentations(
                chunkId,
                cells,
                summaries,
                changedDescriptors,
                values,
                words,
                states));

        ushort[] changedValues = values.ToArray();
        changedValues[0] ^= 1;
        Assert.Throws<InvalidDataException>(() =>
            PressureWorkContract.VerifyAndHashSectionRepresentations(
                chunkId,
                cells,
                summaries,
                descriptors,
                changedValues,
                words,
                states));

        uint[] changedWords = words.ToArray();
        changedWords[0] ^= 1;
        Assert.Throws<InvalidDataException>(() =>
            PressureWorkContract.VerifyAndHashSectionRepresentations(
                chunkId,
                cells,
                summaries,
                descriptors,
                values,
                changedWords,
                states));

        ulong[] changedStates = states.ToArray();
        changedStates[0] ^= 1;
        Assert.Throws<InvalidDataException>(() =>
            PressureWorkContract.VerifyAndHashSectionRepresentations(
                chunkId,
                cells,
                summaries,
                descriptors,
                values,
                words,
                changedStates));
    }

    [Fact]
    public void SeparateSectionRangesMatchTheCanonicalContiguousLayout()
    {
        const int seed = 17;
        const int chunkId = 0;
        VoxelCell[] cells = new VoxelCell[VoxelMath.CellsPerChunk];
        SectionSummary[] summaries =
            new SectionSummary[VoxelMath.SectionsPerChunk];
        PressureWorkContract.GenerateCells(seed, chunkId, cells);
        PressureChunkShape shape =
            PressureWorkContract.DeriveChunkShape(cells, summaries);
        SectionPrerenderDescriptor[] expectedDescriptors =
            new SectionPrerenderDescriptor[
                shape.SectionDescriptorCount];
        ushort[] expectedValues =
            new ushort[shape.SectionValueCount];
        uint[] expectedWords =
            new uint[shape.SectionWordCount];
        ulong[] expectedStates =
            new ulong[shape.SectionStateWordCount];
        ulong[] expectedMasks =
            new ulong[shape.TransparentMaskWords];
        PressureWorkContract.BuildSectionRepresentations(
            cells,
            summaries,
            expectedDescriptors,
            expectedValues,
            expectedWords,
            expectedStates,
            expectedMasks);

        SectionPrerenderDescriptor[] actualDescriptors =
            new SectionPrerenderDescriptor[
                shape.SectionDescriptorCount];
        ushort[][] values =
            new ushort[VoxelMath.SectionsPerChunk][];
        uint[][] words =
            new uint[VoxelMath.SectionsPerChunk][];
        ulong[][] states =
            new ulong[VoxelMath.SectionsPerChunk][];
        ulong[][] masks =
            new ulong[VoxelMath.SectionsPerChunk][];
        int valueCursor = 0;
        int wordCursor = 0;
        int stateCursor = 0;
        for (int section = 0;
            section < VoxelMath.SectionsPerChunk;
            section++)
        {
            PressureWorkContract.GetSectionStorageLengths(
                summaries[section],
                out int valueLength,
                out int wordLength,
                out int stateLength);
            values[section] = new ushort[valueLength];
            words[section] = new uint[wordLength];
            states[section] = new ulong[stateLength];
            masks[section] = new ulong[
                summaries[section].TransparentIds
                    * VoxelMath.TransparentMaskWordsPerId];
            actualDescriptors[section] =
                PressureWorkContract.BuildSectionRepresentation(
                    cells,
                    section,
                    summaries[section],
                    values[section],
                    words[section],
                    states[section],
                    masks[section],
                    ref valueCursor,
                    ref wordCursor,
                    ref stateCursor);
        }

        Assert.Equal(expectedDescriptors, actualDescriptors);
        Assert.Equal(
            expectedValues,
            values.SelectMany(static range => range));
        Assert.Equal(
            expectedWords,
            words.SelectMany(static range => range));
        Assert.Equal(
            expectedStates,
            states.SelectMany(static range => range));
        Assert.Equal(
            expectedMasks,
            masks.SelectMany(static range => range));

        valueCursor = 0;
        wordCursor = 0;
        stateCursor = 0;
        for (int section = 0;
            section < VoxelMath.SectionsPerChunk;
            section++)
        {
            PressureWorkContract.VerifySectionRepresentation(
                cells,
                section,
                summaries[section],
                actualDescriptors[section],
                values[section],
                words[section],
                states[section],
                ref valueCursor,
                ref wordCursor,
                ref stateCursor);
        }

        int changedSection = Array.FindIndex(
            values,
            static range => range.Length != 0);
        values[changedSection][0] ^= 1;
        Assert.Throws<InvalidDataException>(() =>
        {
            int changedValueCursor = 0;
            int changedWordCursor = 0;
            int changedStateCursor = 0;
            for (int section = 0;
                section < VoxelMath.SectionsPerChunk;
                section++)
            {
                PressureWorkContract.VerifySectionRepresentation(
                    cells,
                    section,
                    summaries[section],
                    actualDescriptors[section],
                    values[section],
                    words[section],
                    states[section],
                    ref changedValueCursor,
                    ref changedWordCursor,
                    ref changedStateCursor);
            }
        });
    }

    private static void AssertCanonicalResidentCapacities(
        ReadOnlySpan<PressureChunkShape> shapes)
    {
        int depth = PressureWorkContract.DefaultResidentDepth;
        long maximumRecords = 0;
        long maximumMasks = 0;
        long maximumFaces = 0;
        long maximumUpload = 0;
        long maximumDescriptors = 0;
        long maximumValues = 0;
        long maximumWords = 0;
        long maximumStates = 0;
        long maximumSectionBytes = 0;
        long maximumManagedPoolBytes = 0;
        int maximumVertices = 0;
        int maximumIndices = 0;
        int maximumArraysPerBucket = 0;
        for (int start = 0; start <= shapes.Length - depth; start++)
        {
            long records = 0;
            long masks = 0;
            long faces = 0;
            long upload = 0;
            long descriptors = 0;
            long values = 0;
            long words = 0;
            long states = 0;
            long retainedPoolBytes = 0;
            int vertices = 0;
            int indices = 0;
            Dictionary<int, int> sliceBuckets = [];
            Dictionary<int, int> uploadBuckets = [];
            foreach (PressureChunkShape shape in shapes.Slice(start, depth))
            {
                records += Math.Max(1, shape.RecordCount);
                masks += Math.Max(1, shape.TransparentMaskWords);
                faces += Math.Max(1, shape.FaceCount);
                upload += Math.Max(1, shape.UploadBytes);
                descriptors += shape.SectionDescriptorCount;
                values += shape.SectionValueCount;
                words += shape.SectionWordCount;
                states += shape.SectionStateWordCount;
                vertices = Math.Max(
                    vertices,
                    Math.Max(1, shape.VertexCount));
                indices = Math.Max(
                    indices,
                    Math.Max(1, shape.IndexCount));
                int sliceBucket = PoolLength(Math.Max(1, shape.FaceCount));
                int uploadBucket = PoolLength(Math.Max(1, shape.UploadBytes));
                sliceBuckets[sliceBucket] =
                    sliceBuckets.GetValueOrDefault(sliceBucket) + 1;
                uploadBuckets[uploadBucket] =
                    uploadBuckets.GetValueOrDefault(uploadBucket) + 1;
                retainedPoolBytes = checked(
                    retainedPoolBytes
                    + (long)sliceBucket * VoxelMath.PayloadSliceBytes
                    + uploadBucket);
            }

            maximumRecords = Math.Max(maximumRecords, records);
            maximumMasks = Math.Max(maximumMasks, masks);
            maximumFaces = Math.Max(maximumFaces, faces);
            maximumUpload = Math.Max(maximumUpload, upload);
            maximumDescriptors = Math.Max(maximumDescriptors, descriptors);
            maximumValues = Math.Max(maximumValues, values);
            maximumWords = Math.Max(maximumWords, words);
            maximumStates = Math.Max(maximumStates, states);
            maximumVertices = Math.Max(maximumVertices, vertices);
            maximumIndices = Math.Max(maximumIndices, indices);
            long sectionBytes = checked(
                descriptors * VoxelMath.SectionPrerenderDescriptorBytes
                + values * sizeof(ushort)
                + words * sizeof(uint)
                + states * sizeof(ulong));
            maximumSectionBytes = Math.Max(
                maximumSectionBytes,
                sectionBytes);
            maximumArraysPerBucket = Math.Max(
                maximumArraysPerBucket,
                Math.Max(
                    sliceBuckets.Values.Max(),
                    uploadBuckets.Values.Max()));
            long managedPoolBytes = checked(
                retainedPoolBytes
                + (long)PoolLength(
                    depth * VoxelMath.CellsPerChunk)
                    * VoxelMath.VoxelCellBytes
                + (long)PoolLength(checked((int)records))
                    * VoxelMath.FaceRecordBytes
                + (long)PoolLength(checked((int)masks)) * sizeof(ulong)
                + (long)PoolLength(checked((int)descriptors))
                    * VoxelMath.SectionPrerenderDescriptorBytes
                + (long)PoolLength(Math.Max(1, checked((int)values)))
                    * sizeof(ushort)
                + (long)PoolLength(Math.Max(1, checked((int)words)))
                    * sizeof(uint)
                + (long)PoolLength(Math.Max(1, checked((int)states)))
                    * sizeof(ulong)
                + (long)PoolLength(vertices) * VoxelMath.VertexBytes
                + (long)PoolLength(indices) * VoxelMath.IndexBytes);
            maximumManagedPoolBytes = Math.Max(
                maximumManagedPoolBytes,
                managedPoolBytes);
        }

        Assert.Equal(
            PressureWorkContract.CanonicalResidentCellCapacity,
            depth * VoxelMath.CellsPerChunk);
        Assert.Equal(
            PressureWorkContract.CanonicalResidentFaceRecordCapacity,
            maximumRecords);
        Assert.Equal(
            PressureWorkContract.CanonicalResidentTransparentMaskWordCapacity,
            maximumMasks);
        Assert.Equal(
            PressureWorkContract.CanonicalResidentPayloadSliceCapacity,
            maximumFaces);
        Assert.Equal(
            PressureWorkContract.CanonicalResidentUploadByteCapacity,
            maximumUpload);
        Assert.Equal(
            PressureWorkContract.CanonicalResidentSectionDescriptorCapacity,
            maximumDescriptors);
        Assert.Equal(
            PressureWorkContract.CanonicalResidentSectionValueCapacity,
            maximumValues);
        Assert.Equal(
            PressureWorkContract.CanonicalResidentSectionWordCapacity,
            maximumWords);
        Assert.Equal(
            PressureWorkContract.CanonicalResidentSectionStateWordCapacity,
            maximumStates);
        Assert.Equal(
            PressureWorkContract.CanonicalMaximumVertexCapacity,
            maximumVertices);
        Assert.Equal(
            PressureWorkContract.CanonicalMaximumIndexCapacity,
            maximumIndices);
        Assert.Equal(
            PressureWorkContract.CanonicalMaximumArraysPerPoolBucket,
            maximumArraysPerBucket);
        Assert.True(
            PressureWorkContract.CanonicalRetainedArraysPerPoolBucket
                < PressureWorkContract.CanonicalMaximumArraysPerPoolBucket);
        Assert.Equal(
            PressureWorkContract.CanonicalManagedPoolResidentBytes,
            maximumManagedPoolBytes);
        Assert.Equal(
            PressureWorkContract.CanonicalArenaReservationBytes,
            checked(
                (long)PressureWorkContract.CanonicalResidentCellCapacity
                    * VoxelMath.VoxelCellBytes
                + (long)maximumRecords * VoxelMath.FaceRecordBytes
                + (long)maximumMasks * sizeof(ulong)
                + maximumFaces * VoxelMath.PayloadSliceBytes
                + maximumSectionBytes
                + (long)maximumVertices * VoxelMath.VertexBytes
                + (long)maximumIndices * VoxelMath.IndexBytes
                + 4_096));
    }

    [Fact]
    public void SustainedDiagnosticContractSerializesFromTheSharedAssembly()
    {
        PressureWorkerDiagnostic worker = new(
            3,
            17,
            4_096,
            DateTime.UnixEpoch,
            DateTime.UnixEpoch.AddSeconds(1),
            DateTime.UnixEpoch.AddSeconds(2),
            1_000,
            1_000,
            new PressurePhaseTimings(
                1,
                2,
                3,
                4,
                5,
                6,
                7,
                8),
            default,
            default,
            default);
        PressureSustainedDiagnosticReport report = new(
            "commit",
            "source-hash",
            "image",
            [],
            new PressureSustainedDiagnosticOptionsSnapshot(
                "repository",
                "image",
                "output",
                "0-11",
                268_435_456,
                6_000,
                20,
                17,
                128,
                90,
                1_000,
                4,
                6,
                120,
                10,
                1,
                5),
            ["docker run"],
            100,
            [
                new PressureHostProcessorSample(
                    DateTime.UnixEpoch,
                    100,
                    1,
                    0)
            ],
            [
                new PressureSustainedDiagnosticTrace(
                    "NAM-10",
                    "NAM",
                    10,
                    new PressureHostStateGate([], true, string.Empty),
                    default,
                    [],
                    [],
                    default,
                    [true],
                    true,
                    default)
            ],
            new PressureSustainedDiagnosticCleanup(
                1,
                1,
                true,
                true,
                0),
            new PressureSustainedDiagnosticHostGateFailure(
                "Safe-12",
                new PressureHostStateGate(
                    [
                        new PressureHostProcessorSample(
                            DateTime.UnixEpoch,
                            100,
                            20,
                            0)
                    ],
                    false,
                    "The host is busy.")),
            "The host is busy.",
            DateTime.UnixEpoch,
            DateTime.UnixEpoch.AddSeconds(3));
        string json = JsonSerializer.Serialize(
            report,
            VoxelJson.Options);
        PressureSustainedDiagnosticReport restored =
            JsonSerializer.Deserialize<
                PressureSustainedDiagnosticReport>(
                json,
                VoxelJson.Options);
        PressureRequestDiagnostics diagnostics = new([worker]);

        Assert.Equal("source-hash", restored.WorkingTreeSourceSha256);
        Assert.Single(restored.Traces);
        Assert.Equal("NAM-10", restored.Traces[0].Label);
        Assert.Equal(
            "Safe-12",
            restored.HostGateFailure?.TraceLabel);
        Assert.Equal("docker run", Assert.Single(restored.Commands));
        Assert.Single(
            restored.HostGateFailure?.HostGate.Samples
                ?? []);
        Assert.Single(diagnostics.Workers);
        Assert.Same(
            typeof(FaceRecord).Assembly,
            diagnostics.GetType().Assembly);
    }

    [Fact]
    public void HostStabilityRequiresThreeValidTailSamples()
    {
        PressureHostProcessorSample[] baseline =
        [
            HostSample(99, 1, 0),
            HostSample(101, 1, 0),
            HostSample(100, 1, 0)
        ];
        double median =
            PressureHostStabilityPolicy
                .MedianProcessorPerformance(baseline);
        PressureHostProcessorSample[] samples =
        [
            HostSample(100, 20, 0),
            HostSample(96, 9, 1),
            HostSample(100, 10, 0),
            HostSample(104, 2, 0)
        ];

        Assert.Equal(100, median);
        Assert.True(
            PressureHostStabilityPolicy.HasStableTail(
                samples,
                median,
                10,
                1,
                5));
        Assert.False(
            PressureHostStabilityPolicy.HasStableTail(
                samples[..^1],
                median,
                10,
                1,
                5));
        Assert.False(
            PressureHostStabilityPolicy.IsStable(
                HostSample(100, 10.1, 0),
                median,
                10,
                1,
                5));
        Assert.False(
            PressureHostStabilityPolicy.IsStable(
                HostSample(100, 1, 1.1),
                median,
                10,
                1,
                5));
        Assert.False(
            PressureHostStabilityPolicy.IsStable(
                HostSample(105.1, 1, 0),
                median,
                10,
                1,
                5));
    }

    [Fact]
    public void SustainedDiagnosticUsesTheFixedTracePlan()
    {
        string root = FindRepositoryRoot();
        string demoRoot = Path.Combine(
            root,
            ".Demos",
            "01-VoxelChunkPipeline");
        string harness = File.ReadAllText(
            Path.Combine(
                demoRoot,
                "Harness",
                "PressureMatrixHarness.cs"));
        string nativeProgram = File.ReadAllText(
            Path.Combine(demoRoot, "NAM", "Program.cs"));
        string safeProgram = File.ReadAllText(
            Path.Combine(demoRoot, "SafeCSharp", "Program.cs"));
        int safe = harness.IndexOf(
            "(\"Safe-12\", \"SafeCSharp\", 12)",
            StringComparison.Ordinal);
        int nativeTen = harness.IndexOf(
            "(\"NAM-10\", \"NAM\", 10)",
            StringComparison.Ordinal);
        int nativeEight = harness.IndexOf(
            "(\"NAM-8\", \"NAM\", 8)",
            StringComparison.Ordinal);
        int nativeSix = harness.IndexOf(
            "(\"NAM-6\", \"NAM\", 6)",
            StringComparison.Ordinal);

        Assert.True(safe >= 0);
        Assert.True(nativeTen > safe);
        Assert.True(nativeEight > nativeTen);
        Assert.True(nativeSix > nativeEight);
        Assert.Contains(
            "NAM_DIAGNOSTIC_WORKER_COUNT",
            nativeProgram,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NAM_DIAGNOSTIC_WORKER_COUNT",
            safeProgram,
            StringComparison.Ordinal);
        Assert.Contains(
            "implementation == \"NAM\"",
            harness,
            StringComparison.Ordinal);
        Assert.Contains(
            "? workerCount",
            harness,
            StringComparison.Ordinal);
        Assert.Contains(
            "The sustained diagnostic requires one shared CPU set.",
            harness,
            StringComparison.Ordinal);
        Assert.Contains(
            "commands.ToArray()",
            harness,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DockerChildrenUseTheHarnessLifetimeJob()
    {
        string root = FindRepositoryRoot();
        string demoRoot = Path.Combine(
            root,
            ".Demos",
            "01-VoxelChunkPipeline");
        string harness = File.ReadAllText(
            Path.Combine(
                demoRoot,
                "Harness",
                "PressureMatrixHarness.cs"));
        string lifetime = File.ReadAllText(
            Path.Combine(
                demoRoot,
                "Harness",
                "WindowsProcessLifetimeJob.cs"));

        Assert.Contains(
            "KillOnJobClose = 0x00002000",
            lifetime,
            StringComparison.Ordinal);
        Assert.Contains(
            "AssignProcessToJobObject",
            lifetime,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            harness.Split(
                "_processLifetime.Assign(",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "_processLifetime.Dispose();",
            harness,
            StringComparison.Ordinal);
        Assert.Contains(
            "StopStatsAfterStartFailure();",
            harness,
            StringComparison.Ordinal);
        Assert.Contains(
            "process.Kill(entireProcessTree: true);",
            harness,
            StringComparison.Ordinal);
        Assert.Contains(
            "await worker.DisposeAsync();",
            harness,
            StringComparison.Ordinal);
    }

    private static PressureHostProcessorSample HostSample(
        double performance,
        double cpu,
        double queue) =>
        new(
            DateTime.UnixEpoch,
            performance,
            cpu,
            queue);

    private static PressureMatrixCheckpoint CreatePressureCheckpoint(
        long sequence)
    {
        PressureBinaryIdentity identity = new(
            "Harness",
            "Harness.dll",
            "sha256",
            "1.0.0+commit",
            "commit");
        PressureMatrixOptionsSnapshot options = new(
            "repository",
            "image",
            "checkpoint.json",
            "checkpoint.json.activity",
            "0-3",
            "0-3",
            268_435_456,
            6000,
            20,
            4,
            17,
            128,
            90,
            6,
            [1000, 10000],
            120,
            16_860,
            true);
        return new PressureMatrixCheckpoint(
            1,
            sequence,
            "commit",
            "image",
            [identity],
            options,
            [],
            null,
            null,
            [],
            [],
            [],
            DateTime.UnixEpoch,
            DateTime.UnixEpoch.AddSeconds(sequence));
    }

    private static CompilationSample CompilationSampleForTest(
        int pair,
        int position,
        string implementation,
        PressureCompilationConfiguration configuration) =>
        new(
            pair,
            position,
            implementation,
            configuration,
            DateTime.UnixEpoch,
            1,
            1,
            CompilationOutcome.Completed,
            0,
            "dotnet build",
            string.Empty,
            string.Empty);

    private static string CreateTestArtifactDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"nam-pressure-checkpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTestArtifactDirectory(string directory)
    {
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            File.Delete(path);
        }

        Directory.Delete(directory);
    }

    private static (
        int ExitCode,
        string StandardOutput,
        string StandardError)
        RunPowerShell(
            string script,
            params string[] arguments)
    {
        string executable = OperatingSystem.IsWindows()
            ? "powershell.exe"
            : "pwsh";
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
        }

        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Could not start PowerShell.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "The timeout validation did not complete.");
        }

        process.WaitForExit();
        return (
            process.ExitCode,
            output.GetAwaiter().GetResult(),
            error.GetAwaiter().GetResult());
    }

    private static int PoolLength(int requestedElements)
    {
        int length = 16;
        while (length < requestedElements)
        {
            length = checked(length * 2);
        }

        return length;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Supprocom.NativeAllocationManagement.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
