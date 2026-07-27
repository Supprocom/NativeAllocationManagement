using System.Collections.Concurrent;
using Supprocom.NativeAllocationManagement;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.NAM;

internal sealed class NativePressureSession :
    IQueuedPressureProfileSession,
    IPressureWorkerCapacityPlanner
{
    private readonly SectionSummary[] _sections = new SectionSummary[
        PressureWorkContract.DefaultRetentionDepth * VoxelMath.SectionsPerChunk];
    private readonly BatchSlot[] _slots = new BatchSlot[
        PressureWorkContract.DefaultRetentionDepth];
    private readonly NativeBatchContext _context;
    private readonly BlockingCollection<ProfileWorkItem> _requests = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _worker;
    private Exception? _startupFailure;
    private int _disposed;

    internal NativePressureSession()
    {
        _context = new NativeBatchContext(_sections, _slots);
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "NAM pressure owner"
        };
        _worker.Start();
        _ready.Wait();
        if (_startupFailure is not null)
        {
            throw new InvalidOperationException(
                "The persistent native pressure owner could not start.",
                _startupFailure);
        }
    }

    public string Implementation => "NAM";

    public PressureWorkerCapacity PlanWorkerCapacity(
        PressureProfileRequest request)
    {
        OutputCapacityPlan minimum =
            OutputCapacityPlan.Create(
                request,
                retentionDepth: 1);
        OutputCapacityPlan preferred =
            OutputCapacityPlan.Create(
                request,
                request.RetentionDepth);
        long minimumBytes = checked(
            (long)minimum.RequiredPhaseArenaCapacity);
        long safety = Math.Max(
            512L * 1024,
            minimumBytes / 32);
        return new PressureWorkerCapacity(
            minimumBytes,
            safety,
            checked(
                (long)preferred.RequiredPhaseArenaCapacity));
    }

    public PressureProfileResult Run(
        PressureProfileRequest request,
        Action<PressureProgress> reportProgress) =>
        QueueAsync(
            request,
            reportProgress).GetAwaiter().GetResult();

    public Task<PressureProfileResult> QueueAsync(
        PressureProfileRequest request,
        Action<PressureProgress> reportProgress)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentNullException.ThrowIfNull(reportProgress);
        ProfileWorkItem item = new(request, reportProgress);
        _requests.Add(item);
        return item.Completion.Task;
    }

    private void WorkerLoop()
    {
        try
        {
            using NativeArena phaseArena = new(
                preAllocateBytes: 0,
                NativeMemoryReturn.ToNativeMemory);
            {
                _ready.Set();
                using IEnumerator<ProfileWorkItem> requests =
                    _requests.GetConsumingEnumerable().GetEnumerator();
                if (!requests.MoveNext())
                {
                    return;
                }

                ProfileWorkItem item = requests.Current;
                if (!item.Request.Warmup)
                {
                    InvalidOperationException exception = new(
                        "The native worker requires a warmup capacity plan.");
                    item.Failure = exception;
                    item.Complete();
                    throw exception;
                }

                OutputCapacityPlan outputCapacity =
                    CreateNativeCapacityPlan(
                        item.Request,
                        out int nativeRetentionDepth);
                using MappedGpuBuffer mappedUpload =
                    new(outputCapacity.RequiredPhaseArenaCapacity);
                long sectionAdmissionCapacity =
                    outputCapacity.SectionAdmissionCapacityBytes;
                nuint plannedPhaseCapacity =
                    outputCapacity.RequiredPhaseArenaCapacity;
                nuint reservedPhaseCapacity =
                    phaseArena.ReserveExternalMemory(
                        mappedUpload,
                        byteOffset: 0,
                        byteLength: plannedPhaseCapacity);
                if (reservedPhaseCapacity
                    != plannedPhaseCapacity)
                {
                    throw new InvalidOperationException(
                        "The planned phase-arena reservation did not complete.");
                }

                ArenaLease<VoxelCell> persistentCells =
                    phaseArena.Scratch<VoxelCell>(
                        outputCapacity.CellCapacity,
                        static writer => writer.Fill(default!));
                ArenaLease<FaceRecord> persistentFaces =
                    phaseArena.Scratch<FaceRecord>(
                        outputCapacity.FaceCapacity,
                        static writer => writer.Fill(default!));
                ArenaLease<ulong> persistentMasks =
                    phaseArena.Scratch<ulong>(
                        outputCapacity.MaskCapacity,
                        static writer => writer.Fill(default!));
                ArenaLease<byte> persistentPayloadPatterns =
                    phaseArena.Scratch<byte>(
                        PressureWorkContract.PayloadPatternTableBytes,
                        static writer => writer.Write(
                            PressureWorkContract.PayloadPatternTable));
                while (true)
                {
                    try
                    {
                        PressureProfileRequest request = item.Request;
                        Action<PressureProgress> reportProgress = item.ReportProgress;
                        if (request.RetentionDepth > PressureWorkContract.DefaultRetentionDepth)
                        {
                            throw new ArgumentOutOfRangeException(
                                nameof(request),
                                $"The predeclared native batch supports at most "
                                + $"{PressureWorkContract.DefaultRetentionDepth} chunks.");
                        }

                        PressureRuntimeSnapshot before = PressureRuntimeSnapshot.Capture();
                        NativeMemoryStatistics nativeBefore = NativeMemoryDiagnostics.Snapshot();
                        NativeOwnerStatistics[] ownerBefore =
                        [
                            phaseArena.GetStatistics()
                        ];
                        NativeProfileState state = new(request);
                        Exception? failure = null;
                        PressureProfileOutcome outcome = PressureProfileOutcome.Completed;

                    reportProgress(new PressureProgress(
                        Implementation,
                        request.ProfilePercent,
                        PressureProgressKind.ProcessingStarted,
                        0,
                        0,
                        VoxelPipelineStage.None,
                        -1));
                    try
                    {
                        if (!outputCapacity.IsReady
                            || outputCapacity.Seed != request.Seed
                            || outputCapacity.RetentionDepth
                                != nativeRetentionDepth)
                        {
                            throw new InvalidOperationException(
                                "The native worker requires an equal warmup plan.");
                        }

                        while (state.NeedsBatch(request))
                        {
                            _context.BeginBatch(
                                request,
                                reportProgress,
                                state.Consumed,
                                state.BuiltChunks,
                                state.RealizedDemand,
                                Math.Max(
                                    0,
                                    state.MinimumWarmupChunks
                                        - state.BuiltChunks),
                                nativeRetentionDepth,
                                sectionAdmissionCapacity);
                            persistentCells.Access(
                                _context.BuildAction);
                            state.RecordBuild(request, _context);
                            outputCapacity.EnsureFits(_context);
                            try
                            {
                                scoped ArenaLease<
                                    SectionPrerenderDescriptor>
                                    sectionDescriptors;
                                scoped ArenaLease<ushort>
                                    sectionValues;
                                scoped ArenaLease<uint>
                                    sectionWords;
                                scoped ArenaLease<ulong>
                                    sectionStates;
                                NativeLeaseOperations
                                    .InitializeScoped(
                                        persistentCells,
                                        phaseArena,
                                        _context
                                            .TotalSectionDescriptors,
                                        _context.TotalSectionValues,
                                        _context.TotalSectionWords,
                                        _context
                                            .TotalSectionStateWords,
                                        _context
                                            .SectionInitializeAction,
                                        out sectionDescriptors,
                                        out sectionValues,
                                        out sectionWords,
                                        out sectionStates);
                                if (_context.ExactVerification)
                                {
                                    NativeLeaseOperations.Access(
                                        persistentCells,
                                        sectionDescriptors,
                                        sectionValues,
                                        sectionWords,
                                        sectionStates,
                                            _context
                                                .SectionRecordAction);
                                }

                                NativeLeaseOperations.Access(
                                    persistentCells,
                                    persistentFaces,
                                    persistentMasks,
                                    sectionDescriptors,
                                    _context.FaceMaskAction);
                                state.RecordRender(_context);
                                if (_context.ExactVerification)
                                {
                                    NativeLeaseOperations.Access(
                                        persistentCells,
                                        sectionDescriptors,
                                        sectionValues,
                                        sectionWords,
                                        sectionStates,
                                        _context
                                            .SectionVerifyAction);
                                }
                            }
                            finally
                            {
                                phaseArena.RecycleScoped();
                            }

                            try
                            {
                                scoped ArenaLease<Vertex>
                                    vertices;
                                scoped ArenaLease<int>
                                    indices;
                                scoped ArenaLease<PayloadSlice>
                                    slices;
                                scoped ArenaLease<byte>
                                    aliasMarker;
                                NativeLeaseOperations.InitializeScoped(
                                    persistentFaces,
                                    phaseArena,
                                    _context.TotalVertices,
                                    _context.TotalIndices,
                                    _context.TotalFaces,
                                    0,
                                    _context.PackInitializeAction,
                                    out vertices,
                                    out indices,
                                    out slices,
                                    out aliasMarker);
                                if (_context.ExactVerification)
                                {
                                    NativeLeaseOperations.Access(
                                        persistentFaces,
                                        persistentPayloadPatterns,
                                        vertices,
                                        indices,
                                        slices,
                                        _context.PackCompleteAction);
                                }
                                else
                                {
                                    _context.CompleteScatterHandoff();
                                }
                                RecordCompletedBatch(state);
                                state.RecordArena(_context);
                            }
                            finally
                            {
                                phaseArena.RecycleScoped();
                            }

                            state.LastStage =
                                VoxelPipelineStage.Completed;
                        }
                    }
                    catch (OutOfMemoryException exception)
                    {
                        outcome = PressureProfileOutcome.OutOfMemory;
                        failure = exception;
                    }
                    catch (NativeAllocationFailedException exception)
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
                        state.Consumed.Count,
                        _context.CompletedLogicalBytes,
                        state.LastStage,
                        state.LastChunkId));
                    PressureRuntimeSnapshot after = PressureRuntimeSnapshot.Capture();
                    NativeMemoryStatistics nativeAfter = NativeMemoryDiagnostics.Snapshot();
                    NativeOwnerStatistics[] ownerAfter =
                    [
                        phaseArena.GetStatistics()
                    ];
                    IReadOnlyList<NativeOwnerProfile> owners = BuildOwnerProfiles(
                        ownerBefore,
                        ownerAfter,
                        [
                            state.PhaseArenaPeakRequest
                        ]);
                    string evidenceHash =
                        PressureWorkContract.ComputeProfileEvidenceHash(state.Consumed);
                    bool correctness = outcome == PressureProfileOutcome.Completed
                        && state.Consumed.Count != 0
                        && (!_context.ExactVerification
                            || state.Consumed.All(
                                static chunk =>
                                    chunk.ExactVerificationPassed))
                        && state.RealizedDemand
                            >= request.RequestedCumulativeDemandBytes;
                    long retainedBytes = ownerAfter.Sum(
                        static owner => owner.RetainedBytes);
                    long physicalPeak = owners.Sum(
                        static owner => owner.PeakPhysicalBytes);
                    item.Result = new PressureProfileResult(
                        Implementation,
                        outcome,
                        request.ProfilePercent,
                        request.CgroupCapBytes,
                        request.RequestedCumulativeDemandBytes,
                        state.RealizedDemand,
                        Math.Max(
                            0,
                            state.RealizedDemand
                                - request.RequestedCumulativeDemandBytes),
                        request.DeadlineMilliseconds,
                        state.Consumed.Count,
                        _context.CompletedLogicalBytes,
                        state.SourceInputBytes,
                        state.PeakLiveLogicalBytes,
                        state.PeakLiveLogicalBytes == 0
                            ? 0
                            : state.RealizedDemand
                                / (double)state.PeakLiveLogicalBytes,
                        request.RetentionDepth,
                        state.PeakBatchCount,
                        state.PhaseArenaPeakRequest,
                        state.AdmissionThrottleCount,
                        state.LastStage,
                        state.LastChunkId,
                        correctness,
                        evidenceHash,
                        state.Consumed,
                        before,
                        after,
                        Math.Max(
                            0,
                            after.TotalAllocatedBytes - before.TotalAllocatedBytes),
                        Math.Max(
                            0,
                            after.Gen0Collections - before.Gen0Collections),
                        Math.Max(
                            0,
                            after.Gen1Collections - before.Gen1Collections),
                        Math.Max(
                            0,
                            after.Gen2Collections - before.Gen2Collections),
                        Math.Max(
                            0,
                            after.TotalPauseMilliseconds
                                - before.TotalPauseMilliseconds),
                        physicalPeak,
                        retainedBytes,
                        0,
                        Math.Max(
                            0,
                            nativeAfter.ReusedNativeSegmentCount
                                - nativeBefore.ReusedNativeSegmentCount),
                        Math.Max(
                            0,
                            nativeAfter.ReclaimedRangeReuseCount
                                - nativeBefore.ReclaimedRangeReuseCount),
                        Math.Max(
                            0,
                            nativeAfter.ReclaimedRangeReuseBytes
                                - nativeBefore.ReclaimedRangeReuseBytes),
                        owners,
                        failure?.GetType().FullName,
                        failure?.Message);
                }
                    catch (Exception exception)
                    {
                        item.Failure = exception;
                    }
                    finally
                    {
                        item.Complete();
                    }

                    if (!requests.MoveNext())
                    {
                        break;
                    }

                    item = requests.Current;
                }
            }
        }
        catch (Exception exception)
        {
            _startupFailure = exception;
            _ready.Set();
            while (_requests.TryTake(out ProfileWorkItem? item))
            {
                item.Failure = exception;
                item.Complete();
            }
        }
    }

    private void RecordCompletedBatch(NativeProfileState state)
    {
        for (int batchIndex = 0;
            batchIndex < _context.BatchCount;
            batchIndex++)
        {
            BatchSlot slot = _slots[batchIndex];
            state.RecordPack(
                _context,
                slot,
                slot.Shape);
            _slots[batchIndex] = default;
        }
    }

    private static OutputCapacityPlan CreateNativeCapacityPlan(
        PressureProfileRequest request,
        out int retentionDepth)
    {
        long retainedBudgetBytes = Math.Max(
            1,
            request.CgroupCapBytes);
        for (int candidate = request.RetentionDepth;
            candidate >= 1;
            candidate--)
        {
            OutputCapacityPlan plan =
                OutputCapacityPlan.Create(
                    request,
                    candidate);
            if (plan.RequiredPhaseArenaCapacity
                <= (nuint)retainedBudgetBytes)
            {
                retentionDepth = candidate;
                return plan;
            }
        }

        throw new OutOfMemoryException(
            "One canonical native chunk exceeds its worker memory budget.");
    }

    private static IReadOnlyList<NativeOwnerProfile> BuildOwnerProfiles(
        IReadOnlyList<NativeOwnerStatistics> before,
        IReadOnlyList<NativeOwnerStatistics> after,
        IReadOnlyList<long> peakRequests)
    {
        string[] names =
        [
            "heterogeneous-phase-arena"
        ];
        List<NativeOwnerProfile> profiles = new(names.Length);
        for (int index = 0; index < names.Length; index++)
        {
            NativeOwnerStatistics start = before[index];
            NativeOwnerStatistics finish = after[index];
            long peakPhysical = Math.Max(start.RetainedBytes, finish.RetainedBytes);
            profiles.Add(new NativeOwnerProfile(
                names[index],
                finish.RequestedBytes,
                peakRequests[index],
                peakPhysical,
                finish.RetainedBytes,
                finish.RetiredBytes,
                peakRequests[index] == 0
                    ? 0
                    : Math.Max(0, peakPhysical - peakRequests[index]),
                Math.Max(start.SegmentCount, finish.SegmentCount),
                finish.SegmentCount,
                Math.Max(0, finish.TrimmedBytes - start.TrimmedBytes),
                Math.Max(0, finish.TrimCallCount - start.TrimCallCount),
                Math.Max(
                    0,
                    finish.FreshSegmentAllocationCount
                        - start.FreshSegmentAllocationCount)));
        }

        return profiles;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _requests.CompleteAdding();
        if (!_worker.Join(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException(
                "The persistent native pressure owner did not stop within ten seconds.");
        }

        _requests.Dispose();
        _ready.Dispose();
    }

    private sealed class NativeProfileState
    {
        internal NativeProfileState(PressureProfileRequest request)
        {
            MinimumWarmupChunks =
                request.Warmup
                    && !request.HasPlannedChunks
                ? request.RetentionDepth
                : 0;
        }

        internal List<PressureChunkEvidence> Consumed { get; } = [];

        internal int MinimumWarmupChunks { get; }

        internal long RealizedDemand { get; private set; }

        internal long SourceInputBytes { get; private set; }

        internal long PeakLiveLogicalBytes { get; private set; }

        internal int PeakBatchCount { get; private set; }

        internal int LastChunkId { get; private set; } = -1;

        internal VoxelPipelineStage LastStage { get; set; }

        internal int BuiltChunks { get; private set; }

        internal long PhaseArenaPeakRequest { get; private set; }

        internal int AdmissionThrottleCount { get; private set; }

        internal bool NeedsBatch(PressureProfileRequest request)
        {
            return request.NeedsChunk(
                BuiltChunks,
                RealizedDemand,
                MinimumWarmupChunks);
        }

        internal void RecordBuild(
            PressureProfileRequest request,
            NativeBatchContext context)
        {
            int batchCount = context.BatchCount;
            PeakBatchCount = Math.Max(PeakBatchCount, batchCount);
            RealizedDemand = checked(RealizedDemand + context.BatchDemand);
            SourceInputBytes = checked(
                SourceInputBytes
                + (long)batchCount
                    * PressureWorkContract.SourceInputBytesPerChunk);
            BuiltChunks = checked(BuiltChunks + batchCount);
            if (context.BatchCount < context.BatchCapacity
                && request.NeedsChunk(
                    BuiltChunks,
                    RealizedDemand,
                    MinimumWarmupChunks))
            {
                AdmissionThrottleCount++;
            }
            LastChunkId = request.GetChunkId(
                BuiltChunks - 1);
            LastStage = VoxelPipelineStage.Build;
        }

        internal void RecordRender(NativeBatchContext context)
        {
            PeakLiveLogicalBytes = Math.Max(
                PeakLiveLogicalBytes,
                checked(
                    (long)context.BatchCount
                        * VoxelMath.CellsPerChunk
                        * VoxelMath.VoxelCellBytes
                    + (long)context.TotalRecords
                        * VoxelMath.FaceRecordBytes
                    + (long)context.TotalMaskWords
                        * sizeof(ulong)
                    + (long)context.TotalSectionDescriptors
                        * VoxelMath.SectionPrerenderDescriptorBytes
                    + (long)context.TotalSectionValues * sizeof(ushort)
                    + (long)context.TotalSectionWords * sizeof(uint)
                    + (long)context.TotalSectionStateWords * sizeof(ulong)));
            LastStage = VoxelPipelineStage.Render;
        }

        internal void RecordArena(NativeBatchContext context)
        {
            long persistentBytes = checked(
                (long)context.BatchCapacity
                    * VoxelMath.CellsPerChunk
                    * VoxelMath.VoxelCellBytes
                + (long)context.TotalRecords
                    * VoxelMath.FaceRecordBytes
                + (long)context.TotalMaskWords * sizeof(ulong)
                + PressureWorkContract.PayloadPatternTableBytes);
            long phaseBytes = Math.Max(
                context.SectionArenaLogicalRequestBytes,
                context.OutputArenaLogicalRequestBytes);
            PhaseArenaPeakRequest = Math.Max(
                PhaseArenaPeakRequest,
                checked(persistentBytes + phaseBytes));
        }

        internal void RecordPack(
            NativeBatchContext context,
            BatchSlot slot,
            PressureChunkShape shape)
        {
            long vertexBytes = checked(
                (long)Math.Max(1, shape.VertexCount)
                * VoxelMath.VertexBytes);
            long indexBytes = checked(
                (long)Math.Max(1, shape.IndexCount)
                * VoxelMath.IndexBytes);
            PeakLiveLogicalBytes = Math.Max(
                PeakLiveLogicalBytes,
                checked(
                    (long)context.BatchCount
                        * VoxelMath.CellsPerChunk
                        * VoxelMath.VoxelCellBytes
                    + (long)context.TotalRecords
                        * VoxelMath.FaceRecordBytes
                    + vertexBytes
                    + indexBytes
                    + (long)context.TotalFaces
                        * VoxelMath.PayloadSliceBytes
                    + context.TotalUploadBytes));
            LastStage = slot.ChunkId == LastChunkId
                ? VoxelPipelineStage.GpuUpload
                : VoxelPipelineStage.Prerender;
        }
    }

    private sealed class NativeBatchContext
    {
        internal const long AlignmentAllowanceBytes = 4_096;
        private readonly SectionSummary[] _sections;
        private readonly BatchSlot[] _slots;
        private PressureProfileRequest _request;
        private Action<PressureProgress> _reportProgress = null!;
        private List<PressureChunkEvidence> _consumed = null!;
        private int _startingChunkId;
        private long _startingDemand;
        private int _minimumChunks;
        private int _batchCapacity;
        private long _arenaCapacityBytes;

        internal NativeBatchContext(
            SectionSummary[] sections,
            BatchSlot[] slots)
        {
            _sections = sections;
            _slots = slots;
            BuildAction = Build;
            SectionInitializeAction = InitializeSections;
            SectionRecordAction = RecordSections;
            SectionVerifyAction = VerifySections;
            FaceMaskAction = PopulateFacesAndMasks;
            PackInitializeAction = InitializePack;
            PackCompleteAction = CompletePack;
        }

        internal NativeLeaseAction<VoxelCell> BuildAction { get; }

        internal NativeLeaseSourceQuadSpanInitializer<
            VoxelCell,
            SectionPrerenderDescriptor,
            ushort,
            uint,
            ulong> SectionInitializeAction { get; }

        internal NativeLeaseQuintupleAction<
            VoxelCell,
            SectionPrerenderDescriptor,
            ushort,
            uint,
            ulong> SectionRecordAction { get; }

        internal NativeLeaseQuintupleAction<
            VoxelCell,
            SectionPrerenderDescriptor,
            ushort,
            uint,
            ulong> SectionVerifyAction { get; }

        internal NativeLeaseQuadrupleAction<
            VoxelCell,
            FaceRecord,
            ulong,
            SectionPrerenderDescriptor>
            FaceMaskAction { get; }

        internal NativeLeaseSourceQuadSpanInitializer<
            FaceRecord,
            Vertex,
            int,
            PayloadSlice,
            byte> PackInitializeAction { get; }

        internal NativeLeaseQuintupleAction<
            FaceRecord,
            byte,
            Vertex,
            int,
            PayloadSlice> PackCompleteAction { get; }

        internal int BatchCount { get; private set; }

        internal long BatchDemand { get; private set; }

        internal int TotalRecords { get; private set; }

        internal int TotalMaskWords { get; private set; }

        internal int TotalFaces { get; private set; }

        internal int TotalUploadBytes { get; private set; }

        internal int TotalSectionDescriptors { get; private set; }

        internal int TotalSectionValues { get; private set; }

        internal int TotalSectionWords { get; private set; }

        internal int TotalSectionStateWords { get; private set; }

        internal int TotalVertices { get; private set; }

        internal int TotalIndices { get; private set; }

        internal long SectionArenaLogicalRequestBytes =>
            CalculateSectionArenaLogicalRequestBytes(
                TotalSectionDescriptors,
                TotalSectionValues,
                TotalSectionWords,
                TotalSectionStateWords);

        internal long OutputArenaLogicalRequestBytes =>
            CalculateOutputArenaLogicalRequestBytes(
                TotalFaces,
                TotalVertices,
                TotalIndices);

        internal long ArenaLogicalRequestBytes => checked(
            SectionArenaLogicalRequestBytes
            + OutputArenaLogicalRequestBytes);

        internal long CompletedLogicalBytes { get; private set; }

        internal int BatchCapacity => _batchCapacity;

        internal bool ExactVerification =>
            _request.ExecutionMode == PressureExecutionMode.Verification;

        internal void BeginBatch(
            PressureProfileRequest request,
            Action<PressureProgress> reportProgress,
            List<PressureChunkEvidence> consumed,
            int startingChunkId,
            long startingDemand,
            int minimumChunks,
            int batchCapacity,
            long arenaCapacityBytes)
        {
            _request = request;
            _reportProgress = reportProgress;
            _consumed = consumed;
            _startingChunkId = startingChunkId;
            _startingDemand = startingDemand;
            _minimumChunks = minimumChunks;
            _batchCapacity = batchCapacity;
            _arenaCapacityBytes = arenaCapacityBytes;
            BatchCount = 0;
            BatchDemand = 0;
            TotalRecords = 0;
            TotalMaskWords = 0;
            TotalFaces = 0;
            TotalUploadBytes = 0;
            TotalSectionDescriptors = 0;
            TotalSectionValues = 0;
            TotalSectionWords = 0;
            TotalSectionStateWords = 0;
            TotalVertices = 0;
            TotalIndices = 0;
            CompletedLogicalBytes = startingDemand;
        }

        private void Build(scoped NativeLeaseView<VoxelCell> cells)
        {
            Span<VoxelCell> allCells = cells.AsSpan();
            while (BatchCount < _batchCapacity
                && (_startingDemand + BatchDemand
                        < _request.RequestedCumulativeDemandBytes
                    || BatchCount < _minimumChunks))
            {
                int chunkId = _request.GetChunkId(
                    checked(_startingChunkId + BatchCount));
                Span<VoxelCell> chunkCells = allCells.Slice(
                    checked(BatchCount * VoxelMath.CellsPerChunk),
                    VoxelMath.CellsPerChunk);
                Span<SectionSummary> chunkSections = _sections.AsSpan(
                    checked(BatchCount * VoxelMath.SectionsPerChunk),
                    VoxelMath.SectionsPerChunk);
                PressureWorkContract.GenerateCells(
                    _request.Seed,
                    chunkId,
                    chunkCells);
                PressureChunkShape shape = PressureWorkContract.DeriveChunkShape(
                    chunkCells,
                    chunkSections);
                long demand =
                    PressureWorkContract.CalculateLogicalDemand(shape);
                int candidateRecords = checked(
                    TotalRecords + Math.Max(1, shape.RecordCount));
                int candidateMaskWords = checked(
                    TotalMaskWords
                    + Math.Max(1, shape.TransparentMaskWords));
                int candidateFaces = checked(
                    TotalFaces + Math.Max(1, shape.FaceCount));
                int candidateUploadBytes = checked(
                    TotalUploadBytes + Math.Max(1, shape.UploadBytes));
                int candidateSectionDescriptors = checked(
                    TotalSectionDescriptors
                    + shape.SectionDescriptorCount);
                int candidateSectionValues = checked(
                    TotalSectionValues + shape.SectionValueCount);
                int candidateSectionWords = checked(
                    TotalSectionWords + shape.SectionWordCount);
                int candidateSectionStateWords = checked(
                    TotalSectionStateWords
                    + shape.SectionStateWordCount);
                int candidateVertices = checked(
                    TotalVertices + shape.VertexCount);
                int candidateIndices = checked(
                    TotalIndices + shape.IndexCount);
                long candidateSectionArenaBytes = checked(
                    AlignmentAllowanceBytes
                    + CalculateSectionArenaLogicalRequestBytes(
                        candidateSectionDescriptors,
                        candidateSectionValues,
                        candidateSectionWords,
                        candidateSectionStateWords));
                if (BatchCount != 0
                    && _arenaCapacityBytes != 0
                    && candidateSectionArenaBytes > _arenaCapacityBytes)
                {
                    break;
                }

                _slots[BatchCount] = new BatchSlot(
                    chunkId,
                    shape,
                    demand,
                    TotalRecords,
                    TotalMaskWords,
                    TotalFaces,
                    TotalVertices,
                    TotalIndices,
                    TotalSectionDescriptors,
                    TotalSectionValues,
                    TotalSectionWords,
                    TotalSectionStateWords,
                    checked(_startingDemand + BatchDemand + demand));
                TotalRecords = candidateRecords;
                TotalMaskWords = candidateMaskWords;
                TotalFaces = candidateFaces;
                TotalUploadBytes = candidateUploadBytes;
                TotalSectionDescriptors = candidateSectionDescriptors;
                TotalSectionValues = candidateSectionValues;
                TotalSectionWords = candidateSectionWords;
                TotalSectionStateWords = candidateSectionStateWords;
                TotalVertices = candidateVertices;
                TotalIndices = candidateIndices;
                BatchDemand = checked(BatchDemand + demand);
                BatchCount++;
            }
        }

        private static long CalculateSectionArenaLogicalRequestBytes(
            int sectionDescriptors,
            int sectionValues,
            int sectionWords,
            int sectionStateWords) =>
            checked(
                (long)sectionDescriptors
                    * VoxelMath.SectionPrerenderDescriptorBytes
                + (long)sectionValues * sizeof(ushort)
                + (long)sectionWords * sizeof(uint)
                + (long)sectionStateWords * sizeof(ulong));

        private static long CalculateOutputArenaLogicalRequestBytes(
            int faces,
            int vertices,
            int indices) =>
            checked(
                (long)vertices * VoxelMath.VertexBytes
                + (long)indices * VoxelMath.IndexBytes
                + (long)faces * VoxelMath.PayloadSliceBytes);

        private void InitializeSections(
            scoped NativeLeaseView<VoxelCell> cells,
            scoped Span<SectionPrerenderDescriptor> descriptors,
            scoped Span<ushort> values,
            scoped Span<uint> words,
            scoped Span<ulong> states)
        {
            Span<VoxelCell> allCells = cells.AsSpan();
            for (int batchIndex = 0;
                batchIndex < BatchCount;
                batchIndex++)
            {
                BatchSlot slot = _slots[batchIndex];
                ReadOnlySpan<VoxelCell> chunkCells = allCells.Slice(
                    checked(
                        batchIndex * VoxelMath.CellsPerChunk),
                    VoxelMath.CellsPerChunk);
                ReadOnlySpan<SectionSummary> chunkSections =
                    _sections.AsSpan(
                        checked(
                            batchIndex * VoxelMath.SectionsPerChunk),
                        VoxelMath.SectionsPerChunk);
                PressureWorkContract.BuildSectionRepresentations(
                    chunkCells,
                    chunkSections,
                    descriptors.Slice(
                        slot.SectionDescriptorOffset,
                        slot.Shape.SectionDescriptorCount),
                    values.Slice(
                        slot.SectionValueOffset,
                        slot.Shape.SectionValueCount),
                    words.Slice(
                        slot.SectionWordOffset,
                        slot.Shape.SectionWordCount),
                    states.Slice(
                        slot.SectionStateWordOffset,
                        slot.Shape.SectionStateWordCount));
            }
        }

        private void RecordSections(
            scoped NativeLeaseView<VoxelCell> cells,
            scoped NativeLeaseView<SectionPrerenderDescriptor> descriptors,
            scoped NativeLeaseView<ushort> values,
            scoped NativeLeaseView<uint> words,
            scoped NativeLeaseView<ulong> states)
        {
            Span<VoxelCell> allCells = cells.AsSpan();
            Span<SectionPrerenderDescriptor> allDescriptors =
                descriptors.AsSpan();
            Span<ushort> allValues = values.AsSpan();
            Span<uint> allWords = words.AsSpan();
            Span<ulong> allStates = states.AsSpan();
            for (int batchIndex = 0;
                batchIndex < BatchCount;
                batchIndex++)
            {
                BatchSlot slot = _slots[batchIndex];
                string sectionEvidence =
                    PressureWorkContract
                        .VerifyAndHashSectionRepresentations(
                            slot.ChunkId,
                            allCells.Slice(
                                checked(
                                    batchIndex
                                    * VoxelMath.CellsPerChunk),
                                VoxelMath.CellsPerChunk),
                            _sections.AsSpan(
                                checked(
                                    batchIndex
                                    * VoxelMath.SectionsPerChunk),
                                VoxelMath.SectionsPerChunk),
                            allDescriptors.Slice(
                                slot.SectionDescriptorOffset,
                                slot.Shape.SectionDescriptorCount),
                            allValues.Slice(
                                slot.SectionValueOffset,
                                slot.Shape.SectionValueCount),
                            allWords.Slice(
                                slot.SectionWordOffset,
                                slot.Shape.SectionWordCount),
                            allStates.Slice(
                                slot.SectionStateWordOffset,
                                slot.Shape.SectionStateWordCount));
                _slots[batchIndex] = slot with
                {
                    SectionEvidenceHash = sectionEvidence
                };
            }
        }

        private void VerifySections(
            scoped NativeLeaseView<VoxelCell> cells,
            scoped NativeLeaseView<SectionPrerenderDescriptor> descriptors,
            scoped NativeLeaseView<ushort> values,
            scoped NativeLeaseView<uint> words,
            scoped NativeLeaseView<ulong> states)
        {
            Span<VoxelCell> allCells = cells.AsSpan();
            Span<SectionPrerenderDescriptor> allDescriptors =
                descriptors.AsSpan();
            Span<ushort> allValues = values.AsSpan();
            Span<uint> allWords = words.AsSpan();
            Span<ulong> allStates = states.AsSpan();
            for (int batchIndex = 0;
                batchIndex < BatchCount;
                batchIndex++)
            {
                BatchSlot slot = _slots[batchIndex];
                string sectionEvidence =
                    PressureWorkContract
                        .VerifyAndHashSectionRepresentations(
                            slot.ChunkId,
                            allCells.Slice(
                                checked(
                                    batchIndex
                                    * VoxelMath.CellsPerChunk),
                                VoxelMath.CellsPerChunk),
                            _sections.AsSpan(
                                checked(
                                    batchIndex
                                    * VoxelMath.SectionsPerChunk),
                                VoxelMath.SectionsPerChunk),
                            allDescriptors.Slice(
                                slot.SectionDescriptorOffset,
                                slot.Shape.SectionDescriptorCount),
                            allValues.Slice(
                                slot.SectionValueOffset,
                                slot.Shape.SectionValueCount),
                            allWords.Slice(
                                slot.SectionWordOffset,
                                slot.Shape.SectionWordCount),
                            allStates.Slice(
                                slot.SectionStateWordOffset,
                                slot.Shape.SectionStateWordCount));
                if (!string.Equals(
                    sectionEvidence,
                    slot.SectionEvidenceHash,
                    StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Section representations changed before their render consumer boundary.");
                }
            }
        }

        private void PopulateFacesAndMasks(
            scoped NativeLeaseView<VoxelCell> cells,
            scoped NativeLeaseView<FaceRecord> faces,
            scoped NativeLeaseView<ulong> masks,
            scoped NativeLeaseView<SectionPrerenderDescriptor> descriptors)
        {
            Span<VoxelCell> allCells = cells.AsSpan();
            Span<SectionPrerenderDescriptor> allDescriptors =
                descriptors.AsSpan();
            Span<FaceRecord> allFaces = faces.AsSpan();
            Span<ulong> allMasks = masks.AsSpan();
            for (int batchIndex = 0; batchIndex < BatchCount; batchIndex++)
            {
                BatchSlot slot = _slots[batchIndex];
                PressureWorkContract.PopulateFaceRecords(
                    allCells.Slice(
                        checked(batchIndex * VoxelMath.CellsPerChunk),
                        VoxelMath.CellsPerChunk),
                    allDescriptors.Slice(
                        slot.SectionDescriptorOffset,
                        slot.Shape.SectionDescriptorCount),
                    slot.Shape,
                    allFaces.Slice(
                        slot.RecordOffset,
                        Math.Max(1, slot.Shape.RecordCount)));
                PressureWorkContract.BuildTransparentMasks(
                    allCells.Slice(
                        checked(batchIndex * VoxelMath.CellsPerChunk),
                        VoxelMath.CellsPerChunk),
                    _sections.AsSpan(
                        checked(batchIndex * VoxelMath.SectionsPerChunk),
                        VoxelMath.SectionsPerChunk),
                    allMasks.Slice(
                        slot.MaskOffset,
                        slot.Shape.TransparentMaskWords));
            }
        }

        private void InitializePack(
            scoped NativeLeaseView<FaceRecord> faces,
            scoped Span<Vertex> allVertices,
            scoped Span<int> allIndices,
            scoped Span<PayloadSlice> allSlices,
            scoped Span<byte> aliasMarker)
        {
            if (!aliasMarker.IsEmpty)
            {
                throw new InvalidOperationException(
                    "The aliased payload marker must not reserve storage.");
            }

            Span<FaceRecord> allFaces = faces.AsSpan();
            for (int batchIndex = 0; batchIndex < BatchCount; batchIndex++)
            {
                BatchSlot slot = _slots[batchIndex];
                PressureChunkShape shape = slot.Shape;
                Span<FaceRecord> chunkFaces = allFaces.Slice(
                    slot.RecordOffset,
                    Math.Max(1, shape.RecordCount));
                Span<Vertex> chunkVertices = allVertices.Slice(
                    slot.VertexOffset,
                    shape.VertexCount);
                Span<int> chunkIndices = allIndices.Slice(
                    slot.IndexOffset,
                    shape.IndexCount);
                int opaqueVertices = checked(
                    shape.OpaqueFaceCount * VoxelMath.VerticesPerFace);
                int opaqueIndices = checked(
                    shape.OpaqueFaceCount * VoxelMath.IndicesPerFace);
                PressureWorkContract.PackAliasedScatterStream(
                    chunkFaces.Slice(0, shape.OpaqueRecordCount),
                    chunkVertices.Slice(0, opaqueVertices),
                    chunkIndices.Slice(0, opaqueIndices),
                    allSlices.Slice(
                        slot.SliceOffset,
                        shape.OpaqueFaceCount));
                PressureWorkContract.PackAliasedScatterStream(
                    chunkFaces.Slice(
                        shape.OpaqueRecordCount,
                        shape.TransparentRecordCount),
                    chunkVertices.Slice(
                        opaqueVertices,
                        shape.VertexCount - opaqueVertices),
                    chunkIndices.Slice(
                        opaqueIndices,
                        shape.IndexCount - opaqueIndices),
                    allSlices.Slice(
                        checked(
                            slot.SliceOffset
                            + shape.OpaqueFaceCount),
                        shape.TransparentFaceCount));
            }
        }

        private void CompletePack(
            scoped NativeLeaseView<FaceRecord> faces,
            scoped NativeLeaseView<byte> payloadPatterns,
            scoped NativeLeaseView<Vertex> vertices,
            scoped NativeLeaseView<int> indices,
            scoped NativeLeaseView<PayloadSlice> slices)
        {
            Span<FaceRecord> allFaces = faces.AsSpan();
            ReadOnlySpan<byte> allPayloadPatterns =
                payloadPatterns.AsSpan();
            Span<Vertex> allVertices = vertices.AsSpan();
            Span<int> allIndices = indices.AsSpan();
            Span<PayloadSlice> allSlices = slices.AsSpan();
            for (int batchIndex = 0;
                batchIndex < BatchCount;
                batchIndex++)
            {
                BatchSlot slot = _slots[batchIndex];
                PressureChunkShape shape = slot.Shape;
                Span<FaceRecord> chunkFaces = allFaces.Slice(
                    slot.RecordOffset,
                    Math.Max(1, shape.RecordCount));
                Span<Vertex> chunkVertices = allVertices.Slice(
                    slot.VertexOffset,
                    shape.VertexCount);
                Span<int> chunkIndices = allIndices.Slice(
                    slot.IndexOffset,
                    shape.IndexCount);
                int opaqueVertices = checked(
                    shape.OpaqueFaceCount
                    * VoxelMath.VerticesPerFace);
                int opaqueIndices = checked(
                    shape.OpaqueFaceCount
                    * VoxelMath.IndicesPerFace);
                PressureOutputEvidence output =
                    PressureWorkContract.VerifyAndHashScatterOutput(
                        _request.Seed,
                        allPayloadPatterns,
                        chunkFaces.Slice(0, shape.OpaqueRecordCount),
                        chunkVertices.Slice(0, opaqueVertices),
                        chunkIndices.Slice(0, opaqueIndices),
                        allSlices.Slice(
                            slot.SliceOffset,
                            shape.OpaqueFaceCount),
                        chunkFaces.Slice(
                            shape.OpaqueRecordCount,
                            shape.TransparentRecordCount),
                        chunkVertices.Slice(
                            opaqueVertices,
                            shape.VertexCount - opaqueVertices),
                        chunkIndices.Slice(
                            opaqueIndices,
                            shape.IndexCount - opaqueIndices),
                        allSlices.Slice(
                            checked(
                                slot.SliceOffset
                                    + shape.OpaqueFaceCount),
                            shape.TransparentFaceCount));
                PressureChunkEvidence evidence =
                    CreateChunkEvidence(
                        slot,
                        output,
                        PressureWorkContract.CombineChunkEvidence(
                            slot.SectionEvidenceHash,
                            output.CompleteHash),
                        exactVerificationPassed: true);
                _slots[batchIndex] = slot with
                {
                    Output = output,
                    Evidence = evidence
                };
            }

            CompleteScatterHandoff(
                allFaces,
                allPayloadPatterns,
                allVertices,
                allIndices,
                allSlices);
        }

        internal void CompleteScatterHandoff()
        {
            for (int batchIndex = 0;
                batchIndex < BatchCount;
                batchIndex++)
            {
                BatchSlot slot = _slots[batchIndex];
                PressureOutputEvidence output =
                    PressureWorkContract.DescribeOutput(
                        slot.Shape);
                BatchSlot retained = slot with
                {
                    Output = output,
                    Evidence = CreateChunkEvidence(
                        slot,
                        output,
                        string.Empty,
                        exactVerificationPassed: false)
                };
                _slots[batchIndex] = retained;
                PublishScatterHandoff(retained);
            }
        }

        private void CompleteScatterHandoff(
            scoped Span<FaceRecord> allFaces,
            scoped ReadOnlySpan<byte> payloadPatterns,
            scoped Span<Vertex> allVertices,
            scoped Span<int> allIndices,
            scoped Span<PayloadSlice> allSlices)
        {
            for (int batchIndex = 0; batchIndex < BatchCount; batchIndex++)
            {
                BatchSlot retained = _slots[batchIndex];
                PressureChunkShape retainedShape = retained.Shape;
                Span<FaceRecord> retainedFaces = allFaces.Slice(
                    retained.RecordOffset,
                    Math.Max(1, retainedShape.RecordCount));
                int opaqueVertexCount = checked(
                    retainedShape.OpaqueFaceCount
                        * VoxelMath.VerticesPerFace);
                int opaqueIndexCount = checked(
                    retainedShape.OpaqueFaceCount
                        * VoxelMath.IndicesPerFace);
                PressureWorkContract.VerifyRetainedScatterOutput(
                    retained.Output,
                    _request.Seed,
                    payloadPatterns,
                    retainedFaces.Slice(
                        0,
                        retainedShape.OpaqueRecordCount),
                    allVertices.Slice(
                        retained.VertexOffset,
                        opaqueVertexCount),
                    allIndices.Slice(
                        retained.IndexOffset,
                        opaqueIndexCount),
                    allSlices.Slice(
                        retained.SliceOffset,
                        retainedShape.OpaqueFaceCount),
                    retainedFaces.Slice(
                        retainedShape.OpaqueRecordCount,
                        retainedShape.TransparentRecordCount),
                    allVertices.Slice(
                        retained.VertexOffset
                            + opaqueVertexCount,
                        retainedShape.VertexCount
                            - opaqueVertexCount),
                    allIndices.Slice(
                        retained.IndexOffset
                            + opaqueIndexCount,
                        retainedShape.IndexCount
                            - opaqueIndexCount),
                    allSlices.Slice(
                        retained.SliceOffset
                            + retainedShape.OpaqueFaceCount,
                        retainedShape.TransparentFaceCount));
                PublishScatterHandoff(retained);
            }
        }

        private static PressureChunkEvidence CreateChunkEvidence(
            BatchSlot slot,
            PressureOutputEvidence output,
            string exactEvidenceHash,
            bool exactVerificationPassed)
        {
            PressureChunkShape shape = slot.Shape;
            return new PressureChunkEvidence(
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
                shape.SectionDescriptorCount,
                shape.SectionValueCount,
                shape.SectionWordCount,
                shape.SectionStateWordCount,
                exactEvidenceHash,
                exactVerificationPassed);
        }

        private void PublishScatterHandoff(BatchSlot retained)
        {
            _consumed.Add(retained.Evidence);
            CompletedLogicalBytes = checked(
                CompletedLogicalBytes + retained.Demand);
        }

    }

    private sealed class OutputCapacityPlan
    {
        internal int CellCapacity { get; private set; }

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

        internal long SectionAdmissionCapacityBytes
        {
            get;
            private set;
        }

        internal int VertexCapacity { get; private set; }

        internal int IndexCapacity { get; private set; }

        internal int SliceCapacity { get; private set; }

        internal bool IsReady { get; set; }

        internal int Seed { get; set; }

        internal int RetentionDepth { get; set; }

        private long RequiredOutputTailCapacity => checked(
            NativeBatchContext.AlignmentAllowanceBytes
            + (long)VertexCapacity * VoxelMath.VertexBytes
            + (long)IndexCapacity * VoxelMath.IndexBytes
            + (long)SliceCapacity * VoxelMath.PayloadSliceBytes);

        internal nuint RequiredPhaseArenaCapacity => checked(
            (nuint)(
                NativeBatchContext.AlignmentAllowanceBytes
                + PressureWorkContract.PayloadPatternTableBytes
                + (long)CellCapacity * VoxelMath.VoxelCellBytes
                + (long)FaceCapacity * VoxelMath.FaceRecordBytes
                + (long)MaskCapacity * sizeof(ulong)
                + Math.Max(
                    SectionAdmissionCapacityBytes,
                    RequiredOutputTailCapacity)));

        internal static OutputCapacityPlan Create(
            PressureProfileRequest request,
            int retentionDepth)
        {
            OutputCapacityPlan plan = new();
            plan.Reset(
                request.Seed,
                retentionDepth);
            plan.CellCapacity = checked(
                retentionDepth
                * VoxelMath.CellsPerChunk);
            VoxelCell[]? cells = request.HasPlannedChunks
                ? null
                : GC.AllocateUninitializedArray<VoxelCell>(
                    VoxelMath.CellsPerChunk);
            SectionSummary[]? sections =
                request.HasPlannedChunks
                    ? null
                    : GC.AllocateUninitializedArray<SectionSummary>(
                        VoxelMath.SectionsPerChunk);
            long realizedDemand = 0;
            long retainedSectionBytes = 0;
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
                int batchSlices = 0;
                int batchRecords = 0;
                int batchMasks = 0;
                int batchDescriptors = 0;
                int batchValues = 0;
                int batchWords = 0;
                int batchStates = 0;
                int batchVertices = 0;
                int batchIndices = 0;
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
                    int candidateSlices = checked(
                        batchSlices + Math.Max(1, shape.FaceCount));
                    int candidateRecords = checked(
                        batchRecords
                        + Math.Max(1, shape.RecordCount));
                    int candidateMasks = checked(
                        batchMasks
                        + Math.Max(
                            1,
                            shape.TransparentMaskWords));
                    int candidateDescriptors = checked(
                        batchDescriptors
                        + shape.SectionDescriptorCount);
                    int candidateValues = checked(
                        batchValues + shape.SectionValueCount);
                    int candidateWords = checked(
                        batchWords + shape.SectionWordCount);
                    int candidateStates = checked(
                        batchStates
                        + shape.SectionStateWordCount);
                    batchSlices = candidateSlices;
                    batchRecords = candidateRecords;
                    batchMasks = candidateMasks;
                    batchDescriptors = candidateDescriptors;
                    batchValues = candidateValues;
                    batchWords = candidateWords;
                    batchStates = candidateStates;
                    batchVertices = checked(
                        batchVertices + shape.VertexCount);
                    batchIndices = checked(
                        batchIndices + shape.IndexCount);
                    realizedDemand = checked(
                        realizedDemand
                        + PressureWorkContract.CalculateLogicalDemand(
                            shape));
                    builtChunks++;
                    batchCount++;
                }

                long requiredSectionBytes = checked(
                    NativeBatchContext.AlignmentAllowanceBytes
                    + CalculateSectionBytes(
                        batchDescriptors,
                        batchValues,
                        batchWords,
                        batchStates));
                retainedSectionBytes = Math.Max(
                    retainedSectionBytes,
                    requiredSectionBytes);
                plan.SectionAdmissionCapacityBytes =
                    retainedSectionBytes;
                plan.VertexCapacity = Math.Max(
                    plan.VertexCapacity,
                    batchVertices);
                plan.FaceCapacity = Math.Max(
                    plan.FaceCapacity,
                    batchRecords);
                plan.MaskCapacity = Math.Max(
                    plan.MaskCapacity,
                    batchMasks);
                plan.SectionDescriptorCapacity = Math.Max(
                    plan.SectionDescriptorCapacity,
                    batchDescriptors);
                plan.SectionValueCapacity = Math.Max(
                    plan.SectionValueCapacity,
                    batchValues);
                plan.SectionWordCapacity = Math.Max(
                    plan.SectionWordCapacity,
                    batchWords);
                plan.SectionStateCapacity = Math.Max(
                    plan.SectionStateCapacity,
                    batchStates);
                plan.IndexCapacity = Math.Max(
                    plan.IndexCapacity,
                    batchIndices);
                plan.SliceCapacity = Math.Max(
                    plan.SliceCapacity,
                    batchSlices);
            }

            plan.IsReady = true;
            return plan;
        }

        private static long CalculateSectionBytes(
            int descriptors,
            int values,
            int words,
            int states) =>
            checked(
                (long)descriptors
                    * VoxelMath.SectionPrerenderDescriptorBytes
                + (long)values * sizeof(ushort)
                + (long)words * sizeof(uint)
                + (long)states * sizeof(ulong));

        internal void Reset(int seed, int retentionDepth)
        {
            CellCapacity = 0;
            FaceCapacity = 0;
            MaskCapacity = 0;
            SectionDescriptorCapacity = 0;
            SectionValueCapacity = 0;
            SectionWordCapacity = 0;
            SectionStateCapacity = 0;
            SectionAdmissionCapacityBytes = 0;
            VertexCapacity = 0;
            IndexCapacity = 0;
            SliceCapacity = 0;
            IsReady = false;
            Seed = seed;
            RetentionDepth = retentionDepth;
        }

        internal void EnsureFits(NativeBatchContext context)
        {
            if (context.TotalRecords > FaceCapacity
                || context.TotalMaskWords > MaskCapacity
                || context.TotalSectionDescriptors
                    > SectionDescriptorCapacity
                || context.TotalSectionValues
                    > SectionValueCapacity
                || context.TotalSectionWords
                    > SectionWordCapacity
                || context.TotalSectionStateWords
                    > SectionStateCapacity
                || context.TotalVertices > VertexCapacity
                || context.TotalIndices > IndexCapacity
                || context.TotalFaces > SliceCapacity)
            {
                throw new InvalidOperationException(
                    $"The runtime shape exceeds its plan: "
                    + $"{context.TotalRecords}/{FaceCapacity} records, "
                    + $"{context.TotalMaskWords}/{MaskCapacity} masks, "
                    + $"{context.TotalSectionDescriptors}/"
                    + $"{SectionDescriptorCapacity} descriptors, "
                    + $"{context.TotalSectionValues}/"
                    + $"{SectionValueCapacity} values, "
                    + $"{context.TotalSectionWords}/"
                    + $"{SectionWordCapacity} words, "
                    + $"{context.TotalSectionStateWords}/"
                    + $"{SectionStateCapacity} states, "
                    + $"{context.TotalVertices}/"
                    + $"{VertexCapacity} vertices, "
                    + $"{context.TotalIndices}/"
                    + $"{IndexCapacity} indices, "
                    + $"{context.TotalFaces}/"
                    + $"{SliceCapacity} slices.");
            }
        }
    }

    private sealed class ProfileWorkItem
    {
        internal ProfileWorkItem(
            PressureProfileRequest request,
            Action<PressureProgress> reportProgress)
        {
            Request = request;
            ReportProgress = reportProgress;
        }

        internal PressureProfileRequest Request { get; }

        internal Action<PressureProgress> ReportProgress { get; }

        internal TaskCompletionSource<PressureProfileResult> Completion
        {
            get;
        } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal PressureProfileResult Result { get; set; }

        internal Exception? Failure { get; set; }

        internal void Complete()
        {
            if (Failure is not null)
            {
                Completion.TrySetException(
                    new InvalidOperationException(
                        "The persistent native pressure owner failed.",
                        Failure));
                return;
            }

            Completion.TrySetResult(Result);
        }
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
        int SectionDescriptorOffset,
        int SectionValueOffset,
        int SectionWordOffset,
        int SectionStateWordOffset,
        long CumulativeDemand,
        string SectionEvidenceHash = "",
        PressureOutputEvidence Output = default,
        PressureChunkEvidence Evidence = default);
}
