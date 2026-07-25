using System.Diagnostics;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SafeCSharp;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            string mode = args.Length == 0 ? "--correctness" : args[0];
            VoxelWorkloadOptions options = VoxelWorkloadOptions.Parse(args.AsSpan(1).ToArray());
            if (mode is not ("--correctness" or "--run" or "--pressure"))
            {
                throw new ArgumentException($"Unknown mode '{mode}'.");
            }

            ChildRunResult result = RunMeasured(
                options,
                captureMeasuredFixture: false,
                pressureMode: mode == "--pressure",
                includeCanonicalInputCells: mode == "--correctness");
            Console.WriteLine(JsonSerializer.Serialize(result, VoxelJson.Options));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 2;
        }
    }

    private static ChildRunResult RunMeasured(
        VoxelWorkloadOptions options,
        bool captureMeasuredFixture,
        bool pressureMode,
        bool includeCanonicalInputCells)
    {
        if (!pressureMode)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        CgroupMemorySnapshot cgroupBefore = CgroupMemorySnapshot.Read();
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        long coldAllocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        using WorkingSetSampler sampler = new();
        PipelineResult result = SafeVoxelPipeline.Run(
            options,
            captureMeasuredFixture,
            includeCanonicalInputCells);
        long coldManagedAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - coldAllocationBefore;
        TimeSpan pauseAfter = GC.GetTotalPauseDuration();
        CgroupMemorySnapshot cgroupAfter = CgroupMemorySnapshot.Read();
        sampler.Stop();
        if (!pressureMode)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        PressureRunMetrics? pressure = pressureMode
            ? new PressureRunMetrics(
                true,
                cgroupAfter.Available,
                cgroupAfter.LimitBytes,
                cgroupBefore.CurrentBytes,
                cgroupAfter.CurrentBytes,
                cgroupAfter.PeakBytes,
                Math.Max(0, cgroupAfter.OomEvents - cgroupBefore.OomEvents),
                Math.Max(0, cgroupAfter.OomKillEvents - cgroupBefore.OomKillEvents),
                cgroupAfter.AnonBytes,
                cgroupAfter.FileBytes,
                memory.TotalAvailableMemoryBytes,
                memory.MemoryLoadBytes,
                memory.HighMemoryLoadThresholdBytes,
                memory.TotalCommittedBytes,
                memory.HeapSizeBytes,
                GetLargeObjectHeapBytes(memory),
                memory.FragmentedBytes,
                Math.Max(0, (pauseAfter - pauseBefore).TotalMilliseconds))
            : null;
        return new ChildRunResult(
            "SafeCSharp",
            result,
            result.MeasuredMilliseconds,
            result.MeasuredManagedAllocatedBytes,
            result.MeasuredGen0Collections,
            result.MeasuredGen1Collections,
            result.MeasuredGen2Collections,
            memory.HeapSizeBytes,
            sampler.PeakWorkingSetBytes,
            GetLargeObjectHeapBytes(memory),
            coldManagedAllocatedBytes,
            pressure);
    }

    private static long GetLargeObjectHeapBytes(GCMemoryInfo memory) =>
        memory.GenerationInfo.Length > 3 ? memory.GenerationInfo[3].SizeAfterBytes : 0;


    private sealed class WorkingSetSampler : IDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;
        private long _peakWorkingSetBytes;

        internal WorkingSetSampler()
        {
            _peakWorkingSetBytes = _process.WorkingSet64;
            _task = Task.Run(SampleLoop);
        }

        internal long PeakWorkingSetBytes => Volatile.Read(ref _peakWorkingSetBytes);

        internal void Stop()
        {
            _stop.Cancel();
            _task.GetAwaiter().GetResult();
            Sample();
        }

        private void SampleLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                Sample();
                Thread.Sleep(1);
            }
        }

        private void Sample()
        {
            long current = _process.WorkingSet64;
            long prior;
            do
            {
                prior = Volatile.Read(ref _peakWorkingSetBytes);
                if (current <= prior)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _peakWorkingSetBytes, current, prior) != prior);
        }

        public void Dispose()
        {
            Stop();
            _stop.Dispose();
            _process.Dispose();
        }
    }
}
