namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

/// <summary>
/// Observation-window metrics. Sampled peak is a lower bound unless a new kernel
/// high water is observed. CPU mean includes only measured samples; deltas are
/// unavailable when an endpoint is missing or a counter decreases.
/// </summary>
public readonly record struct PressureExternalSummary(
    long? ObservedPeakBytes,
    double? CpuPercentMean,
    double? CpuPercentPeak,
    int CpuSampleCount,
    bool NewKernelHighWater,
    double? EffectiveCpuCores,
    long? PageFaultsDelta,
    long? MajorPageFaultsDelta)
{
    public static PressureExternalSummary Capture(
        CgroupMemorySnapshot before,
        CgroupMemorySnapshot after,
        IReadOnlyList<PressureHostSample> samples,
        double? elapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        long? peak = ExternalObservation.Maximum(before.CurrentBytes, after.CurrentBytes);
        int cpuSamples = 0;
        double cpuMean = 0;
        double? cpuPeak = null;
        for (int index = 0; index < samples.Count; index++)
        {
            PressureHostSample sample = samples[index];
            peak = ExternalObservation.Maximum(peak, sample.CgroupMemoryBytes);
            if (sample.CpuPercent is >= 0 and { } cpu && double.IsFinite(cpu))
            {
                cpuMean += (cpu - cpuMean) / ++cpuSamples;
                cpuPeak = cpuPeak.HasValue ? Math.Max(cpuPeak.Value, cpu) : cpu;
            }
        }

        bool newHighWater = before.PeakBytes.HasValue && after.PeakBytes > before.PeakBytes;
        if (newHighWater)
        {
            peak = ExternalObservation.Maximum(peak, after.PeakBytes);
        }

        return new(peak, cpuSamples > 0 ? cpuMean : null, cpuPeak, cpuSamples, newHighWater,
            ExternalObservation.CpuCores(ExternalObservation.Delta(before.CpuUsageMicroseconds, after.CpuUsageMicroseconds), elapsedMilliseconds),
            ExternalObservation.Delta(before.PageFaults, after.PageFaults),
            ExternalObservation.Delta(before.MajorPageFaults, after.MajorPageFaults));
    }
}
