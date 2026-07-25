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
        internal bool MeasureTimings;
        internal long GenerationTicks;
        internal long FaceDerivationTicks;
        internal long TransparentMaskTicks;
        internal long OpaquePackingTicks;
        internal long TransparentPackingTicks;
        internal long CoordinateRecycleTicks;
        internal long FaceRecycleTicks;
        internal long MaskRecycleTicks;
        internal long PackingRecycleTicks;

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

        internal void Boundary(long bytes, long elapsedTicks = 0, Stage stage = Stage.Coordinates)
        {
            Recycles++;
            ClearedBytes += bytes;
            if (!MeasureTimings)
            {
                return;
            }

            switch (stage)
            {
                case Stage.Coordinates: CoordinateRecycleTicks += elapsedTicks; break;
                case Stage.Faces: FaceRecycleTicks += elapsedTicks; break;
                case Stage.Packing: PackingRecycleTicks += elapsedTicks; break;
            }
        }

        internal void AddMaskRecycleTicks(long elapsedTicks)
        {
            if (MeasureTimings)
            {
                MaskRecycleTicks += elapsedTicks;
            }
        }

        internal void MaskBoundary(long bytes, long elapsedTicks)
        {
            Recycles++;
            ClearedBytes += bytes;
            AddMaskRecycleTicks(elapsedTicks);
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
        OutputFixture? MaterializedOutput,
        double GenerationMilliseconds = 0,
        double FaceDerivationMilliseconds = 0,
        double TransparentMaskMilliseconds = 0,
        double OpaquePackingMilliseconds = 0,
        double TransparentPackingMilliseconds = 0,
        double CoordinateRecycleMilliseconds = 0,
        double FaceRecycleMilliseconds = 0,
        double MaskRecycleMilliseconds = 0,
        double PackingRecycleMilliseconds = 0)
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

    public static PipelineResult Run(
        VoxelWorkloadOptions options,
        bool captureMeasuredFixture)
    {
        VoxelMath.ValidateBoundaryFixture();
        OutputFixture independentFixture = CreateIndependentFixture();
        WorkerResult[] workers = new WorkerResult[options.WorkerCount];
        Task[] tasks = new Task[options.WorkerCount];
        using CountdownEvent ready = new(options.WorkerCount);
        using ManualResetEventSlim start = new(false);
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
        double generationMilliseconds = 0;
        double faceDerivationMilliseconds = 0;
        double transparentMaskMilliseconds = 0;
        double opaquePackingMilliseconds = 0;
        double transparentPackingMilliseconds = 0;
        double coordinateRecycleMilliseconds = 0;
        double faceRecycleMilliseconds = 0;
        double maskRecycleMilliseconds = 0;
        double packingRecycleMilliseconds = 0;
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
            peakManaged += result.PeakManagedBackingBytes;
            coldPeakManaged += result.ColdManagedBackingBytes;
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
             rents,
             0,
             stopwatch.Elapsed.TotalMilliseconds,
             GC.GetTotalAllocatedBytes(precise: true) - allocationBefore,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            materializedOutput,
            coldPeakManaged,
            independentFixture,
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

    private static OutputFixture CreateIndependentFixture()
    {
        VoxelWorkloadOptions fixtureOptions = new(
            VoxelMath.IndependentFixtureSeed,
            ChunkCount: 1,
            WorkerCount: 1,
            Iterations: 1);
        long digest = 17;
        (int opaqueFaceCount, int opaqueStagedBytes) = MeasureRecords(VoxelMath.IndependentOpaqueRecords);
        (int transparentFaceCount, int transparentStagedBytes) = MeasureRecords(VoxelMath.IndependentTransparentRecords);
        Vertex[] opaqueVertices = new Vertex[Math.Max(1, checked(opaqueFaceCount * VoxelMath.VerticesPerFace))];
        int[] opaqueIndices = new int[Math.Max(1, checked(opaqueFaceCount * VoxelMath.IndicesPerFace))];
        PayloadSlice[] opaqueSlices = new PayloadSlice[Math.Max(1, opaqueFaceCount)];
        byte[] opaqueUpload = new byte[Math.Max(1, opaqueStagedBytes)];
        Vertex[] transparentVertices = new Vertex[Math.Max(1, checked(transparentFaceCount * VoxelMath.VerticesPerFace))];
        int[] transparentIndices = new int[Math.Max(1, checked(transparentFaceCount * VoxelMath.IndicesPerFace))];
        PayloadSlice[] transparentSlices = new PayloadSlice[Math.Max(1, transparentFaceCount)];
        byte[] transparentUpload = new byte[Math.Max(1, transparentStagedBytes)];
        StreamResult opaque = PackRange(
            fixtureOptions,
            VoxelMath.IndependentOpaqueRecords,
            opaqueVertices,
            opaqueIndices,
            opaqueSlices,
            opaqueUpload,
            opaqueFaceCount,
            opaqueStagedBytes,
            ref digest);
        StreamResult transparent = PackRange(
            fixtureOptions,
            VoxelMath.IndependentTransparentRecords,
            transparentVertices,
            transparentIndices,
            transparentSlices,
            transparentUpload,
            transparentFaceCount,
            transparentStagedBytes,
            ref digest);
        return new OutputFixture(
            opaqueVertices.AsSpan(0, opaque.VertexCount).ToArray(),
            opaqueIndices.AsSpan(0, opaque.IndexCount).ToArray(),
            opaqueSlices.AsSpan(0, opaque.FaceCount).ToArray(),
            opaqueUpload.AsSpan(0, opaque.StagedBytes).ToArray(),
            transparentVertices.AsSpan(0, transparent.VertexCount).ToArray(),
            transparentIndices.AsSpan(0, transparent.IndexCount).ToArray(),
            transparentSlices.AsSpan(0, transparent.FaceCount).ToArray(),
            transparentUpload.AsSpan(0, transparent.StagedBytes).ToArray());
    }

    private static WorkerResult RunWorker(
        VoxelWorkloadOptions options,
        int workerId,
        CountdownEvent ready,
        ManualResetEventSlim start,
        bool captureMeasuredFixture)
    {
        Metrics metrics = default;
        SectionSummary[] sectionSummaries = new SectionSummary[64];
        int totalChunks = checked(options.ChunkCount * options.Iterations);
        for (int warmup = 0; warmup < VoxelWorkloadOptions.WarmupChunksPerWorker; warmup++)
        {
            _ = ProcessChunk(
                options,
                checked(totalChunks + workerId + warmup),
                ref metrics,
                17,
                sectionSummaries,
                captureMeasuredFixture);
        }

        long coldPeakManaged = metrics.PeakBackingBytes;
        metrics = default;
        metrics.MeasureTimings = true;
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
        double generationMilliseconds = 0;
        double faceDerivationMilliseconds = 0;
        double transparentMaskMilliseconds = 0;
        double opaquePackingMilliseconds = 0;
        double transparentPackingMilliseconds = 0;
        double coordinateRecycleMilliseconds = 0;
        double faceRecycleMilliseconds = 0;
        double maskRecycleMilliseconds = 0;
        double packingRecycleMilliseconds = 0;
        OutputFixture? materializedOutput = null;
        for (int chunk = workerId; chunk < totalChunks; chunk += options.WorkerCount)
        {
            ChunkResult result = ProcessChunk(
                options,
                chunk,
                ref metrics,
                digest,
                sectionSummaries,
                captureMeasuredFixture);
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
             MeasuredLeaseCount: metrics.Rents,
             MaterializedOutput: materializedOutput,
            ColdManagedBackingBytes: coldPeakManaged,
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

    private static ChunkResult ProcessChunk(
        VoxelWorkloadOptions options,
        int chunk,
        ref Metrics metrics,
        long digest,
        SectionSummary[] sectionSummaries,
        bool captureMeasuredFixture)
    {
        long generationTicksBefore = metrics.GenerationTicks;
        long faceTicksBefore = metrics.FaceDerivationTicks;
        long maskTicksBefore = metrics.TransparentMaskTicks;
        long opaquePackingTicksBefore = metrics.OpaquePackingTicks;
        long transparentPackingTicksBefore = metrics.TransparentPackingTicks;
        long coordinateRecycleTicksBefore = metrics.CoordinateRecycleTicks;
        long faceRecycleTicksBefore = metrics.FaceRecycleTicks;
        long maskRecycleTicksBefore = metrics.MaskRecycleTicks;
        long packingRecycleTicksBefore = metrics.PackingRecycleTicks;
        ChunkResult completed = default;
        int cellCount = VoxelMath.CellsPerChunk;
        VoxelCell[] cells = Rent<VoxelCell>(cellCount, ref metrics, Stage.Coordinates);
        FaceRecord[] faces = Array.Empty<FaceRecord>();
        try
        {
            long generationStart = metrics.MeasureTimings ? Stopwatch.GetTimestamp() : 0;
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

            if (metrics.MeasureTimings)
            {
                metrics.GenerationTicks += Stopwatch.GetTimestamp() - generationStart;
            }

            int opaqueRecordCount = 0;
            int transparentRecordCount = 0;
            int opaqueFaceCount = 0;
            int transparentFaceCount = 0;
            int opaqueStagedBytes = 0;
            int transparentStagedBytes = 0;
            long faceStart = metrics.MeasureTimings ? Stopwatch.GetTimestamp() : 0;
            for (int cell = 0; cell < cellCount; cell++)
            {
                int mask = VoxelMath.FaceMaskFromCells(cell, cells.AsSpan(0, cellCount));
                int blockId = cells[cell].BlockId;
                cells[cell].FaceMask = mask;
                bool transparent = VoxelMath.TransparentById[blockId];
                bool occupied = blockId != VoxelMath.AirBlockId;
                cells[cell].OpaqueMask = occupied && !transparent ? mask : 0;
                cells[cell].TransparentMask = occupied && transparent ? mask : 0;
                digest = VoxelMath.DigestStep(digest, mask);
                if (mask == 0)
                {
                    continue;
                }

                BlockTypeDescriptor type = VoxelMath.BlockTypeForId(blockId);
                int faceCount = VoxelMath.FaceCount(mask);
                int bytes = checked(faceCount * VoxelMath.StageBytesForType(type));
                if (occupied && transparent)
                {
                    transparentRecordCount++;
                    transparentFaceCount += faceCount;
                    transparentStagedBytes = VoxelMath.AlignUp(transparentStagedBytes, type.Alignment);
                    transparentStagedBytes = checked(transparentStagedBytes + bytes);
                }
                else
                {
                    opaqueRecordCount++;
                    opaqueFaceCount += faceCount;
                    opaqueStagedBytes = VoxelMath.AlignUp(opaqueStagedBytes, type.Alignment);
                    opaqueStagedBytes = checked(opaqueStagedBytes + bytes);
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
                sectionSummaries[section] = summary;
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

            faces = Rent<FaceRecord>(
                Math.Max(1, checked(opaqueRecordCount + transparentRecordCount)),
                ref metrics,
                Stage.Faces);
            int opaqueRecordIndex = 0;
            int transparentRecordIndex = 0;
            for (int cell = 0; cell < cellCount; cell++)
            {
                int mask = cells[cell].FaceMask;
                if (mask == 0)
                {
                    continue;
                }

                FaceRecord record = VoxelMath.CreateFaceRecord(cell, cells[cell].BlockId, mask);
                if (VoxelMath.TransparentById[record.BlockId])
                {
                    faces[opaqueRecordCount + transparentRecordIndex++] = record;
                }
                else
                {
                    faces[opaqueRecordIndex++] = record;
                }
            }

            if (opaqueRecordIndex != opaqueRecordCount || transparentRecordIndex != transparentRecordCount)
            {
                throw new InvalidDataException("Managed face output population disagrees with managed face classification.");
            }

            if (metrics.MeasureTimings)
            {
                metrics.FaceDerivationTicks += Stopwatch.GetTimestamp() - faceStart;
            }

            ulong[]? masks = null;
            try
            {
                long maskStart = metrics.MeasureTimings ? Stopwatch.GetTimestamp() : 0;
                if (transparentMaskCount != 0)
                {
                    transparentMaskWords = checked(transparentMaskCount * VoxelMath.TransparentMaskWordsPerId);
                    masks = Rent<ulong>(transparentMaskWords, ref metrics, Stage.Faces);
                    Array.Clear(masks, 0, transparentMaskWords);
                    int maskOffset = 0;
                    for (int section = 0; section < 64; section++)
                    {
                        SectionSummary summary = sectionSummaries[section];
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

                if (metrics.MeasureTimings)
                {
                    metrics.TransparentMaskTicks += Stopwatch.GetTimestamp() - maskStart;
                }
            }
            finally
            {
                if (masks is not null)
                {
                    metrics.Leave(ArrayBytes(masks));
                    long maskRecycleStart = metrics.MeasureTimings ? Stopwatch.GetTimestamp() : 0;
                    Return(masks);
                    metrics.MaskBoundary(
                        ArrayBytes(masks),
                        metrics.MeasureTimings ? Stopwatch.GetTimestamp() - maskRecycleStart : 0);
                }
            }

            metrics.Leave(ArrayBytes(cells));
            long coordinateRecycleStart = metrics.MeasureTimings ? Stopwatch.GetTimestamp() : 0;
            Return(cells);
            cells = Array.Empty<VoxelCell>();
            metrics.Boundary(
                checked((long)cellCount * ElementSize<VoxelCell>()),
                metrics.MeasureTimings ? Stopwatch.GetTimestamp() - coordinateRecycleStart : 0,
                Stage.Coordinates);

            int opaqueVertexCount = checked(opaqueFaceCount * VoxelMath.VerticesPerFace);
            int transparentVertexCount = checked(transparentFaceCount * VoxelMath.VerticesPerFace);
            int opaqueIndexCount = checked(opaqueFaceCount * VoxelMath.IndicesPerFace);
            int transparentIndexCount = checked(transparentFaceCount * VoxelMath.IndicesPerFace);
            int totalVertexCount = Math.Max(1, checked(opaqueVertexCount + transparentVertexCount));
            int totalIndexCount = Math.Max(1, checked(opaqueIndexCount + transparentIndexCount));
            int totalFaceCount = Math.Max(1, checked(opaqueFaceCount + transparentFaceCount));
            int totalStagedBytes = Math.Max(1, checked(opaqueStagedBytes + transparentStagedBytes));
            Vertex[] vertices = Rent<Vertex>(totalVertexCount, ref metrics, Stage.Packing);
            int[] indices = Rent<int>(totalIndexCount, ref metrics, Stage.Packing);
            PayloadSlice[] slices = Rent<PayloadSlice>(totalFaceCount, ref metrics, Stage.Packing);
            byte[] upload = Rent<byte>(totalStagedBytes, ref metrics, Stage.Packing);
            try
            {
                long opaquePackingStart = metrics.MeasureTimings ? Stopwatch.GetTimestamp() : 0;
                StreamResult opaque = PackRange(
                    options,
                    faces.AsSpan(0, opaqueRecordCount),
                    vertices.AsSpan(0, opaqueVertexCount),
                    indices.AsSpan(0, opaqueIndexCount),
                    slices.AsSpan(0, opaqueFaceCount),
                    upload.AsSpan(0, opaqueStagedBytes),
                    opaqueFaceCount,
                    opaqueStagedBytes,
                    ref digest);
                if (metrics.MeasureTimings)
                {
                    metrics.OpaquePackingTicks += Stopwatch.GetTimestamp() - opaquePackingStart;
                }

                long transparentPackingStart = metrics.MeasureTimings ? Stopwatch.GetTimestamp() : 0;
                StreamResult transparent = PackRange(
                    options,
                    faces.AsSpan(opaqueRecordCount, transparentRecordCount),
                    vertices.AsSpan(opaqueVertexCount, transparentVertexCount),
                    indices.AsSpan(opaqueIndexCount, transparentIndexCount),
                    slices.AsSpan(opaqueFaceCount, transparentFaceCount),
                    upload.AsSpan(opaqueStagedBytes, transparentStagedBytes),
                    transparentFaceCount,
                    transparentStagedBytes,
                    ref digest);
                if (metrics.MeasureTimings)
                {
                    metrics.TransparentPackingTicks += Stopwatch.GetTimestamp() - transparentPackingStart;
                }

                digest = VoxelMath.DigestStep(digest, opaque.FaceCount);
                digest = VoxelMath.DigestStep(digest, transparent.FaceCount);
                digest = VoxelMath.DigestStep(digest, opaque.VertexCount);
                digest = VoxelMath.DigestStep(digest, transparent.VertexCount);
                digest = VoxelMath.DigestStep(digest, opaque.IndexCount);
                digest = VoxelMath.DigestStep(digest, transparent.IndexCount);
                digest = VoxelMath.DigestStep(digest, opaque.StagedBytes);
                digest = VoxelMath.DigestStep(digest, transparent.StagedBytes);
                digest = VoxelMath.DigestStep(digest, opaque.EnabledStageBytes + transparent.EnabledStageBytes);
                OutputFixture? fixture = captureMeasuredFixture && chunk == 0
                    ? CreateOutputFixture(
                        vertices.AsSpan(0, opaqueVertexCount),
                        opaque.VertexCount,
                        indices.AsSpan(0, opaqueIndexCount),
                        opaque.IndexCount,
                        slices.AsSpan(0, opaqueFaceCount),
                        opaque.FaceCount,
                        upload.AsSpan(0, opaqueStagedBytes),
                        opaque.StagedBytes,
                        vertices.AsSpan(opaqueVertexCount, transparentVertexCount),
                        transparent.VertexCount,
                        indices.AsSpan(opaqueIndexCount, transparentIndexCount),
                        transparent.IndexCount,
                        slices.AsSpan(opaqueFaceCount, transparentFaceCount),
                        transparent.FaceCount,
                        upload.AsSpan(opaqueStagedBytes, transparentStagedBytes),
                        transparent.StagedBytes)
                    : null;
                completed = new ChunkResult(
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
                long packingRecycleStart = metrics.MeasureTimings ? Stopwatch.GetTimestamp() : 0;
                metrics.Leave(ArrayBytes(vertices));
                metrics.Leave(ArrayBytes(indices));
                metrics.Leave(ArrayBytes(slices));
                metrics.Leave(ArrayBytes(upload));
                Return(vertices);
                Return(indices);
                Return(slices);
                Return(upload);
                metrics.Boundary(
                    checked(ArrayBytes(vertices) + ArrayBytes(indices) + ArrayBytes(slices) + ArrayBytes(upload)),
                    metrics.MeasureTimings ? Stopwatch.GetTimestamp() - packingRecycleStart : 0,
                    Stage.Packing);
            }
        }
        finally
        {
            if (faces.Length != 0)
            {
                metrics.Leave(ArrayBytes(faces));
                long faceRecycleStart = metrics.MeasureTimings ? Stopwatch.GetTimestamp() : 0;
                Return(faces);
                metrics.Boundary(
                    ArrayBytes(faces),
                    metrics.MeasureTimings ? Stopwatch.GetTimestamp() - faceRecycleStart : 0,
                    Stage.Faces);
            }

            if (cells.Length != 0)
            {
                metrics.Leave(ArrayBytes(cells));
                Return(cells);
            }
        }

        return completed with
        {
            GenerationMilliseconds = ToMilliseconds(metrics.GenerationTicks - generationTicksBefore),
            FaceDerivationMilliseconds = ToMilliseconds(metrics.FaceDerivationTicks - faceTicksBefore),
            TransparentMaskMilliseconds = ToMilliseconds(metrics.TransparentMaskTicks - maskTicksBefore),
            OpaquePackingMilliseconds = ToMilliseconds(metrics.OpaquePackingTicks - opaquePackingTicksBefore),
            TransparentPackingMilliseconds = ToMilliseconds(metrics.TransparentPackingTicks - transparentPackingTicksBefore),
            CoordinateRecycleMilliseconds = ToMilliseconds(metrics.CoordinateRecycleTicks - coordinateRecycleTicksBefore),
            FaceRecycleMilliseconds = ToMilliseconds(metrics.FaceRecycleTicks - faceRecycleTicksBefore),
            MaskRecycleMilliseconds = ToMilliseconds(metrics.MaskRecycleTicks - maskRecycleTicksBefore),
            PackingRecycleMilliseconds = ToMilliseconds(metrics.PackingRecycleTicks - packingRecycleTicksBefore)
        };
    }

    private static (int FaceCount, int StagedBytes) MeasureRecords(ReadOnlySpan<FaceRecord> records)
    {
        int faceCount = 0;
        int stagedBytes = 0;
        for (int recordIndex = 0; recordIndex < records.Length; recordIndex++)
        {
            ref readonly FaceRecord record = ref records[recordIndex];
            faceCount += VoxelMath.FaceCount(record.Mask);
            for (int face = 0; face < VoxelMath.FacesPerCell; face++)
            {
                if ((record.Mask & (1 << face)) != 0)
                {
                    stagedBytes = VoxelMath.AlignUp(stagedBytes, record.Alignment);
                    stagedBytes = checked(stagedBytes + record.StageBytes);
                }
            }
        }

        return (faceCount, stagedBytes);
    }

    private static StreamResult PackRange(
        VoxelWorkloadOptions options,
        ReadOnlySpan<FaceRecord> records,
        Span<Vertex> vertices,
        Span<int> indices,
        Span<PayloadSlice> slices,
        Span<byte> upload,
        int expectedFaceCount,
        int expectedStagedBytes,
        ref long digest)
    {
        int vertexCount = 0;
        int indexCount = 0;
        int emitted = 0;
        int offset = 0;
        int enabledStageBytes = 0;
        for (int recordIndex = 0; recordIndex < records.Length; recordIndex++)
        {
            ref readonly FaceRecord record = ref records[recordIndex];
            for (int face = 0; face < VoxelMath.FacesPerCell; face++)
            {
                if ((record.Mask & (1 << face)) == 0)
                {
                    continue;
                }

                int alignedOffset = VoxelMath.AlignUp(offset, record.Alignment);
                upload.Slice(offset, alignedOffset - offset).Clear();
                offset = alignedOffset;
                int cursor = offset;
                for (int slot = 0; slot < record.PayloadBytes; slot++)
                {
                    bool enabled = (record.StageMask & (1 << (slot % 4))) != 0;
                    upload[cursor++] = enabled
                        ? checked((byte)((options.Seed
                            + record.CellIndex * 11
                            + record.BlockId * 37
                            + slot * 17
                            + record.StageMask * 13) & 0xFF))
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

                int faceBytes = record.StageBytes;
                int end = checked(offset + faceBytes);
                while (cursor < end)
                {
                    upload[cursor++] = 0;
                }

                slices[emitted++] = new PayloadSlice(offset, faceBytes, record.Alignment, record.StageMask, record.BlockId, record.CellIndex);
                offset = end;
            }
        }

        if (emitted != expectedFaceCount || offset != expectedStagedBytes)
        {
            throw new InvalidDataException("Managed materialized output length disagrees with face classification.");
        }

        digest = VoxelMath.DigestMaterializedOutput(
            digest,
            vertices[..vertexCount],
            indices[..indexCount],
            slices[..expectedFaceCount],
            upload[..expectedStagedBytes]);
        return new StreamResult(expectedFaceCount, vertexCount, indexCount, expectedStagedBytes, enabledStageBytes);
    }

    private static OutputFixture CreateOutputFixture(
        ReadOnlySpan<Vertex> opaqueVertices,
        int opaqueVertexCount,
        ReadOnlySpan<int> opaqueIndices,
        int opaqueIndexCount,
        ReadOnlySpan<PayloadSlice> opaqueSlices,
        int opaqueSliceCount,
        ReadOnlySpan<byte> opaqueUpload,
        int opaqueUploadLength,
        ReadOnlySpan<Vertex> transparentVertices,
        int transparentVertexCount,
        ReadOnlySpan<int> transparentIndices,
        int transparentIndexCount,
        ReadOnlySpan<PayloadSlice> transparentSlices,
        int transparentSliceCount,
        ReadOnlySpan<byte> transparentUpload,
        int transparentUploadLength)
    {
        return new OutputFixture(
            opaqueVertices[..Math.Min(opaqueVertexCount, VoxelMath.OutputFixtureElementLimit * VoxelMath.VerticesPerFace)].ToArray(),
            opaqueIndices[..Math.Min(opaqueIndexCount, VoxelMath.OutputFixtureElementLimit * VoxelMath.IndicesPerFace)].ToArray(),
            opaqueSlices[..Math.Min(opaqueSliceCount, VoxelMath.OutputFixtureElementLimit)].ToArray(),
            opaqueUpload[..Math.Min(opaqueUploadLength, VoxelMath.OutputFixtureByteLimit)].ToArray(),
            transparentVertices[..Math.Min(transparentVertexCount, VoxelMath.OutputFixtureElementLimit * VoxelMath.VerticesPerFace)].ToArray(),
            transparentIndices[..Math.Min(transparentIndexCount, VoxelMath.OutputFixtureElementLimit * VoxelMath.IndicesPerFace)].ToArray(),
            transparentSlices[..Math.Min(transparentSliceCount, VoxelMath.OutputFixtureElementLimit)].ToArray(),
            transparentUpload[..Math.Min(transparentUploadLength, VoxelMath.OutputFixtureByteLimit)].ToArray());
    }

    private static T[] Rent<T>(int length, ref Metrics metrics, Stage stage)
    {
        T[] values = ArrayPool<T>.Shared.Rent(length);
        metrics.Rents++;
        metrics.Enter(ArrayBytes(values), stage);
        return values;
    }

    private static void Return<T>(T[] values) => ArrayPool<T>.Shared.Return(values, clearArray: false);

    private static double ToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

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

    private static void WriteInt(Span<byte> destination, ref int offset, int value)
    {
        destination[offset++] = unchecked((byte)value);
        destination[offset++] = unchecked((byte)(value >> 8));
        destination[offset++] = unchecked((byte)(value >> 16));
        destination[offset++] = unchecked((byte)(value >> 24));
    }
}
