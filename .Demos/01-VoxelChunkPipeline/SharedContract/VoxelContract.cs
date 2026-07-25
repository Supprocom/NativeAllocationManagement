using System.Globalization;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public readonly record struct VoxelWorkloadOptions(
    int Seed,
    int ChunkCount,
    int WorkerCount,
    int Iterations)
{
    public const int DefaultSeed = 1_706_251;
    public const int DefaultChunkCount = 2;
    public const int DefaultWorkerCount = 2;
    public const int DefaultIterations = 1;

    public static VoxelWorkloadOptions Default => new(
        DefaultSeed,
        DefaultChunkCount,
        DefaultWorkerCount,
        DefaultIterations);

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
                _ => throw new ArgumentException($"Unknown workload option '{name}'.", nameof(args))
            };
        }

        if (options.ChunkCount <= 0 || options.WorkerCount <= 0 || options.Iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Chunk, worker, and iteration counts must be positive.");
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

public readonly record struct FaceRecord(int CellIndex, int BlockId, int Mask);

public readonly record struct Vertex(int X, int Y, int Z, int Face, int BlockId);

public readonly record struct PayloadSlice(int Offset, int Length, int Alignment, int StageMask, int BlockId, int CellIndex);

public enum SectionRepresentationKind
{
    Empty,
    Uniform,
    Expanded,
    Packed,
    MultiPacked
}

public readonly record struct SectionSummary(
    SectionRepresentationKind Kind,
    int TransparentIds,
    bool HasDominantTransparentId,
    bool HasResidualTransparentIds);

public readonly record struct PipelineResult(
    string Implementation,
    long Digest,
    int Chunks,
    long VisibleFaces,
    long Vertices,
    long Indices,
    long StagedBytes,
    long ManagedPayloadObjectBytes,
    long PeakManagedBackingBytes,
    long PeakNativeBackingBytes,
    long PeakRetainedNativeBackingBytes,
    long FinalNativeBackingBytes,
    long PeakCoordinateStageBytes,
    long PeakFaceStageBytes,
    long PeakPackingStageBytes,
    long RentCount,
    long ScopedRecycleCount,
    long ClearedBytes,
    long EmptySections = 0,
    long UniformSections = 0,
    long ExpandedSections = 0,
    long PackedSections = 0,
    long MultiPackedSections = 0,
    long TransparentMaskCount = 0,
    long DominantTransparentSections = 0,
    long ResidualTransparentSections = 0);

public readonly record struct ChildRunResult(
    string Implementation,
    PipelineResult Result,
    double ElapsedMilliseconds,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long HeapBytesAfterRun,
    long PeakWorkingSetBytes,
    long LargeObjectHeapBytesAfterRun = 0,
    long GcPauseMilliseconds = 0)
{
    public string ToJson() => JsonSerializer.Serialize(this, VoxelJson.Options);

    public static ChildRunResult FromJson(string json) =>
        JsonSerializer.Deserialize<ChildRunResult>(json, VoxelJson.Options);
}

public static class VoxelJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}

public static class VoxelMath
{
    public const int ChunkDimension = 64;
    public const int CellsPerChunk = ChunkDimension * ChunkDimension * ChunkDimension;
    public const int FacesPerCell = 6;
    public const int VerticesPerFace = 4;
    public const int IndicesPerFace = 6;
    public const int VertexBytes = 20;
    public const int IndexBytes = 4;
    public const int TransparentMaskWordsPerId = 64;
    public const int DigestModulus = 1_000_000_007;
    public const int DigestMultiplier = 1_000_003;

    public static readonly BlockTypeDescriptor[] BlockTypes =
    [
        new(256, "stone", 8, 4, 0, -18, 0b0011, 40),
        new(257, "water", 24, 8, 16, -28, 0b0101, 18),
        new(258, "foliage", 40, 16, -12, -8, 0b1001, 9),
        new(259, "metal", 56, 32, 8, -4, 0b0110, 5),
        new(260, "crystal", 72, 16, 24, 2, 0b1110, 2)
    ];

    private static readonly ushort[] BlockTypeIndexById = CreateBlockTypeIndex();

    public const int TotalFrequencyWeight = 74;

    public static int CellIndex(int x, int y, int z) =>
        checked((z * ChunkDimension + y) * ChunkDimension + x);

