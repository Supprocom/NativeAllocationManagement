using System.Buffers;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.NAM;

internal static class NativeVoxelPipeline
{
    private enum Stage
    {
        Coordinates,
        Faces,
        Packing
    }

    private enum NativeBucket
    {
        Coordinates,
        Densities,
        Materials,
        Faces,
        Vertices,
        Indices,
        Slices,
        Heterogeneous
    }

    private struct Metrics
    {
        internal long CurrentManagedBackingBytes;
        internal long PeakManagedBackingBytes;
        internal long PeakCoordinateStageBytes;
        internal long PeakFaceStageBytes;
        internal long PeakPackingStageBytes;
        internal long RentCount;
        internal long RecycleCount;
        internal long ClearedBytes;
        internal long PeakNativeBackingBytes;
        internal long PeakRetainedNativeBackingBytes;
        internal long CoordinatesRetained;
        internal long DensitiesRetained;
        internal long MaterialsRetained;
        internal long FacesRetained;
        internal long VerticesRetained;
        internal long IndicesRetained;
        internal long SlicesRetained;
        internal long HeterogeneousRetained;

        internal void Enter(long bytes, Stage stage)
        {
            CurrentManagedBackingBytes += bytes;
            PeakManagedBackingBytes = Math.Max(PeakManagedBackingBytes, CurrentManagedBackingBytes);
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

        internal void StageBoundary(long clearedBytes)
        {
            RecycleCount++;
            ClearedBytes += clearedBytes;
        }

        internal void ObserveNative(NativeBucket bucket, long bytes)
        {
            switch (bucket)
            {
                case NativeBucket.Coordinates: CoordinatesRetained = Math.Max(CoordinatesRetained, bytes); break;
                case NativeBucket.Densities: DensitiesRetained = Math.Max(DensitiesRetained, bytes); break;
                case NativeBucket.Materials: MaterialsRetained = Math.Max(MaterialsRetained, bytes); break;
                case NativeBucket.Faces: FacesRetained = Math.Max(FacesRetained, bytes); break;
                case NativeBucket.Vertices: VerticesRetained = Math.Max(VerticesRetained, bytes); break;
                case NativeBucket.Indices: IndicesRetained = Math.Max(IndicesRetained, bytes); break;
                case NativeBucket.Slices: SlicesRetained = Math.Max(SlicesRetained, bytes); break;
                case NativeBucket.Heterogeneous: HeterogeneousRetained = Math.Max(HeterogeneousRetained, bytes); break;
            }

            long total = checked(
                CoordinatesRetained + DensitiesRetained + MaterialsRetained + FacesRetained +
                VerticesRetained + IndicesRetained + SlicesRetained + HeterogeneousRetained);
            PeakNativeBackingBytes = Math.Max(PeakNativeBackingBytes, total);
            PeakRetainedNativeBackingBytes = Math.Max(PeakRetainedNativeBackingBytes, total);
        }
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
        long peakManaged = 0;
        long peakNative = 0;
        long peakRetainedNative = 0;
        long peakCoordinates = 0;
        long peakFaces = 0;
        long peakPacking = 0;
        long rents = 0;
        long recycles = 0;
        long clearedBytes = 0;
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
            peakManaged = Math.Max(peakManaged, result.PeakManagedBackingBytes);
            peakNative = Math.Max(peakNative, result.PeakNativeBackingBytes);
            peakRetainedNative = Math.Max(peakRetainedNative, result.PeakRetainedNativeBackingBytes);
            peakCoordinates = Math.Max(peakCoordinates, result.PeakCoordinateStageBytes);
            peakFaces = Math.Max(peakFaces, result.PeakFaceStageBytes);
            peakPacking = Math.Max(peakPacking, result.PeakPackingStageBytes);
            rents += result.RentCount;
            recycles += result.ScopedRecycleCount;
            clearedBytes += result.ClearedBytes;
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
            "NAM",
            digest,
            checked((int)chunks),
            visibleFaces,
            vertices,
            indices,
            stagedBytes,
            0,
            peakManaged,
            peakNative,
            peakRetainedNative,
            0,
            peakCoordinates,
            peakFaces,
            peakPacking,
            rents,
            recycles,
            clearedBytes,
            emptySections,
            uniformSections,
            expandedSections,
            packedSections,
            multiPackedSections,
            transparentMasks,
            dominantTransparent,
            residualTransparent);
    }

    private readonly record struct WorkerResult(PipelineResult Result);

