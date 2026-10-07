using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeSharingCustodyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SharedCustodyDoesNotReportAPublicUniqueOwner(int acquisition)
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = Acquire(acquisition, budget, pool);
        NativeTransfer<int> observed = source.Value;
        using NativeShared<int> shared = NativeShared<int>.Create(ref source, new(1, 0));
        Assert.Null(source);
        NativeTransferStatistics custody = observed.CaptureSnapshot();
        Assert.Equal(NativeTransferLifecycle.Shared, custody.Lifecycle);
        Assert.False(custody.BindingIsActive);
        Assert.Equal(0, custody.LiveUniqueOwnerCount);
        Assert.True(custody.HasReturnObligation);
        Assert.True(custody.OwnedBackingBytes > 0);
        Assert.Equal(sizeof(int), custody.InitializedPayloadBytes);
        Assert.Equal(sizeof(int), shared.CaptureSnapshot().InitializedPayloadBytes);
        Assert.Equal(42, shared.Read(static view => view[0]));
        Assert.Throws<InvalidOperationException>(() => observed.Read(static view => view[0]));
        Assert.Throws<InvalidOperationException>(observed.Dispose);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EarlierGenuineUniqueBorrowsKeepTheirPeakAfterSharing(int acquisition)
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = Acquire(acquisition, budget, pool);
        NativeTransfer<int> observed = source.Value;
        observed.Access(_ => observed.Access(static _ => { }));
        Assert.Equal(2, observed.CaptureSnapshot().PeakBorrowCount);
        using NativeShared<int> shared = NativeShared<int>.Create(ref source, new(1, 0));
        Assert.Null(source);
        NativeTransferStatistics custody = observed.CaptureSnapshot();
        Assert.Equal(0, custody.ActiveBorrowCount);
        Assert.Equal(2, custody.PeakBorrowCount);
        Assert.Equal(NativeTransferLifecycle.Shared, custody.Lifecycle);
        Assert.Equal(42, shared.Read(static view => view[0]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedReturnKeepsSharedCustodyWithoutReopeningUniqueAuthority(int acquisition)
    {
        NativeMemoryTestHooks.Reset();
        NativeMemoryBudget budget = new(1_024);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = Acquire(acquisition, budget, pool);
        NativeTransfer<int> observed = source.Value;
        NativeShared<int> shared = NativeShared<int>.Create(ref source, new(1, 1));
        try
        {
            NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
            Assert.Throws<InvalidOperationException>(shared.Dispose);
            NativeTransferStatistics pending = observed.CaptureSnapshot();
            Assert.Equal(NativeTransferLifecycle.Shared, pending.Lifecycle);
            Assert.Equal(0, pending.LiveUniqueOwnerCount);
            Assert.False(pending.BindingIsActive);
            Assert.True(pending.HasReturnObligation);
            Assert.Equal(1, pending.PayloadReturnFailureCount);
            Assert.Throws<InvalidOperationException>(observed.Dispose);
            Assert.False(observed.TryCompletePayloadReturn());
            Assert.True(shared.TryCompletePayloadReturn());
            NativeTransferStatistics returned = observed.CaptureSnapshot();
            Assert.Equal(NativeTransferLifecycle.Returned, returned.Lifecycle);
            Assert.Equal(0, returned.OwnedBackingBytes);
            Assert.False(returned.HasReturnObligation);
            Assert.Equal(1, returned.PayloadReturnCount);
            Assert.Equal(1, returned.PayloadReturnFailureCount);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
            shared.TryCompletePayloadReturn();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CompletedStorageReturnIsNotHiddenOrRepeatedWhenObservationFails(int acquisition)
    {
        NativeMemoryTestHooks.Reset();
        NativeMemoryBudget budget = new(1_024);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = Acquire(acquisition, budget, pool);
        NativeTransfer<int> observed = source.Value;
        NativeShared<int> shared = NativeShared<int>.Create(ref source, new(1, 1));
        try
        {
            NativeMemoryTestHooks.FailAtManagedPublicationBoundary(5);
            Assert.Throws<InvalidOperationException>(shared.Dispose);
            NativeTransferStatistics actual = observed.CaptureSnapshot();
            Assert.Equal(NativeTransferLifecycle.Returned, actual.Lifecycle);
            Assert.False(actual.HasReturnObligation);
            Assert.Equal(0, actual.OwnedBackingBytes);
            Assert.Equal(1, actual.PayloadReturnCount);
            Assert.Equal(0, actual.PayloadReturnFailureCount);
            NativeSharingStatistics sharing = shared.CaptureSnapshot();
            Assert.True(sharing.PayloadReleased);
            Assert.Equal(0, sharing.OwnedBackingBytes);
            Assert.Equal(1, sharing.PayloadReturnCount);
            Assert.Equal(1, sharing.PayloadReturnFailureCount);
            long actualFreeCount = budget.CaptureStatistics().FreeCount;
            Assert.True(shared.TryCompletePayloadReturn());
            Assert.True(observed.TryCompletePayloadReturn());
            Assert.Equal(actualFreeCount, budget.CaptureStatistics().FreeCount);
            Assert.Equal(1, shared.CaptureSnapshot().PayloadReturnCount);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
            shared.TryCompletePayloadReturn();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SharedPayloadReusesAcquisitionCustodyWithoutAnotherPublicUniqueCapability(int acquisition)
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = Acquire(acquisition, budget, pool);
        object? custody = source.Value.ControlForTest;
        using NativeShared<int> shared = NativeShared<int>.Create(ref source, new(1, 1));
        Assert.Same(custody, typeof(NativeShared<int>).GetField("_payload", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shared));
        FieldInfo[] fields = typeof(NativeTransferControl<int>).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, static field => field.FieldType == typeof(NativeTransfer<int>)
            || field.FieldType == typeof(NativeTransfer<int>?));
        Assert.Equal(42, shared.Read(static view => view[0]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SharingPinIsNotInventedAsAnEnteredUniqueBorrow(int acquisition)
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = Acquire(acquisition, budget, pool);
        NativeTransfer<int> observed = source.Value;
        Assert.Equal(0, observed.CaptureSnapshot().PeakBorrowCount);
        using NativeShared<int> shared = NativeShared<int>.Create(ref source, new(1, 0));
        Assert.Null(source);
        NativeTransferStatistics custody = observed.CaptureSnapshot();
        Assert.Equal(0, custody.ActiveBorrowCount);
        Assert.Equal(0, custody.PeakBorrowCount);
        Assert.Equal(42, shared.Read(static view => view[0]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void WeakObserverDoesNotKeepReusedAcquisitionCustodyAlive(int acquisition)
    {
        NativeMemoryBudget budget = new(1_024);
        (NativeWeak<int> weak, WeakReference custody) = AbandonShared(acquisition, budget);
        for (int attempt = 0; attempt < 8 && (!weak.CaptureSnapshot().PayloadReleased || budget.CaptureStatistics().CommittedBytes != 0); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(custody.IsAlive);
        Assert.True(weak.IsExpired);
        Assert.True(weak.CaptureSnapshot().PayloadReleased);
        Assert.Equal(1, weak.CaptureSnapshot().PayloadReturnCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.False(weak.TryUpgrade(out _, out _));
        weak.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (NativeWeak<int> Weak, WeakReference Custody) AbandonShared(int acquisition, NativeMemoryBudget budget)
    {
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = Acquire(acquisition, budget, pool);
        NativeTransfer<int> stale = source.Value;
        NativeShared<int> owner = NativeShared<int>.Create(ref source, new(1, 1));
        if (!owner.TryDowngrade(out NativeWeak<int> weak, out _)) throw new InvalidOperationException("Prepared observer refused.");
        WeakReference custody = new(stale.ControlForTest);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.True(custody.IsAlive);
        Assert.False(weak.CaptureSnapshot().PayloadReleased);
        Assert.Throws<InvalidOperationException>(() => stale.Read(static view => view[0]));
        // The active sharing pin forbids native generation disposal. Explicit
        // GC return detaches that generation while its real pin protects it.
        pool.ReturnMemoryToGarbageCollector();
        GC.KeepAlive(stale);
        return (weak, custody);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DeterministicSharedReturnClearsBudgetAndAllocatorDespiteRetainedAliases(int acquisition)
    {
        (NativeTransfer<int> stale, NativeShared<int> shared, NativeWeak<int> weak,
            WeakReference budget, WeakReference pool) = ReturnSharedWithAliases(acquisition);
        for (int attempt = 0; attempt < 8 && (budget.IsAlive || pool.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(budget.IsAlive);
        Assert.False(pool.IsAlive);
        Assert.False(stale.CaptureSnapshot().HasReturnObligation);
        Assert.Equal(0, stale.CaptureSnapshot().OwnedBackingBytes);
        Assert.Equal(1, shared.CaptureSnapshot().PayloadReturnCount);
        Assert.True(weak.IsExpired);
        weak.Dispose();
        GC.KeepAlive(stale);
        GC.KeepAlive(shared);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (NativeTransfer<int> Stale, NativeShared<int> Shared, NativeWeak<int> Weak,
        WeakReference Budget, WeakReference Pool) ReturnSharedWithAliases(int acquisition)
    {
        NativeMemoryBudget budget = new(1_024, 32);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = Acquire(acquisition, budget, pool);
        NativeTransfer<int> stale = source.Value;
        NativeShared<int> shared = NativeShared<int>.Create(ref source, new(1, 1));
        if (!shared.TryDowngrade(out NativeWeak<int> weak, out _)) throw new InvalidOperationException("Prepared observer refused.");
        shared.Dispose();
        return (stale, shared, weak, new(budget), new(pool));
    }

    [Fact]
    public void FailedPooledPinReturnsConsumedCustodyWithoutInventingASharedBorrow()
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = Acquire(1, budget, pool);
        NativeTransfer<int> stale = source.Value;
        NativeMemoryTestHooks.SetBeforeOperationEntry(static operation =>
        {
            if (string.Equals(operation, "NativeShared.Pin", StringComparison.Ordinal))
                throw new InvalidOperationException("Injected pin failure.");
        });
        try
        {
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => NativeShared<int>.Create(ref source, new(1, 1)));
            Assert.Equal("Injected pin failure.", failure.Message);
            Assert.Null(source);
            NativeTransferStatistics actual = stale.CaptureSnapshot();
            Assert.Equal(NativeTransferLifecycle.Returned, actual.Lifecycle);
            Assert.Equal(0, actual.PeakBorrowCount);
            Assert.Equal(1, actual.PayloadReturnCount);
            pool.Dispose();
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        }
        finally { NativeMemoryTestHooks.Reset(); source?.Dispose(); }
    }

    [Fact]
    public void PinAndCleanupFailureNeverReopensOrReportsAPublicUniqueOwner()
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = Acquire(1, budget, pool);
        NativeTransfer<int> stale = source.Value;
        NativeTransferControl<int> custody = (NativeTransferControl<int>)stale.ControlForTest!;
        NativeMemoryTestHooks.SetBeforeOperationEntry(static operation =>
        {
            if (string.Equals(operation, "NativeShared.Pin", StringComparison.Ordinal))
                throw new InvalidOperationException("Injected pin failure.");
        });
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
        try
        {
            AggregateException failure = Assert.Throws<AggregateException>(() => NativeShared<int>.Create(ref source, new(1, 1)));
            Assert.Equal(2, failure.InnerExceptions.Count);
            Assert.Equal("Injected pin failure.", failure.InnerExceptions[0].Message);
            Assert.Null(source);
            NativeTransferStatistics pending = stale.CaptureSnapshot();
            Assert.Equal(NativeTransferLifecycle.Shared, pending.Lifecycle);
            Assert.False(pending.BindingIsActive);
            Assert.Equal(0, pending.LiveUniqueOwnerCount);
            Assert.Equal(0, pending.PeakBorrowCount);
            Assert.True(pending.HasReturnObligation);
            Assert.Equal(1, pending.PayloadReturnFailureCount);
            Assert.Throws<InvalidOperationException>(() => stale.Read(static view => view[0]));
            Assert.Throws<InvalidOperationException>(stale.Dispose);
            Assert.False(stale.TryCompletePayloadReturn());
            Assert.True(custody.Control.CaptureSnapshot().Expired);
            Assert.Equal(0, custody.Control.CaptureSnapshot().StrongBindingCount);
            pool.ReturnMemoryToNativeMemory();
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(0, stale.CaptureSnapshot().OwnedBackingBytes);
            Assert.Equal(0, stale.CaptureSnapshot().InitializedPayloadBytes);
            Assert.Equal(sizeof(int), stale.CaptureSnapshot().PeakInitializedPayloadBytes);
            Assert.True(stale.CaptureSnapshot().HasReturnObligation);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
            source?.Dispose();
            // No public shared binding was published. Exercise the same private
            // custody retry used by emergency cleanup, without GC timing here.
            Assert.True(custody.Control.TryCompletePayloadReturn(custody));
        }
        Assert.Equal(1, stale.CaptureSnapshot().PayloadReturnCount);
        pool.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(0, 4)]
    [InlineData(0, 5)]
    [InlineData(1, 3)]
    [InlineData(1, 4)]
    [InlineData(1, 5)]
    [InlineData(2, 3)]
    [InlineData(2, 4)]
    [InlineData(2, 5)]
    public void PublishedRetryCannotStrandReturnOrReportSuccessBeforeCompletion(int acquisition, int boundary)
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = Acquire(acquisition, budget, pool);
        NativeTransfer<int> stale = source.Value;
        NativeTransferControl<int> custody = (NativeTransferControl<int>)stale.ControlForTest!;
        NativeShared<int> shared = NativeShared<int>.Create(ref source, new(1, 1));
        Assert.True(shared.TryDowngrade(out NativeWeak<int> weak, out _));
        bool retried = false;
        bool releasedDuringRetry = false;
        int callbacks = 0;
        NativeMemoryTestHooks.SetBeforeOperationEntry(operation =>
        {
            if (!string.Equals(operation, "NativeShared.RetryReady", StringComparison.Ordinal)) return;
            callbacks++;
            retried = weak.TryCompletePayloadReturn();
            releasedDuringRetry = weak.CaptureSnapshot().PayloadReleased;
        });
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(boundary);
        try
        {
            Assert.Throws<InvalidOperationException>(shared.Dispose);
            Assert.Equal(1, callbacks);
            Assert.True(retried);
            Assert.True(releasedDuringRetry, "Retry returned without completing payload return at the published retry boundary.");
            Assert.Equal(1, shared.CaptureSnapshot().PayloadReturnCount);
            Assert.Equal(1, shared.CaptureSnapshot().PayloadReturnFailureCount);
            Assert.Equal(NativeTransferLifecycle.Returned, stale.CaptureSnapshot().Lifecycle);
            Assert.False(stale.CaptureSnapshot().HasReturnObligation);
            Assert.Equal(1, stale.CaptureSnapshot().PayloadReturnCount);
            Assert.True(weak.TryCompletePayloadReturn());
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
            if (!shared.CaptureSnapshot().PayloadReleased)
            {
                // Only failed-regression cleanup: the old ordering could strand
                // the retry flag, so release that test-owned state before exit.
                custody.Control.RecordReturnFailure();
                custody.Control.TryCompletePayloadReturn(custody);
            }
            weak.Dispose();
        }
        pool.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(0, 4)]
    [InlineData(1, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 2)]
    [InlineData(2, 4)]
    public void DeferringABusyCustodyClaimDoesNotReportSuccessOrStrandTheRetry(int acquisition, int busyState)
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = Acquire(acquisition, budget, pool);
        NativeTransfer<int> stale = source.Value;
        NativeTransferControl<int> custody = (NativeTransferControl<int>)stale.ControlForTest!;
        NativeShared<int> shared = NativeShared<int>.Create(ref source, new(1, 1));
        Assert.True(shared.TryDowngrade(out NativeWeak<int> weak, out _));
        FieldInfo state = typeof(NativeTransferControl<int>).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!;
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
        Assert.Throws<InvalidOperationException>(shared.Dispose);
        int ready = (int)state.GetValue(custody)!;
        try
        {
            // Deterministically represent a competing private state claim; no
            // sleeps or timing assumptions are needed to inspect deferral.
            state.SetValue(custody, busyState);
            Assert.False(weak.TryCompletePayloadReturn());
            Assert.False(weak.TryCompletePayloadReturn());
            Assert.Equal(busyState, (int)state.GetValue(custody)!);
            Assert.False(weak.CaptureSnapshot().PayloadReleased);
            Assert.Equal(1, weak.CaptureSnapshot().PayloadReturnFailureCount);
            state.SetValue(custody, ready);
            Assert.True(weak.TryCompletePayloadReturn());
            Assert.Equal(1, weak.CaptureSnapshot().PayloadReturnFailureCount);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
            if (!custody.StorageHasBeenReturned)
            {
                state.SetValue(custody, ready);
                custody.Control.DeferPayloadReturn();
                custody.Control.TryCompletePayloadReturn(custody);
            }
            weak.Dispose();
        }
        Assert.Equal(NativeTransferLifecycle.Returned, stale.CaptureSnapshot().Lifecycle);
        Assert.Equal(1, shared.CaptureSnapshot().PayloadReturnCount);
        Assert.Equal(1, stale.CaptureSnapshot().PayloadReturnCount);
        pool.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    private static NativeTransfer<int> Acquire(int acquisition, NativeMemoryBudget budget, NativeConcurrentPool<int> pool)
    {
        switch (acquisition)
        {
            case 0:
                using (NativeBuilder<int> builder = new(budget, 4))
                {
                    builder.Append(42);
                    return builder.Complete();
                }
            case 1:
                return pool.RentTransferable(1, static writer => writer.Write(42));
            case 2:
                if (!budget.TryReserve<int>(1, out NativeMemoryReservation<int>? permission, out _))
                    throw new InvalidOperationException("The declared test capacity was refused.");
                NativeMemoryReservation<int>? moving = permission;
                return NativeMemoryReservation<int>.Activate(ref moving, static writer => writer.Write(42));
            default:
                throw new ArgumentOutOfRangeException(nameof(acquisition));
        }
    }
}
