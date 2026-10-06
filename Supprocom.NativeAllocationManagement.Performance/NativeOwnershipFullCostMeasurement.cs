using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Performance;

// Same useful work, measured through the actual public ownership boundaries.
internal static class NativeOwnershipFullCostMeasurement
{
    internal const int PayloadBytes = 4_096;
    internal const int SliceBytes = 16;
    internal const int Moves = 16;
    internal const int Reads = 32;
    internal const int Upgrades = 8;
    internal const int WarmupCycles = 32;
    private static readonly NativeLeaseInitializer<byte> Fill = static writer => writer.Fill(7);
    private static readonly NativeLeaseFunc<byte, long> UniqueRead = static view => Verify(view.AsSpan(), PayloadBytes);
    private static readonly NativeLeaseFunc<byte, long> DetachedRead = static view => Verify(view.AsSpan(), SliceBytes);
    private static readonly NativeReadOnlyLeaseFunc<byte, long> SharedRead = static view => Verify(view.AsSpan(), PayloadBytes);

    internal static int RunCommand(string[] arguments)
    {
        if (arguments is not ["--ownership-full-cost-worker", "--implementation", string implementationText,
            "--contract", string contractText, "--cycles", string cyclesText]
            || !Enum.TryParse(implementationText, ignoreCase: false, out OwnershipFullCostImplementation implementation)
            || !Enum.IsDefined(implementation) || !string.Equals(implementationText, implementation.ToString(), StringComparison.Ordinal)
            || !Enum.TryParse(contractText, ignoreCase: false, out OwnershipFullCostContract contract)
            || !Enum.IsDefined(contract) || !string.Equals(contractText, contract.ToString(), StringComparison.Ordinal)
            || !int.TryParse(cyclesText, NumberStyles.None, CultureInfo.InvariantCulture, out int cycles))
            throw new ArgumentException("Expected --ownership-full-cost-worker --implementation <Managed|Native> --contract <Unique|Shared|SharedWeak> --cycles <positive integer>.", nameof(arguments));
        OwnershipFullCostReport report = Run(implementation, contract, cycles);
        Console.WriteLine(JsonSerializer.Serialize(report));
        return report.ExactOutput && report.ExactCleanup && report.ExactNativeAccounting ? 0 : 3;
    }

