namespace Supprocom.NativeAllocationManagement.Conformance;

// Public API only: this exact source is also compiled by an isolated package
// consumer. Baseline capture establishes the existing process epoch/history;
// all subsequent expectations come from independent extent/event arithmetic.
internal static class NativeProcessDiagnosticOracle
{
    internal static void Run()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Expected expected = Expected.FromBaseline(NativeMemoryDiagnostics.Snapshot());
        if (expected.HistoryOverflowed)
            throw new InvalidOperationException("The transition oracle requires unsaturated, quiescent histories.");
        Verify(expected, "baseline");
        using (NativeWorkspace<int> workspace = new(preLease: 4))
        {
            expected = expected.WithAcquisition(16) with
            {
                StorageClearCount = expected.StorageClearCount + 1,
                StorageClearBytes = expected.StorageClearBytes + 16,
                WrittenClearBytes = expected.WrittenClearBytes + 16
            };
            Verify(expected, "workspace:acquired-and-cleared");
            int initialSum = workspace.Process(2, static values =>
            {
                if (values[0] != 0 || values[1] != 0)
                    throw new InvalidOperationException("Workspace backing was not actually zero-initialized.");
            }, static values => values[0] + values[1]);
            if (initialSum != 0)
                throw new InvalidOperationException("Reading prepared workspace backing changed its contents.");
            Verify(expected, "workspace:process-adds-no-storage-or-history");
            workspace.Initialize(2, static writer => writer.Fill(17));
            workspace.Access(static view =>
            {
                view.Clear();
                view.Fill(19);
                view.AsSpan().Clear();
                if (view[0] != 0 || view[1] != 0)
                    throw new InvalidOperationException("Bounded clear did not produce the expected payload.");
            });
            expected = expected with
            {
                StorageClearCount = expected.StorageClearCount + 1,
                StorageClearBytes = expected.StorageClearBytes + 8,
                WrittenClearBytes = expected.WrittenClearBytes + 8
            };
            Verify(expected, "workspace:bounded-clear-excludes-arbitrary-consumer-clear");
            expected = RunBuilder(expected);
        }
        expected = expected.WithRelease(16);
        Verify(expected, "workspace:physically-released");
        Verify(expected, "repeated-snapshot:does-not-reset");
    }

    private static Expected RunBuilder(Expected expected)
    {
        using (NativeBuilder<int> builder = new(preLease: 2))
        {
            expected = expected.WithAcquisition(8);
            Verify(expected, "builder:acquired");
            builder.Append([3, 5]);
            expected = expected with { CopiedBytes = expected.CopiedBytes + 8 };
            Verify(expected, "builder:copied-eight-bytes");
            if (!builder.TryEnsureCapacity(3) || builder.Capacity != 4 || builder.Count != 2)
                throw new InvalidOperationException("Builder did not retain the exact prefix across growth.");
            expected = expected with
            {
                OutstandingNativeBytes = expected.OutstandingNativeBytes + 8,
                PeakOutstandingNativeBytes = Math.Max(expected.PeakOutstandingNativeBytes, expected.OutstandingNativeBytes + 8),
                ReallocationCount = expected.ReallocationCount + 1
            };
            Verify(expected, "builder:realloc-not-a-copy-or-allocate-free-pair");
            int[] output = new int[2];
            using NativeTransfer<int> transfer = builder.Complete();
            transfer.Access(view => view.CopyTo(output));
            if (output[0] != 3 || output[1] != 5)
                throw new InvalidOperationException("Builder growth or bounded export changed the payload.");
            expected = expected with { CopiedBytes = expected.CopiedBytes + 8 };
            Verify(expected, "transfer:exported-eight-bytes-without-new-backing");
        }
        expected = expected.WithRelease(16);
        Verify(expected, "transfer:physically-released");
        return expected;
    }

    internal static void Verify(Expected expected, string stage)
    {
        NativeMemoryStatistics actual = NativeMemoryDiagnostics.Snapshot();
        Check(nameof(actual.OutstandingNativeBytes), actual.OutstandingNativeBytes, expected.OutstandingNativeBytes, stage);
        Check(nameof(actual.PeakOutstandingNativeBytes), actual.PeakOutstandingNativeBytes, expected.PeakOutstandingNativeBytes, stage);
        Check(nameof(actual.DetachedNativeBytes), actual.DetachedNativeBytes, expected.DetachedNativeBytes, stage);
        Check(nameof(actual.RetiredNativeBytes), actual.RetiredNativeBytes, expected.RetiredNativeBytes, stage);
        Check(nameof(actual.RetainedNativeBytes), actual.RetainedNativeBytes, expected.RetainedNativeBytes, stage);
        Check(nameof(actual.AllocationCount), actual.AllocationCount, expected.AllocationCount, stage);
        Check(nameof(actual.ReallocationCount), actual.ReallocationCount, expected.ReallocationCount, stage);
        Check(nameof(actual.FreeCount), actual.FreeCount, expected.FreeCount, stage);
        Check(nameof(actual.MetricsEpoch), actual.MetricsEpoch, expected.MetricsEpoch, stage);
        Check(nameof(actual.HistoryOverflowed), actual.HistoryOverflowed, expected.HistoryOverflowed, stage);
        Check(nameof(actual.ZeroedAllocationCount), actual.ZeroedAllocationCount, expected.ZeroedAllocationCount, stage);
        Check(nameof(actual.DetachedGenerationCount), actual.DetachedGenerationCount, expected.DetachedGenerationCount, stage);
        Check(nameof(actual.BumpTraversalVisitCount), actual.BumpTraversalVisitCount, expected.BumpTraversalVisitCount, stage);
        Check(nameof(actual.ReusedNativeSegmentCount), actual.ReusedNativeSegmentCount, expected.ReusedNativeSegmentCount, stage);
        Check(nameof(actual.ReclaimedRangeReuseCount), actual.ReclaimedRangeReuseCount, expected.ReclaimedRangeReuseCount, stage);
        Check(nameof(actual.ReclaimedRangeReuseBytes), actual.ReclaimedRangeReuseBytes, expected.ReclaimedRangeReuseBytes, stage);
        Check(nameof(actual.StorageClearCount), actual.StorageClearCount, expected.StorageClearCount, stage);
        Check(nameof(actual.StorageClearBytes), actual.StorageClearBytes, expected.StorageClearBytes, stage);
        Check(nameof(actual.WrittenClearBytes), actual.WrittenClearBytes, expected.WrittenClearBytes, stage);
        Check(nameof(actual.CopiedBytes), actual.CopiedBytes, expected.CopiedBytes, stage);
    }

    private static void Check(string field, long actual, long expected, string stage)
    {
        if (actual != expected)
            throw new InvalidOperationException($"{stage}: {field} was {actual}, expected {expected}.");
    }

    private static void Check(string field, bool actual, bool expected, string stage)
    {
        if (actual != expected)
            throw new InvalidOperationException($"{stage}: {field} was {actual}, expected {expected}.");
    }

    internal readonly record struct Expected(
        long OutstandingNativeBytes, long PeakOutstandingNativeBytes,
        long DetachedNativeBytes, long RetiredNativeBytes, long ReusedNativeSegmentCount,
        long ReclaimedRangeReuseCount, long ReclaimedRangeReuseBytes,
        long AllocationCount, long ReallocationCount, long FreeCount,
        long MetricsEpoch, bool HistoryOverflowed, long ZeroedAllocationCount,
        long DetachedGenerationCount, long BumpTraversalVisitCount,
        long StorageClearCount, long StorageClearBytes, long WrittenClearBytes, long CopiedBytes)
    {
        public long RetainedNativeBytes => OutstandingNativeBytes - DetachedNativeBytes;

        internal static Expected FromBaseline(NativeMemoryStatistics before) => new(
            before.OutstandingNativeBytes, before.PeakOutstandingNativeBytes,
            before.DetachedNativeBytes, before.RetiredNativeBytes, before.ReusedNativeSegmentCount,
            before.ReclaimedRangeReuseCount, before.ReclaimedRangeReuseBytes,
            before.AllocationCount, before.ReallocationCount, before.FreeCount,
            before.MetricsEpoch, before.HistoryOverflowed, before.ZeroedAllocationCount,
            before.DetachedGenerationCount, before.BumpTraversalVisitCount,
            before.StorageClearCount, before.StorageClearBytes, before.WrittenClearBytes, before.CopiedBytes);

        internal Expected WithAcquisition(long bytes) => this with
        {
            OutstandingNativeBytes = OutstandingNativeBytes + bytes,
            PeakOutstandingNativeBytes = Math.Max(PeakOutstandingNativeBytes, OutstandingNativeBytes + bytes),
            AllocationCount = AllocationCount + 1
        };

        internal Expected WithRelease(long bytes) => this with
        { OutstandingNativeBytes = OutstandingNativeBytes - bytes, FreeCount = FreeCount + 1 };
    }
}
