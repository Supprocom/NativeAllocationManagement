using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public readonly record struct FaceRecord(
    int CellIndex,
    int BlockId,
    int Mask,
    int PayloadBytes,
    int Alignment,
    int StageMask,
    int StageBytes);

public readonly record struct Vertex(int X, int Y, int Z, int Face, int Corner, int BlockId);

public readonly record struct PayloadSlice(int Offset, int Length, int Alignment, int StageMask, int BlockId, int CellIndex);

[InlineArray(160)]
public struct GpuStage160
{
    private byte _element0;
}

[InlineArray(168)]
public struct GpuStage168
{
    private byte _element0;
}

[InlineArray(176)]
public struct GpuStage176
{
    private byte _element0;
}

[InlineArray(192)]
public struct GpuStage192
{
    private byte _element0;
}

[InlineArray(224)]
public struct GpuStage224
{
    private byte _element0;
}

public ref struct GpuStageBuffers
{
    public GpuStageBuffers(
        Span<GpuStage160> stage160,
        Span<GpuStage168> stage168,
        Span<GpuStage176> stage176,
        Span<GpuStage192> stage192,
        Span<GpuStage224> stage224)
    {
        Stage160 = stage160;
        Stage168 = stage168;
        Stage176 = stage176;
        Stage192 = stage192;
        Stage224 = stage224;
    }

    public Span<GpuStage160> Stage160 { get; }

    public Span<GpuStage168> Stage168 { get; }

    public Span<GpuStage176> Stage176 { get; }

    public Span<GpuStage192> Stage192 { get; }

    public Span<GpuStage224> Stage224 { get; }

    public long ByteLength => checked(
        (long)Stage160.Length * 160
        + (long)Stage168.Length * 168
        + (long)Stage176.Length * 176
        + (long)Stage192.Length * 192
        + (long)Stage224.Length * 224);

    public Span<byte> GetStage(int stageBytes, int byteOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
        return stageBytes switch
        {
            160 => GetStageBytes(Stage160, byteOffset, 160),
            168 => GetStageBytes(Stage168, byteOffset, 168),
            176 => GetStageBytes(Stage176, byteOffset, 176),
            192 => GetStageBytes(Stage192, byteOffset, 192),
            224 => GetStageBytes(Stage224, byteOffset, 224),
            _ => throw new ArgumentOutOfRangeException(
                nameof(stageBytes),
                stageBytes,
                "The GPU stage size is not registered.")
        };
    }

    public Span<byte> GetAllBytes(int stageBytes) =>
        stageBytes switch
        {
            160 => MemoryMarshal.AsBytes(Stage160),
            168 => MemoryMarshal.AsBytes(Stage168),
            176 => MemoryMarshal.AsBytes(Stage176),
            192 => MemoryMarshal.AsBytes(Stage192),
            224 => MemoryMarshal.AsBytes(Stage224),
            _ => throw new ArgumentOutOfRangeException(
                nameof(stageBytes),
                stageBytes,
                "The GPU stage size is not registered.")
        };

    public int Count(int stageBytes) =>
        stageBytes switch
        {
            160 => Stage160.Length,
            168 => Stage168.Length,
            176 => Stage176.Length,
            192 => Stage192.Length,
            224 => Stage224.Length,
            _ => throw new ArgumentOutOfRangeException(
                nameof(stageBytes),
                stageBytes,
                "The GPU stage size is not registered.")
        };

    public GpuStageBuffers Slice(
        GpuStageShape shape,
        int stage160Offset,
        int stage168Offset,
        int stage176Offset,
        int stage192Offset,
        int stage224Offset,
        bool opaque)
    {
        int opaqueOffset160 =
            opaque ? 0 : shape.OpaqueStage160Count;
        int opaqueOffset168 =
            opaque ? 0 : shape.OpaqueStage168Count;
        int opaqueOffset176 =
            opaque ? 0 : shape.OpaqueStage176Count;
        int opaqueOffset192 =
            opaque ? 0 : shape.OpaqueStage192Count;
        int opaqueOffset224 =
            opaque ? 0 : shape.OpaqueStage224Count;
        return new GpuStageBuffers(
            Stage160.Slice(
                checked(stage160Offset + opaqueOffset160),
                opaque
                    ? shape.OpaqueStage160Count
                    : shape.TransparentCountFor(160)),
            Stage168.Slice(
                checked(stage168Offset + opaqueOffset168),
                opaque
                    ? shape.OpaqueStage168Count
                    : shape.TransparentCountFor(168)),
            Stage176.Slice(
                checked(stage176Offset + opaqueOffset176),
                opaque
                    ? shape.OpaqueStage176Count
                    : shape.TransparentCountFor(176)),
            Stage192.Slice(
                checked(stage192Offset + opaqueOffset192),
                opaque
                    ? shape.OpaqueStage192Count
                    : shape.TransparentCountFor(192)),
            Stage224.Slice(
                checked(stage224Offset + opaqueOffset224),
                opaque
                    ? shape.OpaqueStage224Count
                    : shape.TransparentCountFor(224)));
    }

    private static Span<byte> GetStageBytes<T>(
        Span<T> values,
        int byteOffset,
        int stageBytes)
        where T : unmanaged
    {
        if (byteOffset % stageBytes != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteOffset),
                "The GPU stage offset is not aligned to its record size.");
        }

        int index = byteOffset / stageBytes;
        if ((uint)index >= (uint)values.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset));
        }

        return MemoryMarshal.AsBytes(values.Slice(index, 1));
    }
}

