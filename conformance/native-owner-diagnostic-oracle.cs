using System.Runtime.InteropServices;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Conformance;

// Declared operations and shapes, not captured gauge values. All reflection
// comparison is test-only and never executes inside an allocator operation.
internal static class NativeOwnerDiagnosticOracle
{
    internal static void RunBuilder(int traceCapacity)
    {
        long epoch = NativeMemoryDiagnostics.Snapshot().MetricsEpoch;
        NativeMemoryBudget budget = new(32, traceCapacity);
        using NativeBuilder<int> builder = new(budget, 2);
        Statistics expected = Statistics.Active(builder.Id, NativeOwnerModel.SingleWriterBuilder, 8, 8, 1);
        VerifyBoth(builder.GetStatistics(), builder.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { ActiveRecords = 1 });
        builder.Append(42);
        expected = expected with { RequestedBytes = 4, InitializedPayloadBytes = 4, PeakInitializedPayloadBytes = 4, AvailableSegmentCount = 0 };
        VerifyBoth(builder.GetStatistics(), builder.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { ActiveRecords = 1 });
        if (!builder.TryEnsureCapacity(4) || builder.TryEnsureCapacity(17)) throw new InvalidOperationException("Builder capacity decision differs.");
        expected = expected with { RetainedBytes = 16, OutstandingNativeBytes = 16, PeakOutstandingNativeBytes = 16, UsableCapacityBytes = 16 };
        VerifyBoth(builder.GetStatistics(), builder.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { ActiveRecords = 1 });
        NativeTransfer<int> unique = builder.Complete();
        try
        {
            expected = expected with
            {
                Lifecycle = NativeOwnerLifecycle.Returned,
                RequestedBytes = 0,
                InitializedPayloadBytes = 0,
                RetainedBytes = 0,
                OutstandingNativeBytes = 0,
                UsableCapacityBytes = 0,
                SegmentCount = 0
            };
            VerifyBoth(builder.GetStatistics(), builder.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch));
            if (budget.CaptureStatistics().CommittedBytes != 16 || unique.Read(static view => view[0]) != 42)
                throw new InvalidOperationException("Completion confused numeric builder observation with transferred authority.");
        }
        finally { unique.Dispose(); }
        if (budget.CaptureStatistics().CommittedBytes != 0) throw new InvalidOperationException("Transferred backing remains charged.");
    }

    internal static void RunWorkspace(int traceCapacity)
    {
        long epoch = NativeMemoryDiagnostics.Snapshot().MetricsEpoch;
        NativeMemoryBudget budget = new(16, traceCapacity);
        NativeWorkspace<int> workspace = new(budget, 4);
        Statistics expected = Statistics.Active(workspace.Id, NativeOwnerModel.ThreadConfinedWorkspace, 16, 16, 1);
        try
        {
            VerifyBoth(workspace.GetStatistics(), workspace.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { ActiveRecords = 1 });
            workspace.Initialize(2, static writer => writer.Fill(7));
            expected = expected with { RequestedBytes = 8, InitializedPayloadBytes = 8, PeakInitializedPayloadBytes = 8, AvailableSegmentCount = 0 };
            VerifyBoth(workspace.GetStatistics(), workspace.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { ActiveRecords = 1 });
            if (workspace.Read(static view => view[1]) != 7) throw new InvalidOperationException("Workspace initialized output differs.");
            workspace.Reset();
            expected = expected with { RequestedBytes = 0, InitializedPayloadBytes = 0, AvailableSegmentCount = 1 };
            VerifyBoth(workspace.GetStatistics(), workspace.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { ActiveRecords = 1 });
            if (workspace.Process(3, static view => view.Fill(11), static view => view[2]) != 11)
                throw new InvalidOperationException("Workspace entered output differs.");
            expected = expected with { PeakInitializedPayloadBytes = 12 };
            VerifyBoth(workspace.GetStatistics(), workspace.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { ActiveRecords = 1 });
        }
        finally { workspace.Dispose(); }
        expected = expected with
        {
            Lifecycle = NativeOwnerLifecycle.Disposed,
            RetainedBytes = 0,
            OutstandingNativeBytes = 0,
            UsableCapacityBytes = 0,
            SegmentCount = 0,
            AvailableSegmentCount = 0
        };
        VerifyBoth(workspace.GetStatistics(), workspace.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch));
        if (budget.CaptureStatistics().CommittedBytes != 0) throw new InvalidOperationException("Workspace backing remains charged.");
    }

    internal static void RunPool(int traceCapacity)
    {
        long epoch = NativeMemoryDiagnostics.Snapshot().MetricsEpoch;
        long extent = OperatingSystem.IsWindows() ? 16 : 64;
        NativeMemoryBudget budget = new(extent, traceCapacity);
        NativePool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory);
        Statistics expected = Statistics.Active(pool.Id, NativeOwnerModel.ThreadConfinedPool, extent, 16, 1);
        try
        {
            VerifyBoth(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch));
            using (Pooled<int> value = pool.Rent(2, static writer => writer.Fill(7)))
            {
                expected = expected with { RequestedBytes = 8, InitializedPayloadBytes = 8, PeakInitializedPayloadBytes = 8, AvailableSegmentCount = 0 };
                VerifyBoth(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { ActiveRecords = 1 });
                if (pool.TrimRetainedMemory() != 0 || value.Read(static view => view[1]) != 7)
                    throw new InvalidOperationException("Trim freed live backing or changed output.");
                expected = expected with { TrimCallCount = 1 };
                VerifyBoth(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { ActiveRecords = 1 });
            }
            expected = expected with { RequestedBytes = 0, InitializedPayloadBytes = 0, AvailableSegmentCount = 1 };
            VerifyBoth(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { OrdinaryTraversalIndex = 0 });
            if (pool.TrimRetainedMemory() != (nuint)extent) throw new InvalidOperationException("Idle slab trim extent differs.");
            expected = expected with
            {
                RetainedBytes = 0,
                OutstandingNativeBytes = 0,
                UsableCapacityBytes = 0,
                SegmentCount = 0,
                AvailableSegmentCount = 0,
                TrimCallCount = 2,
                TrimmedBytes = extent
            };
            VerifyBoth(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch));
        }
        finally { pool.Dispose(); }
        Verify(pool.CaptureDiagnosticSnapshot(), Structural.From(expected with { Lifecycle = NativeOwnerLifecycle.Disposed }, epoch), "closed-fast-pool");
        if (budget.CaptureStatistics().CommittedBytes != 0) throw new InvalidOperationException("Pool backing remains charged.");
    }

    internal static void RunArena(int traceCapacity)
    {
        long epoch = NativeMemoryDiagnostics.Snapshot().MetricsEpoch;
        long extent = NativeCapacityDiagnosticOracle.ArenaExtent(16) + NativeCapacityDiagnosticOracle.ArenaExtent(8);
        NativeMemoryBudget budget = new(extent, traceCapacity);
        NativeArena arena = new(new NativeArenaPreparation(16, 8), budget);
        Statistics expected = Statistics.Active(arena.Id, NativeOwnerModel.ThreadConfinedArena, extent, 24, 2) with
        { Generation = 1, SegmentCount = 2, AvailableSegmentCount = 2 };
        Structural structure = Structural.From(expected, epoch) with { ScopeEpoch = 1, OrdinaryTraversalIndex = 0, ScopedTraversalIndex = 0 };
        try
        {
            VerifyBoth(arena.GetStatistics(), arena.CaptureDiagnosticSnapshot(), expected, structure);
            {
                ArenaLease<byte> prefix = arena.Scratch<byte>(1, static writer => writer.Write(7));
                ArenaLease<long> aligned = arena.Scratch<long>(1, static writer => writer.Write(11));
                expected = expected with { RequestedBytes = 16, InitializedPayloadBytes = 9, PeakInitializedPayloadBytes = 9, AvailableSegmentCount = 1 };
                structure = Structural.From(expected, epoch) with { ScopeEpoch = 1, OrdinaryTraversalIndex = 0, ScopedTraversalIndex = 0 };
                VerifyBoth(arena.GetStatistics(), arena.CaptureDiagnosticSnapshot(), expected, structure);
                try
                {
                    scoped ArenaLease<long> scoped = arena.ScratchScoped<long>(1, static writer => writer.Write(17));
                    expected = expected with { RequestedBytes = 24, InitializedPayloadBytes = 17, PeakInitializedPayloadBytes = 17, AvailableSegmentCount = 0 };
                    VerifyBoth(arena.GetStatistics(), arena.CaptureDiagnosticSnapshot(), expected,
                        Structural.From(expected, epoch) with { ScopeEpoch = 1, OrdinaryTraversalIndex = 0, ScopedTraversalIndex = 0 });
                    if (prefix.Read(static view => view[0]) != 7 || aligned.Read(static view => view[0]) != 11 || scoped.Read(static view => view[0]) != 17)
                        throw new InvalidOperationException("Aligned/scoped payload differs.");
                }
                finally { arena.RecycleScoped(); }
                expected = expected with { RequestedBytes = 16, InitializedPayloadBytes = 9, AvailableSegmentCount = 1 };
                VerifyBoth(arena.GetStatistics(), arena.CaptureDiagnosticSnapshot(), expected,
                    Structural.From(expected, epoch) with { ScopeEpoch = 2, OrdinaryTraversalIndex = 0, ScopedTraversalIndex = 0 });
            }
            arena.Reset();
            expected = expected with { Generation = 2, RequestedBytes = 0, InitializedPayloadBytes = 0, AvailableSegmentCount = 2 };
            VerifyBoth(arena.GetStatistics(), arena.CaptureDiagnosticSnapshot(), expected,
                Structural.From(expected, epoch) with { ScopeEpoch = 3, OrdinaryTraversalIndex = 0, ScopedTraversalIndex = 0 });
            if (arena.TrimRetainedMemory() != (nuint)extent) throw new InvalidOperationException("Prepared lanes trim extent differs.");
            expected = expected with
            {
                RetainedBytes = 0,
                OutstandingNativeBytes = 0,
                UsableCapacityBytes = 0,
                SegmentCount = 0,
                AvailableSegmentCount = 0,
                TrimCallCount = 1,
                TrimmedBytes = extent
            };
            VerifyBoth(arena.GetStatistics(), arena.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { ScopeEpoch = 3 });
        }
        finally { arena.Dispose(); }
        // Numeric structural observation survives closure; GetStatistics requires
        // active fast-arena authority and is intentionally not called after it.
        Verify(arena.CaptureDiagnosticSnapshot(), Structural.From(expected with { Lifecycle = NativeOwnerLifecycle.Disposed }, epoch) with { ScopeEpoch = 3 }, "closed-arena");
        if (budget.CaptureStatistics().CommittedBytes != 0) throw new InvalidOperationException("Arena backing remains charged.");
    }

    internal static void RunRegion(int traceCapacity)
    {
        long epoch = NativeMemoryDiagnostics.Snapshot().MetricsEpoch;
        long extent = NativeCapacityDiagnosticOracle.ArenaExtent(16);
        NativeMemoryBudget budget = new(extent, traceCapacity);
        bool exactOutput;
        using (NativeRegion region = new(budget, 16, NativeMemoryReturn.ToNativeMemory))
        {
            Statistics expected = Statistics.Active(region.Id, NativeOwnerModel.ThreadConfinedRegion, extent, 16, 1);
            VerifyBoth(region.GetStatistics(), region.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { OrdinaryTraversalIndex = 0 });
            Local<int> value = region.Lease<int>(2, static writer => writer.Fill(7));
            expected = expected with { RequestedBytes = 8, InitializedPayloadBytes = 8, PeakInitializedPayloadBytes = 8, AvailableSegmentCount = 0 };
            VerifyBoth(region.GetStatistics(), region.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { OrdinaryTraversalIndex = 0 });
            exactOutput = value.Read(static view => view[1]) == 7;
        }
        if (!exactOutput) throw new InvalidOperationException("Lexical payload differs.");
        if (budget.CaptureStatistics().CommittedBytes != 0) throw new InvalidOperationException("Region backing remains charged.");
    }

    internal static void RunSynchronizedPool(int traceCapacity)
    {
        long epoch = NativeMemoryDiagnostics.Snapshot().MetricsEpoch;
        long extent = OperatingSystem.IsWindows() ? 2L * IntPtr.Size : 64;
        NativeMemoryBudget budget = new(extent, traceCapacity);
        NativeConcurrentPool<string> pool = new(budget, 2, 0, NativeMemoryReturn.ToNativeMemory, false);
        Statistics expected = Statistics.Active(pool.Id, NativeOwnerModel.SynchronizedPool, extent, 2L * IntPtr.Size, 1);
        try
        {
            VerifyBoth(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { OrdinaryTraversalIndex = 0 });
            using (ConcurrentPooled<string> value = pool.Rent(2, static writer => writer.Fill("payload")))
            {
                expected = expected with
                {
                    RequestedBytes = 2L * IntPtr.Size,
                    InitializedPayloadBytes = 2L * IntPtr.Size,
                    PeakInitializedPayloadBytes = 2L * IntPtr.Size,
                    AvailableSegmentCount = 0
                };
                VerifyBoth(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot(), expected,
                    Structural.From(expected, epoch) with { OrdinaryTraversalIndex = 0, ActiveRecords = 1, ReferenceRoots = 2 });
                if (!string.Equals(value.Read(static view => view[1]), "payload", StringComparison.Ordinal))
                    throw new InvalidOperationException("Managed root payload differs.");
            }
            expected = expected with { RequestedBytes = 0, InitializedPayloadBytes = 0, AvailableSegmentCount = 1 };
            VerifyBoth(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { OrdinaryTraversalIndex = 0 });
            if (pool.TrimRetainedMemory() != (nuint)extent) throw new InvalidOperationException("Synchronized idle trim differs.");
            expected = expected with
            {
                RetainedBytes = 0,
                OutstandingNativeBytes = 0,
                UsableCapacityBytes = 0,
                SegmentCount = 0,
                AvailableSegmentCount = 0,
                TrimCallCount = 1,
                TrimmedBytes = extent
            };
            VerifyBoth(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch) with { OrdinaryTraversalIndex = 0 });
        }
        finally { pool.Dispose(); }
        expected = expected with { Lifecycle = NativeOwnerLifecycle.Disposed, Generation = 1 };
        VerifyBoth(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot(), expected, Structural.From(expected, epoch));
        if (budget.CaptureStatistics().CommittedBytes != 0) throw new InvalidOperationException("Synchronized backing remains charged.");
    }

    internal static void VerifyBoth(NativeOwnerStatistics actual, NativeOwnerDiagnosticSnapshot structure, Statistics expected, Structural expectedStructure)
    {
        Verify(actual, expected, "owner-statistics");
        Verify(structure, expectedStructure, "owner-structure");
    }

    internal static void Verify<TActual, TExpected>(TActual actual, TExpected expected, string stage) =>
        NativeAdmissionDiagnosticOracle.Verify(actual, expected, stage);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Statistics
    {
        public NativeOwnerLifecycle Lifecycle { get; init; }
        public long Generation { get; init; }
        public long RequestedBytes { get; init; }
        public long RetainedBytes { get; init; }
        public long RetiredBytes { get; init; }
        public int SegmentCount { get; init; }
        public int AvailableSegmentCount { get; init; }
        public int RetiredSegmentCount { get; init; }
        public long TrimmedBytes { get; init; }
        public long TrimCallCount { get; init; }
        public long FreshSegmentAllocationCount { get; init; }
        public bool HistoryOverflowed { get; init; }
        public NativeOwnerModel Model { get; init; }
        public long OwnerId { get; init; }
        public long UsableCapacityBytes { get; init; }
        public long BorrowedBytes { get; init; }
        public long RetiredBorrowedBytes { get; init; }
        public long OutstandingNativeBytes { get; init; }
        public long DetachedNativeBytes { get; init; }
        public long PeakOutstandingNativeBytes { get; init; }
        public long InitializedPayloadBytes { get; init; }
        public long PeakInitializedPayloadBytes { get; init; }
        internal static Statistics Active(long id, NativeOwnerModel model, long extent, long usable, long acquisitions) => new()
        {
            OwnerId = id,
            Model = model,
            Lifecycle = NativeOwnerLifecycle.Active,
            RetainedBytes = extent,
            OutstandingNativeBytes = extent,
            PeakOutstandingNativeBytes = extent,
            UsableCapacityBytes = usable,
            FreshSegmentAllocationCount = acquisitions,
            SegmentCount = extent == 0 ? 0 : 1,
            AvailableSegmentCount = extent == 0 ? 0 : 1
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Structural
    {
        public NativeOwnerLifecycle Lifecycle { get; init; }
        public long Generation { get; init; }
        public long ScopeEpoch { get; init; }
        public long MetricsEpoch { get; init; }
        public int ActiveRecords { get; init; }
        public int ScopedRecords { get; init; }
        public int ReferenceRoots { get; init; }
        public int OrdinaryTraversalIndex { get; init; }
        public int ScopedTraversalIndex { get; init; }
        public int RetainedSegmentCount { get; init; }
        public int AvailableSegmentCount { get; init; }
        public int RetiredGenerationCount { get; init; }
        public int RetiredSegmentCount { get; init; }
        public long RetiredBytes { get; init; }
        public int QuarantinedGenerationCount { get; init; }
        public int QuarantinedSegmentCount { get; init; }
        public bool CurrentGenerationQuarantined { get; init; }
        public bool HistoryOverflowed { get; init; }
        public NativeOwnerModel Model { get; init; }
        public long OwnerId { get; init; }
        public long OutstandingNativeBytes { get; init; }
        public long DetachedNativeBytes { get; init; }
        public long PeakOutstandingNativeBytes { get; init; }
        public long InitializedPayloadBytes { get; init; }
        public long PeakInitializedPayloadBytes { get; init; }
        internal static Structural From(Statistics expected, long epoch) => new()
        {
            Lifecycle = expected.Lifecycle,
            Generation = expected.Generation,
            MetricsEpoch = epoch,
            OrdinaryTraversalIndex = -1,
            ScopedTraversalIndex = -1,
            RetainedSegmentCount = expected.SegmentCount,
            AvailableSegmentCount = expected.AvailableSegmentCount,
            RetiredSegmentCount = expected.RetiredSegmentCount,
            RetiredBytes = expected.RetiredBytes,
            HistoryOverflowed = expected.HistoryOverflowed,
            Model = expected.Model,
            OwnerId = expected.OwnerId,
            OutstandingNativeBytes = expected.OutstandingNativeBytes,
            DetachedNativeBytes = expected.DetachedNativeBytes,
            PeakOutstandingNativeBytes = expected.PeakOutstandingNativeBytes,
            InitializedPayloadBytes = expected.InitializedPayloadBytes,
            PeakInitializedPayloadBytes = expected.PeakInitializedPayloadBytes
        };
    }
}
