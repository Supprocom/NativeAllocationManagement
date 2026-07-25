using System.Buffers;
using System.Diagnostics;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SafeCSharp;

internal static class SafeVoxelPipeline
{
    private enum Stage
    {
        Coordinates,
        Faces,
        Packing
    }

    private struct Metrics
    {
        internal long CurrentBackingBytes;
        internal long PeakBackingBytes;
        internal long PeakCoordinateBytes;
        internal long PeakFaceBytes;
        internal long PeakPackingBytes;
        internal long Rents;
        internal long Recycles;
        internal long ClearedBytes;

        internal void Enter(long bytes, Stage stage)
        {
            CurrentBackingBytes += bytes;
            PeakBackingBytes = Math.Max(PeakBackingBytes, CurrentBackingBytes);
            switch (stage)
            {
                case Stage.Coordinates: PeakCoordinateBytes = Math.Max(PeakCoordinateBytes, CurrentBackingBytes); break;
                case Stage.Faces: PeakFaceBytes = Math.Max(PeakFaceBytes, CurrentBackingBytes); break;
                case Stage.Packing: PeakPackingBytes = Math.Max(PeakPackingBytes, CurrentBackingBytes); break;
            }
        }

        internal void Leave(long bytes) => CurrentBackingBytes -= bytes;

        internal void Boundary(long bytes)
        {
            Recycles++;
            ClearedBytes += bytes;
        }
    }

    private readonly record struct WorkerResult(PipelineResult Result);

    private readonly record struct ChunkResult(
        long Digest,
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
        OutputFixture? MaterializedOutput)
    {
        internal int VisibleFaces => OpaqueFaces + TransparentFaces;
        internal int Vertices => OpaqueVertices + TransparentVertices;
        internal int Indices => OpaqueIndices + TransparentIndices;
        internal int StagedBytes => OpaqueStagedBytes + TransparentStagedBytes;
    }

    private readonly record struct StreamResult(
        int FaceCount,
        int VertexCount,
        int IndexCount,
        int StagedBytes,
        int EnabledStageBytes);

    public static PipelineResult Run(VoxelWorkloadOptions options)
    {
        VoxelMath.ValidateBoundaryFixture();
        WorkerResult[] workers = new WorkerResult[options.WorkerCount];
        Task[] tasks = new Task[options.WorkerCount];
        using CountdownEvent ready = new(options.WorkerCount);
        using ManualResetEventSlim start = new(false);
        for (int worker = 0; worker < options.WorkerCount; worker++)
        {
            int workerId = worker;
            tasks[worker] = Task.Run(() => workers[workerId] = RunWorker(options, workerId, ready, start));
        }

        ready.Wait();
        long allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        Stopwatch stopwatch = Stopwatch.StartNew();
        start.Set();
        Task.WaitAll(tasks);
        stopwatch.Stop();

        long digest = 17;
        long chunks = 0;
        long visibleFaces = 0;
        long vertices = 0;
        long indices = 0;
        long stagedBytes = 0;
        long peakManaged = 0;
        long coldPeakManaged = 0;
        long peakCoordinates = 0;
        long peakFaces = 0;
        long peakPacking = 0;
        long rents = 0;
        long recycles = 0;
        long clearedBytes = 0;
        long empty = 0;
        long uniform = 0;
        long expanded = 0;
        long packed = 0;
        long multiPacked = 0;
        long transparentMasks = 0;
        long transparentMaskWords = 0;
        long dominant = 0;
        long residual = 0;
        long opaqueFaces = 0;
        long transparentFaces = 0;
        long opaqueVertices = 0;
        long transparentVertices = 0;
        long opaqueIndices = 0;
        long transparentIndices = 0;
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
            peakManaged = Math.Max(peakManaged, result.PeakManagedBackingBytes);
            coldPeakManaged = Math.Max(coldPeakManaged, result.ColdManagedBackingBytes);
            peakCoordinates = Math.Max(peakCoordinates, result.PeakCoordinateStageBytes);
            peakFaces = Math.Max(peakFaces, result.PeakFaceStageBytes);
            peakPacking = Math.Max(peakPacking, result.PeakPackingStageBytes);
            rents += result.RentCount;
            recycles += result.ScopedRecycleCount;
            clearedBytes += result.ClearedBytes;
            empty += result.EmptySections;
            uniform += result.UniformSections;
            expanded += result.ExpandedSections;
            packed += result.PackedSections;
            multiPacked += result.MultiPackedSections;
            transparentMasks += result.TransparentMaskCount;
            transparentMaskWords += result.TransparentMaskWords;
            dominant += result.DominantTransparentSections;
            residual += result.ResidualTransparentSections;
            opaqueFaces += result.OpaqueVisibleFaces;
            transparentFaces += result.TransparentVisibleFaces;
            opaqueVertices += result.OpaqueVertices;
            transparentVertices += result.TransparentVertices;
            opaqueIndices += result.OpaqueIndices;
            transparentIndices += result.TransparentIndices;
            opaqueStaged += result.OpaqueStagedBytes;
            transparentStaged += result.TransparentStagedBytes;
            enabledStageBytes += result.EnabledStageBytes;
            materializedOutput ??= result.MaterializedOutput;
        }