    public static int BlockIdForCell(int seed, int chunkIndex, int x, int y, int z)
    {
        int section = SectionIndex(x, y, z);
        int sectionPattern = section % 5;
        if (sectionPattern == 0)
        {
            return BlockTypes[1].Id;
        }

        if (sectionPattern == 1)
        {
            return BlockTypes[0].Id;
        }

        if (sectionPattern == 2)
        {
            return ((x + y + z + seed + chunkIndex) & 7) == 0 ? BlockTypes[3].Id : BlockTypes[0].Id;
        }

        if (sectionPattern == 3)
        {
            int selected = (x + y * 3 + z * 5 + seed + chunkIndex) % 3;
            if (selected < 0)
            {
                selected += 3;
            }

            return BlockTypes[selected].Id;
        }

        int selector =
            (seed + chunkIndex * 97 + x * 17 + y * 31 + z * 47 + (x * y) * 3 + (y * z) * 5) %
            TotalFrequencyWeight;
        int cumulative = 0;
        for (int index = 0; index < BlockTypes.Length; index++)
        {
            cumulative += BlockTypes[index].FrequencyWeight;
            if (selector < cumulative)
            {
                return BlockTypes[index].Id;
            }
        }

        return BlockTypes[^1].Id;
    }

    public static short DensityForCell(int seed, int chunkIndex, int x, int y, int z, int blockId)
    {
        int sectionPattern = SectionIndex(x, y, z) % 5;
        if (sectionPattern == 0)
        {
            return -100;
        }

        if (sectionPattern == 1)
        {
            return 64;
        }

        int value = seed + chunkIndex * 113 + x * 13 + y * 29 + z * 43 + x * z * 7;
        value %= 257;
        if (value < 0)
        {
            value += 257;
        }

        return checked((short)(value - 128 + BlockTypeForId(blockId).DensityBias));
    }

    public static bool IsSolid(short density, int blockId) =>
        density >= BlockTypeForId(blockId).SolidThreshold;

    public static int AlignUp(int value, int alignment)
    {
        int remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    public static int StageBytesForFace(int blockId)
    {
        BlockTypeDescriptor type = BlockTypeForId(blockId);
        return AlignUp(
            checked(type.PayloadBytes + VerticesPerFace * VertexBytes + IndicesPerFace * IndexBytes),
            type.Alignment);
    }

    public static long DigestStep(long state, long value)
    {
        long normalized = value % DigestModulus;
        if (normalized < 0)
        {
            normalized += DigestModulus;
        }

        return (state * DigestMultiplier + normalized + 97) % DigestModulus;
    }

    public static long DigestBytes(long state, ReadOnlySpan<byte> bytes)
    {
        for (int index = 0; index < bytes.Length; index++)
        {
            state = DigestStep(state, bytes[index]);
        }

        return state;
    }

    public static int FaceCount(int mask)
    {
        int count = 0;
        int remaining = mask;
        while (remaining != 0)
        {
            count += remaining & 1;
            remaining >>= 1;
        }

        return count;
    }

    public static int NeighborIndex(int cellIndex, int face)
    {
        int x = cellIndex % ChunkDimension;
        int y = (cellIndex / ChunkDimension) % ChunkDimension;
        int z = cellIndex / (ChunkDimension * ChunkDimension);
        int nx = x;
        int ny = y;
        int nz = z;
        switch (face)
        {
            case 0: nx--; break;
            case 1: nx++; break;
            case 2: ny--; break;
            case 3: ny++; break;
            case 4: nz--; break;
            case 5: nz++; break;
            default: throw new ArgumentOutOfRangeException(nameof(face));
        }

        return nx < 0 || nx >= ChunkDimension || ny < 0 || ny >= ChunkDimension || nz < 0 || nz >= ChunkDimension
            ? -1
            : CellIndex(nx, ny, nz);
    }

    public static int FaceMaskFromManaged(int cellIndex, ReadOnlySpan<short> densities, ReadOnlySpan<ushort> materials)
    {
        int blockId = materials[cellIndex];
        if (!IsSolid(densities[cellIndex], blockId))
        {
            return 0;
        }

        int mask = 0;
        for (int face = 0; face < FacesPerCell; face++)
        {
            int neighbor = NeighborIndex(cellIndex, face);
            if (neighbor < 0 || !IsSolid(densities[neighbor], materials[neighbor]))
            {
                mask |= 1 << face;
            }
        }

        return mask;
    }

    public static int PayloadByte(int seed, int cellIndex, int blockId, int slot) =>
        (seed + cellIndex * 11 + blockId * 37 + slot * 17 + BlockTypeForId(blockId).StageMask * 13) & 0xFF;

    public static BlockTypeDescriptor BlockTypeForId(int blockId)
    {
        if ((uint)blockId >= BlockTypeIndexById.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(blockId));
        }

        ushort index = BlockTypeIndexById[blockId];
        if (index >= BlockTypes.Length || BlockTypes[index].Id != blockId)
        {
            throw new ArgumentOutOfRangeException(nameof(blockId), blockId, "The runtime block registry does not contain this identifier.");
        }

        return BlockTypes[index];
    }

    public static int SectionIndex(int x, int y, int z) =>
        checked(((z / 16) * 4 + (y / 16)) * 4 + (x / 16));

