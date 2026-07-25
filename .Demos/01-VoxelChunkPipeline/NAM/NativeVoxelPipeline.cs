using System.Diagnostics;
using Supprocom.NativeAllocationManagement;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.NAM;

internal static class NativeVoxelPipeline
{
    private readonly record struct ChunkResult(
        long Digest,
        int OpaqueFaces,
        int TransparentFaces,
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
        double TransparentPackingMilliseconds = 0)
    {
        internal int VisibleFaces => OpaqueFaces + TransparentFaces;
        internal int Vertices => VisibleFaces * VoxelMath.VerticesPerFace;
        internal int Indices => VisibleFaces * VoxelMath.IndicesPerFace;
        internal int StagedBytes => OpaqueStagedBytes + TransparentStagedBytes;
    }

    private struct Work
    {
        internal long Digest;
        internal int OpaqueFaces;
        internal int TransparentFaces;
        internal int OpaqueRecords;
        internal int TransparentRecords;
        internal int EmptySections;
        internal int UniformSections;
        internal int ExpandedSections;
        internal int PackedSections;
        internal int MultiPackedSections;
        internal int TransparentMaskCount;
        internal int TransparentMaskWords;
        internal int DominantTransparentSections;
        internal int ResidualTransparentSections;
        internal int OpaqueStagedBytes;
        internal int TransparentStagedBytes;
        internal int EnabledStageBytes;
        internal OutputFixture? MaterializedOutput;
    }

    private sealed class WorkerContext
    {
        private readonly VoxelWorkloadOptions _options;
        private readonly bool _captureMeasuredFixture;
        private readonly SectionSummary[] _sectionSummaries = new SectionSummary[64];
        private Work _work;
        private int _chunk;
        private bool _captureCompleteFixture;
        private long _peakCoordinateStageBytes;
        private long _peakFaceStageBytes;
        private long _peakPackingStageBytes;
        private bool _measureTimings;
        private long _generationTicks;
        private long _faceDerivationTicks;
        private long _transparentMaskTicks;
        private long _opaquePackingTicks;
        private long _transparentPackingTicks;
        private long _coordinateRecycleTicks;
        private long _faceRecycleTicks;
        private long _maskRecycleTicks;
        private long _packingRecycleTicks;

        internal WorkerContext(
            VoxelWorkloadOptions options,
            bool captureMeasuredFixture)
        {
            _options = options;
            _captureMeasuredFixture = captureMeasuredFixture;
            GenerationAction = GenerateCells;
            FaceAction = PopulateFaceOutputs;
            MaskAction = ProcessMasks;
            MeshAction = PackBoth;
        }

        internal NativeLeaseAction<VoxelCell> GenerationAction { get; }

        internal NativeLeasePairAction<VoxelCell, NativeFaceOutput> FaceAction { get; }

        internal NativeLeasePooledArenaAction<VoxelCell, ulong> MaskAction { get; }

        internal NativeLeaseQuintupleAction<NativeFaceOutput, Vertex, int, PayloadSlice, byte> MeshAction { get; }

        internal int OpaqueFaceCount => _work.OpaqueFaces;

        internal int TransparentFaceCount => _work.TransparentFaces;

        internal int OpaqueRecordCount => _work.OpaqueRecords;

        internal int TransparentRecordCount => _work.TransparentRecords;

        internal int OpaqueStageBytes => _work.OpaqueStagedBytes;

        internal int TransparentStageBytes => _work.TransparentStagedBytes;

        internal int TransparentMaskCountForTest => _work.TransparentMaskCount;

        internal long PeakCoordinateStageBytes => _peakCoordinateStageBytes;

        internal long PeakFaceStageBytes => _peakFaceStageBytes;

        internal long PeakPackingStageBytes => _peakPackingStageBytes;

        internal double GenerationMilliseconds => ToMilliseconds(_generationTicks);

        internal double FaceDerivationMilliseconds => ToMilliseconds(_faceDerivationTicks);

        internal double TransparentMaskMilliseconds => ToMilliseconds(_transparentMaskTicks);

        internal double OpaquePackingMilliseconds => ToMilliseconds(_opaquePackingTicks);

        internal double TransparentPackingMilliseconds => ToMilliseconds(_transparentPackingTicks);

        internal void AddCoordinateRecycleTicks(long ticks)
        {
            if (_measureTimings)
            {
                _coordinateRecycleTicks += ticks;
            }
        }

        internal void AddFaceRecycleTicks(long ticks)
        {
            if (_measureTimings)
            {
                _faceRecycleTicks += ticks;
            }
        }

        internal void AddMaskRecycleTicks(long ticks)
        {
            if (_measureTimings)
            {
                _maskRecycleTicks += ticks;
            }
        }

        internal void AddPackingRecycleTicks(long ticks)
        {
            if (_measureTimings)
            {
                _packingRecycleTicks += ticks;
            }
        }

        internal double CoordinateRecycleMilliseconds => ToMilliseconds(_coordinateRecycleTicks);

        internal double FaceRecycleMilliseconds => ToMilliseconds(_faceRecycleTicks);

        internal double MaskRecycleMilliseconds => ToMilliseconds(_maskRecycleTicks);

        internal double PackingRecycleMilliseconds => ToMilliseconds(_packingRecycleTicks);

        internal void BeginChunk(int chunk, long digest, bool measureTimings = false)
        {
            _chunk = chunk;
            _captureCompleteFixture = false;
            _work = new Work { Digest = digest };
            _measureTimings = measureTimings;
            _generationTicks = 0;
            _faceDerivationTicks = 0;
            _transparentMaskTicks = 0;
            _opaquePackingTicks = 0;
            _transparentPackingTicks = 0;
            _coordinateRecycleTicks = 0;
            _faceRecycleTicks = 0;
            _maskRecycleTicks = 0;
            _packingRecycleTicks = 0;
            _peakCoordinateStageBytes = 0;
            _peakFaceStageBytes = 0;
            _peakPackingStageBytes = 0;
        }