    internal static OwnershipFullCostReport Run(OwnershipFullCostImplementation implementation, OwnershipFullCostContract contract, int cycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cycles);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cycles, 1_000_000);
        if (!Enum.IsDefined(implementation)) throw new ArgumentOutOfRangeException(nameof(implementation));
        if (!Enum.IsDefined(contract)) throw new ArgumentOutOfRangeException(nameof(contract));
        _ = Fill; _ = UniqueRead; _ = SharedRead; _ = DetachedRead;
        long[] ticks = new long[cycles];
        OwnershipFullCostPhase[] phases = new OwnershipFullCostPhase[4];
        using Process process = Process.GetCurrentProcess();
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        NativeMemoryBudget? budget = null;
        NativeTransfer<byte>? observedUnique = null;
        NativeShared<byte>? observedShared = null;
        NativeTransfer<byte>? observedDetached = null;
        bool native = implementation == OwnershipFullCostImplementation.Native;
        bool sharing = contract != OwnershipFullCostContract.Unique;
        long peakBytes = PayloadBytes + (sharing ? SliceBytes : 0);
        OwnershipClock clock = OwnershipClock.Start(process);
        if (native) budget = new NativeMemoryBudget(peakBytes);
        phases[0] = clock.End("preparation", 1, 0, process);
        long warmChecksum = 0;
        clock = OwnershipClock.Start(process);
        for (int cycle = 0; cycle < WarmupCycles; cycle++)
            warmChecksum += Visit(implementation, contract, budget, out observedUnique, out observedShared, out observedDetached);
        phases[1] = clock.End("warm-up full lifecycle", WarmupCycles, warmChecksum, process);
        long checksum = 0;
        clock = OwnershipClock.Start(process);
        foreach (ref long cycleTicks in ticks.AsSpan())
        {
            long start = Stopwatch.GetTimestamp();
            checksum += Visit(implementation, contract, budget, out observedUnique, out observedShared, out observedDetached);
            cycleTicks = Stopwatch.GetTimestamp() - start;
        }
        phases[2] = clock.End("complete ownership lifecycle", cycles, checksum, process);
        clock = OwnershipClock.Start(process);
        NativeMemoryBudgetStatistics? terminalBudget = budget?.CaptureStatistics();
        NativeTransferStatistics? unique = observedUnique?.CaptureSnapshot();
        NativeSharingStatistics? shared = observedShared?.CaptureSnapshot();
        NativeTransferStatistics? detached = observedDetached?.CaptureSnapshot();
        NativeMemoryStatistics after = NativeMemoryDiagnostics.Snapshot();
        phases[3] = clock.End("terminal diagnostics", 1, 0, process);
        if (before.MetricsEpoch != after.MetricsEpoch) throw new InvalidOperationException("Accounting epoch changed during the measured workload.");
        int acquisitions = sharing ? 2 : 1;
        long expectedAcquisitions = acquisitions * (long)(cycles + WarmupCycles);
        bool cleanup = !native || terminalBudget is { CommittedBytes: 0, ReservedBytes: 0 }
            && unique is { OwnedBackingBytes: 0, ActiveBorrowCount: 0, HasReturnObligation: false }
            && (!sharing || shared is
            {
                StrongBindingCount: 0, WeakBindingCount: 0, ActiveReadCount: 0,
                OwnedBackingBytes: 0, InitializedPayloadBytes: 0, ManagedBankBytes: 0, Expired: true, PayloadReleased: true
            }
                && detached is { OwnedBackingBytes: 0, HasReturnObligation: false });
        bool accounting = !native || terminalBudget is { } actual && actual.AllocationCount == expectedAcquisitions
            && actual.FreeCount == expectedAcquisitions && actual.PeakAdmittedBytes == peakBytes
            && actual.RejectedAllocationCount == 0 && actual.FailedAllocationCount == 0
            && unique?.MoveCount == Moves + (sharing ? 1 : 0)
            && (!sharing || shared is
            {
                ShareCount: Reads + 1, DetachCount: 1, PayloadReturnCount: 1,
                PeakStrongBindingCount: 2
            } && shared.Value.WeakCreationCount == (contract == OwnershipFullCostContract.SharedWeak ? 1 : 0)
                && shared.Value.SuccessfulUpgradeCount == (contract == OwnershipFullCostContract.SharedWeak ? Upgrades : 0)
                && shared.Value.ExpiredUpgradeCount == (contract == OwnershipFullCostContract.SharedWeak ? 1 : 0));
        return new OwnershipFullCostReport(implementation, contract, cycles, WarmupCycles, PayloadBytes, SliceBytes, Moves, Reads,
            contract == OwnershipFullCostContract.SharedWeak ? Upgrades : 0,
            RuntimeInformation.RuntimeIdentifier, RuntimeInformation.ProcessArchitecture.ToString(), Environment.Version.ToString(),
            Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
            phases, ticks, Stopwatch.Frequency, checksum, ExpectedChecksum(contract, cycles), warmChecksum,
            ExpectedChecksum(contract, WarmupCycles), peakBytes, sharing ? SliceBytes : 0,
            native ? expectedAcquisitions : null, before, after, terminalBudget, unique, shared, detached,
            checksum == ExpectedChecksum(contract, cycles) && warmChecksum == ExpectedChecksum(contract, WarmupCycles), cleanup, accounting,
            native ? "Actual NAM requested extents, overlap, physical budget events and native ownership histories. Control snapshots are sampled outside lifecycles in their own timed phase."
                : "Live exact managed array-element extents and explicit-copy overlap, excluding CLR headers. Managed atomic sharing bounds simultaneous strong ownership at two; weak control holds no payload reference after final release. Entered readers use GC-protected array references. No native stale-address/version bank is needed; native observations are unavailable (null). Dropping authority is not physical freeing or RSS relief.",
            "Preparation, warm-up and complete ownership lifecycles include admission, backing, initialization, moves, conversion, sharing banks, all useful reads, observer upgrades/expiry, copies and cleanup. Instrumentation arrays, process-CPU-query allocation and report serialization are excluded on every side. No benefit, complete managed metadata, native matrix or release verdict.");
    }

    internal static long ExpectedChecksum(OwnershipFullCostContract contract, int cycles) => checked(cycles *
        (PayloadBytes * 7L * (Reads + (contract == OwnershipFullCostContract.SharedWeak ? Upgrades : 0))
            + (contract == OwnershipFullCostContract.Unique ? 0 : SliceBytes * 7L)));

    private static long Visit(OwnershipFullCostImplementation implementation, OwnershipFullCostContract contract, NativeMemoryBudget? budget,
        out NativeTransfer<byte>? unique, out NativeShared<byte>? shared, out NativeTransfer<byte>? detached)
    {
        unique = null; shared = null; detached = null;
        return implementation == OwnershipFullCostImplementation.Native
            ? VisitNative(contract, budget!, out unique, out shared, out detached) : VisitManaged(contract);
    }

    private static long VisitNative(OwnershipFullCostContract contract, NativeMemoryBudget budget,
        out NativeTransfer<byte>? observedUnique, out NativeShared<byte>? observedShared, out NativeTransfer<byte>? observedDetached)
    {
        observedUnique = null; observedShared = null; observedDetached = null;
        if (!budget.TryReserve<byte>(PayloadBytes, out NativeMemoryReservation<byte>? permission, out _))
            throw new InvalidOperationException("Declared unique admission was refused.");
        NativeTransfer<byte>? moving = NativeMemoryReservation<byte>.Activate(ref permission, Fill);
        try
        {
            for (int move = 0; move < Moves; move++) moving = NativeTransfer<byte>.Move(ref moving);
            observedUnique = moving;
            if (contract == OwnershipFullCostContract.Unique)
            {
                long result = 0;
                for (int read = 0; read < Reads; read++) result += moving.Value.Read(UniqueRead);
                return result;
            }
            bool weakContract = contract == OwnershipFullCostContract.SharedWeak;
            NativeShared<byte> root = NativeShared<byte>.Create(ref moving, new(2, weakContract ? 1 : 0));
            observedShared = root;
            bool rootReleased = false;
            NativeWeak<byte>? weak = null;
            try
            {
                long checksum = 0;
                for (int read = 0; read < Reads; read++)
                {
                    if (!root.TryShare(out NativeShared<byte> share, out _)) throw new InvalidOperationException("Declared strong slot refused.");
                    try { checksum += share.Read(SharedRead); }
                    finally { share.Dispose(); }
                }
                if (weakContract)
                {
                    if (!root.TryDowngrade(out NativeWeak<byte> observer, out _)) throw new InvalidOperationException("Declared weak slot refused.");
                    weak = observer;
                    for (int upgrade = 0; upgrade < Upgrades; upgrade++)
                    {
                        if (!observer.TryUpgrade(out NativeShared<byte> upgraded, out _)) throw new InvalidOperationException("Live observer upgrade refused.");
                        try { checksum += upgraded.Read(SharedRead); }
                        finally { upgraded.Dispose(); }
                    }
                }
                if (!root.TrySlice(17, SliceBytes, out NativeShared<byte> slice, out _)) throw new InvalidOperationException("Declared slice refused.");
                try
                {
                    root.Dispose(); rootReleased = true;
                    if (!slice.TryDetach(budget, out NativeTransfer<byte> copy)) throw new InvalidOperationException("Declared copy overlap refused.");
                    try
                    {
                        slice.Dispose();
                        if (weak is { } observer)
                        {
                            if (observer.TryUpgrade(out NativeShared<byte> unexpected, out NativeSharingExhaustionReason reason))
                            {
                                unexpected.Dispose();
                                throw new InvalidDataException("Expired observer resurrected payload.");
                            }
                            if (reason != NativeSharingExhaustionReason.ExpiredPayload) throw new InvalidDataException("Observer expiration reason differs.");
                        }
                        checksum += copy.Read(DetachedRead);
                    }
                    finally { copy.Dispose(); observedDetached = copy; }
                }
                catch
                {
                    if (!slice.CaptureSnapshot().Expired) slice.Dispose();
                    throw;
                }
                return checksum;
            }
            finally { weak?.Dispose(); if (!rootReleased) root.Dispose(); }
        }
        // Public Move/Create consume this exact ref binding, also on some
        // exceptional paths; CA1508 does not model those destructive writes.
#pragma warning disable CA1508
        finally { moving?.Dispose(); }
#pragma warning restore CA1508
    }

    private static long VisitManaged(OwnershipFullCostContract contract)
    {
        byte[]? moving = GC.AllocateUninitializedArray<byte>(PayloadBytes);
        moving.AsSpan().Fill(7);
        for (int move = 0; move < Moves; move++)
        {
            byte[] next = moving!;
            moving = null;
            moving = next;
        }
        if (contract == OwnershipFullCostContract.Unique)
        {
            long result = 0;
            for (int read = 0; read < Reads; read++) result += Verify(moving, PayloadBytes);
            moving = null;
            return result;
        }
        ManagedPayload root = new(moving!);
        moving = null;
        bool rootReleased = false;
        try
        {
            long checksum = 0;
            for (int read = 0; read < Reads; read++)
            {
                if (!root.TryAcquire()) throw new InvalidOperationException("Declared managed strong slot refused.");
                try { checksum += Verify(root.GetPayload(), PayloadBytes); }
                finally { root.Release(); }
            }
            // A weak observer references this control only; it stores no array.
            ManagedPayload? observer = contract == OwnershipFullCostContract.SharedWeak ? root : null;
            if (observer is not null)
            {
                for (int upgrade = 0; upgrade < Upgrades; upgrade++)
                {
                    if (!observer.TryAcquire()) throw new InvalidOperationException("Live managed observer upgrade refused.");
                    try { checksum += Verify(observer.GetPayload(), PayloadBytes); }
                    finally { observer.Release(); }
                }
            }
            if (!root.TryAcquire()) throw new InvalidOperationException("Declared managed slice refused.");
            bool sliceReleased = false;
            try
            {
                root.Release(); rootReleased = true;
                byte[] copy = root.GetPayload().AsSpan(17, SliceBytes).ToArray();
                root.Release(); sliceReleased = true;
                if (observer is not null && observer.TryAcquire()) throw new InvalidDataException("Expired managed observer resurrected payload.");
                checksum += Verify(copy, SliceBytes);
                GC.KeepAlive(copy);
            }
            finally { if (!sliceReleased) root.Release(); }
            if (root.RetainsPayload) throw new InvalidDataException("Managed final release retained payload authority.");
            return checksum;
        }
        finally { if (!rootReleased) root.Release(); }
    }

    private static long Verify(ReadOnlySpan<byte> values, int expectedLength)
    {
        if (values.Length != expectedLength) throw new InvalidDataException("Ownership range length differs.");
        long checksum = 0;
        foreach (ref readonly byte value in values)
        {
            if (value != 7) throw new InvalidDataException("Ownership payload differs.");
            checksum += value;
        }
        return checksum;
    }

    // GC protects an entered local array reference. A weak observer holds only
    // this control; the final strong release clears its sole payload reference.
    private sealed class ManagedPayload(byte[] payload)
    {
        private byte[]? _payload = payload;
        private int _strong = 1;
        internal bool RetainsPayload => Volatile.Read(ref _payload) is not null;
        internal byte[] GetPayload() => Volatile.Read(ref _payload) ?? throw new InvalidOperationException("Managed payload expired.");
        internal bool TryAcquire()
        {
            int observed = Volatile.Read(ref _strong);
            while (observed is > 0 and < 2)
            {
                int actual = Interlocked.CompareExchange(ref _strong, observed + 1, observed);
                if (actual == observed) return true;
                observed = actual;
            }
            return false;
        }
        internal void Release()
        {
            if (Interlocked.Decrement(ref _strong) == 0) Volatile.Write(ref _payload, null);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct OwnershipClock(long Timestamp, TimeSpan Cpu, long ManagedBytes, int Gen0, int Gen1, int Gen2)
    {
        internal static OwnershipClock Start(Process process)
        {
            TimeSpan cpu = process.TotalProcessorTime;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            return new(Stopwatch.GetTimestamp(), cpu, allocated, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        }
        internal OwnershipFullCostPhase End(string name, int operations, long checksum, Process process)
        {
            long elapsed = Stopwatch.GetTimestamp() - Timestamp;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - ManagedBytes;
            int gen0 = GC.CollectionCount(0) - Gen0, gen1 = GC.CollectionCount(1) - Gen1, gen2 = GC.CollectionCount(2) - Gen2;
            return new(name, operations, elapsed * 1_000d / Stopwatch.Frequency, (process.TotalProcessorTime - Cpu).TotalMilliseconds,
                allocated, gen0, gen1, gen2, checksum);
        }
    }
}

internal enum OwnershipFullCostImplementation { Managed, Native }
internal enum OwnershipFullCostContract { Unique, Shared, SharedWeak }
internal sealed record OwnershipFullCostPhase(string Name, int Operations, double WallMilliseconds, double CpuMilliseconds,
    long ManagedAllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, long Checksum);
internal sealed record OwnershipFullCostReport(OwnershipFullCostImplementation Implementation, OwnershipFullCostContract Contract,
    int Cycles, int WarmupCycles, int PayloadBytes, int SliceBytes, int MovesPerCycle, int ReadsPerCycle, int UpgradesPerCycle,
    string Rid, string Architecture, string Runtime, string? TieredCompilation, string? TieredPgo,
    OwnershipFullCostPhase[] Phases, long[] CycleTicks, long TimestampFrequency, long Checksum, long ExpectedChecksum,
    long WarmupChecksum, long ExpectedWarmupChecksum, long PeakLiveBackingBytes, long CopiedBytesPerCycle,
    long? NativeBackingAcquisitions, NativeMemoryStatistics ProcessBefore, NativeMemoryStatistics ProcessAfter,
    NativeMemoryBudgetStatistics? Budget, NativeTransferStatistics? TerminalUnique,
    NativeSharingStatistics? TerminalShared, NativeTransferStatistics? TerminalDetached,
    bool ExactOutput, bool ExactCleanup, bool ExactNativeAccounting, string MemoryDomain, string MeasurementScope);
