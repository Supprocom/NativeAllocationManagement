using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Performance;

// This is a measurement contract, not a performance acceptance switch.
internal static class NativePreparedPageMeasurement
{
    internal const int SlotCount = 64;
    internal const int SlotBytes = 4_096;
    internal const int SlotsPerPage = 16;
    internal const int DefaultRounds = 1_024;
    private const int WarmupRounds = 64;
    private const byte Payload = 7;
    private static readonly NativeLeaseInitializer<byte> Fill = static writer => writer.Fill(Payload);
    private static readonly NativeLeaseFunc<byte, long> Check = static view => Verify(view.AsSpan());

    internal static int RunCommand(string[] arguments)
    {
        if (arguments is not ["--prepared-page-worker", "--implementation", string name, "--rounds", string roundsText]
            || !Enum.TryParse(name, ignoreCase: false, out PreparedPageImplementation implementation)
            || !Enum.IsDefined(implementation)
            || !int.TryParse(roundsText, NumberStyles.None, CultureInfo.InvariantCulture, out int rounds))
        {
            throw new ArgumentException("Expected --prepared-page-worker --implementation <exact name> --rounds <positive integer>.", nameof(arguments));
        }

        PreparedPageReport report = Run(implementation, rounds);
        Console.WriteLine(JsonSerializer.Serialize(report));
        return report.ExactOutput && report.ExactCleanup ? 0 : 3;
    }