        internal void BeginIndependentFixture(
            int opaqueRecordCount,
            int opaqueFaceCount,
            int opaqueStageBytes,
            int transparentRecordCount,
            int transparentFaceCount,
            int transparentStageBytes)
        {
            BeginChunk(0, 17);
            _captureCompleteFixture = true;
            _work.OpaqueRecords = opaqueRecordCount;
            _work.OpaqueFaces = opaqueFaceCount;
            _work.OpaqueStagedBytes = opaqueStageBytes;
            _work.TransparentRecords = transparentRecordCount;
            _work.TransparentFaces = transparentFaceCount;
            _work.TransparentStagedBytes = transparentStageBytes;
        }

        internal ChunkResult FinishChunk()
        {
            _work.Digest = VoxelMath.DigestStep(_work.Digest, _work.OpaqueFaces);
            _work.Digest = VoxelMath.DigestStep(_work.Digest, _work.TransparentFaces);
            _work.Digest = VoxelMath.DigestStep(_work.Digest, _work.OpaqueFaces * VoxelMath.VerticesPerFace);
            _work.Digest = VoxelMath.DigestStep(_work.Digest, _work.TransparentFaces * VoxelMath.VerticesPerFace);
            _work.Digest = VoxelMath.DigestStep(_work.Digest, _work.OpaqueFaces * VoxelMath.IndicesPerFace);
            _work.Digest = VoxelMath.DigestStep(_work.Digest, _work.TransparentFaces * VoxelMath.IndicesPerFace);
            _work.Digest = VoxelMath.DigestStep(_work.Digest, _work.OpaqueStagedBytes);
            _work.Digest = VoxelMath.DigestStep(_work.Digest, _work.TransparentStagedBytes);
            _work.Digest = VoxelMath.DigestStep(_work.Digest, _work.EnabledStageBytes);
            return new ChunkResult(
                _work.Digest,
                _work.OpaqueFaces,
                _work.TransparentFaces,
                _work.OpaqueStagedBytes,
                _work.TransparentStagedBytes,
                _work.EnabledStageBytes,
                _work.EmptySections,
                _work.UniformSections,
                _work.ExpandedSections,
                _work.PackedSections,
                _work.MultiPackedSections,
                _work.TransparentMaskCount,
                _work.TransparentMaskWords,
                _work.DominantTransparentSections,
                _work.ResidualTransparentSections,
                _work.MaterializedOutput,
                GenerationMilliseconds,
                FaceDerivationMilliseconds,
                TransparentMaskMilliseconds,
                OpaquePackingMilliseconds,
                TransparentPackingMilliseconds);
        }

        internal static double ToMilliseconds(long ticks) =>
            ticks * 1000.0 / Stopwatch.Frequency;

        private void GenerateCells(scoped NativeLeaseView<VoxelCell> cells)
        {
            long cellBytes = checked((long)cells.Capacity * VoxelMath.VoxelCellBytes);
            _peakCoordinateStageBytes = Math.Max(_peakCoordinateStageBytes, cellBytes);
            Span<VoxelCell> cellSpan = cells.AsSpan();
            int seed = _options.Seed;
            int chunk = _chunk;
            long generationStart = _measureTimings ? Stopwatch.GetTimestamp() : 0;
            for (int cell = 0; cell < cellSpan.Length; cell++)
            {
                int x = cell % VoxelMath.ChunkDimension;
                int y = (cell / VoxelMath.ChunkDimension) % VoxelMath.ChunkDimension;
                int z = cell / (VoxelMath.ChunkDimension * VoxelMath.ChunkDimension);
                int blockId = VoxelMath.BlockIdForCell(seed, chunk, x, y, z);
                short density = VoxelMath.DensityForCell(seed, chunk, x, y, z, blockId);
                cellSpan[cell] = new VoxelCell
                {
                    BlockId = checked((ushort)blockId),
                    Density = density,
                    Section = VoxelMath.SectionIndex(x, y, z)
                };
                _work.Digest = VoxelMath.DigestStep(_work.Digest, x);
                _work.Digest = VoxelMath.DigestStep(_work.Digest, y);
                _work.Digest = VoxelMath.DigestStep(_work.Digest, z);
                _work.Digest = VoxelMath.DigestStep(_work.Digest, density);
                _work.Digest = VoxelMath.DigestStep(_work.Digest, blockId);
            }

            if (_measureTimings)
            {
                _generationTicks += Stopwatch.GetTimestamp() - generationStart;
            }

            long faceStart = _measureTimings ? Stopwatch.GetTimestamp() : 0;
            int opaqueRecordCount = 0;
            int transparentRecordCount = 0;
            int opaqueFaceCount = 0;
            int transparentFaceCount = 0;
            int opaqueBytes = 0;
            int transparentBytes = 0;
            for (int cell = 0; cell < cellSpan.Length; cell++)
            {
                int blockId = cellSpan[cell].BlockId;
                int mask = VoxelMath.FaceMaskFromCells(cell, cellSpan);
                cellSpan[cell].FaceMask = mask;
                bool transparent = VoxelMath.TransparentById[blockId];
                bool occupied = blockId != VoxelMath.AirBlockId;
                cellSpan[cell].OpaqueMask = occupied && !transparent ? mask : 0;
                cellSpan[cell].TransparentMask = occupied && transparent ? mask : 0;
                _work.Digest = VoxelMath.DigestStep(_work.Digest, mask);
                if (mask == 0)
                {
                    continue;
                }

                BlockTypeDescriptor type = VoxelMath.BlockTypeForId(blockId);
                int faceCount = VoxelMath.FaceCount(mask);
                int bytes = checked(faceCount * VoxelMath.StageBytesForType(type));

                if (occupied && !transparent)
                {
                    opaqueRecordCount++;
                    opaqueFaceCount += faceCount;
                    opaqueBytes = VoxelMath.AlignUp(opaqueBytes, type.Alignment);
                    opaqueBytes = checked(opaqueBytes + bytes);
                }
                else if (occupied && transparent)
                {
                    transparentRecordCount++;
                    transparentFaceCount += faceCount;
                    transparentBytes = VoxelMath.AlignUp(transparentBytes, type.Alignment);
                    transparentBytes = checked(transparentBytes + bytes);
                }
            }

            _work.OpaqueFaces = opaqueFaceCount;
            _work.TransparentFaces = transparentFaceCount;
            _work.OpaqueRecords = opaqueRecordCount;
            _work.TransparentRecords = transparentRecordCount;
            _work.OpaqueStagedBytes = opaqueBytes;
            _work.TransparentStagedBytes = transparentBytes;
            ProcessSections(cellSpan);
            if (_measureTimings)
            {
                _faceDerivationTicks += Stopwatch.GetTimestamp() - faceStart;
            }
        }

