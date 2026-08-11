using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Performance;

internal static class NativePoolRetirementRegression
{
    internal const int WorkerCount = 24;
    internal const int BuildCount = 1_179;
    internal const int Capacity = 153_600;
    internal const int SampleCount = 6;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    internal static async Task<int> RunCommandAsync(
        string outputPath)
    {
        NativePoolRetirementReport report = Run();
        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(fullPath, json);
        Console.WriteLine(json);
        return report.Passed ? 0 : 3;
    }

    internal static NativePoolRetirementReport Run()
    {
        NativeMemoryTestHooks.Reset();
        Stopwatch setupClock = Stopwatch.StartNew();
        long setupAllocationBefore = GC.GetTotalAllocatedBytes(true);
        PoolRetirementWorkload workload = new();
        long setupManagedBytes = GC.GetTotalAllocatedBytes(true)
            - setupAllocationBefore;
        setupClock.Stop();
        try
        {
            workload.Warm();
            var samples = new List<NativePoolRetirementSample>(
                SampleCount * 3);
            for (int sampleIndex = 0;
                sampleIndex < SampleCount;
                sampleIndex++)
            {
                foreach (NativePoolRetirementImplementation
                    implementation in GetOrder(sampleIndex))
                {
                    samples.Add(Measure(
                        sampleIndex,
                        implementation,
                        workload));
                }
            }

            string managedHash = workload.Verify(
                NativePoolRetirementImplementation.ManagedScratch);
            string persistentHash = workload.Verify(
                NativePoolRetirementImplementation.PersistentPool);
            string transientHash = workload.Verify(
                NativePoolRetirementImplementation.TransientPool);
            NativeMemoryTestMetrics beforeCleanup =
                NativeMemoryTestHooks.Snapshot();
            Stopwatch cleanupClock = Stopwatch.StartNew();
            workload.Dispose();
            cleanupClock.Stop();
            NativeMemoryTestMetrics afterCleanup =
                NativeMemoryTestHooks.Snapshot();

            bool balancedOrder = IsBalancedOrder(samples);
            bool exactParity = managedHash == persistentHash
                && managedHash == transientHash
                && samples.Select(static sample => sample.Checksum)
                    .Distinct()
                    .Count() == 1;
            bool zeroPersistentGrowth = samples
                .Where(static sample => sample.Implementation
                    == NativePoolRetirementImplementation
                        .PersistentPool)
                .All(static sample =>
                    sample.NativeAllocationCount == 0
                    && sample.NativeFreeCount == 0);
            bool transientOwnership = samples
                .Where(static sample => sample.Implementation
                    == NativePoolRetirementImplementation
                        .TransientPool)
                .All(static sample =>
                    sample.NativeAllocationCount == BuildCount
                    && sample.NativeFreeCount == BuildCount);
            bool exactlyOnceCleanup =
                afterCleanup.FreeCount - beforeCleanup.FreeCount
                    == WorkerCount
                && afterCleanup.OutstandingNativeBytes == 0;

            return new NativePoolRetirementReport(
                WorkerCount,
                BuildCount,
                Capacity,
                SampleCount,
                setupClock.Elapsed.TotalMilliseconds,
                setupManagedBytes,
                cleanupClock.Elapsed.TotalMilliseconds,
                samples,
                Mean(
                    samples,
                    NativePoolRetirementImplementation.ManagedScratch),
                Mean(
                    samples,
                    NativePoolRetirementImplementation.PersistentPool),
                Mean(
                    samples,
                    NativePoolRetirementImplementation.TransientPool),
                managedHash,
                persistentHash,
                transientHash,
                balancedOrder,
                exactParity,
                zeroPersistentGrowth,
                transientOwnership,
                exactlyOnceCleanup,
                balancedOrder
                    && exactParity
                    && zeroPersistentGrowth
                    && transientOwnership
                    && exactlyOnceCleanup,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            workload.Dispose();
            NativeMemoryTestHooks.Reset();
        }
    }

    internal static NativePoolRetirementImplementation[] GetOrder(
        int sampleIndex) => sampleIndex switch
        {
            0 =>
            [
                NativePoolRetirementImplementation.PersistentPool,
                NativePoolRetirementImplementation.ManagedScratch,
                NativePoolRetirementImplementation.TransientPool
            ],
            1 =>
            [
                NativePoolRetirementImplementation.ManagedScratch,
                NativePoolRetirementImplementation.TransientPool,
                NativePoolRetirementImplementation.PersistentPool
            ],
            2 =>
            [
                NativePoolRetirementImplementation.TransientPool,
                NativePoolRetirementImplementation.PersistentPool,
                NativePoolRetirementImplementation.ManagedScratch
            ],
            3 =>
            [
                NativePoolRetirementImplementation.PersistentPool,
                NativePoolRetirementImplementation.TransientPool,
                NativePoolRetirementImplementation.ManagedScratch
            ],
            4 =>
            [
                NativePoolRetirementImplementation.ManagedScratch,
                NativePoolRetirementImplementation.PersistentPool,
                NativePoolRetirementImplementation.TransientPool
            ],
            5 =>
            [
                NativePoolRetirementImplementation.TransientPool,
                NativePoolRetirementImplementation.ManagedScratch,
                NativePoolRetirementImplementation.PersistentPool
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(sampleIndex))
        };

    private static NativePoolRetirementSample Measure(
        int sampleIndex,
        NativePoolRetirementImplementation implementation,
        PoolRetirementWorkload workload)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        NativeMemoryTestMetrics nativeBefore =
            NativeMemoryTestHooks.Snapshot();
        Stopwatch clock = Stopwatch.StartNew();
        ulong checksum = workload.Run(implementation);
        clock.Stop();
        NativeMemoryTestMetrics nativeAfter =
            NativeMemoryTestHooks.Snapshot();
        return new NativePoolRetirementSample(
            sampleIndex,
            implementation,
            clock.Elapsed.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(true) - allocatedBefore,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            nativeAfter.AllocationCount
                - nativeBefore.AllocationCount,
            nativeAfter.FreeCount - nativeBefore.FreeCount,
            nativeAfter.OutstandingNativeBytes,
            Process.GetCurrentProcess().PeakWorkingSet64,
            checksum);
    }

