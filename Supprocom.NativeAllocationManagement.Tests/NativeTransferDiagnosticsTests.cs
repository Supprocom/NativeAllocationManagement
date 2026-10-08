using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeTransferDiagnosticsTests
{
    [Fact]
    public void MovementAndReturnKeepActualLineageWithoutGrantingStaleAuthority()
    {
        NativeMemoryBudget budget = new(16);
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(42);
        NativeTransfer<int>? source = builder.Complete();
        NativeTransfer<int> stale = source.Value;
        NativeTransferStatistics initial = stale.CaptureSnapshot();
        Assert.Equal(builder.Id, initial.OwnerId);
        Assert.Equal(initial.OwnerId, initial.AllocationId);
        Assert.Equal(1, initial.BindingVersion);
        Assert.Equal(1, initial.AuthorityVersion);
        Assert.Equal(0, initial.MoveCount);
        Assert.Equal(1, initial.LiveUniqueOwnerCount);
        Assert.Equal(4, initial.InitializedPayloadBytes);
        Assert.Equal(16, initial.OwnedBackingBytes);
        Assert.Equal(0, initial.BorrowedBackingBytes);
        Assert.True(initial.BindingIsActive);
        Assert.False(stale.TryCompletePayloadReturn());
        NativeTransfer<int> destination = NativeTransfer<int>.Move(ref source);
        Assert.Null(source);
        Assert.False(stale.CaptureSnapshot().BindingIsActive);
        Assert.Equal(2, stale.CaptureSnapshot().AuthorityVersion);
        Assert.Equal(1, destination.CaptureSnapshot().MoveCount);
        Assert.Equal(2, destination.CaptureSnapshot().BindingVersion);
        Assert.True(destination.CaptureSnapshot().BindingIsActive);
        destination.Dispose();
        NativeTransferStatistics returned = stale.CaptureSnapshot();
        Assert.Equal(initial.OwnerId, stale.Id);
        Assert.Equal(NativeTransferLifecycle.Returned, returned.Lifecycle);
        Assert.Equal(0, returned.LiveUniqueOwnerCount);
        Assert.Equal(0, returned.InitializedPayloadBytes);
        Assert.Equal(4, returned.PeakInitializedPayloadBytes);
        Assert.Equal(0, returned.OwnedBackingBytes);
        Assert.Equal(16, returned.PeakOwnedBackingBytes);
        Assert.Equal(1, returned.PayloadReturnCount);
        Assert.Equal(0, returned.PayloadReturnFailureCount);
        Assert.True(stale.TryCompletePayloadReturn());
        Assert.Throws<ObjectDisposedException>(() => stale.Access(static _ => { }));
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void NestedBorrowPeakComesFromRealEntryAndSnapshotsAllocateNothing()
    {
        NativeTransfer<int> owner = CreateDirect(new(16));
        try
        {
            owner.Access(_ =>
            {
                Assert.Equal(1, owner.CaptureSnapshot().ActiveBorrowCount);
                owner.Access(_ =>
                {
                    Assert.Equal(2, owner.CaptureSnapshot().ActiveBorrowCount);
                    Assert.Equal(2, owner.CaptureSnapshot().PeakBorrowCount);
                    Assert.False(owner.TryCompletePayloadReturn());
                });
                Assert.Equal(1, owner.CaptureSnapshot().ActiveBorrowCount);
            });
            long before = GC.GetAllocatedBytesForCurrentThread();
            NativeTransferStatistics observed = default;
            for (int iteration = 0; iteration < 10_000; iteration++) observed = owner.CaptureSnapshot();
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(0, observed.ActiveBorrowCount);
            Assert.Equal(2, observed.PeakBorrowCount);
        }
        finally { owner.Dispose(); }
    }

    [Fact]
    public void FailedDeterministicReturnDoesNotCreditSuccessOrLoseAuthority()
    {
        NativeMemoryBudget budget = new(16);
        NativeTransfer<int> owner = CreateDirect(budget);
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
        Assert.Throws<InvalidOperationException>(owner.Dispose);
        NativeTransferStatistics failed = owner.CaptureSnapshot();
        Assert.Equal(1, failed.PayloadReturnFailureCount);
        Assert.Equal(0, failed.PayloadReturnCount);
        Assert.Equal(16, failed.OwnedBackingBytes);
        Assert.Equal(NativeTransferLifecycle.Active, failed.Lifecycle);
        Assert.True(failed.BindingIsActive);
        Assert.Equal(42, owner.Read(static view => view[0]));
        Assert.False(owner.TryCompletePayloadReturn());
        owner.Dispose();
        Assert.Equal(1, owner.CaptureSnapshot().PayloadReturnCount);
        Assert.Equal(1, owner.CaptureSnapshot().PayloadReturnFailureCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void ConsumedRetirementFailureRemainsVisibleAndCanBeRetriedWithoutResurrection()
    {
        NativeMemoryBudget budget = new(16, 8);
        NativeTransfer<int>? source = CreateDirect(budget);
        NativeTransfer<int> alias = source.Value;
        alias.Access(view =>
        {
            Assert.Throws<InvalidOperationException>(() => NativeTransfer<int>.Move(ref source));
            Assert.Null(source);
            Assert.Equal(NativeTransferLifecycle.Retiring, alias.CaptureSnapshot().Lifecycle);
            Assert.Equal(1, alias.CaptureSnapshot().ActiveBorrowCount);
            Assert.False(alias.TryCompletePayloadReturn());
            Assert.Equal(42, view[0]);
            NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
        });
        NativeTransferStatistics failed = alias.CaptureSnapshot();
        Assert.Equal(0, failed.PayloadReturnCount);
        Assert.Equal(1, failed.PayloadReturnFailureCount);
        Assert.Equal(NativeTransferLifecycle.Retiring, failed.Lifecycle);
        Assert.False(failed.BindingIsActive);
        Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
        Assert.Throws<InvalidOperationException>(() => alias.Read(static view => view[0]));
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
        Assert.Throws<InvalidOperationException>(() => alias.TryCompletePayloadReturn());
        Assert.Equal(2, alias.CaptureSnapshot().PayloadReturnFailureCount);
        Assert.True(alias.TryCompletePayloadReturn());
        Assert.Equal(1, alias.CaptureSnapshot().PayloadReturnCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void FailedReturnHistorySaturatesWithoutDisruptingSuccessfulCleanup()
    {
        NativeMemoryBudget budget = new(16);
        NativeTransfer<int> owner = CreateDirect(budget);
        typeof(NativeTransferControl<int>).GetField("_returnFailures", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(owner.ControlForTest, long.MaxValue);
        Assert.False(owner.CaptureSnapshot().HistoryOverflowed);
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
        Assert.Throws<InvalidOperationException>(owner.Dispose);
        Assert.Equal(long.MaxValue, owner.CaptureSnapshot().PayloadReturnFailureCount);
        Assert.True(owner.CaptureSnapshot().HistoryOverflowed);
        owner.Dispose();
        Assert.Equal(1, owner.CaptureSnapshot().PayloadReturnCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void EmergencyReturnFailureKeepsItsRetryObligationUntilALaterCollection()
    {
        // The failure hook is process-wide. Finish unrelated emergency cleanup
        // before creating its target, then arm it while that target is rooted.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        NativeMemoryBudget budget = new(16);
        WeakReference control = CreateAbandonedControl(budget);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().FreeCount);
        Assert.Equal(1, ReadAbandonedFailureCount(control));
        for (int attempt = 0; attempt < 8 && budget.CaptureStatistics().CommittedBytes != 0; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void ReturnedAliasesDoNotRetainDetachedPooledPayloadStorage()
    {
        NativeMemoryBudget budget = new(128);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int> alias = CreatePooledForCollection(pool);
        long extent = budget.CaptureStatistics().CommittedBytes;
        Assert.Equal(pool.Id, alias.CaptureSnapshot().OwnerId);
        Assert.True(alias.CaptureSnapshot().AllocationId > 0);
        Assert.Equal(extent, alias.CaptureSnapshot().OwnedBackingBytes);
        pool.ReturnMemoryToGarbageCollector();
        alias.Dispose();
        Assert.Equal(1, alias.CaptureSnapshot().PayloadReturnCount);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(extent, alias.CaptureSnapshot().PeakOwnedBackingBytes);
        GC.KeepAlive(alias);
    }

    [Fact]
    public void ControlFieldStorageMatchesItsDeclaredRepresentationsWithoutInventedHeapHeaders()
    {
        using NativeTransfer<int> owner = CreateDirect(new(16));
        long representedBytes = 0;
        foreach (FieldInfo field in typeof(NativeTransferControl<int>).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            Type type = field.FieldType;
            representedBytes += !type.IsValueType ? IntPtr.Size : type == typeof(long) ? sizeof(long)
                : type == typeof(int) ? sizeof(int) : type == typeof(bool) ? sizeof(bool)
                : type == typeof(NativeBlock) ? Unsafe.SizeOf<NativeBlock>()
                : throw new InvalidOperationException($"Unaccounted control field {field.Name}");
        }
        Assert.Equal(representedBytes, owner.CaptureSnapshot().ControlFieldBytes);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(2, 0, false)]
    [InlineData(2, 1, false)]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(1, 0, true)]
    [InlineData(1, 1, true)]
    [InlineData(2, 0, true)]
    [InlineData(2, 1, true)]
    public void ReturnedControlKeepsScalarIdentityButNoDescriptorCustody(int acquisition, int length, bool share)
    {
        NativeMemoryBudget budget = new(1_024, 32);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = AcquireDescriptorControl(acquisition, length, budget, pool);
        NativeTransfer<int> observed = source.Value;
        NativeTransferStatistics initial = observed.CaptureSnapshot();
        Assert.True(initial.OwnerId > 0);
        Assert.True(initial.AllocationId > 0);
        Assert.Equal(length, observed.Length);
        NativeTransfer<int>? destination = NativeTransfer<int>.Move(ref source);
        try
        {
            Assert.Null(source);
            Assert.Equal(initial.AllocationId, destination.Value.CaptureSnapshot().AllocationId);
            if (share)
            {
                using NativeShared<int> shared = NativeShared<int>.Create(ref destination, new(1, 0));
                Assert.Equal(length * 42, shared.Read(static view =>
                {
                    int sum = 0;
                    foreach (ref readonly int value in view.AsSpan()) sum += value;
                    return sum;
                }));
            }
            else
            {
                destination.Value.Dispose();
                destination = null;
            }
        }
        // Sharing consumes the nullable destination even when publication fails.
#pragma warning disable CA1508
        finally { destination?.Dispose(); source?.Dispose(); }
#pragma warning restore CA1508
        NativeTransferStatistics returned = observed.CaptureSnapshot();
        Assert.Equal(initial.OwnerId, returned.OwnerId);
        Assert.Equal(initial.AllocationId, returned.AllocationId);
        Assert.Equal(initial.PeakOwnedBackingBytes, returned.PeakOwnedBackingBytes);
        Assert.Equal(1, returned.PayloadReturnCount);
        Assert.False(returned.HasReturnObligation);
        Assert.False(returned.BindingIsActive);
        Assert.Throws<ObjectDisposedException>(() => observed.Read(static view => view.Length));
        NativeBlock descriptor = (NativeBlock)typeof(NativeTransferControl<int>)
            .GetField("_block", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(observed.ControlForTest)!;
        Assert.Equal(IntPtr.Zero, descriptor.Pointer);
        Assert.Equal((nuint)0, descriptor.ByteLength);
        Assert.Null(descriptor.Budget);
        Assert.Equal(initial.AllocationId, descriptor.OwnerId);
        pool.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
    }

    [Fact]
    public void DefaultCapabilitiesCannotObserveOrCompleteFictitiousOwnership()
    {
        NativeTransfer<int> value = default;
        Assert.Throws<NativeAllocationUninitializedException>(() => value.CaptureSnapshot());
        Assert.Throws<NativeAllocationUninitializedException>(() => value.TryCompletePayloadReturn());
    }

    [Fact]
    public void AllocatorNativeReturnRevokesAuthorityWithoutInventingControlCleanup()
    {
        NativeMemoryBudget budget = new(128);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int> owner = pool.RentTransferable(1, static writer => writer.Write(42));
        long extent = owner.CaptureSnapshot().OwnedBackingBytes;
        pool.ReturnMemoryToNativeMemory();
        NativeTransferStatistics invalid = owner.CaptureSnapshot();
        Assert.Equal(NativeTransferLifecycle.Invalidated, invalid.Lifecycle);
        Assert.False(invalid.BindingIsActive);
        Assert.Equal(0, invalid.LiveUniqueOwnerCount);
        Assert.Equal(0, invalid.InitializedPayloadBytes);
        Assert.Equal(0, invalid.OwnedBackingBytes);
        Assert.Equal(extent, invalid.PeakOwnedBackingBytes);
        Assert.True(invalid.HasReturnObligation);
        Assert.Equal(0, invalid.PayloadReturnCount);
        owner.Dispose();
        Assert.False(owner.CaptureSnapshot().HasReturnObligation);
        Assert.Equal(1, owner.CaptureSnapshot().PayloadReturnCount);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void EnteredBorrowAcrossGcDetachRemainsInitializedWithoutLiveBindingAuthority()
    {
        NativeMemoryBudget budget = new(128);
        using NativeConcurrentPool<int> pool = new(budget, 4, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int> owner = pool.RentTransferable(1, static writer => writer.Write(42));
        long extent = owner.CaptureSnapshot().OwnedBackingBytes;
        owner.Access(view =>
        {
            pool.ReturnMemoryToGarbageCollector();
            NativeTransferStatistics entered = owner.CaptureSnapshot();
            Assert.False(entered.BindingIsActive);
            Assert.Equal(0, entered.LiveUniqueOwnerCount);
            Assert.Equal(1, entered.ActiveBorrowCount);
            Assert.Equal(4, entered.InitializedPayloadBytes);
            Assert.Equal(extent, entered.OwnedBackingBytes);
            Assert.Equal(42, view[0]);
        });
        Assert.Equal(0, owner.CaptureSnapshot().InitializedPayloadBytes);
        Assert.Equal(extent, owner.CaptureSnapshot().OwnedBackingBytes);
        owner.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        GC.KeepAlive(owner);
    }

    [Fact]
    public void ObservationFailureAfterPhysicalReturnCannotReviveOrFreeThePayloadTwice()
    {
        NativeMemoryBudget budget = new(16, 8);
        NativeTransfer<int> owner = CreateDirect(budget);
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(5);
        Assert.Throws<InvalidOperationException>(owner.Dispose);
        NativeTransferStatistics returned = owner.CaptureSnapshot();
        Assert.Equal(NativeTransferLifecycle.Returned, returned.Lifecycle);
        Assert.Equal(1, returned.PayloadReturnCount);
        Assert.Equal(0, returned.PayloadReturnFailureCount);
        Assert.False(returned.BindingIsActive);
        Assert.False(returned.HasReturnObligation);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
        Assert.True(owner.TryCompletePayloadReturn());
        Assert.Throws<ObjectDisposedException>(() => owner.Read(static view => view[0]));
        Assert.Throws<ObjectDisposedException>(owner.Dispose);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void ConsumedRetirementObservationFailureCannotScheduleAnotherStorageReturn()
    {
        NativeMemoryBudget budget = new(16, 8);
        NativeTransfer<int>? source = CreateDirect(budget);
        NativeTransfer<int> alias = source.Value;
        alias.Access(_ =>
        {
            Assert.Throws<InvalidOperationException>(() => NativeTransfer<int>.Move(ref source));
            NativeMemoryTestHooks.FailAtManagedPublicationBoundary(5);
        });
        Assert.Equal(NativeTransferLifecycle.Returned, alias.CaptureSnapshot().Lifecycle);
        Assert.Equal(1, alias.CaptureSnapshot().PayloadReturnCount);
        Assert.Equal(0, alias.CaptureSnapshot().PayloadReturnFailureCount);
        Assert.True(alias.TryCompletePayloadReturn());
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
        GC.KeepAlive(alias);
    }

    private static NativeTransfer<int> CreateDirect(NativeMemoryBudget budget)
    {
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(42);
        return builder.Complete();
    }

    private static NativeTransfer<int> AcquireDescriptorControl(int acquisition, int length,
        NativeMemoryBudget budget, NativeConcurrentPool<int> pool)
    {
        switch (acquisition)
        {
            case 0:
                using (NativeBuilder<int> builder = new(budget, length))
                {
                    for (int index = 0; index < length; index++) builder.Append(42);
                    return builder.Complete();
                }
            case 1:
                return pool.RentTransferable(length, static writer => writer.Fill(42));
            case 2:
                Assert.True(budget.TryReserve<int>(length, out NativeMemoryReservation<int>? permission, out _));
                return NativeMemoryReservation<int>.Activate(ref permission, static writer => writer.Fill(42));
            default:
                throw new ArgumentOutOfRangeException(nameof(acquisition));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAbandonedControl(NativeMemoryBudget budget)
    {
        NativeTransfer<int> owner = CreateDirect(budget);
        WeakReference control = new(owner.ControlForTest!, trackResurrection: true);
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
        GC.KeepAlive(owner);
        return control;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long ReadAbandonedFailureCount(WeakReference control) =>
        ((NativeTransferControl<int>)control.Target!).CaptureSnapshot(1).PayloadReturnFailureCount;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeTransfer<int> CreatePooledForCollection(NativeConcurrentPool<int> pool) =>
        pool.RentTransferable(1, static writer => writer.Write(42));
}
