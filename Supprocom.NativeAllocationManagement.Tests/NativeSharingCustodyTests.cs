using System.Reflection;

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

    [Fact]
    public void SharedPayloadStoresCustodyRatherThanAnotherPublicUniqueCapability()
    {
        FieldInfo[] fields = typeof(NativeSharedPayload<int>).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        int custodyFields = 0;
        foreach (FieldInfo field in fields)
            if (field.FieldType == typeof(NativeTransferControl<int>)) custodyFields++;
        Assert.Equal(1, custodyFields);
        Assert.DoesNotContain(fields, static field => field.FieldType == typeof(NativeTransfer<int>)
            || field.FieldType == typeof(NativeTransfer<int>?));
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
