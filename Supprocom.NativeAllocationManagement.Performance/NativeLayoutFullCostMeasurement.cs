using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Performance;

// Measurement only: a correctness pass is deliberately not a benefit verdict.
internal static class NativeLayoutFullCostMeasurement
{
    internal const int ByteCount = 1_024;
    internal const int IntegerCount = 64;
    internal const int LongCount = 128;
    internal const int DetachedIntegerCount = 64;
    internal const int MoveCount = 16;
    internal const int ReadCount = 8;
    internal const int WarmupCycles = 32;
    internal const int DefaultCycles = 2_048;
    internal const int PayloadBytes = ByteCount + IntegerCount * sizeof(int) + LongCount * sizeof(long);
    internal const int CopyBytes = DetachedIntegerCount * sizeof(int);
    private const long OneReadChecksum = ByteCount * 3L + IntegerCount * 7L + LongCount * 11L;
    private static readonly NativeLayoutInitializer<Fields> Initialize = static (writer, fields) =>
    {
        writer.Region(fields.Bytes).Fill(3);
        writer.Region(fields.Integers).Fill(7);
        writer.Region(fields.Longs).Fill(11);
    };
    private static readonly NativeLayoutFunc<Fields, long> Read = static (view, fields) =>
        Verify(view.Region(fields.Bytes), view.Region(fields.Integers), view.Region(fields.Longs));
    private static readonly NativeLeaseFunc<int, long> ReadDetached = static view => VerifyDetached(view.AsSpan());

    internal static int RunCommand(string[] arguments)
    {
        if (arguments is not ["--layout-full-cost-worker", "--implementation", string name, "--cycles", string cycleText]
            || !Enum.TryParse(name, ignoreCase: false, out LayoutFullCostImplementation implementation)
            || !Enum.IsDefined(implementation)
            || !string.Equals(name, implementation.ToString(), StringComparison.Ordinal)
            || !int.TryParse(cycleText, NumberStyles.None, CultureInfo.InvariantCulture, out int cycles))
        {
            throw new ArgumentException("Expected --layout-full-cost-worker --implementation <exact name> --cycles <positive integer>.", nameof(arguments));
        }

        LayoutFullCostReport report = Run(implementation, cycles);
        Console.WriteLine(JsonSerializer.Serialize(report));
        return report.ExactOutput && report.ExactCleanup && report.ExactNativeAccounting ? 0 : 3;
    }