    private static double Mean(
        IEnumerable<NativePoolRetirementSample> samples,
        NativePoolRetirementImplementation implementation) =>
        samples.Where(sample => sample.Implementation
                == implementation)
            .Average(static sample => sample.ElapsedMilliseconds);

    private static bool IsBalancedOrder(
        IReadOnlyList<NativePoolRetirementSample> samples)
    {
        foreach (NativePoolRetirementImplementation implementation
            in Enum.GetValues<NativePoolRetirementImplementation>())
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

    private sealed class PoolRetirementWorkload : IDisposable
    {
        private readonly PersistentMapWorkers _workers = new(
            WorkerCount,
            BuildCount);
        private readonly ThreadLocal<PersistentPoolWorker> _persistent =
            new(
                static () => new PersistentPoolWorker(),
                trackAllValues: true);
        private readonly ulong[] _checksums = new ulong[BuildCount];
        private readonly byte[] _digests =
            new byte[BuildCount * SHA256.HashSizeInBytes];
        private readonly Action<int, int> _managedAction;
        private readonly Action<int, int> _persistentAction;
        private readonly Action<int, int> _transientAction;
        private readonly Action<int, int> _managedHashAction;
        private readonly Action<int, int> _persistentHashAction;
        private readonly Action<int, int> _transientHashAction;
        private readonly Action<int, int> _initializeAction;
        private readonly Action<int, int> _retireAction;
        private int _disposed;

        internal PoolRetirementWorkload()
        {
            _managedAction = RunManaged;
            _persistentAction = RunPersistent;
            _transientAction = RunTransient;
            _managedHashAction = HashManaged;
            _persistentHashAction = HashPersistent;
            _transientHashAction = HashTransient;
            _initializeAction = InitializePersistent;
            _retireAction = RetirePersistent;
            _workers.Run(_initializeAction);
        }

        internal void Warm()
        {
            _ = Run(NativePoolRetirementImplementation.ManagedScratch);
            _ = Run(NativePoolRetirementImplementation.PersistentPool);
            _ = Run(NativePoolRetirementImplementation.TransientPool);
        }

        internal ulong Run(
            NativePoolRetirementImplementation implementation)
        {
            _workers.Run(implementation switch
            {
                NativePoolRetirementImplementation.ManagedScratch =>
                    _managedAction,
                NativePoolRetirementImplementation.PersistentPool =>
                    _persistentAction,
                NativePoolRetirementImplementation.TransientPool =>
                    _transientAction,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(implementation))
            });
            ulong checksum = 0;
            foreach (ulong value in _checksums)
            {
                checksum = unchecked(checksum * 1_099_511_628_211UL)
                    ^ value;
            }

            return checksum;
        }

        internal string Verify(
            NativePoolRetirementImplementation implementation)
        {
            _workers.Run(implementation switch
            {
                NativePoolRetirementImplementation.ManagedScratch =>
                    _managedHashAction,
                NativePoolRetirementImplementation.PersistentPool =>
                    _persistentHashAction,
                NativePoolRetirementImplementation.TransientPool =>
                    _transientHashAction,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(implementation))
            });
            return Convert.ToHexString(SHA256.HashData(_digests));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _workers.Run(_retireAction);
            foreach (PersistentPoolWorker worker in _persistent.Values)
            {
                worker.Dispose();
            }

            _persistent.Dispose();
            _workers.Dispose();
        }

        private void InitializePersistent(int workerIndex, int mapIndex)
        {
            if (workerIndex == mapIndex)
            {
                _ = _persistent.Value;
            }
        }

