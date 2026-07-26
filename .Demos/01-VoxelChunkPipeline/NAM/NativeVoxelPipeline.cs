using System.Diagnostics;
using Supprocom.NativeAllocationManagement;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.NAM;

internal static class NativeVoxelPipeline
{
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
        internal long OutputByteHash;
        internal string StrongOutputHash;
        internal CanonicalHashAccumulator? InputHash;
        internal CanonicalInputCell[]? InputCells;
        internal OutputFixture? MaterializedOutput;
    }

    private sealed class OwnerAccumulator
    {
        private readonly string _name;
        private NativeOwnerStatistics _baseline;
        private long _peakRequestedBytes;
        private long _peakPhysicalBytes;
        private long _retainedPhysicalBytes;
        private long _retiredPhysicalBytes;
        private long _peakGeometricSlackBytes;
        private int _peakSegmentCount;
        private int _retainedSegmentCount;

        internal OwnerAccumulator(string name, NativeOwnerStatistics baseline)
        {
            _name = name;
            ResetBaseline(baseline);
        }

        internal void ResetBaseline(NativeOwnerStatistics baseline)
        {
            _baseline = baseline;
            _peakRequestedBytes = 0;
            _peakPhysicalBytes = 0;
            _retainedPhysicalBytes = baseline.RetainedBytes;
            _retiredPhysicalBytes = baseline.RetiredBytes;
            _peakGeometricSlackBytes = 0;
            _peakSegmentCount = baseline.SegmentCount;
            _retainedSegmentCount = baseline.SegmentCount;
        }

        internal void Observe(NativeOwnerStatistics statistics)
        {
            _peakRequestedBytes = Math.Max(_peakRequestedBytes, statistics.RequestedBytes);
            _peakPhysicalBytes = Math.Max(_peakPhysicalBytes, statistics.RetainedBytes);
            _retainedPhysicalBytes = statistics.RetainedBytes;
            _retiredPhysicalBytes = statistics.RetiredBytes;
            if (statistics.RequestedBytes > 0)
            {
                _peakGeometricSlackBytes = Math.Max(
                    _peakGeometricSlackBytes,
                    Math.Max(0, statistics.RetainedBytes - statistics.RequestedBytes));
            }
            _peakSegmentCount = Math.Max(_peakSegmentCount, statistics.SegmentCount);
            _retainedSegmentCount = statistics.SegmentCount;
        }

        internal NativeOwnerProfile ToProfile(NativeOwnerStatistics finalStatistics)
        {
            Observe(finalStatistics);
            return new NativeOwnerProfile(
                _name,
                finalStatistics.RequestedBytes,
                _peakRequestedBytes,
                _peakPhysicalBytes,
                _retainedPhysicalBytes,
                _retiredPhysicalBytes,
                _peakGeometricSlackBytes,
                _peakSegmentCount,
                _retainedSegmentCount,
                Math.Max(0, finalStatistics.TrimmedBytes - _baseline.TrimmedBytes),
                Math.Max(0, finalStatistics.TrimCallCount - _baseline.TrimCallCount),
                Math.Max(0, finalStatistics.FreshSegmentAllocationCount - _baseline.FreshSegmentAllocationCount));
        }
    }

    private sealed class WorkerContext
    {
        private readonly VoxelWorkloadOptions _options;
        private readonly bool _captureMeasuredFixture;
        private readonly SectionSummary[] _sectionSummaries = new SectionSummary[64];
        private Work _work;
        private int _chunk;
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

        internal NativeLeasePairAction<VoxelCell, FaceRecord> FaceAction { get; }

        internal NativeLeasePooledArenaAction<VoxelCell, ulong> MaskAction { get; }

        internal NativeLeaseQuintupleAction<FaceRecord, Vertex, int, PayloadSlice, byte> MeshAction { get; }

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
            _work = new Work
            {
                Digest = digest,
                OutputByteHash = 17,
                StrongOutputHash = string.Empty,
                InputHash = new CanonicalHashAccumulator(),
                InputCells = _captureMeasuredFixture
                    ? new CanonicalInputCell[VoxelMath.CellsPerChunk]
                    : null
            };
            _work.InputHash.AddString("voxel-input-chunk-v1");
            _work.InputHash.AddInt32(_options.Seed);
            _work.InputHash.AddInt32(chunk);
            _work.InputHash.AddInt32(VoxelMath.CellsPerChunk);
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
            string inputHash = _work.InputHash?.Complete() ?? string.Empty;
            _work.InputHash?.Dispose();
            return new ChunkResult(
                _work.Digest,
                _work.OpaqueFaces,
                _work.TransparentFaces,
                _work.OpaqueFaces * VoxelMath.VerticesPerFace,
                _work.TransparentFaces * VoxelMath.VerticesPerFace,
                _work.OpaqueFaces * VoxelMath.IndicesPerFace,
                _work.TransparentFaces * VoxelMath.IndicesPerFace,
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
                TransparentPackingMilliseconds,
                OutputByteHash: _work.OutputByteHash,
                ChunkId: _chunk,
                StrongOutputHash: _work.StrongOutputHash,
                StrongInputHash: inputHash,
                InputCells: _work.InputCells);
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
                CanonicalInputCell observed = new(
                    chunk,
                    cell,
                    x,
                    y,
                    z,
                    VoxelMath.SectionIndex(x, y, z),
                    checked((ushort)blockId),
                    density);
                _work.InputCells?[cell] = observed;
                _work.InputHash!.AddCanonicalInputCell(observed);
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
            scoped NativeLeaseView<FaceRecord> faceOutput)
        {
            long cellBytes = checked((long)cells.Capacity * VoxelMath.VoxelCellBytes);
            long faceBytes = checked((long)faceOutput.Capacity
                * VoxelMath.FaceRecordBytes);
            _peakFaceStageBytes = Math.Max(_peakFaceStageBytes, checked(cellBytes + faceBytes));
            Span<VoxelCell> cellSpan = cells.AsSpan();
            Span<FaceRecord> outputSpan = faceOutput.AsSpan().Slice(
                0,
                checked(_work.OpaqueRecords + _work.TransparentRecords));
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

                BlockTypeDescriptor type = VoxelMath.BlockTypeForId(cellSpan[cell].BlockId);
                FaceRecord output = new(
                    cell,
                    cellSpan[cell].BlockId,
                    mask,
                    type.PayloadBytes,
                    type.Alignment,
                    type.StageMask,
                    VoxelMath.StageBytesForType(type));
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
            scoped NativeLeaseView<FaceRecord> faces,
            scoped NativeLeaseView<Vertex> vertices,
            scoped NativeLeaseView<int> indices,
            scoped NativeLeaseView<PayloadSlice> slices,
            scoped NativeLeaseView<byte> upload)
        {
            _peakPackingStageBytes = Math.Max(
                _peakPackingStageBytes,
                checked(
                    (long)faces.Capacity * VoxelMath.FaceRecordBytes
                        + (long)vertices.Capacity * VoxelMath.VertexBytes
                        + (long)indices.Capacity * VoxelMath.IndexBytes
                        + (long)slices.Capacity * VoxelMath.PayloadSliceBytes
                        + upload.Capacity));

            Span<FaceRecord> faceSpan = faces.AsSpan().Slice(
                0,
                checked(_work.OpaqueRecords + _work.TransparentRecords));
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

            _work.StrongOutputHash = VoxelMath.ComputeStrongOutputHash(
                vertexSpan.Slice(0, opaqueVertexCount),
                indexSpan.Slice(0, opaqueIndexCount),
                sliceSpan.Slice(0, _work.OpaqueFaces),
                uploadSpan.Slice(0, _work.OpaqueStagedBytes),
                vertexSpan.Slice(opaqueVertexCount, transparentVertexCount),
                indexSpan.Slice(opaqueIndexCount, transparentIndexCount),
                sliceSpan.Slice(_work.OpaqueFaces, _work.TransparentFaces),
                uploadSpan.Slice(_work.OpaqueStagedBytes, _work.TransparentStagedBytes));

        }

        private void PackRange(
            bool opaque,
            ReadOnlySpan<FaceRecord> faceSpan,
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
                ref readonly FaceRecord record = ref faceSpan[recordIndex];
                for (int face = 0; face < VoxelMath.FacesPerCell; face++)
                {
                    if ((record.Mask & (1 << face)) == 0)
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
            _work.OutputByteHash = VoxelMath.DigestMaterializedOutput(
                _work.OutputByteHash,
                vertexSpan.Slice(0, vertexCount),
                indexSpan.Slice(0, indexCount),
                sliceSpan.Slice(0, sliceCount),
                uploadSpan.Slice(0, offset));
            if (_captureMeasuredFixture && _chunk == 0)
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
            OutputFixture update = opaque
                ? current with
                {
                    OpaqueVertices = vertices.Slice(0, vertexCount).ToArray(),
                    OpaqueIndices = indices.Slice(0, indexCount).ToArray(),
                    OpaqueSlices = slices.Slice(0, sliceCount).ToArray(),
                    OpaqueUpload = upload.Slice(0, uploadLength).ToArray()
                }
                : current with
                {
                    TransparentVertices = vertices.Slice(0, vertexCount).ToArray(),
                    TransparentIndices = indices.Slice(0, indexCount).ToArray(),
                    TransparentSlices = slices.Slice(0, sliceCount).ToArray(),
                    TransparentUpload = upload.Slice(0, uploadLength).ToArray()
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

        using NativePool<FaceRecord> facePool = new(
            initialCapacity: VoxelMath.CellsPerChunk,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<Vertex> vertexPool = new(
            initialCapacity: checked(VoxelMath.CellsPerChunk * 6),
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<int> indexPool = new(
            initialCapacity: checked(VoxelMath.CellsPerChunk * 9),
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena heterogeneousArena = new(
            preAllocateBytes: (nuint)(64 * 1024 * 1024),
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        int totalRecords = checked(VoxelMath.IndependentOpaqueRecords.Length
            + VoxelMath.IndependentTransparentRecords.Length);
        int totalVertices = checked((opaqueFaces + transparentFaces) * VoxelMath.VerticesPerFace);
        int totalIndices = checked((opaqueFaces + transparentFaces) * VoxelMath.IndicesPerFace);
        int totalSlices = checked(opaqueFaces + transparentFaces);
        int totalUpload = checked(opaqueBytes + transparentBytes);
        using Pooled<FaceRecord> face = facePool.Rent(Math.Max(1, totalRecords));
        using Pooled<Vertex> vertices = vertexPool.Rent(Math.Max(1, totalVertices));
        using Pooled<int> indices = indexPool.Rent(Math.Max(1, totalIndices));
        ArenaLease<PayloadSlice> slices = heterogeneousArena.Scratch<PayloadSlice>(Math.Max(1, totalSlices));
        ArenaLease<byte> upload = heterogeneousArena.Scratch<byte>(Math.Max(1, totalUpload));

        face.Access(view =>
        {
            Span<FaceRecord> destination = view.AsSpan();
            int offset = VoxelMath.IndependentOpaqueRecords.Length;
            for (int index = 0; index < VoxelMath.IndependentOpaqueRecords.Length; index++)
            {
                FaceRecord record = VoxelMath.IndependentOpaqueRecords[index];
                BlockTypeDescriptor type = VoxelMath.BlockTypeForId(record.BlockId);
                destination[index] = new FaceRecord
                {
                    CellIndex = record.CellIndex,
                    BlockId = record.BlockId,
                    Mask = record.Mask,
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
                destination[offset + index] = new FaceRecord
                {
                    CellIndex = record.CellIndex,
                    BlockId = record.BlockId,
                    Mask = record.Mask,
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
        bool captureMeasuredFixture,
        bool includeCanonicalInputCells = false)
    {
        Stopwatch endToEnd = Stopwatch.StartNew();
        VoxelMath.ValidateCanonicalInputFixture();
        VoxelMath.ValidateHandAuthoredInputFixture();
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
                captureMeasuredFixture || includeCanonicalInputCells));
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
        long outputHash = 17;
        List<ChunkOutputSummary> chunkOutputs = [];
        List<NativeOwnerProfile> ownerProfiles = [];
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
            if (result.NativeOwnerProfiles is not null)
            {
                ownerProfiles.AddRange(result.NativeOwnerProfiles);
            }
            if (result.ChunkOutputs is not null)
            {
                chunkOutputs.AddRange(result.ChunkOutputs);
            }
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

        chunkOutputs.Sort(static (left, right) => left.ChunkId.CompareTo(right.ChunkId));
        for (int index = 0; index < chunkOutputs.Count; index++)
        {
            outputHash = VoxelMath.DigestChunkOutputSummary(outputHash, chunkOutputs[index]);
        }
        CanonicalInputContract input = VoxelMath.CreateObservedCanonicalInput(options, chunkOutputs);
        string strongOutputHash = VoxelMath.ComputeStrongPipelineOutputHash(chunkOutputs);
        endToEnd.Stop();

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
             PackingRecycleMilliseconds: packingRecycleMilliseconds,
             ChunkOutputs: chunkOutputs,
             Input: input,
             StrongOutputHash: strongOutputHash,
             ColdEndToEndMilliseconds: endToEnd.Elapsed.TotalMilliseconds,
             Output: new CanonicalOutputSummary(
                 outputHash,
                 checked((int)(opaqueFaces * VoxelMath.VerticesPerFace)),
                 checked((int)(opaqueFaces * VoxelMath.IndicesPerFace)),
                 checked((int)opaqueFaces),
                 checked((int)opaqueStaged),
                 checked((int)(transparentFaces * VoxelMath.VerticesPerFace)),
                 checked((int)(transparentFaces * VoxelMath.IndicesPerFace)),
                 checked((int)transparentFaces),
                 checked((int)transparentStaged),
                 opaqueFaces,
                 transparentFaces,
                 opaqueStaged,
                 transparentStaged,
                 strongOutputHash),
             NativeOwnerProfiles: ownerProfiles);
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
        using NativePool<FaceRecord> facePool = new(
            initialCapacity: VoxelMath.CellsPerChunk,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<Vertex> vertexPool = new(
            initialCapacity: checked(VoxelMath.CellsPerChunk * 6),
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<int> indexPool = new(
            initialCapacity: checked(VoxelMath.CellsPerChunk * 9),
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena heterogeneousArena = new(
            preAllocateBytes: (nuint)(64 * 1024 * 1024),
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        OwnerAccumulator[] ownerAccumulators =
        [
            new($"worker-{workerId}:cells", cellPool.GetStatistics()),
            new($"worker-{workerId}:faces", facePool.GetStatistics()),
            new($"worker-{workerId}:vertices", vertexPool.GetStatistics()),
            new($"worker-{workerId}:indices", indexPool.GetStatistics()),
            new($"worker-{workerId}:heterogeneous", heterogeneousArena.GetStatistics())
        ];
        WorkerContext context = new(options, captureMeasuredFixture);
        int totalChunks = checked(options.ChunkCount * options.Iterations);
        int passes = 2;
        long measuredChunks = 0;
        long digest = 17;
        List<ChunkOutputSummary> chunkOutputs = [];
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
                ? options.WarmupChunksPerWorker
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
                    scoped Pooled<FaceRecord> faceOutputs = facePool.LeaseScoped(VoxelMath.CellsPerChunk);
                    rents++;
                    ownerAccumulators[1].Observe(facePool.GetStatistics());
                    {
                        scoped Pooled<VoxelCell> cells = cellPool.LeaseScoped(VoxelMath.CellsPerChunk);
                        rents++;
                        ownerAccumulators[0].Observe(cellPool.GetStatistics());
                        cells.Access(context.GenerationAction);
                        opaqueRecordCapacity = Math.Max(1, context.OpaqueRecordCount);
                        transparentRecordCapacity = Math.Max(1, context.TransparentRecordCount);
                        NativeLeaseOperations.Access(cells, faceOutputs, context.FaceAction);
                        int maskLength = checked(Math.Max(
                            1,
                            context.TransparentMaskCountForTest * VoxelMath.TransparentMaskWordsPerId));
                        try
                        {
                            {
                                scoped ArenaLease<ulong> nativeMasks = heterogeneousArena.ScratchScoped<ulong>(maskLength);
                                rents++;
                                ownerAccumulators[4].Observe(heterogeneousArena.GetStatistics());
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
                    }

                    long coordinateRecycleStart = Stopwatch.GetTimestamp();
                    cellPool.RecycleScoped();
                    context.AddCoordinateRecycleTicks(Stopwatch.GetTimestamp() - coordinateRecycleStart);
                    recycles++;
                    cleared += checked((long)VoxelMath.CellsPerChunk * VoxelMath.VoxelCellBytes);

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
                                            ownerAccumulators[2].Observe(vertexPool.GetStatistics());
                                            ownerAccumulators[3].Observe(indexPool.GetStatistics());
                                            ownerAccumulators[4].Observe(heterogeneousArena.GetStatistics());
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
                    facePool.RecycleScoped();
                    context.AddFaceRecycleTicks(Stopwatch.GetTimestamp() - recycleStart);
                    recycles++;
                    cleared += checked((long)(opaqueRecordCapacity + transparentRecordCapacity) * VoxelMath.FaceRecordBytes);
                }

                ChunkResult result = context.FinishChunk();
                ownerAccumulators[0].Observe(cellPool.GetStatistics());
                ownerAccumulators[1].Observe(facePool.GetStatistics());
                ownerAccumulators[2].Observe(vertexPool.GetStatistics());
                ownerAccumulators[3].Observe(indexPool.GetStatistics());
                ownerAccumulators[4].Observe(heterogeneousArena.GetStatistics());
                peakCoordinateStage = Math.Max(peakCoordinateStage, context.PeakCoordinateStageBytes);
                peakFaceStage = Math.Max(peakFaceStage, context.PeakFaceStageBytes);
                peakPackingStage = Math.Max(peakPackingStage, context.PeakPackingStageBytes);
                if (pass != 0)
                {
                    digest = result.Digest;
                    chunkOutputs.Add(VoxelMath.CreateChunkOutputSummary(result));
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
                NativeOwnerStatistics[] warmupStatistics =
                [
                    cellPool.GetStatistics(),
                    facePool.GetStatistics(),
                    vertexPool.GetStatistics(),
                    indexPool.GetStatistics(),
                    heterogeneousArena.GetStatistics()
                ];
                for (int ownerIndex = 0; ownerIndex < ownerAccumulators.Length; ownerIndex++)
                {
                    ownerAccumulators[ownerIndex].ResetBaseline(warmupStatistics[ownerIndex]);
                }
                ready.Signal();
                start.Wait();
            }
        }

        NativeOwnerStatistics[] finalStatistics =
        [
            cellPool.GetStatistics(),
            facePool.GetStatistics(),
            vertexPool.GetStatistics(),
            indexPool.GetStatistics(),
            heterogeneousArena.GetStatistics()
        ];
        List<NativeOwnerProfile> ownerProfiles = new(ownerAccumulators.Length);
        for (int ownerIndex = 0; ownerIndex < ownerAccumulators.Length; ownerIndex++)
        {
            ownerProfiles.Add(ownerAccumulators[ownerIndex].ToProfile(finalStatistics[ownerIndex]));
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
             PackingRecycleMilliseconds: packingRecycleMilliseconds,
             ChunkOutputs: chunkOutputs,
             NativeOwnerProfiles: ownerProfiles));
    }

}
