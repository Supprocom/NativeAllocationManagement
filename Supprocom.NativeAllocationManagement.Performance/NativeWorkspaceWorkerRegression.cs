using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class NativeWorkspaceWorkerRegression
{
    internal const int BuildCount = 1_179;
    internal const int WorkspaceLength = 153_600;
    internal const int SampleCount = 6;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    internal static async Task<int> RunCommandAsync(string outputPath)
    {
        NativeWorkspaceWorkerReport report = Run();
        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return report.Passed ? 0 : 3;
    }

    internal static NativeWorkspaceWorkerReport Run()
    {
        NativeMemoryTestHooks.Reset();
        Stopwatch setupClock = Stopwatch.StartNew();
        long setupAllocationBefore = GC.GetTotalAllocatedBytes(true);
        using PersistentWorkspaceWorker persistent = new();
        long setupAllocatedBytes = GC.GetTotalAllocatedBytes(true)
            - setupAllocationBefore;
        setupClock.Stop();

        var samples = new List<NativeWorkspaceWorkerSample>(
            SampleCount * 3);
        try
        {
            _ = persistent.RunBuilds();
            for (int sampleIndex = 0;
                sampleIndex < SampleCount;
                sampleIndex++)
            {
                foreach (NativeWorkspaceWorkerImplementation implementation
                    in GetOrder(sampleIndex))
                {
                    samples.Add(Measure(
                        sampleIndex,
                        implementation,
                        persistent));
                }
            }

            string managedHash = VerifyExact(
                NativeWorkspaceWorkerImplementation.ManagedScratch,
                persistent);
            string persistentHash = VerifyExact(
                NativeWorkspaceWorkerImplementation.PersistentWorkspace,
                persistent);
            string transientHash = VerifyExact(
                NativeWorkspaceWorkerImplementation.TransientWorkspace,
                persistent);
            NativeMemoryTestMetrics beforeDispose =
                NativeMemoryTestHooks.Snapshot();
            persistent.Dispose();
            NativeMemoryTestMetrics afterDispose =
                NativeMemoryTestHooks.Snapshot();

            bool balancedOrder = IsBalancedOrder(samples);
            bool exactParity = managedHash == persistentHash
                && managedHash == transientHash
                && samples.Select(static sample => sample.Checksum)
                    .Distinct()
                    .Count() == 1;
            bool zeroTimedPersistentGrowth = samples
                .Where(static sample => sample.Implementation
                    == NativeWorkspaceWorkerImplementation
                        .PersistentWorkspace)
                .All(static sample =>
                    sample.NativeAllocationCount == 0
                    && sample.NativeFreeCount == 0);
            bool transientOwnership = samples
                .Where(static sample => sample.Implementation
                    == NativeWorkspaceWorkerImplementation
                        .TransientWorkspace)
                .All(static sample =>
                    sample.NativeAllocationCount == BuildCount
                    && sample.NativeFreeCount == BuildCount);
            bool exactlyOnceCleanup = beforeDispose.AllocationCount
                    - beforeDispose.FreeCount == 1
                && afterDispose.AllocationCount
                    == afterDispose.FreeCount
                && afterDispose.OutstandingNativeBytes == 0;

            double managedMean = Mean(
                samples,
                NativeWorkspaceWorkerImplementation.ManagedScratch,
                static sample => sample.ElapsedMilliseconds);
            double persistentMean = Mean(
                samples,
                NativeWorkspaceWorkerImplementation.PersistentWorkspace,
                static sample => sample.ElapsedMilliseconds);
            double transientMean = Mean(
                samples,
                NativeWorkspaceWorkerImplementation.TransientWorkspace,
                static sample => sample.ElapsedMilliseconds);
            double[] pairedSpeedups = Enumerable.Range(0, SampleCount)
                .Select(sampleIndex =>
                    Find(
                        samples,
                        sampleIndex,
                        NativeWorkspaceWorkerImplementation.ManagedScratch)
                        .ElapsedMilliseconds
                    / Find(
                        samples,
                        sampleIndex,
                        NativeWorkspaceWorkerImplementation
                            .PersistentWorkspace)
                        .ElapsedMilliseconds)
                .ToArray();
            double aggregateSpeedup = samples
                    .Where(static sample => sample.Implementation
                        == NativeWorkspaceWorkerImplementation.ManagedScratch)
                    .Sum(static sample => sample.ElapsedMilliseconds)
                / samples
                    .Where(static sample => sample.Implementation
                        == NativeWorkspaceWorkerImplementation
                            .PersistentWorkspace)
                    .Sum(static sample => sample.ElapsedMilliseconds);

            return new NativeWorkspaceWorkerReport(
                BuildCount,
                WorkspaceLength,
                SampleCount,
                setupClock.Elapsed.TotalMilliseconds,
                setupAllocatedBytes,
                samples,
                managedMean,
                persistentMean,
                transientMean,
                pairedSpeedups.Average(),
                aggregateSpeedup,
                PairedBenchmarkStatistics.ConfidenceLower95(
                    pairedSpeedups),
                Mean(
                    samples,
                    NativeWorkspaceWorkerImplementation.ManagedScratch,
                    static sample => sample.ManagedAllocatedBytes),
                Mean(
                    samples,
                    NativeWorkspaceWorkerImplementation.PersistentWorkspace,
                    static sample => sample.ManagedAllocatedBytes),
                Mean(
                    samples,
                    NativeWorkspaceWorkerImplementation.TransientWorkspace,
                    static sample => sample.ManagedAllocatedBytes),
                samples.Max(static sample => sample.PeakWorkingSetBytes),
                managedHash,
                persistentHash,
                transientHash,
                balancedOrder,
                exactParity,
                zeroTimedPersistentGrowth,
                transientOwnership,
                exactlyOnceCleanup,
                balancedOrder
                    && exactParity
                    && zeroTimedPersistentGrowth
                    && transientOwnership
                    && exactlyOnceCleanup,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            persistent.Dispose();
            NativeMemoryTestHooks.Reset();
        }
    }

    internal static NativeWorkspaceWorkerImplementation[] GetOrder(
        int sampleIndex) => sampleIndex switch
        {
            0 =>
            [
                NativeWorkspaceWorkerImplementation.PersistentWorkspace,
                NativeWorkspaceWorkerImplementation.ManagedScratch,
                NativeWorkspaceWorkerImplementation.TransientWorkspace
            ],
            1 =>
            [
                NativeWorkspaceWorkerImplementation.ManagedScratch,
                NativeWorkspaceWorkerImplementation.TransientWorkspace,
                NativeWorkspaceWorkerImplementation.PersistentWorkspace
            ],
            2 =>
            [
                NativeWorkspaceWorkerImplementation.TransientWorkspace,
                NativeWorkspaceWorkerImplementation.PersistentWorkspace,
                NativeWorkspaceWorkerImplementation.ManagedScratch
            ],
            3 =>
            [
                NativeWorkspaceWorkerImplementation.PersistentWorkspace,
                NativeWorkspaceWorkerImplementation.TransientWorkspace,
                NativeWorkspaceWorkerImplementation.ManagedScratch
            ],
            4 =>
            [
                NativeWorkspaceWorkerImplementation.ManagedScratch,
                NativeWorkspaceWorkerImplementation.PersistentWorkspace,
                NativeWorkspaceWorkerImplementation.TransientWorkspace
            ],
            5 =>
            [
                NativeWorkspaceWorkerImplementation.TransientWorkspace,
                NativeWorkspaceWorkerImplementation.ManagedScratch,
                NativeWorkspaceWorkerImplementation.PersistentWorkspace
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(sampleIndex))
        };

    private static NativeWorkspaceWorkerSample Measure(
        int sampleIndex,
        NativeWorkspaceWorkerImplementation implementation,
        PersistentWorkspaceWorker persistent)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        NativeMemoryTestMetrics nativeBefore =
            NativeMemoryTestHooks.Snapshot();
        Stopwatch clock = Stopwatch.StartNew();
        ulong checksum = implementation switch
        {
            NativeWorkspaceWorkerImplementation.ManagedScratch =>
                RunManagedScratch(),
            NativeWorkspaceWorkerImplementation.PersistentWorkspace =>
                persistent.RunBuilds(),
            NativeWorkspaceWorkerImplementation.TransientWorkspace =>
                RunTransientWorkspace(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(implementation))
        };
        clock.Stop();
        NativeMemoryTestMetrics nativeAfter =
            NativeMemoryTestHooks.Snapshot();
        Process process = Process.GetCurrentProcess();
        return new NativeWorkspaceWorkerSample(
            sampleIndex,
            implementation,
            clock.Elapsed.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(true) - allocatedBefore,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            nativeAfter.AllocationCount - nativeBefore.AllocationCount,
            nativeAfter.FreeCount - nativeBefore.FreeCount,
            nativeAfter.OutstandingNativeBytes,
            process.PeakWorkingSet64,
            checksum);
    }

    private static ulong RunManagedScratch()
    {
        ulong checksum = 0;
        for (int build = 0; build < BuildCount; build++)
        {
            var values = new int[WorkspaceLength];
            values.AsSpan().Fill(build);
            checksum = Consume(checksum, values, build);
        }

        return checksum;
    }

    private static ulong RunTransientWorkspace()
    {
        ulong checksum = 0;
        for (int build = 0; build < BuildCount; build++)
        {
            using NativeWorkspace<int> workspace = new(
                preLease: WorkspaceLength);
            WorkspaceBuildState state = new(build, checksum);
            checksum = workspace.Process(
                WorkspaceLength,
                state,
                static (values, current) =>
                {
                    values.Fill(current.Build);
                    return Consume(
                        current.Checksum,
                        values,
                        current.Build);
                });
        }

        return checksum;
    }

    private static string VerifyExact(
        NativeWorkspaceWorkerImplementation implementation,
        PersistentWorkspaceWorker persistent)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        for (int build = 0; build < BuildCount; build++)
        {
            if (implementation
                == NativeWorkspaceWorkerImplementation.ManagedScratch)
            {
                var values = new int[WorkspaceLength];
                values.AsSpan().Fill(build);
                hash.AppendData(System.Runtime.InteropServices
                    .MemoryMarshal.AsBytes(values.AsSpan()));
            }
            else if (implementation
                == NativeWorkspaceWorkerImplementation.PersistentWorkspace)
            {
                persistent.AppendBuildToHash(build, hash);
            }
            else
            {
                using NativeWorkspace<int> workspace = new(
                    preLease: WorkspaceLength);
                workspace.Process(
                    WorkspaceLength,
                    (Build: build, Hash: hash),
                    static (values, state) =>
                    {
                        values.Fill(state.Build);
                        state.Hash.AppendData(
                            System.Runtime.InteropServices.MemoryMarshal
                                .AsBytes(values));
                        return 0;
                    });
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static ulong Consume(
        ulong checksum,
        ReadOnlySpan<int> values,
        int build)
    {
        const ulong prime = 1_099_511_628_211UL;
        checksum ^= (uint)values[0];
        checksum *= prime;
        checksum ^= (uint)values[values.Length / 2];
        checksum *= prime;
        checksum ^= (uint)values[^1];
        checksum *= prime;
        checksum ^= (uint)build;
        return checksum * prime;
    }

    private static NativeWorkspaceWorkerSample Find(
        IEnumerable<NativeWorkspaceWorkerSample> samples,
        int sampleIndex,
        NativeWorkspaceWorkerImplementation implementation) =>
        samples.Single(sample =>
            sample.SampleIndex == sampleIndex
            && sample.Implementation == implementation);

    private static double Mean(
        IEnumerable<NativeWorkspaceWorkerSample> samples,
        NativeWorkspaceWorkerImplementation implementation,
        Func<NativeWorkspaceWorkerSample, double> selector) =>
        samples.Where(sample => sample.Implementation == implementation)
            .Average(selector);

    private static bool IsBalancedOrder(
        IReadOnlyList<NativeWorkspaceWorkerSample> samples)
    {
        foreach (NativeWorkspaceWorkerImplementation implementation
            in Enum.GetValues<NativeWorkspaceWorkerImplementation>())
        {
            int[] positions = Enumerable.Range(0, 3)
                .Select(position => samples.Count(sample =>
                    sample.Implementation == implementation
                    && Array.IndexOf(
                        GetOrder(sample.SampleIndex),
                        implementation) == position))
                .ToArray();
            if (positions.Any(static count => count != 2))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class PersistentWorkspaceWorker : IDisposable
    {
        private readonly NativeWorkspace<int> _workspace = new(
            preLease: WorkspaceLength);

        internal ulong RunBuilds()
        {
            ulong checksum = 0;
            for (int build = 0; build < BuildCount; build++)
            {
                WorkspaceBuildState state = new(build, checksum);
                checksum = _workspace.Process(
                    WorkspaceLength,
                    state,
                    static (values, current) =>
                    {
                        values.Fill(current.Build);
                        return Consume(
                            current.Checksum,
                            values,
                            current.Build);
                    });
            }

            return checksum;
        }

        internal void AppendBuildToHash(
            int build,
            IncrementalHash hash) => _workspace.Process(
                WorkspaceLength,
                (Build: build, Hash: hash),
                static (values, state) =>
                {
                    values.Fill(state.Build);
                    state.Hash.AppendData(
                        System.Runtime.InteropServices.MemoryMarshal
                            .AsBytes(values));
                    return 0;
                });

        public void Dispose() => _workspace.Dispose();
    }

    private readonly record struct WorkspaceBuildState(
        int Build,
        ulong Checksum);
}

internal enum NativeWorkspaceWorkerImplementation
{
    ManagedScratch,
    PersistentWorkspace,
    TransientWorkspace
}

internal sealed record NativeWorkspaceWorkerSample(
    int SampleIndex,
    NativeWorkspaceWorkerImplementation Implementation,
    double ElapsedMilliseconds,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long NativeAllocationCount,
    long NativeFreeCount,
    long OutstandingNativeBytes,
    long PeakWorkingSetBytes,
    ulong Checksum);

internal sealed record NativeWorkspaceWorkerReport(
    int BuildCount,
    int WorkspaceLength,
    int SampleCount,
    double SetupMilliseconds,
    long SetupManagedAllocatedBytes,
    IReadOnlyList<NativeWorkspaceWorkerSample> Samples,
    double ManagedMeanMilliseconds,
    double PersistentMeanMilliseconds,
    double TransientMeanMilliseconds,
    double ManagedToPersistentMeanSpeedup,
    double ManagedToPersistentAggregateSpeedup,
    double ManagedToPersistentConfidenceLower95,
    double ManagedMeanAllocatedBytes,
    double PersistentMeanAllocatedBytes,
    double TransientMeanAllocatedBytes,
    long PeakWorkingSetBytes,
    string ManagedOutputSha256,
    string PersistentOutputSha256,
    string TransientOutputSha256,
    bool BalancedOrder,
    bool ExactParity,
    bool ZeroTimedPersistentGrowth,
    bool TransientOwnership,
    bool ExactlyOnceCleanup,
    bool Passed,
    DateTimeOffset CapturedAtUtc);
