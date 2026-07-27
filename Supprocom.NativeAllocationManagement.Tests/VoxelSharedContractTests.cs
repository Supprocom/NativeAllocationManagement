using System.Reflection;
using System.Runtime.CompilerServices;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class VoxelSharedContractTests
{
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
            typeof(PressureRuntimeSnapshot),
            typeof(PressureProfileResult),
            typeof(PressureEnvelope),
            typeof(PressureOutputEvidence),
            typeof(PressureChunkShape),
            typeof(CompilationSample),
            typeof(CompilationGateSummary),
            typeof(CompilationGateReport),
            typeof(PressureHostProgress),
            typeof(PressureHostSample),
            typeof(PressureEffectiveIsolation),
            typeof(PressureImplementationObservation),
            typeof(PressurePairedStatistics),
            typeof(PressureProfilePair),
            typeof(PressureMatrixSummary),
            typeof(PressureMatrixReport)
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

        CompilationGateSummary failedSummary = new(
            5,
            1.10,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            false,
            false,
            false);
        string failedJson = System.Text.Json.JsonSerializer.Serialize(
            failedSummary,
            VoxelJson.Options);
        Assert.DoesNotContain("NaN", failedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", failedJson, StringComparison.Ordinal);

        string pressureHarness = File.ReadAllText(
            Path.Combine(demoRoot, "Harness", "PressureMatrixHarness.cs"));
        Assert.Contains(
            "<= 100 => 1.50",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "200 => 1.75",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "/ 800.0 * 0.25",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "NamScalingGatePassed(profiles)",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.Contains(
            "current.Value >= previous.Value",
            pressureHarness,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "consumes every upload byte",
            pressureHarness,
            StringComparison.Ordinal);

        string runner = File.ReadAllText(
            Path.Combine(demoRoot, "Pressure", "run-constrained.ps1"));
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
            "Evidence = request.ExecutionMode",
            nativeSource,
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
            vertices,
            indices,
            slices,
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
                vertices,
                indices,
                slices,
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
                capBytes * 10,
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
                300,
                400,
                500,
                600,
                700,
                800,
                900,
                1000
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
