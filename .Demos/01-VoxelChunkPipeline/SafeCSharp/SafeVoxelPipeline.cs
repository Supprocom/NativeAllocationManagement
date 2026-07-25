using System.Buffers;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SafeCSharp;

internal static class SafeVoxelPipeline
{
    private readonly record struct WorkerResult(PipelineResult Result, long PeakManagedBackingBytes);

    private struct Metrics
    {
        internal long CurrentManagedBackingBytes;
        internal long PeakManagedBackingBytes;
        internal long PeakCoordinateStageBytes;
        internal long PeakFaceStageBytes;
        internal long PeakPackingStageBytes;
        internal long RentCount;
        internal long RecycleCount;

        internal void Enter(long bytes, Stage stage)
        {
            CurrentManagedBackingBytes += bytes;
            if (CurrentManagedBackingBytes > PeakManagedBackingBytes)
            {
                PeakManagedBackingBytes = CurrentManagedBackingBytes;
            }

            switch (stage)
            {
                case Stage.Coordinates:
                    PeakCoordinateStageBytes = Math.Max(PeakCoordinateStageBytes, CurrentManagedBackingBytes);
                    break;
                case Stage.Faces:
                    PeakFaceStageBytes = Math.Max(PeakFaceStageBytes, CurrentManagedBackingBytes);
                    break;
                case Stage.Packing:
                    PeakPackingStageBytes = Math.Max(PeakPackingStageBytes, CurrentManagedBackingBytes);
                    break;
            }
        }

        internal void Leave(long bytes) => CurrentManagedBackingBytes -= bytes;

        internal void StageBoundary() => RecycleCount++;
    }

    private enum Stage
    {
        Coordinates,
        Faces,
        Packing
    }

    public static PipelineResult Run(VoxelWorkloadOptions options)
    {
        WorkerResult[] workers = new WorkerResult[options.WorkerCount];
        Task[] tasks = new Task[options.WorkerCount];
        for (int worker = 0; worker < options.WorkerCount; worker++)
        {
            int workerId = worker;
            tasks[worker] = Task.Run(() => workers[workerId] = RunWorker(options, workerId));
        }

        Task.WaitAll(tasks);
        long digest = 17;
        long chunks = 0;
        long visibleFaces = 0;
        long vertices = 0;
        long indices = 0;
        long stagedBytes = 0;
        long managedPayloadObjectBytes = 0;
        long peakManaged = 0;
        long peakCoordinates = 0;
        long peakFaces = 0;
        long peakPacking = 0;
        long rents = 0;
        long recycles = 0;
        long emptySections = 0;
        long uniformSections = 0;
        long expandedSections = 0;
        long packedSections = 0;
        long multiPackedSections = 0;
        long transparentMasks = 0;
        long dominantTransparent = 0;
        long residualTransparent = 0;
        for (int worker = 0; worker < workers.Length; worker++)
        {
            PipelineResult result = workers[worker].Result;
            digest = VoxelMath.DigestStep(digest, result.Digest);
            chunks += result.Chunks;
            visibleFaces += result.VisibleFaces;
            vertices += result.Vertices;
            indices += result.Indices;
            stagedBytes += result.StagedBytes;
            managedPayloadObjectBytes += result.ManagedPayloadObjectBytes;
            peakManaged = Math.Max(peakManaged, result.PeakManagedBackingBytes);
            peakCoordinates = Math.Max(peakCoordinates, result.PeakCoordinateStageBytes);
            peakFaces = Math.Max(peakFaces, result.PeakFaceStageBytes);
            peakPacking = Math.Max(peakPacking, result.PeakPackingStageBytes);
            rents += result.RentCount;
            recycles += result.ScopedRecycleCount;
            emptySections += result.EmptySections;
            uniformSections += result.UniformSections;
            expandedSections += result.ExpandedSections;
            packedSections += result.PackedSections;
            multiPackedSections += result.MultiPackedSections;
            transparentMasks += result.TransparentMaskCount;
            dominantTransparent += result.DominantTransparentSections;
            residualTransparent += result.ResidualTransparentSections;
        }

        return new PipelineResult(
            "SafeCSharp",
            digest,
            checked((int)chunks),
            visibleFaces,
            vertices,
            indices,
            stagedBytes,
            managedPayloadObjectBytes,
            peakManaged,
            0,
            0,
            0,
            peakCoordinates,
            peakFaces,
            peakPacking,
            rents,
            recycles,
            0,
            emptySections,
            uniformSections,
            expandedSections,
            packedSections,
            multiPackedSections,
            transparentMasks,
            dominantTransparent,
            residualTransparent);
    }

