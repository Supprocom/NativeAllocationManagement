using System.Globalization;
using System.Text.Json.Serialization;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public readonly record struct VoxelWorkloadOptions(
    int Seed,
    int ChunkCount,
    int WorkerCount,
    int Iterations,
    int WarmupChunksPerWorker = 2)
{
    public const int DefaultSeed = 1_706_251;
    public const int DefaultChunkCount = 4;
    public const int DefaultWorkerCount = 2;
    public const int DefaultIterations = 1;
    public const int DefaultWarmupChunksPerWorker = 2;

    public static VoxelWorkloadOptions Default => new(
        DefaultSeed,
        DefaultChunkCount,
        DefaultWorkerCount,
        DefaultIterations,
        DefaultWarmupChunksPerWorker);

    public static VoxelWorkloadOptions Parse(IReadOnlyList<string> args)
    {
        VoxelWorkloadOptions options = Default;
        for (int index = 0; index < args.Count; index++)
        {
            string name = args[index];
            if (index + 1 >= args.Count)
            {
                throw new ArgumentException($"Missing value for {name}.", nameof(args));
            }

            int value = int.Parse(args[++index], NumberStyles.Integer, CultureInfo.InvariantCulture);
            options = name switch
            {
                "--seed" => options with { Seed = value },
                "--chunks" => options with { ChunkCount = value },
                "--workers" => options with { WorkerCount = value },
                "--iterations" => options with { Iterations = value },
                "--warmup" => options with { WarmupChunksPerWorker = value },
                _ => throw new ArgumentException($"Unknown workload option '{name}'.", nameof(args))
            };
        }

        if (options.ChunkCount <= 0
            || options.WorkerCount <= 0
            || options.Iterations <= 0
            || options.WarmupChunksPerWorker < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "Chunk, worker, and iteration counts must be positive and warmup cannot be negative.");
        }

        return options;
    }
}

public readonly record struct BlockTypeDescriptor(
    int Id,
    string Name,
    int PayloadBytes,
    int Alignment,
    int DensityBias,
    int SolidThreshold,
    int StageMask,
    int FrequencyWeight);

public readonly record struct CellCoordinate(int X, int Y, int Z);

public readonly record struct CanonicalInputCell(
    int ChunkId,
    int CellIndex,
    int X,
    int Y,
    int Z,
    int Section,
    ushort BlockId,
    short Density);

public readonly struct CanonicalInputContract : IEquatable<CanonicalInputContract>
{
    [JsonConstructor]
    public CanonicalInputContract(
        VoxelWorkloadOptions options,
        BlockTypeDescriptor[] registry,
        long cellCount,
        long cellValueByteHash,
        long byteHash,
        long chunkOrderHash,
        CanonicalInputCell[]? cells = null,
        string strongHash = "",
        bool observed = false)
    {
        Options = options;
        Registry = registry;
        CellCount = cellCount;
        CellValueByteHash = cellValueByteHash;
        ByteHash = byteHash;
        ChunkOrderHash = chunkOrderHash;
        Cells = cells;
        StrongHash = strongHash;
        Observed = observed;
    }

    public VoxelWorkloadOptions Options { get; }

    public BlockTypeDescriptor[] Registry { get; }

    public long CellCount { get; }

    [JsonIgnore]
    public long CellValueByteHash { get; }

    [JsonIgnore]
    public long ByteHash { get; }

    [JsonIgnore]
    public long ChunkOrderHash { get; }

    /// <summary>Complete pre-mutation cells when correctness mode requests materialized input; null in timed pressure runs.</summary>
    public CanonicalInputCell[]? Cells { get; }

    /// <summary>SHA-256 over the complete canonical input contract and every measured cell.</summary>
    public string StrongHash { get; }

    /// <summary>True only when the implementation reported cells observed in its own pre-mutation storage.</summary>
    public bool Observed { get; }

    public bool Equals(CanonicalInputContract other) =>
        Options == other.Options
        && CellCount == other.CellCount
        && CellValueByteHash == other.CellValueByteHash
        && ByteHash == other.ByteHash
        && ChunkOrderHash == other.ChunkOrderHash
        && StrongHash == other.StrongHash
        && Observed == other.Observed
        && (Cells ?? Array.Empty<CanonicalInputCell>()).AsSpan()
            .SequenceEqual((other.Cells ?? Array.Empty<CanonicalInputCell>()).AsSpan())
        && (Registry ?? Array.Empty<BlockTypeDescriptor>()).AsSpan()
            .SequenceEqual((other.Registry ?? Array.Empty<BlockTypeDescriptor>()).AsSpan());

    public override bool Equals(object? obj) =>
        obj is CanonicalInputContract other && Equals(other);

    public static bool operator ==(CanonicalInputContract left, CanonicalInputContract right) =>
        left.Equals(right);

    public static bool operator !=(CanonicalInputContract left, CanonicalInputContract right) =>
        !left.Equals(right);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Options);
        hash.Add(CellCount);
        hash.Add(CellValueByteHash);
        hash.Add(ByteHash);
        hash.Add(ChunkOrderHash);
        hash.Add(StrongHash);
        hash.Add(Observed);
        BlockTypeDescriptor[] registry = Registry ?? Array.Empty<BlockTypeDescriptor>();
        for (int index = 0; index < registry.Length; index++)
        {
            hash.Add(registry[index]);
        }

        return hash.ToHashCode();
    }
}

public readonly record struct CanonicalInputFixture(
    int Seed,
    int ChunkId,
    CanonicalInputCell[] Cells,
    long ExpectedByteHash);

public readonly record struct HandAuthoredInputFixture(
    int Seed,
    int ChunkId,
    int Width,
    int Height,
    int Depth,
    CanonicalInputCell[] Cells,
    long ExpectedByteHash);

public enum SectionRepresentationKind
{
    Empty = 0,
    Uniform = 1,
    Expanded = 3,
    Packed = 4,
    MultiPacked = 5
}

public readonly record struct SectionSummary(
    SectionRepresentationKind Kind,
    int DistinctIds,
    int TransparentIds,
    ushort UniformBlockId,
    int OpaqueCount,
    int TransparentCount,
    int EmptyCount,
    int BitsPerIndex,
    bool HasDominantTransparentId,
    bool HasResidualTransparentIds);
