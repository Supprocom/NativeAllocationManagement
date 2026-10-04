using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeCopyAccountingTests
{
    private static readonly int[] Input = [17, 19, 23, 29];
    private static readonly int[] OverlappedOutput = [1, 1, 2, 3];

    [Fact]
    public void BorrowedAppendCountsCompletedCopyEvenWhenItsCallbackFails()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativeBuilder<int> completed = new(preLease: 4);
            completed.Borrow(static (scoped ref NativeBuilderBorrow<int> borrow) => borrow.Append(Input.AsSpan()));
            Assert.Equal(16, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            using NativeTransfer<int> transfer = completed.Complete();
            Assert.Equal(29, transfer.Read(static view => view[3]));
            using NativeBuilder<int> failed = new(preLease: 4);
            Assert.Throws<InvalidOperationException>(() => failed.Borrow(static (scoped ref NativeBuilderBorrow<int> borrow) =>
            {
                borrow.Append(Input.AsSpan());
                throw new InvalidOperationException("Borrow failed after completed copy.");
            }));
            Assert.Equal(32, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            Assert.Equal(1, NativeMemoryDiagnostics.Snapshot().FreeCount);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void BuilderBulkAppendAndBoundedExportCountRealCopiesNotOpaqueRealloc()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativeBuilder<int> builder = new(preLease: 1);
            builder.Append(Input.AsSpan(0, 3));
            builder.Append(29);
            Assert.Equal(12, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            Assert.True(builder.TryEnsureCapacity(128));
            Assert.Equal(12, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            using NativeTransfer<int> transfer = builder.Complete();
            int[] exported = new int[4];
            transfer.Access(view => view.CopyTo(exported));
            Assert.Equal(Input, exported);
            Assert.Equal(28, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void PreparedBulkInitializationAndExportAllocateNothingAndCountOnce()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeMemoryBudget budget = new(512);
            using NativePool<int> pool = new(new NativePoolPreparation(1, 4, 1), budget);
            NativeLeaseInitializer<int> initialize = static writer => writer.Write(Input.AsSpan());
            int[] exported = new int[4];
            NativeLeaseAction<int> export = view => view.CopyTo(exported);
            Assert.True(pool.TryRent(4, initialize, out Pooled<int> warm, out _));
            warm.Access(export);
            warm.Dispose();
            long beforeCopies = NativeMemoryDiagnostics.Snapshot().CopiedBytes;
            long beforeAllocations = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 1_000; iteration++)
            {
                if (!pool.TryRent(4, initialize, out Pooled<int> value, out _))
                    throw new InvalidOperationException("Prepared copy fixture exhausted.");
                value.Access(export);
                value.Dispose();
            }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - beforeAllocations);
            Assert.Equal(32_000, NativeMemoryDiagnostics.Snapshot().CopiedBytes - beforeCopies);
            Assert.Equal(Input, exported);
            Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void SameStartCopyAndEmptyCopyDoNotInventTransferredBytes()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativePool<int> pool = new(new NativePoolPreparation(1, 4, 1), new NativeMemoryBudget(512));
            Assert.True(pool.TryRent(4, static writer => writer.Fill(42), out Pooled<int> value, out _));
            using (value)
            {
                value.Access(static view =>
                {
                    view.CopyFrom(view.AsSpan());
                    view.CopyTo(view.AsSpan());
                });
                Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
                Assert.Equal(42, value.Read(static view => view[3]));
            }
            using NativePool<int> emptyPool = new(0, 0, NativeMemoryReturn.ToNativeMemory);
            using Pooled<int> empty = emptyPool.Rent(0, static writer => writer.Write(ReadOnlySpan<int>.Empty));
            empty.Access(static view =>
            {
                view.CopyFrom(ReadOnlySpan<int>.Empty);
                view.CopyTo(Span<int>.Empty);
            });
            Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void OverlappingDistinctRangesCountTheirActualMemmoveLength()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            int[] data = [1, 2, 3, 4];
            ReadOnlySpan<int> source = data.AsSpan(0, 3);
            Span<int> target = data.AsSpan(1, 3);
            source.CopyTo(target);
            NativeMemoryAccounting.RecordCopiedRange(source, target);
            Assert.Equal(OverlappedOutput, data);
            Assert.Equal(12, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void CompletedCopiesRemainHistoricalWhenInitializationFails()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativePool<int> pool = new(0, 0, NativeMemoryReturn.ToNativeMemory);
            Assert.Throws<InvalidOperationException>(() => pool.Rent(4, static writer =>
            {
                writer.Write(Input.AsSpan(0, 2));
                throw new InvalidOperationException("Producer failed after an actual copy.");
            }));
            Assert.Equal(8, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            Assert.Equal(0, pool.CaptureDiagnosticSnapshot().ActiveRecords);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void CopyHistorySaturatesAfterSuccessfulMovementWithoutAffectingOutput()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeMemoryAccounting.PrepareThread();
            object metrics = typeof(NativeMemoryAccounting).GetField("_threadHotMetrics", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            metrics.GetType().GetField("CopiedBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(metrics, long.MaxValue);
            using NativePool<int> pool = new(new NativePoolPreparation(1, 4, 1), new NativeMemoryBudget(512));
            Assert.True(pool.TryRent(4, static writer => writer.Write(Input.AsSpan()), out Pooled<int> value, out _));
            using (value)
            {
                Assert.Equal(29, value.Read(static view => view[3]));
                Assert.Equal(long.MaxValue, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
                Assert.True(NativeMemoryDiagnostics.Snapshot().HistoryOverflowed);
            }
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void AllocationBackedCopyInAndCopyOutCountTheirRangesExactlyOnce()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativeConcurrentPool<int> pool = new(8, NativeMemoryReturn.ToNativeMemory);
            using ConcurrentPooled<int> value = pool.Rent(4, static writer => writer.Write(Input.AsSpan()));
            Assert.Equal(16, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            int[] exported = new int[4];
            value.Access(view => view.CopyTo(exported));
            Assert.Equal(Input, exported);
            Assert.Equal(32, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            value.Access(static view => view.CopyFrom(Input.AsSpan()));
            Assert.Equal(48, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            value.Access(static view => view.CopyFrom(view.AsSpan()));
            value.Access(static view => view.CopyTo(view.AsSpan()));
            Assert.Equal(48, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            int processed = value.Process(2, exported, static (view, destination) =>
            {
                Span<int> span = view.AsSpan();
                span.Fill(42);
                span.CopyTo(destination);
                return span[1];
            });
            Assert.Equal(42, processed);
            Assert.Equal(48, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void InitializerRangeOverwriteCountsOnlyItsActualCopiedPrefix()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativePool<int> pool = new(0, 0, NativeMemoryReturn.ToNativeMemory);
            using Pooled<int> value = pool.Rent(4, static writer =>
            {
                writer.Write(Input.AsSpan());
                writer.WriteRangeAt(1, Input.AsSpan(0, 2));
            });
            Assert.Equal(24, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            Assert.Equal(17, value.Read(static view => view[1]));
            Assert.Equal(19, value.Read(static view => view[2]));
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void InvalidSourceAndShortDestinationDoNotReceiveCopyCredit()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativePool<int> pool = new(0, 0, NativeMemoryReturn.ToNativeMemory);
            using Pooled<int> value = pool.Rent(4, static writer => writer.Fill(42));
            bool rejectedSource = false;
            try { value.CopyFrom(Input.AsSpan(0, 2)); }
            catch (ArgumentException) { rejectedSource = true; }
            bool rejectedDestination = false;
            try { value.CopyTo(Span<int>.Empty); }
            catch (ArgumentException) { rejectedDestination = true; }
            Assert.True(rejectedSource);
            Assert.True(rejectedDestination);
            Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            Assert.Equal(42, value.Read(static view => view[3]));
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void ManagedRootAssignmentDoesNotPretendToCopyUnmanagedPayloadBytes()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            using NativeConcurrentPool<string> pool = new(0, NativeMemoryReturn.ToNativeMemory);
            using ConcurrentPooled<string> value = pool.Rent(2, static writer =>
            {
                writer.Write("first");
                writer.Write("second");
            });
            string[] exported = new string[2];
            value.Access(view => view.CopyTo(exported));
            value.Access(view => view.CopyFrom(exported));
            Assert.Equal("second", value.Read(static view => view[1]));
            Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            Assert.Equal(2, pool.CaptureDiagnosticSnapshot().ReferenceRoots);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void SequentialBulkWriteCountsOnlyTheRangeAndResetsWithTheEpoch()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            long epoch = NativeMemoryDiagnostics.Snapshot().MetricsEpoch;
            using (NativePool<int> pool = new(0, 0, NativeMemoryReturn.ToNativeMemory))
            {
                using Pooled<int> value = pool.Rent(4, static writer =>
                {
                    NativeSequentialLeaseWriter<int> range = writer.BeginSequentialRange(0, 4);
                    range.Write(Input.AsSpan());
                    range.Complete();
                });
                Assert.Equal(29, value.Read(static view => view[3]));
                Assert.Equal(16, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            }
            NativeMemoryTestHooks.Reset();
            NativeMemoryStatistics reset = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(epoch + 1, reset.MetricsEpoch);
            Assert.Equal(0, reset.CopiedBytes);
            Assert.False(reset.HistoryOverflowed);
            using NativePool<int> nextPool = new(0, 0, NativeMemoryReturn.ToNativeMemory);
            using Pooled<int> next = nextPool.Rent(4, static writer => writer.Write(Input.AsSpan()));
            Assert.Equal(16, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
            Assert.Equal(17, next.Read(static view => view[0]));
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }
}
