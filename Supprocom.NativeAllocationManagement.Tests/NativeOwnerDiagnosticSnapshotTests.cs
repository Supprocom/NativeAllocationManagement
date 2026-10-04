using System.Reflection;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeOwnerDiagnosticSnapshotTests
{
    [Fact]
    public void MissingGenerationHasExplicitTraversalSentinelsAndNoStorage()
    {
        using NativeConcurrentArena arena = new(
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory,
            doNotLeaseOnDeclaration: true);
        AssertMissingGeneration(arena.CaptureDiagnosticSnapshot(), NativeOwnerLifecycle.Unleased);

        arena.LeaseFromMemory();
        NativeOwnerDiagnosticSnapshot active = arena.CaptureDiagnosticSnapshot();
        Assert.Equal(NativeOwnerLifecycle.Active, active.Lifecycle);
        Assert.Equal(0, active.Generation);
        Assert.Equal(0, active.OrdinaryTraversalIndex);
        Assert.Equal(-1, active.ScopedTraversalIndex);

        arena.ReturnMemoryToNativeMemory();
        AssertMissingGeneration(arena.CaptureDiagnosticSnapshot(), NativeOwnerLifecycle.Returned);
        arena.Dispose();
        AssertMissingGeneration(arena.CaptureDiagnosticSnapshot(), NativeOwnerLifecycle.Disposed);
    }

    [Fact]
    public void PoolSnapshotTracksActualRecordsRootsAndScopedEpoch()
    {
        using NativeConcurrentPool<string> pool = new(
            preLease: 2,
            returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeOwnerDiagnosticSnapshot reserved = pool.CaptureDiagnosticSnapshot();
        Assert.Equal(1, reserved.RetainedSegmentCount);
        Assert.Equal(1, reserved.AvailableSegmentCount);
        Assert.Equal(0, reserved.ActiveRecords);
        Assert.Equal(0, reserved.ReferenceRoots);

        ConcurrentPooled<string> ordinary = pool.Rent(2, static writer => writer.Fill("ordinary"));
        ConcurrentPooled<string> scoped = pool.LeaseScoped(3, static writer => writer.Fill("scoped"));
        Assert.Equal("scoped", scoped.Read(static view => view[0]));
        NativeOwnerDiagnosticSnapshot live = pool.CaptureDiagnosticSnapshot();
        Assert.Equal(2, live.ActiveRecords);
        Assert.Equal(1, live.ScopedRecords);
        Assert.Equal(5, live.ReferenceRoots);
        Assert.Equal(2, live.RetainedSegmentCount);
        Assert.Equal(0, live.AvailableSegmentCount);
        Assert.Equal(NativeMemoryTestHooks.CurrentMetricsEpoch, live.MetricsEpoch);
        Assert.Equal(pool.GetStatistics().RetiredBytes, live.RetiredBytes);

        pool.RecycleScoped();
        NativeOwnerDiagnosticSnapshot recycled = pool.CaptureDiagnosticSnapshot();
        Assert.True(recycled.ScopeEpoch > live.ScopeEpoch);
        Assert.Equal(live.Generation, recycled.Generation);
        Assert.Equal(1, recycled.ActiveRecords);
        Assert.Equal(0, recycled.ScopedRecords);
        Assert.Equal(2, recycled.ReferenceRoots);
        Assert.Equal("ordinary", ordinary.Read(static view => view[0]));
        ordinary.Dispose();

        _ = pool.TrimRetainedMemory();
        NativeOwnerDiagnosticSnapshot trimmed = pool.CaptureDiagnosticSnapshot();
        Assert.Equal(0, trimmed.RetainedSegmentCount);
        Assert.Equal(0, trimmed.AvailableSegmentCount);
        Assert.Equal(0, trimmed.ActiveRecords);
        Assert.Equal(0, trimmed.ReferenceRoots);
    }

    [Fact]
    public void ArenaSnapshotDistinguishesTraversalAndGroupedScopedRecords()
    {
        using NativeConcurrentArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        _ = arena.Scratch<byte>(4_096, static writer => writer.Fill(1));
        _ = arena.Scratch<byte>(4_096, static writer => writer.Fill(2));
        NativeOwnerDiagnosticSnapshot ordinary = arena.CaptureDiagnosticSnapshot();
        Assert.True(ordinary.OrdinaryTraversalIndex > 0);
        Assert.Equal(2, ordinary.RetainedSegmentCount);
        Assert.Equal(0, ordinary.ActiveRecords);
        Assert.Equal(0, ordinary.ScopedRecords);
        Assert.Equal(0, ordinary.ReferenceRoots);

        _ = arena.ScratchScoped<byte>(1, static writer => writer.Fill(3));
        NativeOwnerDiagnosticSnapshot scoped = arena.CaptureDiagnosticSnapshot();
        Assert.True(scoped.ScopedTraversalIndex >= 0);
        Assert.Equal(ordinary.ScopeEpoch, scoped.ScopeEpoch);
        Assert.Equal(1, scoped.ActiveRecords);
        Assert.Equal(1, scoped.ScopedRecords);
        arena.RecycleScoped();
        NativeOwnerDiagnosticSnapshot recycled = arena.CaptureDiagnosticSnapshot();
        Assert.True(recycled.ScopeEpoch > scoped.ScopeEpoch);
        Assert.Equal(0, recycled.ActiveRecords);
        Assert.Equal(0, recycled.ScopedRecords);
        Assert.Equal(scoped.RetainedSegmentCount, recycled.RetainedSegmentCount);
    }

    [Fact]
    public void MaterializedCompositeRecordsAreCountedOnceAndScopedRecordsExpire()
    {
        using NativeConcurrentArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentArenaLease<int> ordinary = arena.Scratch<int>(2, static writer => writer.Fill(1));
        ConcurrentArenaLease<long> scoped = arena.ScratchScoped<long>(2, static writer => writer.Fill(2));
        Assert.Equal(1, arena.CaptureDiagnosticSnapshot().ActiveRecords);
        NativeLeaseOperations.Access(ordinary, scoped, static (_, _) => { });
        NativeOwnerDiagnosticSnapshot materialized = arena.CaptureDiagnosticSnapshot();
        Assert.Equal(3, materialized.ActiveRecords);
        Assert.Equal(2, materialized.ScopedRecords);
        NativeLeaseOperations.Access(ordinary, scoped, static (_, _) => { });
        Assert.Equal(materialized.ActiveRecords, arena.CaptureDiagnosticSnapshot().ActiveRecords);
        arena.RecycleScoped();
        NativeOwnerDiagnosticSnapshot recycled = arena.CaptureDiagnosticSnapshot();
        Assert.Equal(1, recycled.ActiveRecords);
        Assert.Equal(0, recycled.ScopedRecords);
        arena.ReturnMemoryToNativeMemory();
        Assert.Equal(0, arena.CaptureDiagnosticSnapshot().ActiveRecords);
    }

    [Fact]
    public void SnapshotContainsOnlyValuesAndAllocatesNothingOnRepeatedCapture()
    {
        Assert.All(
            typeof(NativeOwnerDiagnosticSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.True(property.PropertyType.IsValueType));
        using NativeConcurrentPool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        _ = pool.CaptureDiagnosticSnapshot();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_024; iteration++)
        {
            _ = pool.CaptureDiagnosticSnapshot();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    private static void AssertMissingGeneration(NativeOwnerDiagnosticSnapshot snapshot, NativeOwnerLifecycle lifecycle)
    {
        Assert.Equal(lifecycle, snapshot.Lifecycle);
        Assert.Equal(0, snapshot.ScopeEpoch);
        Assert.Equal(-1, snapshot.OrdinaryTraversalIndex);
        Assert.Equal(-1, snapshot.ScopedTraversalIndex);
        Assert.Equal(0, snapshot.ActiveRecords);
        Assert.Equal(0, snapshot.ScopedRecords);
        Assert.Equal(0, snapshot.ReferenceRoots);
        Assert.Equal(0, snapshot.RetainedSegmentCount);
        Assert.Equal(0, snapshot.AvailableSegmentCount);
        Assert.Equal(0, snapshot.RetiredGenerationCount);
        Assert.Equal(0, snapshot.RetiredSegmentCount);
        Assert.Equal(0, snapshot.RetiredBytes);
        Assert.Equal(0, snapshot.QuarantinedGenerationCount);
        Assert.Equal(0, snapshot.QuarantinedSegmentCount);
        Assert.False(snapshot.CurrentGenerationQuarantined);
    }
}
