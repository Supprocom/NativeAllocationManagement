using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SafeCSharp;

internal sealed class SafePressureSession :
    IPressureProfileSession,
    IPressureWorkerCapacityPlanner
{
    private const int TransientArraysPerSizeClass =
        PressureWorkContract.CanonicalRetainedArraysPerPoolBucket;
    private const int BatchCellCapacity =
        PressureWorkContract.CanonicalResidentCellCapacity;
    private const int BatchMaskCapacity =
        PressureWorkContract.CanonicalResidentTransparentMaskWordCapacity;
    private readonly TrackedArrayPool<VoxelCell> _cells = new(
        BatchCellCapacity,
        maxArraysPerBucket: TransientArraysPerSizeClass);
    private readonly TrackedArrayPool<FaceRecord> _faces = new(
        checked(
            PressureWorkContract.CanonicalResidentFaceRecordCapacity),
        maxArraysPerBucket: TransientArraysPerSizeClass);
    private readonly TrackedArrayPool<ulong> _masks = new(
        BatchMaskCapacity,
        maxArraysPerBucket: TransientArraysPerSizeClass);
    private readonly ManagedArrayCacheBudget _transientBudget = new();
    private readonly BudgetedArrayPool<Vertex> _vertices;
    private readonly BudgetedArrayPool<int> _indices;
    private readonly TrackedArrayPool<SectionPrerenderDescriptor>
        _sectionDescriptors = new(
            PressureWorkContract.CanonicalResidentSectionDescriptorCapacity,
            maxArraysPerBucket: TransientArraysPerSizeClass);
    private readonly BudgetedArrayPool<ushort> _sectionValues;
    private readonly BudgetedArrayPool<uint> _sectionWords;
    private readonly BudgetedArrayPool<ulong> _sectionStates;
    private readonly BudgetedArrayPool<PayloadSlice> _slices;
    private readonly BudgetedArrayPool<GpuStage160> _stage160;
    private readonly BudgetedArrayPool<GpuStage168> _stage168;
    private readonly BudgetedArrayPool<GpuStage176> _stage176;
    private readonly BudgetedArrayPool<GpuStage192> _stage192;
    private readonly BudgetedArrayPool<GpuStage224> _stage224;
    private readonly SectionSummary[] _sections = new SectionSummary[
        PressureWorkContract.DefaultRetentionDepth * VoxelMath.SectionsPerChunk];
    private readonly BatchSlot[] _slots = new BatchSlot[
        PressureWorkContract.DefaultRetentionDepth];
    private SafeCapacityPlan? _capacityPlan;
    private MappedGpuBuffer? _mappedUpload;
    private UnmanagedMemoryStream? _mappedUploadStream;
    private long _fixedRetainedBytes;

    internal SafePressureSession()
    {
        _vertices = new(_transientBudget);
        _indices = new(_transientBudget);
        _sectionValues = new(_transientBudget);
        _sectionWords = new(_transientBudget);
        _sectionStates = new(_transientBudget);
        _slices = new(_transientBudget);
        _stage160 = new(_transientBudget);
        _stage168 = new(_transientBudget);
        _stage176 = new(_transientBudget);
        _stage192 = new(_transientBudget);
        _stage224 = new(_transientBudget);
    }

    public string Implementation => "SafeCSharp";

    public PressureWorkerCapacity PlanWorkerCapacity(
        PressureProfileRequest request)
    {
        return SafeCapacityPlan.EstimateWorkerCapacity(
            request,
            request.RetentionDepth);
    }

    public PressureProfileResult Run(
        PressureProfileRequest request,
        Action<PressureProgress> reportProgress)
    {
        bool exactVerification =
            request.ExecutionMode == PressureExecutionMode.Verification;
        if (request.RetentionDepth > PressureWorkContract.DefaultRetentionDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"The predeclared managed batch supports at most "
                + $"{PressureWorkContract.DefaultRetentionDepth} chunks.");
        }

        PressureRuntimeSnapshot before = PressureRuntimeSnapshot.Capture();
        SafeAdmissionPlan admission = EnsureCapacityPlan(
            request,
            before);
        UnmanagedMemoryStream mappedUpload =
            _mappedUploadStream
            ?? throw new InvalidOperationException(
                "The managed GPU upload range is not ready.");
        List<PressureChunkEvidence>? evidence = exactVerification
            ? new List<PressureChunkEvidence>(
                request.PlannedChunkCount)
            : null;
        long realizedDemand = 0;
        long completedLogicalBytes = 0;
        long sourceInputBytes = 0;
        long peakLiveLogicalBytes = 0;
        int peakBatchCount = 0;
        int lastChunkId = -1;
        VoxelPipelineStage lastStage = VoxelPipelineStage.None;
        Exception? failure = null;
        PressureProfileOutcome outcome = PressureProfileOutcome.Completed;
        int minimumWarmupChunks =
            request.Warmup
                && !request.HasPlannedChunks
            ? request.RetentionDepth
            : 0;
        int builtChunks = 0;
        int admissionThrottleCount = 0;

        reportProgress(new PressureProgress(
            Implementation,
            request.ProfilePercent,
            PressureProgressKind.ProcessingReady,
            0,
            0,
            VoxelPipelineStage.None,
            -1));
        try
        {
            while (request.NeedsChunk(
                builtChunks,
                realizedDemand,
                minimumWarmupChunks))
            {
                VoxelCell[] cells = _cells.Rent(
                    checked(
                        admission.RetentionDepth
                        * VoxelMath.CellsPerChunk));
                FaceRecord[]? faces = null;
                ulong[]? masks = null;
                SectionPrerenderDescriptor[]? sectionDescriptors = null;
                ushort[]? sectionValues = null;
                uint[]? sectionWords = null;
                ulong[]? sectionStates = null;
                Vertex[]? vertices = null;
                int[]? indices = null;
                PayloadSlice[]? slices = null;
                GpuStage160[]? stage160 = null;
                GpuStage168[]? stage168 = null;
                GpuStage176[]? stage176 = null;
                GpuStage192[]? stage192 = null;
                GpuStage224[]? stage224 = null;
                try
                {
                    int batchCount = 0;
                    int totalRecords = 0;
                    int totalMaskWords = 0;
                    int totalFaces = 0;
                    int totalUploadBytes = 0;
                    int totalSectionDescriptors = 0;
                    int totalSectionValues = 0;
                    int totalSectionWords = 0;
                    int totalSectionStateWords = 0;
                    int totalVertices = 0;
                    int totalIndices = 0;
                    int totalStage160 = 0;
                    int totalStage168 = 0;
                    int totalStage176 = 0;
                    int totalStage192 = 0;
                    int totalStage224 = 0;
                    long sectionStorageBytes = 0;
                    long outputStorageBytes = 0;
                    lastStage = VoxelPipelineStage.Build;
                    while (batchCount < admission.RetentionDepth
                        && request.NeedsChunk(
                            builtChunks,
                            realizedDemand,
                            minimumWarmupChunks))
                    {
                        int chunkId =
                            request.GetChunkId(builtChunks);
                        Span<VoxelCell> chunkCells = cells.AsSpan(
                            checked(batchCount * VoxelMath.CellsPerChunk),
                            VoxelMath.CellsPerChunk);
                        Span<SectionSummary> chunkSections = _sections.AsSpan(
                            checked(batchCount * VoxelMath.SectionsPerChunk),
                            VoxelMath.SectionsPerChunk);
                        PressureWorkContract.GenerateCells(
                            request.Seed,
                            chunkId,
                            chunkCells);
                        PressureChunkShape shape = PressureWorkContract.DeriveChunkShape(
                            chunkCells,
                            chunkSections);
                        long demand =
                            PressureWorkContract.CalculateLogicalDemand(shape);
                        int candidateRecords = checked(
                            totalRecords + Math.Max(1, shape.RecordCount));
                        int candidateMaskWords = checked(
                            totalMaskWords
                            + Math.Max(1, shape.TransparentMaskWords));
                        int candidateFaces = checked(
                            totalFaces + Math.Max(1, shape.FaceCount));
                        int candidateUploadBytes = checked(
                            totalUploadBytes
                            + Math.Max(1, shape.UploadBytes));
                        int candidateSectionDescriptors = checked(
                            totalSectionDescriptors
                            + shape.SectionDescriptorCount);
                        int candidateSectionValues = checked(
                            totalSectionValues + shape.SectionValueCount);
                        int candidateSectionWords = checked(
                            totalSectionWords + shape.SectionWordCount);
                        int candidateSectionStateWords = checked(
                            totalSectionStateWords
                            + shape.SectionStateWordCount);
                        int candidateVertices = checked(
                            totalVertices + shape.VertexCount);
                        int candidateIndices = checked(
                            totalIndices + shape.IndexCount);
                        int candidateStage160 = checked(
                            totalStage160
                            + shape.GpuStages.Stage160Count);
                        int candidateStage168 = checked(
                            totalStage168
                            + shape.GpuStages.Stage168Count);
                        int candidateStage176 = checked(
                            totalStage176
                            + shape.GpuStages.Stage176Count);
                        int candidateStage192 = checked(
                            totalStage192
                            + shape.GpuStages.Stage192Count);
                        int candidateStage224 = checked(
                            totalStage224
                            + shape.GpuStages.Stage224Count);
                        long candidateSectionStorageBytes =
                            CalculateSectionStorageBytes(
                                candidateSectionValues,
                                candidateSectionWords,
                                candidateSectionStateWords);
                        long candidateOutputStorageBytes =
                            CalculateOutputStorageBytes(
                                candidateVertices,
                                candidateIndices,
                                candidateFaces,
                                candidateStage160,
                                candidateStage168,
                                candidateStage176,
                                candidateStage192,
                                candidateStage224);
                        if (batchCount != 0
                            && !CanAdmitBatch(
                                admission.RetentionBudgetBytes,
                                candidateSectionStorageBytes,
                                candidateOutputStorageBytes))
                        {
                            break;
                        }

                        _slots[batchCount] = new BatchSlot(
                            chunkId,
                            shape,
                            demand,
                            totalRecords,
                            totalMaskWords,
                            totalFaces,
                            totalVertices,
                            totalIndices,
                            totalStage160,
                            totalStage168,
                            totalStage176,
                            totalStage192,
                            totalStage224,
                            totalSectionDescriptors,
                            totalSectionValues,
                            totalSectionWords,
                            totalSectionStateWords,
                            checked(realizedDemand + demand));
                        totalRecords = candidateRecords;
                        totalMaskWords = candidateMaskWords;
                        totalFaces = candidateFaces;
                        totalUploadBytes = candidateUploadBytes;
                        totalSectionDescriptors =
                            candidateSectionDescriptors;
                        totalSectionValues = candidateSectionValues;
                        totalSectionWords = candidateSectionWords;
                        totalSectionStateWords =
                            candidateSectionStateWords;
                        totalVertices = candidateVertices;
                        totalIndices = candidateIndices;
                        totalStage160 = candidateStage160;
                        totalStage168 = candidateStage168;
                        totalStage176 = candidateStage176;
                        totalStage192 = candidateStage192;
                        totalStage224 = candidateStage224;
                        sectionStorageBytes =
                            candidateSectionStorageBytes;
                        outputStorageBytes =
                            candidateOutputStorageBytes;
                        realizedDemand = checked(realizedDemand + demand);
                        sourceInputBytes = checked(
                            sourceInputBytes + PressureWorkContract.SourceInputBytesPerChunk);
                        builtChunks++;
                        batchCount++;
                        lastChunkId = chunkId;
                    }

                    peakBatchCount = Math.Max(peakBatchCount, batchCount);
                    sectionDescriptors = _sectionDescriptors.Rent(
                        totalSectionDescriptors);
                    _transientBudget.BeginPhase(
                        sectionStorageBytes);
                    sectionValues = RentBuffer(
                        _sectionValues,
                        totalSectionValues);
                    sectionWords = RentBuffer(
                        _sectionWords,
                        totalSectionWords);
                    sectionStates = RentBuffer(
                        _sectionStates,
                        totalSectionStateWords);
                    masks = _masks.Rent(Math.Max(1, totalMaskWords));
                    _transientBudget.CompletePhase();
                    for (int batchIndex = 0;
                        batchIndex < batchCount;
                        batchIndex++)
                    {
                        BatchSlot slot = _slots[batchIndex];
                        ReadOnlySpan<VoxelCell> chunkCells = cells.AsSpan(
                            checked(
                                batchIndex
                                * VoxelMath.CellsPerChunk),
                            VoxelMath.CellsPerChunk);
                        ReadOnlySpan<SectionSummary> chunkSections =
                            _sections.AsSpan(
                                checked(
                                    batchIndex
                                    * VoxelMath.SectionsPerChunk),
                                VoxelMath.SectionsPerChunk);
                        Span<SectionPrerenderDescriptor> chunkDescriptors =
                            sectionDescriptors.AsSpan(
                                slot.SectionDescriptorOffset,
                                slot.Shape.SectionDescriptorCount);
                        PressureWorkContract.BuildSectionRepresentations(
                            chunkCells,
                            chunkSections,
                            chunkDescriptors,
                            Slice(
                                sectionValues,
                                slot.SectionValueOffset,
                                slot.Shape.SectionValueCount),
                            Slice(
                                sectionWords,
                                slot.SectionWordOffset,
                                slot.Shape.SectionWordCount),
                            Slice(
                                sectionStates,
                                slot.SectionStateWordOffset,
                                slot.Shape.SectionStateWordCount),
                            masks.AsSpan(
                                slot.MaskOffset,
                                slot.Shape.TransparentMaskWords));
                    }

                    faces = _faces.Rent(Math.Max(1, totalRecords));
                    for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                    {
                        BatchSlot slot = _slots[batchIndex];
                        Span<VoxelCell> chunkCells = cells.AsSpan(
                            checked(batchIndex * VoxelMath.CellsPerChunk),
                            VoxelMath.CellsPerChunk);
                        PressureWorkContract.PopulateFaceRecords(
                            chunkCells,
                            sectionDescriptors.AsSpan(
                                slot.SectionDescriptorOffset,
                                slot.Shape.SectionDescriptorCount),
                            slot.Shape,
                            faces.AsSpan(
                                slot.RecordOffset,
                                Math.Max(1, slot.Shape.RecordCount)));
                    }

                    peakLiveLogicalBytes = Math.Max(
                        peakLiveLogicalBytes,
                        checked(
                            (long)batchCount
                                * VoxelMath.CellsPerChunk
                                * VoxelMath.VoxelCellBytes
                            + (long)totalRecords * VoxelMath.FaceRecordBytes
                            + (long)totalMaskWords * sizeof(ulong)
                            + (long)totalSectionDescriptors
                                * VoxelMath.SectionPrerenderDescriptorBytes
                            + (long)totalSectionValues * sizeof(ushort)
                            + (long)totalSectionWords * sizeof(uint)
                            + (long)totalSectionStateWords * sizeof(ulong)));
                    if (exactVerification)
                    {
                        for (int batchIndex = 0;
                            batchIndex < batchCount;
                            batchIndex++)
                        {
                            BatchSlot slot = _slots[batchIndex];
                            string retainedSectionEvidence =
                                VerifyAndHashSections(
                                    slot,
                                    batchIndex,
                                    cells,
                                    sectionDescriptors,
                                    sectionValues,
                                    sectionWords,
                                    sectionStates);
                            _slots[batchIndex] = slot with
                            {
                                SectionEvidenceHash =
                                    retainedSectionEvidence,
                                MaskEvidenceHash =
                                    PressureWorkContract
                                        .HashTransparentMasks(
                                            slot.ChunkId,
                                            masks.AsSpan(
                                                slot.MaskOffset,
                                                slot.Shape
                                                    .TransparentMaskWords))
                            };
                        }
                    }

                    ReturnBuffer(_sectionValues, sectionValues);
                    sectionValues = null;
                    ReturnBuffer(_sectionWords, sectionWords);
                    sectionWords = null;
                    ReturnBuffer(_sectionStates, sectionStates);
                    sectionStates = null;
                    _masks.Return(masks, clearArray: false);
                    masks = null;
                    _sectionDescriptors.Return(
                        sectionDescriptors,
                        clearArray: false);
                    sectionDescriptors = null;

                    lastStage = VoxelPipelineStage.Render;
                    _transientBudget.BeginPhase(
                        outputStorageBytes);
                    vertices = RentBuffer(
                        _vertices,
                        totalVertices);
                    indices = RentBuffer(
                        _indices,
                        totalIndices);
                    slices = RentBuffer(
                        _slices,
                        totalFaces);
                    stage160 = RentBuffer(
                        _stage160,
                        totalStage160);
                    stage168 = RentBuffer(
                        _stage168,
                        totalStage168);
                    stage176 = RentBuffer(
                        _stage176,
                        totalStage176);
                    stage192 = RentBuffer(
                        _stage192,
                        totalStage192);
                    stage224 = RentBuffer(
                        _stage224,
                        totalStage224);
                    _transientBudget.CompletePhase();
                    GpuStageBuffers allStages = new(
                        Slice(stage160, 0, totalStage160),
                        Slice(stage168, 0, totalStage168),
                        Slice(stage176, 0, totalStage176),
                        Slice(stage192, 0, totalStage192),
                        Slice(stage224, 0, totalStage224));
                    for (int batchIndex = 0;
                        batchIndex < batchCount;
                        batchIndex++)
                    {
                        BatchSlot slot = _slots[batchIndex];
                        PressureChunkShape shape = slot.Shape;
                        Span<FaceRecord> chunkFaces = faces.AsSpan(
                            slot.RecordOffset,
                            Math.Max(1, shape.RecordCount));
                        Span<Vertex> chunkVertices = Slice(
                            vertices,
                            slot.VertexOffset,
                            shape.VertexCount);
                        Span<int> chunkIndices = Slice(
                            indices,
                            slot.IndexOffset,
                            shape.IndexCount);
                        GpuStageBuffers opaqueStages = allStages.Slice(
                            shape.GpuStages,
                            slot.Stage160Offset,
                            slot.Stage168Offset,
                            slot.Stage176Offset,
                            slot.Stage192Offset,
                            slot.Stage224Offset,
                            opaque: true);
                        GpuStageBuffers transparentStages = allStages.Slice(
                            shape.GpuStages,
                            slot.Stage160Offset,
                            slot.Stage168Offset,
                            slot.Stage176Offset,
                            slot.Stage192Offset,
                            slot.Stage224Offset,
                            opaque: false);
                        int opaqueVertexCount = checked(
                            shape.OpaqueFaceCount
                            * VoxelMath.VerticesPerFace);
                        int opaqueIndexCount = checked(
                            shape.OpaqueFaceCount
                            * VoxelMath.IndicesPerFace);
                        _ = PressureWorkContract.PackStream(
                            request.Seed,
                            chunkFaces.Slice(
                                0,
                                shape.OpaqueRecordCount),
                            chunkVertices.Slice(
                                0,
                                opaqueVertexCount),
                            chunkIndices.Slice(
                                0,
                                opaqueIndexCount),
                            Slice(
                                slices,
                                slot.SliceOffset,
                                shape.OpaqueFaceCount),
                            opaqueStages);
                        _ = PressureWorkContract.PackStream(
                            request.Seed,
                            chunkFaces.Slice(
                                shape.OpaqueRecordCount,
                                shape.TransparentRecordCount),
                            chunkVertices.Slice(
                                opaqueVertexCount,
                                shape.VertexCount
                                - opaqueVertexCount),
                            chunkIndices.Slice(
                                opaqueIndexCount,
                                shape.IndexCount
                                - opaqueIndexCount),
                            Slice(
                                slices,
                                checked(
                                    slot.SliceOffset
                                    + shape.OpaqueFaceCount),
                                shape.TransparentFaceCount),
                            transparentStages);
                        if (exactVerification)
                        {
                            PressureOutputEvidence output =
                                PressureWorkContract.DescribeOutput(shape);
                            _slots[batchIndex] = slot with
                            {
                                Output = output,
                                Evidence = CreateEvidence(
                                    slot,
                                    output)
                            };
                        }
                        peakLiveLogicalBytes = Math.Max(
                            peakLiveLogicalBytes,
                            checked(
                                (long)batchCount
                                    * VoxelMath.CellsPerChunk
                                    * VoxelMath.VoxelCellBytes
                                + (long)totalRecords
                                    * VoxelMath.FaceRecordBytes
                                + (long)totalVertices
                                    * VoxelMath.VertexBytes
                                + (long)totalIndices
                                    * VoxelMath.IndexBytes
                                + (long)totalFaces
                                    * VoxelMath.PayloadSliceBytes
                                + totalUploadBytes));
                        lastStage = VoxelPipelineStage.Prerender;
                    }

                    CopyStagesToMappedUpload(
                        mappedUpload,
                        allStages);
                    if (exactVerification)
                    {
                        VerifyMappedUpload(
                            mappedUpload,
                            allStages);
                    }

                    lastStage = VoxelPipelineStage.GpuUpload;
                    for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                    {
                        BatchSlot slot = _slots[batchIndex];
                        if (exactVerification)
                        {
                            PressureChunkShape shape = slot.Shape;
                            Span<FaceRecord> chunkFaces = Slice(
                                faces,
                                slot.RecordOffset,
                                Math.Max(1, shape.RecordCount));
                            int opaqueVertexCount = checked(
                                shape.OpaqueFaceCount
                                * VoxelMath.VerticesPerFace);
                            int opaqueIndexCount = checked(
                                shape.OpaqueFaceCount
                                * VoxelMath.IndicesPerFace);
                            Span<Vertex> chunkVertices = Slice(
                                vertices,
                                slot.VertexOffset,
                                shape.VertexCount);
                            Span<int> chunkIndices = Slice(
                                indices,
                                slot.IndexOffset,
                                shape.IndexCount);
                            GpuStageBuffers opaqueStages = allStages.Slice(
                                shape.GpuStages,
                                slot.Stage160Offset,
                                slot.Stage168Offset,
                                slot.Stage176Offset,
                                slot.Stage192Offset,
                                slot.Stage224Offset,
                                opaque: true);
                            GpuStageBuffers transparentStages =
                                allStages.Slice(
                                    shape.GpuStages,
                                    slot.Stage160Offset,
                                    slot.Stage168Offset,
                                    slot.Stage176Offset,
                                    slot.Stage192Offset,
                                    slot.Stage224Offset,
                                    opaque: false);
                            PressureOutputEvidence output =
                                PressureWorkContract.VerifyRetainedOutput(
                                    slot.Output,
                                    request.Seed,
                                    chunkFaces.Slice(
                                        0,
                                        shape.OpaqueRecordCount),
                                    chunkVertices.Slice(
                                        0,
                                        opaqueVertexCount),
                                    chunkIndices.Slice(
                                        0,
                                        opaqueIndexCount),
                                    Slice(
                                        slices,
                                        slot.SliceOffset,
                                        shape.OpaqueFaceCount),
                                    chunkFaces.Slice(
                                        shape.OpaqueRecordCount,
                                        shape.TransparentRecordCount),
                                    chunkVertices.Slice(
                                        opaqueVertexCount,
                                        shape.VertexCount
                                        - opaqueVertexCount),
                                    chunkIndices.Slice(
                                        opaqueIndexCount,
                                        shape.IndexCount
                                        - opaqueIndexCount),
                                    Slice(
                                        slices,
                                        checked(
                                            slot.SliceOffset
                                            + shape.OpaqueFaceCount),
                                        shape.TransparentFaceCount),
                                    opaqueStages,
                                    transparentStages);
                            _slots[batchIndex] = slot with
                            {
                                Output = output,
                                Evidence = CreateEvidence(slot, output)
                            };
                            slot = _slots[batchIndex];
                        }

                        if (exactVerification)
                        {
                            evidence!.Add(slot.Evidence);
                        }

                        completedLogicalBytes = checked(
                            completedLogicalBytes + slot.Demand);
                        _slots[batchIndex] = default;
                    }

                    ReturnBuffer(_vertices, vertices);
                    vertices = null;
                    ReturnBuffer(_indices, indices);
                    indices = null;
                    ReturnBuffer(_slices, slices);
                    slices = null;
                    ReturnBuffer(_stage160, stage160);
                    stage160 = null;
                    ReturnBuffer(_stage168, stage168);
                    stage168 = null;
                    ReturnBuffer(_stage176, stage176);
                    stage176 = null;
                    ReturnBuffer(_stage192, stage192);
                    stage192 = null;
                    ReturnBuffer(_stage224, stage224);
                    stage224 = null;
                    _faces.Return(faces, clearArray: false);
                    faces = null;
                    _cells.Return(cells, clearArray: false);
                    cells = [];
                    lastStage = VoxelPipelineStage.Completed;
                    if (batchCount < request.RetentionDepth
                        && request.NeedsChunk(
                            builtChunks,
                            realizedDemand,
                            minimumWarmupChunks))
                    {
                        admissionThrottleCount++;
                    }
                }
                finally
                {
                    if (cells.Length != 0)
                    {
                        _cells.Return(cells, clearArray: false);
                    }

                    if (faces is not null)
                    {
                        _faces.Return(faces, clearArray: false);
                    }

                    if (masks is not null)
                    {
                        _masks.Return(masks, clearArray: false);
                    }

                    if (sectionDescriptors is not null)
                    {
                        _sectionDescriptors.Return(
                            sectionDescriptors,
                            clearArray: false);
                    }

                    ReturnBuffer(_sectionValues, sectionValues);
                    ReturnBuffer(_sectionWords, sectionWords);
                    ReturnBuffer(_sectionStates, sectionStates);
                    ReturnBuffer(_vertices, vertices);
                    ReturnBuffer(_indices, indices);
                    ReturnBuffer(_slices, slices);
                    ReturnBuffer(_stage160, stage160);
                    ReturnBuffer(_stage168, stage168);
                    ReturnBuffer(_stage176, stage176);
                    ReturnBuffer(_stage192, stage192);
                    ReturnBuffer(_stage224, stage224);
                }
            }
        }
        catch (OutOfMemoryException exception)
        {
            outcome = PressureProfileOutcome.OutOfMemory;
            failure = exception;
        }
        catch (InvalidDataException exception)
        {
            outcome = PressureProfileOutcome.IncorrectOutput;
            failure = exception;
        }
        catch (Exception exception)
        {
            outcome = PressureProfileOutcome.Crash;
            failure = exception;
        }

        reportProgress(new PressureProgress(
            Implementation,
            request.ProfilePercent,
            PressureProgressKind.ProcessingCompleted,
            builtChunks,
            completedLogicalBytes,
            lastStage,
            lastChunkId));
        PressureRuntimeSnapshot after = PressureRuntimeSnapshot.Capture();
        string evidenceHash = evidence is null
            ? string.Empty
            : PressureWorkContract.ComputeProfileEvidenceHash(
                evidence);
        bool correctness = outcome == PressureProfileOutcome.Completed
            && builtChunks != 0
            && (evidence is null
                || evidence.Count == builtChunks
                && evidence.All(
                    static chunk => chunk.ExactVerificationPassed))
            && realizedDemand >= request.RequestedCumulativeDemandBytes;
        return new PressureProfileResult(
            Implementation,
            outcome,
            request.ProfilePercent,
            request.ExecutionMode,
            request.CgroupCapBytes,
            request.RequestedCumulativeDemandBytes,
            realizedDemand,
            Math.Max(0, realizedDemand - request.RequestedCumulativeDemandBytes),
            request.DeadlineMilliseconds,
            builtChunks,
            completedLogicalBytes,
            sourceInputBytes,
            peakLiveLogicalBytes,
            peakLiveLogicalBytes == 0 ? 0 : realizedDemand / (double)peakLiveLogicalBytes,
            request.RetentionDepth,
            peakBatchCount,
            admission.RetentionBudgetBytes,
            admissionThrottleCount,
            lastStage,
            lastChunkId,
            correctness,
            evidenceHash,
            evidence is { } capturedEvidence
                ? capturedEvidence
                : Array.Empty<PressureChunkEvidence>(),
            before,
            after,
            Math.Max(0, after.TotalAllocatedBytes - before.TotalAllocatedBytes),
            Math.Max(0, after.Gen0Collections - before.Gen0Collections),
            Math.Max(0, after.Gen1Collections - before.Gen1Collections),
            Math.Max(0, after.Gen2Collections - before.Gen2Collections),
            Math.Max(0, after.TotalPauseMilliseconds - before.TotalPauseMilliseconds),
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            failure?.GetType().FullName,
            failure?.Message);
    }

    private SafeAdmissionPlan EnsureCapacityPlan(
        PressureProfileRequest request,
        PressureRuntimeSnapshot runtime)
    {
        if (_capacityPlan is { } current)
        {
            if (current.Seed != request.Seed)
            {
                throw new InvalidOperationException(
                    "The managed worker requires a compatible warmup plan.");
            }

            return new SafeAdmissionPlan(
                current.RetentionDepth,
                current.RetentionBudgetBytes);
        }

        if (!request.Warmup)
        {
            throw new InvalidOperationException(
                "The managed worker requires a warmup capacity plan.");
        }

        long retainedBudget = Math.Max(
            1,
            request.CgroupCapBytes);
        SafeCapacityPlan plan = SafeCapacityPlan.Create(
            request,
            maximumRetentionDepth: request.RetentionDepth,
            retainedBudget);
        _cells.Prime(plan.CellCapacity);
        _faces.Prime(plan.FaceCapacity);
        _masks.Prime(plan.MaskCapacity);
        _sectionDescriptors.Prime(
            plan.SectionDescriptorCapacity);
        MappedGpuBuffer mappedUpload = new(
            checked((nuint)plan.MappedUploadCapacityBytes));
        UnmanagedMemoryStream mappedUploadStream;
        try
        {
            mappedUploadStream =
                mappedUpload.OpenStream();
        }
        catch
        {
            mappedUpload.Dispose();
            throw;
        }

        _mappedUpload = mappedUpload;
        _mappedUploadStream = mappedUploadStream;
        _fixedRetainedBytes = checked(
            _cells.RetainedBytes
            + _faces.RetainedBytes
            + _masks.RetainedBytes
            + _sectionDescriptors.RetainedBytes
            + plan.MappedUploadCapacityBytes
            + (long)_sections.Length
                * Unsafe.SizeOf<SectionSummary>()
            + (long)_slots.Length * Unsafe.SizeOf<BatchSlot>());
        _transientBudget.Configure(
            checked(
                plan.RetentionBudgetBytes
                - _fixedRetainedBytes));
        _sectionValues.Prime(plan.SectionValueCapacity);
        _sectionWords.Prime(plan.SectionWordCapacity);
        _sectionStates.Prime(plan.SectionStateCapacity);
        _vertices.Prime(plan.VertexCapacity);
        _indices.Prime(plan.IndexCapacity);
        _slices.Prime(plan.SliceCapacity);
        _stage160.Prime(plan.Stage160Capacity);
        _stage168.Prime(plan.Stage168Capacity);
        _stage176.Prime(plan.Stage176Capacity);
        _stage192.Prime(plan.Stage192Capacity);
        _stage224.Prime(plan.Stage224Capacity);
        _capacityPlan = plan;
        return new SafeAdmissionPlan(
            plan.RetentionDepth,
            plan.RetentionBudgetBytes);
    }

    private bool CanAdmitBatch(
        long retainedBudgetBytes,
        long sectionStorageBytes,
        long outputStorageBytes) =>
        checked(
            _fixedRetainedBytes
            + Math.Max(
                sectionStorageBytes,
                outputStorageBytes))
        <= retainedBudgetBytes;

    private static void CopyStagesToMappedUpload(
        UnmanagedMemoryStream destination,
        scoped GpuStageBuffers stages)
    {
        destination.Position = 0;
        destination.Write(
            MemoryMarshal.AsBytes(stages.Stage160));
        destination.Write(
            MemoryMarshal.AsBytes(stages.Stage168));
        destination.Write(
            MemoryMarshal.AsBytes(stages.Stage176));
        destination.Write(
            MemoryMarshal.AsBytes(stages.Stage192));
        destination.Write(
            MemoryMarshal.AsBytes(stages.Stage224));
        if (destination.Position != stages.ByteLength)
        {
            throw new InvalidDataException(
                "The safe GPU transfer did not write its exact range.");
        }
    }

    private static void VerifyMappedUpload(
        UnmanagedMemoryStream source,
        scoped GpuStageBuffers stages)
    {
        source.Position = 0;
        byte[] scratch = new byte[64 * 1024];
        VerifyMappedRange(
            source,
            MemoryMarshal.AsBytes(stages.Stage160),
            scratch);
        VerifyMappedRange(
            source,
            MemoryMarshal.AsBytes(stages.Stage168),
            scratch);
        VerifyMappedRange(
            source,
            MemoryMarshal.AsBytes(stages.Stage176),
            scratch);
        VerifyMappedRange(
            source,
            MemoryMarshal.AsBytes(stages.Stage192),
            scratch);
        VerifyMappedRange(
            source,
            MemoryMarshal.AsBytes(stages.Stage224),
            scratch);
        if (source.Position != stages.ByteLength)
        {
            throw new InvalidDataException(
                "The mapped GPU verification did not read its exact range.");
        }
    }

    private static void VerifyMappedRange(
        UnmanagedMemoryStream source,
        ReadOnlySpan<byte> expected,
        byte[] scratch)
    {
        int offset = 0;
        while (offset < expected.Length)
        {
            int count = Math.Min(
                scratch.Length,
                expected.Length - offset);
            Span<byte> actual = scratch.AsSpan(0, count);
            source.ReadExactly(actual);
            if (!actual.SequenceEqual(
                expected.Slice(offset, count)))
            {
                throw new InvalidDataException(
                    "The mapped GPU transfer changed an output byte.");
            }

            offset += count;
        }
    }

    private static long CalculateSectionStorageBytes(
        int valueCount,
        int wordCount,
        int stateCount) =>
        checked(
            BufferBytes<ushort>(valueCount)
            + BufferBytes<uint>(wordCount)
            + BufferBytes<ulong>(stateCount));

    private static long CalculateOutputStorageBytes(
        int vertexCount,
        int indexCount,
        int sliceCount,
        int stage160Count,
        int stage168Count,
        int stage176Count,
        int stage192Count,
        int stage224Count) =>
        checked(
            BufferBytes<Vertex>(vertexCount)
            + BufferBytes<int>(indexCount)
            + BufferBytes<PayloadSlice>(sliceCount)
            + BufferBytes<GpuStage160>(stage160Count)
            + BufferBytes<GpuStage168>(stage168Count)
            + BufferBytes<GpuStage176>(stage176Count)
            + BufferBytes<GpuStage192>(stage192Count)
            + BufferBytes<GpuStage224>(stage224Count));

    private static long BufferBytes<T>(int length) =>
        checked((long)length * Unsafe.SizeOf<T>());

    private static long PoolBytes<T>(int length) =>
        length == 0
            ? 0
            : checked(
                (long)TrackedArrayPool<T>.PlannedLength(length)
                * Unsafe.SizeOf<T>());

    private static T[]? RentBuffer<T>(
        ArrayPool<T> pool,
        int length) =>
        length == 0
            ? null
            : pool.Rent(length);

    private static Span<T> Slice<T>(
        T[]? values,
        int start,
        int length) =>
        length == 0
            ? Span<T>.Empty
            : values!.AsSpan(start, length);

    private string VerifyAndHashSections(
        BatchSlot slot,
        int batchIndex,
        VoxelCell[] cells,
        SectionPrerenderDescriptor[] descriptors,
        ushort[]? values,
        uint[]? words,
        ulong[]? states) =>
        PressureWorkContract.VerifyAndHashSectionRepresentations(
            slot.ChunkId,
            cells.AsSpan(
                checked(batchIndex * VoxelMath.CellsPerChunk),
                VoxelMath.CellsPerChunk),
            _sections.AsSpan(
                checked(batchIndex * VoxelMath.SectionsPerChunk),
                VoxelMath.SectionsPerChunk),
            descriptors.AsSpan(
                slot.SectionDescriptorOffset,
                slot.Shape.SectionDescriptorCount),
            Slice(
                values,
                slot.SectionValueOffset,
                slot.Shape.SectionValueCount),
            Slice(
                words,
                slot.SectionWordOffset,
                slot.Shape.SectionWordCount),
            Slice(
                states,
                slot.SectionStateWordOffset,
                slot.Shape.SectionStateWordCount));

    private static void ReturnBuffer<T>(
        ArrayPool<T> pool,
        T[]? values)
    {
        if (values is not null)
        {
            pool.Return(values, clearArray: false);
        }
    }

    private static PressureChunkEvidence CreateEvidence(
        BatchSlot slot,
        PressureOutputEvidence output) =>
        new(
            slot.ChunkId,
            PressureWorkContract.SourceInputBytesPerChunk,
            slot.Demand,
            output.OpaqueVertexLength,
            output.OpaqueIndexLength,
            output.OpaqueSliceLength,
            output.OpaqueUploadLength,
            output.TransparentVertexLength,
            output.TransparentIndexLength,
            output.TransparentSliceLength,
            output.TransparentUploadLength,
            slot.Shape.SectionDescriptorCount,
            slot.Shape.SectionValueCount,
            slot.Shape.SectionWordCount,
            slot.Shape.SectionStateWordCount,
            PressureWorkContract.CombineChunkEvidence(
                slot.SectionEvidenceHash,
                slot.MaskEvidenceHash,
                output.CompleteHash),
            ExactVerificationPassed: true);

    public void Dispose()
    {
        _cells.Dispose();
        _faces.Dispose();
        _masks.Dispose();
        _sectionDescriptors.Dispose();
        _vertices.Dispose();
        _indices.Dispose();
        _sectionValues.Dispose();
        _sectionWords.Dispose();
        _sectionStates.Dispose();
        _slices.Dispose();
        _stage160.Dispose();
        _stage168.Dispose();
        _stage176.Dispose();
        _stage192.Dispose();
        _stage224.Dispose();
        _transientBudget.Dispose();
        _mappedUploadStream?.Dispose();
        _mappedUploadStream = null;
        _mappedUpload?.Dispose();
        _mappedUpload = null;
        _fixedRetainedBytes = 0;
    }

    private readonly record struct BatchSlot(
        int ChunkId,
        PressureChunkShape Shape,
        long Demand,
        int RecordOffset,
        int MaskOffset,
        int SliceOffset,
        int VertexOffset,
        int IndexOffset,
        int Stage160Offset,
        int Stage168Offset,
        int Stage176Offset,
        int Stage192Offset,
        int Stage224Offset,
        int SectionDescriptorOffset,
        int SectionValueOffset,
        int SectionWordOffset,
        int SectionStateWordOffset,
        long CumulativeDemand,
        string SectionEvidenceHash = "",
        string MaskEvidenceHash = "",
        PressureOutputEvidence Output = default,
        PressureChunkEvidence Evidence = default);

    private sealed class SafeCapacityPlan
    {
        private SafeCapacityPlan(
            PressureProfileRequest request,
            int retentionDepth,
            long retainedBudgetBytes)
        {
            Seed = request.Seed;
            RetentionDepth = retentionDepth;
            RetentionBudgetBytes = retainedBudgetBytes;
            CellCapacity = checked(
                retentionDepth * VoxelMath.CellsPerChunk);
        }

        internal int Seed { get; }

        internal int RetentionDepth { get; }

        internal long RetentionBudgetBytes { get; }

        internal long MinimumResidentBytes { get; private set; }

        internal long PreferredResidentBytes { get; private set; }

        internal long CoreResidentBytes { get; private set; }

        internal long PeakTransientBytes { get; private set; }

        internal int CellCapacity { get; }

        internal int FaceCapacity { get; private set; }

        internal int MaskCapacity { get; private set; }

        internal int SectionDescriptorCapacity
        {
            get;
            private set;
        }

        internal int SectionValueCapacity { get; private set; }

        internal int SectionWordCapacity { get; private set; }

        internal int SectionStateCapacity { get; private set; }

        internal int VertexCapacity { get; private set; }

        internal int IndexCapacity { get; private set; }

        internal int SliceCapacity { get; private set; }

        internal int Stage160Capacity { get; private set; }

        internal int Stage168Capacity { get; private set; }

        internal int Stage176Capacity { get; private set; }

        internal int Stage192Capacity { get; private set; }

        internal int Stage224Capacity { get; private set; }

        internal long MappedUploadCapacityBytes { get; private set; }

        internal static SafeCapacityPlan Create(
            PressureProfileRequest request,
            int maximumRetentionDepth,
            long retainedBudgetBytes)
        {
            for (int retentionDepth = maximumRetentionDepth;
                retentionDepth >= 1;
                retentionDepth--)
            {
                SafeCapacityPlan plan = CreateForDepth(
                    request,
                    retentionDepth,
                    retainedBudgetBytes);
                if (plan.PreferredResidentBytes
                    <= retainedBudgetBytes)
                {
                    return plan;
                }
            }

            throw new OutOfMemoryException(
                "One canonical chunk exceeds the managed retention budget.");
        }

        private static SafeCapacityPlan CreateForDepth(
            PressureProfileRequest request,
            int retentionDepth,
            long retainedBudgetBytes)
        {
            SafeCapacityPlan plan = new(
                request,
                retentionDepth,
                retainedBudgetBytes);
            VoxelCell[]? cells = request.HasPlannedChunks
                ? null
                : GC.AllocateUninitializedArray<VoxelCell>(
                    VoxelMath.CellsPerChunk);
            SectionSummary[]? sections =
                request.HasPlannedChunks
                    ? null
                    : GC.AllocateUninitializedArray<SectionSummary>(
                        VoxelMath.SectionsPerChunk);
            int maximumValues = 0;
            int maximumWords = 0;
            int maximumStates = 0;
            int maximumVertices = 0;
            int maximumIndices = 0;
            int maximumSlices = 0;
            int maximumStage160 = 0;
            int maximumStage168 = 0;
            int maximumStage176 = 0;
            int maximumStage192 = 0;
            int maximumStage224 = 0;
            long realizedDemand = 0;
            int builtChunks = 0;
            int minimumWarmupChunks =
                request.HasPlannedChunks
                    ? 0
                    : request.RetentionDepth;
            while (request.NeedsChunk(
                builtChunks,
                realizedDemand,
                minimumWarmupChunks))
            {
                int batchCount = 0;
                int records = 0;
                int masks = 0;
                int descriptors = 0;
                int values = 0;
                int words = 0;
                int states = 0;
                int vertices = 0;
                int indices = 0;
                int slices = 0;
                int stage160 = 0;
                int stage168 = 0;
                int stage176 = 0;
                int stage192 = 0;
                int stage224 = 0;
                while (batchCount < retentionDepth
                    && request.NeedsChunk(
                        builtChunks,
                        realizedDemand,
                        minimumWarmupChunks))
                {
                    PressureChunkShape shape;
                    if (request.HasPlannedChunks)
                    {
                        shape = request.GetChunkShape(
                            builtChunks);
                    }
                    else
                    {
                        PressureWorkContract.GenerateCells(
                            request.Seed,
                            request.GetChunkId(builtChunks),
                            cells!);
                        shape =
                            PressureWorkContract.DeriveChunkShape(
                                cells!,
                                sections!);
                    }
                    records = checked(
                        records + Math.Max(1, shape.RecordCount));
                    masks = checked(
                        masks
                        + Math.Max(
                            1,
                            shape.TransparentMaskWords));
                    descriptors = checked(
                        descriptors
                        + shape.SectionDescriptorCount);
                    values = checked(
                        values + shape.SectionValueCount);
                    words = checked(
                        words + shape.SectionWordCount);
                    states = checked(
                        states + shape.SectionStateWordCount);
                    vertices = checked(
                        vertices + shape.VertexCount);
                    indices = checked(
                        indices + shape.IndexCount);
                    slices = checked(
                        slices + Math.Max(1, shape.FaceCount));
                    stage160 = checked(
                        stage160
                        + shape.GpuStages.Stage160Count);
                    stage168 = checked(
                        stage168
                        + shape.GpuStages.Stage168Count);
                    stage176 = checked(
                        stage176
                        + shape.GpuStages.Stage176Count);
                    stage192 = checked(
                        stage192
                        + shape.GpuStages.Stage192Count);
                    stage224 = checked(
                        stage224
                        + shape.GpuStages.Stage224Count);
                    realizedDemand = checked(
                        realizedDemand
                        + PressureWorkContract.CalculateLogicalDemand(
                            shape));
                    builtChunks++;
                    batchCount++;
                }

                maximumValues = Math.Max(
                    maximumValues,
                    values);
                maximumWords = Math.Max(
                    maximumWords,
                    words);
                maximumStates = Math.Max(
                    maximumStates,
                    states);
                maximumVertices = Math.Max(
                    maximumVertices,
                    vertices);
                maximumIndices = Math.Max(
                    maximumIndices,
                    indices);
                maximumSlices = Math.Max(
                    maximumSlices,
                    slices);
                maximumStage160 = Math.Max(
                    maximumStage160,
                    stage160);
                maximumStage168 = Math.Max(
                    maximumStage168,
                    stage168);
                maximumStage176 = Math.Max(
                    maximumStage176,
                    stage176);
                maximumStage192 = Math.Max(
                    maximumStage192,
                    stage192);
                maximumStage224 = Math.Max(
                    maximumStage224,
                    stage224);
                long sectionBytes =
                    CalculateSectionStorageBytes(
                        values,
                        words,
                        states);
                long outputBytes =
                    CalculateOutputStorageBytes(
                        vertices,
                        indices,
                        slices,
                        stage160,
                        stage168,
                        stage176,
                        stage192,
                        stage224);
                plan.PeakTransientBytes = Math.Max(
                    plan.PeakTransientBytes,
                    Math.Max(
                        sectionBytes,
                        outputBytes));
                plan.FaceCapacity = Math.Max(
                    plan.FaceCapacity,
                    records);
                plan.MaskCapacity = Math.Max(
                    plan.MaskCapacity,
                    masks);
                plan.SectionDescriptorCapacity = Math.Max(
                    plan.SectionDescriptorCapacity,
                    descriptors);
            }

            plan.SectionValueCapacity = maximumValues;
            plan.SectionWordCapacity = maximumWords;
            plan.SectionStateCapacity = maximumStates;
            plan.VertexCapacity = maximumVertices;
            plan.IndexCapacity = maximumIndices;
            plan.SliceCapacity = maximumSlices;
            plan.Stage160Capacity = maximumStage160;
            plan.Stage168Capacity = maximumStage168;
            plan.Stage176Capacity = maximumStage176;
            plan.Stage192Capacity = maximumStage192;
            plan.Stage224Capacity = maximumStage224;
            plan.MappedUploadCapacityBytes = Math.Max(
                1,
                checked(
                    BufferBytes<GpuStage160>(
                        maximumStage160)
                    + BufferBytes<GpuStage168>(
                        maximumStage168)
                    + BufferBytes<GpuStage176>(
                        maximumStage176)
                    + BufferBytes<GpuStage192>(
                        maximumStage192)
                    + BufferBytes<GpuStage224>(
                        maximumStage224)));
            plan.CoreResidentBytes =
                plan.CalculateCoreResidentBytes();
            plan.MinimumResidentBytes = checked(
                plan.CoreResidentBytes
                + plan.PeakTransientBytes);
            long completeTransientCacheBytes = checked(
                BufferBytes<ushort>(maximumValues)
                + BufferBytes<uint>(maximumWords)
                + BufferBytes<ulong>(maximumStates)
                + BufferBytes<Vertex>(maximumVertices)
                + BufferBytes<int>(maximumIndices)
                + BufferBytes<PayloadSlice>(maximumSlices)
                + BufferBytes<GpuStage160>(maximumStage160)
                + BufferBytes<GpuStage168>(maximumStage168)
                + BufferBytes<GpuStage176>(maximumStage176)
                + BufferBytes<GpuStage192>(maximumStage192)
                + BufferBytes<GpuStage224>(maximumStage224));
            plan.PreferredResidentBytes = checked(
                plan.CoreResidentBytes
                + completeTransientCacheBytes);
            return plan;
        }

        internal static PressureWorkerCapacity EstimateWorkerCapacity(
            PressureProfileRequest request,
            int preferredRetentionDepth)
        {
            SafeCapacityPlan minimum = CreateForDepth(
                request,
                retentionDepth: 1,
                long.MaxValue);
            SafeCapacityPlan preferred = CreateForDepth(
                request,
                preferredRetentionDepth,
                long.MaxValue);
            long safety = Math.Max(
                10L * 1024 * 1024,
                minimum.MinimumResidentBytes / 16);
            return new PressureWorkerCapacity(
                minimum.PreferredResidentBytes,
                safety,
                preferred.PreferredResidentBytes);
        }

        private long CalculateCoreResidentBytes() =>
            checked(
                SafePressureSession.PoolBytes<VoxelCell>(
                    CellCapacity)
                + SafePressureSession.PoolBytes<FaceRecord>(
                    FaceCapacity)
                + SafePressureSession.PoolBytes<ulong>(
                    MaskCapacity)
                + SafePressureSession
                    .PoolBytes<SectionPrerenderDescriptor>(
                    SectionDescriptorCapacity)
                + MappedUploadCapacityBytes
                + (long)PressureWorkContract.DefaultRetentionDepth
                    * VoxelMath.SectionsPerChunk
                    * Unsafe.SizeOf<SectionSummary>()
                + (long)PressureWorkContract.DefaultRetentionDepth
                    * Unsafe.SizeOf<BatchSlot>()
                );
    }

    private interface IManagedArrayCacheEntry
    {
        LinkedListNode<IManagedArrayCacheEntry>? CacheNode
        {
            get;
            set;
        }

        long CachedBytes { get; }

        void DropCached();
    }

    private sealed class ManagedArrayCacheBudget : IDisposable
    {
        private readonly LinkedList<IManagedArrayCacheEntry> _lru = [];
        private long _capacityBytes = -1;
        private long _activeBytes;
        private long _cachedBytes;
        private long _phaseRemainingBytes;

        internal void Configure(long capacityBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                capacityBytes);
            if (_capacityBytes >= 0)
            {
                throw new InvalidOperationException(
                    "The managed array cache budget is already configured.");
            }

            _capacityBytes = capacityBytes;
        }

        internal void BeginPhase(long requestedBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(
                requestedBytes);
            EnsureConfigured();
            if (_activeBytes != 0
                || _phaseRemainingBytes != 0)
            {
                throw new InvalidOperationException(
                    "The previous managed array phase is not complete.");
            }

            if (requestedBytes > _capacityBytes)
            {
                throw new OutOfMemoryException(
                    "The managed array phase exceeds its worker budget.");
            }

            _phaseRemainingBytes = requestedBytes;
        }

        internal void CompletePhase()
        {
            if (_phaseRemainingBytes != 0)
            {
                throw new InvalidOperationException(
                    "The managed array phase did not reserve all requested bytes.");
            }
        }

        internal void ReserveActive(long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            EnsureConfigured();
            long remaining = GetRemainingAfter(bytes);
            EvictUntilFits(bytes);
            if (bytes + remaining
                > _capacityBytes - _activeBytes)
            {
                throw new OutOfMemoryException(
                    "The active managed array set exceeds its worker budget.");
            }

            _phaseRemainingBytes = remaining;
            _activeBytes = checked(_activeBytes + bytes);
        }

        internal bool TryActivateCached(
            IManagedArrayCacheEntry entry,
            long requestedBytes)
        {
            LinkedListNode<IManagedArrayCacheEntry> node =
                entry.CacheNode
                ?? throw new InvalidOperationException(
                    "The managed array is not in the cache.");
            long cachedBytes = entry.CachedBytes;
            long remaining = GetRemainingAfter(
                requestedBytes);
            if (cachedBytes + remaining
                > _capacityBytes - _activeBytes)
            {
                return false;
            }

            _lru.Remove(node);
            entry.CacheNode = null;
            _cachedBytes = checked(
                _cachedBytes - cachedBytes);
            _activeBytes = checked(
                _activeBytes + cachedBytes);
            _phaseRemainingBytes = remaining;
            return true;
        }

        internal void ReleaseActive(long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            _activeBytes = checked(_activeBytes - bytes);
        }

        internal bool TryCache(
            IManagedArrayCacheEntry entry,
            long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            EnsureConfigured();
            EvictUntilFits(bytes);
            if (bytes
                > _capacityBytes
                    - _activeBytes
                    - _cachedBytes)
            {
                return false;
            }

            entry.CacheNode = _lru.AddLast(entry);
            _cachedBytes = checked(_cachedBytes + bytes);
            return true;
        }

        internal void DiscardCached(
            IManagedArrayCacheEntry entry)
        {
            LinkedListNode<IManagedArrayCacheEntry>? node =
                entry.CacheNode;
            if (node is null)
            {
                return;
            }

            _lru.Remove(node);
            entry.CacheNode = null;
            _cachedBytes = checked(
                _cachedBytes - entry.CachedBytes);
            entry.DropCached();
        }

        private void EvictUntilFits(long incomingBytes)
        {
            while (_lru.First is { } node
                && incomingBytes
                    > _capacityBytes
                        - _activeBytes
                        - _cachedBytes)
            {
                DiscardCached(node.Value);
            }
        }

        private void EnsureConfigured()
        {
            if (_capacityBytes < 0)
            {
                throw new InvalidOperationException(
                    "The managed array cache budget is not configured.");
            }
        }

        private long GetRemainingAfter(long bytes)
        {
            if (bytes > _phaseRemainingBytes)
            {
                throw new InvalidOperationException(
                    "The managed array request exceeds the current phase plan.");
            }

            return _phaseRemainingBytes - bytes;
        }

        public void Dispose()
        {
            if (_activeBytes != 0)
            {
                throw new InvalidOperationException(
                    "A managed array remains active.");
            }

            while (_lru.First is { } node)
            {
                DiscardCached(node.Value);
            }
        }
    }

    private sealed class BudgetedArrayPool<T> :
        ArrayPool<T>,
        IManagedArrayCacheEntry,
        IDisposable
    {
        private readonly ManagedArrayCacheBudget _budget;
        private T[]? _active;
        private T[]? _cached;
        private long _activeBytes;

        internal BudgetedArrayPool(
            ManagedArrayCacheBudget budget)
        {
            _budget = budget;
        }

        internal void Prime(int minimumLength)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(
                minimumLength);
            if (minimumLength == 0)
            {
                return;
            }

            if (_active is not null
                || _cached is not null)
            {
                throw new InvalidOperationException(
                    "The worker-local array pool is already primed.");
            }

            T[] values =
                GC.AllocateUninitializedArray<T>(
                    minimumLength);
            long bytes = checked(
                (long)values.Length * Unsafe.SizeOf<T>());
            _cached = values;
            if (!_budget.TryCache(this, bytes))
            {
                _cached = null;
                throw new OutOfMemoryException(
                    "The planned worker-local array exceeds its cache budget.");
            }
        }

        public override T[] Rent(int minimumLength)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(
                minimumLength);
            if (minimumLength == 0)
            {
                return [];
            }

            if (_active is not null)
            {
                throw new InvalidOperationException(
                    "The worker-local array pool already has an active array.");
            }

            T[]? cached = _cached;
            if (cached is not null
                && cached.Length >= minimumLength
                && _budget.TryActivateCached(
                    this,
                    checked(
                        (long)minimumLength
                        * Unsafe.SizeOf<T>())))
            {
                _cached = null;
                _active = cached;
                _activeBytes = checked(
                    (long)cached.Length
                    * Unsafe.SizeOf<T>());
                return cached;
            }

            if (cached is not null)
            {
                _budget.DiscardCached(this);
            }

            long bytes = checked(
                (long)minimumLength
                * Unsafe.SizeOf<T>());
            _budget.ReserveActive(bytes);
            try
            {
                T[] values =
                    GC.AllocateUninitializedArray<T>(
                        minimumLength);
                _active = values;
                _activeBytes = bytes;
                return values;
            }
            catch
            {
                _budget.ReleaseActive(bytes);
                throw;
            }
        }

        public override void Return(
            T[] array,
            bool clearArray = false)
        {
            ArgumentNullException.ThrowIfNull(array);
            if (array.Length == 0
                && _active is null)
            {
                return;
            }

            if (!ReferenceEquals(array, _active))
            {
                throw new InvalidOperationException(
                    "The returned array is not the active worker-local array.");
            }

            if (clearArray)
            {
                array.AsSpan().Clear();
            }

            long bytes = _activeBytes;
            _active = null;
            _activeBytes = 0;
            _budget.ReleaseActive(bytes);
            _cached = array;
            if (!_budget.TryCache(this, bytes))
            {
                _cached = null;
            }
        }

        public LinkedListNode<IManagedArrayCacheEntry>? CacheNode
        {
            get;
            set;
        }

        public long CachedBytes =>
            _cached is null
                ? 0
                : checked(
                    (long)_cached.Length
                    * Unsafe.SizeOf<T>());

        public void DropCached()
        {
            _cached = null;
        }

        public void Dispose()
        {
            if (_active is not null)
            {
                throw new InvalidOperationException(
                    "A worker-local array remains active.");
            }

            _budget.DiscardCached(this);
        }
    }

    private sealed class TrackedArrayPool<T> : IDisposable
    {
        private const int MinimumBucketLength = 16;
        private readonly ArrayPool<T> _pool;
        private T[]? _persistent;
        private bool _rented;

        internal TrackedArrayPool(
            int maximumLength,
            int maxArraysPerBucket)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                maximumLength);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                maxArraysPerBucket);
            _pool = ArrayPool<T>.Create(
                PoolLength(maximumLength),
                maxArraysPerBucket);
        }

        internal long RetainedBytes =>
            _persistent is null
                ? 0
                : checked(
                    (long)_persistent.Length
                    * Unsafe.SizeOf<T>());

        internal static int PlannedLength(int minimumLength) =>
            PoolLength(Math.Max(1, minimumLength));

        internal T[] Rent(int minimumLength)
        {
            T[] values = _persistent
                ?? throw new InvalidOperationException(
                    "The worker-local array pool is not primed.");
            if (_rented)
            {
                throw new InvalidOperationException(
                    "The worker-local array is already in use.");
            }

            if (minimumLength > values.Length)
            {
                throw new InvalidOperationException(
                    "The runtime array request exceeds its warmup plan.");
            }

            _rented = true;
            return values;
        }

        internal void Prime(int minimumLength)
        {
            if (_persistent is not null)
            {
                throw new InvalidOperationException(
                    "The worker-local array pool is already primed.");
            }

            _persistent = _pool.Rent(minimumLength);
        }

        internal void Return(T[] values, bool clearArray)
        {
            ArgumentNullException.ThrowIfNull(values);
            if (!ReferenceEquals(values, _persistent)
                || !_rented)
            {
                throw new InvalidOperationException(
                    "The returned array is not the active worker-local lease.");
            }

            if (clearArray)
            {
                values.AsSpan().Clear();
            }

            _rented = false;
        }

        public void Dispose()
        {
            if (_rented)
            {
                throw new InvalidOperationException(
                    "The worker-local array remains in use.");
            }

            T[]? values = _persistent;
            if (values is null)
            {
                return;
            }

            _persistent = null;
            _pool.Return(values, clearArray: false);
        }

        private static int PoolLength(int minimumLength)
        {
            int length = MinimumBucketLength;
            while (length < minimumLength)
            {
                length = checked(length * 2);
            }

            return length;
        }
    }

    private readonly record struct SafeAdmissionPlan(
        int RetentionDepth,
        long RetentionBudgetBytes);
}
