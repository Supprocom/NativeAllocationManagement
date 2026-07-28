using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public enum PressureProfileOutcome
{
    Completed,
    DeadlineExceeded,
    OutOfMemory,
    IncorrectOutput,
    Crash,
    HarnessFailure
}

public enum VoxelPipelineStage
{
    None,
    Build,
    Render,
    Prerender,
    GpuUpload,
    Verification,
    Completed
}

public enum PressureCommandKind
{
    Hello,
    Warmup,
    RunProfile,
    VerifyProfile,
    BeginProcessing,
    Shutdown
}

public enum PressureExecutionMode
{
    Measurement,
    Verification
}

public enum PressureEnvelopeKind
{
    Ready,
    Progress,
    Result,
    Failure,
    Goodbye
}

public enum PressureProgressKind
{
    ProcessingReady,
    Checkpoint,
    ProcessingCompleted
}

public readonly record struct PressureProfileRequest(
    int ProfilePercent,
    long CgroupCapBytes,
    long RequestedCumulativeDemandBytes,
    double DeadlineMilliseconds,
    int Seed,
    int RetentionDepth,
    int ProgressEveryChunks,
    bool Warmup = false,
    PressureExecutionMode ExecutionMode =
        PressureExecutionMode.Verification,
    PressureChunkPlanEntry[]? PlannedChunks = null,
    long RequestOrdinal = 0,
    PressureDiagnosticRequest? Diagnostic = null)
{
    public bool HasPlannedChunks =>
        PlannedChunks is { Length: > 0 };

    public int PlannedChunkCount =>
        PlannedChunks?.Length ?? 0;

    public bool RequiresExactVerification =>
        ExecutionMode == PressureExecutionMode.Verification
        || Diagnostic is { VerifyExactOutput: true };

    public void Validate()
    {
        if (ProfilePercent <= 0
            || CgroupCapBytes <= 0
            || RequestedCumulativeDemandBytes <= 0
            || DeadlineMilliseconds <= 0
            || RetentionDepth <= 0
            || ProgressEveryChunks <= 0
            || RequestOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PressureProfileRequest),
                "Pressure profile sizes, deadline, retention, and progress cadence must be positive.");
        }

        if (PlannedChunks is null)
        {
            return;
        }

        if (PlannedChunks.Length == 0
            || PlannedChunks.Any(
                static chunk =>
                    chunk.ChunkId < 0
                    || chunk.LogicalDemandBytes <= 0
                    || chunk.EstimatedWorkUnits <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(PlannedChunks),
                "A planned chunk sequence contains an invalid chunk.");
        }
    }

    public bool NeedsChunk(
        int completedChunks,
        long realizedDemandBytes,
        int minimumChunks) =>
        HasPlannedChunks
            ? completedChunks < PlannedChunkCount
            : realizedDemandBytes
                < RequestedCumulativeDemandBytes
                || completedChunks < minimumChunks;

    public int GetChunkId(int localChunkIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(
            localChunkIndex);
        if (PlannedChunks is null)
        {
            return localChunkIndex;
        }

        if ((uint)localChunkIndex
            >= (uint)PlannedChunks.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localChunkIndex));
        }

        return PlannedChunks[localChunkIndex].ChunkId;
    }

    public PressureChunkShape GetChunkShape(
        int localChunkIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(
            localChunkIndex);
        if (PlannedChunks is null
            || (uint)localChunkIndex
                >= (uint)PlannedChunks.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localChunkIndex));
        }

        return PlannedChunks[localChunkIndex].Shape;
    }
}

public readonly record struct PressureCommand(
    string RequestId,
    PressureCommandKind Kind,
    PressureProfileRequest? Profile = null,
    long CommandOrdinal = 0);

public readonly record struct PressureProgress(
    string Implementation,
    int ProfilePercent,
    PressureProgressKind Kind,
    int CompletedChunks,
    long CompletedLogicalBytes,
    VoxelPipelineStage LastCompletedStage,
    int LastCompletedChunkId);