        private void RetirePersistent(int workerIndex, int mapIndex)
        {
            if (workerIndex == mapIndex)
            {
                _persistent.Value!.Retire();
            }
        }

        private void RunManaged(int workerIndex, int mapIndex)
        {
            var values = new short[Capacity];
            values.AsSpan().Fill(1);
            _checksums[mapIndex] = Consume(values);
        }

        private void RunPersistent(int workerIndex, int mapIndex) =>
            _checksums[mapIndex] = _persistent.Value!.Build();

        private void RunTransient(int workerIndex, int mapIndex)
        {
            using NativePool<short> pool = new(
                preLease: Capacity,
                returnMemoryOnDispose:
                    NativeMemoryReturn.ToNativeMemory);
            _checksums[mapIndex] = RunOne(pool);
        }

        private void HashManaged(int workerIndex, int mapIndex)
        {
            var values = new short[Capacity];
            values.AsSpan().Fill(1);
            Hash(values, mapIndex);
        }

        private void HashPersistent(int workerIndex, int mapIndex) =>
            _persistent.Value!.Hash(_digests, mapIndex);

        private void HashTransient(int workerIndex, int mapIndex)
        {
            using NativePool<short> pool = new(
                preLease: Capacity,
                returnMemoryOnDispose:
                    NativeMemoryReturn.ToNativeMemory);
            Pooled<short> lease = pool.Rent(
                Capacity,
                static writer => writer.Fill(1));
            try
            {
                lease.Process(
                    Capacity,
                    (_digests, mapIndex),
                    static (values, state) =>
                    {
                        Hash(values, state.mapIndex, state._digests);
                        return 0;
                    });
            }
            finally
            {
                lease.Dispose();
            }
        }

        private void Hash(ReadOnlySpan<short> values, int mapIndex) =>
            Hash(values, mapIndex, _digests);

        private static void Hash(
            ReadOnlySpan<short> values,
            int mapIndex,
            byte[] digests)
        {
            Span<byte> destination = digests.AsSpan(
                mapIndex * SHA256.HashSizeInBytes,
                SHA256.HashSizeInBytes);
            SHA256.HashData(
                MemoryMarshal.AsBytes(values),
                destination);
        }

        private static ulong RunOne(NativePool<short> pool)
        {
            Pooled<short> lease = pool.Rent(
                Capacity,
                static writer => writer.Fill(1));
            try
            {
                return lease.Read(static values =>
                    Consume(values.AsSpan()));
            }
            finally
            {
                lease.Dispose();
            }
        }

        private static ulong Consume(ReadOnlySpan<short> values)
        {
            const ulong prime = 1_099_511_628_211UL;
            ulong checksum = (uint)values[0];
            checksum = unchecked(checksum * prime)
                ^ (uint)values[values.Length / 2];
            return unchecked(checksum * prime)
                ^ (uint)values[^1];
        }

        private sealed class PersistentPoolWorker : IDisposable
        {
            private readonly NativePool<short> _pool = new(
                preLease: Capacity,
                returnMemoryOnDispose:
                    NativeMemoryReturn.ToNativeMemory);

            internal ulong Build() => RunOne(_pool);

            internal void Hash(byte[] digests, int mapIndex)
            {
                Pooled<short> lease = _pool.Rent(
                    Capacity,
                    static writer => writer.Fill(1));
                try
                {
                    lease.Process(
                        Capacity,
                        (digests, mapIndex),
                        static (values, state) =>
                        {
                            PoolRetirementWorkload.Hash(
                                values,
                                state.mapIndex,
                                state.digests);
                            return 0;
                        });
                }
                finally
                {
                    lease.Dispose();
                }
            }

            internal void Retire() => _pool.Retire();

            public void Dispose() =>
                _pool.ReleaseRetiredStorage();
        }
    }
}

internal enum NativePoolRetirementImplementation
{
    ManagedScratch,
    PersistentPool,
    TransientPool
}

internal sealed record NativePoolRetirementSample(
    int SampleIndex,
    NativePoolRetirementImplementation Implementation,
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

internal sealed record NativePoolRetirementReport(
    int WorkerCount,
    int BuildCount,
    int Capacity,
    int SampleCount,
    double SetupMilliseconds,
    long SetupManagedAllocatedBytes,
    double CleanupMilliseconds,
    IReadOnlyList<NativePoolRetirementSample> Samples,
    double ManagedMeanMilliseconds,
    double PersistentMeanMilliseconds,
    double TransientMeanMilliseconds,
    string ManagedOutputSha256,
    string PersistentOutputSha256,
    string TransientOutputSha256,
    bool BalancedOrder,
    bool ExactParity,
    bool ZeroPersistentGrowth,
    bool TransientOwnership,
    bool ExactlyOnceCleanup,
    bool Passed,
    DateTimeOffset CapturedAtUtc);