    private static WorkerResult RunWorker(VoxelWorkloadOptions options, int workerId)
    {
        Metrics metrics = default;
        long digest = 17;
        long chunks = 0;
        long visibleFaces = 0;
        long vertices = 0;
        long indices = 0;
        long stagedBytes = 0;
        long emptySections = 0;
        long uniformSections = 0;
        long expandedSections = 0;
        long packedSections = 0;
        long multiPackedSections = 0;
        long transparentMasks = 0;
        long dominantTransparent = 0;
        long residualTransparent = 0;
        int totalChunks = checked(options.ChunkCount * options.Iterations);
        for (int chunk = workerId; chunk < totalChunks; chunk += options.WorkerCount)
        {
            ChunkResult result = ProcessChunk(options, chunk, ref metrics, digest);
            digest = result.Digest;
            chunks++;
            visibleFaces += result.VisibleFaces;
            vertices += result.Vertices;
            indices += result.Indices;
            stagedBytes += result.StagedBytes;
            emptySections += result.EmptySections;
            uniformSections += result.UniformSections;
            expandedSections += result.ExpandedSections;
            packedSections += result.PackedSections;
            multiPackedSections += result.MultiPackedSections;
            transparentMasks += result.TransparentMaskCount;
            dominantTransparent += result.DominantTransparentSections;
            residualTransparent += result.ResidualTransparentSections;
        }

        return new WorkerResult(
            new PipelineResult(
                "SafeCSharp",
                digest,
                checked((int)chunks),
                visibleFaces,
                vertices,
                indices,
                stagedBytes,
                0,
                metrics.PeakManagedBackingBytes,
                0,
                0,
                0,
                metrics.PeakCoordinateStageBytes,
                metrics.PeakFaceStageBytes,
                metrics.PeakPackingStageBytes,
                metrics.RentCount,
                metrics.RecycleCount,
                0,
                emptySections,
                uniformSections,
                expandedSections,
                packedSections,
                multiPackedSections,
                transparentMasks,
                dominantTransparent,
                residualTransparent),
            metrics.PeakManagedBackingBytes);
    }

    private readonly record struct ChunkResult(
        long Digest,
        int VisibleFaces,
        int Vertices,
        int Indices,
        int StagedBytes,
        int EmptySections,
        int UniformSections,
        int ExpandedSections,
        int PackedSections,
        int MultiPackedSections,
        int TransparentMaskCount,
        int DominantTransparentSections,
        int ResidualTransparentSections);