    private static WorkerResult RunWorker(VoxelWorkloadOptions options, int workerId)
    {
        using NativePool<CellCoordinate> coordinatePool = new(
            initialCapacity: VoxelMath.CellsPerChunk,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<short> densityPool = new(
            initialCapacity: VoxelMath.CellsPerChunk,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<ushort> materialPool = new(
            initialCapacity: VoxelMath.CellsPerChunk,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<FaceRecord> facePool = new(
            initialCapacity: VoxelMath.CellsPerChunk,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<Vertex> vertexPool = new(
            initialCapacity: VoxelMath.CellsPerChunk,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<int> indexPool = new(
            initialCapacity: VoxelMath.CellsPerChunk,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<PayloadSlice> slicePool = new(
            initialCapacity: VoxelMath.CellsPerChunk,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena heterogeneousArena = new(
            preAllocateBytes: 1024 * 1024,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);

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
            int cellCount = VoxelMath.CellsPerChunk;
            CellCoordinate[] coordinates = Rent<CellCoordinate>(cellCount, ref metrics, Stage.Coordinates);
            short[] densities = Rent<short>(cellCount, ref metrics, Stage.Coordinates);
            ushort[] materials = Rent<ushort>(cellCount, ref metrics, Stage.Coordinates);
            FaceRecord[] faces = Rent<FaceRecord>(cellCount, ref metrics, Stage.Faces);
            ulong[]? transparentMaskStorage = null;
            Vertex[]? outputVertices = null;
            int[]? outputIndices = null;
            PayloadSlice[]? slices = null;
            byte[]? upload = null;
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

                int faceCellCount = 0;
                int visibleFaceCount = 0;
                int emptySectionCount = 0;
                int uniformSectionCount = 0;
                int expandedSectionCount = 0;
                int packedSectionCount = 0;
                int multiPackedSectionCount = 0;
                int transparentMaskCount = 0;
                int dominantTransparentCount = 0;
                int residualTransparentCount = 0;

                {
                    scoped Pooled<CellCoordinate> nativeCoordinates = coordinatePool.LeaseScoped(cellCount);
                    scoped Pooled<short> nativeDensities = densityPool.LeaseScoped(cellCount);
                    scoped Pooled<ushort> nativeMaterials = materialPool.LeaseScoped(cellCount);
                    metrics.RentCount += 3;
                    metrics.ObserveNative(NativeBucket.Coordinates, checked((long)nativeCoordinates.Capacity * 12));
                    metrics.ObserveNative(NativeBucket.Densities, checked((long)nativeDensities.Capacity * 2));
                    metrics.ObserveNative(NativeBucket.Materials, checked((long)nativeMaterials.Capacity * 2));
                    try
                    {
                        nativeCoordinates.CopyFrom(coordinates.AsSpan(0, cellCount));
                        nativeDensities.CopyFrom(densities.AsSpan(0, cellCount));
                        nativeMaterials.CopyFrom(materials.AsSpan(0, cellCount));
                        nativeCoordinates.CopyTo(coordinates.AsSpan(0, cellCount));
                        nativeDensities.CopyTo(densities.AsSpan(0, cellCount));
                        nativeMaterials.CopyTo(materials.AsSpan(0, cellCount));

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

                for (int section = 0; section < 64; section++)
                {
                            SectionSummary summary = VoxelMath.ClassifySection(materials.AsSpan(0, cellCount), densities.AsSpan(0, cellCount), section);
                            switch (summary.Kind)
                            {
                                case SectionRepresentationKind.Empty: emptySectionCount++; break;
                                case SectionRepresentationKind.Uniform: uniformSectionCount++; break;
                                case SectionRepresentationKind.Expanded: expandedSectionCount++; break;
                                case SectionRepresentationKind.Packed: packedSectionCount++; break;
                                case SectionRepresentationKind.MultiPacked: multiPackedSectionCount++; break;
                            }

                            transparentMaskCount += summary.TransparentIds;
                            if (summary.HasDominantTransparentId) dominantTransparentCount++;
                    if (summary.HasResidualTransparentIds) residualTransparentCount++;
                }

                if (transparentMaskCount != 0)
                {
                    int maskWords = checked(transparentMaskCount * VoxelMath.TransparentMaskWordsPerId);
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
                }
                    }
                    finally
                    {
                        try
                        {
                            nativeMaterials.Dispose();
                            nativeDensities.Dispose();
                            nativeCoordinates.Dispose();
                        }
                        finally
                        {
                            materialPool.RecycleScoped();
                            densityPool.RecycleScoped();
                            coordinatePool.RecycleScoped();
                            metrics.StageBoundary(checked((long)cellCount * (12 + 2 + 2)));
                        }
                    }
                }

                metrics.Leave(ArrayBytes(coordinates));
                ArrayPool<CellCoordinate>.Shared.Return(coordinates, clearArray: false);
                coordinates = Array.Empty<CellCoordinate>();
                metrics.Leave(ArrayBytes(densities));
                ArrayPool<short>.Shared.Return(densities, clearArray: false);
                densities = Array.Empty<short>();
                metrics.Leave(ArrayBytes(materials));
                ArrayPool<ushort>.Shared.Return(materials, clearArray: false);
                materials = Array.Empty<ushort>();

                if (transparentMaskStorage is not null)
                {
                    int maskWords = checked(transparentMaskCount * VoxelMath.TransparentMaskWordsPerId);
                    {
                        scoped ArenaLease<ulong> nativeMasks = heterogeneousArena.ScratchScoped<ulong>(maskWords);
                        metrics.RentCount++;
                        metrics.ObserveNative(NativeBucket.Heterogeneous, checked((long)nativeMasks.Capacity * sizeof(ulong)));
                        nativeMasks.CopyFrom(transparentMaskStorage.AsSpan(0, maskWords));
                        nativeMasks.CopyTo(transparentMaskStorage.AsSpan(0, maskWords));
                    }

                    heterogeneousArena.RecycleScoped();
                    metrics.StageBoundary(checked((long)maskWords * sizeof(ulong)));
                    metrics.Leave(ArrayBytes(transparentMaskStorage));
                    ArrayPool<ulong>.Shared.Return(transparentMaskStorage, clearArray: false);
                    transparentMaskStorage = null;
                }

                int outputVertexCount = checked(visibleFaceCount * VoxelMath.VerticesPerFace);
                int outputIndexCount = checked(visibleFaceCount * VoxelMath.IndicesPerFace);
                outputVertices = Rent<Vertex>(Math.Max(1, outputVertexCount), ref metrics, Stage.Packing);
                outputIndices = Rent<int>(Math.Max(1, outputIndexCount), ref metrics, Stage.Packing);
                slices = Rent<PayloadSlice>(Math.Max(1, visibleFaceCount), ref metrics, Stage.Packing);
                int vertexCount = 0;
                int indexCount = 0;
                int offset = 0;
                int emittedFaceCount = 0;

                {
                    scoped Pooled<FaceRecord> nativeFaces = facePool.LeaseScoped(Math.Max(1, faceCellCount));
                    metrics.RentCount++;
                    metrics.ObserveNative(NativeBucket.Faces, checked((long)nativeFaces.Capacity * 12));
                    try
                    {
                        if (faceCellCount == 0)
                        {
                            faces[0] = default;
                            nativeFaces.CopyFrom(faces.AsSpan(0, 1));
                            nativeFaces.CopyTo(faces.AsSpan(0, 1));
                        }
                        else
                        {
                            nativeFaces.CopyFrom(faces.AsSpan(0, faceCellCount));
                            nativeFaces.CopyTo(faces.AsSpan(0, faceCellCount));
                        }

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
                                    outputVertices[vertexCount++] = new Vertex(
                                        record.CellIndex % VoxelMath.ChunkDimension,
                                        (record.CellIndex / VoxelMath.ChunkDimension) % VoxelMath.ChunkDimension,
                                        record.CellIndex / (VoxelMath.ChunkDimension * VoxelMath.ChunkDimension),
                                        face,
                                        record.BlockId);
                                }

                                for (int index = 0; index < VoxelMath.IndicesPerFace; index++)
                                {
                                    outputIndices[indexCount++] = VoxelMath.IndexValue(vertexOffset, index);
                                }

                                BlockTypeDescriptor type = VoxelMath.BlockTypeForId(record.BlockId);
                                int blockBytes = VoxelMath.StageBytesForFace(record.BlockId);
                                offset = VoxelMath.AlignUp(offset, type.Alignment);
                                slices[emittedFaceCount++] = new PayloadSlice(
                                    offset,
                                    blockBytes,
                                    type.Alignment,
                                    type.StageMask,
                                    record.BlockId,
                                    record.CellIndex);
                                offset = checked(offset + blockBytes);
                            }
                        }
                    }
                    finally
                    {
                        try
                        {
                            nativeFaces.Dispose();
                        }
                        finally
                        {
                            facePool.RecycleScoped();
                            metrics.StageBoundary(checked((long)Math.Max(1, faceCellCount) * 12));
                        }
                    }
                }

                metrics.Leave(ArrayBytes(faces));
                ArrayPool<FaceRecord>.Shared.Return(faces, clearArray: false);
                faces = Array.Empty<FaceRecord>();

                upload = Rent<byte>(Math.Max(1, offset), ref metrics, Stage.Packing);
                {
                    scoped Pooled<Vertex> nativeVertices = vertexPool.LeaseScoped(Math.Max(1, outputVertexCount));
                    scoped Pooled<int> nativeIndices = indexPool.LeaseScoped(Math.Max(1, outputIndexCount));
                    scoped Pooled<PayloadSlice> nativeSlices = slicePool.LeaseScoped(Math.Max(1, visibleFaceCount));
                    metrics.RentCount += 3;
                    metrics.ObserveNative(NativeBucket.Vertices, checked((long)nativeVertices.Capacity * 20));
                    metrics.ObserveNative(NativeBucket.Indices, checked((long)nativeIndices.Capacity * 4));
                    metrics.ObserveNative(NativeBucket.Slices, checked((long)nativeSlices.Capacity * 24));
                    try
                    {
                        nativeVertices.CopyFrom(outputVertices.AsSpan(0, Math.Max(1, outputVertexCount)));
                        nativeIndices.CopyFrom(outputIndices.AsSpan(0, Math.Max(1, outputIndexCount)));
                        nativeSlices.CopyFrom(slices.AsSpan(0, Math.Max(1, visibleFaceCount)));
                        nativeVertices.CopyTo(outputVertices.AsSpan(0, Math.Max(1, outputVertexCount)));
                        nativeIndices.CopyTo(outputIndices.AsSpan(0, Math.Max(1, outputIndexCount)));
                        nativeSlices.CopyTo(slices.AsSpan(0, Math.Max(1, visibleFaceCount)));

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
                                Vertex value = outputVertices[vertexOffset + vertex];
                                WriteInt(upload, ref cursor, value.X);
                                WriteInt(upload, ref cursor, value.Y);
                                WriteInt(upload, ref cursor, value.Z);
                                WriteInt(upload, ref cursor, value.Face);
                                WriteInt(upload, ref cursor, value.BlockId);
                            }

                            int indexOffset = faceIndex * VoxelMath.IndicesPerFace;
                            for (int index = 0; index < VoxelMath.IndicesPerFace; index++)
                            {
                                WriteInt(upload, ref cursor, outputIndices[indexOffset + index]);
                            }

                            int end = checked(slice.Offset + slice.Length);
                            while (cursor < end)
                            {
                                upload[cursor++] = 0;
                            }
                        }

                        {
                            scoped ArenaLease<byte> staging = heterogeneousArena.ScratchScoped<byte>(offset);
                            metrics.RentCount++;
                            metrics.ObserveNative(NativeBucket.Heterogeneous, checked((long)staging.Capacity));
                            staging.CopyFrom(upload.AsSpan(0, offset));
                            staging.CopyTo(upload.AsSpan(0, offset));
                        }
                        heterogeneousArena.RecycleScoped();
                        metrics.StageBoundary(offset);

                        digest = VoxelMath.DigestBytes(digest, upload.AsSpan(0, offset));
                        digest = VoxelMath.DigestStep(digest, visibleFaceCount);
                        digest = VoxelMath.DigestStep(digest, vertexCount);
                        digest = VoxelMath.DigestStep(digest, indexCount);
                        digest = VoxelMath.DigestStep(digest, offset);
                    }
                    finally
                    {
                        try
                        {
                            nativeSlices.Dispose();
                            nativeIndices.Dispose();
                            nativeVertices.Dispose();
                        }
                        finally
                        {
                            slicePool.RecycleScoped();
                            indexPool.RecycleScoped();
                            vertexPool.RecycleScoped();
                            metrics.StageBoundary(checked((long)Math.Max(1, outputVertexCount) * 20 + Math.Max(1, outputIndexCount) * 4 + Math.Max(1, visibleFaceCount) * 24));
                        }
                    }
                }

                ChunkResult chunkResult = new(
                    digest,
                    visibleFaceCount,
                    vertexCount,
                    indexCount,
                    offset,
                    emptySectionCount,
                    uniformSectionCount,
                    expandedSectionCount,
                    packedSectionCount,
                    multiPackedSectionCount,
                    transparentMaskCount,
                    dominantTransparentCount,
                    residualTransparentCount);
                digest = chunkResult.Digest;
                chunks++;
                visibleFaces += chunkResult.VisibleFaces;
                vertices += chunkResult.Vertices;
                indices += chunkResult.Indices;
                stagedBytes += chunkResult.StagedBytes;
                emptySections += chunkResult.EmptySections;
                uniformSections += chunkResult.UniformSections;
                expandedSections += chunkResult.ExpandedSections;
                packedSections += chunkResult.PackedSections;
                multiPackedSections += chunkResult.MultiPackedSections;
                transparentMasks += chunkResult.TransparentMaskCount;
                dominantTransparent += chunkResult.DominantTransparentSections;
                residualTransparent += chunkResult.ResidualTransparentSections;
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
                    ArrayPool<byte>.Shared.Return(upload, clearArray: false);
                }

                if (slices is not null)
                {
                    metrics.Leave(ArrayBytes(slices));
                    ArrayPool<PayloadSlice>.Shared.Return(slices, clearArray: false);
                }

                if (outputIndices is not null)
                {
                    metrics.Leave(ArrayBytes(outputIndices));
                    ArrayPool<int>.Shared.Return(outputIndices, clearArray: false);
                }

                if (outputVertices is not null)
                {
                    metrics.Leave(ArrayBytes(outputVertices));
                    ArrayPool<Vertex>.Shared.Return(outputVertices, clearArray: false);
                }

                metrics.Leave(ArrayBytes(faces));
                ArrayPool<FaceRecord>.Shared.Return(faces, clearArray: false);
                metrics.Leave(ArrayBytes(coordinates));
                ArrayPool<CellCoordinate>.Shared.Return(coordinates, clearArray: false);
                metrics.Leave(ArrayBytes(densities));
                ArrayPool<short>.Shared.Return(densities, clearArray: false);
                metrics.Leave(ArrayBytes(materials));
                ArrayPool<ushort>.Shared.Return(materials, clearArray: false);
            }
        }

