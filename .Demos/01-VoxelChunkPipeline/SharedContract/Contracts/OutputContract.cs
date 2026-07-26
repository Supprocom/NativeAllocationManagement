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
