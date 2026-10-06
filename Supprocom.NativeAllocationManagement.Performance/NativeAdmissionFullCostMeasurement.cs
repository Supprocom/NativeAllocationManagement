using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Supprocom.NativeAllocationManagement.Performance;

// All implementations perform the same producer/admission schedule. Retained
// managed backing is an expert reuse baseline, not a simulated native free.
internal static class NativeAdmissionFullCostMeasurement
{
    internal const int CapacityBytes = 65_536;
    internal const int OriginalBytes = 54_272;
    internal const int ProbeBytes = CapacityBytes - OriginalBytes;
    internal const int WarmupCycles = 32;
    private static readonly int[] Lengths = [32_768, 16_384, 4_096, 1_024];
    private static readonly int[] Offsets = [0, 32_768, 49_152, 53_248];
    private static readonly NativeLeaseInitializer<byte> Fill = static writer => writer.Fill(7);
    private static readonly NativeLeaseFunc<byte, long> Check = static view => Verify(view.AsSpan());
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        Converters = { new NativeExtentJsonConverter() }
    };

    internal static string Serialize(AdmissionFullCostReport report) => JsonSerializer.Serialize(report, ReportJsonOptions);

    internal static int RunCommand(string[] arguments)
    {
        if (arguments is not ["--admission-full-cost-worker", "--implementation", string name, "--cycles", string cyclesText]
            || !Enum.TryParse(name, ignoreCase: false, out AdmissionFullCostImplementation implementation)
            || !Enum.IsDefined(implementation) || !string.Equals(name, implementation.ToString(), StringComparison.Ordinal)
            || !int.TryParse(cyclesText, NumberStyles.None, CultureInfo.InvariantCulture, out int cycles))
            throw new ArgumentException("Expected --admission-full-cost-worker --implementation <Native|NativeTrace|ManagedExact|ManagedRetained> --cycles <positive integer>.", nameof(arguments));
        AdmissionFullCostReport report = Run(implementation, cycles);
        Console.WriteLine(Serialize(report));
        return report.ExactOutput && report.ExactCleanup && report.ExactAccounting ? 0 : 3;
    }

    internal static AdmissionFullCostReport Run(AdmissionFullCostImplementation implementation, int cycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cycles);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cycles, 1_000_000);
        if (!Enum.IsDefined(implementation)) throw new ArgumentOutOfRangeException(nameof(implementation));
        _ = Fill; _ = Check; _ = Lengths; _ = Offsets;
        long[] ticks = new long[cycles];
        AdmissionFullCostPhase[] phases = new AdmissionFullCostPhase[4];
        using Process process = Process.GetCurrentProcess();
        NativeMemoryStatistics original = NativeMemoryDiagnostics.Snapshot();
        bool native = implementation is AdmissionFullCostImplementation.Native or AdmissionFullCostImplementation.NativeTrace;
        AdmissionClock clock = AdmissionClock.Start(process);
        NativeSchedule? nativeSchedule = native ? new(implementation == AdmissionFullCostImplementation.NativeTrace) : null;
        ManagedSchedule? managedSchedule = native ? null : new(implementation == AdmissionFullCostImplementation.ManagedRetained);
        phases[0] = clock.End("preparation", 1, 0, process);
        try
        {
            long warmChecksum = 0;
            clock = AdmissionClock.Start(process);
            for (int cycle = 0; cycle < WarmupCycles; cycle++)
                warmChecksum += native ? nativeSchedule!.Visit() : managedSchedule!.Visit();
            phases[1] = clock.End("warm-up full lifecycle", WarmupCycles, warmChecksum, process);
            long checksum = 0;
            clock = AdmissionClock.Start(process);
            foreach (ref long cycleTicks in ticks.AsSpan())
            {
                long start = Stopwatch.GetTimestamp();
                checksum += native ? nativeSchedule!.Visit() : managedSchedule!.Visit();
                cycleTicks = Stopwatch.GetTimestamp() - start;
            }
            phases[2] = clock.End("complete admission lifecycle", cycles, checksum, process);
            clock = AdmissionClock.Start(process);
            ManagedAdmissionObservation? managedBeforeCleanup = managedSchedule?.Capture();
            managedSchedule?.Dispose();
            NativeMemoryBudgetStatistics? budget = nativeSchedule?.Budget.CaptureStatistics();
            NativeMemoryAdmissionStatistics? admission = nativeSchedule?.Budget.CaptureAdmissionStatistics();
            NativeMemoryTraceEvent[]? trace = native ? new NativeMemoryTraceEvent[budget!.Value.TraceCount] : null;
            if (trace is not null && nativeSchedule!.Budget.CopyTraceTo(trace) != trace.Length)
                throw new InvalidDataException("Retained trace count differs from the actual copy.");
            ManagedAdmissionObservation? managed = managedSchedule?.Capture();
            NativeMemoryReservationStatistics? movedPermission = nativeSchedule?.LastPermission?.CaptureSnapshot();
            NativeTransferStatistics? movedOwner = nativeSchedule?.LastOwner?.CaptureSnapshot();
            NativeMemoryStatistics terminal = NativeMemoryDiagnostics.Snapshot();
            phases[3] = clock.End("terminal cleanup and diagnostics", 1, 0, process);
            if (original.MetricsEpoch != terminal.MetricsEpoch) throw new InvalidOperationException("The process accounting epoch changed.");
            long visits = cycles + WarmupCycles;
            bool cleanup = native ? budget is { CommittedBytes: 0, ReservedBytes: 0, ActiveAllocationCount: 0 }
                && admission is { OutstandingReservationCount: 0, PendingBytes: 0, PreparedUnpublishedBytes: 0 }
                : managedBeforeCleanup is { AdmittedBytes: 0 }
                    && managedBeforeCleanup.Value.LiveBackingBytes == (implementation == AdmissionFullCostImplementation.ManagedRetained ? CapacityBytes : 0)
                    && managed is { AdmittedBytes: 0, LiveBackingBytes: 0 };
            bool accounting = native ? budget is
            {
                PeakCommittedBytes: CapacityBytes, PeakAdmittedBytes: CapacityBytes,
                FailedAllocationCount: 0, ReallocationCount: 0
            }
                && budget.Value.AllocationCount == 6 * visits && budget.Value.FreeCount == 6 * visits
                && budget.Value.RejectedAllocationCount == 3 * visits
                && admission is
                {
                    PeakOutstandingReservationCount: 5, PeakPendingBytes: CapacityBytes,
                    PeakPreparedUnpublishedBytes: CapacityBytes, ControlPreparationFailureCount: 0,
                    BackingPreparationFailureCount: 0, BackendAllocationFailureCount: 0, InitializationFailureCount: 0,
                    AbandonedReservationCount: 0, ReturnFailureCount: 0, HistoryOverflowed: false
                }
                && admission.Value.AdmittedReservationCount == 7 * visits && admission.Value.RejectedReservationCount == 3 * visits
                && admission.Value.BackingPreparationCount == 6 * visits && admission.Value.ActivationCount == 5 * visits
                && admission.Value.CancelledReservationCount == 2 * visits
                && movedPermission is { ReservationMoveCount: 1, Outcome: NativeMemoryReservationOutcome.Activated, HasReservationReturnObligation: false }
                && movedOwner is { MoveCount: 1, HasReturnObligation: false, OwnedBackingBytes: 0 }
                : managed is { PeakAdmittedBytes: CapacityBytes, PeakLiveBackingBytes: CapacityBytes }
                    && managed.Value.AdmissionCount == 7 * visits && managed.Value.RefusalCount == 3 * visits
                    && managed.Value.PendingCancellationCount == visits && managed.Value.PreparedCancellationCount == visits
                    && managed.Value.PreparationCount == 6 * visits && managed.Value.ProducerCount == 5 * visits
                    && managed.Value.ReservationMoveCount == visits && managed.Value.UniqueMoveCount == 5 * visits
                    && managed.Value.ConsumedPermissionBytes == 0 && managed.Value.MovedPermissionBytes == 16_384
                    && managed.Value.BackingAcquisitionCount == (implementation == AdmissionFullCostImplementation.ManagedRetained ? 1 : 6 * visits);
            return new(implementation, cycles, WarmupCycles, CapacityBytes, OriginalBytes, ProbeBytes,
                RuntimeInformation.RuntimeIdentifier, RuntimeInformation.ProcessArchitecture.ToString(), Environment.Version.ToString(),
                Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
                phases, ticks, Stopwatch.Frequency, checksum, ExpectedChecksum(cycles), warmChecksum, ExpectedChecksum(WarmupCycles),
                original, terminal, budget, admission, trace, movedPermission, movedOwner, managedBeforeCleanup, managed,
                checksum == ExpectedChecksum(cycles) && warmChecksum == ExpectedChecksum(WarmupCycles), cleanup, accounting,
                native ? "Actual NAM complete requested extents, pending permission, committed unpublished backing and physical release; no process RSS or complete managed-metadata bound."
                    : "Live managed array-element backing and separate producer permission. The retained implementation allocates one exact 65536-byte array and directly reuses fixed contiguous ranges. Dropping authority is not observed physical freeing, GC reclamation or RSS relief; native observations are unavailable (null).",
                "Preparation, warm-up, full admission/production/refusal/cancellation/move/overlap and terminal cleanup/diagnostics are timed. Operational storage and actual optional tracing are included. Measurement tick/phase arrays, process-CPU-query allocation and serialization are excluded consistently. NativeTrace compares optional tracing cost with Native, not equivalent managed observability. No material-benefit, platform or release verdict.");
        }
        finally { nativeSchedule?.Dispose(); managedSchedule?.Dispose(); }
    }

    internal static long ExpectedChecksum(int cycles) => checked(cycles * 838_656L);

    // Trace extents are numeric native-width quantities, not native addresses.
    // JSON natively supports UInt64 but deliberately rejects System.UIntPtr.
    // This output-only worker policy preserves every actual event field; it
    // does not change the runtime API or enter any measured phase.
    private sealed class NativeExtentJsonConverter : JsonConverter<nuint>
    {
        public override nuint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            checked((nuint)reader.GetUInt64());
        public override void Write(Utf8JsonWriter writer, nuint value, JsonSerializerOptions options) =>
            writer.WriteNumberValue((ulong)value);
    }

    private static long Verify(scoped ReadOnlySpan<byte> values)
    {
        long checksum = 0;
        foreach (ref readonly byte value in values)
        {
            if (value != 7) throw new InvalidDataException("An admitted initialized payload differs.");
            checksum += value;
        }
        return checksum;
    }

    private sealed class NativeSchedule : IDisposable
    {
        private readonly NativeMemoryReservation<byte>?[] _permissions = new NativeMemoryReservation<byte>?[4];
        private readonly NativeTransfer<byte>?[] _owners = new NativeTransfer<byte>?[4];
        internal NativeMemoryBudget Budget { get; }
        internal NativeMemoryReservation<byte>? LastPermission { get; private set; }
        internal NativeTransfer<byte>? LastOwner { get; private set; }
        internal NativeSchedule(bool trace) => Budget = new(CapacityBytes, trace ? 64 : 0);

        internal long Visit()
        {
            NativeMemoryReservation<byte>? probe = null;
            NativeTransfer<byte>? replacement = null;
            try
            {
                for (int index = 0; index < Lengths.Length; index++) _permissions[index] = Admit(Lengths[index]);
                _permissions[1] = NativeMemoryReservation<byte>.Move(ref _permissions[1]);
                LastPermission = _permissions[1];
                probe = Admit(ProbeBytes);
                Refuse(1);
                probe.Value.Dispose(); probe = null;
                foreach (NativeMemoryReservation<byte>? permission in _permissions) permission!.Value.PrepareBacking();
                probe = Admit(ProbeBytes);
                probe.Value.PrepareBacking();
                Refuse(1);
                probe.Value.Dispose(); probe = null;
                long checksum = 0;
                for (int index = 0; index < Lengths.Length; index++)
                {
                    _owners[index] = NativeMemoryReservation<byte>.Activate(ref _permissions[index], Fill);
                    _owners[index] = NativeTransfer<byte>.Move(ref _owners[index]);
                    checksum += _owners[index]!.Value.Read(Check);
                }
                Refuse(16_384);
                foreach (ref NativeTransfer<byte>? owner in _owners.AsSpan(1)) Return(ref owner);
                probe = Admit(32_768);
                probe.Value.PrepareBacking();
                replacement = NativeMemoryReservation<byte>.Activate(ref probe, Fill);
                replacement = NativeTransfer<byte>.Move(ref replacement);
                LastOwner = replacement;
                checksum += replacement.Value.Read(Check);
                checksum += _owners[0]!.Value.Read(Check);
                Return(ref _owners[0]);
                Return(ref replacement);
                return checksum;
            }
            finally { probe?.Dispose(); replacement?.Dispose(); Dispose(); }
        }

        private NativeMemoryReservation<byte> Admit(int bytes)
        {
            if (!Budget.TryReserve<byte>(bytes, out NativeMemoryReservation<byte>? permission, out NativeMemoryAdmissionExhaustionReason reason)
                || reason != NativeMemoryAdmissionExhaustionReason.None) throw new InvalidDataException("Declared admission was refused.");
            return permission.Value;
        }

        private void Refuse(int bytes)
        {
            if (Budget.TryReserve<byte>(bytes, out NativeMemoryReservation<byte>? unexpected, out NativeMemoryAdmissionExhaustionReason reason))
            {
                unexpected.Value.Dispose();
                throw new InvalidDataException("A request over the byte cap was admitted.");
            }
            if (unexpected is not null || reason != NativeMemoryAdmissionExhaustionReason.NativeByteCapacity)
                throw new InvalidDataException("Expected refusal lost its distinct reason.");
        }

        private static void Return(ref NativeTransfer<byte>? owner)
        {
            owner!.Value.Dispose();
            owner = null;
        }

        public void Dispose()
        {
            for (int index = 0; index < _owners.Length; index++)
            {
                if (_owners[index] is not null) Return(ref _owners[index]);
                _permissions[index]?.Dispose();
                _permissions[index] = null;
            }
        }
    }

    // Lexical single-threaded jobs need no reference counting, stale-native
    // version banks or per-producer wrapper. Storage authority and permission
    // are distinct: retained backing is charged once, not once per reuse.
    private sealed class ManagedSchedule : IDisposable
    {
        private byte[]? _retained;
        private readonly Memory<byte>[] _jobs = new Memory<byte>[4];
        private long _admitted, _peakAdmitted, _liveBacking, _peakBacking;
        private long _admissions, _refusals, _pendingCancellations, _preparedCancellations;
        private long _preparations, _producers, _reservationMoves, _uniqueMoves, _acquisitions;
        private int _consumedPermissionBytes, _movedPermissionBytes;

        internal ManagedSchedule(bool retained)
        {
            if (!retained) return;
            _retained = GC.AllocateUninitializedArray<byte>(CapacityBytes);
            _liveBacking = _peakBacking = CapacityBytes;
            _acquisitions = 1;
        }

        internal long Visit()
        {
            foreach (int length in Lengths) Admit(length);
            int permission = Lengths[1];
            int movedPermission = MovePermission(ref permission);
            _consumedPermissionBytes = permission;
            _movedPermissionBytes = movedPermission;
            Admit(ProbeBytes);
            Refuse(1);
            _admitted -= ProbeBytes;
            _pendingCancellations++;
            _jobs[0] = Prepare(Lengths[0], Offsets[0]);
            _jobs[1] = Prepare(movedPermission, Offsets[1]);
            _jobs[2] = Prepare(Lengths[2], Offsets[2]);
            _jobs[3] = Prepare(Lengths[3], Offsets[3]);
            Admit(ProbeBytes);
            Memory<byte> probe = Prepare(ProbeBytes, OriginalBytes);
            Refuse(1);
            ReturnBacking(probe.Length);
            probe = default;
            _admitted -= ProbeBytes;
            _preparedCancellations++;
            long checksum = 0;
            foreach (ref Memory<byte> job in _jobs.AsSpan())
            {
                Produce(job);
                Memory<byte> moved = job;
                job = default;
                job = moved;
                _uniqueMoves++;
                checksum += Verify(job.Span);
            }
            Refuse(16_384);
            for (int index = 1; index < _jobs.Length; index++) ReturnJob(index);
            Admit(32_768);
            Memory<byte> replacement = Prepare(32_768, 32_768);
            Produce(replacement);
            Memory<byte> destination = replacement;
            replacement = default;
            replacement = destination;
            _uniqueMoves++;
            checksum += Verify(replacement.Span);
            checksum += Verify(_jobs[0].Span);
            ReturnJob(0);
            ReturnBacking(replacement.Length);
            _admitted -= replacement.Length;
            replacement = default;
            return checksum;
        }

        private void Admit(int bytes)
        {
            if (bytes > CapacityBytes - _admitted) throw new InvalidDataException("Declared managed permission was refused.");
            _admitted += bytes;
            _peakAdmitted = Math.Max(_peakAdmitted, _admitted);
            _admissions++;
        }
        private void Refuse(int bytes)
        {
            if (bytes <= CapacityBytes - _admitted) throw new InvalidDataException("Expected managed byte refusal was admitted.");
            _refusals++;
        }
        private int MovePermission(ref int source)
        {
            int bytes = source;
            source = 0;
            _reservationMoves++;
            return bytes;
        }
        private Memory<byte> Prepare(int bytes, int offset)
        {
            _preparations++;
            if (_retained is not null) return _retained.AsMemory(offset, bytes);
            byte[] backing = GC.AllocateUninitializedArray<byte>(bytes);
            _liveBacking += bytes;
            _peakBacking = Math.Max(_peakBacking, _liveBacking);
            _acquisitions++;
            return backing;
        }
        private void Produce(Memory<byte> memory)
        {
            memory.Span.Fill(7);
            _producers++;
        }
        private void ReturnBacking(int bytes)
        {
            if (_retained is null) _liveBacking -= bytes;
        }
        private void ReturnJob(int index)
        {
            int bytes = _jobs[index].Length;
            _jobs[index] = default;
            ReturnBacking(bytes);
            _admitted -= bytes;
        }
        internal ManagedAdmissionObservation Capture() => new(_admitted, _peakAdmitted, _liveBacking, _peakBacking,
            _admissions, _refusals, _pendingCancellations, _preparedCancellations, _preparations,
            _producers, _reservationMoves, _uniqueMoves, _acquisitions, _consumedPermissionBytes, _movedPermissionBytes);
        public void Dispose()
        {
            Array.Clear(_jobs);
            _retained = null;
            _liveBacking = 0;
            _admitted = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AdmissionClock(long Timestamp, TimeSpan Cpu, long Allocated, int Gen0, int Gen1, int Gen2)
    {
        internal static AdmissionClock Start(Process process)
        {
            TimeSpan cpu = process.TotalProcessorTime;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            return new(Stopwatch.GetTimestamp(), cpu, allocated, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        }
        internal AdmissionFullCostPhase End(string name, int operations, long checksum, Process process)
        {
            long elapsed = Stopwatch.GetTimestamp() - Timestamp;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - Allocated;
            int gen0 = GC.CollectionCount(0) - Gen0, gen1 = GC.CollectionCount(1) - Gen1, gen2 = GC.CollectionCount(2) - Gen2;
            return new(name, operations, elapsed * 1_000d / Stopwatch.Frequency, (process.TotalProcessorTime - Cpu).TotalMilliseconds,
                allocated, gen0, gen1, gen2, checksum);
        }
    }
}

internal enum AdmissionFullCostImplementation { Native, NativeTrace, ManagedExact, ManagedRetained }
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct ManagedAdmissionObservation(long AdmittedBytes, long PeakAdmittedBytes,
    long LiveBackingBytes, long PeakLiveBackingBytes, long AdmissionCount, long RefusalCount,
    long PendingCancellationCount, long PreparedCancellationCount, long PreparationCount, long ProducerCount,
    long ReservationMoveCount, long UniqueMoveCount, long BackingAcquisitionCount,
    int ConsumedPermissionBytes, int MovedPermissionBytes);
internal sealed record AdmissionFullCostPhase(string Name, int Operations, double WallMilliseconds, double CpuMilliseconds,
    long ManagedAllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, long Checksum);
internal sealed record AdmissionFullCostReport(AdmissionFullCostImplementation Implementation, int Cycles, int WarmupCycles,
    int CapacityBytes, int OriginalBytes, int ProbeBytes, string Rid, string Architecture, string Runtime,
    string? TieredCompilation, string? TieredPgo, AdmissionFullCostPhase[] Phases, long[] CycleTicks, long TimestampFrequency,
    long Checksum, long ExpectedChecksum, long WarmupChecksum, long ExpectedWarmupChecksum,
    NativeMemoryStatistics ProcessBefore, NativeMemoryStatistics ProcessAfter, NativeMemoryBudgetStatistics? Budget,
    NativeMemoryAdmissionStatistics? Admission, NativeMemoryTraceEvent[]? Trace,
    NativeMemoryReservationStatistics? MovedPermission, NativeTransferStatistics? MovedOwner,
    ManagedAdmissionObservation? ManagedBeforeCleanup, ManagedAdmissionObservation? Managed,
    bool ExactOutput, bool ExactCleanup, bool ExactAccounting, string MemoryDomain, string MeasurementScope);
