namespace Supprocom.NativeAllocationManagement.Tests;

// An unbudgeted direct payload's control parks once its shared payload returns,
// and its weak-reference slot is recycled; bindings and observers of earlier
// lifetimes keep their own sharing control and stay stale.
public sealed class NativeSharingReuseTests
{
    [Fact]
    public void AReturnedSharedPayloadControlServesTheNextTransferAndOldBindingsStayStale()
    {
        NativeTransfer<int>? source = NativeTransfer<int>.Create(4, 7);
        object payloadControl = source.Value.ControlForTest!;
        NativeTransfer<int> staleSource = source.Value;
        NativeShared<int> first = NativeShared<int>.Create(ref source, new(2, 1));
        Assert.Null(source);
        Assert.Throws<InvalidOperationException>(() => staleSource.Read(static view => view[0]));
        Assert.True(first.TryShare(out NativeShared<int> alias, out _));
        long firstId = first.Id;
        alias.Dispose();
        first.Dispose();
        NativeSharingStatistics released = first.CaptureSnapshot();
        Assert.True(released.PayloadReleased);
        Assert.Equal(1, released.PayloadReturnCount);

        NativeTransfer<int> next = NativeTransfer<int>.Create(4, 9);
        Assert.Same(payloadControl, next.ControlForTest);
        Assert.Equal(9, next.Read(static view => view[3]));
        Assert.Throws<InvalidOperationException>(() => first.Read(static view => view[0]));
        Assert.Throws<InvalidOperationException>(first.Dispose);
        Assert.Throws<InvalidOperationException>(() => alias.TryShare(out _, out _));
        Assert.Throws<InvalidOperationException>(() => staleSource.Read(static view => view[0]));
        Assert.Equal(firstId, first.Id);
        Assert.True(first.TryCompletePayloadReturn());
        Assert.True(first.CaptureSnapshot().PayloadReleased);
        next.Dispose();
    }

    [Fact]
    public void ARecycledWeakReferenceNeverUpgradesAnEarlierObserver()
    {
        NativeTransfer<int>? source = NativeTransfer<int>.Create(2, 1);
        NativeShared<int> first = NativeShared<int>.Create(ref source, new(1, 1));
        Assert.True(first.TryDowngrade(out NativeWeak<int> observer, out _));
        first.Dispose();
        Assert.True(observer.IsExpired);

        NativeTransfer<int>? nextSource = NativeTransfer<int>.Create(2, 5);
        NativeShared<int> second = NativeShared<int>.Create(ref nextSource, new(2, 1));
        Assert.True(second.TryDowngrade(out NativeWeak<int> secondObserver, out _));
        Assert.False(observer.TryUpgrade(out _, out NativeSharingExhaustionReason reason));
        Assert.Equal(NativeSharingExhaustionReason.ExpiredPayload, reason);
        Assert.True(observer.IsExpired);
        Assert.True(secondObserver.TryUpgrade(out NativeShared<int> upgraded, out _));
        Assert.Equal(5, upgraded.Read(static view => view[1]));
        upgraded.Dispose();
        second.Dispose();
        Assert.True(secondObserver.IsExpired);
        secondObserver.Dispose();
        observer.Dispose();
    }

    [Fact]
    public void SharingASourceWithAnActiveCallbackStillRetiresIt()
    {
        NativeTransfer<int>? source = NativeTransfer<int>.Create(2, 3);
        NativeTransfer<int> observed = source.Value;
        InvalidOperationException? failure = null;
        observed.Access(_ =>
        {
            NativeTransfer<int>? inside = observed;
            failure = Assert.Throws<InvalidOperationException>(() => NativeShared<int>.Create(ref inside, new(1, 0)));
            Assert.Null(inside);
        });
        Assert.NotNull(failure);
        Assert.ThrowsAny<InvalidOperationException>(() => observed.Read(static view => view[0]));
        Assert.True(observed.TryCompletePayloadReturn());
    }

    [Fact]
    public void ManySharedLifetimesOnOneThreadKeepTheirOwnValues()
    {
        for (int iteration = 0; iteration < 2000; iteration++)
        {
            NativeTransfer<int>? source = NativeTransfer<int>.Create(1 + (iteration & 15), iteration);
            NativeShared<int> shared = NativeShared<int>.Create(ref source, new(2, iteration & 1));
            Assert.Equal(iteration, shared.Read(static view => view[^1]));
            if (shared.TryShare(out NativeShared<int> alias, out _))
            {
                shared.Dispose();
                Assert.Equal(iteration, alias.Read(static view => view[0]));
                alias.Dispose();
            }
            else
            {
                shared.Dispose();
            }
        }
    }
}
