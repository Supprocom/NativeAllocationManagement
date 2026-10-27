using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public readonly record struct PressureOutputEvidence(
    string CompleteHash,
    int OpaqueVertexLength,
    int OpaqueIndexLength,
    int OpaqueSliceLength,
    int OpaqueUploadLength,
    int TransparentVertexLength,
    int TransparentIndexLength,
    int TransparentSliceLength,
    int TransparentUploadLength);

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public readonly record struct PressureChunkPlanEntry(
    int ChunkId,
    long LogicalDemandBytes,
    long EstimatedWorkUnits,
    PressureChunkShape Shape);

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public readonly record struct GpuStageShape(
    int Stage160Count,
    int Stage168Count,
    int Stage176Count,
    int Stage192Count,
    int Stage224Count,
    int OpaqueStage160Count,
    int OpaqueStage168Count,
    int OpaqueStage176Count,
    int OpaqueStage192Count,
    int OpaqueStage224Count)
{
    public int TotalPayloadBytes { get; init; }

    public int OpaquePayloadBytes { get; init; }

    public int TransparentPayloadBytes => checked(
        TotalPayloadBytes - OpaquePayloadBytes);

    public int TotalCount => checked(
        Stage160Count
        + Stage168Count
        + Stage176Count
        + Stage192Count
        + Stage224Count);

    public int OpaqueCount => checked(
        OpaqueStage160Count
        + OpaqueStage168Count
        + OpaqueStage176Count
        + OpaqueStage192Count
        + OpaqueStage224Count);

    public int TransparentCount => checked(TotalCount - OpaqueCount);

    public int TotalBytes => checked(
        Stage160Count * 160
        + Stage168Count * 168
        + Stage176Count * 176
        + Stage192Count * 192
        + Stage224Count * 224);

    public int OpaqueBytes => checked(
        OpaqueStage160Count * 160
        + OpaqueStage168Count * 168
        + OpaqueStage176Count * 176
        + OpaqueStage192Count * 192
        + OpaqueStage224Count * 224);

    public int TransparentBytes => checked(TotalBytes - OpaqueBytes);

    public int Count(int stageBytes) =>
        stageBytes switch
        {
            160 => Stage160Count,
            168 => Stage168Count,
            176 => Stage176Count,
            192 => Stage192Count,
            224 => Stage224Count,
            _ => throw new ArgumentOutOfRangeException(
                nameof(stageBytes),
                stageBytes,
                "The GPU stage size is not registered.")
        };

    public int OpaqueCountFor(int stageBytes) =>
        stageBytes switch
        {
            160 => OpaqueStage160Count,
            168 => OpaqueStage168Count,
            176 => OpaqueStage176Count,
            192 => OpaqueStage192Count,
            224 => OpaqueStage224Count,
            _ => throw new ArgumentOutOfRangeException(
                nameof(stageBytes),
                stageBytes,
                "The GPU stage size is not registered.")
        };

    public int TransparentCountFor(int stageBytes) =>
        checked(Count(stageBytes) - OpaqueCountFor(stageBytes));

    public GpuStageShape Add(
        int stageBytes,
        int faceCount,
        int payloadBytesPerFace,
        bool opaque)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(faceCount);
        ArgumentOutOfRangeException.ThrowIfNegative(
            payloadBytesPerFace);
        GpuStageShape result = stageBytes switch
        {
            160 => this with
            {
                Stage160Count = checked(Stage160Count + faceCount),
                OpaqueStage160Count = checked(
                    OpaqueStage160Count + (opaque ? faceCount : 0))
            },
            168 => this with
            {
                Stage168Count = checked(Stage168Count + faceCount),
                OpaqueStage168Count = checked(
                    OpaqueStage168Count + (opaque ? faceCount : 0))
            },
            176 => this with
            {
                Stage176Count = checked(Stage176Count + faceCount),
                OpaqueStage176Count = checked(
                    OpaqueStage176Count + (opaque ? faceCount : 0))
            },
            192 => this with
            {
                Stage192Count = checked(Stage192Count + faceCount),
                OpaqueStage192Count = checked(
                    OpaqueStage192Count + (opaque ? faceCount : 0))
            },
            224 => this with
            {
                Stage224Count = checked(Stage224Count + faceCount),
                OpaqueStage224Count = checked(
                    OpaqueStage224Count + (opaque ? faceCount : 0))
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(stageBytes),
                stageBytes,
                "The GPU stage size is not registered.")
        };
        int payloadBytes = checked(
            faceCount * payloadBytesPerFace);
        return result with
        {
            TotalPayloadBytes = checked(
                TotalPayloadBytes + payloadBytes),
            OpaquePayloadBytes = checked(
                OpaquePayloadBytes
                + (opaque ? payloadBytes : 0))
        };
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public readonly record struct PressureChunkShape(
    int OpaqueRecordCount,
    int TransparentRecordCount,
    int OpaqueFaceCount,
    int TransparentFaceCount,
    GpuStageShape GpuStages,
    int TransparentMaskCount,
    int TransparentMaskWords,
    int EmptySections,
    int UniformSections,
    int ExpandedSections,
    int PackedSections,
    int MultiPackedSections,
    int DominantTransparentSections,
    int ResidualTransparentSections,
    int SectionDescriptorCount,
    int SectionValueCount,
    int SectionWordCount,
    int SectionStateWordCount)
{
    public int RecordCount => OpaqueRecordCount + TransparentRecordCount;

    public int FaceCount => OpaqueFaceCount + TransparentFaceCount;

    public int VertexCount => checked(FaceCount * VoxelMath.VerticesPerFace);

    public int IndexCount => checked(FaceCount * VoxelMath.IndicesPerFace);

    public int OpaqueUploadBytes => GpuStages.OpaqueBytes;

    public int TransparentUploadBytes => GpuStages.TransparentBytes;

    public int UploadBytes => GpuStages.TotalBytes;
}

public static class PressureWorkContract
{
    private static readonly int[] CanonicalSourceCycle =
    [
        5096,
        5777,
        1410,
        3451,
        2948,
        5701,
        7862,
        6207,
        16,
        1410,
        2275,
        2948,
        5,
        7894,
        6207,
        6207
    ];

    private static readonly long[] ManagedResidentBytesByDepth =
    [
        0,
        18_449_920,
        25_888_256,
        36_441_088,
        45_304_832,
        59_119_616,
        64_100_352,
        69_081_088,
        75_700_224,
        93_794_304,
        103_100_416,
        108_212_224,
        113_848_320,
        123_154_432,
        128_135_168,
        130_428_928,
        130_740_224,
        151_199_744,
        159_588_352,
        168_894_464,
        182_132_736
    ];

    public const int DefaultRetentionDepth = 20;
    public const int DefaultResidentDepth = DefaultRetentionDepth;
    public const int CanonicalPressureCycleLength = 16;
    public const int CanonicalManagedAdmissionDepth = 14;
    public const int DefaultProgressEveryChunks = 4;
    public const int CanonicalPressureSeed = 17;
    public const int CanonicalPressureCorpusLength = 1_024;
    public const int MaximumFaceRecordsPerChunk = 22_173;
    public const int MaximumVisibleFacesPerChunk = 35_086;
    public const int MaximumTransparentMaskWordsPerChunk = 1_536;
    public const int MaximumUploadBytesPerChunk = 6_468_880;
    public const int MaximumSectionDescriptorsPerChunk =
        VoxelMath.SectionsPerChunk;
    public const int MaximumSectionWordsPerSection = 384 + 36;
    public const int MaximumSectionStateWordsPerSection = 240;
    public const int MaximumSectionValuesPerChunk =
        VoxelMath.SectionsPerChunk * VoxelMath.CellsPerSection;
    public const int MaximumSectionWordsPerChunk =
        VoxelMath.SectionsPerChunk * MaximumSectionWordsPerSection;
    public const int MaximumSectionStateWordsPerChunk =
        VoxelMath.SectionsPerChunk * MaximumSectionStateWordsPerSection;
    public const int MaximumRetainedBytesPerChunk = 7_329_016;
    public static readonly int MaximumPressureStageBytesPerFace =
        CalculateMaximumPressureStageBytesPerFace();
    private static readonly int MaximumPayloadBytes =
        CalculateMaximumPayloadBytes();
    private static readonly byte[] PayloadPatterns =
        CreatePayloadPatterns();
    private static readonly int[] EnabledByteCounts =
    [
        0, 1, 1, 2,
        1, 2, 2, 3,
        1, 2, 2, 3,
        2, 3, 3, 4
    ];

    public static int PayloadPatternTableBytes =>
        PayloadPatterns.Length;

    public static ReadOnlySpan<byte> PayloadPatternTable =>
        PayloadPatterns;

    public static int PayloadPatternOffset(
        int seed,
        int stageMask) =>
        checked(
            ((((stageMask & 0b1111) << 8)
                + (seed & byte.MaxValue))
            * MaximumPayloadBytes));

    public const int CanonicalResidentCellCapacity = 655_360;
    public const int CanonicalResidentFaceRecordCapacity = 268_532;
    public const int CanonicalResidentTransparentMaskWordCapacity = 18_496;
    public const int CanonicalResidentPayloadSliceCapacity = 481_532;
    public const int CanonicalResidentUploadByteCapacity = 86_915_248;
    public const int CanonicalResidentSectionDescriptorCapacity = 160;
    public const int CanonicalResidentSectionValueCapacity = 110_980;
    public const int CanonicalResidentSectionWordCapacity = 38_674;
    public const int CanonicalResidentSectionStateWordCapacity = 30_160;
    public const int CanonicalMaximumVertexCapacity = 140_344;
    public const int CanonicalMaximumIndexCapacity = 210_516;
    public const int CanonicalMaximumArraysPerPoolBucket = 15;
    public const int CanonicalRetainedArraysPerPoolBucket = 1;
    public const long CanonicalArenaReservationBytes = 37_157_808;
    public const long CanonicalManagedPoolResidentBytes = 182_132_736;
    public const int GpuCommandPaddingBytesPerFace = 32;
    public const int OccupancyWordsPerSection = 64;
    public const int BoundaryWordsPerFace = 4;
    public const int BoundaryFaceCount = 6;
    public const int BoundaryWordsPerSection =
        BoundaryWordsPerFace * BoundaryFaceCount;

    public static readonly int MaximumVerticesPerChunk =
        checked(MaximumVisibleFacesPerChunk * VoxelMath.VerticesPerFace);

    public static readonly int MaximumIndicesPerChunk =
        checked(MaximumVisibleFacesPerChunk * VoxelMath.IndicesPerFace);

    public static long SourceInputBytesPerChunk =>
        checked((long)VoxelMath.CellsPerChunk * (sizeof(ushort) + sizeof(short)));

    public static PressureChunkPlanEntry[] CreateCanonicalChunkPlan(
        int seed,
        long requestedCumulativeDemandBytes,
        int minimumChunks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            requestedCumulativeDemandBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(
            minimumChunks);
        VoxelCell[] cells =
            GC.AllocateUninitializedArray<VoxelCell>(
                VoxelMath.CellsPerChunk);
        SectionSummary[] sections =
            GC.AllocateUninitializedArray<SectionSummary>(
                VoxelMath.SectionsPerChunk);
        List<PressureChunkPlanEntry> entries = [];
        long realizedDemand = 0;
        while (realizedDemand
                    < requestedCumulativeDemandBytes
            || entries.Count < minimumChunks
            || entries.Count % CanonicalPressureCycleLength != 0)
        {
            int chunkId = entries.Count;
            GenerateCells(
                seed,
                chunkId,
                cells);
            PressureChunkShape shape = DeriveChunkShape(
                cells,
                sections);
            long demand = CalculateLogicalDemand(shape);
            entries.Add(new PressureChunkPlanEntry(
                chunkId,
                demand,
                checked(
                    (long)VoxelMath.CellsPerChunk
                    + shape.FaceCount),
                shape));
            realizedDemand = checked(
                realizedDemand + demand);
        }

        return entries.ToArray();
    }

    public static int PressureSourceChunkIndex(int chunkId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunkId);
        return CanonicalSourceCycle[
            chunkId % CanonicalPressureCycleLength];
    }

    public static long CanonicalManagedResidentBytes(int retentionDepth)
    {
        if ((uint)retentionDepth
            >= (uint)ManagedResidentBytesByDepth.Length
            || retentionDepth == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDepth));
        }

        return ManagedResidentBytesByDepth[retentionDepth];
    }

    public static long CalculateLogicalDemand(PressureChunkShape shape) =>
        checked(
            (long)VoxelMath.CellsPerChunk * VoxelMath.VoxelCellBytes
            + (long)Math.Max(1, shape.RecordCount) * VoxelMath.FaceRecordBytes
            + (long)Math.Max(1, shape.TransparentMaskWords) * sizeof(ulong)
            + (long)shape.SectionDescriptorCount
                * VoxelMath.SectionPrerenderDescriptorBytes
            + (long)Math.Max(1, shape.SectionValueCount) * sizeof(ushort)
            + (long)Math.Max(1, shape.SectionWordCount) * sizeof(uint)
            + (long)Math.Max(1, shape.SectionStateWordCount) * sizeof(ulong)
            + (long)Math.Max(1, shape.VertexCount) * VoxelMath.VertexBytes
            + (long)Math.Max(1, shape.IndexCount) * VoxelMath.IndexBytes
            + (long)Math.Max(1, shape.FaceCount) * VoxelMath.PayloadSliceBytes
            + Math.Max(1, shape.UploadBytes));

    public static long CalculateRetainedLogicalBytes(PressureChunkShape shape) =>
        checked(
            (long)shape.SectionDescriptorCount
                * VoxelMath.SectionPrerenderDescriptorBytes
            + (long)Math.Max(1, shape.SectionValueCount) * sizeof(ushort)
            + (long)Math.Max(1, shape.SectionWordCount) * sizeof(uint)
            + (long)Math.Max(1, shape.SectionStateWordCount) * sizeof(ulong)
            + (long)Math.Max(1, shape.FaceCount) * VoxelMath.PayloadSliceBytes
            + Math.Max(1, shape.UploadBytes));

    public static void EnsurePressureCapacity(PressureChunkShape shape)
    {
        long retainedBytes = CalculateRetainedLogicalBytes(shape);
        if (shape.RecordCount > MaximumFaceRecordsPerChunk
            || shape.FaceCount > MaximumVisibleFacesPerChunk
            || shape.TransparentMaskWords > MaximumTransparentMaskWordsPerChunk
            || shape.UploadBytes > MaximumUploadBytesPerChunk
            || shape.SectionDescriptorCount > MaximumSectionDescriptorsPerChunk
            || shape.SectionValueCount > MaximumSectionValuesPerChunk
            || shape.SectionWordCount > MaximumSectionWordsPerChunk
            || shape.SectionStateWordCount > MaximumSectionStateWordsPerChunk
            || retainedBytes > MaximumRetainedBytesPerChunk)
        {
            throw new InvalidDataException(
                $"Canonical pressure shape exceeded its predeclared bounded capacity: "
                + $"{shape.RecordCount} records/{shape.FaceCount} faces/"
                + $"{shape.TransparentMaskWords} transparent-mask words/"
                + $"{shape.SectionDescriptorCount} section descriptors/"
                + $"{shape.SectionValueCount} section values/"
                + $"{shape.SectionWordCount} section words/"
                + $"{shape.SectionStateWordCount} section-state words/"
                + $"{shape.UploadBytes} upload bytes/"
                + $"{retainedBytes} retained bytes versus "
                + $"{MaximumFaceRecordsPerChunk}/"
                + $"{MaximumVisibleFacesPerChunk}/"
                + $"{MaximumTransparentMaskWordsPerChunk}/"
                + $"{MaximumSectionDescriptorsPerChunk}/"
                + $"{MaximumSectionValuesPerChunk}/"
                + $"{MaximumSectionWordsPerChunk}/"
                + $"{MaximumSectionStateWordsPerChunk}/"
                + $"{MaximumUploadBytesPerChunk}/"
                + $"{MaximumRetainedBytesPerChunk}.");
        }
    }

    public static void GenerateCells(int seed, int chunkId, Span<VoxelCell> cells)
    {
        if (cells.Length != VoxelMath.CellsPerChunk)
        {
            throw new ArgumentException("The pressure build requires one complete canonical chunk.", nameof(cells));
        }

        VoxelMath.FillPressureCells(
            seed,
            PressureSourceChunkIndex(chunkId),
            cells);
    }

    public static PressureChunkShape DeriveChunkShape(
        Span<VoxelCell> cells,
        Span<SectionSummary> sectionSummaries)
    {
        if (cells.Length != VoxelMath.CellsPerChunk)
        {
            throw new ArgumentException(
                "The cell buffer does not cover one canonical chunk.", nameof(cells));
        }

        if (sectionSummaries.Length != VoxelMath.SectionsPerChunk)
        {
            throw new ArgumentException(
                "The section summary buffer does not cover one canonical chunk.",
                nameof(sectionSummaries));
        }

        // Copy IDs and visibility kinds into the padded scratch: with its air
        // border, every neighbor read below is a plain load.
        FaceScratch scratch = t_faceScratch ??= new FaceScratch();
        Span<ushort> ids = scratch.Ids;
        Span<ushort> kinds = scratch.Kinds;
        ReadOnlySpan<byte> kindById = KindById;
        for (int z = 0; z < VoxelMath.ChunkDimension; z++)
        {
            for (int y = 0; y < VoxelMath.ChunkDimension; y++)
            {
                ReadOnlySpan<VoxelCell> row = cells.Slice(
                    VoxelMath.CellIndex(0, y, z),
                    VoxelMath.ChunkDimension);
                int padded = FaceScratch.Interior(y, z);
                for (int x = 0; x < row.Length; x++)
                {
                    ushort id = row[x].BlockId;
                    ids[padded + x] = id;
                    kinds[padded + x] = kindById[id];
                }
            }
        }

        ReadOnlySpan<BlockTypeDescriptor> types = VoxelMath.BlockTypes;
        ReadOnlySpan<bool> transparentByType = TransparentByType;
        Span<int> recordsByType = stackalloc int[types.Length];
        Span<int> facesByType = stackalloc int[types.Length];

        // Per-section type counts for the section summaries, gathered in the
        // same pass; air is added from the occupied count afterwards.
        int typeCount = types.Length;
        Span<int> sectionCounts =
            stackalloc int[VoxelMath.SectionsPerChunk * typeCount];
        Span<int> sectionTransparentCounts =
            stackalloc int[VoxelMath.SectionsPerChunk * typeCount];
        Span<int> sectionOccupied = stackalloc int[VoxelMath.SectionsPerChunk];
        Span<ushort> rowMasks = stackalloc ushort[VoxelMath.ChunkDimension];
        Vector128<ushort> airKind = Vector128.Create(AirKind);
        Vector128<ushort> transparentKind = Vector128.Create(TransparentKind);
        int cell = 0;
        for (int z = 0; z < VoxelMath.ChunkDimension; z++)
        {
            for (int y = 0; y < VoxelMath.ChunkDimension; y++)
            {
                int padded = FaceScratch.Interior(y, z);

                int sectionRow =
                    ((z / VoxelMath.SectionDimension) * VoxelMath.SectionsPerAxis
                        + y / VoxelMath.SectionDimension)
                    * VoxelMath.SectionsPerAxis;

                // Eight cells per vector: a transparent block shows a face toward
                // any other ID, air included; an opaque block toward air or
                // transparent blocks; air shows none.
                for (int x = 0; x < VoxelMath.ChunkDimension; x += Vector128<ushort>.Count)
                {
                    int at = padded + x;
                    Vector128<ushort> id = Vector128.Create<ushort>(ids.Slice(at));
                    Vector128<ushort> kind = Vector128.Create<ushort>(kinds.Slice(at));
                    Vector128<ushort> transparent = Vector128.Equals(kind, transparentKind);
                    Vector128<ushort> mask = Vector128.AndNot(
                        (FaceVisible(ids, kinds, at - 1, id, transparent) & Vector128.Create((ushort)(1 << 0)))
                        | (FaceVisible(ids, kinds, at + 1, id, transparent) & Vector128.Create((ushort)(1 << 1)))
                        | (FaceVisible(ids, kinds, at - FaceScratch.RowStride, id, transparent) & Vector128.Create((ushort)(1 << 2)))
                        | (FaceVisible(ids, kinds, at + FaceScratch.RowStride, id, transparent) & Vector128.Create((ushort)(1 << 3)))
                        | (FaceVisible(ids, kinds, at - FaceScratch.PlaneStride, id, transparent) & Vector128.Create((ushort)(1 << 4)))
                        | (FaceVisible(ids, kinds, at + FaceScratch.PlaneStride, id, transparent) & Vector128.Create((ushort)(1 << 5))),
                        Vector128.Equals(kind, airKind));
                    mask.CopyTo(rowMasks.Slice(x));

                    // Count the block's cells per type with one compare per type,
                    // not one counter update per cell. Every lane is in one section.
                    int section = sectionRow + x / VoxelMath.SectionDimension;
                    uint visibleBits = ~Vector128.ExtractMostSignificantBits(
                        Vector128.Equals(mask, Vector128<ushort>.Zero)) & 0xFF;
                    Vector128<ushort> faceCounts = FaceCounts(mask);
                    uint matchedBits = Vector128.ExtractMostSignificantBits(
                        Vector128.Equals(id, Vector128<ushort>.Zero));
                    sectionOccupied[section] +=
                        Vector128<ushort>.Count - BitOperations.PopCount(matchedBits);
                    for (int typeIndex = 1; typeIndex < typeCount; typeIndex++)
                    {
                        Vector128<ushort> match = Vector128.Equals(
                            id,
                            Vector128.Create((ushort)types[typeIndex].Id));
                        uint matchBits = Vector128.ExtractMostSignificantBits(match);
                        if (matchBits == 0)
                        {
                            continue;
                        }

                        matchedBits |= matchBits;
                        int matches = BitOperations.PopCount(matchBits);
                        sectionCounts[section * typeCount + typeIndex] += matches;
                        sectionTransparentCounts[section * typeCount + typeIndex] +=
                            transparentByType[typeIndex] ? matches : 0;
                        recordsByType[typeIndex] +=
                            BitOperations.PopCount(matchBits & visibleBits);
                        facesByType[typeIndex] += Vector128.Sum(faceCounts & match);
                    }

                    // Unregistered IDs count as type 0, as TypeIndexById maps
                    // them; a visible one fails as the registry lookup does.
                    uint unregisteredBits = ~matchedBits & 0xFF;
                    if (unregisteredBits != 0)
                    {
                        sectionCounts[section * typeCount] +=
                            BitOperations.PopCount(unregisteredBits);
                        if ((unregisteredBits & visibleBits) != 0)
                        {
                            _ = VoxelMath.RegisteredType(ids[
                                at + BitOperations.TrailingZeroCount(
                                    unregisteredBits & visibleBits)]);
                        }
                    }
                }

                Span<VoxelCell> row = cells.Slice(cell, VoxelMath.ChunkDimension);
                for (int x = 0; x < row.Length; x++)
                {
                    int mask = rowMasks[x];
                    bool transparent = kinds[padded + x] == TransparentKind;
                    ref VoxelCell target = ref row[x];
                    target.FaceMask = mask;
                    target.OpaqueMask = !transparent ? mask : 0;
                    target.TransparentMask = transparent ? mask : 0;
                }

                cell += VoxelMath.ChunkDimension;
            }
        }

        // Stage totals are linear in faces, so each type is added once.
        ReadOnlySpan<bool> transparentById = VoxelMath.TransparentById;
        int opaqueRecords = 0;
        int transparentRecords = 0;
        int opaqueFaces = 0;
        int transparentFaces = 0;
        GpuStageShape gpuStages = default;
        for (int typeIndex = 0; typeIndex < types.Length; typeIndex++)
        {
            int faceCount = facesByType[typeIndex];
            if (faceCount == 0)
            {
                continue;
            }

            ref readonly BlockTypeDescriptor type = ref types[typeIndex];
            bool transparent = transparentById[type.Id];
            if (transparent)
            {
                transparentRecords += recordsByType[typeIndex];
                transparentFaces += faceCount;
            }
            else
            {
                opaqueRecords += recordsByType[typeIndex];
                opaqueFaces += faceCount;
            }

            gpuStages = gpuStages.Add(
                PressureStageBytes(type),
                faceCount,
                type.PayloadBytes,
                opaque: !transparent);
        }

        int empty = 0;
        int uniform = 0;
        int expanded = 0;
        int packed = 0;
        int multiPacked = 0;
        int transparentMasks = 0;
        int dominant = 0;
        int residual = 0;
        int sectionValues = 0;
        int sectionWords = 0;
        int sectionStateWords = 0;
        for (int section = 0; section < sectionSummaries.Length; section++)
        {
            Span<int> counts = sectionCounts.Slice(section * typeCount, typeCount);
            counts[0] += VoxelMath.CellsPerSection - sectionOccupied[section];
            SectionSummary summary = VoxelMath.SummarizeSection(
                counts,
                sectionTransparentCounts.Slice(section * typeCount, typeCount),
                cells[SectionBaseCell(section)].BlockId);
            sectionSummaries[section] = summary;
            switch (summary.Kind)
            {
                case SectionRepresentationKind.Empty: empty++; break;
                case SectionRepresentationKind.Uniform: uniform++; break;
                case SectionRepresentationKind.Expanded: expanded++; break;
                case SectionRepresentationKind.Packed: packed++; break;
                case SectionRepresentationKind.MultiPacked: multiPacked++; break;
            }

            transparentMasks += summary.TransparentIds;
            if (summary.HasDominantTransparentId)
            {
                dominant++;
            }

            if (summary.HasResidualTransparentIds)
            {
                residual++;
            }

            sectionValues = checked(
                sectionValues + SectionValueCount(summary));
            sectionWords = checked(
                sectionWords + SectionWordCount(summary));
            sectionStateWords = checked(
                sectionStateWords + SectionStateWordCount(summary));
        }

        int maskWords = checked(transparentMasks * VoxelMath.TransparentMaskWordsPerId);
        PressureChunkShape shape = new(
            opaqueRecords,
            transparentRecords,
            opaqueFaces,
            transparentFaces,
            gpuStages,
            transparentMasks,
            maskWords,
            empty,
            uniform,
            expanded,
            packed,
            multiPacked,
            dominant,
            residual,
            VoxelMath.SectionsPerChunk,
            sectionValues,
            sectionWords,
            sectionStateWords);
        EnsurePressureCapacity(shape);
        return shape;
    }

    // Indexed like VoxelMath.BlockTypes.
    private static readonly int[] PressureStageBytesByType =
        Array.ConvertAll(VoxelMath.BlockTypes, PressureStageBytes);

    private const ushort AirKind = 0;
    private const ushort OpaqueKind = 1;
    private const ushort TransparentKind = 2;

    // Visibility kind per block ID; unregistered IDs count as opaque, as
    // VoxelMath.TransparentById reports them.
    private static readonly byte[] KindById = CreateKindById();

    private static byte[] CreateKindById()
    {
        byte[] lookup = new byte[ushort.MaxValue + 1];
        for (int blockId = 0; blockId < lookup.Length; blockId++)
        {
            lookup[blockId] = blockId == VoxelMath.AirBlockId
                ? (byte)AirKind
                : VoxelMath.TransparentById[blockId]
                    ? (byte)TransparentKind
                    : (byte)OpaqueKind;
        }

        return lookup;
    }

    // Indexed like VoxelMath.BlockTypes.
    private static readonly bool[] TransparentByType =
        Array.ConvertAll(VoxelMath.BlockTypes, static type => VoxelMath.TransparentById[type.Id]);

    // Set bits per lane of 6-bit face masks.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> FaceCounts(Vector128<ushort> masks)
    {
        Vector128<ushort> one = Vector128<ushort>.One;
        return (masks & one)
            + ((masks >> 1) & one)
            + ((masks >> 2) & one)
            + ((masks >> 3) & one)
            + ((masks >> 4) & one)
            + (masks >> 5);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> FaceVisible(
        ReadOnlySpan<ushort> ids,
        ReadOnlySpan<ushort> kinds,
        int neighbor,
        Vector128<ushort> id,
        Vector128<ushort> transparent)
    {
        Vector128<ushort> otherId = ~Vector128.Equals(
            Vector128.Create<ushort>(ids.Slice(neighbor)),
            id);
        Vector128<ushort> seeThrough = ~Vector128.Equals(
            Vector128.Create<ushort>(kinds.Slice(neighbor)),
            Vector128.Create(OpaqueKind));
        return Vector128.ConditionalSelect(transparent, otherId, seeThrough);
    }

    // One chunk's block IDs and visibility kinds with a one-cell air border.
    // The border is never written, so it stays air across chunks.
    private sealed class FaceScratch
    {
        internal const int Side = VoxelMath.ChunkDimension + 2;
        internal const int RowStride = Side;
        internal const int PlaneStride = Side * Side;

        internal readonly ushort[] Ids = new ushort[Side * Side * Side];
        internal readonly ushort[] Kinds = new ushort[Side * Side * Side];

        // Padded index of cell (0, y, z).
        internal static int Interior(int y, int z) =>
            (z + 1) * PlaneStride + (y + 1) * RowStride + 1;
    }

    [ThreadStatic]
    private static FaceScratch? t_faceScratch;

    public static string BuildAndVerifySectionRepresentations(
        int chunkId,
        ReadOnlySpan<VoxelCell> cells,
        ReadOnlySpan<SectionSummary> summaries,
        Span<SectionPrerenderDescriptor> descriptors,
        Span<ushort> values,
        Span<uint> words,
        Span<ulong> states)
    {
        BuildSectionRepresentations(
            cells,
            summaries,
            descriptors,
            values,
            words,
            states);
        return VerifyAndHashSectionRepresentations(
            chunkId,
            cells,
            summaries,
            descriptors,
            values,
            words,
            states);
    }

    public static string BuildAndVerifySectionRepresentations(
        int chunkId,
        ReadOnlySpan<VoxelCell> cells,
        ReadOnlySpan<SectionSummary> summaries,
        Span<SectionPrerenderDescriptor> descriptors,
        Span<ushort> values,
        Span<uint> words,
        Span<ulong> states,
        Span<ulong> transparentMasks)
    {
        BuildSectionRepresentations(
            cells,
            summaries,
            descriptors,
            values,
            words,
            states,
            transparentMasks);
        return VerifyAndHashSectionRepresentations(
            chunkId,
            cells,
            summaries,
            descriptors,
            values,
            words,
            states);
    }

    public static void BuildSectionRepresentations(
        ReadOnlySpan<VoxelCell> cells,
        ReadOnlySpan<SectionSummary> summaries,
        Span<SectionPrerenderDescriptor> descriptors,
        Span<ushort> values,
        Span<uint> words,
        Span<ulong> states) =>
        BuildSectionRepresentationsCore(
            cells,
            summaries,
            descriptors,
            values,
            words,
            states,
            [],
            buildTransparentMasks: false);

    public static void BuildSectionRepresentations(
        ReadOnlySpan<VoxelCell> cells,
        ReadOnlySpan<SectionSummary> summaries,
        Span<SectionPrerenderDescriptor> descriptors,
        Span<ushort> values,
        Span<uint> words,
        Span<ulong> states,
        Span<ulong> transparentMasks) =>
        BuildSectionRepresentationsCore(
            cells,
            summaries,
            descriptors,
            values,
            words,
            states,
            transparentMasks,
            buildTransparentMasks: true);

    private static void BuildSectionRepresentationsCore(
        ReadOnlySpan<VoxelCell> cells,
        ReadOnlySpan<SectionSummary> summaries,
        Span<SectionPrerenderDescriptor> descriptors,
        Span<ushort> values,
        Span<uint> words,
        Span<ulong> states,
        Span<ulong> transparentMasks,
        bool buildTransparentMasks)
    {
        ValidateSectionRepresentationRanges(
            cells,
            summaries,
            descriptors,
            values,
            words,
            states);
        int valueCursor = 0;
        int wordCursor = 0;
        int stateCursor = 0;
        int maskCursor = 0;
        int expectedMaskWords = 0;
        if (buildTransparentMasks)
        {
            foreach (ref readonly var sectionIndexValue in summaries)
            {
                expectedMaskWords = checked(
                    expectedMaskWords
                    + sectionIndexValue.TransparentIds
                        * VoxelMath.TransparentMaskWordsPerId);
            }

            if (transparentMasks.Length != expectedMaskWords)
            {
                throw new ArgumentException(
                    "The transparent-mask range does not match the classified sections.",
                    nameof(transparentMasks));
            }

            transparentMasks.Clear();
        }

        states.Clear();
        for (int sectionIndex = 0;
            sectionIndex < VoxelMath.SectionsPerChunk;
            sectionIndex++)
        {
            SectionSummary summary = summaries[sectionIndex];
            int valueLength = SectionValueCount(summary);
            int wordLength = SectionWordCount(summary);
            int stateLength = SectionStateWordCount(summary);
            int maskLength = buildTransparentMasks
                ? checked(
                    summary.TransparentIds
                        * VoxelMath.TransparentMaskWordsPerId)
                : 0;
            int stateStart = stateCursor;
            SpanSectionInitializationBuffer<ushort> valueBuffer = new(
                values.Slice(valueCursor, valueLength));
            SpanSectionInitializationBuffer<uint> wordBuffer = new(
                words.Slice(wordCursor, wordLength));
            SectionPrerenderDescriptor descriptor =
                BuildSectionRepresentation<
                    SpanSectionInitializationBuffer<ushort>,
                    SpanSectionInitializationBuffer<uint>>(
                cells,
                sectionIndex,
                summary,
                ref valueBuffer,
                ref wordBuffer,
                states.Slice(stateStart, stateLength),
                transparentMasks.Slice(maskCursor, maskLength),
                clearStates: false,
                clearTransparentMasks: false,
                ref valueCursor,
                ref wordCursor,
                ref stateCursor);
            descriptors[sectionIndex] = descriptor;
            maskCursor = checked(maskCursor + maskLength);
        }

        if (valueCursor != values.Length
            || wordCursor != words.Length
            || stateCursor != states.Length
            || (buildTransparentMasks
                && maskCursor != transparentMasks.Length))
        {
            throw new InvalidDataException(
                "Section prerender layout did not fill its exact typed ranges.");
        }
    }

    public static SectionPrerenderDescriptor BuildSectionRepresentation(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionSummary summary,
        Span<ushort> values,
        Span<uint> words,
        Span<ulong> states,
        ref int valueCursor,
        ref int wordCursor,
        ref int stateCursor)
    {
        SpanSectionInitializationBuffer<ushort> valueBuffer = new(values);
        SpanSectionInitializationBuffer<uint> wordBuffer = new(words);
        return BuildSectionRepresentation<
            SpanSectionInitializationBuffer<ushort>,
            SpanSectionInitializationBuffer<uint>>(
            cells,
            sectionIndex,
            summary,
            ref valueBuffer,
            ref wordBuffer,
            states,
            [],
            clearStates: true,
            clearTransparentMasks: false,
            ref valueCursor,
            ref wordCursor,
            ref stateCursor);
    }

    public static SectionPrerenderDescriptor BuildSectionRepresentation(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionSummary summary,
        Span<ushort> values,
        Span<uint> words,
        Span<ulong> states,
        Span<ulong> transparentMasks,
        ref int valueCursor,
        ref int wordCursor,
        ref int stateCursor)
    {
        SpanSectionInitializationBuffer<ushort> valueBuffer = new(values);
        SpanSectionInitializationBuffer<uint> wordBuffer = new(words);
        return BuildSectionRepresentation<
            SpanSectionInitializationBuffer<ushort>,
            SpanSectionInitializationBuffer<uint>>(
            cells,
            sectionIndex,
            summary,
            ref valueBuffer,
            ref wordBuffer,
            states,
            transparentMasks,
            clearStates: true,
            clearTransparentMasks: true,
            ref valueCursor,
            ref wordCursor,
            ref stateCursor);
    }

    private static SectionPrerenderDescriptor BuildSectionRepresentation<
        TValueBuffer,
        TWordBuffer>(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionSummary summary,
        ref TValueBuffer values,
        ref TWordBuffer words,
        Span<ulong> states,
        Span<ulong> transparentMasks,
        bool clearStates,
        bool clearTransparentMasks,
        ref int valueCursor,
        ref int wordCursor,
        ref int stateCursor)
        where TValueBuffer : ISectionInitializationBuffer<ushort>, allows ref struct
        where TWordBuffer : ISectionInitializationBuffer<uint>, allows ref struct
    {
        int valueStart = valueCursor;
        int wordStart = wordCursor;
        int stateStart = stateCursor;
        SectionPrerenderDescriptor descriptor = CreateSectionLayout(
            cells,
            sectionIndex,
            summary,
            ref valueCursor,
            ref wordCursor,
            ref stateCursor);
        if (values.Length != valueCursor - valueStart)
        {
            throw new ArgumentException(
                "The value range does not match the classified representation.",
                nameof(values));
        }

        if (words.Length != wordCursor - wordStart)
        {
            throw new ArgumentException(
                "The word range does not match the classified representation.",
                nameof(words));
        }

        if (states.Length != stateCursor - stateStart)
        {
            throw new ArgumentException(
                "The state range does not match the classified representation.",
                nameof(states));
        }

        SectionPrerenderDescriptor relative = RebaseSectionLayout(
            descriptor,
            valueStart,
            wordStart,
            stateStart);
        PopulateSectionValuesAndWords(
            cells,
            sectionIndex,
            summary,
            relative,
            ref values,
            ref words);
        if (transparentMasks.IsEmpty)
        {
            PopulateSectionStateWords(
                cells,
                sectionIndex,
                relative,
                states,
                clearStates);
        }
        else
        {
            PopulateSectionStateWordsAndMasks(
                cells,
                sectionIndex,
                summary,
                relative,
                states,
                transparentMasks,
                clearStates,
                clearTransparentMasks);
        }

        values.Complete();
        words.Complete();
        return descriptor with
        {
            ContentTag = ComputeSectionContentTag(
                relative,
                ref values,
                ref words,
                states)
        };
    }

    public static void GetSectionStorageLengths(
        SectionSummary summary,
        out int valueLength,
        out int wordLength,
        out int stateLength)
    {
        valueLength = SectionValueCount(summary);
        wordLength = SectionWordCount(summary);
        stateLength = SectionStateWordCount(summary);
    }

    public static string VerifyAndHashSectionRepresentations(
        int chunkId,
        ReadOnlySpan<VoxelCell> cells,
        ReadOnlySpan<SectionSummary> summaries,
        ReadOnlySpan<SectionPrerenderDescriptor> descriptors,
        ReadOnlySpan<ushort> values,
        ReadOnlySpan<uint> words,
        ReadOnlySpan<ulong> states)
    {
        ValidateSectionRepresentationRanges(
            cells,
            summaries,
            descriptors,
            values,
            words,
            states);
        int valueCursor = 0;
        int wordCursor = 0;
        int stateCursor = 0;
        for (int sectionIndex = 0;
            sectionIndex < VoxelMath.SectionsPerChunk;
            sectionIndex++)
        {
            SectionSummary summary = summaries[sectionIndex];
            int sectionStateStart = stateCursor;
            SectionPrerenderDescriptor expected = CreateSectionLayout(
                cells,
                sectionIndex,
                summary,
                ref valueCursor,
                ref wordCursor,
                ref stateCursor);
            SectionPrerenderDescriptor actual = descriptors[sectionIndex];
            if (actual with { ContentTag = 0 } != expected)
            {
                throw new InvalidDataException(
                    $"Section descriptor {sectionIndex} does not match its canonical representation layout.");
            }

            VerifySectionValuesAndWords(
                cells,
                sectionIndex,
                summary,
                actual,
                values,
                words);
            VerifySectionStates(
                cells,
                sectionIndex,
                actual,
                states,
                sectionStateStart,
                stateCursor - sectionStateStart);
            int contentTag = ComputeSectionContentTag(
                actual with { ContentTag = 0 },
                values,
                words,
                states);
            if (actual.ContentTag != contentTag)
            {
                throw new InvalidDataException(
                    $"Section descriptor {sectionIndex} content tag is stale.");
            }
        }

        if (valueCursor != values.Length
            || wordCursor != words.Length
            || stateCursor != states.Length)
        {
            throw new InvalidDataException(
                "Section prerender verification did not consume every typed value.");
        }

        using CanonicalHashAccumulator hash = new();
        hash.AddString("voxel-section-prerender-v1");
        hash.AddInt32(chunkId);
        hash.AddInt32(descriptors.Length);
        foreach (ref readonly var indexValue in descriptors)
        {
            AddSectionDescriptor(hash, indexValue);
        }

        hash.AddInt32(values.Length);
        foreach (ref readonly var indexValue in values)
        {
            hash.AddUInt16(indexValue);
        }

        hash.AddInt32(words.Length);
        foreach (ref readonly var indexValue in words)
        {
            hash.AddInt32(unchecked((int)indexValue));
        }

        hash.AddInt32(states.Length);
        foreach (ref readonly var indexValue in states)
        {
            hash.AddInt64(unchecked((long)indexValue));
        }

        return hash.Complete();
    }

    public static void VerifySectionRepresentation(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionSummary summary,
        SectionPrerenderDescriptor actual,
        ReadOnlySpan<ushort> values,
        ReadOnlySpan<uint> words,
        ReadOnlySpan<ulong> states,
        ref int valueCursor,
        ref int wordCursor,
        ref int stateCursor)
    {
        int valueStart = valueCursor;
        int wordStart = wordCursor;
        int stateStart = stateCursor;
        SectionPrerenderDescriptor expected = CreateSectionLayout(
            cells,
            sectionIndex,
            summary,
            ref valueCursor,
            ref wordCursor,
            ref stateCursor);
        if (actual with { ContentTag = 0 } != expected)
        {
            throw new InvalidDataException(
                $"Section descriptor {sectionIndex} does not match its canonical representation layout.");
        }

        if (values.Length != valueCursor - valueStart
            || words.Length != wordCursor - wordStart
            || states.Length != stateCursor - stateStart)
        {
            throw new InvalidDataException(
                $"Section {sectionIndex} storage does not match its canonical representation.");
        }

        SectionPrerenderDescriptor relative = RebaseSectionLayout(
            actual,
            valueStart,
            wordStart,
            stateStart);
        VerifySectionValuesAndWords(
            cells,
            sectionIndex,
            summary,
            relative,
            values,
            words);
        VerifySectionStates(
            cells,
            sectionIndex,
            relative,
            states,
            0,
            states.Length);
        int contentTag = ComputeSectionContentTag(
            relative with { ContentTag = 0 },
            values,
            words,
            states);
        if (actual.ContentTag != contentTag)
        {
            throw new InvalidDataException(
                $"Section descriptor {sectionIndex} content tag is stale.");
        }
    }

    public static void AddSectionDescriptorEvidence(
        CanonicalHashAccumulator hash,
        SectionPrerenderDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(hash);
        AddSectionDescriptor(hash, descriptor);
    }

    public static string CombineChunkEvidence(
        string sectionEvidenceHash,
        string transparentMaskEvidenceHash,
        string outputEvidenceHash)
    {
        using CanonicalHashAccumulator hash = new();
        hash.AddString("voxel-pressure-chunk-v3");
        hash.AddString(sectionEvidenceHash);
        hash.AddString(transparentMaskEvidenceHash);
        hash.AddString(outputEvidenceHash);
        return hash.Complete();
    }

    public static string HashTransparentMasks(
        int chunkId,
        ReadOnlySpan<ulong> masks)
    {
        using CanonicalHashAccumulator hash = new();
        hash.AddString("voxel-transparent-masks-v1");
        hash.AddInt32(chunkId);
        hash.AddInt32(masks.Length);
        foreach (ref readonly ulong value in masks)
        {
            hash.AddInt64(unchecked((long)value));
        }

        return hash.Complete();
    }

    public static void PopulateFaceRecords(
        ReadOnlySpan<VoxelCell> cells,
        ReadOnlySpan<SectionPrerenderDescriptor> sections,
        PressureChunkShape shape,
        Span<FaceRecord> records)
    {
        if (records.Length != Math.Max(1, shape.RecordCount))
        {
            throw new ArgumentException("The face record buffer does not match the derived pressure shape.", nameof(records));
        }

        if (sections.Length != VoxelMath.SectionsPerChunk)
        {
            throw new ArgumentException(
                "The face-record stage requires every section prerender descriptor.",
                nameof(sections));
        }

        ReadOnlySpan<BlockTypeDescriptor> types = VoxelMath.BlockTypes;
        ReadOnlySpan<byte> typeIndexById = VoxelMath.TypeIndexById;
        ReadOnlySpan<int> stageBytesByType = PressureStageBytesByType;
        ReadOnlySpan<bool> transparentById = VoxelMath.TransparentById;
        int opaque = 0;
        int transparent = 0;
        for (int cell = 0; cell < cells.Length; cell++)
        {
            ref readonly VoxelCell source = ref cells[cell];
            int mask = source.FaceMask;
            if (mask == 0)
            {
                continue;
            }

            int blockId = source.BlockId;
            int typeIndex = typeIndexById[blockId];
            ref readonly BlockTypeDescriptor type = ref types[typeIndex];
            if (type.Id != blockId)
            {
                _ = VoxelMath.RegisteredType(blockId);
            }

            ref readonly SectionPrerenderDescriptor section =
                ref sections[source.Section];
            if (section.Kind == SectionRepresentationKind.Empty)
            {
                throw new InvalidDataException(
                    "A visible face referenced an empty section representation.");
            }

            FaceRecord record = new(
                cell,
                blockId,
                mask,
                type.PayloadBytes,
                type.Alignment,
                unchecked(
                    type.StageMask
                    | ((int)section.Kind << 8)
                    | ((section.ContentTag & 0x000F_FFFF) << 12)),
                stageBytesByType[typeIndex]);
            if (transparentById[blockId])
            {
                records[
                    shape.OpaqueRecordCount
                    + transparent++] = record;
            }
            else
            {
                records[opaque++] = record;
            }
        }

        if (opaque != shape.OpaqueRecordCount
            || transparent != shape.TransparentRecordCount)
        {
            throw new InvalidDataException(
                "Face record materialization disagrees with pressure classification.");
        }
    }

    public static void BuildTransparentMasks(
        ReadOnlySpan<VoxelCell> cells,
        ReadOnlySpan<SectionSummary> sectionSummaries,
        Span<ulong> masks)
    {
        int offset = 0;
        masks.Clear();
        for (int section = 0;
            section < sectionSummaries.Length;
            section++)
        {
            int expectedWords = checked(
                sectionSummaries[section].TransparentIds
                    * VoxelMath.TransparentMaskWordsPerId);
            int written = VoxelMath.BuildTransparentMasks(
                cells,
                section,
                sectionSummaries[section].TransparentTypeMask,
                masks.Slice(offset, expectedWords));
            if (written
                != sectionSummaries[section].TransparentIds)
            {
                throw new InvalidDataException(
                    "Transparent mask emission disagrees with pressure classification.");
            }

            offset += expectedWords;
        }

        if (offset != masks.Length)
        {
            throw new InvalidDataException(
                "Transparent mask materialization did not fill its logical range.");
        }
    }

    public static int PackStream(
        int seed,
        ReadOnlySpan<FaceRecord> records,
        Span<Vertex> vertices,
        Span<int> indices,
        Span<PayloadSlice> slices,
        GpuStageBuffers stages)
    {
        int vertexCount = 0;
        int indexCount = 0;
        int sliceCount = 0;
        int enabledStageBytes = 0;
        Span<int> stageCursors = stackalloc int[5];
        foreach (ref readonly var recordIndexValue in records)
        {
            ref readonly FaceRecord record =
                ref recordIndexValue;
            int stageClass = StageClassIndex(record.StageBytes);
            int payloadSeed = unchecked(
                seed
                + record.CellIndex * 11
                + record.BlockId * 37
                + record.StageMask * 13);
            FaceVertexOrigin origin = new(record.CellIndex, record.BlockId);
            uint remainingFaces =
                unchecked((uint)record.Mask) & 0b11_1111u;
            while (remainingFaces != 0)
            {
                int face = BitOperations.TrailingZeroCount(
                    remainingFaces);
                remainingFaces &= remainingFaces - 1;
                int stageOffset = checked(
                    stageCursors[stageClass] * record.StageBytes);
                Span<byte> stage = stages.GetStage(
                    record.StageBytes,
                    stageOffset);
                stageCursors[stageClass]++;
                int cursor = 0;
                enabledStageBytes = checked(
                    enabledStageBytes
                    + WritePayload(
                        stage,
                        record.PayloadBytes,
                        payloadSeed,
                        record.StageMask));
                cursor = record.PayloadBytes;

                int vertexOffset = vertexCount;
                Span<Vertex> faceVertices = vertices.Slice(
                    vertexOffset,
                    VoxelMath.VerticesPerFace);
                WriteFaceVertices(
                    face,
                    origin,
                    MemoryMarshal.Cast<Vertex, int>(faceVertices));
                vertexCount += VoxelMath.VerticesPerFace;

                WriteVertices(
                    faceVertices,
                    stage,
                    ref cursor);
                int indexOffset = indexCount;
                Span<int> faceIndices = indices.Slice(
                    indexOffset,
                    VoxelMath.IndicesPerFace);
                WriteFaceIndices(
                    vertexOffset,
                    faceIndices);
                indexCount += VoxelMath.IndicesPerFace;

                WriteInt32Values(
                    faceIndices,
                    stage,
                    ref cursor);
                stage[cursor..].Clear();
                slices[sliceCount++] = new PayloadSlice(
                    stageOffset,
                    record.StageBytes,
                    record.Alignment,
                    record.StageMask,
                    record.BlockId,
                    record.CellIndex);
            }
        }

        if (vertexCount != vertices.Length
            || indexCount != indices.Length
            || sliceCount != slices.Length
            || stageCursors[0] != stages.Stage160.Length
            || stageCursors[1] != stages.Stage168.Length
            || stageCursors[2] != stages.Stage176.Length
            || stageCursors[3] != stages.Stage192.Length
            || stageCursors[4] != stages.Stage224.Length)
        {
            throw new InvalidDataException(
                "Pressure packing did not fill every exact output range.");
        }

        return enabledStageBytes;
    }

    public static void PackAliasedScatterStream(
        ReadOnlySpan<FaceRecord> records,
        Span<Vertex> vertices,
        Span<int> indices,
        Span<PayloadSlice> slices)
    {
        if (vertices.Length % VoxelMath.VerticesPerFace != 0
            || indices.Length % VoxelMath.IndicesPerFace != 0
            || vertices.Length / VoxelMath.VerticesPerFace
                != slices.Length
            || indices.Length / VoxelMath.IndicesPerFace
                != slices.Length)
        {
            throw new InvalidDataException(
                "Aliased scatter packing did not receive exact output ranges.");
        }

        int vertexCount = 0;
        int indexCount = 0;
        int sliceCount = 0;
        Span<int> stageCursors = stackalloc int[5];
        Span<int> vertexInts = MemoryMarshal.Cast<Vertex, int>(vertices);
        ref int indexBase = ref MemoryMarshal.GetReference(indices);
        ref PayloadSlice sliceBase = ref MemoryMarshal.GetReference(slices);
        foreach (ref readonly var recordIndexValue in records)
        {
            ref readonly FaceRecord record =
                ref recordIndexValue;
            int stageClass = StageClassIndex(
                record.StageBytes);
            FaceVertexOrigin origin = new(record.CellIndex, record.BlockId);
            uint remainingFaces =
                unchecked((uint)record.Mask) & 0b11_1111u;
            while (remainingFaces != 0)
            {
                int face = BitOperations.TrailingZeroCount(
                    remainingFaces);
                remainingFaces &= remainingFaces - 1;
                if (vertexCount > vertices.Length
                        - VoxelMath.VerticesPerFace
                    || indexCount > indices.Length
                        - VoxelMath.IndicesPerFace
                    || sliceCount >= slices.Length)
                {
                    throw new InvalidDataException(
                        "Aliased scatter packing exceeded its output ranges.");
                }

                int stageOffset = stageCursors[stageClass];
                stageCursors[stageClass] = checked(
                    stageOffset + record.StageBytes);

                int vertexOffset = vertexCount;
                WriteFaceVertices(
                    face,
                    origin,
                    vertexInts.Slice(
                        vertexOffset * VertexInts,
                        VoxelMath.VerticesPerFace * VertexInts));
                vertexCount += VoxelMath.VerticesPerFace;

                WriteFaceIndices(
                    ref Unsafe.Add(ref indexBase, indexCount),
                    vertexOffset);
                indexCount += VoxelMath.IndicesPerFace;

                Unsafe.Add(ref sliceBase, sliceCount++) = new PayloadSlice(
                    stageOffset,
                    record.StageBytes,
                    record.Alignment,
                    record.StageMask,
                    record.BlockId,
                    record.CellIndex);
            }
        }

        if (vertexCount != vertices.Length
            || indexCount != indices.Length
            || sliceCount != slices.Length)
        {
            throw new InvalidDataException(
                "Aliased scatter packing did not fill every output range.");
        }
    }

    private static int StageClassIndex(int stageBytes) =>
        stageBytes switch
        {
            160 => 0,
            168 => 1,
            176 => 2,
            192 => 3,
            224 => 4,
            _ => throw new ArgumentOutOfRangeException(
                nameof(stageBytes),
                stageBytes,
                "The GPU stage size is not registered.")
        };

    public static int PackStream<
        TVertexBuffer,
        TIndexBuffer,
        TSliceBuffer,
        TUploadBuffer>(
        int seed,
        ReadOnlySpan<FaceRecord> records,
        ref TVertexBuffer vertices,
        ref TIndexBuffer indices,
        ref TSliceBuffer slices,
        ref TUploadBuffer upload)
        where TVertexBuffer : IOutputInitializationBuffer<Vertex>, allows ref struct
        where TIndexBuffer : IOutputInitializationBuffer<int>, allows ref struct
        where TSliceBuffer : IOutputInitializationBuffer<PayloadSlice>, allows ref struct
        where TUploadBuffer : IOutputInitializationBuffer<byte>, allows ref struct
    {
        int vertexCount = 0;
        int indexCount = 0;
        int sliceCount = 0;
        int offset = 0;
        int enabledStageBytes = 0;
        Span<Vertex> faceVertices =
            stackalloc Vertex[VoxelMath.VerticesPerFace];
        Span<int> faceIndices =
            stackalloc int[VoxelMath.IndicesPerFace];
        Span<byte> faceUpload =
            stackalloc byte[MaximumPressureStageBytesPerFace];
        foreach (ref readonly var recordIndexValue in records)
        {
            ref readonly FaceRecord record = ref recordIndexValue;
            int payloadSeed = unchecked(
                seed
                + record.CellIndex * 11
                + record.BlockId * 37
                + record.StageMask * 13);
            int x = record.CellIndex % VoxelMath.ChunkDimension;
            int yz = record.CellIndex / VoxelMath.ChunkDimension;
            int y = yz % VoxelMath.ChunkDimension;
            int z = yz / VoxelMath.ChunkDimension;
            uint remainingFaces =
                unchecked((uint)record.Mask) & 0b11_1111u;
            while (remainingFaces != 0)
            {
                int face = BitOperations.TrailingZeroCount(
                    remainingFaces);
                remainingFaces &= remainingFaces - 1;
                int alignedOffset = VoxelMath.AlignUp(offset, record.Alignment);
                upload.Fill(offset, alignedOffset - offset, 0);
                offset = alignedOffset;
                if (record.StageBytes > faceUpload.Length)
                {
                    throw new InvalidDataException(
                        "The face upload range exceeds the registered runtime stage bound.");
                }

                Span<byte> stage = faceUpload[..record.StageBytes];
                int cursor = 0;
                enabledStageBytes = checked(
                    enabledStageBytes
                    + WritePayload(
                        stage,
                        record.PayloadBytes,
                        payloadSeed,
                        record.StageMask));
                cursor = record.PayloadBytes;

                int vertexOffset = vertexCount;
                WriteFaceVertices(
                    x,
                    y,
                    z,
                    record.BlockId,
                    face,
                    faceVertices);

                WriteVertices(
                    faceVertices,
                    stage,
                    ref cursor);
                vertices.Write(vertexCount, faceVertices);
                vertexCount += faceVertices.Length;
                WriteFaceIndices(
                    vertexOffset,
                    faceIndices);

                WriteInt32Values(
                    faceIndices,
                    stage,
                    ref cursor);
                indices.Write(indexCount, faceIndices);
                indexCount += faceIndices.Length;
                stage[cursor..].Clear();
                upload.Write(offset, stage);
                slices.Write(
                    sliceCount++,
                    new PayloadSlice(
                        offset,
                        record.StageBytes,
                        record.Alignment,
                        record.StageMask,
                        record.BlockId,
                        record.CellIndex));
                offset = checked(offset + record.StageBytes);
            }
        }

        if (vertexCount != vertices.Length
            || indexCount != indices.Length
            || sliceCount != slices.Length
            || offset != upload.Length)
        {
            throw new InvalidDataException("Pressure packing did not fill every exact output range.");
        }

        return enabledStageBytes;
    }

    public static PressureOutputEvidence VerifyAndHashOutput(
        int seed,
        ReadOnlySpan<FaceRecord> opaqueRecords,
        ReadOnlySpan<Vertex> opaqueVertices,
        ReadOnlySpan<int> opaqueIndices,
        ReadOnlySpan<PayloadSlice> opaqueSlices,
        ReadOnlySpan<FaceRecord> transparentRecords,
        ReadOnlySpan<Vertex> transparentVertices,
        ReadOnlySpan<int> transparentIndices,
        ReadOnlySpan<PayloadSlice> transparentSlices,
        GpuStageBuffers opaqueStages,
        GpuStageBuffers transparentStages)
    {
        VerifyTypedStream(
            opaqueRecords,
            opaqueVertices,
            opaqueIndices);
        VerifyTypedStream(
            transparentRecords,
            transparentVertices,
            transparentIndices);
        VerifyRetainedStream(
            seed,
            opaqueRecords,
            opaqueSlices,
            opaqueStages);
        VerifyRetainedStream(
            seed,
            transparentRecords,
            transparentSlices,
            transparentStages);
        int opaqueUploadLength = SumStageLengths(opaqueSlices);
        int transparentUploadLength =
            SumStageLengths(transparentSlices);
        string complete = ComputeTypedOutputHash(
            opaqueVertices,
            opaqueIndices,
            opaqueSlices,
            transparentVertices,
            transparentIndices,
            transparentSlices,
            opaqueStages,
            transparentStages);
        return new PressureOutputEvidence(
            complete,
            opaqueVertices.Length,
            opaqueIndices.Length,
            opaqueSlices.Length,
            opaqueUploadLength,
            transparentVertices.Length,
            transparentIndices.Length,
            transparentSlices.Length,
            transparentUploadLength);
    }

    public static PressureOutputEvidence VerifyAndHashScatterOutput(
        int seed,
        ReadOnlySpan<byte> payloadPatterns,
        ReadOnlySpan<FaceRecord> opaqueRecords,
        ReadOnlySpan<Vertex> opaqueVertices,
        ReadOnlySpan<int> opaqueIndices,
        ReadOnlySpan<PayloadSlice> opaqueSlices,
        ReadOnlySpan<FaceRecord> transparentRecords,
        ReadOnlySpan<Vertex> transparentVertices,
        ReadOnlySpan<int> transparentIndices,
        ReadOnlySpan<PayloadSlice> transparentSlices)
    {
        ValidatePayloadPatternTable(payloadPatterns);
        VerifyTypedStream(
            opaqueRecords,
            opaqueVertices,
            opaqueIndices);
        VerifyTypedStream(
            transparentRecords,
            transparentVertices,
            transparentIndices);
        VerifyScatterRetainedStream(
            seed,
            opaqueRecords,
            opaqueSlices,
            payloadPatterns);
        VerifyScatterRetainedStream(
            seed,
            transparentRecords,
            transparentSlices,
            payloadPatterns);
        int opaqueUploadLength = SumStageLengths(
            opaqueSlices);
        int transparentUploadLength = SumStageLengths(
            transparentSlices);
        string complete = ComputeScatterOutputHash(
            seed,
            payloadPatterns,
            opaqueRecords,
            opaqueVertices,
            opaqueIndices,
            opaqueSlices,
            transparentRecords,
            transparentVertices,
            transparentIndices,
            transparentSlices);
        return new PressureOutputEvidence(
            complete,
            opaqueVertices.Length,
            opaqueIndices.Length,
            opaqueSlices.Length,
            opaqueUploadLength,
            transparentVertices.Length,
            transparentIndices.Length,
            transparentSlices.Length,
            transparentUploadLength);
    }

    public static PressureOutputEvidence DescribeOutput(
        PressureChunkShape shape) =>
        new(
            string.Empty,
            checked(
                shape.OpaqueFaceCount
                * VoxelMath.VerticesPerFace),
            checked(
                shape.OpaqueFaceCount
                * VoxelMath.IndicesPerFace),
            shape.OpaqueFaceCount,
            shape.OpaqueUploadBytes,
            checked(
                shape.TransparentFaceCount
                * VoxelMath.VerticesPerFace),
            checked(
                shape.TransparentFaceCount
                * VoxelMath.IndicesPerFace),
            shape.TransparentFaceCount,
            shape.TransparentUploadBytes);

    public static PressureOutputEvidence VerifyRetainedOutput(
        PressureOutputEvidence expected,
        int seed,
        ReadOnlySpan<FaceRecord> opaqueRecords,
        ReadOnlySpan<Vertex> opaqueVertices,
        ReadOnlySpan<int> opaqueIndices,
        ReadOnlySpan<PayloadSlice> opaqueSlices,
        ReadOnlySpan<FaceRecord> transparentRecords,
        ReadOnlySpan<Vertex> transparentVertices,
        ReadOnlySpan<int> transparentIndices,
        ReadOnlySpan<PayloadSlice> transparentSlices,
        GpuStageBuffers opaqueStages,
        GpuStageBuffers transparentStages)
    {
        ValidateRetainedOutputLengths(
            expected,
            opaqueVertices,
            opaqueIndices,
            opaqueSlices,
            transparentVertices,
            transparentIndices,
            transparentSlices);

        VerifyTypedStream(
            opaqueRecords,
            opaqueVertices,
            opaqueIndices);
        VerifyTypedStream(
            transparentRecords,
            transparentVertices,
            transparentIndices);
        VerifyRetainedStream(
            seed,
            opaqueRecords,
            opaqueSlices,
            opaqueStages);
        VerifyRetainedStream(
            seed,
            transparentRecords,
            transparentSlices,
            transparentStages);
        string retainedHash = ComputeTypedOutputHash(
            opaqueVertices,
            opaqueIndices,
            opaqueSlices,
            transparentVertices,
            transparentIndices,
            transparentSlices,
            opaqueStages,
            transparentStages);
        if (expected.CompleteHash.Length != 0
            && !string.Equals(
                retainedHash,
                expected.CompleteHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Retained output values changed before the upload consumer boundary.");
        }

        return expected with { CompleteHash = retainedHash };
    }

    public static PressureOutputEvidence VerifyRetainedScatterOutput(
        PressureOutputEvidence expected,
        int seed,
        ReadOnlySpan<byte> payloadPatterns,
        ReadOnlySpan<FaceRecord> opaqueRecords,
        ReadOnlySpan<Vertex> opaqueVertices,
        ReadOnlySpan<int> opaqueIndices,
        ReadOnlySpan<PayloadSlice> opaqueSlices,
        ReadOnlySpan<FaceRecord> transparentRecords,
        ReadOnlySpan<Vertex> transparentVertices,
        ReadOnlySpan<int> transparentIndices,
        ReadOnlySpan<PayloadSlice> transparentSlices)
    {
        ValidateRetainedOutputLengths(
            expected,
            opaqueVertices,
            opaqueIndices,
            opaqueSlices,
            transparentVertices,
            transparentIndices,
            transparentSlices);
        ValidatePayloadPatternTable(payloadPatterns);
        VerifyTypedStream(
            opaqueRecords,
            opaqueVertices,
            opaqueIndices);
        VerifyTypedStream(
            transparentRecords,
            transparentVertices,
            transparentIndices);
        VerifyScatterRetainedStream(
            seed,
            opaqueRecords,
            opaqueSlices,
            payloadPatterns);
        VerifyScatterRetainedStream(
            seed,
            transparentRecords,
            transparentSlices,
            payloadPatterns);

        string retainedHash = ComputeScatterOutputHash(
            seed,
            payloadPatterns,
            opaqueRecords,
            opaqueVertices,
            opaqueIndices,
            opaqueSlices,
            transparentRecords,
            transparentVertices,
            transparentIndices,
            transparentSlices);
        if (expected.CompleteHash.Length != 0
            && !string.Equals(
                retainedHash,
                expected.CompleteHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Retained scatter output changed before upload.");
        }

        return expected with { CompleteHash = retainedHash };
    }

    private static void ValidateRetainedOutputLengths(
        PressureOutputEvidence expected,
        ReadOnlySpan<Vertex> opaqueVertices,
        ReadOnlySpan<int> opaqueIndices,
        ReadOnlySpan<PayloadSlice> opaqueSlices,
        ReadOnlySpan<Vertex> transparentVertices,
        ReadOnlySpan<int> transparentIndices,
        ReadOnlySpan<PayloadSlice> transparentSlices)
    {
        if (opaqueVertices.Length != expected.OpaqueVertexLength
            || opaqueIndices.Length != expected.OpaqueIndexLength
            || opaqueSlices.Length != expected.OpaqueSliceLength
            || transparentVertices.Length
                != expected.TransparentVertexLength
            || transparentIndices.Length
                != expected.TransparentIndexLength
            || transparentSlices.Length != expected.TransparentSliceLength
            || SumStageLengths(opaqueSlices)
                != expected.OpaqueUploadLength
            || SumStageLengths(transparentSlices)
                != expected.TransparentUploadLength)
        {
            throw new InvalidDataException(
                "Retained output lengths changed before the upload consumer boundary.");
        }
    }

    private static void ValidatePayloadPatternTable(
        ReadOnlySpan<byte> payloadPatterns)
    {
        if (payloadPatterns.Length
            != PayloadPatternTableBytes)
        {
            throw new InvalidDataException(
                "The aliased payload pattern table has an invalid length.");
        }
    }

    private static int SumStageLengths(
        ReadOnlySpan<PayloadSlice> slices)
    {
        int length = 0;
        foreach (ref readonly var indexValue in slices)
        {
            length = checked(length + indexValue.Length);
        }

        return length;
    }

    private static void ValidateStageCoverage(
        ReadOnlySpan<PayloadSlice> slices,
        GpuStageBuffers stages)
    {
        Span<int> cursors = stackalloc int[5];
        foreach (ref readonly var indexValue in slices)
        {
            PayloadSlice slice = indexValue;
            BlockTypeDescriptor type =
                VoxelMath.BlockTypeForId(slice.BlockId);
            int stageBytes = PressureStageBytes(type);
            int stageClass = StageClassIndex(stageBytes);
            if (slice.Length != stageBytes
                || slice.Alignment != type.Alignment
                || slice.Offset != cursors[stageClass])
            {
                throw new InvalidDataException(
                    "The GPU stage descriptor does not match its typed stream.");
            }

            _ = stages.GetStage(stageBytes, slice.Offset);
            cursors[stageClass] = checked(
                cursors[stageClass] + stageBytes);
        }

        if (cursors[0] != stages.GetAllBytes(160).Length
            || cursors[1] != stages.GetAllBytes(168).Length
            || cursors[2] != stages.GetAllBytes(176).Length
            || cursors[3] != stages.GetAllBytes(192).Length
            || cursors[4] != stages.GetAllBytes(224).Length)
        {
            throw new InvalidDataException(
                "The typed GPU streams contain trailing or missing records.");
        }
    }

    private static string ComputeTypedOutputHash(
        ReadOnlySpan<Vertex> opaqueVertices,
        ReadOnlySpan<int> opaqueIndices,
        ReadOnlySpan<PayloadSlice> opaqueSlices,
        ReadOnlySpan<Vertex> transparentVertices,
        ReadOnlySpan<int> transparentIndices,
        ReadOnlySpan<PayloadSlice> transparentSlices,
        GpuStageBuffers opaqueStages,
        GpuStageBuffers transparentStages)
    {
        using CanonicalHashAccumulator hash = new();
        hash.AddString("voxel-pressure-typed-output-v1");
        hash.AddVertices(opaqueVertices);
        hash.AddInt32Values(opaqueIndices);
        hash.AddPayloadSlices(opaqueSlices);
        AddStageBuffers(hash, opaqueStages);
        hash.AddVertices(transparentVertices);
        hash.AddInt32Values(transparentIndices);
        hash.AddPayloadSlices(transparentSlices);
        AddStageBuffers(hash, transparentStages);
        return hash.Complete();
    }

    private static string ComputeScatterOutputHash(
        int seed,
        ReadOnlySpan<byte> payloadPatterns,
        ReadOnlySpan<FaceRecord> opaqueRecords,
        ReadOnlySpan<Vertex> opaqueVertices,
        ReadOnlySpan<int> opaqueIndices,
        ReadOnlySpan<PayloadSlice> opaqueSlices,
        ReadOnlySpan<FaceRecord> transparentRecords,
        ReadOnlySpan<Vertex> transparentVertices,
        ReadOnlySpan<int> transparentIndices,
        ReadOnlySpan<PayloadSlice> transparentSlices)
    {
        using CanonicalHashAccumulator hash = new();
        hash.AddString("voxel-pressure-typed-output-v1");
        hash.AddVertices(opaqueVertices);
        hash.AddInt32Values(opaqueIndices);
        hash.AddPayloadSlices(opaqueSlices);
        AddScatterStageBuffers(
            hash,
            seed,
            payloadPatterns,
            opaqueRecords,
            opaqueVertices,
            opaqueIndices);
        hash.AddVertices(transparentVertices);
        hash.AddInt32Values(transparentIndices);
        hash.AddPayloadSlices(transparentSlices);
        AddScatterStageBuffers(
            hash,
            seed,
            payloadPatterns,
            transparentRecords,
            transparentVertices,
            transparentIndices);
        return hash.Complete();
    }

    private static void AddScatterStageBuffers(
        CanonicalHashAccumulator hash,
        int seed,
        ReadOnlySpan<byte> payloadPatterns,
        ReadOnlySpan<FaceRecord> records,
        ReadOnlySpan<Vertex> vertices,
        ReadOnlySpan<int> indices)
    {
        AddScatterStageBuffer(
            hash,
            seed,
            payloadPatterns,
            records,
            vertices,
            indices,
            160);
        AddScatterStageBuffer(
            hash,
            seed,
            payloadPatterns,
            records,
            vertices,
            indices,
            168);
        AddScatterStageBuffer(
            hash,
            seed,
            payloadPatterns,
            records,
            vertices,
            indices,
            176);
        AddScatterStageBuffer(
            hash,
            seed,
            payloadPatterns,
            records,
            vertices,
            indices,
            192);
        AddScatterStageBuffer(
            hash,
            seed,
            payloadPatterns,
            records,
            vertices,
            indices,
            224);
    }

    private static void AddScatterStageBuffer(
        CanonicalHashAccumulator hash,
        int seed,
        ReadOnlySpan<byte> payloadPatterns,
        ReadOnlySpan<FaceRecord> records,
        ReadOnlySpan<Vertex> vertices,
        ReadOnlySpan<int> indices,
        int stageBytes)
    {
        long stageLength = 0;
        foreach (ref readonly var recordIndexValue in records)
        {
            ref readonly FaceRecord record =
                ref recordIndexValue;
            if (record.StageBytes == stageBytes)
            {
                stageLength = checked(
                    stageLength
                    + (long)VoxelMath.FaceCount(record.Mask)
                        * stageBytes);
            }
        }

        hash.BeginByteSequence(stageLength);
        Span<byte> zeroPadding =
            stackalloc byte[MaximumPressureStageBytesPerFace];
        zeroPadding.Clear();
        int faceOffset = 0;
        foreach (ref readonly var recordIndexValue in records)
        {
            ref readonly FaceRecord record =
                ref recordIndexValue;
            int payloadSeed = unchecked(
                seed
                + record.CellIndex * 11
                + record.BlockId * 37
                + record.StageMask * 13);
            ReadOnlySpan<byte> payload =
                GetPayloadPattern(
                    payloadPatterns,
                    record.PayloadBytes,
                    payloadSeed,
                    record.StageMask);
            uint remainingFaces =
                unchecked((uint)record.Mask) & 0b11_1111u;
            while (remainingFaces != 0)
            {
                remainingFaces &= remainingFaces - 1;
                if (record.StageBytes == stageBytes)
                {
                    hash.AppendByteSequencePart(payload);
                    AppendScatterVertices(
                        hash,
                        vertices.Slice(
                            checked(
                                faceOffset
                                * VoxelMath.VerticesPerFace),
                            VoxelMath.VerticesPerFace));
                    AppendScatterIndices(
                        hash,
                        indices.Slice(
                            checked(
                                faceOffset
                                * VoxelMath.IndicesPerFace),
                            VoxelMath.IndicesPerFace));
                    int paddingLength = checked(
                        stageBytes
                        - record.PayloadBytes
                        - VoxelMath.VerticesPerFace
                            * VoxelMath.VertexBytes
                        - VoxelMath.IndicesPerFace
                            * VoxelMath.IndexBytes);
                    if (paddingLength < 0)
                    {
                        throw new InvalidDataException(
                            "A scatter stage has an invalid size.");
                    }

                    hash.AppendByteSequencePart(
                        zeroPadding[..paddingLength]);
                }

                faceOffset++;
            }
        }

        if (checked(
                faceOffset * VoxelMath.VerticesPerFace)
                != vertices.Length
            || checked(
                faceOffset * VoxelMath.IndicesPerFace)
                != indices.Length)
        {
            throw new InvalidDataException(
                "The scatter upload ranges do not match.");
        }
    }

    private static void AppendScatterVertices(
        CanonicalHashAccumulator hash,
        ReadOnlySpan<Vertex> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            hash.AppendByteSequencePart(
                MemoryMarshal.AsBytes(values));
            return;
        }

        Span<byte> encoded = stackalloc byte[
            VoxelMath.VerticesPerFace
            * VoxelMath.VertexBytes];
        int offset = 0;
        WriteVertices(values, encoded, ref offset);
        hash.AppendByteSequencePart(encoded[..offset]);
    }

    private static void AppendScatterIndices(
        CanonicalHashAccumulator hash,
        ReadOnlySpan<int> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            hash.AppendByteSequencePart(
                MemoryMarshal.AsBytes(values));
            return;
        }

        Span<byte> encoded = stackalloc byte[
            VoxelMath.IndicesPerFace
            * VoxelMath.IndexBytes];
        int offset = 0;
        WriteInt32Values(values, encoded, ref offset);
        hash.AppendByteSequencePart(encoded[..offset]);
    }

    private static void AddStageBuffers(
        CanonicalHashAccumulator hash,
        GpuStageBuffers stages)
    {
        hash.AddBytes(stages.GetAllBytes(160));
        hash.AddBytes(stages.GetAllBytes(168));
        hash.AddBytes(stages.GetAllBytes(176));
        hash.AddBytes(stages.GetAllBytes(192));
        hash.AddBytes(stages.GetAllBytes(224));
    }

    public static string ComputeProfileEvidenceHash(IReadOnlyList<PressureChunkEvidence> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        using CanonicalHashAccumulator hash = new();
        hash.AddString("voxel-pressure-evidence-v2");
        hash.AddInt32(chunks.Count);
        for (int index = 0; index < chunks.Count; index++)
        {
            PressureChunkEvidence chunk = chunks[index];
            hash.AddInt32(chunk.ChunkId);
            hash.AddInt64(chunk.SourceInputBytes);
            hash.AddInt64(chunk.LogicalDemandBytes);
            hash.AddInt32(chunk.OpaqueVertexLength);
            hash.AddInt32(chunk.OpaqueIndexLength);
            hash.AddInt32(chunk.OpaqueSliceLength);
            hash.AddInt32(chunk.OpaqueUploadLength);
            hash.AddInt32(chunk.TransparentVertexLength);
            hash.AddInt32(chunk.TransparentIndexLength);
            hash.AddInt32(chunk.TransparentSliceLength);
            hash.AddInt32(chunk.TransparentUploadLength);
            hash.AddInt32(chunk.SectionDescriptorLength);
            hash.AddInt32(chunk.SectionValueLength);
            hash.AddInt32(chunk.SectionWordLength);
            hash.AddInt32(chunk.SectionStateWordLength);
            hash.AddString(chunk.ExactEvidenceHash);
            hash.AddInt32(chunk.ExactVerificationPassed ? 1 : 0);
        }

        return hash.Complete();
    }

    private static int SectionValueCount(SectionSummary summary) =>
        summary.Kind switch
        {
            SectionRepresentationKind.Expanded => VoxelMath.CellsPerSection,
            SectionRepresentationKind.Packed
                or SectionRepresentationKind.MultiPacked => summary.DistinctIds,
            _ => 0
        };

    private static int SectionWordCount(SectionSummary summary)
    {
        if (summary.Kind is not (
            SectionRepresentationKind.Packed
            or SectionRepresentationKind.MultiPacked))
        {
            return 0;
        }

        return checked(
            PackedWordCount(summary.BitsPerIndex)
            + summary.TransparentIds * BoundaryFaceCount);
    }

    private static int SectionStateWordCount(SectionSummary summary) =>
        checked(
            (summary.OpaqueCount > 0
                ? OccupancyWordsPerSection + BoundaryWordsPerSection
                : 0)
            + (summary.TransparentCount > 0
                ? OccupancyWordsPerSection + BoundaryWordsPerSection
                : 0)
            + (summary.EmptyCount > 0 ? OccupancyWordsPerSection : 0));

    private static int PackedWordCount(int bitsPerIndex) =>
        checked(
            (VoxelMath.CellsPerSection * bitsPerIndex + 31)
            / 32);

    private static void ValidateSectionRepresentationRanges(
        ReadOnlySpan<VoxelCell> cells,
        ReadOnlySpan<SectionSummary> summaries,
        ReadOnlySpan<SectionPrerenderDescriptor> descriptors,
        ReadOnlySpan<ushort> values,
        ReadOnlySpan<uint> words,
        ReadOnlySpan<ulong> states)
    {
        if (cells.Length != VoxelMath.CellsPerChunk)
        {
            throw new ArgumentException(
                "The cell range must cover one complete canonical chunk.",
                nameof(cells));
        }

        if (summaries.Length != VoxelMath.SectionsPerChunk)
        {
            throw new ArgumentException(
                "The summary range must cover one complete canonical chunk.",
                nameof(summaries));
        }

        if (descriptors.Length != VoxelMath.SectionsPerChunk)
        {
            throw new ArgumentException(
                "The descriptor range must cover one complete canonical chunk.",
                nameof(descriptors));
        }

        int expectedValues = 0;
        int expectedWords = 0;
        int expectedStates = 0;
        foreach (ref readonly var sectionValue in summaries)
        {
            expectedValues = checked(
                expectedValues + SectionValueCount(sectionValue));
            expectedWords = checked(
                expectedWords + SectionWordCount(sectionValue));
            expectedStates = checked(
                expectedStates + SectionStateWordCount(sectionValue));
        }

        if (values.Length != expectedValues)
        {
            throw new ArgumentException(
                "The value backing range does not match the classified layouts.",
                nameof(values));
        }

        if (words.Length != expectedWords)
        {
            throw new ArgumentException(
                "The word backing range does not match the classified layouts.",
                nameof(words));
        }

        if (states.Length != expectedStates)
        {
            throw new ArgumentException(
                "The state backing range does not match the classified layouts.",
                nameof(states));
        }
    }

    private static SectionPrerenderDescriptor CreateSectionLayout(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionSummary summary,
        ref int valueCursor,
        ref int wordCursor,
        ref int stateCursor)
    {
        int valueLength = SectionValueCount(summary);
        int valueOffset = valueLength == 0 ? -1 : valueCursor;
        valueCursor = checked(valueCursor + valueLength);

        bool packed = summary.Kind is SectionRepresentationKind.Packed
            or SectionRepresentationKind.MultiPacked;
        int packedWordLength = packed
            ? PackedWordCount(summary.BitsPerIndex)
            : 0;
        int packedWordOffset = packedWordLength == 0 ? -1 : wordCursor;
        wordCursor = checked(wordCursor + packedWordLength);
        int tileLength = packed
            ? checked(summary.TransparentIds * BoundaryFaceCount)
            : 0;
        int tileOffset = tileLength == 0 ? -1 : wordCursor;
        wordCursor = checked(wordCursor + tileLength);

        int opaqueBitsOffset = summary.OpaqueCount > 0
            ? stateCursor
            : -1;
        stateCursor = checked(
            stateCursor
            + (summary.OpaqueCount > 0 ? OccupancyWordsPerSection : 0));
        int transparentBitsOffset = summary.TransparentCount > 0
            ? stateCursor
            : -1;
        stateCursor = checked(
            stateCursor
            + (summary.TransparentCount > 0
                ? OccupancyWordsPerSection
                : 0));
        int emptyBitsOffset = summary.EmptyCount > 0
            ? stateCursor
            : -1;
        stateCursor = checked(
            stateCursor
            + (summary.EmptyCount > 0 ? OccupancyWordsPerSection : 0));
        int opaqueFaceBitsOffset = summary.OpaqueCount > 0
            ? stateCursor
            : -1;
        stateCursor = checked(
            stateCursor
            + (summary.OpaqueCount > 0 ? BoundaryWordsPerSection : 0));
        int transparentFaceBitsOffset = summary.TransparentCount > 0
            ? stateCursor
            : -1;
        stateCursor = checked(
            stateCursor
            + (summary.TransparentCount > 0
                ? BoundaryWordsPerSection
                : 0));

        ComputeSectionBounds(
            cells,
            sectionIndex,
            out bool hasBounds,
            out byte minX,
            out byte minY,
            out byte minZ,
            out byte maxX,
            out byte maxY,
            out byte maxZ);
        int baseX = (sectionIndex % VoxelMath.SectionsPerAxis)
            * VoxelMath.SectionDimension;
        int baseY = ((sectionIndex / VoxelMath.SectionsPerAxis)
                % VoxelMath.SectionsPerAxis)
            * VoxelMath.SectionDimension;
        int baseZ = (sectionIndex
                / (VoxelMath.SectionsPerAxis * VoxelMath.SectionsPerAxis))
            * VoxelMath.SectionDimension;
        return new SectionPrerenderDescriptor(
            sectionIndex,
            summary.Kind,
            summary.UniformBlockId,
            summary.OpaqueCount,
            summary.TransparentCount,
            summary.EmptyCount,
            summary.BitsPerIndex,
            valueOffset,
            valueLength,
            packedWordOffset,
            packedWordLength,
            tileOffset,
            tileLength,
            opaqueBitsOffset,
            transparentBitsOffset,
            emptyBitsOffset,
            opaqueFaceBitsOffset,
            transparentFaceBitsOffset,
            hasBounds,
            minX,
            minY,
            minZ,
            maxX,
            maxY,
            maxZ,
            baseX,
            baseY,
            baseZ,
            ContentTag: 0);
    }

    private static void PopulateSectionValuesAndWords<
        TValueBuffer,
        TWordBuffer>(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionSummary summary,
        SectionPrerenderDescriptor descriptor,
        ref TValueBuffer values,
        ref TWordBuffer words)
        where TValueBuffer : ISectionInitializationBuffer<ushort>, allows ref struct
        where TWordBuffer : ISectionInitializationBuffer<uint>, allows ref struct
    {
        if (summary.Kind == SectionRepresentationKind.Expanded)
        {
            for (int localIndex = 0;
                localIndex < VoxelMath.CellsPerSection;
                localIndex++)
            {
                values.Append(
                    GetSectionBlockId(
                        cells,
                        sectionIndex,
                        localIndex));
            }

            return;
        }

        if (summary.Kind is not (
            SectionRepresentationKind.Packed
            or SectionRepresentationKind.MultiPacked))
        {
            return;
        }

        Span<int> paletteByType = stackalloc int[VoxelMath.BlockTypes.Length];
        BuildSectionPalette(
            cells,
            sectionIndex,
            ref values,
            descriptor.ValueOffset,
            descriptor.ValueLength,
            paletteByType);
        AppendPackedIndices(
            cells,
            sectionIndex,
            descriptor,
            paletteByType,
            ref words);

        int tileCursor = 0;
        for (int paletteIndex = 0;
            paletteIndex < descriptor.ValueLength;
            paletteIndex++)
        {
            ushort blockId = values.ReadInitialized(
                descriptor.ValueOffset + paletteIndex);
            if (!VoxelMath.TransparentById[blockId])
            {
                continue;
            }

            for (int face = 0; face < BoundaryFaceCount; face++)
            {
                words.Append(
                    checked(
                        (uint)(blockId * BoundaryFaceCount + face)));
                tileCursor++;
            }
        }

        if (tileCursor != descriptor.TransparentTileLength)
        {
            throw new InvalidDataException(
                "Transparent section tile materialization disagrees with the palette.");
        }
    }

    private static void AppendPackedIndices<TWordBuffer>(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionPrerenderDescriptor descriptor,
        scoped Span<int> paletteByType,
        ref TWordBuffer words)
        where TWordBuffer : ISectionInitializationBuffer<uint>, allows ref struct
    {
        ulong pending = 0;
        int pendingBits = 0;
        int wordCount = 0;
        for (int localIndex = 0;
            localIndex < VoxelMath.CellsPerSection;
            localIndex++)
        {
            ushort blockId = GetSectionBlockId(
                cells,
                sectionIndex,
                localIndex);
            int paletteIndex = paletteByType[VoxelMath.TypeIndexById[blockId]];
            pending |= unchecked((ulong)(uint)paletteIndex) << pendingBits;
            pendingBits += descriptor.BitsPerIndex;
            if (pendingBits < 32)
            {
                continue;
            }

            words.Append(unchecked((uint)pending));
            wordCount++;
            pending >>= 32;
            pendingBits -= 32;
        }

        if (pendingBits > 0)
        {
            words.Append(unchecked((uint)pending));
            wordCount++;
        }

        if (wordCount != descriptor.PackedWordLength)
        {
            throw new InvalidDataException(
                "Packed section materialization did not fill its word range.");
        }
    }

    private static void PopulateSectionStates(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionPrerenderDescriptor descriptor,
        Span<ulong> states)
    {
        PopulateSectionStateWords(
            cells,
            sectionIndex,
            descriptor,
            states,
            clearStates: true);
    }

    private static void PopulateSectionStateWords(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionPrerenderDescriptor descriptor,
        Span<ulong> states,
        bool clearStates)
    {
        int start = FirstStateOffset(descriptor);
        int length = SectionStateLength(descriptor);
        if (states.Length != length)
        {
            throw new ArgumentException(
                "The state output range does not match the section layout.",
                nameof(states));
        }

        if (clearStates)
        {
            states.Clear();
        }

        PopulateSectionStateBits(
            cells,
            sectionIndex,
            descriptor,
            start,
            states,
            [],
            []);
    }

    private static void PopulateSectionStateWordsAndMasks(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionSummary summary,
        SectionPrerenderDescriptor descriptor,
        Span<ulong> states,
        Span<ulong> transparentMasks,
        bool clearStates,
        bool clearTransparentMasks)
    {
        int start = FirstStateOffset(descriptor);
        int length = SectionStateLength(descriptor);
        if (states.Length != length)
        {
            throw new ArgumentException(
                "The state output range does not match the section layout.",
                nameof(states));
        }

        if (transparentMasks.Length
            % VoxelMath.TransparentMaskWordsPerId != 0)
        {
            throw new ArgumentException(
                "The transparent-mask output range is not complete.",
                nameof(transparentMasks));
        }

        if (BitOperations.PopCount(summary.TransparentTypeMask)
                * VoxelMath.TransparentMaskWordsPerId
            != transparentMasks.Length)
        {
            throw new ArgumentException(
                "The transparent-mask range does not match its material types.",
                nameof(transparentMasks));
        }

        Span<int> transparentMaskSlots =
            stackalloc int[VoxelMath.BlockTypes.Length];
        transparentMaskSlots.Fill(-1);
        int nextMaskSlot = 0;
        for (int typeIndex = 0;
            typeIndex < VoxelMath.BlockTypes.Length;
            typeIndex++)
        {
            if ((summary.TransparentTypeMask
                    & (1UL << typeIndex)) != 0)
            {
                transparentMaskSlots[typeIndex] =
                    nextMaskSlot++;
            }
        }

        if (clearStates)
        {
            states.Clear();
        }

        if (clearTransparentMasks)
        {
            transparentMasks.Clear();
        }

        PopulateSectionStateBits(
            cells,
            sectionIndex,
            descriptor,
            start,
            states,
            transparentMasks,
            transparentMaskSlots);
    }

    // Builds one Z plane's 64-bit words (4 per range: 4 X columns of 16 Y
    // bits each) in locals and ORs each into its output word once.
    private static void PopulateSectionStateBits(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionPrerenderDescriptor descriptor,
        int start,
        Span<ulong> states,
        Span<ulong> transparentMasks,
        ReadOnlySpan<int> transparentMaskSlots)
    {
        const int WordsPerPlane =
            VoxelMath.SectionDimension * VoxelMath.SectionDimension / 64;
        const int Empty = 0;
        const int Transparent = 1;
        const int Opaque = 2;
        ReadOnlySpan<bool> transparentById = VoxelMath.TransparentById;
        ReadOnlySpan<byte> typeIndexById = VoxelMath.TypeIndexById;
        bool buildMasks = !transparentMaskSlots.IsEmpty;
        Span<ulong> occupancy = stackalloc ulong[3 * WordsPerPlane];
        Span<ulong> planeMasks = stackalloc ulong[
            VoxelMath.BlockTypes.Length * WordsPerPlane];
        Span<int> occupancyOffsets =
        [
            descriptor.EmptyBitsOffset - start,
            descriptor.TransparentBitsOffset - start,
            descriptor.OpaqueBitsOffset - start
        ];
        int sectionBase = SectionBaseCell(sectionIndex);
        for (int localZ = 0; localZ < VoxelMath.SectionDimension; localZ++)
        {
            occupancy.Clear();
            ulong planeTypes = 0;
            for (int localY = 0; localY < VoxelMath.SectionDimension; localY++)
            {
                ReadOnlySpan<VoxelCell> row = cells.Slice(
                    sectionBase + SectionRowOffset(localZ, localY),
                    VoxelMath.SectionDimension);
                bool rowOnBoundary = IsSectionEdge(localY) || IsSectionEdge(localZ);
                for (int localX = 0; localX < row.Length; localX++)
                {
                    ushort blockId = row[localX].BlockId;
                    int word = localX >> 2;
                    ulong bit = 1UL << (((localX & 3) << 4) | localY);
                    if (blockId == VoxelMath.AirBlockId)
                    {
                        occupancy[Empty * WordsPerPlane + word] |= bit;
                        continue;
                    }

                    bool transparent = transparentById[blockId];
                    occupancy[(transparent ? Transparent : Opaque) * WordsPerPlane + word] |= bit;
                    if (transparent && buildMasks)
                    {
                        int typeIndex = typeIndexById[blockId];
                        ulong typeBit = 1UL << typeIndex;
                        if ((planeTypes & typeBit) == 0)
                        {
                            planeTypes |= typeBit;
                            planeMasks.Slice(typeIndex * WordsPerPlane, WordsPerPlane).Clear();
                        }

                        planeMasks[typeIndex * WordsPerPlane + word] |= bit;
                    }

                    SetBoundaryBitsIfEdge(
                        states,
                        transparent
                            ? descriptor.TransparentFaceBitsOffset - start
                            : descriptor.OpaqueFaceBitsOffset - start,
                        SectionLocalIndex(localX, localY, localZ),
                        rowOnBoundary || IsSectionEdge(localX));
                }
            }

            int planeWord = localZ * WordsPerPlane;
            for (int range = 0; range < occupancyOffsets.Length; range++)
            {
                ReadOnlySpan<ulong> words = occupancy.Slice(range * WordsPerPlane, WordsPerPlane);
                if ((words[0] | words[1] | words[2] | words[3]) == 0)
                {
                    continue;
                }

                if (occupancyOffsets[range] < 0)
                {
                    throw new InvalidDataException(
                        "Section state omitted a required occupancy range.");
                }

                Span<ulong> target = states.Slice(occupancyOffsets[range] + planeWord, WordsPerPlane);
                for (int index = 0; index < WordsPerPlane; index++)
                {
                    target[index] |= words[index];
                }
            }

            while (planeTypes != 0)
            {
                int typeIndex = BitOperations.TrailingZeroCount(planeTypes);
                planeTypes &= planeTypes - 1;
                int maskWord =
                    transparentMaskSlots[typeIndex] * VoxelMath.TransparentMaskWordsPerId
                    + planeWord;
                for (int index = 0; index < WordsPerPlane; index++)
                {
                    transparentMasks[maskWord + index] |=
                        planeMasks[typeIndex * WordsPerPlane + index];
                }
            }
        }
    }

    private static void VerifySectionValuesAndWords(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionSummary summary,
        SectionPrerenderDescriptor descriptor,
        ReadOnlySpan<ushort> values,
        ReadOnlySpan<uint> words)
    {
        if (summary.Kind == SectionRepresentationKind.Expanded)
        {
            ReadOnlySpan<ushort> dense = values.Slice(
                descriptor.ValueOffset,
                descriptor.ValueLength);
            for (int localIndex = 0;
                localIndex < VoxelMath.CellsPerSection;
                localIndex++)
            {
                if (dense[localIndex] != GetSectionBlockId(
                    cells,
                    sectionIndex,
                    localIndex))
                {
                    throw new InvalidDataException(
                        $"Expanded section {sectionIndex} value {localIndex} is not canonical.");
                }
            }

            return;
        }

        if (summary.Kind is not (
            SectionRepresentationKind.Packed
            or SectionRepresentationKind.MultiPacked))
        {
            return;
        }

        Span<ushort> expectedPalette =
            stackalloc ushort[VoxelMath.BlockTypes.Length];
        Span<int> paletteByType = stackalloc int[VoxelMath.BlockTypes.Length];
        expectedPalette = expectedPalette.Slice(0, descriptor.ValueLength);
        BuildSectionPalette(
            cells,
            sectionIndex,
            expectedPalette,
            paletteByType);
        ReadOnlySpan<ushort> actualPalette = values.Slice(
            descriptor.ValueOffset,
            descriptor.ValueLength);
        if (!actualPalette.SequenceEqual(expectedPalette))
        {
            throw new InvalidDataException(
                $"Section {sectionIndex} palette is not canonical.");
        }

        Span<uint> expectedPacked = stackalloc uint[384];
        expectedPacked = expectedPacked.Slice(
            0,
            descriptor.PackedWordLength);
        expectedPacked.Clear();
        for (int localIndex = 0;
            localIndex < VoxelMath.CellsPerSection;
            localIndex++)
        {
            ushort blockId = GetSectionBlockId(
                cells,
                sectionIndex,
                localIndex);
            WritePackedIndex(
                expectedPacked,
                localIndex,
                descriptor.BitsPerIndex,
                paletteByType[VoxelMath.TypeIndexById[blockId]]);
        }

        if (!words.Slice(
                descriptor.PackedWordOffset,
                descriptor.PackedWordLength)
            .SequenceEqual(expectedPacked))
        {
            throw new InvalidDataException(
                $"Section {sectionIndex} packed words are not canonical.");
        }

        ReadOnlySpan<uint> actualTiles =
            descriptor.TransparentTileLength == 0
                ? []
                : words.Slice(
                    descriptor.TransparentTileOffset,
                    descriptor.TransparentTileLength);
        int tileCursor = 0;
        foreach (ref readonly var paletteIndexValue in expectedPalette)
        {
            ushort blockId = paletteIndexValue;
            if (!VoxelMath.TransparentById[blockId])
            {
                continue;
            }

            for (int face = 0; face < BoundaryFaceCount; face++)
            {
                uint expected = checked(
                    (uint)(blockId * BoundaryFaceCount + face));
                if (actualTiles[tileCursor++] != expected)
                {
                    throw new InvalidDataException(
                        $"Section {sectionIndex} transparent tile value is not canonical.");
                }
            }
        }

        if (tileCursor != actualTiles.Length)
        {
            throw new InvalidDataException(
                $"Section {sectionIndex} transparent tile range has trailing values.");
        }
    }

    private static void VerifySectionStates(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionPrerenderDescriptor descriptor,
        ReadOnlySpan<ulong> states,
        int sectionStateStart,
        int sectionStateLength)
    {
        Span<ulong> expected = stackalloc ulong[240];
        expected = expected.Slice(0, sectionStateLength);
        SectionPrerenderDescriptor relative = descriptor with
        {
            OpaqueBitsOffset = RelativeOffset(
                descriptor.OpaqueBitsOffset,
                sectionStateStart),
            TransparentBitsOffset = RelativeOffset(
                descriptor.TransparentBitsOffset,
                sectionStateStart),
            EmptyBitsOffset = RelativeOffset(
                descriptor.EmptyBitsOffset,
                sectionStateStart),
            OpaqueFaceBitsOffset = RelativeOffset(
                descriptor.OpaqueFaceBitsOffset,
                sectionStateStart),
            TransparentFaceBitsOffset = RelativeOffset(
                descriptor.TransparentFaceBitsOffset,
                sectionStateStart)
        };
        PopulateSectionStates(
            cells,
            sectionIndex,
            relative,
            expected);
        if (!states.Slice(sectionStateStart, sectionStateLength)
            .SequenceEqual(expected))
        {
            throw new InvalidDataException(
                $"Section {sectionIndex} occupancy or boundary state is not canonical.");
        }
    }

    private static void BuildSectionPalette(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        Span<ushort> palette,
        Span<int> paletteByType)
    {
        SpanSectionInitializationBuffer<ushort> buffer = new(palette);
        BuildSectionPalette(
            cells,
            sectionIndex,
            ref buffer,
            offset: 0,
            palette.Length,
            paletteByType);
    }

    private static void BuildSectionPalette<TValueBuffer>(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        ref TValueBuffer palette,
        int offset,
        int length,
        scoped Span<int> paletteByType)
        where TValueBuffer : ISectionInitializationBuffer<ushort>, allows ref struct
    {
        Span<byte> seen = stackalloc byte[VoxelMath.BlockTypes.Length];
        paletteByType.Fill(-1);
        for (int localIndex = 0;
            localIndex < VoxelMath.CellsPerSection;
            localIndex++)
        {
            ushort blockId = GetSectionBlockId(
                cells,
                sectionIndex,
                localIndex);
            seen[VoxelMath.TypeIndexById[blockId]] = 1;
        }

        int cursor = 0;
        for (int typeIndex = 0;
            typeIndex < VoxelMath.BlockTypes.Length;
            typeIndex++)
        {
            if (seen[typeIndex] == 0)
            {
                continue;
            }

            if ((uint)cursor >= (uint)length)
            {
                throw new InvalidDataException(
                    "Section palette classification undercounted a block id.");
            }

            paletteByType[typeIndex] = cursor;
            palette.Append(
                checked(
                    (ushort)VoxelMath.BlockTypes[typeIndex].Id));
            cursor++;
        }

        if (cursor != length)
        {
            throw new InvalidDataException(
                "Section palette classification overcounted block ids.");
        }
    }

    private static void WritePackedIndex(
        Span<uint> words,
        int localIndex,
        int bitsPerIndex,
        int value)
    {
        int bitPosition = checked(localIndex * bitsPerIndex);
        int wordIndex = bitPosition >> 5;
        int bitOffset = bitPosition & 31;
        words[wordIndex] |= unchecked((uint)value) << bitOffset;
        int available = 32 - bitOffset;
        if (available < bitsPerIndex)
        {
            words[wordIndex + 1] |= unchecked((uint)value) >> available;
        }
    }

    private static ushort GetSectionBlockId(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        int localIndex)
    {
        int localY = localIndex & (VoxelMath.SectionDimension - 1);
        int packed = localIndex >> 4;
        int localX = packed & (VoxelMath.SectionDimension - 1);
        int localZ = packed >> 4;
        int baseX = (sectionIndex % VoxelMath.SectionsPerAxis)
            * VoxelMath.SectionDimension;
        int baseY = ((sectionIndex / VoxelMath.SectionsPerAxis)
                % VoxelMath.SectionsPerAxis)
            * VoxelMath.SectionDimension;
        int baseZ = (sectionIndex
                / (VoxelMath.SectionsPerAxis * VoxelMath.SectionsPerAxis))
            * VoxelMath.SectionDimension;
        int cellIndex = ((baseZ + localZ) * VoxelMath.ChunkDimension
                + baseY + localY)
            * VoxelMath.ChunkDimension
            + baseX
            + localX;
        return cells[cellIndex].BlockId;
    }

    private static void ComputeSectionBounds(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        out bool hasBounds,
        out byte minX,
        out byte minY,
        out byte minZ,
        out byte maxX,
        out byte maxY,
        out byte maxZ)
    {
        hasBounds = false;
        minX = minY = minZ = byte.MaxValue;
        maxX = maxY = maxZ = 0;
        // Rows in memory order; each row contributes its first and last
        // occupied X, and min/max do not depend on the visiting order.
        int sectionBase = SectionBaseCell(sectionIndex);
        for (int localZ = 0; localZ < VoxelMath.SectionDimension; localZ++)
        {
            for (int localY = 0; localY < VoxelMath.SectionDimension; localY++)
            {
                ReadOnlySpan<VoxelCell> row = cells.Slice(
                    sectionBase + SectionRowOffset(localZ, localY),
                    VoxelMath.SectionDimension);
                int first = 0;
                while (first < row.Length
                    && row[first].BlockId == VoxelMath.AirBlockId)
                {
                    first++;
                }

                if (first == row.Length)
                {
                    continue;
                }

                int last = row.Length - 1;
                while (row[last].BlockId == VoxelMath.AirBlockId)
                {
                    last--;
                }

                hasBounds = true;
                minX = Math.Min(minX, (byte)first);
                minY = Math.Min(minY, (byte)localY);
                minZ = Math.Min(minZ, (byte)localZ);
                maxX = Math.Max(maxX, (byte)last);
                maxY = Math.Max(maxY, (byte)localY);
                maxZ = Math.Max(maxZ, (byte)localZ);
            }
        }

        if (!hasBounds)
        {
            minX = minY = minZ = 0;
        }
    }

    private static void SetWordBit(
        Span<ulong> states,
        int offset,
        int bitIndex)
    {
        if (offset < 0)
        {
            throw new InvalidDataException(
                "Section state omitted a required occupancy range.");
        }

        int index = offset + (bitIndex >> 6);
        states[index] |= 1UL << (bitIndex & 63);
    }

    // First cell of a section: its local (0, 0, 0).
    private static int SectionBaseCell(int sectionIndex) =>
        VoxelMath.CellIndex(
            (sectionIndex % VoxelMath.SectionsPerAxis)
                * VoxelMath.SectionDimension,
            ((sectionIndex / VoxelMath.SectionsPerAxis)
                    % VoxelMath.SectionsPerAxis)
                * VoxelMath.SectionDimension,
            (sectionIndex
                    / (VoxelMath.SectionsPerAxis * VoxelMath.SectionsPerAxis))
                * VoxelMath.SectionDimension);

    private static int SectionRowOffset(int localZ, int localY) =>
        (localZ * VoxelMath.ChunkDimension + localY) * VoxelMath.ChunkDimension;

    // Section-local bit order: Y fastest, then X, then Z.
    private static int SectionLocalIndex(int localX, int localY, int localZ) =>
        (localZ << 8) | (localX << 4) | localY;

    private static bool IsSectionEdge(int local) =>
        local == 0 || local == VoxelMath.SectionDimension - 1;

    // Interior cells set no boundary bits, but a missing range still fails.
    private static void SetBoundaryBitsIfEdge(
        Span<ulong> states,
        int offset,
        int localIndex,
        bool onEdge)
    {
        if (onEdge)
        {
            SetBoundaryBits(states, offset, localIndex);
        }
        else if (offset < 0)
        {
            throw new InvalidDataException(
                "Section state omitted a required boundary range.");
        }
    }

    private static void SetBoundaryBits(
        Span<ulong> states,
        int offset,
        int localIndex)
    {
        if (offset < 0)
        {
            throw new InvalidDataException(
                "Section state omitted a required boundary range.");
        }

        int localY = localIndex & (VoxelMath.SectionDimension - 1);
        int packed = localIndex >> 4;
        int localX = packed & (VoxelMath.SectionDimension - 1);
        int localZ = packed >> 4;
        if (localX == 0)
        {
            SetFaceBit(states, offset, 0, localZ * 16 + localY);
        }

        if (localX == VoxelMath.SectionDimension - 1)
        {
            SetFaceBit(states, offset, 1, localZ * 16 + localY);
        }

        if (localY == 0)
        {
            SetFaceBit(states, offset, 2, localX * 16 + localZ);
        }

        if (localY == VoxelMath.SectionDimension - 1)
        {
            SetFaceBit(states, offset, 3, localX * 16 + localZ);
        }

        if (localZ == 0)
        {
            SetFaceBit(states, offset, 4, localX * 16 + localY);
        }

        if (localZ == VoxelMath.SectionDimension - 1)
        {
            SetFaceBit(states, offset, 5, localX * 16 + localY);
        }
    }

    private static void SetFaceBit(
        Span<ulong> states,
        int offset,
        int face,
        int bitIndex)
    {
        int index = offset
            + face * BoundaryWordsPerFace
            + (bitIndex >> 6);
        states[index] |= 1UL << (bitIndex & 63);
    }

    private static SectionPrerenderDescriptor RebaseSectionLayout(
        SectionPrerenderDescriptor descriptor,
        int valueStart,
        int wordStart,
        int stateStart) =>
        descriptor with
        {
            ValueOffset = RelativeOffset(
                descriptor.ValueOffset,
                valueStart),
            PackedWordOffset = RelativeOffset(
                descriptor.PackedWordOffset,
                wordStart),
            TransparentTileOffset = RelativeOffset(
                descriptor.TransparentTileOffset,
                wordStart),
            OpaqueBitsOffset = RelativeOffset(
                descriptor.OpaqueBitsOffset,
                stateStart),
            TransparentBitsOffset = RelativeOffset(
                descriptor.TransparentBitsOffset,
                stateStart),
            EmptyBitsOffset = RelativeOffset(
                descriptor.EmptyBitsOffset,
                stateStart),
            OpaqueFaceBitsOffset = RelativeOffset(
                descriptor.OpaqueFaceBitsOffset,
                stateStart),
            TransparentFaceBitsOffset = RelativeOffset(
                descriptor.TransparentFaceBitsOffset,
                stateStart)
        };

    private static int RelativeOffset(int offset, int start) =>
        offset < 0 ? -1 : checked(offset - start);

    private static int FirstStateOffset(
        SectionPrerenderDescriptor descriptor)
    {
        int start = int.MaxValue;
        start = MinimumPresent(start, descriptor.OpaqueBitsOffset);
        start = MinimumPresent(start, descriptor.TransparentBitsOffset);
        start = MinimumPresent(start, descriptor.EmptyBitsOffset);
        start = MinimumPresent(start, descriptor.OpaqueFaceBitsOffset);
        start = MinimumPresent(start, descriptor.TransparentFaceBitsOffset);
        return start == int.MaxValue ? 0 : start;
    }

    private static int SectionStateLength(
        SectionPrerenderDescriptor descriptor)
    {
        int start = FirstStateOffset(descriptor);
        int end = start;
        end = MaximumRangeEnd(
            end,
            descriptor.OpaqueBitsOffset,
            OccupancyWordsPerSection);
        end = MaximumRangeEnd(
            end,
            descriptor.TransparentBitsOffset,
            OccupancyWordsPerSection);
        end = MaximumRangeEnd(
            end,
            descriptor.EmptyBitsOffset,
            OccupancyWordsPerSection);
        end = MaximumRangeEnd(
            end,
            descriptor.OpaqueFaceBitsOffset,
            BoundaryWordsPerSection);
        end = MaximumRangeEnd(
            end,
            descriptor.TransparentFaceBitsOffset,
            BoundaryWordsPerSection);
        return checked(end - start);
    }

    private static int MinimumPresent(int current, int value) =>
        value < 0 ? current : Math.Min(current, value);

    private static int MaximumRangeEnd(
        int current,
        int offset,
        int length) =>
        offset < 0 ? current : Math.Max(current, checked(offset + length));

    private static int ComputeSectionContentTag(
        SectionPrerenderDescriptor descriptor,
        ReadOnlySpan<ushort> values,
        ReadOnlySpan<uint> words,
        ReadOnlySpan<ulong> states)
    {
        uint hash = BeginSectionContentTag(descriptor);
        if (descriptor.ValueLength > 0)
        {
            foreach (ref readonly ushort value in values.Slice(
                descriptor.ValueOffset,
                descriptor.ValueLength))
            {
                hash = Mix(hash, value);
            }
        }

        if (descriptor.PackedWordLength > 0)
        {
            foreach (ref readonly uint value in words.Slice(
                descriptor.PackedWordOffset,
                descriptor.PackedWordLength))
            {
                hash = Mix(hash, value);
            }
        }

        if (descriptor.TransparentTileLength > 0)
        {
            foreach (ref readonly uint value in words.Slice(
                descriptor.TransparentTileOffset,
                descriptor.TransparentTileLength))
            {
                hash = Mix(hash, value);
            }
        }

        int stateLength = SectionStateLength(descriptor);
        if (stateLength > 0)
        {
            foreach (ref readonly ulong value in states.Slice(
                FirstStateOffset(descriptor),
                stateLength))
            {
                hash = Mix(hash, unchecked((uint)value));
                hash = Mix(hash, unchecked((uint)(value >> 32)));
            }
        }

        int result = unchecked((int)hash);
        return result == 0 ? 1 : result;
    }

    private static int ComputeSectionContentTag<
        TValueBuffer,
        TWordBuffer>(
        SectionPrerenderDescriptor descriptor,
        ref TValueBuffer values,
        ref TWordBuffer words,
        ReadOnlySpan<ulong> states)
        where TValueBuffer : ISectionInitializationBuffer<ushort>, allows ref struct
        where TWordBuffer : ISectionInitializationBuffer<uint>, allows ref struct
    {
        uint hash = BeginSectionContentTag(descriptor);
        hash = values.MixInitialized(
            hash,
            descriptor.ValueOffset,
            descriptor.ValueLength);
        hash = words.MixInitialized(
            hash,
            descriptor.PackedWordOffset,
            descriptor.PackedWordLength);
        hash = words.MixInitialized(
            hash,
            descriptor.TransparentTileOffset,
            descriptor.TransparentTileLength);
        int stateLength = SectionStateLength(descriptor);
        if (stateLength > 0)
        {
            hash = MixSectionContent(
                hash,
                states.Slice(
                    FirstStateOffset(descriptor),
                    stateLength));
        }

        int result = unchecked((int)hash);
        return result == 0 ? 1 : result;
    }

    /// <summary>Adds one typed section range to a content tag.</summary>
    public static uint MixSectionContent<T>(
        uint hash,
        ReadOnlySpan<T> values)
        where T : unmanaged
    {
        if (typeof(T) == typeof(ushort))
        {
            foreach (ref readonly ushort value in MemoryMarshal.Cast<T, ushort>(values))
            {
                hash = Mix(hash, value);
            }

            return hash;
        }

        if (typeof(T) == typeof(uint))
        {
            foreach (ref readonly uint value in MemoryMarshal.Cast<T, uint>(values))
            {
                hash = Mix(hash, value);
            }

            return hash;
        }

        if (typeof(T) == typeof(ulong))
        {
            foreach (ref readonly ulong value in MemoryMarshal.Cast<T, ulong>(values))
            {
                hash = Mix(hash, unchecked((uint)value));
                hash = Mix(hash, unchecked((uint)(value >> 32)));
            }

            return hash;
        }

        throw new NotSupportedException(
            "The section content tag supports ushort, uint, and ulong storage.");
    }

    private static uint BeginSectionContentTag(
        SectionPrerenderDescriptor descriptor)
    {
        uint hash = 2_166_136_261;
        hash = Mix(hash, descriptor.SectionIndex);
        hash = Mix(hash, (int)descriptor.Kind);
        hash = Mix(hash, descriptor.UniformBlockId);
        hash = Mix(hash, descriptor.OpaqueCount);
        hash = Mix(hash, descriptor.TransparentCount);
        hash = Mix(hash, descriptor.EmptyCount);
        hash = Mix(hash, descriptor.BitsPerIndex);
        hash = Mix(hash, descriptor.HasBounds ? 1 : 0);
        hash = Mix(hash, descriptor.MinX);
        hash = Mix(hash, descriptor.MinY);
        hash = Mix(hash, descriptor.MinZ);
        hash = Mix(hash, descriptor.MaxX);
        hash = Mix(hash, descriptor.MaxY);
        hash = Mix(hash, descriptor.MaxZ);
        hash = Mix(hash, descriptor.SectionBaseX);
        hash = Mix(hash, descriptor.SectionBaseY);
        return Mix(hash, descriptor.SectionBaseZ);
    }

    private static uint Mix(uint hash, int value) =>
        Mix(hash, unchecked((uint)value));

    private static uint Mix(uint hash, uint value)
    {
        hash ^= value;
        return unchecked(hash * 16_777_619);
    }

    private static void AddSectionDescriptor(
        CanonicalHashAccumulator hash,
        SectionPrerenderDescriptor descriptor)
    {
        hash.AddInt32(descriptor.SectionIndex);
        hash.AddInt32((int)descriptor.Kind);
        hash.AddUInt16(descriptor.UniformBlockId);
        hash.AddInt32(descriptor.OpaqueCount);
        hash.AddInt32(descriptor.TransparentCount);
        hash.AddInt32(descriptor.EmptyCount);
        hash.AddInt32(descriptor.BitsPerIndex);
        hash.AddInt32(descriptor.ValueOffset);
        hash.AddInt32(descriptor.ValueLength);
        hash.AddInt32(descriptor.PackedWordOffset);
        hash.AddInt32(descriptor.PackedWordLength);
        hash.AddInt32(descriptor.TransparentTileOffset);
        hash.AddInt32(descriptor.TransparentTileLength);
        hash.AddInt32(descriptor.OpaqueBitsOffset);
        hash.AddInt32(descriptor.TransparentBitsOffset);
        hash.AddInt32(descriptor.EmptyBitsOffset);
        hash.AddInt32(descriptor.OpaqueFaceBitsOffset);
        hash.AddInt32(descriptor.TransparentFaceBitsOffset);
        hash.AddInt32(descriptor.HasBounds ? 1 : 0);
        hash.AddInt32(descriptor.MinX);
        hash.AddInt32(descriptor.MinY);
        hash.AddInt32(descriptor.MinZ);
        hash.AddInt32(descriptor.MaxX);
        hash.AddInt32(descriptor.MaxY);
        hash.AddInt32(descriptor.MaxZ);
        hash.AddInt32(descriptor.SectionBaseX);
        hash.AddInt32(descriptor.SectionBaseY);
        hash.AddInt32(descriptor.SectionBaseZ);
        hash.AddInt32(descriptor.ContentTag);
    }

    private static void VerifyTypedStream(
        ReadOnlySpan<FaceRecord> records,
        ReadOnlySpan<Vertex> vertices,
        ReadOnlySpan<int> indices)
    {
        int vertexCursor = 0;
        int indexCursor = 0;
        foreach (ref readonly var recordIndexValue in records)
        {
            ref readonly FaceRecord record = ref recordIndexValue;
            for (int face = 0; face < VoxelMath.FacesPerCell; face++)
            {
                if ((record.Mask & (1 << face)) == 0)
                {
                    continue;
                }

                int vertexOffset = vertexCursor;
                for (int corner = 0; corner < VoxelMath.VerticesPerFace; corner++)
                {
                    Vertex expected = new(
                        VoxelMath.VertexValue(record.CellIndex, face, corner, 0, record.BlockId),
                        VoxelMath.VertexValue(record.CellIndex, face, corner, 1, record.BlockId),
                        VoxelMath.VertexValue(record.CellIndex, face, corner, 2, record.BlockId),
                        face,
                        corner,
                        record.BlockId);
                    if ((uint)vertexCursor >= (uint)vertices.Length || vertices[vertexCursor++] != expected)
                    {
                        throw new InvalidDataException($"Vertex {vertexCursor - 1} is not canonical.");
                    }
                }

                for (int index = 0; index < VoxelMath.IndicesPerFace; index++)
                {
                    int expected = VoxelMath.IndexValue(vertexOffset, index);
                    if ((uint)indexCursor >= (uint)indices.Length || indices[indexCursor++] != expected)
                    {
                        throw new InvalidDataException($"Index {indexCursor - 1} is not canonical.");
                    }
                }
            }
        }

        if (vertexCursor != vertices.Length || indexCursor != indices.Length)
        {
            throw new InvalidDataException(
                "Typed output contains trailing or missing canonical values.");
        }
    }

    private static void VerifyRetainedStream(
        int seed,
        ReadOnlySpan<FaceRecord> records,
        ReadOnlySpan<PayloadSlice> slices,
        GpuStageBuffers stages)
    {
        int vertexCursor = 0;
        int sliceCursor = 0;
        Span<int> stageCursors = stackalloc int[5];
        foreach (ref readonly var recordIndexValue in records)
        {
            ref readonly FaceRecord record = ref recordIndexValue;
            for (int face = 0; face < VoxelMath.FacesPerCell; face++)
            {
                if ((record.Mask & (1 << face)) == 0)
                {
                    continue;
                }

                int stageClass = StageClassIndex(record.StageBytes);
                int stageOffset = stageCursors[stageClass];
                PayloadSlice expectedSlice = new(
                    stageOffset,
                    record.StageBytes,
                    record.Alignment,
                    record.StageMask,
                    record.BlockId,
                    record.CellIndex);
                if ((uint)sliceCursor >= (uint)slices.Length
                    || slices[sliceCursor] != expectedSlice)
                {
                    throw new InvalidDataException(
                        $"Payload descriptor {sliceCursor} does not match canonical ordering.");
                }

                Span<byte> upload = stages.GetStage(
                    record.StageBytes,
                    stageOffset);
                int encodedCursor = 0;
                int payloadSeed = unchecked(
                    seed
                    + record.CellIndex * 11
                    + record.BlockId * 37
                    + record.StageMask * 13);
                for (int slot = 0; slot < record.PayloadBytes; slot++)
                {
                    byte expected = (record.StageMask & (1 << (slot % 4))) != 0
                        ? (byte)(unchecked(payloadSeed + slot * 17) & 0xFF)
                        : (byte)0;
                    if (upload[encodedCursor++] != expected)
                    {
                        throw new InvalidDataException(
                            $"Payload byte {encodedCursor - 1} is not canonical.");
                    }
                }

                int vertexOffset = vertexCursor;
                for (int corner = 0; corner < VoxelMath.VerticesPerFace; corner++)
                {
                    Vertex expected = new(
                        VoxelMath.VertexValue(
                            record.CellIndex,
                            face,
                            corner,
                            0,
                            record.BlockId),
                        VoxelMath.VertexValue(
                            record.CellIndex,
                            face,
                            corner,
                            1,
                            record.BlockId),
                        VoxelMath.VertexValue(
                            record.CellIndex,
                            face,
                            corner,
                            2,
                            record.BlockId),
                        face,
                        corner,
                        record.BlockId);
                    VerifyEncodedVertex(upload, ref encodedCursor, expected);
                    vertexCursor++;
                }

                for (int index = 0; index < VoxelMath.IndicesPerFace; index++)
                {
                    int expected = VoxelMath.IndexValue(vertexOffset, index);
                    if (ReadInt32(upload, ref encodedCursor) != expected)
                    {
                        throw new InvalidDataException(
                            $"Encoded index for face {sliceCursor} is not canonical.");
                    }
                }

                AssertZero(
                    upload[encodedCursor..],
                    "face padding");
                stageCursors[stageClass] = checked(
                    stageOffset + record.StageBytes);
                sliceCursor++;
            }
        }

        if (sliceCursor != slices.Length
            || stageCursors[0] != stages.GetAllBytes(160).Length
            || stageCursors[1] != stages.GetAllBytes(168).Length
            || stageCursors[2] != stages.GetAllBytes(176).Length
            || stageCursors[3] != stages.GetAllBytes(192).Length
            || stageCursors[4] != stages.GetAllBytes(224).Length)
        {
            throw new InvalidDataException(
                "Retained output contains trailing or missing canonical values.");
        }
    }

    private static void VerifyScatterRetainedStream(
        int seed,
        ReadOnlySpan<FaceRecord> records,
        ReadOnlySpan<PayloadSlice> slices,
        ReadOnlySpan<byte> payloadPatterns)
    {
        int sliceCursor = 0;
        Span<int> stageCursors = stackalloc int[5];
        foreach (ref readonly var recordIndexValue in records)
        {
            ref readonly FaceRecord record =
                ref recordIndexValue;
            int payloadSeed = unchecked(
                seed
                + record.CellIndex * 11
                + record.BlockId * 37
                + record.StageMask * 13);
            ReadOnlySpan<byte> payload =
                GetPayloadPattern(
                    payloadPatterns,
                    record.PayloadBytes,
                    payloadSeed,
                    record.StageMask);
            uint remainingFaces =
                unchecked((uint)record.Mask) & 0b11_1111u;
            while (remainingFaces != 0)
            {
                remainingFaces &= remainingFaces - 1;
                int stageClass = StageClassIndex(
                    record.StageBytes);
                int stageOffset = stageCursors[stageClass];
                PayloadSlice expectedSlice = new(
                    stageOffset,
                    record.StageBytes,
                    record.Alignment,
                    record.StageMask,
                    record.BlockId,
                    record.CellIndex);
                if ((uint)sliceCursor >= (uint)slices.Length
                    || slices[sliceCursor] != expectedSlice)
                {
                    throw new InvalidDataException(
                        $"Scatter descriptor {sliceCursor} is not canonical.");
                }

                for (int slot = 0;
                    slot < record.PayloadBytes;
                    slot++)
                {
                    byte expected =
                        (record.StageMask
                            & (1 << (slot % 4))) != 0
                        ? (byte)(
                            unchecked(
                                payloadSeed + slot * 17)
                            & 0xFF)
                        : (byte)0;
                    if (payload[slot] != expected)
                    {
                        throw new InvalidDataException(
                            "A scatter payload byte is not canonical.");
                    }
                }

                stageCursors[stageClass] = checked(
                    stageOffset + record.StageBytes);
                sliceCursor++;
            }
        }

        if (sliceCursor != slices.Length)
        {
            throw new InvalidDataException(
                "Scatter output has a missing or trailing value.");
        }
    }

    private static void VerifyEncodedVertex(
        ReadOnlySpan<byte> upload,
        ref int offset,
        Vertex expected)
    {
        if (ReadInt32(upload, ref offset) != expected.X
            || ReadInt32(upload, ref offset) != expected.Y
            || ReadInt32(upload, ref offset) != expected.Z
            || ReadInt32(upload, ref offset) != expected.Face
            || ReadInt32(upload, ref offset) != expected.Corner
            || ReadInt32(upload, ref offset) != expected.BlockId)
        {
            throw new InvalidDataException("The upload vertex encoding differs from the materialized vertex.");
        }
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static void AssertZero(ReadOnlySpan<byte> bytes, string region)
    {
        if (bytes.IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidDataException($"The canonical {region} contains a non-zero byte.");
        }
    }

    private static void WriteInt32(
        Span<byte> destination,
        ref int offset,
        int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[offset..],
            value);
        offset += sizeof(int);
    }

    private static int WritePayload(
        Span<byte> destination,
        int length,
        int seed,
        int stageMask)
    {
        int patternOffset = PayloadPatternOffset(
            seed,
            stageMask);
        PayloadPatterns.AsSpan(
            patternOffset,
            length).CopyTo(destination);
        return CountEnabledPayloadBytes(
            length,
            stageMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountEnabledPayloadBytes(
        int length,
        int stageMask)
    {
        int enabledBits = stageMask & 0b1111;
        int completeGroups = length >> 2;
        int remainderMask = enabledBits
            & ((1 << (length & 3)) - 1);
        return checked(
            completeGroups * EnabledByteCounts[enabledBits]
            + EnabledByteCounts[remainderMask]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> GetPayloadPattern(
        ReadOnlySpan<byte> payloadPatterns,
        int length,
        int seed,
        int stageMask) =>
        payloadPatterns.Slice(
            PayloadPatternOffset(seed, stageMask),
            length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteFaceVertices(
        FaceRecord record,
        int face,
        Span<Vertex> destination)
    {
        int x = record.CellIndex % VoxelMath.ChunkDimension;
        int yz = record.CellIndex / VoxelMath.ChunkDimension;
        int y = yz % VoxelMath.ChunkDimension;
        int z = yz / VoxelMath.ChunkDimension;
        WriteFaceVertices(x, y, z, record.BlockId, face, destination);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteFaceVertices(
        int x,
        int y,
        int z,
        int blockId,
        int face,
        Span<Vertex> destination)
    {
        switch (face)
        {
            case 0:
                destination[0] = new(x, y, z, face, 0, blockId);
                destination[1] = new(x, y, z + 1, face, 1, blockId);
                destination[2] = new(x, y + 1, z + 1, face, 2, blockId);
                destination[3] = new(x, y + 1, z, face, 3, blockId);
                return;
            case 1:
                destination[0] = new(x + 1, y, z, face, 0, blockId);
                destination[1] = new(x + 1, y, z + 1, face, 1, blockId);
                destination[2] = new(x + 1, y + 1, z + 1, face, 2, blockId);
                destination[3] = new(x + 1, y + 1, z, face, 3, blockId);
                return;
            case 2:
                destination[0] = new(x, y, z, face, 0, blockId);
                destination[1] = new(x + 1, y, z + 1, face, 1, blockId);
                destination[2] = new(x, y, z + 1, face, 2, blockId);
                destination[3] = new(x + 1, y, z, face, 3, blockId);
                return;
            case 3:
                destination[0] = new(x, y + 1, z, face, 0, blockId);
                destination[1] = new(x + 1, y + 1, z + 1, face, 1, blockId);
                destination[2] = new(x, y + 1, z + 1, face, 2, blockId);
                destination[3] = new(x + 1, y + 1, z, face, 3, blockId);
                return;
            case 4:
                destination[0] = new(x, y, z, face, 0, blockId);
                destination[1] = new(x + 1, y, z, face, 1, blockId);
                destination[2] = new(x, y + 1, z, face, 2, blockId);
                destination[3] = new(x + 1, y + 1, z, face, 3, blockId);
                return;
            case 5:
                destination[0] = new(x, y, z + 1, face, 0, blockId);
                destination[1] = new(x + 1, y, z + 1, face, 1, blockId);
                destination[2] = new(x, y + 1, z + 1, face, 2, blockId);
                destination[3] = new(x + 1, y + 1, z + 1, face, 3, blockId);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(face));
        }
    }

    private const int VertexInts = 6;

    // A face's four vertices at (0, 0, 0) with block 0, from the scalar writer;
    // the vector writer adds the cell's (x, y, z, 0, 0, blockId) to them.
    private static readonly int[] FaceVertexTemplates = CreateFaceVertexTemplates();

    private static int[] CreateFaceVertexTemplates()
    {
        Vertex[] vertices =
            new Vertex[VoxelMath.FacesPerCell * VoxelMath.VerticesPerFace];
        for (int face = 0; face < VoxelMath.FacesPerCell; face++)
        {
            WriteFaceVertices(
                0,
                0,
                0,
                0,
                face,
                vertices.AsSpan(
                    face * VoxelMath.VerticesPerFace,
                    VoxelMath.VerticesPerFace));
        }

        return MemoryMarshal.Cast<Vertex, int>(vertices).ToArray();
    }

    // A face's 24 vertex ints repeat (x, y, z, 0, 0, blockId) every six, so
    // as 4-int vectors they cycle through three base vectors.
    [StructLayout(LayoutKind.Auto)]
    private readonly struct FaceVertexOrigin
    {
        internal readonly Vector128<int> First;
        internal readonly Vector128<int> Second;
        internal readonly Vector128<int> Third;

        internal FaceVertexOrigin(int cellIndex, int blockId)
        {
            int x = cellIndex % VoxelMath.ChunkDimension;
            int yz = cellIndex / VoxelMath.ChunkDimension;
            int y = yz % VoxelMath.ChunkDimension;
            int z = yz / VoxelMath.ChunkDimension;
            First = Vector128.Create(x, y, z, 0);
            Second = Vector128.Create(0, blockId, x, y);
            Third = Vector128.Create(z, 0, 0, blockId);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteFaceVertices(
        int face,
        in FaceVertexOrigin origin,
        Span<int> destination)
    {
        const int Lanes = 4;
        ReadOnlySpan<int> template = FaceVertexTemplates.AsSpan(
            face * VoxelMath.VerticesPerFace * VertexInts,
            VoxelMath.VerticesPerFace * VertexInts);
        (Vector128.Create(template) + origin.First).CopyTo(destination);
        (Vector128.Create(template[(1 * Lanes)..]) + origin.Second).CopyTo(destination[(1 * Lanes)..]);
        (Vector128.Create(template[(2 * Lanes)..]) + origin.Third).CopyTo(destination[(2 * Lanes)..]);
        (Vector128.Create(template[(3 * Lanes)..]) + origin.First).CopyTo(destination[(3 * Lanes)..]);
        (Vector128.Create(template[(4 * Lanes)..]) + origin.Second).CopyTo(destination[(4 * Lanes)..]);
        (Vector128.Create(template[(5 * Lanes)..]) + origin.Third).CopyTo(destination[(5 * Lanes)..]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteFaceIndices(
        int vertexOffset,
        Span<int> destination)
    {
        destination[0] = vertexOffset;
        destination[1] = vertexOffset + 1;
        destination[2] = vertexOffset + 2;
        destination[3] = vertexOffset + 2;
        destination[4] = vertexOffset + 3;
        destination[5] = vertexOffset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteFaceIndices(
        ref int destination,
        int vertexOffset)
    {
        destination = vertexOffset;
        Unsafe.Add(ref destination, 1) = vertexOffset + 1;
        Unsafe.Add(ref destination, 2) = vertexOffset + 2;
        Unsafe.Add(ref destination, 3) = vertexOffset + 2;
        Unsafe.Add(ref destination, 4) = vertexOffset + 3;
        Unsafe.Add(ref destination, 5) = vertexOffset;
    }

    private static void WriteVertices(
        ReadOnlySpan<Vertex> values,
        Span<byte> destination,
        ref int offset)
    {
        if (BitConverter.IsLittleEndian)
        {
            ReadOnlySpan<byte> bytes =
                MemoryMarshal.AsBytes(values);
            bytes.CopyTo(destination[offset..]);
            offset += bytes.Length;
            return;
        }

        foreach (ref readonly Vertex value in values)
        {
            WriteInt32(destination, ref offset, value.X);
            WriteInt32(destination, ref offset, value.Y);
            WriteInt32(destination, ref offset, value.Z);
            WriteInt32(destination, ref offset, value.Face);
            WriteInt32(destination, ref offset, value.Corner);
            WriteInt32(destination, ref offset, value.BlockId);
        }
    }

    private static void WriteInt32Values(
        ReadOnlySpan<int> values,
        Span<byte> destination,
        ref int offset)
    {
        if (BitConverter.IsLittleEndian)
        {
            ReadOnlySpan<byte> bytes =
                MemoryMarshal.AsBytes(values);
            bytes.CopyTo(destination[offset..]);
            offset += bytes.Length;
            return;
        }

        foreach (ref readonly int value in values)
        {
            WriteInt32(destination, ref offset, value);
        }
    }

    private static int CalculateMaximumPressureStageBytesPerFace()
    {
        int maximum = 0;
        foreach (BlockTypeDescriptor type in VoxelMath.BlockTypes)
        {
            maximum = Math.Max(
                maximum,
                PressureStageBytes(type));
        }

        return maximum;
    }

    private static int CalculateMaximumPayloadBytes()
    {
        int maximum = 0;
        foreach (BlockTypeDescriptor type in VoxelMath.BlockTypes)
        {
            maximum = Math.Max(
                maximum,
                type.PayloadBytes);
        }

        return maximum;
    }

    private static byte[] CreatePayloadPatterns()
    {
        byte[] patterns = GC.AllocateUninitializedArray<byte>(
            checked(16 * 256 * MaximumPayloadBytes));
        for (int enabledBits = 0;
            enabledBits < 16;
            enabledBits++)
        {
            for (int seed = 0;
                seed <= byte.MaxValue;
                seed++)
            {
                int patternOffset = checked(
                    ((enabledBits << 8) + seed)
                    * MaximumPayloadBytes);
                Span<byte> pattern = patterns.AsSpan(
                    patternOffset,
                    MaximumPayloadBytes);
                for (int offset = 0;
                    offset < pattern.Length;
                    offset++)
                {
                    pattern[offset] =
                        (enabledBits & (1 << (offset & 3))) == 0
                            ? (byte)0
                            : (byte)unchecked(seed + offset * 17);
                }
            }
        }

        return patterns;
    }

    private static int PressureStageBytes(BlockTypeDescriptor type)
    {
        return VoxelMath.AlignUp(
            checked(
                type.PayloadBytes
                + VoxelMath.VerticesPerFace * VoxelMath.VertexBytes
                + VoxelMath.IndicesPerFace * VoxelMath.IndexBytes
                + GpuCommandPaddingBytesPerFace),
            type.Alignment);
    }

    private ref struct SpanSectionInitializationBuffer<T>
        : ISectionInitializationBuffer<T>
        where T : unmanaged
    {
        private readonly Span<T> _values;
        private int _position;

        internal SpanSectionInitializationBuffer(Span<T> values)
        {
            _values = values;
            _position = 0;
        }

        public int Length => _values.Length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Append(T value)
        {
            _values[_position++] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T ReadInitialized(int index) => _values[index];

        public void Complete()
        {
            if (_position != _values.Length)
            {
                throw new InvalidOperationException(
                    "The section initializer did not write its complete range.");
            }
        }

        public uint MixInitialized(
            uint hash,
            int start,
            int length)
        {
            if (length == 0)
            {
                return hash;
            }

            return MixSectionContent<T>(
                hash,
                _values.Slice(start, length));
        }
    }

    private ref struct SpanOutputInitializationBuffer<T>
        : IOutputInitializationBuffer<T>
    {
        private readonly Span<T> _values;

        internal SpanOutputInitializationBuffer(Span<T> values)
        {
            _values = values;
        }

        public int Length => _values.Length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(int index, T value)
        {
            _values[index] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(int start, scoped ReadOnlySpan<T> source)
        {
            source.CopyTo(_values[start..]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Fill(int start, int length, T value)
        {
            _values.Slice(start, length).Fill(value);
        }
    }
}