    public static SectionSummary ClassifySection(ReadOnlySpan<ushort> materials, ReadOnlySpan<short> densities, int sectionIndex)
    {
        Span<int> counts = stackalloc int[BlockTypes.Length];
        Span<int> transparentCounts = stackalloc int[BlockTypes.Length];
        int solidCount = 0;
        int startX = (sectionIndex % 4) * 16;
        int startY = ((sectionIndex / 4) % 4) * 16;
        int startZ = (sectionIndex / 16) * 16;
        for (int z = startZ; z < startZ + 16; z++)
        {
            for (int y = startY; y < startY + 16; y++)
            {
                int row = (z * ChunkDimension + y) * ChunkDimension;
                for (int x = startX; x < startX + 16; x++)
                {
                    int cell = row + x;
                    int index = BlockTypeIndexById[materials[cell]];
                    counts[index]++;
                    if (IsSolid(densities[cell], materials[cell]))
                    {
                        solidCount++;
                    }
                    else
                    {
                        transparentCounts[index]++;
                    }
                }
            }
        }

        int unique = 0;
        int transparentIds = 0;
        int dominantTransparentCount = 0;
        int transparentCount = 0;
        for (int index = 0; index < counts.Length; index++)
        {
            if (counts[index] == 0)
            {
                continue;
            }

            unique++;
            if (transparentCounts[index] != 0)
            {
                transparentIds++;
                transparentCount += transparentCounts[index];
                dominantTransparentCount = Math.Max(dominantTransparentCount, transparentCounts[index]);
            }
        }

        SectionRepresentationKind kind = solidCount == 0
            ? SectionRepresentationKind.Empty
            : unique == 1
                ? SectionRepresentationKind.Uniform
                : unique == 2
                    ? SectionRepresentationKind.Packed
                    : unique == 3
                        ? SectionRepresentationKind.Expanded
                        : SectionRepresentationKind.MultiPacked;
        bool dominant = transparentIds > 0 && dominantTransparentCount * 2 >= transparentCount;
        bool residual = transparentIds > 1 && !dominant;
        return new SectionSummary(kind, transparentIds, dominant, residual);
    }

    public static int BuildTransparentMasks(
        ReadOnlySpan<ushort> materials,
        ReadOnlySpan<short> densities,
        int sectionIndex,
        Span<ulong> destination)
    {
        Span<int> maskSlots = stackalloc int[BlockTypes.Length];
        maskSlots.Fill(-1);
        int startX = (sectionIndex % 4) * 16;
        int startY = ((sectionIndex / 4) % 4) * 16;
        int startZ = (sectionIndex / 16) * 16;
        int transparentIds = 0;
        for (int z = startZ; z < startZ + 16; z++)
        {
            for (int y = startY; y < startY + 16; y++)
            {
                int row = (z * ChunkDimension + y) * ChunkDimension;
                for (int x = startX; x < startX + 16; x++)
                {
                    int cell = row + x;
                    if (IsSolid(densities[cell], materials[cell]))
                    {
                        continue;
                    }

                    int typeIndex = BlockTypeIndexById[materials[cell]];
                    int slot = maskSlots[typeIndex];
                    if (slot < 0)
                    {
                        slot = maskSlots[typeIndex] = transparentIds++;
                        int requiredWords = checked((slot + 1) * TransparentMaskWordsPerId);
                        if (requiredWords > destination.Length)
                        {
                            throw new ArgumentException("The destination does not contain enough transparent-mask words.", nameof(destination));
                        }
                    }

                    int localX = x - startX;
                    int localY = y - startY;
                    int localZ = z - startZ;
                    int localCell = (localZ * 16 + localY) * 16 + localX;
                    int word = checked(slot * TransparentMaskWordsPerId + (localCell >> 6));
                    destination[word] |= 1UL << (localCell & 63);
                }
            }
        }

        return transparentIds;
    }

    private static ushort[] CreateBlockTypeIndex()
    {
        ushort[] lookup = new ushort[ushort.MaxValue + 1];
        for (ushort index = 0; index < BlockTypes.Length; index++)
        {
            lookup[BlockTypes[index].Id] = index;
        }

        return lookup;
    }

    public static int VertexValue(int cellIndex, int face, int vertex, int field, int blockId)
    {
        int value = field switch
        {
            0 => cellIndex % ChunkDimension,
            1 => (cellIndex / ChunkDimension) % ChunkDimension,
            2 => cellIndex / (ChunkDimension * ChunkDimension),
            3 => face,
            4 => blockId,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        return value + vertex * (field + 3);
    }

    public static int IndexValue(int vertexOffset, int indexOffset) =>
        vertexOffset + ((indexOffset % VerticesPerFace) switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            3 => 2,
            4 => 3,
            _ => 0
        });
}
