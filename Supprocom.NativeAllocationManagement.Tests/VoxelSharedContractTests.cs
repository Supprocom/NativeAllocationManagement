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
            typeof(BenchmarkSummary)
        ];

        Assembly assembly = typeof(FaceRecord).Assembly;
        Assert.All(contractTypes, type => Assert.Same(assembly, type.Assembly));
        Assert.Equal(VoxelMath.FaceRecordBytes, Unsafe.SizeOf<FaceRecord>());
        Assert.Equal(7 * sizeof(int), VoxelMath.FaceRecordBytes);
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
        Assert.Equal((long)2 * VoxelMath.CellsPerChunk, contract.CellCount);
        Assert.NotEqual(0, contract.CellValueByteHash);
        Assert.NotEqual(0, contract.ByteHash);
        Assert.NotEqual(0, contract.ChunkOrderHash);
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
        Assert.Equal(options.ChunkCount - 1, cells[^1].ChunkId);
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
