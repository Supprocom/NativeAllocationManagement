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
            if (mode is not ("--correctness" or "--run"))
            {
                throw new ArgumentException($"Unknown mode '{mode}'.");
            }

            ChildRunResult result = RunMeasured(options);
            Console.WriteLine(JsonSerializer.Serialize(result, VoxelJson.Options));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 2;
        }
    }

    private static ChildRunResult RunMeasured(VoxelWorkloadOptions options)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using WorkingSetSampler sampler = new();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        Stopwatch clock = Stopwatch.StartNew();
        PipelineResult result = SafeVoxelPipeline.Run(options);
        clock.Stop();
        sampler.Stop();
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        return new ChildRunResult(
            "SafeCSharp",
            result,
            clock.Elapsed.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            memory.HeapSizeBytes,
            sampler.PeakWorkingSetBytes,
            GetLargeObjectHeapBytes(memory),
            GetPauseMilliseconds(memory));
    }

    private static long GetLargeObjectHeapBytes(GCMemoryInfo memory) =>
        memory.GenerationInfo.Length > 3 ? memory.GenerationInfo[3].SizeAfterBytes : 0;

    private static long GetPauseMilliseconds(GCMemoryInfo memory)
    {
        double total = 0;
        foreach (TimeSpan duration in memory.PauseDurations)
        {
            total += duration.TotalMilliseconds;
        }

        return checked((long)total);
    }

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
