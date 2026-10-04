namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeFastOwnerDiagnosticTests
{
    [Fact]
    public void EmptyPoolLeaseIsARealRecordButNotAPhysicalSegment()
    {
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using (Pooled<int> empty = pool.Rent(0, static writer => writer.Fill(0)))
        {
            NativeOwnerDiagnosticSnapshot snapshot = pool.CaptureDiagnosticSnapshot();
            Assert.Equal(pool.Id, snapshot.OwnerId);
            Assert.Equal(NativeOwnerModel.ThreadConfinedPool, snapshot.Model);
            Assert.Equal(1, snapshot.ActiveRecords);
            Assert.Equal(0, snapshot.RetainedSegmentCount);
            Assert.Equal(0, snapshot.AvailableSegmentCount);
            Assert.Equal(0, pool.GetStatistics().SegmentCount);
            Assert.Equal(0, pool.GetStatistics().RetainedBytes);
            AssertNongenerationalInvariants(snapshot);
        }
        Assert.Equal(0, pool.CaptureDiagnosticSnapshot().ActiveRecords);
        Assert.Equal(0, pool.GetStatistics().AvailableSegmentCount);
    }

    [Fact]
    public void PoolCaptureReadsInitializingStateAndActualReturnedCacheCandidate()
    {
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Assert.Equal(-1, pool.CaptureDiagnosticSnapshot().OrdinaryTraversalIndex);
        using (Pooled<int> lease = pool.Rent(1, writer =>
        {
            NativeOwnerDiagnosticSnapshot initializing = pool.CaptureDiagnosticSnapshot();
            Assert.Equal(1, initializing.ActiveRecords);
            Assert.Equal(1, initializing.RetainedSegmentCount);
            Assert.Equal(0, initializing.AvailableSegmentCount);
            writer.Write(42);
        }))
        {
            Assert.Equal(1, pool.CaptureDiagnosticSnapshot().ActiveRecords);
        }
        NativeOwnerDiagnosticSnapshot returned = pool.CaptureDiagnosticSnapshot();
        Assert.Equal(0, returned.ActiveRecords);
        Assert.Equal(0, returned.OrdinaryTraversalIndex);
        Assert.Equal(1, returned.AvailableSegmentCount);
        using Pooled<int> reused = pool.Rent(1, static writer => writer.Write(7));
        Assert.Equal(-1, pool.CaptureDiagnosticSnapshot().OrdinaryTraversalIndex);
        Assert.Equal(0, pool.CaptureDiagnosticSnapshot().AvailableSegmentCount);
    }

    [Fact]
    public void ArenaCaptureReportsRealLaneIndicesAndEpochTransitionsWithoutInventingRecords()
    {
        using NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeOwnerDiagnosticSnapshot empty = arena.CaptureDiagnosticSnapshot();
        Assert.Equal(NativeOwnerModel.ThreadConfinedArena, empty.Model);
        Assert.Equal(-1, empty.OrdinaryTraversalIndex);
        Assert.Equal(-1, empty.ScopedTraversalIndex);
        Assert.Equal(1, empty.Generation);
        _ = arena.Scratch<int>(1, static writer => writer.Write(42));
        _ = arena.ScratchScoped<long>(1, static writer => writer.Write(7));
        NativeOwnerDiagnosticSnapshot active = arena.CaptureDiagnosticSnapshot();
        Assert.Equal(0, active.OrdinaryTraversalIndex);
        Assert.Equal(0, active.ScopedTraversalIndex);
        Assert.Equal(2, active.RetainedSegmentCount);
        Assert.Equal(0, active.AvailableSegmentCount);
        AssertNoTablesOrRetirement(active);
        arena.RecycleScoped();
        NativeOwnerDiagnosticSnapshot recycled = arena.CaptureDiagnosticSnapshot();
        Assert.True(recycled.ScopeEpoch > active.ScopeEpoch);
        Assert.Equal(active.Generation, recycled.Generation);
        Assert.Equal(1, recycled.AvailableSegmentCount);
        arena.Reset();
        NativeOwnerDiagnosticSnapshot reset = arena.CaptureDiagnosticSnapshot();
        Assert.True(reset.Generation > active.Generation);
        Assert.Equal(2, reset.AvailableSegmentCount);
        Assert.Equal(arena.GetStatistics().AvailableSegmentCount, reset.AvailableSegmentCount);
    }

    [Fact]
    public void RegionCaptureReportsActualIdleReservationAndTerminalCleanup()
    {
        using NativeRegion region = new(64, NativeMemoryReturn.ToNativeMemory);
        NativeOwnerDiagnosticSnapshot reserved = region.CaptureDiagnosticSnapshot();
        Assert.Equal(NativeOwnerModel.ThreadConfinedRegion, reserved.Model);
        Assert.Equal(region.Id, reserved.OwnerId);
        Assert.Equal(1, reserved.RetainedSegmentCount);
        Assert.Equal(1, reserved.AvailableSegmentCount);
        Assert.Equal(1, region.GetStatistics().AvailableSegmentCount);
        Assert.Equal(0, reserved.OrdinaryTraversalIndex);
        AssertNongenerationalInvariants(reserved);
        _ = region.Lease<int>(1, static writer => writer.Write(42));
        NativeOwnerDiagnosticSnapshot active = region.CaptureDiagnosticSnapshot();
        Assert.Equal(0, active.AvailableSegmentCount);
        AssertNoTablesOrRetirement(active);
        region.Dispose();
        NativeOwnerDiagnosticSnapshot disposed = region.CaptureDiagnosticSnapshot();
        Assert.Equal(NativeOwnerLifecycle.Disposed, disposed.Lifecycle);
        Assert.Equal(reserved.OwnerId, disposed.OwnerId);
        Assert.Equal(0, disposed.RetainedSegmentCount);
        Assert.Equal(-1, disposed.OrdinaryTraversalIndex);
    }

    [Fact]
    public void DisposedFastOwnersRemainObservableWithoutGrantingBorrowAuthority()
    {
        using NativePool<int> pool = new(preLease: 4, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena arena = new(preAllocateBytes: 64, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        pool.Dispose();
        arena.Dispose();
        NativeOwnerDiagnosticSnapshot poolSnapshot = pool.CaptureDiagnosticSnapshot();
        NativeOwnerDiagnosticSnapshot arenaSnapshot = arena.CaptureDiagnosticSnapshot();
        Assert.Equal(NativeOwnerLifecycle.Disposed, poolSnapshot.Lifecycle);
        Assert.Equal(NativeOwnerLifecycle.Disposed, arenaSnapshot.Lifecycle);
        Assert.Equal(0, poolSnapshot.RetainedSegmentCount);
        Assert.Equal(0, arenaSnapshot.RetainedSegmentCount);
        Assert.Equal(-1, poolSnapshot.OrdinaryTraversalIndex);
        Assert.Equal(-1, arenaSnapshot.OrdinaryTraversalIndex);
        Assert.Throws<NativeAllocationDisposedException>(() => pool.Rent(1, static writer => writer.Write(1)));
        Assert.Throws<NativeAllocationDisposedException>(() => arena.Scratch<int>(1, static writer => writer.Write(1)));
    }

    [Fact]
    public void RepeatedFastCaptureDoesNotAllocateOrChangeMeasurementEpoch()
    {
        using NativePool<int> pool = new(preLease: 4, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena arena = new(preAllocateBytes: 64, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeRegion region = new(64, NativeMemoryReturn.ToNativeMemory);
        long epoch = pool.CaptureDiagnosticSnapshot().MetricsEpoch;
        _ = arena.CaptureDiagnosticSnapshot();
        _ = region.CaptureDiagnosticSnapshot();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1024; iteration++)
        {
            _ = pool.CaptureDiagnosticSnapshot();
            _ = arena.CaptureDiagnosticSnapshot();
            _ = region.CaptureDiagnosticSnapshot();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(epoch, pool.CaptureDiagnosticSnapshot().MetricsEpoch);
        Assert.Equal(epoch, arena.CaptureDiagnosticSnapshot().MetricsEpoch);
        Assert.Equal(epoch, region.CaptureDiagnosticSnapshot().MetricsEpoch);
    }

    [Fact]
    public void SynchronizedSnapshotsIdentifyTheirActualDifferentModel()
    {
        using NativeConcurrentPool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Assert.Equal(NativeOwnerModel.SynchronizedPool, pool.CaptureDiagnosticSnapshot().Model);
        Assert.Equal(NativeOwnerModel.SynchronizedArena, arena.CaptureDiagnosticSnapshot().Model);
        Assert.Equal(pool.CaptureDiagnosticSnapshot().Model, pool.GetStatistics().Model);
        Assert.Equal(arena.CaptureDiagnosticSnapshot().Model, arena.GetStatistics().Model);
    }

    [Fact]
    public void RegionInitializerFailureRestoresTheActualIdleAvailability()
    {
        using NativeRegion region = new(64, NativeMemoryReturn.ToNativeMemory);
        bool failed = false;
        try
        {
            _ = region.Lease<int>(1, static writer =>
            {
                writer.Write(42);
                throw new InvalidOperationException("producer failed");
            });
        }
        catch (InvalidOperationException exception) when (string.Equals(exception.Message, "producer failed", StringComparison.Ordinal))
        {
            failed = true;
        }
        Assert.True(failed);
        Assert.Equal(1, region.CaptureDiagnosticSnapshot().AvailableSegmentCount);
        Assert.Equal(1, region.GetStatistics().AvailableSegmentCount);
        Assert.Equal(0, region.GetStatistics().RequestedBytes);
    }

    [Fact]
    public void FastSnapshotCallsRetainTheirConstructionThreadBoundary()
    {
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Exception? poolFailure = null;
        Exception? arenaFailure = null;
        Thread thread = new(() =>
        {
            poolFailure = Record.Exception(() => { _ = pool.CaptureDiagnosticSnapshot(); });
            arenaFailure = Record.Exception(() => { _ = arena.CaptureDiagnosticSnapshot(); });
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.IsType<NativeAllocationStateException>(poolFailure);
        Assert.IsType<NativeAllocationStateException>(arenaFailure);
    }

    private static void AssertNongenerationalInvariants(NativeOwnerDiagnosticSnapshot snapshot)
    {
        Assert.Equal(0, snapshot.Generation);
        Assert.Equal(0, snapshot.ScopeEpoch);
        Assert.Equal(-1, snapshot.ScopedTraversalIndex);
        Assert.Equal(0, snapshot.ScopedRecords);
        Assert.Equal(0, snapshot.ReferenceRoots);
        AssertNoRetirement(snapshot);
    }

    private static void AssertNoTablesOrRetirement(NativeOwnerDiagnosticSnapshot snapshot)
    {
        Assert.Equal(0, snapshot.ActiveRecords);
        Assert.Equal(0, snapshot.ScopedRecords);
        Assert.Equal(0, snapshot.ReferenceRoots);
        AssertNoRetirement(snapshot);
    }

    private static void AssertNoRetirement(NativeOwnerDiagnosticSnapshot snapshot)
    {
        Assert.Equal(0, snapshot.RetiredGenerationCount);
        Assert.Equal(0, snapshot.RetiredSegmentCount);
        Assert.Equal(0, snapshot.RetiredBytes);
        Assert.Equal(0, snapshot.QuarantinedGenerationCount);
        Assert.Equal(0, snapshot.QuarantinedSegmentCount);
        Assert.False(snapshot.CurrentGenerationQuarantined);
    }
}
