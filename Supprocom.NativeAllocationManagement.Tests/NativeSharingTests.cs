using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeSharingTests
{
    [Fact]
    public void ConversionConsumesUniqueAuthorityWithoutCopyingOrDoubleCharging()
    {
        NativeMemoryBudget budget = new(64);
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(42);
        NativeTransfer<int>? source = builder.Complete();
        NativeTransfer<int> stale = source.Value;
        long copies = NativeMemoryDiagnostics.Snapshot().CopiedBytes;
        NativeShared<int> shared = NativeShared<int>.Create(ref source, new(2, 1));
        Assert.Null(source);
        Assert.Throws<InvalidOperationException>(() => stale.Read(static view => view[0]));
        Assert.Throws<InvalidOperationException>(stale.Dispose);
        Assert.Equal(42, shared.Read(static view => view[0]));
        NativeSharingStatistics snapshot = shared.CaptureSnapshot();
        Assert.Equal(stale.Id, snapshot.OwnerId);
        Assert.NotEqual(snapshot.Id, snapshot.OwnerId);
        Assert.Equal(16, snapshot.OwnedBackingBytes);
        Assert.Equal(4, snapshot.InitializedPayloadBytes);
        Assert.Equal(1, snapshot.StrongBindingCount);
        Assert.Equal(0, snapshot.BorrowedBackingBytes);
        Assert.Equal(copies, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
        Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
        shared.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, shared.CaptureSnapshot().PayloadReturnCount);
        Assert.True(shared.TryCompletePayloadReturn());
        GC.KeepAlive(stale);
    }

    [Fact]
    public void SlotExhaustionDefaultsOutputsAndAliasesCannotReleaseReusedSlots()
    {
        NativeMemoryBudget budget = new(64);
        NativeShared<int> owner = Create(budget, 4, 2, 1);
        Assert.True(owner.TryShare(out NativeShared<int> share, out NativeSharingExhaustionReason reason));
        Assert.Equal(NativeSharingExhaustionReason.None, reason);
        NativeShared<int> stale = share;
        Assert.False(owner.TryShare(out NativeShared<int> refused, out reason));
        Assert.Equal(NativeSharingExhaustionReason.NoStrongBinding, reason);
        Assert.Throws<NativeAllocationUninitializedException>(refused.Dispose);
        share.Dispose();
        Assert.True(owner.TryShare(out NativeShared<int> replacement, out _));
        Assert.Throws<InvalidOperationException>(stale.Dispose);
        Assert.Throws<InvalidOperationException>(() => stale.Read(static view => view[0]));
        Assert.Equal(42, replacement.Read(static view => view[0]));
        Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
        NativeWeak<int> staleWeak = weak;
        Assert.False(owner.TryDowngrade(out NativeWeak<int> refusedWeak, out reason));
        Assert.Equal(NativeSharingExhaustionReason.NoWeakBinding, reason);
        Assert.Throws<NativeAllocationUninitializedException>(refusedWeak.Dispose);
        weak.Dispose();
        Assert.True(owner.TryDowngrade(out NativeWeak<int> replacementWeak, out _));
        Assert.Throws<InvalidOperationException>(staleWeak.Dispose);
        Assert.Throws<InvalidOperationException>(() => staleWeak.TryUpgrade(out _, out _));
        replacementWeak.Dispose();
        replacement.Dispose();
        NativeSharingStatistics live = owner.CaptureSnapshot();
        Assert.Equal(2, live.ShareCount);
        Assert.Equal(2, live.WeakCreationCount);
        Assert.Equal(1, live.RejectedStrongCount);
        Assert.Equal(1, live.RejectedWeakCount);
        Assert.Equal(2, live.PeakStrongBindingCount);
        Assert.Equal(1, live.PeakWeakBindingCount);
        owner.Dispose();
        Assert.Throws<InvalidOperationException>(owner.Dispose);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void LastStrongReleaseClosesUpgradeBeforeAnEnteredReadEnds()
    {
        NativeMemoryBudget budget = new(64);
        NativeShared<int> owner = Create(budget, 4, 2, 1);
        Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
        owner.Access(view =>
        {
            owner.Dispose();
            Assert.True(weak.IsExpired);
            Assert.False(weak.TryUpgrade(out _, out NativeSharingExhaustionReason reason));
            Assert.Equal(NativeSharingExhaustionReason.ExpiredPayload, reason);
            Assert.False(weak.TryCompletePayloadReturn());
            NativeSharingStatistics entered = weak.CaptureSnapshot();
            Assert.Equal(0, entered.StrongBindingCount);
            Assert.Equal(1, entered.ActiveReadCount);
            Assert.False(entered.PayloadReleased);
            Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(42, view[0]);
        });
        NativeSharingStatistics returned = weak.CaptureSnapshot();
        Assert.True(returned.PayloadReleased);
        Assert.Equal(0, returned.ActiveReadCount);
        Assert.Equal(0, returned.OwnedBackingBytes);
        Assert.Equal(0, returned.InitializedPayloadBytes);
        Assert.Equal(1, returned.ExpiredUpgradeCount);
        Assert.Equal(1, returned.PayloadReturnCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        weak.Dispose();
    }

    [Fact]
    public void WeakUpgradeAcquiresIndependentAuthorityAndNeverResurrectsPayload()
    {
        NativeMemoryBudget budget = new(64);
        NativeShared<int> owner = Create(budget, 4, 2, 1);
        Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
        Assert.True(weak.TryUpgrade(out NativeShared<int> upgraded, out _));
        Assert.False(weak.TryUpgrade(out _, out NativeSharingExhaustionReason reason));
        Assert.Equal(NativeSharingExhaustionReason.NoStrongBinding, reason);
        owner.Dispose();
        Assert.Equal(42, upgraded.Read(static view => view[0]));
        Assert.False(weak.IsExpired);
        upgraded.Dispose();
        Assert.True(weak.IsExpired);
        Assert.False(weak.TryUpgrade(out _, out reason));
        Assert.Equal(NativeSharingExhaustionReason.ExpiredPayload, reason);
        Assert.Equal(1, weak.CaptureSnapshot().SuccessfulUpgradeCount);
        Assert.Equal(1, weak.CaptureSnapshot().RejectedStrongCount);
        weak.Dispose();
    }

    [Fact]
    public void SmallSliceRetainsFullBackingAndDetachAdmitsTemporaryOverlap()
    {
        NativeMemoryBudget budget = new(4_100);
        NativeShared<int> owner = Create(budget, 1_024, 2, 0);
        Assert.True(owner.TrySlice(0, 1, out NativeShared<int> slice, out _));
        owner.Dispose();
        Assert.Equal(1, slice.Length);
        Assert.Equal(4_096, slice.CaptureSnapshot().OwnedBackingBytes);
        long copied = NativeMemoryDiagnostics.Snapshot().CopiedBytes;
        Assert.True(slice.TryDetach(budget, out NativeTransfer<int> detached));
        Assert.Equal(4_100, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(42, detached.Read(static view => view[0]));
        Assert.Equal(copied + 4, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
        Assert.Equal(1, slice.CaptureSnapshot().DetachCount);
        slice.Dispose();
        Assert.Equal(4, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(42, detached.Read(static view => view[0]));
        detached.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(4_100, budget.CaptureStatistics().PeakAdmittedBytes);
    }

    [Fact]
    public void DetachRefusalAndBackendFailurePreserveSourceWithoutCopying()
    {
        NativeMemoryBudget budget = new(16);
        NativeShared<int> owner = Create(budget, 4, 1, 0);
        long copied = NativeMemoryDiagnostics.Snapshot().CopiedBytes;
        Assert.False(owner.TryDetach(budget, out NativeTransfer<int> refused));
        Assert.Throws<NativeAllocationUninitializedException>(refused.Dispose);
        Assert.Equal(1, owner.CaptureSnapshot().RejectedDetachCount);
        Assert.Equal(copied, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
        Assert.Equal(42, owner.Read(static view => view[0]));
        NativeMemoryBudget destination = new(4);
        NativeMemoryTestHooks.FailNextAllocation();
        Assert.Throws<NativeAllocationFailedException>(() => owner.TryDetach(destination, out _));
        Assert.Equal(0, destination.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, destination.CaptureStatistics().ReservedBytes);
        Assert.Equal(1, destination.CaptureStatistics().FailedAllocationCount);
        Assert.Equal(0, owner.CaptureSnapshot().DetachCount);
        Assert.Equal(copied, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
        Assert.Equal(42, owner.Read(static view => view[0]));
        owner.Dispose();
    }

    [Fact]
    public void EmptyPublicationAndDetachHaveNoInventedBackingOrCopyEvents()
    {
        NativeMemoryBudget budget = new(0);
        using NativeBuilder<int> builder = new(budget, 0);
        NativeTransfer<int>? source = builder.Complete();
        NativeShared<int> owner = NativeShared<int>.Create(ref source, new(1, 0));
        long copied = NativeMemoryDiagnostics.Snapshot().CopiedBytes;
        Assert.Equal(0, owner.Read(static view => view.Length));
        Assert.True(owner.TryDetach(budget, out NativeTransfer<int> detached));
        Assert.Equal(0, detached.Length);
        detached.Dispose();
        owner.Dispose();
        Assert.Equal(copied, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(1, owner.CaptureSnapshot().PayloadReturnCount);
    }

    [Fact]
    public void InvalidPreparationAndPreMoveFailurePreserveUniqueSource()
    {
        NativeMemoryBudget budget = new(16);
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(42);
        NativeTransfer<int>? source = builder.Complete();
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeShared<int>.Create(ref source, default));
        Assert.True(source.HasValue);
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(1);
        Assert.Throws<InvalidOperationException>(() => NativeShared<int>.Create(ref source, new(1, 0)));
        Assert.Equal(42, source!.Value.Read(static view => view[0]));
        Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
        source.Value.Dispose();
    }

    [Fact]
    public void FailedPostMovePublicationConsumesSourceAndReturnsItsStorage()
    {
        NativeMemoryBudget budget = new(16);
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(42);
        NativeTransfer<int>? source = builder.Complete();
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(2);
        Assert.Throws<InvalidOperationException>(() => NativeShared<int>.Create(ref source, new(1, 0)));
        Assert.Null(source);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void FailedPayloadReturnRemainsChargedAndCanBeExplicitlyRetried()
    {
        NativeMemoryBudget budget = new(16);
        NativeShared<int> owner = Create(budget, 4, 1, 1);
        Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(3);
        Assert.Throws<InvalidOperationException>(owner.Dispose);
        NativeSharingStatistics failed = weak.CaptureSnapshot();
        Assert.True(failed.Expired);
        Assert.False(failed.PayloadReleased);
        Assert.Equal(1, failed.PayloadReturnFailureCount);
        Assert.Equal(0, failed.PayloadReturnCount);
        Assert.Equal(16, failed.OwnedBackingBytes);
        Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
        Assert.False(weak.TryUpgrade(out _, out _));
        Assert.True(weak.TryCompletePayloadReturn());
        Assert.True(owner.TryCompletePayloadReturn());
        Assert.Equal(1, weak.CaptureSnapshot().PayloadReturnCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        weak.Dispose();
        GC.KeepAlive(owner);
    }

    [Fact]
    public void PreparedSharingReadsSnapshotsAndRefusalsRequireNoManagedAllocation()
    {
        NativeMemoryBudget budget = new(16);
        NativeShared<int> owner = Create(budget, 4, 2, 1);
        Cycle(owner);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int sum = 0;
        for (int index = 0; index < 10_000; index++) sum += Cycle(owner);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(420_000, sum);
        Assert.Equal(0, allocated);
        Assert.Equal(10_001, owner.CaptureSnapshot().SuccessfulUpgradeCount);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        owner.Dispose();
    }

    private static int Cycle(NativeShared<int> owner)
    {
        if (!owner.TryShare(out NativeShared<int> share, out _)) throw new InvalidOperationException("Prepared share refused.");
        if (owner.TryShare(out _, out _)) throw new InvalidOperationException("Exhaustion was ignored.");
        share.Dispose();
        if (!owner.TryDowngrade(out NativeWeak<int> weak, out _)) throw new InvalidOperationException("Prepared observer refused.");
        if (!weak.TryUpgrade(out NativeShared<int> upgrade, out _)) throw new InvalidOperationException("Prepared upgrade refused.");
        int value = upgrade.Read(static view => view[0]);
        upgrade.Dispose();
        weak.Dispose();
        _ = owner.CaptureSnapshot();
        return value;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackAndLastReturnFailuresPreserveBothCausesAndPermitCleanup(bool resultCallback)
    {
        NativeMemoryBudget budget = new(16);
        NativeShared<int> owner = Create(budget, 4, 1, 1);
        Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
        FormatException cause = new("Deliberate consumer failure.");
        AggregateException failure = Assert.Throws<AggregateException>(() =>
        {
            if (resultCallback)
                owner.Read<int>(view =>
                {
                    Assert.Equal(42, view[0]);
                    owner.Dispose();
                    NativeMemoryTestHooks.FailAtManagedPublicationBoundary(3);
                    throw cause;
                });
            else
                owner.Access(view =>
                {
                    Assert.Equal(42, view[0]);
                    owner.Dispose();
                    NativeMemoryTestHooks.FailAtManagedPublicationBoundary(3);
                    throw cause;
                });
        });
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Same(cause, failure.InnerExceptions[0]);
        Assert.IsType<InvalidOperationException>(failure.InnerExceptions[1]);
        Assert.Equal(0, weak.CaptureSnapshot().ActiveReadCount);
        Assert.Equal(1, weak.CaptureSnapshot().PayloadReturnFailureCount);
        Assert.Equal(0, weak.CaptureSnapshot().PayloadReturnCount);
        Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
        Assert.True(weak.TryCompletePayloadReturn());
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        weak.Dispose();
        GC.KeepAlive(owner);
    }

    [Fact]
    public void EmergencyCleanupRetriesAFailedReturnWithoutInventingSuccess()
    {
        NativeMemoryBudget budget = new(2_000_000);
        (NativeWeak<byte> weak, WeakReference payload) = CreateAbandonedPayload(budget);
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(3);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        NativeSharingStatistics failed = weak.CaptureSnapshot();
        Assert.True(failed.Expired);
        Assert.False(failed.PayloadReleased);
        Assert.Equal(1, failed.PayloadReturnFailureCount);
        Assert.Equal(0, failed.PayloadReturnCount);
        Assert.Equal(1_000_000, budget.CaptureStatistics().CommittedBytes);
        Assert.False(payload.IsAlive);
        Assert.False(weak.TryUpgrade(out _, out _));
        for (int attempt = 0; attempt < 8 && !weak.CaptureSnapshot().PayloadReleased; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.True(weak.CaptureSnapshot().PayloadReleased);
        Assert.Equal(1, weak.CaptureSnapshot().PayloadReturnCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        weak.Dispose();
    }

    [Fact]
    public void WeakObservationClosesOwnershipWhenGcClearsPayloadBeforeItsFinalizerStarts()
    {
        NativeMemoryBudget budget = new(2_000_000);
        (NativeWeak<byte> weak, WeakReference payload) = CreateAbandonedPayload(budget);
        using ManualResetEventSlim finalizerEntered = new(false);
        using ManualResetEventSlim allowReturn = new(false);
        NativeMemoryTestHooks.SetBeforeOperationEntry(operation =>
        {
            if (!string.Equals(operation, "NativeShared.Finalize", StringComparison.Ordinal)) return;
            finalizerEntered.Set();
            if (!allowReturn.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Finalizer return was not released.");
        });
        try
        {
            GC.Collect();
            Assert.True(finalizerEntered.Wait(TimeSpan.FromSeconds(20)));
            Assert.False(payload.IsAlive);
            Assert.False(weak.TryUpgrade(out _, out NativeSharingExhaustionReason reason));
            Assert.Equal(NativeSharingExhaustionReason.ExpiredPayload, reason);
            Assert.True(weak.IsExpired);
            NativeSharingStatistics waiting = weak.CaptureSnapshot();
            Assert.True(waiting.Expired);
            Assert.Equal(0, waiting.StrongBindingCount);
            Assert.Equal(1, waiting.WeakBindingCount);
            Assert.False(waiting.PayloadReleased);
            Assert.Equal(1_000_000, budget.CaptureStatistics().CommittedBytes);
        }
        finally
        {
            allowReturn.Set();
            GC.WaitForPendingFinalizers();
            NativeMemoryTestHooks.SetBeforeOperationEntry(null);
        }
        Assert.True(weak.CaptureSnapshot().PayloadReleased);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        weak.Dispose();
    }

    [Fact]
    public void ReleasedControlMetadataIsCollectibleAfterAllBindingsEnd()
    {
        NativeMemoryBudget budget = new(16);
        WeakReference control = CreateReleasedControl(budget);
        for (int attempt = 0; attempt < 8 && control.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(control.IsAlive);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void WeakObservationDropsTheCompleteExpiredStrongBankRatherThanRetainingItsCapacity()
    {
        NativeMemoryBudget budget = new(16);
        NativeShared<int> owner = Create(budget, 4, 100_000, 1);
        Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
        long before = owner.CaptureSnapshot().ManagedBankBytes;
        Assert.True(before >= 1_600_016);
        owner.Dispose();
        NativeSharingStatistics expired = weak.CaptureSnapshot();
        Assert.Equal(0, expired.StrongBindingCount);
        Assert.Equal(16, expired.ManagedBankBytes);
        Assert.Equal(100_000, expired.Preparation.StrongBindingCount);
        Assert.Equal(1, expired.WeakBindingCount);
        Assert.False(weak.TryUpgrade(out _, out _));
        weak.Dispose();
        Assert.Equal(0, weak.CaptureSnapshot().ManagedBankBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void NoObserverPreparationHasNoPayloadWeakReferenceAndStillSupportsFailedReturnRetry()
    {
        NativeMemoryBudget budget = new(16);
        NativeShared<int> owner = Create(budget, 4, 2, 0);
        object payload = typeof(NativeShared<int>).GetField("_payload", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        object control = payload.GetType().GetProperty("Control", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(payload)!;
        Assert.Null(control.GetType().GetField("_payload", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control));
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(3);
        Assert.Throws<InvalidOperationException>(owner.Dispose);
        Assert.Equal(0, owner.CaptureSnapshot().ManagedBankBytes);
        Assert.True(owner.TryCompletePayloadReturn());
        Assert.Equal(1, owner.CaptureSnapshot().PayloadReturnCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        GC.KeepAlive(payload);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateReleasedControl(NativeMemoryBudget budget)
    {
        NativeShared<int> owner = Create(budget, 4, 1, 1);
        if (!owner.TryDowngrade(out NativeWeak<int> weak, out _)) throw new InvalidOperationException("Observer refused.");
        object control = typeof(NativeWeak<int>).GetField("_control", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(weak)!;
        owner.Dispose();
        weak.Dispose();
        return new(control);
    }

    [Fact]
    public void ConsumedMoveFailureDoesNotPublishOrFinalizeAFictitiousSharedPayload()
    {
        NativeMemoryBudget budget = new(16, 16);
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(42);
        NativeTransfer<int>? source = builder.Complete();
        NativeTransfer<int> entered = source.Value;
        entered.Access(view =>
        {
            Assert.Throws<InvalidOperationException>(() => NativeShared<int>.Create(ref source, new(1, 0)));
            Assert.Null(source);
            Assert.Equal(42, view[0]);
            Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
        });
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[16];
        int count = budget.CopyTraceTo(events);
        foreach (ref readonly NativeMemoryTraceEvent entry in events[..count]) Assert.Null(entry.CorrelationId);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
        GC.KeepAlive(entered);
    }

    [Fact]
    public void PooledPayloadPinProtectsBackingUntilReturnThenAllowsSlotReuse()
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = pool.RentTransferable(1, static writer => writer.Write(42));
        NativeShared<int> owner = NativeShared<int>.Create(ref source, new(1, 1));
        Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
        Assert.Throws<NativeAllocationInUseException>(pool.Dispose);
        Assert.Equal(42, owner.Read(static view => view[0]));
        long allocated = budget.CaptureStatistics().AllocationCount;
        owner.Dispose();
        using NativeTransfer<int> reused = pool.RentTransferable(1, static writer => writer.Write(99));
        Assert.Equal(99, reused.Read(static view => view[0]));
        Assert.Equal(allocated, budget.CaptureStatistics().AllocationCount);
        Assert.True(weak.IsExpired);
        Assert.False(weak.TryUpgrade(out _, out _));
        weak.Dispose();
    }

    [Fact]
    public void NativeGenerationReturnRejectsTheSharedLifetimeBeforeChangingBacking()
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = pool.RentTransferable(1, static writer => writer.Write(42));
        NativeShared<int> owner = NativeShared<int>.Create(ref source, new(1, 0));
        long extent = budget.CaptureStatistics().CommittedBytes;
        Assert.Throws<NativeAllocationInUseException>(pool.ReturnMemoryToNativeMemory);
        Assert.Equal(extent, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(42, owner.Read(static view => view[0]));
        owner.Dispose();
        pool.ReturnMemoryToNativeMemory();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void DeferredGenerationReturnKeepsSharedReadsValidUntilTheirRealPinEnds()
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeShared<int> owner = CreatePooledSharedForCollection(pool);
        Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
        long extent = budget.CaptureStatistics().CommittedBytes;
        pool.ReturnMemoryToGarbageCollector();
        // Whole-generation GC return detaches its owner, unlike scoped/lease
        // rollover's retired bank. Do not relabel these two real categories.
        Assert.Equal(0, pool.CaptureDiagnosticSnapshot().RetiredBytes);
        Assert.True(NativeMemoryDiagnostics.Snapshot().DetachedNativeBytes >= extent);
        Assert.Equal(extent, owner.CaptureSnapshot().OwnedBackingBytes);
        Assert.Equal(extent, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(42, owner.Read(static view => view[0]));
        pool.LeaseFromMemory();
        using NativeTransfer<int> fresh = pool.RentTransferable(1, static writer => writer.Write(99));
        long combined = budget.CaptureStatistics().CommittedBytes;
        Assert.True(combined > extent);
        owner.Dispose();
        // This generation was explicitly returned to GC, not to native memory.
        // Ending payload authority only removes its pin; credit physical release
        // after its actual finalizer, not at the shared-binding boundary.
        Assert.Equal(combined, budget.CaptureStatistics().CommittedBytes);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.Equal(combined - extent, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, pool.CaptureDiagnosticSnapshot().RetiredBytes);
        Assert.Equal(99, fresh.Read(static view => view[0]));
        Assert.True(weak.IsExpired);
        Assert.False(weak.TryUpgrade(out _, out _));
        weak.Dispose();
    }

    [Fact]
    public void QuarantinedDrainStaysChargedAndPayloadReturnNeverClaimsAPhysicalFree()
    {
        NativeMemoryBudget budget = new(1_024);
        NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        try
        {
            NativeTransfer<int>? source = pool.RentTransferable(1, static writer => writer.Write(42));
            NativeShared<int> owner = NativeShared<int>.Create(ref source, new(1, 1));
            Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
            long extent = budget.CaptureStatistics().CommittedBytes;
            pool.ReleaseLeasesToGarbageCollector();
            NativeMemoryTestHooks.FailAfterCommitBoundary(1);
            Assert.Throws<NativeAllocationQuarantinedException>(owner.Dispose);
            Assert.Equal(1, owner.CaptureSnapshot().PayloadReturnFailureCount);
            Assert.Equal(0, owner.CaptureSnapshot().PayloadReturnCount);
            Assert.False(owner.CaptureSnapshot().PayloadReleased);
            Assert.Equal(extent, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(1, pool.CaptureDiagnosticSnapshot().QuarantinedSegmentCount);
            Assert.True(weak.TryCompletePayloadReturn());
            Assert.Equal(1, weak.CaptureSnapshot().PayloadReturnCount);
            Assert.Equal(0, weak.CaptureSnapshot().OwnedBackingBytes);
            Assert.Equal(extent, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(0, budget.CaptureStatistics().FreeCount);
            Assert.True(weak.IsExpired);
            weak.Dispose();
            GC.KeepAlive(owner);
        }
        finally { pool.Dispose(); }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void LingeringWeakObserverDoesNotRootAnAbandonedLargePayloadOrAllocator()
    {
        NativeMemoryBudget budget = new(2_000_000);
        (NativeWeak<byte> weak, WeakReference payload) = CreateAbandonedPayload(budget);
        for (int attempt = 0; attempt < 8 && payload.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(payload.IsAlive);
        Assert.True(weak.IsExpired);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, weak.CaptureSnapshot().PayloadReturnCount);
        Assert.False(weak.TryUpgrade(out _, out _));
        weak.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (NativeWeak<byte>, WeakReference) CreateAbandonedPayload(NativeMemoryBudget budget)
    {
        using NativeBuilder<byte> builder = new(budget, 1_000_000);
        builder.Append(42);
        NativeTransfer<byte>? source = builder.Complete();
        NativeShared<byte> owner = NativeShared<byte>.Create(ref source, new(1, 1));
        if (!owner.TryDowngrade(out NativeWeak<byte> weak, out _)) throw new InvalidOperationException("Observer refused.");
        object payload = typeof(NativeShared<byte>).GetField("_payload", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        return (weak, new(payload));
    }

    [Fact]
    public void HistoriesSaturateIndependentlyOfExactBindingGauges()
    {
        NativeMemoryBudget budget = new(16);
        NativeShared<int> owner = Create(budget, 4, 2, 1);
        object payload = typeof(NativeShared<int>).GetField("_payload", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        object control = payload.GetType().GetProperty("Control", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(payload)!;
        control.GetType().GetField("_shares", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(control, long.MaxValue);
        Assert.False(owner.CaptureSnapshot().HistoryOverflowed);
        Assert.True(owner.TryShare(out NativeShared<int> share, out _));
        NativeSharingStatistics snapshot = owner.CaptureSnapshot();
        Assert.Equal(long.MaxValue, snapshot.ShareCount);
        Assert.True(snapshot.HistoryOverflowed);
        Assert.Equal(2, snapshot.StrongBindingCount);
        share.Dispose();
        owner.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void VersionExhaustionCannotMutateAFreeSlotOrPublishAnAlias()
    {
        NativeSharingBindingBank bank = new(1);
        Assert.True(bank.TryAcquire(out int slot, out long version));
        bank.Release(slot, version);
        Array slots = (Array)typeof(NativeSharingBindingBank).GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bank)!;
        object entry = slots.GetValue(0)!;
        entry.GetType().GetField("Version", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, long.MaxValue);
        slots.SetValue(entry, 0);
        Assert.Throws<OverflowException>(() => bank.TryAcquire(out _, out _));
        Assert.Equal(0, bank.Occupied);
        Assert.False(bank.IsActive(slot, version));
    }

    [Fact]
    public void TraceSeparatesBackingIdentityFromSharedOwnershipCorrelation()
    {
        NativeMemoryBudget budget = new(32, 16);
        NativeShared<int> owner = Create(budget, 4, 2, 1);
        Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
        Assert.True(weak.TryUpgrade(out NativeShared<int> upgraded, out _));
        upgraded.Dispose();
        owner.Dispose();
        Assert.False(weak.TryUpgrade(out _, out _));
        weak.Dispose();
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[16];
        int count = budget.CopyTraceTo(events);
        int correlated = 0;
        int returns = 0;
        foreach (ref readonly NativeMemoryTraceEvent entry in events[..count])
        {
            if (entry.CorrelationId is null) continue;
            correlated++;
            Assert.Equal(owner.Id, entry.CorrelationId);
            Assert.Equal(owner.CaptureSnapshot().OwnerId, entry.OwnerId);
            Assert.Null(entry.AllocationOrdinal);
            if (entry.Kind == NativeMemoryTraceKind.PayloadReturned) returns++;
        }
        Assert.Equal(8, correlated);
        Assert.Equal(1, returns);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public async Task EnteredReadSurvivesConcurrentLastReleaseWithoutUpgradeResurrection()
    {
        NativeMemoryBudget budget = new(16);
        NativeShared<int> owner = Create(budget, 4, 2, 1);
        Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim finish = new(false);
        Task reader = Task.Run(() => owner.Access(view =>
        {
            entered.Set();
            Assert.True(finish.Wait(TimeSpan.FromSeconds(20)));
            Assert.Equal(42, view[0]);
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(20)));
            owner.Dispose();
            Assert.True(weak.IsExpired);
            Assert.False(weak.TryUpgrade(out _, out _));
            Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
        }
        finally { finish.Set(); }
        await reader.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, weak.CaptureSnapshot().PayloadReturnCount);
        weak.Dispose();
    }

    private static NativeShared<int> Create(NativeMemoryBudget budget, int capacity, int strongSlots, int weakSlots)
    {
        using NativeBuilder<int> builder = new(budget, capacity);
        builder.Append(42);
        NativeTransfer<int>? source = builder.Complete();
        try { return NativeShared<int>.Create(ref source, new(strongSlots, weakSlots)); }
        finally { source?.Dispose(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeShared<int> CreatePooledSharedForCollection(NativeConcurrentPool<int> pool)
    {
        // Keep acquisition/conversion temporaries out of the collecting frame;
        // a JIT-retained stale unique value legitimately retains its CLR control.
        NativeTransfer<int>? source = pool.RentTransferable(1, static writer => writer.Write(42));
        try { return NativeShared<int>.Create(ref source, new(1, 1)); }
        finally { source?.Dispose(); }
    }
}