public readonly record struct PressureChunkEvidence(
    int ChunkId,
    long SourceInputBytes,
    long LogicalDemandBytes,
    int OpaqueVertexLength,
    int OpaqueIndexLength,
    int OpaqueSliceLength,
    int OpaqueUploadLength,
    int TransparentVertexLength,
    int TransparentIndexLength,
    int TransparentSliceLength,
    int TransparentUploadLength,
    int SectionDescriptorLength,
    int SectionValueLength,
    int SectionWordLength,
    int SectionStateWordLength,
    string ExactEvidenceHash,
    bool ExactVerificationPassed);

public readonly record struct PressureCompilationConfiguration(
    string TieredCompilation,
    string TieredPgo)
{
    public static PressureCompilationConfiguration Capture() =>
        new(
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredCompilation") ?? string.Empty,
            Environment.GetEnvironmentVariable(
                "DOTNET_TieredPGO") ?? string.Empty);
}

public readonly record struct PressureRuntimeSnapshot(
    DateTime Utc,
    long TotalAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double TotalPauseMilliseconds,
    long TotalAvailableMemoryBytes,
    long MemoryLoadBytes,
    long HighMemoryLoadThresholdBytes,
    long TotalCommittedBytes,
    long HeapSizeBytes,
    long FragmentedBytes,
    long LargeObjectHeapBytes,
    long ProcessWorkingSetBytes,
    double ProcessCpuMilliseconds,
    int ProcessorCount,
    CgroupMemorySnapshot Cgroup,
    IReadOnlyDictionary<string, string> GcConfiguration,
    PressureCompilationConfiguration CompilationConfiguration = default)
{
    public static PressureRuntimeSnapshot Capture()
    {
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        using Process process = Process.GetCurrentProcess();
        Dictionary<string, string> configuration = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object> entry in GC.GetConfigurationVariables())
        {
            configuration[entry.Key] = Convert.ToString(
                entry.Value,
                CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return new PressureRuntimeSnapshot(
            DateTime.UtcNow,
            GC.GetTotalAllocatedBytes(precise: true),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GC.GetTotalPauseDuration().TotalMilliseconds,
            memory.TotalAvailableMemoryBytes,
            memory.MemoryLoadBytes,
            memory.HighMemoryLoadThresholdBytes,
            memory.TotalCommittedBytes,
            memory.HeapSizeBytes,
            memory.FragmentedBytes,
            memory.GenerationInfo.Length > 3 ? memory.GenerationInfo[3].SizeAfterBytes : 0,
            process.WorkingSet64,
            process.TotalProcessorTime.TotalMilliseconds,
            Environment.ProcessorCount,
            CgroupMemorySnapshot.Read(),
            configuration,
            PressureCompilationConfiguration.Capture());
    }
}

public static class PressureCompilationPolicy
{
    public static bool HasEquivalentDisabledTiering(
        PressureRuntimeSnapshot safe,
        PressureRuntimeSnapshot nam)
    {
        PressureCompilationConfiguration safeConfiguration =
            safe.CompilationConfiguration;
        PressureCompilationConfiguration namConfiguration =
            nam.CompilationConfiguration;
        return IsDisabled(safeConfiguration)
            && IsDisabled(namConfiguration)
            && string.Equals(
                safeConfiguration.TieredCompilation,
                namConfiguration.TieredCompilation,
                StringComparison.Ordinal)
            && string.Equals(
                safeConfiguration.TieredPgo,
                namConfiguration.TieredPgo,
                StringComparison.Ordinal);
    }

    private static bool IsDisabled(
        PressureCompilationConfiguration configuration) =>
        string.Equals(
            configuration.TieredCompilation,
            "0",
            StringComparison.Ordinal)
        && string.Equals(
            configuration.TieredPgo,
            "0",
            StringComparison.Ordinal);
}

public readonly record struct PressureProfileResult(
    string Implementation,
    PressureProfileOutcome Outcome,
    int ProfilePercent,
    PressureExecutionMode ExecutionMode,
    long CgroupCapBytes,
    long RequestedCumulativeDemandBytes,
    long RealizedCumulativeDemandBytes,
    long DemandOvershootBytes,
    double DeadlineMilliseconds,
    int CompletedChunks,
    long CompletedLogicalBytes,
    long SourceInputBytes,
    long PeakLiveLogicalBytes,
    double AllocatorTurnover,
    int RetentionDepth,
    int PeakRetentionDepth,
    long AdmissionBudgetBytes,
    int AdmissionThrottleCount,
    VoxelPipelineStage LastCompletedStage,
    int LastCompletedChunkId,
    bool CorrectnessPassed,
    string CanonicalEvidenceHash,
    IReadOnlyList<PressureChunkEvidence> ChunkEvidence,
    PressureRuntimeSnapshot Before,
    PressureRuntimeSnapshot After,
    long ManagedAllocationDeltaBytes,
    int Gen0Delta,
    int Gen1Delta,
    int Gen2Delta,
    double PauseDeltaMilliseconds,
    long NativePeakBytes,
    long NativeRetainedBytes,
    long NativeFinalBytes,
    long TypedPhysicalReuseCount,
    long ScopedPhysicalReuseCount,
    long ScopedPhysicalReuseBytes,
    IReadOnlyList<NativeOwnerProfile>? NativeOwners,
    string? ExceptionType = null,
    string? ExceptionMessage = null,
    int ActiveWorkerCount = 1,
    IReadOnlyList<long>? WorkerBudgetBytes = null,
    IReadOnlyList<PressureWorkerCapacity>? WorkerCapacities = null,
    PressureSessionState? StateAfterReset = null,
    PressureRequestDiagnostics? Diagnostics = null);

public readonly record struct PressureSessionState(
    string Implementation,
    long RequestOrdinal,
    long CompletedRequestCount,
    int ActiveRetentionSlots,
    int ActiveSectionEntries,
    int EvidenceEntries,
    int HashStateEntries,
    long LogicalCursorBytes,
    long CompletedLogicalBytes,
    long MappedUploadPosition,
    long ActiveAllocationBytes,
    long RetainedCapacityBytes,
    long PersistentAllocationBytes,
    long AllocationPlanFingerprint,
    long AllocationGeneration,
    int PendingCommandCount,
    PressureRuntimeSnapshot RuntimeBaseline)
{
    public bool LogicalResetPassed =>
        ActiveRetentionSlots == 0
        && ActiveSectionEntries == 0
        && EvidenceEntries == 0
        && HashStateEntries == 0
        && LogicalCursorBytes == 0
        && CompletedLogicalBytes == 0
        && MappedUploadPosition == 0
        && ActiveAllocationBytes == 0
        && PendingCommandCount == 0;
}

public static class PressureStateFingerprint
{
    public static long Compute(params long[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        unchecked
        {
            ulong hash = 14_695_981_039_346_656_037;
            for (int index = 0; index < values.Length; index++)
            {
                hash ^= (ulong)values[index];
                hash *= 1_099_511_628_211;
            }

            long result = (long)(hash & long.MaxValue);
            return result == 0 ? 1 : result;
        }
    }
}

public readonly record struct PressureEnvelope(
    string RequestId,
    PressureEnvelopeKind Kind,
    string Implementation,
    PressureProgress? Progress = null,
    PressureProfileResult? Result = null,
    PressureRuntimeSnapshot? Runtime = null,
    string? ErrorType = null,
    string? ErrorMessage = null);

public interface IPressureProfileSession : IDisposable
{
    string Implementation { get; }

    PressureProfileResult Run(
        PressureProfileRequest request,
        Action<PressureProgress> reportProgress);
}

public interface IQueuedPressureProfileSession :
    IPressureProfileSession
{
    Task<PressureProfileResult> QueueAsync(
        PressureProfileRequest request,
        Action<PressureProgress> reportProgress);
}

public readonly record struct PressureWorkerCapacity(
    long MinimumRetainedBytes,
    long SafetyReserveBytes,
    long PreferredRetainedBytes);

public interface IPressureWorkerCapacityPlanner
{
    PressureWorkerCapacity PlanWorkerCapacity(
        PressureProfileRequest request);
}

public static class PressureProtocolServer
{
    public static int Run(IPressureProfileSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            Write(new PressureEnvelope(
                "startup",
                PressureEnvelopeKind.Ready,
                session.Implementation,
                Runtime: PressureRuntimeSnapshot.Capture()));
            string? line;
            while ((line = Console.ReadLine()) is not null)
            {
                PressureCommand command;
                try
                {
                    command = JsonSerializer.Deserialize<PressureCommand>(
                        line.TrimStart('\uFEFF'),
                        VoxelJson.Options);
                }
                catch (Exception exception)
                {
                    WriteFailure("unparsed", session.Implementation, exception);
                    continue;
                }

                try
                {
                    switch (command.Kind)
                    {
                        case PressureCommandKind.Hello:
                            Write(new PressureEnvelope(
                                command.RequestId,
                                PressureEnvelopeKind.Ready,
                                session.Implementation,
                                Runtime: PressureRuntimeSnapshot.Capture()));
                            break;
                        case PressureCommandKind.Warmup:
                        case PressureCommandKind.RunProfile:
                        case PressureCommandKind.VerifyProfile:
                            PressureProfileRequest request = command.Profile
                                ?? throw new InvalidDataException("A profile command requires a profile request.");
                            if (command.CommandOrdinal <= 0)
                            {
                                throw new InvalidDataException(
                                    "A profile command requires a positive command ordinal.");
                            }

                            request.Validate();
                            PressureProfileResult result = session.Run(
                                request with
                                {
                                    Warmup = command.Kind
                                        == PressureCommandKind.Warmup,
                                    ExecutionMode = command.Kind
                                        == PressureCommandKind.VerifyProfile
                                        ? PressureExecutionMode.Verification
                                        : PressureExecutionMode.Measurement,
                                    RequestOrdinal = command.CommandOrdinal
                                },
                                progress => ReportProgress(
                                    command.RequestId,
                                    command.CommandOrdinal,
                                    session.Implementation,
                                    progress));
                            Write(new PressureEnvelope(
                                command.RequestId,
                                PressureEnvelopeKind.Result,
                                session.Implementation,
                                Result: result));
                            break;
                        case PressureCommandKind.Shutdown:
                            Write(new PressureEnvelope(
                                command.RequestId,
                                PressureEnvelopeKind.Goodbye,
                                session.Implementation));
                            return 0;
                        case PressureCommandKind.BeginProcessing:
                            throw new InvalidDataException(
                                "BeginProcessing is valid only after ProcessingReady.");
                        default:
                            throw new InvalidDataException($"Unknown pressure command '{command.Kind}'.");
                    }
                }
                catch (Exception exception)
                {
                    WriteFailure(command.RequestId, session.Implementation, exception);
                }
            }

            return 0;
        }
        finally
        {
            session.Dispose();
        }
    }

    private static void WriteFailure(string requestId, string implementation, Exception exception) =>
        Write(new PressureEnvelope(
            requestId,
            PressureEnvelopeKind.Failure,
            implementation,
            ErrorType: exception.GetType().FullName,
            ErrorMessage: exception.Message));

    private static void ReportProgress(
        string requestId,
        long commandOrdinal,
        string implementation,
        PressureProgress progress)
    {
        Write(new PressureEnvelope(
            requestId,
            PressureEnvelopeKind.Progress,
            implementation,
            Progress: progress));
        if (progress.Kind != PressureProgressKind.ProcessingReady)
        {
            return;
        }

        string line = Console.ReadLine()
            ?? throw new EndOfStreamException(
                "The host ended the protocol before BeginProcessing.");
        PressureCommand begin = JsonSerializer.Deserialize<PressureCommand>(
            line.TrimStart('\uFEFF'),
            VoxelJson.Options);
        if (!string.Equals(
                begin.RequestId,
                requestId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "BeginProcessing has an incorrect request identifier.");
        }

        if (begin.Kind != PressureCommandKind.BeginProcessing
            || begin.Profile is not null
            || begin.CommandOrdinal != commandOrdinal)
        {
            throw new InvalidDataException(
                "ProcessingReady requires the matching BeginProcessing command without a profile.");
        }
    }

    private static void Write(PressureEnvelope envelope)
    {
        Console.WriteLine(JsonSerializer.Serialize(envelope, VoxelJson.Options));
        Console.Out.Flush();
    }
}
