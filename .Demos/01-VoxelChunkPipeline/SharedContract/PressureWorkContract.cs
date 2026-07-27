using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

public readonly record struct PressureChunkPlanEntry(
    int ChunkId,
    long LogicalDemandBytes,
    long EstimatedWorkUnits,
    PressureChunkShape Shape);

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
    private static long _measurementConsumerSink;

    private static readonly int[] RegionArchetypes =
    [
        6,
        4,
        5,
        1,
        3,
        7,
        0,
        2,
        5,
        4,
        6,
        1,
        7,
        3,
        0,
        2
    ];

    private static readonly long[] ManagedResidentBytesByDepth =
    [
        0,
        18_449_920,
        29_558_272,
        41_774_080,
        51_776_512,
        64_411_648,
        76_208_128,
        86_169_600,
        96_212_992,
        111_423_488,
        121_483_264,
        131_444_736,
        145_076_224,
        155_037_696,
        164_999_168,
        174_960_640,
        185_085_952,
        205_545_472,
        215_506_944,
        225_665_024,
        235_626_496
    ];

    public const int DefaultRetentionDepth = 20;
    public const int DefaultResidentDepth = DefaultRetentionDepth;
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
    public const int CanonicalResidentFaceRecordCapacity = 429_588;
    public const int CanonicalResidentTransparentMaskWordCapacity = 30_720;
    public const int CanonicalResidentPayloadSliceCapacity = 693_168;
    public const int CanonicalResidentUploadByteCapacity = 127_726_496;
    public const int CanonicalResidentSectionDescriptorCapacity = 160;
    public const int CanonicalResidentSectionValueCapacity = 655_360;
    public const int CanonicalResidentSectionWordCapacity = 64_320;
    public const int CanonicalResidentSectionStateWordCapacity = 38_400;
    public const int CanonicalMaximumVertexCapacity = 140_344;
    public const int CanonicalMaximumIndexCapacity = 210_516;
    public const int CanonicalMaximumArraysPerPoolBucket = 20;
    public const int CanonicalRetainedArraysPerPoolBucket = 1;
    public const long CanonicalArenaReservationBytes = 47_865_152;
    public const long CanonicalManagedPoolResidentBytes = 235_626_496;
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
            || entries.Count < minimumChunks)
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
        if (chunkId < DefaultRetentionDepth)
        {
            return chunkId;
        }

        int relative = chunkId - DefaultRetentionDepth;
        int region = relative / DefaultRetentionDepth;
        int archetype = RegionArchetypes[region % RegionArchetypes.Length];
        return checked((chunkId << 3) | archetype);
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

        int sourceChunkIndex = PressureSourceChunkIndex(chunkId);
        int cell = 0;
        for (int z = 0; z < VoxelMath.ChunkDimension; z++)
        {
            int sectionZ = z / VoxelMath.SectionDimension;
            for (int y = 0; y < VoxelMath.ChunkDimension; y++)
            {
                int sectionBase = checked(
                    (sectionZ * VoxelMath.SectionsPerAxis
                        + y / VoxelMath.SectionDimension)
                    * VoxelMath.SectionsPerAxis);
                for (int x = 0; x < VoxelMath.ChunkDimension; x++, cell++)
                {
                    int blockId = VoxelMath.BlockIdForCell(
                        seed,
                        sourceChunkIndex,
                        x,
                        y,
                        z);
                    cells[cell] = new VoxelCell
                    {
                        BlockId = checked((ushort)blockId),
                        Density = VoxelMath.DensityForCell(
                            seed,
                            sourceChunkIndex,
                            x,
                            y,
                            z,
                            blockId),
                        Section = checked(
                            sectionBase + x / VoxelMath.SectionDimension)
                    };
                }
            }
        }
    }

    public static PressureChunkShape DeriveChunkShape(
        Span<VoxelCell> cells,
        Span<SectionSummary> sectionSummaries)
    {
        if (cells.Length != VoxelMath.CellsPerChunk
            || sectionSummaries.Length != VoxelMath.SectionsPerChunk)
        {
            throw new ArgumentException("Pressure shape buffers do not cover one canonical chunk.");
        }

        int opaqueRecords = 0;
        int transparentRecords = 0;
        int opaqueFaces = 0;
        int transparentFaces = 0;
        GpuStageShape gpuStages = default;
        int cell = 0;
        int rowStride = VoxelMath.ChunkDimension;
        int planeStride = checked(
            VoxelMath.ChunkDimension * VoxelMath.ChunkDimension);
        for (int z = 0; z < VoxelMath.ChunkDimension; z++)
        {
            for (int y = 0; y < VoxelMath.ChunkDimension; y++)
            {
                for (int x = 0; x < VoxelMath.ChunkDimension; x++, cell++)
                {
                    int blockId = cells[cell].BlockId;
                    if (blockId == VoxelMath.AirBlockId)
                    {
                        cells[cell].FaceMask = 0;
                        cells[cell].OpaqueMask = 0;
                        cells[cell].TransparentMask = 0;
                        continue;
                    }

                    bool transparent = VoxelMath.TransparentById[blockId];
                    int mask = 0;
                    if (PressureFaceVisible(
                            blockId,
                            x == 0
                                ? VoxelMath.AirBlockId
                                : cells[cell - 1].BlockId,
                            transparent))
                    {
                        mask |= 1 << 0;
                    }

                    if (PressureFaceVisible(
                            blockId,
                            x == VoxelMath.ChunkDimension - 1
                                ? VoxelMath.AirBlockId
                                : cells[cell + 1].BlockId,
                            transparent))
                    {
                        mask |= 1 << 1;
                    }

                    if (PressureFaceVisible(
                            blockId,
                            y == 0
                                ? VoxelMath.AirBlockId
                                : cells[cell - rowStride].BlockId,
                            transparent))
                    {
                        mask |= 1 << 2;
                    }

                    if (PressureFaceVisible(
                            blockId,
                            y == VoxelMath.ChunkDimension - 1
                                ? VoxelMath.AirBlockId
                                : cells[cell + rowStride].BlockId,
                            transparent))
                    {
                        mask |= 1 << 3;
                    }

                    if (PressureFaceVisible(
                            blockId,
                            z == 0
                                ? VoxelMath.AirBlockId
                                : cells[cell - planeStride].BlockId,
                            transparent))
                    {
                        mask |= 1 << 4;
                    }

                    if (PressureFaceVisible(
                            blockId,
                            z == VoxelMath.ChunkDimension - 1
                                ? VoxelMath.AirBlockId
                                : cells[cell + planeStride].BlockId,
                            transparent))
                    {
                        mask |= 1 << 5;
                    }

                    cells[cell].FaceMask = mask;
                    cells[cell].OpaqueMask = !transparent ? mask : 0;
                    cells[cell].TransparentMask = transparent ? mask : 0;
                    if (mask == 0)
                    {
                        continue;
                    }

                    BlockTypeDescriptor type = VoxelMath.BlockTypeForId(blockId);
                    int faceCount = VoxelMath.FaceCount(mask);
                    int stageBytes = PressureStageBytes(type);
                    if (transparent)
                    {
                        transparentRecords++;
                        transparentFaces += faceCount;
                        gpuStages = gpuStages.Add(
                            stageBytes,
                            faceCount,
                            type.PayloadBytes,
                            opaque: false);
                    }
                    else
                    {
                        opaqueRecords++;
                        opaqueFaces += faceCount;
                        gpuStages = gpuStages.Add(
                            stageBytes,
                            faceCount,
                            type.PayloadBytes,
                            opaque: true);
                    }
                }
            }
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
            SectionSummary summary = VoxelMath.ClassifySection(cells, section);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool PressureFaceVisible(
        int currentBlockId,
        int neighborBlockId,
        bool currentTransparent) =>
        neighborBlockId == VoxelMath.AirBlockId
        || (currentTransparent
            ? neighborBlockId != currentBlockId
            : VoxelMath.TransparentById[neighborBlockId]);

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

    public static void BuildSectionRepresentations(
        ReadOnlySpan<VoxelCell> cells,
        ReadOnlySpan<SectionSummary> summaries,
        Span<SectionPrerenderDescriptor> descriptors,
        Span<ushort> values,
        Span<uint> words,
        Span<ulong> states)
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
            int valueLength = SectionValueCount(summary);
            int wordLength = SectionWordCount(summary);
            int stateLength = SectionStateWordCount(summary);
            SpanSectionInitializationBuffer<ushort> valueBuffer = new(
                values.Slice(valueCursor, valueLength));
            SpanSectionInitializationBuffer<uint> wordBuffer = new(
                words.Slice(wordCursor, wordLength));
            SpanSectionInitializationBuffer<ulong> stateBuffer = new(
                states.Slice(stateCursor, stateLength));
            SectionPrerenderDescriptor descriptor =
                BuildSectionRepresentation<
                    SpanSectionInitializationBuffer<ushort>,
                    SpanSectionInitializationBuffer<uint>,
                    SpanSectionInitializationBuffer<ulong>>(
                cells,
                sectionIndex,
                summary,
                ref valueBuffer,
                ref wordBuffer,
                ref stateBuffer,
                ref valueCursor,
                ref wordCursor,
                ref stateCursor);
            descriptors[sectionIndex] = descriptor;
        }

        if (valueCursor != values.Length
            || wordCursor != words.Length
            || stateCursor != states.Length)
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
        SpanSectionInitializationBuffer<ulong> stateBuffer = new(states);
        return BuildSectionRepresentation<
            SpanSectionInitializationBuffer<ushort>,
            SpanSectionInitializationBuffer<uint>,
            SpanSectionInitializationBuffer<ulong>>(
            cells,
            sectionIndex,
            summary,
            ref valueBuffer,
            ref wordBuffer,
            ref stateBuffer,
            ref valueCursor,
            ref wordCursor,
            ref stateCursor);
    }

    public static SectionPrerenderDescriptor BuildSectionRepresentation<
        TValueBuffer,
        TWordBuffer,
        TStateBuffer>(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionSummary summary,
        ref TValueBuffer values,
        ref TWordBuffer words,
        ref TStateBuffer states,
        ref int valueCursor,
        ref int wordCursor,
        ref int stateCursor)
        where TValueBuffer : ISectionInitializationBuffer<ushort>, allows ref struct
        where TWordBuffer : ISectionInitializationBuffer<uint>, allows ref struct
        where TStateBuffer : ISectionInitializationBuffer<ulong>, allows ref struct
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
        if (values.Length != valueCursor - valueStart
            || words.Length != wordCursor - wordStart
            || states.Length != stateCursor - stateStart)
        {
            throw new ArgumentException(
                "The section output ranges do not match the classified representation.");
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
        PopulateSectionStates(
            cells,
            sectionIndex,
            relative,
            ref states);
        values.Complete();
        words.Complete();
        states.Complete();
        return descriptor with
        {
            ContentTag = ComputeSectionContentTag(
                relative,
                ref values,
                ref words,
                ref states)
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
        for (int index = 0; index < descriptors.Length; index++)
        {
            AddSectionDescriptor(hash, descriptors[index]);
        }

        hash.AddInt32(values.Length);
        for (int index = 0; index < values.Length; index++)
        {
            hash.AddUInt16(values[index]);
        }

        hash.AddInt32(words.Length);
        for (int index = 0; index < words.Length; index++)
        {
            hash.AddInt32(unchecked((int)words[index]));
        }

        hash.AddInt32(states.Length);
        for (int index = 0; index < states.Length; index++)
        {
            hash.AddInt64(unchecked((long)states[index]));
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
        string outputEvidenceHash)
    {
        using CanonicalHashAccumulator hash = new();
        hash.AddString("voxel-pressure-chunk-v2");
        hash.AddString(sectionEvidenceHash);
        hash.AddString(outputEvidenceHash);
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

        int opaque = 0;
        int transparent = 0;
        for (int cell = 0; cell < cells.Length; cell++)
        {
            int mask = cells[cell].FaceMask;
            if (mask == 0)
            {
                continue;
            }

            FaceRecord record = VoxelMath.CreateFaceRecord(cell, cells[cell].BlockId, mask);
            ref readonly SectionPrerenderDescriptor section =
                ref sections[cells[cell].Section];
            if (section.Kind == SectionRepresentationKind.Empty)
            {
                throw new InvalidDataException(
                    "A visible face referenced an empty section representation.");
            }

            record = record with
            {
                StageMask = unchecked(
                    record.StageMask
                    | ((int)section.Kind << 8)
                    | ((section.ContentTag & 0x000F_FFFF) << 12)),
                StageBytes = PressureStageBytes(
                    VoxelMath.BlockTypeForId(record.BlockId))
            };
            if (VoxelMath.TransparentById[record.BlockId])
            {
                records[shape.OpaqueRecordCount + transparent++] = record;
            }
            else
            {
                records[opaque++] = record;
            }
        }

        if (opaque != shape.OpaqueRecordCount || transparent != shape.TransparentRecordCount)
        {
            throw new InvalidDataException("Face record materialization disagrees with pressure classification.");
        }
    }

    public static void BuildTransparentMasks(
        ReadOnlySpan<VoxelCell> cells,
        ReadOnlySpan<SectionSummary> sectionSummaries,
        Span<ulong> masks)
    {
        int offset = 0;
        masks.Clear();
        for (int section = 0; section < sectionSummaries.Length; section++)
        {
            int expectedWords = checked(
                sectionSummaries[section].TransparentIds * VoxelMath.TransparentMaskWordsPerId);
            int written = VoxelMath.BuildTransparentMasks(
                cells,
                section,
                masks.Slice(offset, expectedWords));
            if (written != sectionSummaries[section].TransparentIds)
            {
                throw new InvalidDataException("Transparent mask emission disagrees with pressure classification.");
            }

            offset += expectedWords;
        }

        if (offset != masks.Length)
        {
            throw new InvalidDataException("Transparent mask materialization did not cover its exact logical range.");
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
        for (int recordIndex = 0;
            recordIndex < records.Length;
            recordIndex++)
        {
            ref readonly FaceRecord record =
                ref records[recordIndex];
            int stageClass = StageClassIndex(record.StageBytes);
            int payloadSeed = unchecked(
                seed
                + record.CellIndex * 11
                + record.BlockId * 37
                + record.StageMask * 13);
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
                    record,
                    face,
                    faceVertices);
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
        int vertexCount = 0;
        int indexCount = 0;
        int sliceCount = 0;
        Span<int> stageCursors = stackalloc int[5];
        for (int recordIndex = 0;
            recordIndex < records.Length;
            recordIndex++)
        {
            ref readonly FaceRecord record =
                ref records[recordIndex];
            int stageClass = StageClassIndex(
                record.StageBytes);
            uint remainingFaces =
                unchecked((uint)record.Mask) & 0b11_1111u;
            while (remainingFaces != 0)
            {
                int face = BitOperations.TrailingZeroCount(
                    remainingFaces);
                remainingFaces &= remainingFaces - 1;
                int stageOffset = stageCursors[stageClass];
                stageCursors[stageClass] = checked(
                    stageOffset + record.StageBytes);

                int vertexOffset = vertexCount;
                WriteFaceVertices(
                    record,
                    face,
                    vertices.Slice(
                        vertexOffset,
                        VoxelMath.VerticesPerFace));
                vertexCount += VoxelMath.VerticesPerFace;

                WriteFaceIndices(
                    vertexOffset,
                    indices.Slice(
                        indexCount,
                        VoxelMath.IndicesPerFace));
                indexCount += VoxelMath.IndicesPerFace;

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
        for (int recordIndex = 0; recordIndex < records.Length; recordIndex++)
        {
            ref readonly FaceRecord record = ref records[recordIndex];
            int payloadSeed = unchecked(
                seed
                + record.CellIndex * 11
                + record.BlockId * 37
                + record.StageMask * 13);
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
                    record,
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
        ReadOnlySpan<Vertex> opaqueVertices,
        ReadOnlySpan<int> opaqueIndices,
        ReadOnlySpan<PayloadSlice> opaqueSlices,
        ReadOnlySpan<Vertex> transparentVertices,
        ReadOnlySpan<int> transparentIndices,
        ReadOnlySpan<PayloadSlice> transparentSlices,
        GpuStageBuffers opaqueStages,
        GpuStageBuffers transparentStages)
    {
        return
        new(
            string.Empty,
            opaqueVertices.Length,
            opaqueIndices.Length,
            opaqueSlices.Length,
            SumStageLengths(opaqueSlices),
            transparentVertices.Length,
            transparentIndices.Length,
            transparentSlices.Length,
            SumStageLengths(transparentSlices));
    }

    public static PressureOutputEvidence DescribeScatterOutput(
        ReadOnlySpan<Vertex> opaqueVertices,
        ReadOnlySpan<int> opaqueIndices,
        ReadOnlySpan<PayloadSlice> opaqueSlices,
        ReadOnlySpan<Vertex> transparentVertices,
        ReadOnlySpan<int> transparentIndices,
        ReadOnlySpan<PayloadSlice> transparentSlices) =>
        new(
            string.Empty,
            opaqueVertices.Length,
            opaqueIndices.Length,
            opaqueSlices.Length,
            SumStageLengths(opaqueSlices),
            transparentVertices.Length,
            transparentIndices.Length,
            transparentSlices.Length,
            SumStageLengths(transparentSlices));

    public static void ConsumeGpuUpload(
        PressureOutputEvidence expected,
        ReadOnlySpan<Vertex> opaqueVertices,
        ReadOnlySpan<int> opaqueIndices,
        ReadOnlySpan<PayloadSlice> opaqueSlices,
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

        long sink = 0;
        ConsumeStageBuffer(
            MemoryMarshal.AsBytes(opaqueVertices),
            ref sink);
        ConsumeStageBuffer(
            MemoryMarshal.AsBytes(opaqueIndices),
            ref sink);
        ConsumeStages(opaqueStages, ref sink);
        ConsumeStageBuffer(
            MemoryMarshal.AsBytes(transparentVertices),
            ref sink);
        ConsumeStageBuffer(
            MemoryMarshal.AsBytes(transparentIndices),
            ref sink);
        ConsumeStages(transparentStages, ref sink);
        Volatile.Write(ref _measurementConsumerSink, sink);
    }

    public static void ConsumeDirectGpuUpload(
        PressureOutputEvidence expected,
        ReadOnlySpan<Vertex> opaqueVertices,
        ReadOnlySpan<int> opaqueIndices,
        ReadOnlySpan<PayloadSlice> opaqueSlices,
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

        long sink = 0;
        ConsumeDirectStageBuffer(
            MemoryMarshal.AsBytes(opaqueVertices),
            ref sink);
        ConsumeDirectStageBuffer(
            MemoryMarshal.AsBytes(opaqueIndices),
            ref sink);
        ConsumeDirectStages(opaqueStages, ref sink);
        ConsumeDirectStageBuffer(
            MemoryMarshal.AsBytes(transparentVertices),
            ref sink);
        ConsumeDirectStageBuffer(
            MemoryMarshal.AsBytes(transparentIndices),
            ref sink);
        ConsumeDirectStages(transparentStages, ref sink);
        Volatile.Write(ref _measurementConsumerSink, sink);
    }

    public static void ConsumeScatterGpuUpload(
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

        long sink = 0;
        ConsumeDirectStageBuffer(
            MemoryMarshal.AsBytes(opaqueVertices),
            ref sink);
        ConsumeDirectStageBuffer(
            MemoryMarshal.AsBytes(opaqueIndices),
            ref sink);
        ConsumeAliasedPayloadPatterns(
            seed,
            opaqueRecords,
            payloadPatterns,
            ref sink);
        ConsumeDirectStageBuffer(
            MemoryMarshal.AsBytes(transparentVertices),
            ref sink);
        ConsumeDirectStageBuffer(
            MemoryMarshal.AsBytes(transparentIndices),
            ref sink);
        ConsumeAliasedPayloadPatterns(
            seed,
            transparentRecords,
            payloadPatterns,
            ref sink);
        sink = unchecked(
            sink
            + expected.OpaqueUploadLength
            + expected.TransparentUploadLength);
        Volatile.Write(ref _measurementConsumerSink, sink);
    }

    public static void VerifyRetainedOutput(
        PressureOutputEvidence expected,
        ReadOnlySpan<Vertex> opaqueVertices,
        ReadOnlySpan<int> opaqueIndices,
        ReadOnlySpan<PayloadSlice> opaqueSlices,
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

        string retainedHash = ComputeTypedOutputHash(
            opaqueVertices,
            opaqueIndices,
            opaqueSlices,
            transparentVertices,
            transparentIndices,
            transparentSlices,
            opaqueStages,
            transparentStages);
        if (!string.Equals(
            retainedHash,
            expected.CompleteHash,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Retained output values changed before the upload consumer boundary.");
        }
    }

    public static void VerifyRetainedScatterOutput(
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
        if (!string.Equals(
            retainedHash,
            expected.CompleteHash,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Retained scatter output changed before upload.");
        }
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

    private static void ConsumeAliasedPayloadPatterns(
        int seed,
        ReadOnlySpan<FaceRecord> records,
        ReadOnlySpan<byte> payloadPatterns,
        ref long sink)
    {
        for (int recordIndex = 0;
            recordIndex < records.Length;
            recordIndex++)
        {
            ref readonly FaceRecord record =
                ref records[recordIndex];
            int payloadSeed = unchecked(
                seed
                + record.CellIndex * 11
                + record.BlockId * 37
                + record.StageMask * 13);
            ConsumeDirectStageBuffer(
                GetPayloadPattern(
                    payloadPatterns,
                    record.PayloadBytes,
                    payloadSeed,
                    record.StageMask),
                ref sink);
            sink = unchecked(
                sink + VoxelMath.FaceCount(record.Mask));
        }
    }

    private static void ConsumeStages(
        GpuStageBuffers stages,
        ref long sink)
    {
        ConsumeStageBuffer(stages.GetAllBytes(160), ref sink);
        ConsumeStageBuffer(stages.GetAllBytes(168), ref sink);
        ConsumeStageBuffer(stages.GetAllBytes(176), ref sink);
        ConsumeStageBuffer(stages.GetAllBytes(192), ref sink);
        ConsumeStageBuffer(stages.GetAllBytes(224), ref sink);
    }

    private static void ConsumeDirectStages(
        GpuStageBuffers stages,
        ref long sink)
    {
        ConsumeDirectStageBuffer(stages.GetAllBytes(160), ref sink);
        ConsumeDirectStageBuffer(stages.GetAllBytes(168), ref sink);
        ConsumeDirectStageBuffer(stages.GetAllBytes(176), ref sink);
        ConsumeDirectStageBuffer(stages.GetAllBytes(192), ref sink);
        ConsumeDirectStageBuffer(stages.GetAllBytes(224), ref sink);
    }

    private static int SumStageLengths(
        ReadOnlySpan<PayloadSlice> slices)
    {
        int length = 0;
        for (int index = 0; index < slices.Length; index++)
        {
            length = checked(length + slices[index].Length);
        }

        return length;
    }

    private static void ValidateStageCoverage(
        ReadOnlySpan<PayloadSlice> slices,
        GpuStageBuffers stages)
    {
        Span<int> cursors = stackalloc int[5];
        for (int index = 0; index < slices.Length; index++)
        {
            PayloadSlice slice = slices[index];
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
        for (int recordIndex = 0;
            recordIndex < records.Length;
            recordIndex++)
        {
            ref readonly FaceRecord record =
                ref records[recordIndex];
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
        for (int recordIndex = 0;
            recordIndex < records.Length;
            recordIndex++)
        {
            ref readonly FaceRecord record =
                ref records[recordIndex];
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

    private static void ConsumeStageBuffer(
        ReadOnlySpan<byte> upload,
        ref long sink)
    {
        Span<byte> staging = stackalloc byte[4_096];
        int offset = 0;
        while (offset < upload.Length)
        {
            int length = Math.Min(staging.Length, upload.Length - offset);
            upload.Slice(offset, length).CopyTo(staging);
            sink = unchecked(
                sink * 1_000_003
                + staging[0]
                + ((long)staging[length - 1] << 8)
                + length);
            offset += length;
        }
    }

    private static void ConsumeDirectStageBuffer(
        ReadOnlySpan<byte> upload,
        ref long sink)
    {
        const int transferBlockBytes = 4_096;
        int offset = 0;
        while (offset < upload.Length)
        {
            int length = Math.Min(
                transferBlockBytes,
                upload.Length - offset);
            sink = unchecked(
                sink * 1_000_003
                + upload[offset]
                + ((long)upload[offset + length - 1] << 8)
                + length);
            offset += length;
        }
    }

    public static string ComputeProfileEvidenceHash(IReadOnlyList<PressureChunkEvidence> chunks)
    {
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
        if (cells.Length != VoxelMath.CellsPerChunk
            || summaries.Length != VoxelMath.SectionsPerChunk
            || descriptors.Length != VoxelMath.SectionsPerChunk)
        {
            throw new ArgumentException(
                "Section representation ranges must cover one complete canonical chunk.");
        }

        int expectedValues = 0;
        int expectedWords = 0;
        int expectedStates = 0;
        for (int section = 0; section < summaries.Length; section++)
        {
            expectedValues = checked(
                expectedValues + SectionValueCount(summaries[section]));
            expectedWords = checked(
                expectedWords + SectionWordCount(summaries[section]));
            expectedStates = checked(
                expectedStates + SectionStateWordCount(summaries[section]));
        }

        if (values.Length != expectedValues
            || words.Length != expectedWords
            || states.Length != expectedStates)
        {
            throw new ArgumentException(
                "Section representation backing ranges do not match the classified layouts.");
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
        SpanSectionInitializationBuffer<ulong> buffer = new(states);
        PopulateSectionStates(
            cells,
            sectionIndex,
            descriptor,
            ref buffer);
    }

    private static void PopulateSectionStates<TStateBuffer>(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionPrerenderDescriptor descriptor,
        ref TStateBuffer states)
        where TStateBuffer : ISectionInitializationBuffer<ulong>, allows ref struct
    {
        int start = FirstStateOffset(descriptor);
        int length = SectionStateLength(descriptor);
        if (length == 0)
        {
            return;
        }

        Span<ulong> materialized =
            stackalloc ulong[MaximumSectionStateWordsPerSection];
        materialized = materialized[..length];
        PopulateSectionStateWords(
            cells,
            sectionIndex,
            descriptor,
            materialized);
        foreach (ulong value in materialized)
        {
            states.Append(value);
        }
    }

    private static void PopulateSectionStateWords(
        ReadOnlySpan<VoxelCell> cells,
        int sectionIndex,
        SectionPrerenderDescriptor descriptor,
        Span<ulong> states)
    {
        int start = FirstStateOffset(descriptor);
        int length = SectionStateLength(descriptor);
        if (states.Length != length)
        {
            throw new ArgumentException(
                "The state output range does not match the section layout.",
                nameof(states));
        }

        states.Clear();
        for (int localIndex = 0;
            localIndex < VoxelMath.CellsPerSection;
            localIndex++)
        {
            ushort blockId = GetSectionBlockId(
                cells,
                sectionIndex,
                localIndex);
            bool empty = blockId == VoxelMath.AirBlockId;
            bool transparent = !empty && VoxelMath.TransparentById[blockId];
            int occupancyOffset = empty
                ? descriptor.EmptyBitsOffset
                : transparent
                    ? descriptor.TransparentBitsOffset
                    : descriptor.OpaqueBitsOffset;
            SetWordBit(states, occupancyOffset - start, localIndex);
            if (!empty)
            {
                SetBoundaryBits(
                    states,
                    transparent
                        ? descriptor.TransparentFaceBitsOffset - start
                        : descriptor.OpaqueFaceBitsOffset - start,
                    localIndex);
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
        for (int paletteIndex = 0;
            paletteIndex < expectedPalette.Length;
            paletteIndex++)
        {
            ushort blockId = expectedPalette[paletteIndex];
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
        for (int localIndex = 0;
            localIndex < VoxelMath.CellsPerSection;
            localIndex++)
        {
            if (GetSectionBlockId(cells, sectionIndex, localIndex)
                == VoxelMath.AirBlockId)
            {
                continue;
            }

            int localY = localIndex & (VoxelMath.SectionDimension - 1);
            int packed = localIndex >> 4;
            int localX = packed & (VoxelMath.SectionDimension - 1);
            int localZ = packed >> 4;
            hasBounds = true;
            minX = Math.Min(minX, checked((byte)localX));
            minY = Math.Min(minY, checked((byte)localY));
            minZ = Math.Min(minZ, checked((byte)localZ));
            maxX = Math.Max(maxX, checked((byte)localX));
            maxY = Math.Max(maxY, checked((byte)localY));
            maxZ = Math.Max(maxZ, checked((byte)localZ));
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
            foreach (ushort value in values.Slice(
                descriptor.ValueOffset,
                descriptor.ValueLength))
            {
                hash = Mix(hash, value);
            }
        }

        if (descriptor.PackedWordLength > 0)
        {
            foreach (uint value in words.Slice(
                descriptor.PackedWordOffset,
                descriptor.PackedWordLength))
            {
                hash = Mix(hash, value);
            }
        }

        if (descriptor.TransparentTileLength > 0)
        {
            foreach (uint value in words.Slice(
                descriptor.TransparentTileOffset,
                descriptor.TransparentTileLength))
            {
                hash = Mix(hash, value);
            }
        }

        int stateLength = SectionStateLength(descriptor);
        if (stateLength > 0)
        {
            foreach (ulong value in states.Slice(
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
        TWordBuffer,
        TStateBuffer>(
        SectionPrerenderDescriptor descriptor,
        ref TValueBuffer values,
        ref TWordBuffer words,
        ref TStateBuffer states)
        where TValueBuffer : ISectionInitializationBuffer<ushort>, allows ref struct
        where TWordBuffer : ISectionInitializationBuffer<uint>, allows ref struct
        where TStateBuffer : ISectionInitializationBuffer<ulong>, allows ref struct
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
        hash = states.MixInitialized(
            hash,
            FirstStateOffset(descriptor),
            SectionStateLength(descriptor));

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
            foreach (ushort value in MemoryMarshal.Cast<T, ushort>(values))
            {
                hash = Mix(hash, value);
            }

            return hash;
        }

        if (typeof(T) == typeof(uint))
        {
            foreach (uint value in MemoryMarshal.Cast<T, uint>(values))
            {
                hash = Mix(hash, value);
            }

            return hash;
        }

        if (typeof(T) == typeof(ulong))
        {
            foreach (ulong value in MemoryMarshal.Cast<T, ulong>(values))
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
        for (int recordIndex = 0; recordIndex < records.Length; recordIndex++)
        {
            ref readonly FaceRecord record = ref records[recordIndex];
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
        for (int recordIndex = 0; recordIndex < records.Length; recordIndex++)
        {
            ref readonly FaceRecord record = ref records[recordIndex];
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
        for (int recordIndex = 0;
            recordIndex < records.Length;
            recordIndex++)
        {
            ref readonly FaceRecord record =
                ref records[recordIndex];
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
        int blockId = record.BlockId;
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

        foreach (Vertex value in values)
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

        foreach (int value in values)
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

            return MixSectionContent(
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
