using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public readonly record struct VoxelWorkloadOptions(
    int Seed,
    int ChunkCount,
    int WorkerCount,
    int Iterations)
{
    public const int DefaultSeed = 1_706_251;
    public const int DefaultChunkCount = 4;
    public const int DefaultWorkerCount = 2;
    public const int DefaultIterations = 1;
    public const int WarmupChunksPerWorker = 2;

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

public readonly record struct Vertex(int X, int Y, int Z, int Face, int Corner, int BlockId);

public readonly record struct PayloadSlice(int Offset, int Length, int Alignment, int StageMask, int BlockId, int CellIndex);

public struct VoxelCell
{
    public ushort BlockId;
    public short Density;
    public int FaceMask;
    public int OpaqueMask;
    public int TransparentMask;
    public int Section;
}

public struct NativeFaceOutput
{
    public int CellIndex;
    public int BlockId;
    public int FaceMask;
}

public readonly record struct OutputFixture(
    Vertex[] OpaqueVertices,
    int[] OpaqueIndices,
    PayloadSlice[] OpaqueSlices,
    byte[] OpaqueUpload,
    Vertex[] TransparentVertices,
    int[] TransparentIndices,
    PayloadSlice[] TransparentSlices,
    byte[] TransparentUpload);

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
    long ResidualTransparentSections = 0,
    long OpaqueVisibleFaces = 0,
    long TransparentVisibleFaces = 0,
    long OpaqueVertices = 0,
    long TransparentVertices = 0,
    long OpaqueIndices = 0,
    long TransparentIndices = 0,
    long OpaqueStagedBytes = 0,
    long TransparentStagedBytes = 0,
    long EnabledStageBytes = 0,
    long ManagedContainerBytes = 0,
    long TransparentMaskWords = 0,
    long ReusedLeaseCount = 0,
    long ReusedNativeSegmentCount = 0,
    double MeasuredMilliseconds = 0,
    long MeasuredManagedAllocatedBytes = 0,
    int MeasuredGen0Collections = 0,
    int MeasuredGen1Collections = 0,
    int MeasuredGen2Collections = 0,
    OutputFixture? MaterializedOutput = null,
    long ColdManagedBackingBytes = 0);

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
    long ColdManagedAllocatedBytes = 0)
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
    public static readonly int VoxelCellBytes = Unsafe.SizeOf<VoxelCell>();
    public static readonly int FaceRecordBytes = Unsafe.SizeOf<FaceRecord>();
    public static readonly int NativeFaceOutputBytes = Unsafe.SizeOf<NativeFaceOutput>();
    public static readonly int VertexBytes = Unsafe.SizeOf<Vertex>();
    public static readonly int IndexBytes = Unsafe.SizeOf<int>();
    public static readonly int PayloadSliceBytes = Unsafe.SizeOf<PayloadSlice>();
    public const int TransparentMaskWordsPerId = 64;
    public const int DigestModulus = 1_000_000_007;
    public const int DigestMultiplier = 1_000_003;
    public const int AirBlockId = 0;
    public const int OutputFixtureElementLimit = 2;
    public const int OutputFixtureByteLimit = 512;

    public static int SizeOf<T>() where T : unmanaged => Unsafe.SizeOf<T>();

    // These are setup-time value LUTs. The measured cells carry only ushort IDs,
    // matching VoxelEngine's runtime BlockType registry contract.
    public static readonly BlockTypeDescriptor[] BlockTypes =
    [
        new(AirBlockId, "air", 0, 1, -128, -32768, 0, 100),
        new(256, "stone", 8, 4, 0, 32, 0b0011, 40),
        new(257, "water", 24, 8, 16, -32768, 0b0101, 18),
        new(258, "foliage", 40, 16, -12, -32768, 0b1001, 9),
        new(259, "metal", 56, 32, 8, 24, 0b0110, 5),
        new(260, "crystal", 72, 16, 24, 18, 0b1110, 2),
        new(261, "moss", 16, 8, -4, -32768, 0b0010, 7)
    ];

    private static readonly ushort[] BlockTypeIndexById = CreateBlockTypeIndex();

    public const int TotalFrequencyWeight = 181;

    public static int CellIndex(int x, int y, int z) =>
        checked((z * ChunkDimension + y) * ChunkDimension + x);

    public static int BlockIdForCell(int seed, int chunkIndex, int x, int y, int z)
    {
        int sectionPattern = SectionIndex(x, y, z) % 6;
        if (sectionPattern == 0)
        {
            return AirBlockId;
        }

        if (sectionPattern == 1)
        {
            return 256;
        }

        if (sectionPattern == 2)
        {
            return 257;
        }

        int hash = Hash(seed, chunkIndex, x, y, z);
        return sectionPattern switch
        {
            3 => (hash & 31) == 0 ? 259 : 256,
            4 => (hash % 11) switch
            {
                0 or 1 or 2 or 3 or 4 => 257,
                5 or 6 or 7 => 258,
                8 => 261,
                _ => AirBlockId
            },
            _ => (hash % 13) switch
            {
                0 or 1 => AirBlockId,
                2 or 3 or 4 => 257,
                5 or 6 => 258,
                7 or 8 => 259,
                9 or 10 => 260,
                _ => 261
            }
        };
    }

    public static short DensityForCell(int seed, int chunkIndex, int x, int y, int z, int blockId)
    {
        if (IsAir(blockId))
        {
            return short.MinValue;
        }

        int value = Hash(seed, chunkIndex, x, y, z) % 241 - 120;
        return checked((short)(value + BlockTypeForId(blockId).DensityBias));
    }

    public static bool IsAir(int blockId) => blockId == AirBlockId;

    public static bool IsOccupied(int blockId) => !IsAir(blockId);

    public static bool IsTransparent(int blockId) => IsOccupied(blockId) && BlockTypeForId(blockId).SolidThreshold < 0;

    public static bool IsOpaque(int blockId) => IsOccupied(blockId) && !IsTransparent(blockId);

    public static bool IsSolid(short density, int blockId) => IsOccupied(blockId);

    public static bool FaceVisible(int currentBlockId, int neighborBlockId)
    {
        if (IsAir(currentBlockId))
        {
            return false;
        }

        if (IsOpaque(currentBlockId))
        {
            return IsAir(neighborBlockId) || IsTransparent(neighborBlockId);
        }

        return IsAir(neighborBlockId)
            || IsOpaque(neighborBlockId)
            || neighborBlockId != currentBlockId;
    }

    public static int AlignUp(int value, int alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
        int remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    public static int StageBytesForFace(int blockId)
    {
        return StageBytesForType(BlockTypeForId(blockId));
    }

    public static int StageBytesForType(BlockTypeDescriptor type)
    {
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

    public static long DigestZeroBytes(long state, int count)
    {
        for (int index = 0; index < count; index++)
        {
            state = DigestStep(state, 0);
        }

        return state;
    }

    public static long DigestMaterializedOutput(
        long state,
        ReadOnlySpan<Vertex> vertices,
        ReadOnlySpan<int> indices,
        ReadOnlySpan<PayloadSlice> slices,
        ReadOnlySpan<byte> upload)
    {
        for (int index = 0; index < vertices.Length; index++)
        {
            Vertex value = vertices[index];
            state = DigestStep(state, value.X);
            state = DigestStep(state, value.Y);
            state = DigestStep(state, value.Z);
            state = DigestStep(state, value.Face);
            state = DigestStep(state, value.Corner);
            state = DigestStep(state, value.BlockId);
        }

        for (int index = 0; index < indices.Length; index++)
        {
            state = DigestStep(state, indices[index]);
        }

        for (int index = 0; index < slices.Length; index++)
        {
            PayloadSlice value = slices[index];
            state = DigestStep(state, value.Offset);
            state = DigestStep(state, value.Length);
            state = DigestStep(state, value.Alignment);
            state = DigestStep(state, value.StageMask);
            state = DigestStep(state, value.BlockId);
            state = DigestStep(state, value.CellIndex);
        }

        return DigestBytes(state, upload);
    }

    public static int FaceCount(int mask) => BitOperations.PopCount((uint)mask);

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

        return (uint)nx >= ChunkDimension || (uint)ny >= ChunkDimension || (uint)nz >= ChunkDimension
            ? -1
            : CellIndex(nx, ny, nz);
    }

    public static int FaceMaskFromManaged(int cellIndex, ReadOnlySpan<short> densities, ReadOnlySpan<ushort> materials)
    {
        _ = densities;
        int current = materials[cellIndex];
        int mask = 0;
        for (int face = 0; face < FacesPerCell; face++)
        {
            int neighbor = NeighborIndex(cellIndex, face);
            int neighborBlock = neighbor < 0 ? AirBlockId : materials[neighbor];
            if (FaceVisible(current, neighborBlock))
            {
                mask |= 1 << face;
            }
        }

        return mask;
    }

    public static int FaceMaskFromCells(int cellIndex, ReadOnlySpan<VoxelCell> cells)
    {
        int current = cells[cellIndex].BlockId;
        int mask = 0;
        for (int face = 0; face < FacesPerCell; face++)
        {
            int neighbor = NeighborIndex(cellIndex, face);
            int neighborBlock = neighbor < 0 ? AirBlockId : cells[neighbor].BlockId;
            if (FaceVisible(current, neighborBlock))
            {
                mask |= 1 << face;
            }
        }

        return mask;
    }

    public static int PayloadByte(int seed, int cellIndex, int blockId, int slot)
    {
        BlockTypeDescriptor type = BlockTypeForId(blockId);
        return (seed + cellIndex * 11 + blockId * 37 + slot * 17 + type.StageMask * 13) & 0xFF;
    }

    public static BlockTypeDescriptor BlockTypeForId(int blockId)
    {
        if ((uint)blockId > ushort.MaxValue)
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

    public static int BlockTypeIndexForId(int blockId)
    {
        if ((uint)blockId > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(blockId));
        }

        ushort index = BlockTypeIndexById[blockId];
        if (index >= BlockTypes.Length || BlockTypes[index].Id != blockId)
        {
            throw new ArgumentOutOfRangeException(nameof(blockId));
        }

        return index;
    }

    public static int SectionIndex(int x, int y, int z) =>
        checked(((z / 16) * 4 + (y / 16)) * 4 + (x / 16));

    public static SectionSummary ClassifySection(ReadOnlySpan<ushort> materials, ReadOnlySpan<short> densities, int sectionIndex)
    {
        _ = densities;
        Span<int> counts = stackalloc int[BlockTypes.Length];
        Span<int> transparentCounts = stackalloc int[BlockTypes.Length];
        int distinct = 0;
        int transparentIds = 0;
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
                    int blockId = materials[row + x];
                    int index = BlockTypeIndexById[blockId];
                    if (counts[index]++ == 0)
                    {
                        distinct++;
                    }

                    if (IsTransparent(blockId) && transparentCounts[index]++ == 0)
                    {
                        transparentIds++;
                    }
                }
            }
        }

        SectionRepresentationKind kind = distinct == 1 && counts[0] == 4096
            ? SectionRepresentationKind.Empty
            : distinct == 1
                ? SectionRepresentationKind.Uniform
                : distinct <= 2
                    ? SectionRepresentationKind.Packed
                    : distinct <= 4
                        ? SectionRepresentationKind.Expanded
                        : SectionRepresentationKind.MultiPacked;

        int largest = 0;
        int totalTransparent = 0;
        for (int index = 0; index < transparentCounts.Length; index++)
        {
            largest = Math.Max(largest, transparentCounts[index]);
            totalTransparent += transparentCounts[index];
        }

        bool dominant = transparentIds > 0 && largest * 2 >= totalTransparent;
        return new SectionSummary(kind, transparentIds, dominant, transparentIds > 1 && !dominant);
    }

    public static SectionSummary ClassifySection(ReadOnlySpan<VoxelCell> cells, int sectionIndex)
    {
        Span<int> counts = stackalloc int[BlockTypes.Length];
        Span<int> transparentCounts = stackalloc int[BlockTypes.Length];
        int distinct = 0;
        int transparentIds = 0;
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
                    int blockId = cells[row + x].BlockId;
                    int index = BlockTypeIndexById[blockId];
                    if (counts[index]++ == 0)
                    {
                        distinct++;
                    }

                    if (IsTransparent(blockId) && transparentCounts[index]++ == 0)
                    {
                        transparentIds++;
                    }
                }
            }
        }

        SectionRepresentationKind kind = distinct == 1 && counts[0] == 4096
            ? SectionRepresentationKind.Empty
            : distinct == 1
                ? SectionRepresentationKind.Uniform
                : distinct <= 2
                    ? SectionRepresentationKind.Packed
                    : distinct <= 4
                        ? SectionRepresentationKind.Expanded
                        : SectionRepresentationKind.MultiPacked;
        int largest = 0;
        int totalTransparent = 0;
        for (int index = 0; index < transparentCounts.Length; index++)
        {
            largest = Math.Max(largest, transparentCounts[index]);
            totalTransparent += transparentCounts[index];
        }

        bool dominant = transparentIds > 0 && largest * 2 >= totalTransparent;
        return new SectionSummary(kind, transparentIds, dominant, transparentIds > 1 && !dominant);
    }

    public static int BuildTransparentMasks(
        ReadOnlySpan<ushort> materials,
        ReadOnlySpan<short> densities,
        int sectionIndex,
        Span<ulong> destination)
    {
        _ = densities;
        Span<int> maskSlots = stackalloc int[BlockTypes.Length];
        maskSlots.Fill(-1);
        int transparentIds = 0;
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
                    int blockId = materials[cell];
                    if (!IsTransparent(blockId))
                    {
                        continue;
                    }

                    int typeIndex = BlockTypeIndexById[blockId];
                    int maskIndex = maskSlots[typeIndex];
                    if (maskIndex < 0)
                    {
                        maskIndex = maskSlots[typeIndex] = transparentIds++;
                    }

                    int local = ((z - startZ) * 16 + (y - startY)) * 16 + x - startX;
                    destination[maskIndex * TransparentMaskWordsPerId + (local >> 6)] |= 1UL << (local & 63);
                }
            }
        }

        return transparentIds;
    }

    public static int BuildTransparentMasks(ReadOnlySpan<VoxelCell> cells, int sectionIndex, Span<ulong> destination)
    {
        Span<int> maskSlots = stackalloc int[BlockTypes.Length];
        maskSlots.Fill(-1);
        int transparentIds = 0;
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
                    int blockId = cells[cell].BlockId;
                    if (!IsTransparent(blockId))
                    {
                        continue;
                    }

                    int typeIndex = BlockTypeIndexById[blockId];
                    int maskIndex = maskSlots[typeIndex];
                    if (maskIndex < 0)
                    {
                        maskIndex = maskSlots[typeIndex] = transparentIds++;
                    }

                    int local = ((z - startZ) * 16 + (y - startY)) * 16 + x - startX;
                    destination[maskIndex * TransparentMaskWordsPerId + (local >> 6)] |= 1UL << (local & 63);
                }
            }
        }

        return transparentIds;
    }

    public static int VertexValue(int cellIndex, int face, int vertex, int field, int blockId)
    {
        int x = cellIndex % ChunkDimension;
        int y = (cellIndex / ChunkDimension) % ChunkDimension;
        int z = cellIndex / (ChunkDimension * ChunkDimension);
        int corner = vertex & 3;
        int xOffset = face switch { 0 => 0, 1 => 1, _ => (corner & 1) },
            yOffset = face switch { 2 => 0, 3 => 1, _ => ((corner >> 1) & 1) },
            zOffset = face switch { 4 => 0, 5 => 1, _ => (corner ^ (corner >> 1)) & 1 };
        return field switch
        {
            0 => x + xOffset,
            1 => y + yOffset,
            2 => z + zOffset,
            3 => face,
            4 => corner,
            5 => blockId,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
    }

    public static int IndexValue(int vertexOffset, int indexOffset) =>
        vertexOffset + ((indexOffset % IndicesPerFace) switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            3 => 2,
            4 => 3,
            _ => 0
        });

    public static void ValidateBoundaryFixture()
    {
        int[] opaque = [256, 259, 260];
        int[] transparent = [257, 258, 261];
        int[] materials = [AirBlockId, .. opaque, .. transparent];
        foreach (int current in materials)
        {
            foreach (int neighbor in materials)
            {
                bool expected = !IsAir(current)
                    && (IsOpaque(current)
                        ? IsAir(neighbor) || IsTransparent(neighbor)
                        : IsAir(neighbor) || IsOpaque(neighbor) || neighbor != current);
                if (FaceVisible(current, neighbor) != expected)
                {
                    throw new InvalidDataException(
                        $"The independent opaque/transparent boundary fixture failed for {current}/{neighbor}.");
                }
            }
        }

        int first = VertexValue(CellIndex(1, 2, 3), 5, 0, 0, transparent[0]);
        int second = VertexValue(CellIndex(1, 2, 3), 5, 1, 0, transparent[0]);
        if (first == second)
        {
            throw new InvalidDataException("Face-corner fixture did not distinguish vertex work.");
        }
    }

    private static int Hash(int seed, int chunkIndex, int x, int y, int z)
    {
        unchecked
        {
            uint value = (uint)(seed * 31 + chunkIndex * 113 + x * 13 + y * 29 + z * 43 + x * z * 7);
            value ^= value >> 16;
            value *= 0x7feb352d;
            value ^= value >> 15;
            return (int)(value & 0x7fffffff);
        }
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
}
