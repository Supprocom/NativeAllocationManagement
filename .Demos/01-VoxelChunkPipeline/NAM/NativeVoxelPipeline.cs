using System.Runtime.CompilerServices;
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
        int ResidualTransparentSections)
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
    }

    private sealed class WorkerContext
    {
        private readonly VoxelWorkloadOptions _options;
        private readonly NativeMemoryStatistics _baseline;
        private Work _work;
        private int _chunk;
        private long _peakCoordinateStageBytes;
        private long _peakFaceStageBytes;
        private long _peakPackingStageBytes;

        internal WorkerContext(VoxelWorkloadOptions options, NativeMemoryStatistics baseline)
        {
            _options = options;
            _baseline = baseline;
            FaceAction = ProcessFaces;
            MaskAction = ProcessMasks;
            OpaquePackAction = PackOpaque;
            TransparentPackAction = PackTransparent;
        }

        internal NativeLeaseTripleAction<VoxelCell, NativeFaceOutput, NativeFaceOutput> FaceAction { get; }

        internal NativeLeasePooledArenaAction<VoxelCell, ulong> MaskAction { get; }

        internal NativeLeaseFunc<NativeFaceOutput, int> OpaquePackAction { get; }

        internal NativeLeaseFunc<NativeFaceOutput, int> TransparentPackAction { get; }

        internal void BeginChunk(int chunk, long digest)
        {
            _chunk = chunk;
            _work = new Work { Digest = digest };
            _peakCoordinateStageBytes = 0;
            _peakFaceStageBytes = 0;
            _peakPackingStageBytes = 0;
        }

        internal int TransparentMaskCountForTest => _work.TransparentMaskCount;

        internal long PeakCoordinateStageBytes => _peakCoordinateStageBytes;

        internal long PeakFaceStageBytes => _peakFaceStageBytes;

        internal long PeakPackingStageBytes => _peakPackingStageBytes;

        internal void SetTransparentMaskWords(int words) => _work.TransparentMaskWords = words;

        internal void AddStagedBytes(int opaqueBytes, int transparentBytes)
        {
            _work.OpaqueStagedBytes = checked(_work.OpaqueStagedBytes + opaqueBytes);
            _work.TransparentStagedBytes = checked(_work.TransparentStagedBytes + transparentBytes);
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
                _work.ResidualTransparentSections);
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
            long cellBytes = checked((long)cells.Capacity * Unsafe.SizeOf<VoxelCell>());
            long faceBytes = checked((long)opaqueOutput.Capacity * Unsafe.SizeOf<NativeFaceOutput>());
            _peakCoordinateStageBytes = Math.Max(_peakCoordinateStageBytes, cellBytes);
            _peakFaceStageBytes = Math.Max(_peakFaceStageBytes, checked(cellBytes + faceBytes * 2));
            Span<VoxelCell> cellSpan = cells.AsSpan();
            Span<NativeFaceOutput> opaqueSpan = opaqueOutput.AsSpan();
            Span<NativeFaceOutput> transparentSpan = transparentOutput.AsSpan();
            int opaqueCount = 0;
            int transparentCount = 0;
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
                if (VoxelMath.IsOpaque(blockId))
                {
                    for (int face = 0; face < VoxelMath.FacesPerCell; face++)
                    {
                        if ((mask & (1 << face)) != 0)
                        {
                            opaqueSpan[opaqueCount++] = new NativeFaceOutput { CellIndex = cell, BlockId = blockId, Face = face };
                        }
                    }
                }
                else if (VoxelMath.IsTransparent(blockId))
                {
                    for (int face = 0; face < VoxelMath.FacesPerCell; face++)
                    {
                        if ((mask & (1 << face)) != 0)
                        {
                            transparentSpan[transparentCount++] = new NativeFaceOutput { CellIndex = cell, BlockId = blockId, Face = face };
                        }
                    }
                }
            }

            _work.OpaqueFaces = opaqueCount;
            _work.TransparentFaces = transparentCount;
            ProcessSections(cellSpan);
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
        }

        private int PackOpaque(scoped NativeLeaseView<NativeFaceOutput> view) => Pack(view, _work.OpaqueFaces);

        private int PackTransparent(scoped NativeLeaseView<NativeFaceOutput> view) => Pack(view, _work.TransparentFaces);

        private int Pack(scoped NativeLeaseView<NativeFaceOutput> view, int faceCount)
        {
            _peakPackingStageBytes = Math.Max(
                _peakPackingStageBytes,
                checked((long)view.Capacity * Unsafe.SizeOf<NativeFaceOutput>() * 2));
            ReadOnlySpan<NativeFaceOutput> outputs = view.AsSpan();
            Span<byte> staging = stackalloc byte[256];
            int totalBytes = 0;
            int enabled = 0;
            for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
            {
                NativeFaceOutput output = outputs[faceIndex];
                BlockTypeDescriptor type = VoxelMath.BlockTypeForId(output.BlockId);
                int alignedBytes = VoxelMath.AlignUp(totalBytes, type.Alignment);
                _work.Digest = VoxelMath.DigestZeroBytes(_work.Digest, alignedBytes - totalBytes);
                totalBytes = alignedBytes;
                staging.Clear();
                int cursor = 0;
                for (int slot = 0; slot < type.PayloadBytes; slot++)
                {
                    bool stageEnabled = (type.StageMask & (1 << (slot % 4))) != 0;
                    staging[cursor++] = stageEnabled
                        ? checked((byte)VoxelMath.PayloadByte(_options.Seed, output.CellIndex, output.BlockId, slot))
                        : (byte)0;
                    if (stageEnabled) enabled++;
                }

                for (int vertex = 0; vertex < VoxelMath.VerticesPerFace; vertex++)
                {
                    WriteInt(staging, ref cursor, VoxelMath.VertexValue(output.CellIndex, output.Face, vertex, 0, output.BlockId));
                    WriteInt(staging, ref cursor, VoxelMath.VertexValue(output.CellIndex, output.Face, vertex, 1, output.BlockId));
                    WriteInt(staging, ref cursor, VoxelMath.VertexValue(output.CellIndex, output.Face, vertex, 2, output.BlockId));
                    WriteInt(staging, ref cursor, output.Face);
                    WriteInt(staging, ref cursor, vertex);
                    WriteInt(staging, ref cursor, output.BlockId);
                }

                int vertexOffset = faceIndex * VoxelMath.VerticesPerFace;
                for (int index = 0; index < VoxelMath.IndicesPerFace; index++)
                {
                    WriteInt(staging, ref cursor, VoxelMath.IndexValue(vertexOffset, index));
                }

                int faceBytes = VoxelMath.StageBytesForFace(output.BlockId);
                _work.Digest = VoxelMath.DigestBytes(_work.Digest, staging.Slice(0, faceBytes));
                totalBytes = checked(totalBytes + faceBytes);
            }

            _work.EnabledStageBytes += enabled;
            return totalBytes;
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

    public static PipelineResult Run(VoxelWorkloadOptions options)
    {
        VoxelMath.ValidateBoundaryFixture();
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
        allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        gen0Before = GC.CollectionCount(0);
        gen1Before = GC.CollectionCount(1);
        gen2Before = GC.CollectionCount(2);
        Stopwatch stopwatch = Stopwatch.StartNew();
        start.Set();
        Task.WaitAll(tasks);
        stopwatch.Stop();

        NativeMemoryStatistics final = NativeMemoryDiagnostics.Snapshot();
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
        long reusedLeases = 0;
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
            reusedLeases += result.ReusedLeaseCount;
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
            reusedLeases,
            0,
            stopwatch.Elapsed.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocationBefore,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before);
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
        using NativePool<NativeFaceOutput> opaquePool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativePool<NativeFaceOutput> transparentPool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena heterogeneousArena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        WorkerContext context = new(options, baseline);
        int totalChunks = checked(options.ChunkCount * options.Iterations);
        int passes = VoxelWorkloadOptions.WarmupChunksPerWorker + 1;
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
        long reusedLeases = 0;
        long cleared = 0;
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
                // The fixed deterministic registry workload stays below one output
                // record per cell in each stream. Keep this native bound equal to
                // the expert baseline's rented FaceRecord capacity; the bounded span
                // still guards any workload-contract change that would exceed it.
                int faceCapacity = VoxelMath.CellsPerChunk;
                try
                {
                    {
                        scoped Pooled<NativeFaceOutput> opaque = opaquePool.LeaseScoped(faceCapacity);
                        rents++;
                        if (pass != 0) reusedLeases++;
                        try
                        {
                            {
                                scoped Pooled<NativeFaceOutput> transparent = transparentPool.LeaseScoped(faceCapacity);
                                rents++;
                                if (pass != 0) reusedLeases++;
                                try
                                {
                                    {
                                        scoped Pooled<VoxelCell> cells = cellPool.LeaseScoped(VoxelMath.CellsPerChunk);
                                        rents++;
                                        if (pass != 0) reusedLeases++;
                                        NativeLeaseOperations.Access(cells, opaque, transparent, context.FaceAction);
                                        int maskLength = checked(Math.Max(1, contextMaskCount(context) * VoxelMath.TransparentMaskWordsPerId));
                                        context.SetTransparentMaskWords(contextMaskCount(context) * VoxelMath.TransparentMaskWordsPerId);
                                        try
                                        {
                                            {
                                                scoped ArenaLease<ulong> nativeMasks = heterogeneousArena.ScratchScoped<ulong>(maskLength);
                                                rents++;
                                                if (pass != 0) reusedLeases++;
                                                NativeLeaseOperations.Access(cells, nativeMasks, context.MaskAction);
                                            }
                                        }
                                        finally
                                        {
                                            heterogeneousArena.RecycleScoped();
                                            recycles++;
                                        }
                                    }
                                }
                                finally
                                {
                                    cellPool.RecycleScoped();
                                    recycles++;
                                    cleared += checked((long)VoxelMath.CellsPerChunk * 24);
                                }

                                int opaqueBytes = opaque.Read(context.OpaquePackAction);
                                int transparentBytes = transparent.Read(context.TransparentPackAction);
                                context.AddStagedBytes(opaqueBytes, transparentBytes);
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
                                    opaqueStaged += opaqueBytes;
                                    transparentStaged += transparentBytes;
                                    enabledStageBytes += result.EnabledStageBytes;
                                }
                            }
                        }
                        finally
                        {
                            transparentPool.RecycleScoped();
                            recycles++;
                            cleared += checked((long)faceCapacity * 32);
                        }
                    }
                }
                finally
                {
                    opaquePool.RecycleScoped();
                    recycles++;
                    cleared += checked((long)faceCapacity * 32);
                }
            }

            if (pass == 0)
            {
                rents = 0;
                recycles = 0;
                reusedLeases = 0;
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

        NativeMemoryStatistics final = NativeMemoryDiagnostics.Snapshot();
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
            reusedLeases,
            0));
    }

    private static int contextMaskCount(WorkerContext context) => context.TransparentMaskCountForTest;
}