        private void PopulateFaceOutputs(
            scoped NativeLeaseView<VoxelCell> cells,
            scoped NativeLeaseView<NativeFaceOutput> faceOutput)
        {
            long cellBytes = checked((long)cells.Capacity * VoxelMath.VoxelCellBytes);
            long faceBytes = checked((long)faceOutput.Capacity
                * VoxelMath.NativeFaceOutputBytes);
            _peakFaceStageBytes = Math.Max(_peakFaceStageBytes, checked(cellBytes + faceBytes));
            Span<VoxelCell> cellSpan = cells.AsSpan();
            Span<NativeFaceOutput> outputSpan = faceOutput.AsSpan();
            int transparentOffset = _work.OpaqueRecords;
            int opaqueRecordCount = 0;
            int transparentRecordCount = 0;
            for (int cell = 0; cell < cellSpan.Length; cell++)
            {
                int mask = cellSpan[cell].FaceMask;
                if (mask == 0)
                {
                    continue;
                }

                NativeFaceOutput output = new()
                {
                    CellIndex = cell,
                    BlockId = cellSpan[cell].BlockId,
                    FaceMask = mask
                };
                BlockTypeDescriptor type = VoxelMath.BlockTypeForId(output.BlockId);
                output.PayloadBytes = type.PayloadBytes;
                output.Alignment = type.Alignment;
                output.StageMask = type.StageMask;
                output.StageBytes = VoxelMath.StageBytesForType(type);
                if (output.BlockId != VoxelMath.AirBlockId && !VoxelMath.TransparentById[output.BlockId])
                {
                    outputSpan[opaqueRecordCount++] = output;
                }
                else if (output.BlockId != VoxelMath.AirBlockId && VoxelMath.TransparentById[output.BlockId])
                {
                    outputSpan[transparentOffset + transparentRecordCount++] = output;
                }
            }

            if (opaqueRecordCount != _work.OpaqueRecords || transparentRecordCount != _work.TransparentRecords)
            {
                throw new InvalidDataException("Native face output population disagrees with native face classification.");
            }
        }

        private void ProcessSections(ReadOnlySpan<VoxelCell> cellSpan)
        {
            for (int section = 0; section < 64; section++)
            {
                SectionSummary summary = VoxelMath.ClassifySection(cellSpan, section);
                _sectionSummaries[section] = summary;
                switch (summary.Kind)
                {
                    case SectionRepresentationKind.Empty: _work.EmptySections++; break;
                    case SectionRepresentationKind.Uniform: _work.UniformSections++; break;
                    case SectionRepresentationKind.Expanded: _work.ExpandedSections++; break;
                    case SectionRepresentationKind.Packed: _work.PackedSections++; break;
                    case SectionRepresentationKind.MultiPacked: _work.MultiPackedSections++; break;
                }

                _work.TransparentMaskCount += summary.TransparentIds;
                if (summary.HasDominantTransparentId) _work.DominantTransparentSections++;
                if (summary.HasResidualTransparentIds) _work.ResidualTransparentSections++;
            }
        }

        private void ProcessMasks(
            scoped NativeLeaseView<VoxelCell> cells,
            scoped NativeLeaseView<ulong> view)
        {
            long maskStart = _measureTimings ? Stopwatch.GetTimestamp() : 0;
            ReadOnlySpan<VoxelCell> cellSpan = cells.AsSpan();
            Span<ulong> destination = view.AsSpan();
            int maskOffset = 0;
            for (int section = 0; section < 64; section++)
            {
                SectionSummary summary = _sectionSummaries[section];
                int expectedWords = checked(summary.TransparentIds * VoxelMath.TransparentMaskWordsPerId);
                int written = VoxelMath.BuildTransparentMasks(
                    cellSpan,
                    section,
                    destination.Slice(maskOffset, expectedWords));
                if (written != summary.TransparentIds)
                {
                    throw new InvalidDataException("Transparent mask classification and emission disagree in native storage.");
                }

                int words = checked(written * VoxelMath.TransparentMaskWordsPerId);
                for (int word = 0; word < words; word++)
                {
                    _work.Digest = VoxelMath.DigestStep(_work.Digest, unchecked((long)destination[maskOffset + word]));
                }

                maskOffset += words;
            }

            _work.TransparentMaskWords = maskOffset;
            if (_measureTimings)
            {
                _transparentMaskTicks += Stopwatch.GetTimestamp() - maskStart;
            }
        }

        private void PackBoth(
            scoped NativeLeaseView<NativeFaceOutput> faces,
            scoped NativeLeaseView<Vertex> vertices,
            scoped NativeLeaseView<int> indices,
            scoped NativeLeaseView<PayloadSlice> slices,
            scoped NativeLeaseView<byte> upload)
        {
            _peakPackingStageBytes = Math.Max(
                _peakPackingStageBytes,
                checked(
                    (long)faces.Capacity * VoxelMath.NativeFaceOutputBytes
                        + (long)vertices.Capacity * VoxelMath.VertexBytes
                        + (long)indices.Capacity * VoxelMath.IndexBytes
                        + (long)slices.Capacity * VoxelMath.PayloadSliceBytes
                        + upload.Capacity));

            Span<NativeFaceOutput> faceSpan = faces.AsSpan();
            Span<Vertex> vertexSpan = vertices.AsSpan();
            Span<int> indexSpan = indices.AsSpan();
            Span<PayloadSlice> sliceSpan = slices.AsSpan();
            Span<byte> uploadSpan = upload.AsSpan();
            int opaqueVertexCount = checked(_work.OpaqueFaces * VoxelMath.VerticesPerFace);
            int transparentVertexCount = checked(_work.TransparentFaces * VoxelMath.VerticesPerFace);
            int opaqueIndexCount = checked(_work.OpaqueFaces * VoxelMath.IndicesPerFace);
            int transparentIndexCount = checked(_work.TransparentFaces * VoxelMath.IndicesPerFace);
            long opaqueStart = _measureTimings ? Stopwatch.GetTimestamp() : 0;
            PackRange(
                opaque: true,
                faceSpan.Slice(0, _work.OpaqueRecords),
                vertexSpan.Slice(0, opaqueVertexCount),
                indexSpan.Slice(0, opaqueIndexCount),
                sliceSpan.Slice(0, _work.OpaqueFaces),
                uploadSpan.Slice(0, _work.OpaqueStagedBytes));
            if (_measureTimings)
            {
                _opaquePackingTicks += Stopwatch.GetTimestamp() - opaqueStart;
            }

            long transparentStart = _measureTimings ? Stopwatch.GetTimestamp() : 0;
            PackRange(
                opaque: false,
                faceSpan.Slice(_work.OpaqueRecords, _work.TransparentRecords),
                vertexSpan.Slice(opaqueVertexCount, transparentVertexCount),
                indexSpan.Slice(opaqueIndexCount, transparentIndexCount),
                sliceSpan.Slice(_work.OpaqueFaces, _work.TransparentFaces),
                uploadSpan.Slice(_work.OpaqueStagedBytes, _work.TransparentStagedBytes));
            if (_measureTimings)
            {
                _transparentPackingTicks += Stopwatch.GetTimestamp() - transparentStart;
            }

        }