    internal static PreparedPageReport Run(PreparedPageImplementation implementation, int rounds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rounds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rounds, 1_000_000);
        if (!Enum.IsDefined(implementation)) throw new ArgumentOutOfRangeException(nameof(implementation));
        _ = Fill;
        _ = Check;
        long[] roundTicks = new long[rounds];
        PreparedPagePhase[] phases = new PreparedPagePhase[5];
        using Process process = Process.GetCurrentProcess();
        bool managed = implementation == PreparedPageImplementation.ManagedExactPages;
        NativeMemoryBudget? budget = null;
        NativePool<byte>? pool = null;
        NativePreparedPool<byte>? preparedPool = null;
        byte[][]? pages = null;
        int pageCount = SlotCount / SlotsPerPage;
        NativeMemoryStatistics original = NativeMemoryDiagnostics.Snapshot();
        long checksum = 0;
        long sparseChecksum;
        long retainedBeforeTrim;
        long retainedAfterTrim;
        long retainedAfterReturn;
        long? metadataBytes;
        long ownedAcquisitions;
        long expectedSparseBytes;
        NativeMemoryBudgetStatistics? terminalBudget = null;
        NativePreparedPoolStatistics? terminalPrepared = null;
        try
        {
            PageClock clock = PageClock.Start(process);
            if (managed)
            {
                pages = new byte[pageCount][];
                foreach (ref byte[] page in pages.AsSpan())
                {
                    page = GC.AllocateUninitializedArray<byte>(SlotBytes * SlotsPerPage);
                }
            }
            else
            {
                bool prepared = implementation != PreparedPageImplementation.OrdinaryNativePool;
                if (implementation is PreparedPageImplementation.PreparedBudget or PreparedPageImplementation.PreparedTrace)
                {
                    budget = new NativeMemoryBudget(SlotCount * SlotBytes,
                        implementation == PreparedPageImplementation.PreparedTrace ? 64 : 0);
                }
                if (prepared)
                    preparedPool = new(new NativePoolPreparation(SlotCount, SlotBytes, SlotsPerPage), budget);
                else
                    pool = new(0, NativeMemoryReturn.ToNativeMemory);
            }
            phases[0] = clock.End("preparation", 1, 0, process);

            clock = PageClock.Start(process);
            for (int round = 0; round < WarmupRounds; round++)
            {
                long warm = managed ? VisitManaged(pages!, 0)
                    : preparedPool is not null ? VisitPrepared(preparedPool, 0) : VisitNative(pool!, 0);
                if (warm != ExpectedChecksum(1)) throw new InvalidDataException("Warm-up output differs.");
            }
            phases[1] = clock.End("warm-up", WarmupRounds, ExpectedChecksum(WarmupRounds), process);

            NativeMemoryStatistics beforeReuse = NativeMemoryDiagnostics.Snapshot();
            clock = PageClock.Start(process);
            foreach (ref long ticks in roundTicks.AsSpan())
            {
                long start = Stopwatch.GetTimestamp();
                checksum += managed ? VisitManaged(pages!, 0)
                    : preparedPool is not null ? VisitPrepared(preparedPool, 0) : VisitNative(pool!, 0);
                ticks = Stopwatch.GetTimestamp() - start;
            }
            phases[2] = clock.End("prepared reuse", rounds, checksum, process);
            NativeMemoryStatistics afterReuse = NativeMemoryDiagnostics.Snapshot();
            if (beforeReuse.MetricsEpoch != afterReuse.MetricsEpoch) throw new InvalidOperationException("The process accounting epoch changed during measurement.");

            if (managed)
            {
                ownedAcquisitions = 0;
                metadataBytes = checked((long)pageCount * IntPtr.Size);
                retainedBeforeTrim = SlotCount * SlotBytes;
                expectedSparseBytes = SlotsPerPage * SlotBytes;
                clock = PageClock.Start(process);
                Span<byte> survivor = pages![0].AsSpan(0, SlotBytes);
                survivor.Fill(Payload);
                foreach (ref byte[] page in pages.AsSpan(1)) page = [];
                retainedAfterTrim = SlotBytes * SlotsPerPage;
                sparseChecksum = Verify(survivor);
                // The managed heap may still retain these unreachable arrays.
                // Only live application backing authority is reported here.
                pages[0] = [];
                retainedAfterReturn = 0;
                phases[3] = clock.End("sparse maintenance", 1, sparseChecksum, process);
            }
            else if (preparedPool is not null)
            {
                ownedAcquisitions = preparedPool.GetStatistics().FreshSegmentAllocationCount;
                metadataBytes = preparedPool.CapturePreparedSnapshot().ManagedBankBytes;
                retainedBeforeTrim = preparedPool.GetStatistics().RetainedBytes;
                expectedSparseBytes = SlotBytes * SlotsPerPage;
                clock = PageClock.Start(process);
                using (PreparedPooled<byte> survivor = preparedPool.Rent(SlotBytes, Fill))
                {
                    _ = preparedPool.TrimRetainedMemory();
                    retainedAfterTrim = preparedPool.GetStatistics().RetainedBytes;
                    sparseChecksum = survivor.Read(Check);
                }
                _ = preparedPool.TrimRetainedMemory();
                retainedAfterReturn = preparedPool.GetStatistics().RetainedBytes;
                phases[3] = clock.End("sparse maintenance", 1, sparseChecksum, process);
            }
            else
            {
                ownedAcquisitions = pool!.GetStatistics().FreshSegmentAllocationCount;
                metadataBytes = null;
                retainedBeforeTrim = pool.GetStatistics().RetainedBytes;
                expectedSparseBytes = SlotBytes;
                clock = PageClock.Start(process);
                using (Pooled<byte> survivor = pool.Rent(SlotBytes, Fill))
                {
                    _ = pool.TrimRetainedMemory();
                    retainedAfterTrim = pool.GetStatistics().RetainedBytes;
                    sparseChecksum = survivor.Read(Check);
                }
                _ = pool.TrimRetainedMemory();
                retainedAfterReturn = pool.GetStatistics().RetainedBytes;
                phases[3] = clock.End("sparse maintenance", 1, sparseChecksum, process);
            }

            clock = PageClock.Start(process);
            if (managed) Array.Clear(pages!);
            else if (preparedPool is not null)
            {
                NativePreparedPool<byte> terminalPool = preparedPool;
                terminalPool.Dispose();
                preparedPool = null;
                terminalPrepared = terminalPool.CapturePreparedSnapshot();
            }
            else
            {
                pool!.Dispose();
                pool = null;
            }
            terminalBudget = budget?.CaptureStatistics();
            phases[4] = clock.End("cleanup", 1, 0, process);
            NativeMemoryStatistics terminal = NativeMemoryDiagnostics.Snapshot();
            if (original.MetricsEpoch != terminal.MetricsEpoch) throw new InvalidOperationException("The process accounting epoch changed during the workload.");
            bool cleanup = retainedAfterReturn == 0 && (terminalBudget is null
                || terminalBudget.Value is { CommittedBytes: 0, ReservedBytes: 0 });
            bool exact = checksum == ExpectedChecksum(rounds) && sparseChecksum == Payload * SlotBytes
                && retainedBeforeTrim == SlotCount * SlotBytes && retainedAfterTrim == expectedSparseBytes;
            return new PreparedPageReport(implementation, rounds, SlotCount, SlotBytes, SlotsPerPage,
                RuntimeInformation.RuntimeIdentifier, RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.Version.ToString(), Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                Environment.GetEnvironmentVariable("DOTNET_TieredPGO"), phases,
                roundTicks, Stopwatch.Frequency, checksum, ExpectedChecksum(rounds),
                managed ? null : ownedAcquisitions, metadataBytes, retainedBeforeTrim, retainedAfterTrim, retainedAfterReturn,
                afterReuse.AllocationCount - beforeReuse.AllocationCount,
                afterReuse.FreeCount - beforeReuse.FreeCount,
                terminal.AllocationCount - original.AllocationCount,
                terminal.FreeCount - original.FreeCount, original, beforeReuse, afterReuse, terminal,
                terminalBudget, terminalPrepared, exact, cleanup,
                managed ? "Live managed array-element backing; dropping authority is NOT an observed physical free or RSS relief. Native events are not applicable. Metadata is page-reference elements, excluding CLR headers."
                    : "NAM-requested complete native extents. Prepared bank bytes exclude CLR headers; ordinary bank bytes are unavailable. Process event deltas include unrelated finalization in the observation interval.",
                "Worker phases exclude report/serialization, process-CPU-query allocation and instrumentation arrays allocated before preparation. CPU clock resolution may produce a real zero for short phases. No benefit verdict, RSS bound, page-fault or real-time claim.");
        }
        finally
        {
            pool?.Dispose();
            preparedPool?.Dispose();
            if (pages is not null) Array.Clear(pages);
        }
    }

    internal static long ExpectedChecksum(int rounds) => checked((long)Payload * SlotCount * SlotBytes * rounds);

    // Identical lexical topology: all sixty-four buffers coexist at the deepest
    // frame; readers run while unwinding and each owner is returned in finally.
    private static long VisitNative(NativePool<byte> pool, int index)
    {
        using Pooled<byte> lease = pool.Rent(SlotBytes, Fill);
        long checksum = index + 1 < SlotCount ? VisitNative(pool, index + 1) : 0;
        return checksum + lease.Read(Check);
    }

    private static long VisitPrepared(NativePreparedPool<byte> pool, int index)
    {
        using PreparedPooled<byte> lease = pool.Rent(SlotBytes, Fill);
        long checksum = index + 1 < SlotCount ? VisitPrepared(pool, index + 1) : 0;
        return checksum + lease.Read(Check);
    }

    private static long VisitManaged(byte[][] pages, int index)
    {
        Span<byte> slot = pages[index / SlotsPerPage].AsSpan(index % SlotsPerPage * SlotBytes, SlotBytes);
        slot.Fill(Payload);
        long checksum = index + 1 < SlotCount ? VisitManaged(pages, index + 1) : 0;
        return checksum + Verify(slot);
    }

    internal static long Verify(scoped ReadOnlySpan<byte> bytes)
    {
        long checksum = 0;
        foreach (ref readonly byte value in bytes)
        {
            if (value != Payload) throw new InvalidDataException("An initialized payload byte differs.");
            checksum += value;
        }
        return checksum;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PageClock(long Timestamp, long Allocated, TimeSpan Cpu,
        int Gen0, int Gen1, int Gen2)
    {
        internal static PageClock Start(Process process)
        {
            TimeSpan cpu = process.TotalProcessorTime;
            int gen0 = GC.CollectionCount(0);
            int gen1 = GC.CollectionCount(1);
            int gen2 = GC.CollectionCount(2);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            return new(Stopwatch.GetTimestamp(), allocated, cpu, gen0, gen1, gen2);
        }

        internal PreparedPagePhase End(string name, int operations, long checksum, Process process)
        {
            long end = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - Allocated;
            int gen0 = GC.CollectionCount(0) - Gen0;
            int gen1 = GC.CollectionCount(1) - Gen1;
            int gen2 = GC.CollectionCount(2) - Gen2;
            double cpu = (process.TotalProcessorTime - Cpu).TotalMilliseconds;
            return new(name, operations, Stopwatch.GetElapsedTime(Timestamp, end).TotalMilliseconds,
                cpu, allocated, gen0, gen1, gen2, checksum);
        }
    }
}

