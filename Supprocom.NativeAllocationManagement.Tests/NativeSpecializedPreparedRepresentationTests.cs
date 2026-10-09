using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Supprocom.NativeAllocationManagement.Conformance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeSpecializedPreparedRepresentationTests
{
    [Theory]
    [InlineData(7, 1, 3)]
    [InlineData(7, 3, 3)]
    [InlineData(7, 16, 3)]
    [InlineData(5, 1001, 2)]
    [InlineData(1, 1, 64)]
    [InlineData(64, 4096, 16)]
    public void CompactMetadataHasCompleteDefaultsAndOneDescendingChain(int slotCount, int capacity, int slotsPerPage)
    {
        NativeMemoryBudget budget = new(checked((long)slotCount * capacity));
        using NativePreparedPool<byte> pool = new(new NativePoolPreparation(slotCount, capacity, slotsPerPage), budget);
        Array slots = (Array)Field(pool, "_slabs");
        Assert.Equal(slotCount, slots.Length);
        Assert.Equal(slotCount - 1, (int)Field(pool, "_freeHead"));
        Assert.Null(pool.GetType().GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic));
        foreach (string removed in new[] { "_freeHeads", "_nonEmptyFreeClasses", "_prepared", "_returnedSlabIndex" })
            Assert.Null(pool.GetType().GetField(removed, BindingFlags.Instance | BindingFlags.NonPublic));
        for (int index = 0; index < slots.Length; index++)
        {
            object slot = slots.GetValue(index)!;
            Assert.NotEqual(IntPtr.Zero, (IntPtr)Field(slot, "Pointer"));
            Assert.Equal(index - 1, (int)Field(slot, "Next"));
            Assert.Null(slot.GetType().GetField("State", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.Equal(0L, (long)Field(slot, "Token"));
            Assert.Equal(0, (int)Field(slot, "BorrowCount"));
            Assert.Equal(4, slot.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Length);
        }
        Type slotType = slots.GetType().GetElementType()!;
        Type pageType = ((Array)Field(pool, "_pages")).GetType().GetElementType()!;
        if (IntPtr.Size == 8)
        {
            MethodInfo size = typeof(NativeSpecializedPreparedRepresentationTests).GetMethod(nameof(ElementSize), BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.Equal(24, (int)size.MakeGenericMethod(slotType).Invoke(null, null)!);
            Assert.Equal(40, (int)size.MakeGenericMethod(pageType).Invoke(null, null)!);
            Assert.Equal((IntPtr)0, Marshal.OffsetOf(slotType, "Pointer"));
            Assert.Equal((IntPtr)8, Marshal.OffsetOf(slotType, "Token"));
            Assert.Equal((IntPtr)16, Marshal.OffsetOf(slotType, "Next"));
            Assert.Equal((IntPtr)20, Marshal.OffsetOf(slotType, "BorrowCount"));
            Assert.Equal(slotCount * 24L + (1 + (slotCount - 1) / slotsPerPage) * 40L, pool.CapturePreparedSnapshot().ManagedBankBytes);
        }
        HoldAll(pool, slotCount, capacity);
        Assert.Equal(slotCount, pool.CapturePreparedSnapshot().AvailableSlotCount);
        Assert.Equal(slotCount, pool.CapturePreparedSnapshot().SuccessfulRentCount);
        pool.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(budget.CaptureStatistics().AllocationCount, budget.CaptureStatistics().FreeCount);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    public void EveryPublicationFailureReclaimsItsCommittedPrefix(int boundary, long acquiredPages)
    {
        NativeMemoryBudget budget = new(91, traceCapacity: 32);
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(boundary);
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
        {
            using NativePreparedPool<byte> pool = new(new NativePoolPreparation(7, 13, 3), budget);
        });
        Assert.Contains("Injected managed publication failure during NativePreparedPool.Preparation", failure.Message, StringComparison.Ordinal);
        NativeMemoryBudgetStatistics snapshot = budget.CaptureStatistics();
        Assert.Equal(acquiredPages, snapshot.AllocationCount);
        Assert.Equal(acquiredPages, snapshot.FreeCount);
        Assert.Equal(0, snapshot.CommittedBytes);
        Assert.Equal(0, snapshot.ReservedBytes);
        Assert.Equal(0, snapshot.ActiveAllocationCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void AllOwnerFieldsFollowActualFreeListAndWholePageTransitions(int traceCapacity)
    {
        long epoch = NativeMemoryDiagnostics.Snapshot().MetricsEpoch;
        NativeMemoryBudget budget = new(32, traceCapacity);
        NativePreparedPool<int> pool = new(new NativePoolPreparation(4, 2, 2), budget);
        NativeOwnerDiagnosticOracle.Statistics expected = NativeOwnerDiagnosticOracle.Statistics.Active(
            pool.Id, NativeOwnerModel.ThreadConfinedPool, 32, 32, 2) with
        { SegmentCount = 2, AvailableSegmentCount = 2 };
        try
        {
            VerifyOwner(pool, expected, epoch, head: 3, records: 0);
            using (PreparedPooled<int> first = pool.Rent(1, static writer => writer.Write(7)))
            {
                expected = expected with
                { RequestedBytes = 4, InitializedPayloadBytes = 4, PeakInitializedPayloadBytes = 4, AvailableSegmentCount = 1 };
                VerifyOwner(pool, expected, epoch, head: 2, records: 1);
                first.Access(_ =>
                {
                    VerifyOwner(pool, expected, epoch, head: 2, records: 1);
                    Assert.Equal((nuint)16, pool.TrimRetainedMemory());
                });
                expected = expected with
                {
                    RetainedBytes = 16,
                    OutstandingNativeBytes = 16,
                    UsableCapacityBytes = 16,
                    SegmentCount = 1,
                    AvailableSegmentCount = 0,
                    TrimCallCount = 1,
                    TrimmedBytes = 16
                };
                VerifyOwner(pool, expected, epoch, head: 2, records: 1);
                using PreparedPooled<int> second = pool.Rent(2, static writer => writer.Fill(11));
                expected = expected with { RequestedBytes = 12, InitializedPayloadBytes = 12, PeakInitializedPayloadBytes = 12 };
                VerifyOwner(pool, expected, epoch, head: -1, records: 2);
                Assert.Equal(7, first.Read(static view => view[0]));
                Assert.Equal(11, second.Read(static view => view[1]));
            }
            expected = expected with { RequestedBytes = 0, InitializedPayloadBytes = 0, AvailableSegmentCount = 1 };
            VerifyOwner(pool, expected, epoch, head: 3, records: 0);
            Assert.Equal((nuint)16, pool.TrimRetainedMemory());
            expected = expected with
            {
                RetainedBytes = 0,
                OutstandingNativeBytes = 0,
                UsableCapacityBytes = 0,
                SegmentCount = 0,
                AvailableSegmentCount = 0,
                TrimCallCount = 2,
                TrimmedBytes = 32
            };
            VerifyOwner(pool, expected, epoch, head: -1, records: 0);
        }
        finally { pool.Dispose(); }
        NativeOwnerDiagnosticOracle.Verify(pool.CaptureDiagnosticSnapshot(),
            NativeOwnerDiagnosticOracle.Structural.From(expected with { Lifecycle = NativeOwnerLifecycle.Disposed }, epoch),
            "closed-specialized-prepared-pool");
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(2, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(2, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void RetireRequiresNoInitializingLiveOrBorrowedSlotAndCoordinatorReleasesExactlyOnce()
    {
        NativeMemoryBudget budget = new(16);
        NativePreparedPool<int>? pendingDispose = new(new NativePoolPreparation(2, 2, 2), budget);
        NativePreparedPool<int> pool = pendingDispose;
        try
        {
            using (PreparedPooled<int> lease = pool.Rent(2, writer =>
            {
                Assert.Throws<InvalidOperationException>(pool.Retire);
                Assert.Throws<InvalidOperationException>(pool.Dispose);
                writer.Fill(42);
            }))
            {
                Assert.Throws<InvalidOperationException>(pool.Retire);
                lease.Access(_ => Assert.Throws<InvalidOperationException>(pool.Retire));
            }
            pool.Retire();
            // Retirement transfers backing cleanup to ReleaseRetiredStorage;
            // ordinary Dispose must no longer be called on this owner.
            pendingDispose = null;
            Assert.Equal(NativeOwnerLifecycle.Returned, pool.CaptureDiagnosticSnapshot().Lifecycle);
            Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
            Exception? failure = null;
            Thread coordinator = new(() =>
            {
                try { pool.ReleaseRetiredStorage(); }
                catch (InvalidOperationException exception) { failure = exception; }
            });
            coordinator.Start();
            Assert.True(coordinator.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
            Assert.Equal(NativeOwnerLifecycle.Disposed, pool.CaptureDiagnosticSnapshot().Lifecycle);
            Assert.Equal(0, pool.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
            Assert.Equal(0, pool.CapturePreparedSnapshot().RetainedSlotCount);
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(1, budget.CaptureStatistics().FreeCount);
            Assert.Throws<InvalidOperationException>(pool.ReleaseRetiredStorage);
            Assert.Throws<InvalidOperationException>(pool.Dispose);
        }
        finally
        {
            pendingDispose?.Dispose();
            if (pool.CurrentLifecycle == NativeOwnerLifecycle.Returned) pool.ReleaseRetiredStorage();
        }
    }

    private static void VerifyOwner(NativePreparedPool<int> pool, NativeOwnerDiagnosticOracle.Statistics expected,
        long epoch, int head, int records) =>
        NativeOwnerDiagnosticOracle.VerifyBoth(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot(), expected,
            NativeOwnerDiagnosticOracle.Structural.From(expected, epoch) with
            { OrdinaryTraversalIndex = head, ActiveRecords = records });

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static int ElementSize<T>() => Unsafe.SizeOf<T>();

    private static void HoldAll(NativePreparedPool<byte> pool, int remaining, int capacity)
    {
        using PreparedPooled<byte> lease = pool.Rent(capacity, static writer => writer.Fill(7));
        if (remaining > 1) HoldAll(pool, remaining - 1, capacity);
        else
        {
            Assert.Equal(0, pool.CapturePreparedSnapshot().AvailableSlotCount);
            Assert.False(pool.TryRent(capacity, static writer => writer.Fill(0), out _, out NativePoolExhaustionReason reason));
            Assert.Equal(NativePoolExhaustionReason.NoAvailableSlot, reason);
        }
        Assert.Equal(7, lease.Read(static values => values[0]));
    }
}