        private void PackRange(
            bool opaque,
            ReadOnlySpan<NativeFaceOutput> faceSpan,
            Span<Vertex> vertexSpan,
            Span<int> indexSpan,
            Span<PayloadSlice> sliceSpan,
            Span<byte> uploadSpan)
        {
            int expectedFaces = opaque ? _work.OpaqueFaces : _work.TransparentFaces;
            int expectedBytes = opaque ? _work.OpaqueStagedBytes : _work.TransparentStagedBytes;
            int vertexCount = 0;
            int indexCount = 0;
            int sliceCount = 0;
            int offset = 0;
            int enabledStageBytes = 0;
            int seed = _options.Seed;
            for (int recordIndex = 0; recordIndex < faceSpan.Length; recordIndex++)
            {
                ref readonly NativeFaceOutput record = ref faceSpan[recordIndex];
                for (int face = 0; face < VoxelMath.FacesPerCell; face++)
                {
                    if ((record.FaceMask & (1 << face)) == 0)
                    {
                        continue;
                    }

                    int alignedOffset = VoxelMath.AlignUp(offset, record.Alignment);
                    uploadSpan.Slice(offset, alignedOffset - offset).Clear();
                    offset = alignedOffset;
                    int cursor = offset;
                    for (int slot = 0; slot < record.PayloadBytes; slot++)
                    {
                        bool enabled = (record.StageMask & (1 << (slot % 4))) != 0;
                        uploadSpan[cursor++] = enabled
                            ? checked((byte)((seed
                                + record.CellIndex * 11
                                + record.BlockId * 37
                                + slot * 17
                                + record.StageMask * 13) & 0xFF))
                            : (byte)0;
                        if (enabled) enabledStageBytes++;
                    }

                    int vertexOffset = vertexCount;
                    for (int vertex = 0; vertex < VoxelMath.VerticesPerFace; vertex++)
                    {
                        Vertex value = new(
                            VoxelMath.VertexValue(record.CellIndex, face, vertex, 0, record.BlockId),
                            VoxelMath.VertexValue(record.CellIndex, face, vertex, 1, record.BlockId),
                            VoxelMath.VertexValue(record.CellIndex, face, vertex, 2, record.BlockId),
                            face,
                            vertex,
                            record.BlockId);
                        vertexSpan[vertexCount++] = value;
                        WriteInt(uploadSpan, ref cursor, value.X);
                        WriteInt(uploadSpan, ref cursor, value.Y);
                        WriteInt(uploadSpan, ref cursor, value.Z);
                        WriteInt(uploadSpan, ref cursor, value.Face);
                        WriteInt(uploadSpan, ref cursor, value.Corner);
                        WriteInt(uploadSpan, ref cursor, value.BlockId);
                    }

                    for (int index = 0; index < VoxelMath.IndicesPerFace; index++)
                    {
                        int value = VoxelMath.IndexValue(vertexOffset, index);
                        indexSpan[indexCount++] = value;
                        WriteInt(uploadSpan, ref cursor, value);
                    }

                    int faceBytes = record.StageBytes;
                    int end = checked(offset + faceBytes);
                    while (cursor < end)
                    {
                        uploadSpan[cursor++] = 0;
                    }

                    sliceSpan[sliceCount++] = new PayloadSlice(
                        offset,
                        faceBytes,
                        record.Alignment,
                        record.StageMask,
                        record.BlockId,
                        record.CellIndex);
                    offset = end;
                }
            }

            if (sliceCount != expectedFaces || offset != expectedBytes)
            {
                throw new InvalidDataException("Native materialized output length disagrees with face classification.");
            }

            _work.EnabledStageBytes += enabledStageBytes;
            _work.Digest = VoxelMath.DigestMaterializedOutput(
                _work.Digest,
                vertexSpan.Slice(0, vertexCount),
                indexSpan.Slice(0, indexCount),
                sliceSpan.Slice(0, sliceCount),
                uploadSpan.Slice(0, offset));
            if ((_captureMeasuredFixture || _captureCompleteFixture) && _chunk == 0)
            {
                CaptureFixture(
                    opaque,
                    vertexSpan,
                    vertexCount,
                    indexSpan,
                    indexCount,
                    sliceSpan,
                    sliceCount,
                    uploadSpan,
                    offset);
            }

        }