    internal static LayoutFullCostReport Run(LayoutFullCostImplementation implementation, int cycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cycles);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cycles, 1_000_000);
        if (!Enum.IsDefined(implementation)) throw new ArgumentOutOfRangeException(nameof(implementation));
        _ = Initialize;
        _ = Read;
        _ = ReadDetached;
        long[] cycleTicks = new long[cycles];
        LayoutFullCostPhase[] phases = new LayoutFullCostPhase[4];
        using Process process = Process.GetCurrentProcess();
        NativeLayout? layout = null;
        NativeMemoryBudget? budget = null;
        Fields fields = default;
        NativeLayoutOwner? lastOwner = null;
        NativeTransfer<int>? lastDetached = null;
        NativeMemoryStatistics original = NativeMemoryDiagnostics.Snapshot();

        CycleClock clock = CycleClock.Start(process);
        if (implementation == LayoutFullCostImplementation.NativeLayout)
        {
            NativeLayoutBuilder builder = new(3);
            fields = new(builder.Add<byte>(ByteCount), builder.Add<int>(IntegerCount), builder.Add<long>(LongCount));
            layout = builder.Build();
            budget = new NativeMemoryBudget(checked(layout.BackingBytes + CopyBytes));
        }
        phases[0] = clock.End("preparation", 1, 0, process);

        clock = CycleClock.Start(process);
        long warmChecksum = 0;
        for (int cycle = 0; cycle < WarmupCycles; cycle++)
        {
            warmChecksum += Visit(implementation, layout, fields, budget, out lastOwner, out lastDetached);
        }
        phases[1] = clock.End("warm-up full lifecycle", WarmupCycles, warmChecksum, process);

        clock = CycleClock.Start(process);
        long checksum = 0;
        foreach (ref long ticks in cycleTicks.AsSpan())
        {
            long start = Stopwatch.GetTimestamp();
            checksum += Visit(implementation, layout, fields, budget, out lastOwner, out lastDetached);
            ticks = Stopwatch.GetTimestamp() - start;
        }
        phases[2] = clock.End("complete ownership lifecycle", cycles, checksum, process);

        clock = CycleClock.Start(process);
        NativeMemoryBudgetStatistics? terminalBudget = budget?.CaptureStatistics();
        NativeLayoutStatistics? terminalLayout = lastOwner?.CaptureSnapshot();
        NativeTransferStatistics? terminalDetached = lastDetached?.CaptureSnapshot();
        NativeMemoryStatistics terminal = NativeMemoryDiagnostics.Snapshot();
        phases[3] = clock.End("terminal diagnostics", 1, 0, process);
        if (original.MetricsEpoch != terminal.MetricsEpoch) throw new InvalidOperationException("Accounting epoch changed during the measured workload.");
        bool native = implementation == LayoutFullCostImplementation.NativeLayout;
        int allCycles = checked(cycles + WarmupCycles);
        long sourceBacking = native ? layout!.BackingBytes : PayloadBytes;
        bool cleanup = !native || terminalBudget is { CommittedBytes: 0, ReservedBytes: 0 }
            && terminalLayout is { LogicalInitializedBytes: 0, InitializedRegionCount: 0, Ownership.OwnedBackingBytes: 0 }
            && terminalDetached is { OwnedBackingBytes: 0 };
        bool accounting = !native || terminalBudget is { } observed
            && observed.AllocationCount == allCycles * 2L
            && observed.FreeCount == allCycles * 2L
            && observed.PeakAdmittedBytes == sourceBacking + CopyBytes
            && observed.RejectedAllocationCount == 0
            && observed.FailedAllocationCount == 0
            && terminalLayout is { } owner
            && owner.Ownership.MoveCount == MoveCount
            && owner.DetachedOwnerCount == 1
            && owner.CopiedBytes == CopyBytes;
        return new LayoutFullCostReport(implementation, cycles, WarmupCycles,
            ByteCount, IntegerCount, LongCount, DetachedIntegerCount, MoveCount, ReadCount,
            RuntimeInformation.RuntimeIdentifier, RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version.ToString(), Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            Environment.GetEnvironmentVariable("DOTNET_TieredPGO"), phases, cycleTicks, Stopwatch.Frequency,
            checksum, ExpectedChecksum(cycles), warmChecksum, ExpectedChecksum(WarmupCycles),
            PayloadBytes, sourceBacking, sourceBacking + CopyBytes, CopyBytes,
            native ? 1 : implementation == LayoutFullCostImplementation.ManagedContiguous ? 1 : 3,
            native ? 2L * allCycles : null,
            native ? layout!.MetadataFieldBytes : null,
            original, terminal, terminalBudget, terminalLayout, terminalDetached,
            checksum == ExpectedChecksum(cycles) && warmChecksum == ExpectedChecksum(WarmupCycles), cleanup, accounting,
            native
                ? "Native requested full backing, including alignment slack and source-plus-copy overlap; budget/control snapshots are real. Metadata field bytes exclude CLR headers, control object, budget and temporary builder. Process deltas can include unrelated finalization."
                : "Exact live managed array elements and source-plus-copy overlap, excluding CLR headers; authority cleanup is not physical freeing, collection or RSS relief. Native/budget/descriptor counters are unavailable (null). Managed handoffs clear source references without extra wrappers or copies.",
            "All preparation, warm-up, acquisition, initialization, handoffs, full reads, detach copy and authority cleanup are timed. Snapshot observation has its own phase. Instrumentation arrays, process-CPU-query allocation and report serialization are excluded on every side. No benefit, managed-heap cap or RSS verdict.");
    }

    internal static long ExpectedChecksum(int cycles) => checked((OneReadChecksum * ReadCount + DetachedIntegerCount * 7L) * cycles);

    private static long Visit(LayoutFullCostImplementation implementation, NativeLayout? layout, Fields fields,
        NativeMemoryBudget? budget, out NativeLayoutOwner? observedOwner, out NativeTransfer<int>? observedDetached)
    {
        observedOwner = null;
        observedDetached = null;
        if (implementation == LayoutFullCostImplementation.NativeLayout)
        {
            return VisitNative(layout!, fields, budget!, out observedOwner, out observedDetached);
        }
        return implementation == LayoutFullCostImplementation.ManagedContiguous ? VisitContiguous() : VisitSeparate();
    }

    private static long VisitNative(NativeLayout layout, Fields fields, NativeMemoryBudget budget,
        out NativeLayoutOwner? observedOwner, out NativeTransfer<int>? observedDetached)
    {
        observedOwner = null;
        observedDetached = null;
        if (!layout.TryReserve(budget, out NativeLayoutReservation? permission, out _)) throw new InvalidOperationException("Declared layout admission was refused.");
        NativeLayoutOwner owner = NativeLayoutReservation.Activate(ref permission, fields, Initialize);
        bool sourceReleased = false;
        try
        {
            NativeLayoutOwner? moving = owner;
            for (int move = 0; move < MoveCount; move++)
            {
                owner = NativeLayoutOwner.Move(ref moving);
                moving = owner;
            }
            long checksum = 0;
            for (int read = 0; read < ReadCount; read++) checksum += owner.Read(fields, Read);
            NativeTransfer<int> detached = owner.DetachField(fields.Integers, budget);
            try
            {
                owner.Dispose();
                sourceReleased = true;
                checksum += detached.Read(ReadDetached);
            }
            finally { detached.Dispose(); observedDetached = detached; }
            return checksum;
        }
        finally
        {
            if (!sourceReleased) owner.Dispose();
            observedOwner = owner;
        }
    }

    private static long VisitContiguous()
    {
        byte[]? source = GC.AllocateUninitializedArray<byte>(PayloadBytes);
        source.AsSpan(0, ByteCount).Fill(3);
        MemoryMarshal.Cast<byte, int>(source.AsSpan(ByteCount, IntegerCount * sizeof(int))).Fill(7);
        MemoryMarshal.Cast<byte, long>(source.AsSpan(ByteCount + IntegerCount * sizeof(int))).Fill(11);
        for (int move = 0; move < MoveCount; move++)
        {
            byte[] moved = source!;
            source = null;
            source = moved;
        }
        long checksum = 0;
        int[] detached;
        {
            Span<byte> bytes = source.AsSpan(0, ByteCount);
            Span<int> integers = MemoryMarshal.Cast<byte, int>(source.AsSpan(ByteCount, IntegerCount * sizeof(int)));
            Span<long> longs = MemoryMarshal.Cast<byte, long>(source.AsSpan(ByteCount + IntegerCount * sizeof(int)));
            for (int read = 0; read < ReadCount; read++) checksum += Verify(bytes, integers, longs);
            detached = integers.ToArray();
        }
        source = null;
        checksum += VerifyDetached(detached);
        GC.KeepAlive(detached);
        return checksum;
    }

    private static long VisitSeparate()
    {
        byte[]? bytes = GC.AllocateUninitializedArray<byte>(ByteCount);
        int[]? integers = GC.AllocateUninitializedArray<int>(IntegerCount);
        long[]? longs = GC.AllocateUninitializedArray<long>(LongCount);
        bytes.AsSpan().Fill(3); integers.AsSpan().Fill(7); longs.AsSpan().Fill(11);
        for (int move = 0; move < MoveCount; move++)
        {
            (byte[]?, int[]?, long[]?) moved = (bytes, integers, longs);
            bytes = null; integers = null; longs = null;
            (bytes, integers, longs) = moved;
        }
        long checksum = 0;
        for (int read = 0; read < ReadCount; read++) checksum += Verify(bytes, integers, longs);
        int[] detached = integers.AsSpan(0, DetachedIntegerCount).ToArray();
        bytes = null; integers = null; longs = null;
        checksum += VerifyDetached(detached);
        GC.KeepAlive(detached);
        return checksum;
    }

    private static long Verify(ReadOnlySpan<byte> bytes, ReadOnlySpan<int> integers, ReadOnlySpan<long> longs)
    {
        if (bytes.Length != ByteCount || integers.Length != IntegerCount || longs.Length != LongCount)
            throw new InvalidDataException("Typed region dimensions differ.");
        long checksum = 0;
        foreach (ref readonly byte value in bytes) { if (value != 3) throw new InvalidDataException("Byte payload differs."); checksum += value; }
        foreach (ref readonly int value in integers) { if (value != 7) throw new InvalidDataException("Integer payload differs."); checksum += value; }
        foreach (ref readonly long value in longs) { if (value != 11) throw new InvalidDataException("Long payload differs."); checksum += value; }
        return checksum;
    }

    private static long VerifyDetached(ReadOnlySpan<int> integers)
    {
        if (integers.Length != DetachedIntegerCount) throw new InvalidDataException("Detached dimensions differ.");
        long checksum = 0;
        foreach (ref readonly int value in integers) { if (value != 7) throw new InvalidDataException("Detached payload differs."); checksum += value; }
        return checksum;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Fields(NativeLayoutField<byte> Bytes, NativeLayoutField<int> Integers, NativeLayoutField<long> Longs);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CycleClock(long Timestamp, TimeSpan Cpu, long ManagedBytes, int Gen0, int Gen1, int Gen2)
    {
        internal static CycleClock Start(Process process)
        {
            TimeSpan cpu = process.TotalProcessorTime;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            return new(Stopwatch.GetTimestamp(), cpu, allocated, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        }

        internal LayoutFullCostPhase End(string name, int operations, long checksum, Process process)
        {
            long elapsed = Stopwatch.GetTimestamp() - Timestamp;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - ManagedBytes;
            int gen0 = GC.CollectionCount(0) - Gen0;
            int gen1 = GC.CollectionCount(1) - Gen1;
            int gen2 = GC.CollectionCount(2) - Gen2;
            double cpu = (process.TotalProcessorTime - Cpu).TotalMilliseconds;
            return new(name, operations, elapsed * 1_000d / Stopwatch.Frequency, cpu, allocated, gen0, gen1, gen2, checksum);
        }
    }
}

internal enum LayoutFullCostImplementation { NativeLayout, ManagedContiguous, ManagedSeparate }
internal sealed record LayoutFullCostPhase(string Name, int Operations, double WallMilliseconds, double CpuMilliseconds,
    long ManagedAllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, long Checksum);
internal sealed record LayoutFullCostReport(LayoutFullCostImplementation Implementation, int Cycles, int WarmupCycles,
    int ByteCount, int IntegerCount, int LongCount, int DetachedIntegerCount, int MovesPerCycle, int ReadsPerCycle,
    string Rid, string Architecture, string Runtime, string? TieredCompilation, string? TieredPgo,
    LayoutFullCostPhase[] Phases, long[] CycleTicks, long TimestampFrequency,
    long Checksum, long ExpectedChecksum, long WarmupChecksum, long ExpectedWarmupChecksum,
    long LogicalPayloadBytes, long SourceBackingBytes, long PeakLiveBackingBytes, long CopiedBytesPerCycle,
    int SourceBackingAcquisitionsPerCycle, long? NativeBackingAcquisitions, long? DescriptorFieldBytes,
    NativeMemoryStatistics ProcessBefore, NativeMemoryStatistics ProcessAfter,
    NativeMemoryBudgetStatistics? Budget, NativeLayoutStatistics? TerminalLayout, NativeTransferStatistics? TerminalDetached,
    bool ExactOutput, bool ExactCleanup, bool ExactNativeAccounting, string MemoryDomain, string MeasurementScope);
