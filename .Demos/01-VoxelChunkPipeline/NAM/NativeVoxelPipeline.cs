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
        OutputFixture? MaterializedOutput)
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
        private readonly NativeMemoryStatistics _baseline;
        private Work _work;
        private int _chunk;
        private bool _captureCompleteFixture;
        private long _peakCoordinateStageBytes;
        private long _peakFaceStageBytes;
        private long _peakPackingStageBytes;

        internal WorkerContext(VoxelWorkloadOptions options, NativeMemoryStatistics baseline)
        {
            _options = options;
            _baseline = baseline;
            FaceAction = ProcessFaces;
            MaskAction = ProcessMasks;
            OpaqueMeshAction = PackOpaque;
            TransparentMeshAction = PackTransparent;
        }

        internal NativeLeaseTripleAction<VoxelCell, NativeFaceOutput, NativeFaceOutput> FaceAction { get; }

        internal NativeLeasePooledArenaAction<VoxelCell, ulong> MaskAction { get; }

        internal NativeLeaseQuintupleAction<NativeFaceOutput, Vertex, int, PayloadSlice, byte> OpaqueMeshAction { get; }

        internal NativeLeaseQuintupleAction<NativeFaceOutput, Vertex, int, PayloadSlice, byte> TransparentMeshAction { get; }

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

        internal void BeginChunk(int chunk, long digest)
        {
            _chunk = chunk;
            _captureCompleteFixture = false;
            _work = new Work { Digest = digest };
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
                _work.MaterializedOutput);
        }

        internal (long PeakNativeBytes, long PeakRetainedBytes) ObserveNative(long peakNative, long peakRetained)
        {
            NativeMemoryStatistics current = NativeMemoryDiagnostics.Snapshot();
            long native = Math.Max(0, current.OutstandingNativeBytes - _baseline.OutstandingNativeBytes);
            long retained = Math.Max(0, current.RetainedNativeBytes - _baseline.RetainedNativeBytes);
            return (Math.Max(peakNative, native), Math.Max(peakRetained, retained));
        }

        private void ProcessFaces(
            scoped NativeLeaseView<VoxelCell> cells,
            scoped NativeLeaseView<NativeFaceOutput> opaqueOutput,
            scoped NativeLeaseView<NativeFaceOutput> transparentOutput)
        {
            long cellBytes = checked((long)cells.Capacity * VoxelMath.VoxelCellBytes);
            long faceBytes = checked((long)opaqueOutput.Capacity * VoxelMath.NativeFaceOutputBytes);
            _peakCoordinateStageBytes = Math.Max(_peakCoordinateStageBytes, cellBytes);
            _peakFaceStageBytes = Math.Max(_peakFaceStageBytes, checked(cellBytes + faceBytes * 2));
            Span<VoxelCell> cellSpan = cells.AsSpan();
            Span<NativeFaceOutput> opaqueSpan = opaqueOutput.AsSpan();
            Span<NativeFaceOutput> transparentSpan = transparentOutput.AsSpan();
            int opaqueCount = 0;
            int transparentCount = 0;
            int opaqueRecordCount = 0;
            int transparentRecordCount = 0;
            for (int cell = 0; cell < cellSpan.Length; cell++)
            {
                int x = cell % VoxelMath.ChunkDimension;
                int y = (cell / VoxelMath.ChunkDimension) % VoxelMath.ChunkDimension;
                int z = cell / (VoxelMath.ChunkDimension * VoxelMath.ChunkDimension);
                int blockId = VoxelMath.BlockIdForCell(_options.Seed, _chunk, x, y, z);
                short density = VoxelMath.DensityForCell(_options.Seed, _chunk, x, y, z, blockId);
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

            for (int cell = 0; cell < cellSpan.Length; cell++)
            {
                int blockId = cellSpan[cell].BlockId;
                int mask = VoxelMath.FaceMaskFromCells(cell, cellSpan);
                cellSpan[cell].FaceMask = mask;
                cellSpan[cell].OpaqueMask = VoxelMath.IsOpaque(blockId) ? mask : 0;
                cellSpan[cell].TransparentMask = VoxelMath.IsTransparent(blockId) ? mask : 0;
                _work.Digest = VoxelMath.DigestStep(_work.Digest, mask);

                if (mask == 0)
                {
                    continue;
                }

                NativeFaceOutput output = new() { CellIndex = cell, BlockId = blockId, FaceMask = mask };
                if (VoxelMath.IsOpaque(blockId))
                {
                    opaqueSpan[opaqueRecordCount++] = output;
                }
                else if (VoxelMath.IsTransparent(blockId))
                {
                    transparentSpan[transparentRecordCount++] = output;
                }
            }

            (opaqueCount, int opaqueBytes) = MeasureOutput(opaqueSpan, opaqueRecordCount);
            (transparentCount, int transparentBytes) = MeasureOutput(transparentSpan, transparentRecordCount);
            _work.OpaqueFaces = opaqueCount;
            _work.TransparentFaces = transparentCount;
            _work.OpaqueRecords = opaqueRecordCount;
            _work.TransparentRecords = transparentRecordCount;
            _work.OpaqueStagedBytes = opaqueBytes;
            _work.TransparentStagedBytes = transparentBytes;
            ProcessSections(cellSpan);
        }

        private static (int FaceCount, int StagedBytes) MeasureOutput(
            ReadOnlySpan<NativeFaceOutput> records,
            int recordCount)
        {
            int faceCount = 0;
            int stagedBytes = 0;
            for (int recordIndex = 0; recordIndex < recordCount; recordIndex++)
            {
                NativeFaceOutput record = records[recordIndex];
                BlockTypeDescriptor type = VoxelMath.BlockTypeForId(record.BlockId);
                for (int face = 0; face < VoxelMath.FacesPerCell; face++)
                {
                    if ((record.FaceMask & (1 << face)) == 0)
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

        private void ProcessSections(ReadOnlySpan<VoxelCell> cellSpan)
        {
            for (int section = 0; section < 64; section++)
            {
                SectionSummary summary = VoxelMath.ClassifySection(cellSpan, section);
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
            ReadOnlySpan<VoxelCell> cellSpan = cells.AsSpan();
            Span<ulong> destination = view.AsSpan();
            destination.Clear();
            int maskOffset = 0;
            for (int section = 0; section < 64; section++)
            {
                SectionSummary summary = VoxelMath.ClassifySection(cellSpan, section);
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
        }

        private void PackOpaque(
            scoped NativeLeaseView<NativeFaceOutput> faces,
            scoped NativeLeaseView<Vertex> vertices,
            scoped NativeLeaseView<int> indices,
            scoped NativeLeaseView<PayloadSlice> slices,
            scoped NativeLeaseView<byte> upload) =>
            Pack(true, _work.OpaqueRecords, faces, vertices, indices, slices, upload);

        private void PackTransparent(
            scoped NativeLeaseView<NativeFaceOutput> faces,
            scoped NativeLeaseView<Vertex> vertices,
            scoped NativeLeaseView<int> indices,
            scoped NativeLeaseView<PayloadSlice> slices,
            scoped NativeLeaseView<byte> upload) =>
            Pack(false, _work.TransparentRecords, faces, vertices, indices, slices, upload);

        private void Pack(
            bool opaque,
            int recordCount,
            scoped NativeLeaseView<NativeFaceOutput> faces,
            scoped NativeLeaseView<Vertex> vertices,
            scoped NativeLeaseView<int> indices,
            scoped NativeLeaseView<PayloadSlice> slices,
            scoped NativeLeaseView<byte> upload)
        {
            int expectedBytes = opaque ? _work.OpaqueStagedBytes : _work.TransparentStagedBytes;
            _peakPackingStageBytes = Math.Max(
                _peakPackingStageBytes,
                checked((long)faces.Capacity * VoxelMath.NativeFaceOutputBytes
                    + (long)vertices.Capacity * VoxelMath.VertexBytes
                    + (long)indices.Capacity * VoxelMath.IndexBytes
                    + (long)slices.Capacity * VoxelMath.PayloadSliceBytes
                    + upload.Capacity));
            Span<NativeFaceOutput> faceSpan = faces.AsSpan();
            Span<Vertex> vertexSpan = vertices.AsSpan();
            Span<int> indexSpan = indices.AsSpan();
            Span<PayloadSlice> sliceSpan = slices.AsSpan();
            Span<byte> uploadSpan = upload.AsSpan();
            int vertexCount = 0;
            int indexCount = 0;
            int sliceCount = 0;
            int offset = 0;
            int enabledStageBytes = 0;
            for (int recordIndex = 0; recordIndex < recordCount; recordIndex++)
            {
                NativeFaceOutput record = faceSpan[recordIndex];
                for (int face = 0; face < VoxelMath.FacesPerCell; face++)
                {
                    if ((record.FaceMask & (1 << face)) == 0)
                    {
                        continue;
                    }

                    BlockTypeDescriptor type = VoxelMath.BlockTypeForId(record.BlockId);
                    int alignedOffset = VoxelMath.AlignUp(offset, type.Alignment);
                    uploadSpan.Slice(offset, alignedOffset - offset).Clear();
                    offset = alignedOffset;
                    int cursor = offset;
                    for (int slot = 0; slot < type.PayloadBytes; slot++)
                    {
                        bool enabled = (type.StageMask & (1 << (slot % 4))) != 0;
                        uploadSpan[cursor++] = enabled
                            ? checked((byte)VoxelMath.PayloadByte(_options.Seed, record.CellIndex, record.BlockId, slot))
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

                    int faceBytes = VoxelMath.StageBytesForType(type);
                    int end = checked(offset + faceBytes);
                    while (cursor < end)
                    {
                        uploadSpan[cursor++] = 0;
                    }

                    sliceSpan[sliceCount++] = new PayloadSlice(
                        offset,
                        faceBytes,
                        type.Alignment,
                        type.StageMask,
                        record.BlockId,
                        record.CellIndex);
                    offset = end;
                }
            }

            if (offset != expectedBytes)
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
            if (_chunk == 0)
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
        WorkerContext context = new(fixtureOptions, NativeMemoryDiagnostics.Snapshot());
        context.BeginIndependentFixture(
            VoxelMath.IndependentOpaqueRecords.Length,
            opaqueFaces,
            opaqueBytes,
            VoxelMath.IndependentTransparentRecords.Length,
            transparentFaces,
            transparentBytes);

        using NativePool<NativeFaceOutput> facePool = new();
        using NativePool<Vertex> vertexPool = new();
        using NativePool<int> indexPool = new();
        using NativeArena heterogeneousArena = new();
        using Pooled<NativeFaceOutput> opaque = facePool.Rent(VoxelMath.IndependentOpaqueRecords.Length);
        using Pooled<NativeFaceOutput> transparent = facePool.Rent(VoxelMath.IndependentTransparentRecords.Length);
        using Pooled<Vertex> opaqueVertices = vertexPool.Rent(opaqueFaces * VoxelMath.VerticesPerFace);
        using Pooled<int> opaqueIndices = indexPool.Rent(opaqueFaces * VoxelMath.IndicesPerFace);
        ArenaLease<PayloadSlice> opaqueSlices = heterogeneousArena.Scratch<PayloadSlice>(opaqueFaces);
        ArenaLease<byte> opaqueUpload = heterogeneousArena.Scratch<byte>(opaqueBytes);
        using Pooled<Vertex> transparentVertices = vertexPool.Rent(transparentFaces * VoxelMath.VerticesPerFace);
        using Pooled<int> transparentIndices = indexPool.Rent(transparentFaces * VoxelMath.IndicesPerFace);
        ArenaLease<PayloadSlice> transparentSlices = heterogeneousArena.Scratch<PayloadSlice>(transparentFaces);
        ArenaLease<byte> transparentUpload = heterogeneousArena.Scratch<byte>(transparentBytes);

        NativeLeaseOperations.Access(
            opaque,
            transparent,
            static (opaqueView, transparentView) =>
            {
                Span<NativeFaceOutput> opaqueSpan = opaqueView.AsSpan();
                for (int index = 0; index < VoxelMath.IndependentOpaqueRecords.Length; index++)
                {
                    FaceRecord record = VoxelMath.IndependentOpaqueRecords[index];
                    opaqueSpan[index] = new NativeFaceOutput
                    {
                        CellIndex = record.CellIndex,
                        BlockId = record.BlockId,
                        FaceMask = record.Mask
                    };
                }

                Span<NativeFaceOutput> transparentSpan = transparentView.AsSpan();
                for (int index = 0; index < VoxelMath.IndependentTransparentRecords.Length; index++)
                {
                    FaceRecord record = VoxelMath.IndependentTransparentRecords[index];
                    transparentSpan[index] = new NativeFaceOutput
                    {
                        CellIndex = record.CellIndex,
                        BlockId = record.BlockId,
                        FaceMask = record.Mask
                    };
                }
            });
        NativeLeaseOperations.Access(
            opaque,
            opaqueVertices,
            opaqueIndices,
            opaqueSlices,
            opaqueUpload,
            context.OpaqueMeshAction);
        NativeLeaseOperations.Access(
            transparent,
            transparentVertices,
            transparentIndices,
            transparentSlices,
            transparentUpload,
            context.TransparentMeshAction);

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

    public static PipelineResult Run(VoxelWorkloadOptions options)
    {
        VoxelMath.ValidateBoundaryFixture();
        OutputFixture independentFixture = CreateIndependentFixture();
        NativeMemoryStatistics baseline = NativeMemoryDiagnostics.Snapshot();
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
            tasks[worker] = Task.Run(() => workers[workerId] = RunWorker(options, workerId, baseline, ready, start));
        }

        ready.Wait();
        NativeMemoryStatistics measuredBaseline = NativeMemoryDiagnostics.Snapshot();
        allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        gen0Before = GC.CollectionCount(0);
        gen1Before = GC.CollectionCount(1);
        gen2Before = GC.CollectionCount(2);
        Stopwatch stopwatch = Stopwatch.StartNew();
        start.Set();
        Task.WaitAll(tasks);
        stopwatch.Stop();

        NativeMemoryStatistics final = NativeMemoryDiagnostics.Snapshot();
        long reusedNativeSegments = Math.Max(
            0,
            final.ReusedNativeSegmentCount - measuredBaseline.ReusedNativeSegmentCount);
        long digest = 17;
        long chunks = 0;
        long visibleFaces = 0;
        long vertices = 0;
        long indices = 0;
        long stagedBytes = 0;
        long peakNative = 0;
        long peakRetained = 0;
        long peakCoordinateStage = 0;
        long peakFaceStage = 0;
        long peakPackingStage = 0;
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
            IndependentFixture: independentFixture);
    }

    private static WorkerResult RunWorker(
        VoxelWorkloadOptions options,
        int workerId,
        NativeMemoryStatistics baseline,
        CountdownEvent ready,
        ManualResetEventSlim start)
    {
        using NativePool<VoxelCell> cellPool = new(
            initialCapacity: VoxelMath.CellsPerChunk,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<NativeFaceOutput> facePool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<Vertex> vertexPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<int> indexPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena heterogeneousArena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        WorkerContext context = new(options, baseline);
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
                context.BeginChunk(chunk, pass == 0 ? 17 : digest);
                int faceCapacity = VoxelMath.CellsPerChunk;
                try
                {
                    scoped Pooled<NativeFaceOutput> opaque = facePool.LeaseScoped(faceCapacity);
                    rents++;
                    {
                        scoped Pooled<NativeFaceOutput> transparent = facePool.LeaseScoped(faceCapacity);
                        rents++;
                                    try
                                    {
                                            scoped Pooled<VoxelCell> cells = cellPool.LeaseScoped(VoxelMath.CellsPerChunk);
                                            rents++;
                                            NativeLeaseOperations.Access(cells, opaque, transparent, context.FaceAction);
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
                                                heterogeneousArena.RecycleScoped();
                                                recycles++;
                                                cleared += checked((long)maskLength * sizeof(ulong));
                                            }
                                        }
                                    finally
                                    {
                                        cellPool.RecycleScoped();
                                        recycles++;
                                        cleared += checked((long)VoxelMath.CellsPerChunk * VoxelMath.VoxelCellBytes);
                                    }

                                    int opaqueVertexLength = Math.Max(1, checked(context.OpaqueFaceCount * VoxelMath.VerticesPerFace));
                                    int transparentVertexLength = Math.Max(1, checked(context.TransparentFaceCount * VoxelMath.VerticesPerFace));
                                    int opaqueIndexLength = Math.Max(1, checked(context.OpaqueFaceCount * VoxelMath.IndicesPerFace));
                                    int transparentIndexLength = Math.Max(1, checked(context.TransparentFaceCount * VoxelMath.IndicesPerFace));
                                    try
                                    {
                                        {
                                            scoped Pooled<Vertex> opaqueVertices = vertexPool.LeaseScoped(opaqueVertexLength);
                                            scoped Pooled<int> opaqueIndices = indexPool.LeaseScoped(opaqueIndexLength);
                                            scoped ArenaLease<PayloadSlice> opaqueSlices = heterogeneousArena.ScratchScoped<PayloadSlice>(Math.Max(1, context.OpaqueFaceCount));
                                            scoped ArenaLease<byte> opaqueUpload = heterogeneousArena.ScratchScoped<byte>(Math.Max(1, context.OpaqueStageBytes));
                                            scoped Pooled<Vertex> transparentVertices = vertexPool.LeaseScoped(transparentVertexLength);
                                            scoped Pooled<int> transparentIndices = indexPool.LeaseScoped(transparentIndexLength);
                                            scoped ArenaLease<PayloadSlice> transparentSlices = heterogeneousArena.ScratchScoped<PayloadSlice>(Math.Max(1, context.TransparentFaceCount));
                                            scoped ArenaLease<byte> transparentUpload = heterogeneousArena.ScratchScoped<byte>(Math.Max(1, context.TransparentStageBytes));
                                            rents += 8;
                                            NativeLeaseOperations.Access(
                                                opaque,
                                                opaqueVertices,
                                                opaqueIndices,
                                                opaqueSlices,
                                                opaqueUpload,
                                                context.OpaqueMeshAction);
                                            NativeLeaseOperations.Access(
                                                transparent,
                                                transparentVertices,
                                                transparentIndices,
                                                transparentSlices,
                                                transparentUpload,
                                                context.TransparentMeshAction);
                                        }
                                    }
                                    finally
                                    {
                                        heterogeneousArena.RecycleScoped();
                                        recycles++;
                                        cleared += checked((long)Math.Max(1, context.OpaqueFaceCount) * VoxelMath.PayloadSliceBytes);
                                        cleared += checked((long)Math.Max(1, context.TransparentFaceCount) * VoxelMath.PayloadSliceBytes);
                                        cleared += Math.Max(1, context.OpaqueStageBytes);
                                        cleared += Math.Max(1, context.TransparentStageBytes);
                                        vertexPool.RecycleScoped();
                                        indexPool.RecycleScoped();
                                        recycles += 2;
                                        cleared += checked((long)opaqueVertexLength * VoxelMath.VertexBytes);
                                        cleared += checked((long)transparentVertexLength * VoxelMath.VertexBytes);
                                        cleared += checked((long)opaqueIndexLength * VoxelMath.IndexBytes);
                                        cleared += checked((long)transparentIndexLength * VoxelMath.IndexBytes);
                                    }

                                    ChunkResult result = context.FinishChunk();
                                    (long observedNative, long observedRetained) = context.ObserveNative(peakNative, peakRetained);
                                    peakNative = observedNative;
                                    peakRetained = observedRetained;
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
                                        materializedOutput ??= result.MaterializedOutput;
                                    }
                        }
                }
                finally
                {
                    facePool.RecycleScoped();
                    recycles++;
                    cleared += checked((long)faceCapacity * VoxelMath.NativeFaceOutputBytes * 2);
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
            MaterializedOutput: materializedOutput));
    }

}