        private void CaptureFixture(
            bool opaque,
            ReadOnlySpan<Vertex> vertices,
            int vertexCount,
            ReadOnlySpan<int> indices,
            int indexCount,
            ReadOnlySpan<PayloadSlice> slices,
            int sliceCount,
            ReadOnlySpan<byte> upload,
            int uploadLength)
        {
            OutputFixture empty = new([], [], [], [], [], [], [], []);
            OutputFixture current = _work.MaterializedOutput ?? empty;
            int vertexLimit = _captureCompleteFixture
                ? vertexCount
                : Math.Min(vertexCount, VoxelMath.OutputFixtureElementLimit * VoxelMath.VerticesPerFace);
            int indexLimit = _captureCompleteFixture
                ? indexCount
                : Math.Min(indexCount, VoxelMath.OutputFixtureElementLimit * VoxelMath.IndicesPerFace);
            int sliceLimit = _captureCompleteFixture
                ? sliceCount
                : Math.Min(sliceCount, VoxelMath.OutputFixtureElementLimit);
            int uploadLimit = _captureCompleteFixture
                ? uploadLength
                : Math.Min(uploadLength, VoxelMath.OutputFixtureByteLimit);
            OutputFixture update = opaque
                ? current with
                {
                    OpaqueVertices = vertices.Slice(0, vertexLimit).ToArray(),
                    OpaqueIndices = indices.Slice(0, indexLimit).ToArray(),
                    OpaqueSlices = slices.Slice(0, sliceLimit).ToArray(),
                    OpaqueUpload = upload.Slice(0, uploadLimit).ToArray()
                }
                : current with
                {
                    TransparentVertices = vertices.Slice(0, vertexLimit).ToArray(),
                    TransparentIndices = indices.Slice(0, indexLimit).ToArray(),
                    TransparentSlices = slices.Slice(0, sliceLimit).ToArray(),
                    TransparentUpload = upload.Slice(0, uploadLimit).ToArray()
                };
            _work.MaterializedOutput = update;
        }

        private static void WriteInt(Span<byte> destination, ref int offset, int value)
        {
            destination[offset++] = unchecked((byte)value);
            destination[offset++] = unchecked((byte)(value >> 8));
            destination[offset++] = unchecked((byte)(value >> 16));
            destination[offset++] = unchecked((byte)(value >> 24));
        }
    }

    private readonly record struct WorkerResult(PipelineResult Result);

    private static OutputFixture CreateIndependentFixture()
    {
        VoxelWorkloadOptions fixtureOptions = new(
            VoxelMath.IndependentFixtureSeed,
            ChunkCount: 1,
            WorkerCount: 1,
            Iterations: 1);
        (int opaqueFaces, int opaqueBytes) = MeasureRecords(VoxelMath.IndependentOpaqueRecords);
        (int transparentFaces, int transparentBytes) = MeasureRecords(VoxelMath.IndependentTransparentRecords);
        WorkerContext context = new(fixtureOptions, captureMeasuredFixture: true);
        context.BeginIndependentFixture(
            VoxelMath.IndependentOpaqueRecords.Length,
            opaqueFaces,
            opaqueBytes,
            VoxelMath.IndependentTransparentRecords.Length,
            transparentFaces,
            transparentBytes);

        using NativePool<NativeFaceOutput> facePool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<Vertex> vertexPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<int> indexPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena heterogeneousArena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        int totalRecords = checked(VoxelMath.IndependentOpaqueRecords.Length
            + VoxelMath.IndependentTransparentRecords.Length);
        int totalVertices = checked((opaqueFaces + transparentFaces) * VoxelMath.VerticesPerFace);
        int totalIndices = checked((opaqueFaces + transparentFaces) * VoxelMath.IndicesPerFace);
        int totalSlices = checked(opaqueFaces + transparentFaces);
        int totalUpload = checked(opaqueBytes + transparentBytes);
        using Pooled<NativeFaceOutput> face = facePool.Rent(Math.Max(1, totalRecords));
        using Pooled<Vertex> vertices = vertexPool.Rent(Math.Max(1, totalVertices));
        using Pooled<int> indices = indexPool.Rent(Math.Max(1, totalIndices));
        ArenaLease<PayloadSlice> slices = heterogeneousArena.Scratch<PayloadSlice>(Math.Max(1, totalSlices));
        ArenaLease<byte> upload = heterogeneousArena.Scratch<byte>(Math.Max(1, totalUpload));

        face.Access(view =>
        {
            Span<NativeFaceOutput> destination = view.AsSpan();
            int offset = VoxelMath.IndependentOpaqueRecords.Length;
            for (int index = 0; index < VoxelMath.IndependentOpaqueRecords.Length; index++)
            {
                FaceRecord record = VoxelMath.IndependentOpaqueRecords[index];
                BlockTypeDescriptor type = VoxelMath.BlockTypeForId(record.BlockId);
                destination[index] = new NativeFaceOutput
                {
                    CellIndex = record.CellIndex,
                    BlockId = record.BlockId,
                    FaceMask = record.Mask,
                    PayloadBytes = type.PayloadBytes,
                    Alignment = type.Alignment,
                    StageMask = type.StageMask,
                    StageBytes = VoxelMath.StageBytesForType(type)
                };
            }

            for (int index = 0; index < VoxelMath.IndependentTransparentRecords.Length; index++)
            {
                FaceRecord record = VoxelMath.IndependentTransparentRecords[index];
                BlockTypeDescriptor type = VoxelMath.BlockTypeForId(record.BlockId);
                destination[offset + index] = new NativeFaceOutput
                {
                    CellIndex = record.CellIndex,
                    BlockId = record.BlockId,
                    FaceMask = record.Mask,
                    PayloadBytes = type.PayloadBytes,
                    Alignment = type.Alignment,
                    StageMask = type.StageMask,
                    StageBytes = VoxelMath.StageBytesForType(type)
                };
            }
        });
        NativeLeaseOperations.Access(face, vertices, indices, slices, upload, context.MeshAction);

        return context.FinishChunk().MaterializedOutput
            ?? throw new InvalidDataException("The direct native fixture did not capture complete output.");
    }

    private static (int FaceCount, int StagedBytes) MeasureRecords(ReadOnlySpan<FaceRecord> records)
    {
        int faceCount = 0;
        int stagedBytes = 0;
        for (int index = 0; index < records.Length; index++)
        {
            FaceRecord record = records[index];
            BlockTypeDescriptor type = VoxelMath.BlockTypeForId(record.BlockId);
            for (int face = 0; face < VoxelMath.FacesPerCell; face++)
            {
                if ((record.Mask & (1 << face)) == 0)
                {
                    continue;
                }

                faceCount++;
                stagedBytes = VoxelMath.AlignUp(stagedBytes, type.Alignment);
                stagedBytes = checked(stagedBytes + VoxelMath.StageBytesForType(type));
            }
        }

        return (faceCount, stagedBytes);
    }

