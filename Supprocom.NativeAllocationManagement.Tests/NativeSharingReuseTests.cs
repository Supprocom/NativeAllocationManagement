using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

// An unbudgeted direct payload's control parks once its shared payload returns,
// and its weak-reference slot is recycled; bindings and observers of earlier
// lifetimes keep their own sharing control and stay stale. A sharing control
// with no observer or reader left parks too, and serves the thread's next
// share with the same preparation.
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

    [Fact]
    public void ASharingControlServesTheNextShareAndEarlierBindingsReportTheirReturn()
    {
        NativeShared<int> first = Share(4, 7, new(2, 1));
        Assert.True(first.TryShare(out NativeShared<int> alias, out _));
        Assert.True(first.TryDowngrade(out NativeWeak<int> observer, out _));
        long firstId = first.Id;
        alias.Dispose();
        observer.Dispose();
        first.Dispose();

        NativeShared<int> second = Share(4, 9, new(2, 1));
        Assert.Same(ControlOf(first), ControlOf(second));
        Assert.NotEqual(firstId, second.Id);
        NativeSharingStatistics fresh = second.CaptureSnapshot();
        Assert.Equal(0, fresh.ShareCount);
        Assert.Equal(0, fresh.WeakCreationCount);
        Assert.Equal(0, fresh.PayloadReturnCount);
        Assert.Equal(1, fresh.StrongBindingCount);
        Assert.False(fresh.Expired);

        Assert.Throws<InvalidOperationException>(() => first.Read(static view => view[0]));
        Assert.Throws<InvalidOperationException>(() => alias.Read(static view => view[0]));
        Assert.Throws<InvalidOperationException>(first.Dispose);
        Assert.Throws<InvalidOperationException>(() => first.TryShare(out _, out _));
        Assert.Throws<InvalidOperationException>(() => first.TryDowngrade(out _, out _));
        Assert.Throws<InvalidOperationException>(() => observer.TryUpgrade(out _, out _));
        NativeSharingStatistics earlier = first.CaptureSnapshot();
        Assert.True(earlier.Expired);
        Assert.True(earlier.PayloadReleased);
        Assert.Equal(1, earlier.PayloadReturnCount);
        Assert.Equal(0, earlier.StrongBindingCount);
        Assert.Equal(0, earlier.OwnedBackingBytes);
        Assert.True(first.TryCompletePayloadReturn());
        Assert.True(observer.TryCompletePayloadReturn());
        Assert.Equal(9, second.Read(static view => view[3]));
        second.Dispose();
    }

    [Fact]
    public void ASharingControlWithALiveObserverIsNotReused()
    {
        NativeShared<int> first = Share(2, 1, new(1, 1));
        Assert.True(first.TryDowngrade(out NativeWeak<int> observer, out _));
        first.Dispose();
        Assert.True(observer.IsExpired);

        NativeShared<int> second = Share(2, 5, new(1, 1));
        Assert.NotSame(ControlOf(first), ControlOf(second));
        Assert.True(observer.IsExpired);
        Assert.False(observer.TryUpgrade(out _, out NativeSharingExhaustionReason reason));
        Assert.Equal(NativeSharingExhaustionReason.ExpiredPayload, reason);
        Assert.Equal(5, second.Read(static view => view[0]));
        second.Dispose();
        observer.Dispose();
    }

    [Fact]
    public void OnlyTheSamePreparationReusesAControl()
    {
        NativeShared<int> first = Share(2, 1, new(1, 0));
        first.Dispose();
        NativeShared<int> other = Share(2, 2, new(3, 0));
        Assert.NotSame(ControlOf(first), ControlOf(other));
        NativeShared<int> same = Share(2, 3, new(1, 0));
        Assert.Same(ControlOf(first), ControlOf(same));
        Assert.Equal(new NativeSharingPreparation(3, 0), other.CaptureSnapshot().Preparation);
        other.Dispose();
        same.Dispose();
    }

    private static NativeShared<int> Share(int length, int value, NativeSharingPreparation preparation)
    {
        NativeTransfer<int>? source = NativeTransfer<int>.Create(length, value);
        return NativeShared<int>.Create(ref source, preparation);
    }

    private static object ControlOf(NativeShared<int> binding) =>
        typeof(NativeShared<int>).GetField("_control", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(binding)!;
}
