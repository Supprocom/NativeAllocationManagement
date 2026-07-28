using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

public readonly record struct PressureMatrixOptionsSnapshot(
    string RepositoryRoot,
    string Image,
    string OutputPath,
    string ActivityPath,
    string SafeCpuSet,
    string NamCpuSet,
    long CgroupCapBytes,
    double DeadlineMilliseconds,
    int RetentionDepth,
    int ProgressEveryChunks,
    int Seed,
    int PidsLimit,
    int GcHeapHardLimitPercent,
    int SamplesPerProfile,
    IReadOnlyList<int> ProfilePercents,
    int InactivityTimeoutSeconds,
    long AbsoluteFailSafeTimeoutSeconds,
    bool Enforce);

public readonly record struct PressureWorkerCheckpoint(
    string Implementation,
    string ContainerName,
    string ContainerId,
    int ContainerProcessId,
    string CgroupIdentity,
    PressureRuntimeSnapshot StartupRuntime,
    PressureEffectiveIsolation Isolation,
    long CurrentRequestOrdinal,
    bool IsAlive);

public readonly record struct PressureCurrentProfileCheckpoint(
    int ProfilePercent,
    long CgroupCapBytes,
    long RequestedCumulativeDemandBytes,
    IReadOnlyList<PressurePairedObservation> CompletedPairs,
    IReadOnlyList<PressureProfileInitialization> Initializations);

public readonly record struct PressurePreparationSeriesCheckpoint(
    int PairOrdinal,
    int SampleIndex,
    bool SafeRanFirst,
    string Implementation,
    int ProfilePercent,
    long RequestedCumulativeDemandBytes,
    double ElapsedMilliseconds,
    IReadOnlyList<PressureImplementationObservation> Attempts,
    PressurePreparationAssessment Assessment);

public readonly record struct PressureCurrentPairCheckpoint(
    int PairOrdinal,
    int SampleIndex,
    bool SafeRanFirst,
    PressureImplementationObservation? SafeObservation,
    PressureImplementationObservation? NamObservation,
    bool SafeTimedRequestStarted,
    bool NamTimedRequestStarted,
    IReadOnlyList<PressurePreparationSeriesCheckpoint> Preparations);

public readonly record struct PressureMatrixCheckpoint(
    int FormatVersion,
    long Sequence,
    string GitCommit,
    string ImageId,
    IReadOnlyList<PressureBinaryIdentity> BinaryIdentities,
    PressureMatrixOptionsSnapshot Options,
    IReadOnlyList<PressureProfilePair> CompletedProfiles,
    PressureCurrentProfileCheckpoint? CurrentProfile,
    PressureCurrentPairCheckpoint? CurrentPair,
    IReadOnlyList<string> Commands,
    IReadOnlyList<PressureWorkerLifecycle> CompletedLifecycles,
    IReadOnlyList<PressureWorkerCheckpoint> ActiveWorkers,
    DateTime StartedUtc,
    DateTime UpdatedUtc);

public static class AtomicPressureArtifactFile
{
    public static async Task WriteJsonAsync<T>(
        string path,
        T value,
        JsonSerializerOptions? serializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(
                "The artifact path must have a parent directory.",
                nameof(path));
        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                65_536,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    serializerOptions ?? VoxelJson.Options,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