    public static PipelineResult Run(
        VoxelWorkloadOptions options,
        bool captureMeasuredFixture)
    {
        VoxelMath.ValidateBoundaryFixture();
        NativeMemoryStatistics baseline = NativeMemoryDiagnostics.Snapshot();
        if (baseline.OutstandingNativeBytes != 0)
        {
            throw new InvalidOperationException(
                $"NAM benchmark must start at a zero native baseline; observed {baseline.OutstandingNativeBytes} bytes.");
        }

        OutputFixture independentFixture = CreateIndependentFixture();
        NativeMemoryStatistics measuredBaseline = NativeMemoryDiagnostics.Snapshot();
        WorkerResult[] workers = new WorkerResult[options.WorkerCount];
        Task[] tasks = new Task[options.WorkerCount];
        using CountdownEvent ready = new(options.WorkerCount);
        using ManualResetEventSlim start = new(false);
        long allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        for (int worker = 0; worker < options.WorkerCount; worker++)
        {
            int workerId = worker;
            tasks[worker] = Task.Run(() => workers[workerId] = RunWorker(
                options,
                workerId,
                ready,
                start,
                captureMeasuredFixture));
        }

        ready.Wait();
        allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        gen0Before = GC.CollectionCount(0);
        gen1Before = GC.CollectionCount(1);
        gen2Before = GC.CollectionCount(2);
        Stopwatch stopwatch = Stopwatch.StartNew();
        start.Set();
        Task.WaitAll(tasks);
        stopwatch.Stop();

        NativeMemoryStatistics final = NativeMemoryDiagnostics.Snapshot();
        long measuredPeakNative = final.PeakOutstandingNativeBytes;
        long reusedNativeSegments = Math.Max(
            0,
            final.ReusedNativeSegmentCount - measuredBaseline.ReusedNativeSegmentCount);
        long reclaimedRangeReuseCount = Math.Max(
            0,
            final.ReclaimedRangeReuseCount - measuredBaseline.ReclaimedRangeReuseCount);
        long reclaimedRangeReuseBytes = Math.Max(
            0,
            final.ReclaimedRangeReuseBytes - measuredBaseline.ReclaimedRangeReuseBytes);
        long digest = 17;
        long chunks = 0;
        long visibleFaces = 0;
        long vertices = 0;
        long indices = 0;
        long stagedBytes = 0;
        long peakNative = measuredPeakNative;
        long peakRetained = measuredPeakNative;
        long peakCoordinateStage = 0;
        long peakFaceStage = 0;
        long peakPackingStage = 0;
        double generationMilliseconds = 0;
        double faceDerivationMilliseconds = 0;
        double transparentMaskMilliseconds = 0;
        double opaquePackingMilliseconds = 0;
        double transparentPackingMilliseconds = 0;
        double coordinateRecycleMilliseconds = 0;
        double faceRecycleMilliseconds = 0;
        double maskRecycleMilliseconds = 0;
        double packingRecycleMilliseconds = 0;
        long rents = 0;
        long recycles = 0;
        long measuredLeases = 0;
        long cleared = 0;
        long empty = 0;
        long uniform = 0;
        long expanded = 0;
        long packed = 0;
        long multiPacked = 0;
        long masks = 0;
        long maskWords = 0;
        long dominant = 0;
        long residual = 0;
        long opaqueFaces = 0;
        long transparentFaces = 0;
        long opaqueStaged = 0;
        long transparentStaged = 0;
        long enabledStageBytes = 0;
        OutputFixture? materializedOutput = null;
        for (int worker = 0; worker < workers.Length; worker++)
        {
            PipelineResult result = workers[worker].Result;
            digest = VoxelMath.DigestStep(digest, result.Digest);
            chunks += result.Chunks;
            visibleFaces += result.VisibleFaces;
            vertices += result.Vertices;
            indices += result.Indices;
            stagedBytes += result.StagedBytes;
            peakNative = Math.Max(peakNative, result.PeakNativeBackingBytes);
            peakRetained = Math.Max(peakRetained, result.PeakRetainedNativeBackingBytes);
            peakCoordinateStage = Math.Max(peakCoordinateStage, result.PeakCoordinateStageBytes);
            peakFaceStage = Math.Max(peakFaceStage, result.PeakFaceStageBytes);
            peakPackingStage = Math.Max(peakPackingStage, result.PeakPackingStageBytes);
            rents += result.RentCount;
            recycles += result.ScopedRecycleCount;
            measuredLeases += result.MeasuredLeaseCount;
            cleared += result.ClearedBytes;
            empty += result.EmptySections;
            uniform += result.UniformSections;
            expanded += result.ExpandedSections;
            packed += result.PackedSections;
            multiPacked += result.MultiPackedSections;
            masks += result.TransparentMaskCount;
            maskWords += result.TransparentMaskWords;
            dominant += result.DominantTransparentSections;
            residual += result.ResidualTransparentSections;
            opaqueFaces += result.OpaqueVisibleFaces;
            transparentFaces += result.TransparentVisibleFaces;
            opaqueStaged += result.OpaqueStagedBytes;
            transparentStaged += result.TransparentStagedBytes;
            enabledStageBytes += result.EnabledStageBytes;
            generationMilliseconds += result.GenerationMilliseconds;
            faceDerivationMilliseconds += result.FaceDerivationMilliseconds;
            transparentMaskMilliseconds += result.TransparentMaskMilliseconds;
            opaquePackingMilliseconds += result.OpaquePackingMilliseconds;
            transparentPackingMilliseconds += result.TransparentPackingMilliseconds;
            coordinateRecycleMilliseconds += result.CoordinateRecycleMilliseconds;
            faceRecycleMilliseconds += result.FaceRecycleMilliseconds;
            maskRecycleMilliseconds += result.MaskRecycleMilliseconds;
            packingRecycleMilliseconds += result.PackingRecycleMilliseconds;
            materializedOutput ??= result.MaterializedOutput;
        }

        return new PipelineResult(
            "NAM",
            digest,
            checked((int)chunks),
            visibleFaces,
            vertices,
            indices,
            stagedBytes,
            0,
            0,
            peakNative,
            peakRetained,
            final.OutstandingNativeBytes - baseline.OutstandingNativeBytes,
            peakCoordinateStage,
            peakFaceStage,
            peakPackingStage,
            rents,
            recycles,
            cleared,
            empty,
            uniform,
            expanded,
            packed,
            multiPacked,
            masks,
            dominant,
            residual,
            opaqueFaces,
            transparentFaces,
            opaqueFaces * VoxelMath.VerticesPerFace,
            transparentFaces * VoxelMath.VerticesPerFace,
            opaqueFaces * VoxelMath.IndicesPerFace,
            transparentFaces * VoxelMath.IndicesPerFace,
            opaqueStaged,
            transparentStaged,
            enabledStageBytes,
            0,
            maskWords,
            measuredLeases,
            reusedNativeSegments,
            stopwatch.Elapsed.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocationBefore,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
             GC.CollectionCount(2) - gen2Before,
             materializedOutput,
             IndependentFixture: independentFixture,
             ReclaimedRangeReuseCount: reclaimedRangeReuseCount,
             ReclaimedRangeReuseBytes: reclaimedRangeReuseBytes,
             GenerationMilliseconds: generationMilliseconds,
             FaceDerivationMilliseconds: faceDerivationMilliseconds,
             TransparentMaskMilliseconds: transparentMaskMilliseconds,
             OpaquePackingMilliseconds: opaquePackingMilliseconds,
             TransparentPackingMilliseconds: transparentPackingMilliseconds,
             CoordinateRecycleMilliseconds: coordinateRecycleMilliseconds,
             FaceRecycleMilliseconds: faceRecycleMilliseconds,
             MaskRecycleMilliseconds: maskRecycleMilliseconds,
             PackingRecycleMilliseconds: packingRecycleMilliseconds);
    }