        return new WorkerResult(new PipelineResult(
            "NAM",
            digest,
            checked((int)chunks),
            visibleFaces,
            vertices,
            indices,
            stagedBytes,
            0,
            metrics.PeakManagedBackingBytes,
            metrics.PeakNativeBackingBytes,
            metrics.PeakRetainedNativeBackingBytes,
            0,
            metrics.PeakCoordinateStageBytes,
            metrics.PeakFaceStageBytes,
            metrics.PeakPackingStageBytes,
            metrics.RentCount,
            metrics.RecycleCount,
            metrics.ClearedBytes,
            emptySections,
            uniformSections,
            expandedSections,
            packedSections,
            multiPackedSections,
            transparentMasks,
            dominantTransparent,
            residualTransparent));
    }

    private static T[] Rent<T>(int length, ref Metrics metrics, Stage stage)
    {
        T[] values = ArrayPool<T>.Shared.Rent(length);
        metrics.Enter(checked((long)values.Length * ElementSize<T>()), stage);
        return values;
    }

    private static long ArrayBytes<T>(T[] values) => checked((long)values.Length * ElementSize<T>());

    private static int ElementSize<T>() =>
        typeof(T) == typeof(CellCoordinate) ? 12 :
        typeof(T) == typeof(short) ? 2 :
        typeof(T) == typeof(ushort) ? 2 :
        typeof(T) == typeof(FaceRecord) ? 12 :
        typeof(T) == typeof(Vertex) ? 20 :
        typeof(T) == typeof(PayloadSlice) ? 24 :
        typeof(T) == typeof(ulong) ? 8 :
        typeof(T) == typeof(int) ? 4 :
        typeof(T) == typeof(byte) ? 1 :
        throw new InvalidOperationException($"No contract size for {typeof(T)}.");

    private static void WriteInt(byte[] destination, ref int offset, int value)
    {
        destination[offset++] = checked((byte)(value & 0xFF));
        destination[offset++] = checked((byte)((value >> 8) & 0xFF));
        destination[offset++] = checked((byte)((value >> 16) & 0xFF));
        destination[offset++] = checked((byte)((value >> 24) & 0xFF));
    }
}