internal enum PreparedPageImplementation
{
    ManagedExactPages,
    OrdinaryNativePool,
    PreparedNoBudget,
    PreparedBudget,
    PreparedTrace
}

internal sealed record PreparedPagePhase(string Name, int Operations, double WallMilliseconds,
    double CpuMilliseconds, long ManagedAllocatedBytes, int Gen0Collections, int Gen1Collections,
    int Gen2Collections, long Checksum);

internal sealed record PreparedPageReport(PreparedPageImplementation Implementation, int Rounds, int SlotCount,
    int SlotBytes, int SlotsPerPage, string Rid, string Architecture, string Runtime,
    string? TieredCompilation, string? TieredPgo, PreparedPagePhase[] Phases, long[] RoundTicks,
    long TimestampFrequency, long Checksum, long ExpectedChecksum, long? OwnedBackingAcquisitions,
    long? MetadataElementBytes, long RetainedBeforeTrim, long RetainedAfterTrim, long RetainedAfterReturn,
    long ReuseProcessAllocations, long ReuseProcessFrees, long TotalProcessAllocations, long TotalProcessFrees,
    NativeMemoryStatistics ProcessBefore, NativeMemoryStatistics ReuseProcessBefore,
    NativeMemoryStatistics ReuseProcessAfter, NativeMemoryStatistics ProcessAfter,
    NativeMemoryBudgetStatistics? Budget, NativePreparedPoolStatistics? Prepared, bool ExactOutput,
    bool ExactCleanup, string MemoryDomain, string MeasurementScope);