        return new PipelineResult(
            "SafeCSharp",
            digest,
            checked((int)chunks),
            visibleFaces,
            vertices,
            indices,
            stagedBytes,
            0,
            peakManaged,
            0,
            0,
            0,
            peakCoordinates,
            peakFaces,
            peakPacking,
            rents,
            recycles,
            clearedBytes,
            empty,
            uniform,
            expanded,
            packed,
            multiPacked,
            transparentMasks,
            dominant,
            residual,
            opaqueFaces,
            transparentFaces,
            opaqueVertices,
            transparentVertices,
            opaqueIndices,
            transparentIndices,
            opaqueStaged,
            transparentStaged,
            enabledStageBytes,
            peakManaged,
             transparentMaskWords,
             0,
             0,
             stopwatch.Elapsed.TotalMilliseconds,
             GC.GetTotalAllocatedBytes(precise: true) - allocationBefore,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            materializedOutput,
            coldPeakManaged);
    }

    private static WorkerResult RunWorker(
        VoxelWorkloadOptions options,
        int workerId,
        CountdownEvent ready,
        ManualResetEventSlim start)
    {
        Metrics metrics = default;
        int totalChunks = checked(options.ChunkCount * options.Iterations);
        for (int warmup = 0; warmup < VoxelWorkloadOptions.WarmupChunksPerWorker; warmup++)
        {
            _ = ProcessChunk(options, checked(totalChunks + workerId + warmup), ref metrics, 17);
        }

        long coldPeakManaged = metrics.PeakBackingBytes;
        metrics = default;
        ready.Signal();
        start.Wait();
        long digest = 17;
        long chunks = 0;
        long visibleFaces = 0;
        long vertices = 0;
        long indices = 0;
        long stagedBytes = 0;
        long empty = 0;
        long uniform = 0;
        long expanded = 0;
        long packed = 0;
        long multiPacked = 0;
        long transparentMasks = 0;
        long transparentMaskWords = 0;
        long dominant = 0;
        long residual = 0;
        long opaqueFaces = 0;
        long transparentFaces = 0;
        long opaqueVertices = 0;
        long transparentVertices = 0;
        long opaqueIndices = 0;
        long transparentIndices = 0;
        long opaqueStaged = 0;
        long transparentStaged = 0;
        long enabledStageBytes = 0;
        OutputFixture? materializedOutput = null;
        for (int chunk = workerId; chunk < totalChunks; chunk += options.WorkerCount)
        {
            ChunkResult result = ProcessChunk(options, chunk, ref metrics, digest);
            digest = result.Digest;
            chunks++;
            visibleFaces += result.VisibleFaces;
            vertices += result.Vertices;
            indices += result.Indices;
            stagedBytes += result.StagedBytes;
            empty += result.EmptySections;
            uniform += result.UniformSections;
            expanded += result.ExpandedSections;
            packed += result.PackedSections;
            multiPacked += result.MultiPackedSections;
            transparentMasks += result.TransparentMaskCount;
            transparentMaskWords += result.TransparentMaskWords;
            dominant += result.DominantTransparentSections;
            residual += result.ResidualTransparentSections;
            opaqueFaces += result.OpaqueFaces;
            transparentFaces += result.TransparentFaces;
            opaqueVertices += result.OpaqueVertices;
            transparentVertices += result.TransparentVertices;
            opaqueIndices += result.OpaqueIndices;
            transparentIndices += result.TransparentIndices;
            opaqueStaged += result.OpaqueStagedBytes;
            transparentStaged += result.TransparentStagedBytes;
            enabledStageBytes += result.EnabledStageBytes;
            materializedOutput ??= result.MaterializedOutput;
        }

        return new WorkerResult(new PipelineResult(
            "SafeCSharp",
            digest,
            checked((int)chunks),
            visibleFaces,
            vertices,
            indices,
            stagedBytes,
            0,
            metrics.PeakBackingBytes,
            0,
            0,
            0,
            metrics.PeakCoordinateBytes,
            metrics.PeakFaceBytes,
            metrics.PeakPackingBytes,
            metrics.Rents,
            metrics.Recycles,
            metrics.ClearedBytes,
            empty,
            uniform,
            expanded,
            packed,
            multiPacked,
            transparentMasks,
            dominant,
            residual,
            opaqueFaces,
            transparentFaces,
            opaqueVertices,
            transparentVertices,
            opaqueIndices,
            transparentIndices,
            opaqueStaged,
            transparentStaged,
            enabledStageBytes,
            metrics.PeakBackingBytes,
            transparentMaskWords,
            MaterializedOutput: materializedOutput,
            ColdManagedBackingBytes: coldPeakManaged));
    }

    private static ChunkResult ProcessChunk(
        VoxelWorkloadOptions options,
        int chunk,
        ref Metrics metrics,
        long digest)
    {
        int cellCount = VoxelMath.CellsPerChunk;
        VoxelCell[] cells = Rent<VoxelCell>(cellCount, ref metrics, Stage.Coordinates);
        try
        {
            for (int cell = 0; cell < cellCount; cell++)
            {
                int x = cell % VoxelMath.ChunkDimension;
                int y = (cell / VoxelMath.ChunkDimension) % VoxelMath.ChunkDimension;
                int z = cell / (VoxelMath.ChunkDimension * VoxelMath.ChunkDimension);
                int blockId = VoxelMath.BlockIdForCell(options.Seed, chunk, x, y, z);
                short density = VoxelMath.DensityForCell(options.Seed, chunk, x, y, z, blockId);
                cells[cell] = new VoxelCell { BlockId = checked((ushort)blockId), Density = density, Section = VoxelMath.SectionIndex(x, y, z) };
                digest = VoxelMath.DigestStep(digest, x);
                digest = VoxelMath.DigestStep(digest, y);
                digest = VoxelMath.DigestStep(digest, z);
                digest = VoxelMath.DigestStep(digest, density);
                digest = VoxelMath.DigestStep(digest, blockId);
            }

            FaceRecord[] opaqueFaces = Rent<FaceRecord>(cellCount, ref metrics, Stage.Faces);
            FaceRecord[] transparentFaces = Rent<FaceRecord>(cellCount, ref metrics, Stage.Faces);
            int opaqueFaceCells = 0;
            int transparentFaceCells = 0;
            for (int cell = 0; cell < cellCount; cell++)
            {
                int mask = VoxelMath.FaceMaskFromCells(cell, cells.AsSpan(0, cellCount));
                int blockId = cells[cell].BlockId;
                cells[cell].FaceMask = mask;
                cells[cell].OpaqueMask = VoxelMath.IsOpaque(blockId) ? mask : 0;
                cells[cell].TransparentMask = VoxelMath.IsTransparent(blockId) ? mask : 0;
                digest = VoxelMath.DigestStep(digest, mask);
                if (mask == 0)
                {
                    continue;
                }

                if (VoxelMath.IsTransparent(blockId))
                {
                    transparentFaces[transparentFaceCells++] = new FaceRecord(cell, blockId, mask);
                }
                else
                {
                    opaqueFaces[opaqueFaceCells++] = new FaceRecord(cell, blockId, mask);
                }
            }

            int empty = 0;
            int uniform = 0;
            int expanded = 0;
            int packed = 0;
            int multiPacked = 0;
            int transparentMaskCount = 0;
            int dominant = 0;
            int residual = 0;
            int transparentMaskWords = 0;
            for (int section = 0; section < 64; section++)
            {
                SectionSummary summary = VoxelMath.ClassifySection(cells.AsSpan(0, cellCount), section);
                switch (summary.Kind)
                {
                    case SectionRepresentationKind.Empty: empty++; break;
                    case SectionRepresentationKind.Uniform: uniform++; break;
                    case SectionRepresentationKind.Expanded: expanded++; break;
                    case SectionRepresentationKind.Packed: packed++; break;
                    case SectionRepresentationKind.MultiPacked: multiPacked++; break;
                }

                transparentMaskCount += summary.TransparentIds;
                if (summary.HasDominantTransparentId) dominant++;
                if (summary.HasResidualTransparentIds) residual++;
            }

            ulong[]? masks = null;
            try
            {
                if (transparentMaskCount != 0)
                {
                    transparentMaskWords = checked(transparentMaskCount * VoxelMath.TransparentMaskWordsPerId);
                    masks = Rent<ulong>(transparentMaskWords, ref metrics, Stage.Faces);
                    Array.Clear(masks, 0, transparentMaskWords);
                    int maskOffset = 0;
                    for (int section = 0; section < 64; section++)
                    {
                        SectionSummary summary = VoxelMath.ClassifySection(cells.AsSpan(0, cellCount), section);
                        int written = VoxelMath.BuildTransparentMasks(
                            cells.AsSpan(0, cellCount),
                            section,
                            masks.AsSpan(maskOffset, checked(summary.TransparentIds * VoxelMath.TransparentMaskWordsPerId)));
                        if (written != summary.TransparentIds)
                        {
                            throw new InvalidDataException("Transparent mask classification and emission disagree.");
                        }

                        int words = checked(written * VoxelMath.TransparentMaskWordsPerId);
                        for (int word = 0; word < words; word++)
                        {
                            digest = VoxelMath.DigestStep(digest, unchecked((long)masks[maskOffset + word]));
                        }

                        maskOffset += words;
                    }
                }

                metrics.Leave(ArrayBytes(cells));
                Return(cells);
                cells = Array.Empty<VoxelCell>();
                metrics.Boundary(checked((long)cellCount * ElementSize<VoxelCell>()));

                StreamResult opaque = PackStream(
                    options,
                    opaqueFaces,
                    opaqueFaceCells,
                    ref metrics,
                    Stage.Packing,
                    ref digest,
                    out Vertex[] opaqueVertices,
                    out int[] opaqueIndices,
                    out PayloadSlice[] opaqueSlices,
                    out byte[] opaqueUpload);
                try
                {
                    StreamResult transparent = PackStream(
                        options,
                        transparentFaces,
                        transparentFaceCells,
                        ref metrics,
                        Stage.Packing,
                        ref digest,
                        out Vertex[] transparentVertices,
                        out int[] transparentIndices,
                        out PayloadSlice[] transparentSlices,
                        out byte[] transparentUpload);
                    try
                    {
                        digest = VoxelMath.DigestStep(digest, opaque.FaceCount);
                        digest = VoxelMath.DigestStep(digest, transparent.FaceCount);
                        digest = VoxelMath.DigestStep(digest, opaque.VertexCount);
                        digest = VoxelMath.DigestStep(digest, transparent.VertexCount);
                        digest = VoxelMath.DigestStep(digest, opaque.IndexCount);
                        digest = VoxelMath.DigestStep(digest, transparent.IndexCount);
                        digest = VoxelMath.DigestStep(digest, opaque.StagedBytes);
                        digest = VoxelMath.DigestStep(digest, transparent.StagedBytes);
                        digest = VoxelMath.DigestStep(digest, opaque.EnabledStageBytes + transparent.EnabledStageBytes);
                        OutputFixture? fixture = chunk == 0
                            ? CreateOutputFixture(
                                opaqueVertices,
                                opaque.VertexCount,
                                opaqueIndices,
                                opaque.IndexCount,
                                opaqueSlices,
                                opaque.FaceCount,
                                opaqueUpload,
                                opaque.StagedBytes,
                                transparentVertices,
                                transparent.VertexCount,
                                transparentIndices,
                                transparent.IndexCount,
                                transparentSlices,
                                transparent.FaceCount,
                                transparentUpload,
                                transparent.StagedBytes)
                            : null;
                        return new ChunkResult(
                            digest,
                            opaque.FaceCount,
                            transparent.FaceCount,
                            opaque.VertexCount,
                            transparent.VertexCount,
                            opaque.IndexCount,
                            transparent.IndexCount,
                            opaque.StagedBytes,
                            transparent.StagedBytes,
                            opaque.EnabledStageBytes + transparent.EnabledStageBytes,
                            empty,
                            uniform,
                            expanded,
                            packed,
                            multiPacked,
                            transparentMaskCount,
                            transparentMaskWords,
                            dominant,
                            residual,
                            fixture);
                    }
                    finally
                    {
                        ReleaseStream(transparentVertices, transparentIndices, transparentSlices, transparentUpload, ref metrics);
                    }
                }
                finally
                {
                    ReleaseStream(opaqueVertices, opaqueIndices, opaqueSlices, opaqueUpload, ref metrics);
                    metrics.Boundary(opaque.StagedBytes);
                }
            }
            finally
            {
                if (masks is not null)
                {
                    metrics.Leave(ArrayBytes(masks));
                    Return(masks);
                }

                metrics.Leave(ArrayBytes(opaqueFaces));
                metrics.Leave(ArrayBytes(transparentFaces));
                Return(opaqueFaces);
                Return(transparentFaces);
                metrics.Boundary(checked((long)(opaqueFaces.Length + transparentFaces.Length) * ElementSize<FaceRecord>()));
            }
        }
        finally
        {
            if (cells.Length != 0)
            {
                metrics.Leave(ArrayBytes(cells));
                Return(cells);
            }
        }
    }

    private static StreamResult PackStream(
        VoxelWorkloadOptions options,
        FaceRecord[] records,
        int recordCount,
        ref Metrics metrics,
        Stage stage,
        ref long digest,
        out Vertex[] vertices,
        out int[] indices,
        out PayloadSlice[] slices,
        out byte[] upload)
    {
        int stagedBytes = 0;
        for (int recordIndex = 0; recordIndex < recordCount; recordIndex++)
        {
            FaceRecord record = records[recordIndex];
            for (int face = 0; face < VoxelMath.FacesPerCell; face++)
            {
                if ((record.Mask & (1 << face)) != 0)
                {
                    BlockTypeDescriptor type = VoxelMath.BlockTypeForId(record.BlockId);
                    stagedBytes = VoxelMath.AlignUp(stagedBytes, type.Alignment);
                    stagedBytes = checked(stagedBytes + VoxelMath.StageBytesForType(type));
                }
            }
        }

        int faceCount = 0;
        for (int recordIndex = 0; recordIndex < recordCount; recordIndex++)
        {
            faceCount += VoxelMath.FaceCount(records[recordIndex].Mask);
        }

        vertices = Rent<Vertex>(Math.Max(1, checked(faceCount * VoxelMath.VerticesPerFace)), ref metrics, stage);
        indices = Rent<int>(Math.Max(1, checked(faceCount * VoxelMath.IndicesPerFace)), ref metrics, stage);
        slices = Rent<PayloadSlice>(Math.Max(1, faceCount), ref metrics, stage);
        upload = Rent<byte>(Math.Max(1, stagedBytes), ref metrics, stage);
        int vertexCount = 0;
        int indexCount = 0;
        int emitted = 0;
        int offset = 0;
        int enabledStageBytes = 0;
        for (int recordIndex = 0; recordIndex < recordCount; recordIndex++)
        {
            FaceRecord record = records[recordIndex];
            for (int face = 0; face < VoxelMath.FacesPerCell; face++)
            {
                if ((record.Mask & (1 << face)) == 0)
                {
                    continue;
                }

                BlockTypeDescriptor type = VoxelMath.BlockTypeForId(record.BlockId);
                int alignedOffset = VoxelMath.AlignUp(offset, type.Alignment);
                upload.AsSpan(offset, alignedOffset - offset).Clear();
                offset = alignedOffset;
                int cursor = offset;
                for (int slot = 0; slot < type.PayloadBytes; slot++)
                {
                    bool enabled = (type.StageMask & (1 << (slot % 4))) != 0;
                    upload[cursor++] = enabled
                        ? checked((byte)VoxelMath.PayloadByte(options.Seed, record.CellIndex, record.BlockId, slot))
                        : (byte)0;
                    if (enabled)
                    {
                        enabledStageBytes++;
                    }
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
                    vertices[vertexCount++] = value;
                    WriteInt(upload, ref cursor, value.X);
                    WriteInt(upload, ref cursor, value.Y);
                    WriteInt(upload, ref cursor, value.Z);
                    WriteInt(upload, ref cursor, value.Face);
                    WriteInt(upload, ref cursor, value.Corner);
                    WriteInt(upload, ref cursor, value.BlockId);
                }

                for (int index = 0; index < VoxelMath.IndicesPerFace; index++)
                {
                    int value = VoxelMath.IndexValue(vertexOffset, index);
                    indices[indexCount++] = value;
                    WriteInt(upload, ref cursor, value);
                }

                int faceBytes = VoxelMath.StageBytesForType(type);
                int end = checked(offset + faceBytes);
                while (cursor < end)
                {
                    upload[cursor++] = 0;
                }

                slices[emitted++] = new PayloadSlice(offset, faceBytes, type.Alignment, type.StageMask, record.BlockId, record.CellIndex);
                offset = end;
            }
        }

        digest = VoxelMath.DigestMaterializedOutput(
            digest,
            vertices.AsSpan(0, vertexCount),
            indices.AsSpan(0, indexCount),
            slices.AsSpan(0, faceCount),
            upload.AsSpan(0, stagedBytes));
        return new StreamResult(faceCount, vertexCount, indexCount, stagedBytes, enabledStageBytes);
    }

    private static OutputFixture CreateOutputFixture(
        Vertex[] opaqueVertices,
        int opaqueVertexCount,
        int[] opaqueIndices,
        int opaqueIndexCount,
        PayloadSlice[] opaqueSlices,
        int opaqueSliceCount,
        byte[] opaqueUpload,
        int opaqueUploadLength,
        Vertex[] transparentVertices,
        int transparentVertexCount,
        int[] transparentIndices,
        int transparentIndexCount,
        PayloadSlice[] transparentSlices,
        int transparentSliceCount,
        byte[] transparentUpload,
        int transparentUploadLength)
    {
        return new OutputFixture(
            opaqueVertices.AsSpan(0, Math.Min(opaqueVertexCount, VoxelMath.OutputFixtureElementLimit * VoxelMath.VerticesPerFace)).ToArray(),
            opaqueIndices.AsSpan(0, Math.Min(opaqueIndexCount, VoxelMath.OutputFixtureElementLimit * VoxelMath.IndicesPerFace)).ToArray(),
            opaqueSlices.AsSpan(0, Math.Min(opaqueSliceCount, VoxelMath.OutputFixtureElementLimit)).ToArray(),
            opaqueUpload.AsSpan(0, Math.Min(opaqueUploadLength, VoxelMath.OutputFixtureByteLimit)).ToArray(),
            transparentVertices.AsSpan(0, Math.Min(transparentVertexCount, VoxelMath.OutputFixtureElementLimit * VoxelMath.VerticesPerFace)).ToArray(),
            transparentIndices.AsSpan(0, Math.Min(transparentIndexCount, VoxelMath.OutputFixtureElementLimit * VoxelMath.IndicesPerFace)).ToArray(),
            transparentSlices.AsSpan(0, Math.Min(transparentSliceCount, VoxelMath.OutputFixtureElementLimit)).ToArray(),
            transparentUpload.AsSpan(0, Math.Min(transparentUploadLength, VoxelMath.OutputFixtureByteLimit)).ToArray());
    }

    private static void ReleaseStream(
        Vertex[] vertices,
        int[] indices,
        PayloadSlice[] slices,
        byte[] upload,
        ref Metrics metrics)
    {
        metrics.Leave(ArrayBytes(vertices));
        metrics.Leave(ArrayBytes(indices));
        metrics.Leave(ArrayBytes(slices));
        metrics.Leave(ArrayBytes(upload));
        Return(vertices);
        Return(indices);
        Return(slices);
        Return(upload);
    }

    private static T[] Rent<T>(int length, ref Metrics metrics, Stage stage)
    {
        T[] values = ArrayPool<T>.Shared.Rent(length);
        metrics.Rents++;
        metrics.Enter(ArrayBytes(values), stage);
        return values;
    }

    private static void Return<T>(T[] values) => ArrayPool<T>.Shared.Return(values, clearArray: true);

    private static long ElementSize<T>() =>
        typeof(T) == typeof(VoxelCell) ? VoxelMath.VoxelCellBytes :
        typeof(T) == typeof(FaceRecord) ? VoxelMath.FaceRecordBytes :
        typeof(T) == typeof(Vertex) ? VoxelMath.VertexBytes :
        typeof(T) == typeof(PayloadSlice) ? VoxelMath.PayloadSliceBytes :
        typeof(T) == typeof(ulong) ? 8 :
        typeof(T) == typeof(byte) ? 1 :
        typeof(T) == typeof(int) ? 4 :
        throw new InvalidOperationException($"No demo size registered for {typeof(T)}.");

    private static long ArrayBytes<T>(T[] values) => checked(values.LongLength * ElementSize<T>());

    private static void WriteInt(byte[] destination, ref int offset, int value)
    {
        destination[offset++] = unchecked((byte)value);
        destination[offset++] = unchecked((byte)(value >> 8));
        destination[offset++] = unchecked((byte)(value >> 16));
        destination[offset++] = unchecked((byte)(value >> 24));
    }
}