    private static ChunkResult ProcessChunk(
        VoxelWorkloadOptions options,
        int chunk,
        ref Metrics metrics,
        long digest)
    {
        int cellCount = VoxelMath.CellsPerChunk;
        CellCoordinate[] coordinates = Rent<CellCoordinate>(cellCount, ref metrics, Stage.Coordinates);
        short[] densities = Rent<short>(cellCount, ref metrics, Stage.Coordinates);
        ushort[] materials = Rent<ushort>(cellCount, ref metrics, Stage.Coordinates);
        try
        {
            for (int cell = 0; cell < cellCount; cell++)
            {
                int x = cell % VoxelMath.ChunkDimension;
                int y = (cell / VoxelMath.ChunkDimension) % VoxelMath.ChunkDimension;
                int z = cell / (VoxelMath.ChunkDimension * VoxelMath.ChunkDimension);
                int blockId = VoxelMath.BlockIdForCell(options.Seed, chunk, x, y, z);
                coordinates[cell] = new CellCoordinate(x, y, z);
                densities[cell] = VoxelMath.DensityForCell(options.Seed, chunk, x, y, z, blockId);
                materials[cell] = checked((ushort)blockId);
                digest = VoxelMath.DigestStep(digest, x);
                digest = VoxelMath.DigestStep(digest, y);
                digest = VoxelMath.DigestStep(digest, z);
                digest = VoxelMath.DigestStep(digest, densities[cell]);
                digest = VoxelMath.DigestStep(digest, blockId);
            }

            FaceRecord[] faces = Rent<FaceRecord>(cellCount, ref metrics, Stage.Faces);
            ulong[]? transparentMaskStorage = null;
            try
            {
                int faceCellCount = 0;
                int visibleFaceCount = 0;
                for (int cell = 0; cell < cellCount; cell++)
                {
                    int mask = VoxelMath.FaceMaskFromManaged(cell, densities.AsSpan(0, cellCount), materials.AsSpan(0, cellCount));
                    digest = VoxelMath.DigestStep(digest, mask);
                    if (mask != 0)
                    {
                        faces[faceCellCount++] = new FaceRecord(cell, materials[cell], mask);
                        visibleFaceCount += VoxelMath.FaceCount(mask);
                    }
                }

                int emptySections = 0;
                int uniformSections = 0;
                int expandedSections = 0;
                int packedSections = 0;
                int multiPackedSections = 0;
                int transparentMasks = 0;
                int dominantTransparent = 0;
                int residualTransparent = 0;
                for (int section = 0; section < 64; section++)
                {
                    SectionSummary summary = VoxelMath.ClassifySection(materials.AsSpan(0, cellCount), densities.AsSpan(0, cellCount), section);
                    switch (summary.Kind)
                    {
                        case SectionRepresentationKind.Empty: emptySections++; break;
                        case SectionRepresentationKind.Uniform: uniformSections++; break;
                        case SectionRepresentationKind.Expanded: expandedSections++; break;
                        case SectionRepresentationKind.Packed: packedSections++; break;
                        case SectionRepresentationKind.MultiPacked: multiPackedSections++; break;
                    }

                    transparentMasks += summary.TransparentIds;
                    if (summary.HasDominantTransparentId) dominantTransparent++;
                    if (summary.HasResidualTransparentIds) residualTransparent++;
                }

                if (transparentMasks != 0)
                {
                    int maskWords = checked(transparentMasks * VoxelMath.TransparentMaskWordsPerId);
                    transparentMaskStorage = ArrayPool<ulong>.Shared.Rent(maskWords);
                    metrics.Enter(ArrayBytes(transparentMaskStorage), Stage.Faces);
                    Span<ulong> maskWordsSpan = transparentMaskStorage.AsSpan(0, maskWords);
                    maskWordsSpan.Clear();
                    int maskOffset = 0;
                    for (int section = 0; section < 64; section++)
                    {
                        SectionSummary summary = VoxelMath.ClassifySection(materials.AsSpan(0, cellCount), densities.AsSpan(0, cellCount), section);
                        int writtenIds = VoxelMath.BuildTransparentMasks(
                            materials.AsSpan(0, cellCount),
                            densities.AsSpan(0, cellCount),
                            section,
                            maskWordsSpan.Slice(maskOffset, checked(summary.TransparentIds * VoxelMath.TransparentMaskWordsPerId)));
                        if (writtenIds != summary.TransparentIds)
                        {
                            throw new InvalidDataException("The transparent mask builder disagreed with section classification.");
                        }

                        int writtenWords = checked(writtenIds * VoxelMath.TransparentMaskWordsPerId);
                        for (int word = 0; word < writtenWords; word++)
                        {
                            digest = VoxelMath.DigestStep(digest, unchecked((long)maskWordsSpan[maskOffset + word]));
                        }

                        maskOffset += writtenWords;
                    }

                    metrics.Leave(ArrayBytes(transparentMaskStorage));
                    ArrayPool<ulong>.Shared.Return(transparentMaskStorage, clearArray: false);
                    transparentMaskStorage = null;
                }

                metrics.Leave(ArrayBytes(coordinates));
                Return(coordinates);
                coordinates = Array.Empty<CellCoordinate>();
                metrics.Leave(ArrayBytes(densities));
                Return(densities);
                densities = Array.Empty<short>();
                metrics.Leave(ArrayBytes(materials));
                Return(materials);
                materials = Array.Empty<ushort>();
                metrics.StageBoundary();

                int outputVertexCount = checked(visibleFaceCount * VoxelMath.VerticesPerFace);
                int outputIndexCount = checked(visibleFaceCount * VoxelMath.IndicesPerFace);
                Vertex[] vertices = Rent<Vertex>(Math.Max(1, outputVertexCount), ref metrics, Stage.Packing);
                int[] indices = Rent<int>(Math.Max(1, outputIndexCount), ref metrics, Stage.Packing);
                PayloadSlice[] slices = Rent<PayloadSlice>(Math.Max(1, visibleFaceCount), ref metrics, Stage.Packing);
                byte[]? upload = null;
                try
                {
                    int vertexCount = 0;
                    int indexCount = 0;
                    int offset = 0;
                    int emittedFaceCount = 0;
                    for (int faceRecordIndex = 0; faceRecordIndex < faceCellCount; faceRecordIndex++)
                    {
                        FaceRecord record = faces[faceRecordIndex];
                        for (int face = 0; face < VoxelMath.FacesPerCell; face++)
                        {
                            if ((record.Mask & (1 << face)) == 0)
                            {
                                continue;
                            }

                            int vertexOffset = vertexCount;
                            for (int vertex = 0; vertex < VoxelMath.VerticesPerFace; vertex++)
                            {
                                vertices[vertexCount++] = new Vertex(
                                    record.CellIndex % VoxelMath.ChunkDimension,
                                    (record.CellIndex / VoxelMath.ChunkDimension) % VoxelMath.ChunkDimension,
                                    record.CellIndex / (VoxelMath.ChunkDimension * VoxelMath.ChunkDimension),
                                    face,
                                    record.BlockId);
                            }

                            for (int index = 0; index < VoxelMath.IndicesPerFace; index++)
                            {
                                indices[indexCount++] = VoxelMath.IndexValue(vertexOffset, index);
                            }

                            int blockBytes = VoxelMath.StageBytesForFace(record.BlockId);
                            BlockTypeDescriptor recordType = VoxelMath.BlockTypeForId(record.BlockId);
                            int alignment = recordType.Alignment;
                            offset = VoxelMath.AlignUp(offset, alignment);
                            slices[emittedFaceCount++] = new PayloadSlice(
                                offset,
                                blockBytes,
                                alignment,
                                recordType.StageMask,
                                record.BlockId,
                                record.CellIndex);
                            offset = checked(offset + blockBytes);
                        }
                    }

                    metrics.Leave(ArrayBytes(faces));
                    Return(faces);
                    faces = Array.Empty<FaceRecord>();
                    metrics.StageBoundary();

                    upload = Rent<byte>(Math.Max(1, offset), ref metrics, Stage.Packing);
                    for (int faceIndex = 0; faceIndex < visibleFaceCount; faceIndex++)
                    {
                        PayloadSlice slice = slices[faceIndex];
                        int cursor = slice.Offset;
                        BlockTypeDescriptor type = VoxelMath.BlockTypeForId(slice.BlockId);
                        for (int slot = 0; slot < type.PayloadBytes; slot++)
                        {
                            upload[cursor++] = checked((byte)VoxelMath.PayloadByte(options.Seed, slice.CellIndex, slice.BlockId, slot));
                        }

                        int vertexOffset = faceIndex * VoxelMath.VerticesPerFace;
                        for (int vertex = 0; vertex < VoxelMath.VerticesPerFace; vertex++)
                        {
                            Vertex value = vertices[vertexOffset + vertex];
                            WriteInt(upload, ref cursor, value.X);
                            WriteInt(upload, ref cursor, value.Y);
                            WriteInt(upload, ref cursor, value.Z);
                            WriteInt(upload, ref cursor, value.Face);
                            WriteInt(upload, ref cursor, value.BlockId);
                        }

                        int indexOffset = faceIndex * VoxelMath.IndicesPerFace;
                        for (int index = 0; index < VoxelMath.IndicesPerFace; index++)
                        {
                            WriteInt(upload, ref cursor, indices[indexOffset + index]);
                        }

                        int end = checked(slice.Offset + slice.Length);
                        while (cursor < end)
                        {
                            upload[cursor++] = 0;
                        }
                    }

                    digest = VoxelMath.DigestBytes(digest, upload.AsSpan(0, offset));
                    digest = VoxelMath.DigestStep(digest, visibleFaceCount);
                    digest = VoxelMath.DigestStep(digest, vertexCount);
                    digest = VoxelMath.DigestStep(digest, indexCount);
                    digest = VoxelMath.DigestStep(digest, offset);
                    return new ChunkResult(
                        digest,
                        visibleFaceCount,
                        vertexCount,
                        indexCount,
                        offset,
                        emptySections,
                        uniformSections,
                        expandedSections,
                        packedSections,
                        multiPackedSections,
                        transparentMasks,
                        dominantTransparent,
                        residualTransparent);
                }
                finally
                {
                    if (transparentMaskStorage is not null)
                    {
                        metrics.Leave(ArrayBytes(transparentMaskStorage));
                        ArrayPool<ulong>.Shared.Return(transparentMaskStorage, clearArray: false);
                    }

                    if (upload is not null)
                    {
                        metrics.Leave(ArrayBytes(upload));
                        Return(upload);
                    }

                    metrics.Leave(ArrayBytes(slices));
                    Return(slices);
                    metrics.Leave(ArrayBytes(indices));
                    Return(indices);
                    metrics.Leave(ArrayBytes(vertices));
                    Return(vertices);
                    metrics.StageBoundary();
                }
            }
            finally
            {
                if (faces.Length != 0)
                {
                    metrics.Leave(ArrayBytes(faces));
                    Return(faces);
                }
            }
        }
        finally
        {
            if (coordinates.Length != 0)
            {
                metrics.Leave(ArrayBytes(coordinates));
                Return(coordinates);
            }

            if (densities.Length != 0)
            {
                metrics.Leave(ArrayBytes(densities));
                Return(densities);
            }

            if (materials.Length != 0)
            {
                metrics.Leave(ArrayBytes(materials));
                Return(materials);
            }
        }
    }