public readonly record struct SectionPrerenderDescriptor(
    int SectionIndex,
    SectionRepresentationKind Kind,
    ushort UniformBlockId,
    int OpaqueCount,
    int TransparentCount,
    int EmptyCount,
    int BitsPerIndex,
    int ValueOffset,
    int ValueLength,
    int PackedWordOffset,
    int PackedWordLength,
    int TransparentTileOffset,
    int TransparentTileLength,
    int OpaqueBitsOffset,
    int TransparentBitsOffset,
    int EmptyBitsOffset,
    int OpaqueFaceBitsOffset,
    int TransparentFaceBitsOffset,
    bool HasBounds,
    byte MinX,
    byte MinY,
    byte MinZ,
    byte MaxX,
    byte MaxY,
    byte MaxZ,
    int SectionBaseX,
    int SectionBaseY,
    int SectionBaseZ,
    int ContentTag);

public struct VoxelCell
{
    public ushort BlockId;
    public short Density;
    public int FaceMask;
    public int OpaqueMask;
    public int TransparentMask;
    public int Section;
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

public readonly record struct CanonicalOutputSummary(
    [property: JsonIgnore] long ByteHash,
    int OpaqueVertexLength,
    int OpaqueIndexLength,
    int OpaqueSliceLength,
    int OpaqueUploadLength,
    int TransparentVertexLength,
    int TransparentIndexLength,
    int TransparentSliceLength,
    int TransparentUploadLength,
    long OpaqueFaceCount,
    long TransparentFaceCount,
    long OpaqueStagedBytes,
    long TransparentStagedBytes,
    string StrongHash = "");

public readonly record struct ChunkOutputSummary(
    int ChunkId,
    [property: JsonIgnore] long ByteHash,
    int OpaqueVertexLength,
    int OpaqueIndexLength,
    int OpaqueSliceLength,
    int OpaqueUploadLength,
    int TransparentVertexLength,
    int TransparentIndexLength,
    int TransparentSliceLength,
    int TransparentUploadLength,
    string StrongOutputHash = "",
    string StrongInputHash = "",
    long InputCellCount = 0,
    CanonicalInputCell[]? InputCells = null);

public readonly record struct NativeOwnerProfile(
    string Owner,
    long RequestedBytes,
    long PeakRequestedBytes,
    long PeakPhysicalBytes,
    long RetainedPhysicalBytes,
    long RetiredPhysicalBytes,
    long PeakGeometricSlackBytes,
    int PeakSegmentCount,
    int RetainedSegmentCount,
    long TrimmedBytes,
    long TrimCalls,
    long RegrowthCount);

public readonly record struct StreamResult(
    int FaceCount,
    int VertexCount,
    int IndexCount,
    int StagedBytes,
    int EnabledStageBytes);

public readonly record struct ChunkResult(
    [property: JsonIgnore] long Digest,
    int OpaqueFaces,
    int TransparentFaces,
    int OpaqueVertices,
    int TransparentVertices,
    int OpaqueIndices,
    int TransparentIndices,
    int OpaqueStagedBytes,
    int TransparentStagedBytes,
    int EnabledStageBytes,
    int EmptySections,
    int UniformSections,
    int ExpandedSections,
    int PackedSections,
    int MultiPackedSections,
    int TransparentMaskCount,
    int TransparentMaskWords,
    int DominantTransparentSections,
    int ResidualTransparentSections,
    OutputFixture? MaterializedOutput,
    double GenerationMilliseconds = 0,
    double FaceDerivationMilliseconds = 0,
    double TransparentMaskMilliseconds = 0,
    double OpaquePackingMilliseconds = 0,
    double TransparentPackingMilliseconds = 0,
    double CoordinateRecycleMilliseconds = 0,
    double FaceRecycleMilliseconds = 0,
    double MaskRecycleMilliseconds = 0,
    double PackingRecycleMilliseconds = 0,
    [property: JsonIgnore] long OutputByteHash = 17,
    int ChunkId = -1,
    string StrongOutputHash = "",
    string StrongInputHash = "",
    CanonicalInputCell[]? InputCells = null)
{
    public int VisibleFaces => OpaqueFaces + TransparentFaces;
    public int Vertices => OpaqueVertices + TransparentVertices;
    public int Indices => OpaqueIndices + TransparentIndices;
    public int StagedBytes => OpaqueStagedBytes + TransparentStagedBytes;
}

public readonly record struct WorkerResult(PipelineResult Result);

public readonly record struct PipelineResult(
    string Implementation,
    [property: JsonIgnore] long Digest,
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
    long MeasuredLeaseCount = 0,
    long ReusedNativeSegmentCount = 0,
    double MeasuredMilliseconds = 0,
    long MeasuredManagedAllocatedBytes = 0,
    int MeasuredGen0Collections = 0,
    int MeasuredGen1Collections = 0,
    int MeasuredGen2Collections = 0,
    OutputFixture? MaterializedOutput = null,
    long ColdManagedBackingBytes = 0,
    OutputFixture? IndependentFixture = null,
    long ReclaimedRangeReuseCount = 0,
    long ReclaimedRangeReuseBytes = 0,
    double GenerationMilliseconds = 0,
    double FaceDerivationMilliseconds = 0,
    double TransparentMaskMilliseconds = 0,
    double OpaquePackingMilliseconds = 0,
    double TransparentPackingMilliseconds = 0,
    double CoordinateRecycleMilliseconds = 0,
    double FaceRecycleMilliseconds = 0,
    double MaskRecycleMilliseconds = 0,
    double PackingRecycleMilliseconds = 0,
    CanonicalInputContract Input = default,
    CanonicalOutputSummary Output = default,
    IReadOnlyList<NativeOwnerProfile>? NativeOwnerProfiles = null,
    IReadOnlyList<ChunkOutputSummary>? ChunkOutputs = null,
    string StrongOutputHash = "",
    double ColdEndToEndMilliseconds = 0);
