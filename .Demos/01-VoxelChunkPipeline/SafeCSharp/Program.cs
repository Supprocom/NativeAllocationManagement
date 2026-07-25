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
        long coldAllocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        using WorkingSetSampler sampler = new();
        PipelineResult result = SafeVoxelPipeline.Run(options);
        long coldManagedAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - coldAllocationBefore;
        sampler.Stop();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
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
            coldManagedAllocatedBytes);
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