    private static T[] Rent<T>(int length, ref Metrics metrics, Stage stage)
    {
        T[] values = ArrayPool<T>.Shared.Rent(length);
        metrics.RentCount++;
        metrics.Enter(checked(values.Length * ElementSize<T>()), stage);
        return values;
    }

    private static void Return<T>(T[] values) => ArrayPool<T>.Shared.Return(values, clearArray: false);

    private static int ElementSize<T>() =>
        typeof(T) == typeof(CellCoordinate) ? 12 :
        typeof(T) == typeof(FaceRecord) ? 12 :
        typeof(T) == typeof(Vertex) ? 20 :
        typeof(T) == typeof(PayloadSlice) ? 24 :
        typeof(T) == typeof(ulong) ? 8 :
        typeof(T) == typeof(short) ? 2 :
        typeof(T) == typeof(ushort) ? 2 :
        typeof(T) == typeof(byte) ? 1 :
        typeof(T) == typeof(int) ? 4 :
        throw new InvalidOperationException($"No contract size for {typeof(T)}.");

    private static long ArrayBytes<T>(T[] values) => checked((long)values.Length * ElementSize<T>());

    private static void WriteInt(byte[] destination, ref int offset, int value)
    {
        destination[offset++] = checked((byte)(value & 0xFF));
        destination[offset++] = checked((byte)((value >> 8) & 0xFF));
        destination[offset++] = checked((byte)((value >> 16) & 0xFF));
        destination[offset++] = checked((byte)((value >> 24) & 0xFF));
    }
}