    private static WorkerResult RunWorker(
        VoxelWorkloadOptions options,
        int workerId,
        CountdownEvent ready,
        ManualResetEventSlim start,
        bool captureMeasuredFixture)
    {
        using NativePool<VoxelCell> cellPool = new(
            initialCapacity: VoxelMath.CellsPerChunk,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<NativeFaceOutput> facePool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<Vertex> vertexPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<int> indexPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena heterogeneousArena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        WorkerContext context = new(options, captureMeasuredFixture);
        int totalChunks = checked(options.ChunkCount * options.Iterations);
        int passes = 2;
        long measuredChunks = 0;
        long digest = 17;
        long visibleFaces = 0;
        long vertices = 0;
        long indices = 0;
        long stagedBytes = 0;
        long empty = 0;
        long uniform = 0;
        long expanded = 0;
        long packed = 0;
        long multiPacked = 0;
        long masks = 0;
        long maskWords = 0;
        long dominant = 0;
        long residual = 0;
        long opaqueFaces = 0;
        long transparentFaces = 0;
        long opaqueStaged = 0;
        long transparentStaged = 0;
        long enabledStageBytes = 0;
        long peakNative = 0;
        long peakRetained = 0;
        long peakCoordinateStage = 0;
        long peakFaceStage = 0;
        long peakPackingStage = 0;
        double generationMilliseconds = 0;
        double faceDerivationMilliseconds = 0;
        double transparentMaskMilliseconds = 0;
        double opaquePackingMilliseconds = 0;
        double transparentPackingMilliseconds = 0;
        double coordinateRecycleMilliseconds = 0;
        double faceRecycleMilliseconds = 0;
        double maskRecycleMilliseconds = 0;
        double packingRecycleMilliseconds = 0;
        long rents = 0;
        long recycles = 0;
        long cleared = 0;
        OutputFixture? materializedOutput = null;
        for (int pass = 0; pass < passes; pass++)
        {
            int count = pass == 0
                ? VoxelWorkloadOptions.WarmupChunksPerWorker
                : totalChunks <= workerId
                    ? 0
                    : checked((totalChunks - 1 - workerId) / options.WorkerCount + 1);
            for (int iteration = 0; iteration < count; iteration++)
            {
                int chunk = pass == 0
                    ? checked(totalChunks + workerId + iteration)
                    : checked(workerId + iteration * options.WorkerCount);
                context.BeginChunk(chunk, pass == 0 ? 17 : digest, measureTimings: pass != 0);
                int opaqueRecordCapacity = 0;
                int transparentRecordCapacity = 0;
                try
                {
                    scoped Pooled<VoxelCell> cells = cellPool.LeaseScoped(VoxelMath.CellsPerChunk);
                    rents++;
                    cells.Access(context.GenerationAction);
                    opaqueRecordCapacity = Math.Max(1, context.OpaqueRecordCount);
                    transparentRecordCapacity = Math.Max(1, context.TransparentRecordCount);
                    scoped Pooled<NativeFaceOutput> faceOutputs = facePool.LeaseScoped(
                        checked(opaqueRecordCapacity + transparentRecordCapacity));
                    rents++;
                    NativeLeaseOperations.Access(cells, faceOutputs, context.FaceAction);
                    int maskLength = checked(Math.Max(
                        1,
                        context.TransparentMaskCountForTest * VoxelMath.TransparentMaskWordsPerId));
                    try
                    {
                        {
                            scoped ArenaLease<ulong> nativeMasks = heterogeneousArena.ScratchScoped<ulong>(maskLength);
                            rents++;
                            NativeLeaseOperations.Access(cells, nativeMasks, context.MaskAction);
                        }
                    }
                    finally
                    {
                        long recycleStart = Stopwatch.GetTimestamp();
                        heterogeneousArena.RecycleScoped();
                        context.AddMaskRecycleTicks(Stopwatch.GetTimestamp() - recycleStart);
                        recycles++;
                        cleared += checked((long)maskLength * sizeof(ulong));
                    }

                                    int opaqueVertexLength = checked(context.OpaqueFaceCount * VoxelMath.VerticesPerFace);
                                    int transparentVertexLength = checked(context.TransparentFaceCount * VoxelMath.VerticesPerFace);
                                    int opaqueIndexLength = checked(context.OpaqueFaceCount * VoxelMath.IndicesPerFace);
                                    int transparentIndexLength = checked(context.TransparentFaceCount * VoxelMath.IndicesPerFace);
                                    int totalVertexLength = Math.Max(1, checked(opaqueVertexLength + transparentVertexLength));
                                    int totalIndexLength = Math.Max(1, checked(opaqueIndexLength + transparentIndexLength));
                                    int totalFaceLength = Math.Max(1, checked(context.OpaqueFaceCount + context.TransparentFaceCount));
                                    int totalStageLength = Math.Max(1, checked(context.OpaqueStageBytes + context.TransparentStageBytes));
                                    try
                                    {
                                        {
                                            scoped Pooled<Vertex> vertexOutput = vertexPool.LeaseScoped(totalVertexLength);
                                            scoped Pooled<int> indexOutput = indexPool.LeaseScoped(totalIndexLength);
                                            scoped ArenaLease<PayloadSlice> slices = heterogeneousArena.ScratchScoped<PayloadSlice>(totalFaceLength);
                                            scoped ArenaLease<byte> upload = heterogeneousArena.ScratchScoped<byte>(totalStageLength);
                                            rents += 4;
                                            NativeLeaseOperations.Access(
                                                faceOutputs,
                                                vertexOutput,
                                                indexOutput,
                                                slices,
                                                upload,
                                                context.MeshAction);
                                        }
                                    }
                                    finally
                                    {
                                        long recycleStart = Stopwatch.GetTimestamp();
                                        heterogeneousArena.RecycleScoped();
                                        vertexPool.RecycleScoped();
                                        indexPool.RecycleScoped();
                                        context.AddPackingRecycleTicks(Stopwatch.GetTimestamp() - recycleStart);
                                        recycles++;
                                        cleared += checked((long)totalFaceLength * VoxelMath.PayloadSliceBytes);
                                        cleared += totalStageLength;
                                        recycles += 2;
                                        cleared += checked((long)totalVertexLength * VoxelMath.VertexBytes);
                                        cleared += checked((long)totalIndexLength * VoxelMath.IndexBytes);
                                    }

                    }
                finally
                {
                    long recycleStart = Stopwatch.GetTimestamp();
                    cellPool.RecycleScoped();
                    context.AddCoordinateRecycleTicks(Stopwatch.GetTimestamp() - recycleStart);
                    recycles++;
                    cleared += checked((long)VoxelMath.CellsPerChunk * VoxelMath.VoxelCellBytes);
                    recycleStart = Stopwatch.GetTimestamp();
                    facePool.RecycleScoped();
                    context.AddFaceRecycleTicks(Stopwatch.GetTimestamp() - recycleStart);
                    recycles++;
                    cleared += checked((long)(opaqueRecordCapacity + transparentRecordCapacity) * VoxelMath.NativeFaceOutputBytes);
                }

                ChunkResult result = context.FinishChunk();
                peakCoordinateStage = Math.Max(peakCoordinateStage, context.PeakCoordinateStageBytes);
                peakFaceStage = Math.Max(peakFaceStage, context.PeakFaceStageBytes);
                peakPackingStage = Math.Max(peakPackingStage, context.PeakPackingStageBytes);
                if (pass != 0)
                {
                    digest = result.Digest;
                    measuredChunks++;
                    visibleFaces += result.VisibleFaces;
                    vertices += result.Vertices;
                    indices += result.Indices;
                    stagedBytes += result.StagedBytes;
                    empty += result.EmptySections;
                    uniform += result.UniformSections;
                    expanded += result.ExpandedSections;
                    packed += result.PackedSections;
                    multiPacked += result.MultiPackedSections;
                    masks += result.TransparentMaskCount;
                    maskWords += result.TransparentMaskWords;
                    dominant += result.DominantTransparentSections;
                    residual += result.ResidualTransparentSections;
                    opaqueFaces += result.OpaqueFaces;
                    transparentFaces += result.TransparentFaces;
                    opaqueStaged += result.OpaqueStagedBytes;
                    transparentStaged += result.TransparentStagedBytes;
                    enabledStageBytes += result.EnabledStageBytes;
                    generationMilliseconds += result.GenerationMilliseconds;
                    faceDerivationMilliseconds += result.FaceDerivationMilliseconds;
                    transparentMaskMilliseconds += result.TransparentMaskMilliseconds;
                    opaquePackingMilliseconds += result.OpaquePackingMilliseconds;
                    transparentPackingMilliseconds += result.TransparentPackingMilliseconds;
                    coordinateRecycleMilliseconds += context.CoordinateRecycleMilliseconds;
                    faceRecycleMilliseconds += context.FaceRecycleMilliseconds;
                    maskRecycleMilliseconds += context.MaskRecycleMilliseconds;
                    packingRecycleMilliseconds += context.PackingRecycleMilliseconds;
                    materializedOutput ??= result.MaterializedOutput;
                }
            }

            if (pass == 0)
            {
                rents = 0;
                recycles = 0;
                cleared = 0;
                peakNative = 0;
                peakRetained = 0;
                peakCoordinateStage = 0;
                peakFaceStage = 0;
                peakPackingStage = 0;
                ready.Signal();
                start.Wait();
            }
        }

        return new WorkerResult(new PipelineResult(
            "NAM",
            digest,
            checked((int)measuredChunks),
            visibleFaces,
            vertices,
            indices,
            stagedBytes,
            0,
            0,
            peakNative,
            peakRetained,
            0,
            peakCoordinateStage,
            peakFaceStage,
            peakPackingStage,
            rents,
            recycles,
            cleared,
            empty,
            uniform,
            expanded,
            packed,
            multiPacked,
            masks,
            dominant,
            residual,
            opaqueFaces,
            transparentFaces,
            opaqueFaces * VoxelMath.VerticesPerFace,
            transparentFaces * VoxelMath.VerticesPerFace,
            opaqueFaces * VoxelMath.IndicesPerFace,
            transparentFaces * VoxelMath.IndicesPerFace,
            opaqueStaged,
            transparentStaged,
            enabledStageBytes,
            0,
             maskWords,
             rents,
             0,
             MaterializedOutput: materializedOutput,
             GenerationMilliseconds: generationMilliseconds,
             FaceDerivationMilliseconds: faceDerivationMilliseconds,
             TransparentMaskMilliseconds: transparentMaskMilliseconds,
             OpaquePackingMilliseconds: opaquePackingMilliseconds,
             TransparentPackingMilliseconds: transparentPackingMilliseconds,
             CoordinateRecycleMilliseconds: coordinateRecycleMilliseconds,
             FaceRecycleMilliseconds: faceRecycleMilliseconds,
             MaskRecycleMilliseconds: maskRecycleMilliseconds,
             PackingRecycleMilliseconds: packingRecycleMilliseconds));
    }

}
