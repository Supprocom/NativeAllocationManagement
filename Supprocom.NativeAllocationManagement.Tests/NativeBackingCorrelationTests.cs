using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeBackingCorrelationTests
{
    [Fact]
    public void DirectBlockOrdinalsSurviveReplacementMovementAndReturn()
    {
        NativeMemoryBudget budget = new(256, traceCapacity: 32);
        using NativeWorkspace<byte> workspace = new(budget, 8);
        using NativeBuilder<int> builder = new(budget, 0);
        long workspaceId = workspace.Id;
        long builderId = builder.Id;
        Assert.NotEqual(workspaceId, builderId);
        Assert.True(builder.TryEnsureCapacity(4));
        builder.Append(42);
        Assert.True(builder.TryEnsureCapacity(12));
        workspace.Dispose();
        NativeTransfer<int>? source = builder.Complete();
        NativeTransfer<int> moved = NativeTransfer<int>.Move(ref source);
        try
        {
            Assert.Null(source);
            Assert.Equal(builderId, moved.Id);
            Assert.Equal(42, moved.Read(static view => view[0]));
        }
        finally { moved.Dispose(); }

        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[32];
        int count = budget.CopyTraceTo(events);
        int physicalEvents = 0;
        foreach (ref readonly NativeMemoryTraceEvent entry in events[..count])
        {
            if (entry.Kind is NativeMemoryTraceKind.Allocated or NativeMemoryTraceKind.Reallocated or NativeMemoryTraceKind.Released)
            {
                Assert.Equal(1L, entry.AllocationOrdinal);
                Assert.True(entry.OwnerId == workspaceId || entry.OwnerId == builderId);
                Assert.NotEqual((nuint)0, entry.RequestedBytes);
                physicalEvents++;
            }
        }
        Assert.Equal(5, physicalEvents);
        NativeMemoryBudgetStatistics statistics = budget.CaptureStatistics();
        Assert.Equal(0, statistics.CommittedBytes);
        Assert.Equal(0, statistics.ReservedBytes);
        Assert.Equal(2, statistics.AllocationCount);
        Assert.Equal(2, statistics.ReallocationCount);
        Assert.Equal(2, statistics.FreeCount);
        Assert.Equal(0, statistics.DroppedTraceEventCount);
    }

    [Fact]
    public void EmptyFailedAndRefusedAcquisitionsHaveNoInventedBackingIdentity()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeMemoryBudget budget = new(64, traceCapacity: 16);
            Assert.Throws<ArgumentOutOfRangeException>(() => new NativeWorkspace<byte>(budget, 0));
            using NativeBuilder<int> builder = new(budget, 0);
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => new NativeWorkspace<byte>(budget, 1));
            Assert.False(builder.TryEnsureCapacity(17));
            Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[16];
            Assert.Equal(3, budget.CopyTraceTo(events));
            Assert.Equal(NativeMemoryTraceKind.Admitted, events[0].Kind);
            Assert.Equal(NativeMemoryTraceKind.AcquisitionFailed, events[1].Kind);
            Assert.Equal(NativeMemoryTraceKind.Rejected, events[2].Kind);
            foreach (ref readonly NativeMemoryTraceEvent entry in events[..3]) Assert.Null(entry.AllocationOrdinal);
            NativeMemoryBudgetStatistics statistics = budget.CaptureStatistics();
            Assert.Equal(0, statistics.CommittedBytes);
            Assert.Equal(0, statistics.ReservedBytes);
            Assert.Equal(0, statistics.AllocationCount);
            Assert.Equal(0, statistics.FreeCount);
            Assert.Equal(1, statistics.FailedAllocationCount);
            Assert.Equal(1, statistics.RejectedAllocationCount);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void RegionAppendOrdinalsReconcileUnchangedBackingAndPhysicalCleanup(int prefix)
    {
        NativeMemoryBudget budget = new(65_536, traceCapacity: 16);
        long ownerId;
        long retained;
        using (NativeRegion region = new(budget, (nuint)(prefix + 1), NativeMemoryReturn.ToNativeMemory))
        {
            ownerId = region.Id;
            Local<byte> first = region.Lease<byte>(prefix, static writer => writer.Fill(17));
            Local<long> second = region.Lease<long>(1, static writer => writer.Write(42));
            _ = region.Lease<byte>(16_384, static writer => writer.Fill(7));
            Assert.Equal(17, first.Read(static view => view[0]));
            Assert.Equal(42, second.Read(static view => view[0]));
            retained = region.GetStatistics().RetainedBytes;
            Assert.Equal(3, region.GetStatistics().SegmentCount);
        }
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[16];
        Assert.Equal(9, budget.CopyTraceTo(events));
        long acquiredBytes = 0;
        for (int index = 0; index < 3; index++)
        {
            NativeMemoryTraceEvent acquired = events[index * 2 + 1];
            NativeMemoryTraceEvent released = events[index + 6];
            Assert.Equal(NativeMemoryTraceKind.Allocated, acquired.Kind);
            Assert.Equal(NativeMemoryTraceKind.Released, released.Kind);
            Assert.Equal(index + 1L, acquired.AllocationOrdinal);
            Assert.Equal(acquired.AllocationOrdinal, released.AllocationOrdinal);
            Assert.Equal(acquired.RequestedBytes, released.RequestedBytes);
            Assert.Equal(ownerId, acquired.OwnerId);
            Assert.Equal(ownerId, released.OwnerId);
            acquiredBytes += checked((long)acquired.RequestedBytes);
        }
        Assert.Equal(NativeAlignedAllocation.GetBackingByteLength((nuint)(64 + prefix + 1)), events[1].RequestedBytes);
        Assert.Equal(retained, acquiredBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(3, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(3, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void RegionOrdinalExhaustionPrecedesAdmissionAndPreservesEarlierOutput()
    {
        NativeMemoryBudget budget = new(8192, traceCapacity: 16);
        NativeRegionKernel kernel = new(1, NativeMemoryReturn.ToNativeMemory, budget);
        try
        {
            Local<byte> first = kernel.LeaseInitialized<byte>(1, static writer => writer.Write(11));
            FieldInfo field = typeof(NativeRegionKernel).GetField("_segmentCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(kernel, int.MaxValue);
            NativeMemoryBudgetStatistics before = budget.CaptureStatistics();
            Assert.Throws<OverflowException>(() => { _ = kernel.LeaseInitialized<int>(1, static writer => writer.Write(42)); });
            Assert.Equal(before, budget.CaptureStatistics());
            Assert.Equal(11, first.Read(static view => view[0]));
        }
        finally { kernel.Dispose(); }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[16];
        Assert.Equal(3, budget.CopyTraceTo(events));
        Assert.Equal(1L, events[2].AllocationOrdinal);
        Assert.Equal(NativeMemoryTraceKind.Released, events[2].Kind);
    }

    [Fact]
    public void EmergencyRegionCleanupKeepsTheOriginalBackingOrdinal()
    {
        NativeMemoryBudget budget = new(8192, traceCapacity: 16);
        long ownerId = AbandonRegionBacking(budget);
        for (int attempt = 0; attempt < 8 && budget.CaptureStatistics().CommittedBytes != 0; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[16];
        Assert.Equal(3, budget.CopyTraceTo(events));
        Assert.Equal(ownerId, events[2].OwnerId);
        Assert.Equal(1L, events[1].AllocationOrdinal);
        Assert.Equal(events[1].AllocationOrdinal, events[2].AllocationOrdinal);
        Assert.Equal(NativeMemoryTraceKind.Released, events[2].Kind);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long AbandonRegionBacking(NativeMemoryBudget budget)
    {
        using NativeRegion region = new(budget, 8, NativeMemoryReturn.ToGarbageCollector);
        _ = region.Lease<byte>(1, static writer => writer.Write(17));
        return region.Id;
    }
}
