using System.Reflection;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeInitializedPayloadHistoryTests
{
    [Fact]
    public void FastPoolPublishesDemandOnlyAfterFullInitializationAndReturnsItOnce()
    {
        using NativePool<int> pool = new(preLease: 8, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Assert.Throws<InvalidOperationException>(() =>
        {
            using Pooled<int> failed = pool.Rent(4, writer =>
            {
                AssertDemand(pool.CaptureDiagnosticSnapshot(), 0, 0);
                writer.Write(1);
                AssertDemand(pool.CaptureDiagnosticSnapshot(), 0, 0);
                throw new InvalidOperationException("Incomplete producer.");
            });
        });
        AssertDemand(pool.GetStatistics(), 0, 0);
        Pooled<int> first = pool.Rent(4, writer =>
        {
            writer.Fill(42);
            AssertDemand(pool.CaptureDiagnosticSnapshot(), 0, 0);
        });
        Pooled<int> second = pool.Rent(6, static writer => writer.Fill(17));
        AssertDemand(pool.GetStatistics(), 40, 40);
        Pooled<int> alias = second;
        second.Dispose();
        bool rejected = false;
        try { alias.Dispose(); }
        catch (NativeAllocationReturnedException) { rejected = true; }
        Assert.True(rejected);
        AssertDemand(pool.GetStatistics(), 16, 40);
        first.Dispose();
        using (Pooled<int> empty = pool.Rent(0, static writer => writer.Fill(0)))
        {
            AssertDemand(pool.GetStatistics(), 0, 40);
        }
        pool.TrimRetainedMemory();
        pool.Dispose();
        AssertDemand(pool.CaptureDiagnosticSnapshot(), 0, 40);
    }

    [Fact]
    public void PreparedPoolNestedPublicationDoesNotCountTheOuterPendingProducer()
    {
        using NativePool<int> pool = new(new NativePoolPreparation(4, 8, 2), new NativeMemoryBudget(512));
        Pooled<int> outer = pool.Rent(3, writer =>
        {
            using Pooled<int> inner = pool.Rent(2, static nested => nested.Fill(17));
            AssertDemand(pool.GetStatistics(), 8, 8);
            writer.Fill(42);
            AssertDemand(pool.CaptureDiagnosticSnapshot(), 8, 8);
        });
        AssertDemand(pool.GetStatistics(), 12, 12);
        outer.Dispose();
        AssertDemand(pool.CaptureDiagnosticSnapshot(), 0, 12);
    }

    [Fact]
    public void RegionReusesItsMonotonicDemandHistoryAfterFailureAndDisposal()
    {
        NativeRegionKernel region = new(preAllocateBytes: 128, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        try
        {
            Assert.Throws<InvalidOperationException>(() => region.LeaseInitialized<int>(3, writer =>
            {
                writer.Write(1);
                AssertDemand(region.GetDiagnosticSnapshot(), 0, 0);
                throw new InvalidOperationException("Injected failure.");
            }));
            Local<int> first = region.LeaseInitialized<int>(3, writer =>
            {
                writer.Fill(42);
                AssertDemand(region.GetDiagnosticSnapshot(), 0, 0);
            });
            Local<byte> second = region.LeaseInitialized<byte>(5, static writer => writer.Fill(17));
            Assert.Equal(42, first.Read(static view => view[2]));
            Assert.Equal(17, second.Read(static view => view[4]));
            AssertDemand(region.GetStatistics(), 17, 17);
            region.Dispose();
            AssertDemand(region.GetDiagnosticSnapshot(), 0, 17);
        }
        finally { region.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FastArenaSeparatesPaddingAndUnpublishedDemandAndRecyclesOnlyTheScopedSubtotal(bool prepared)
    {
        using NativeArena arena = prepared
            ? new(new NativeArenaPreparation(128, 128), new NativeMemoryBudget(512))
            : new(preAllocateBytes: 128, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        _ = arena.Scratch<byte>(3, static writer => writer.Fill(1));
        _ = arena.Scratch<long>(1, static writer => writer.Write(2));
        _ = arena.ScratchScoped<byte>(2, static writer => writer.Fill(3));
        _ = arena.Scratch<int>(1, static writer => writer.Write(4));
        AssertDemand(arena.GetStatistics(), 17, 17);
        Assert.True(arena.GetStatistics().RequestedBytes > 17);
        Assert.Throws<InvalidOperationException>(() => arena.ScratchScoped<int>(2, writer =>
        {
            writer.Write(1);
            AssertDemand(arena.CaptureDiagnosticSnapshot(), 17, 17);
            throw new InvalidOperationException("Failed initialization.");
        }));
        AssertDemand(arena.GetStatistics(), 17, 17);
        arena.RecycleScoped();
        AssertDemand(arena.GetStatistics(), 15, 17);
        _ = arena.ScratchScoped<long>(1, static writer => writer.Write(5));
        _ = arena.Scratch<byte>(1, static writer => writer.Write(6));
        AssertDemand(arena.GetStatistics(), 24, 24);
        arena.RecycleScoped();
        AssertDemand(arena.GetStatistics(), 16, 24);
        arena.Reset();
        AssertDemand(arena.GetStatistics(), 0, 24);
        arena.Dispose();
        AssertDemand(arena.CaptureDiagnosticSnapshot(), 0, 24);
    }

    [Fact]
    public void FastQuadPublishesOneLogicalSumWithoutItsAlignmentOrPendingRanges()
    {
        using NativeArena arena = new(preAllocateBytes: 128, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ArenaLease<int> source = arena.Scratch<int>(1, static writer => writer.Write(42));
        NativeLeaseOperations.InitializeScoped<int, byte, long, byte, int>(source, arena, 1, 1, 3, 2,
            (input, a, b, c, d) =>
            {
                AssertDemand(arena.CaptureDiagnosticSnapshot(), 4, 4);
                a.Fill(1); b.Fill(input[0]); c.Fill(3); d.Fill(4);
                AssertDemand(arena.CaptureDiagnosticSnapshot(), 4, 4);
            }, out ArenaLease<byte> first, out ArenaLease<long> second,
            out ArenaLease<byte> third, out ArenaLease<int> fourth);
        Assert.Equal(42, second.Read(static view => view[0]));
        Assert.Equal(1, first.Read(static view => view[0]));
        Assert.Equal(3, third.Length);
        Assert.Equal(2, fourth.Length);
        AssertDemand(arena.GetStatistics(), 24, 24);
        arena.RecycleScoped();
        AssertDemand(arena.GetStatistics(), 4, 24);
    }

    [Fact]
    public void PreparedOctetRefusalAndProducerFailureCannotIncreaseInitializedPeaks()
    {
        using NativeArena arena = new(new NativeArenaPreparation(16, 32), new NativeMemoryBudget(512));
        ArenaLease<int> source = arena.Scratch<int>(1, static writer => writer.Write(42));
        Assert.False(NativeLeaseOperations.TryInitializeScoped<int, int, int, int, int, int, int, int, int>(
            source, arena, 1, 1, 1, 1, 1, 1, 1, 2,
            static (_, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("Must not run."),
            out _, out _, out _, out _, out _, out _, out _, out _));
        AssertDemand(arena.GetStatistics(), 4, 4);
        bool failed = false;
        try
        {
            NativeLeaseOperations.TryInitializeScoped<int, int, int, int, int, int, int, int, int>(
                source, arena, 1, 1, 1, 1, 1, 1, 1, 1,
                (_, a, _, _, _, _, _, _, _) =>
                {
                    a.Fill(17);
                    AssertDemand(arena.CaptureDiagnosticSnapshot(), 4, 4);
                    throw new InvalidOperationException("Producer failed.");
                }, out _, out _, out _, out _, out _, out _, out _, out _);
        }
        catch (InvalidOperationException exception) when (string.Equals(exception.Message, "Producer failed.", StringComparison.Ordinal))
        {
            failed = true;
        }
        Assert.True(failed);
        AssertDemand(arena.GetStatistics(), 4, 4);
        Assert.True(NativeLeaseOperations.TryInitializeScoped<int, int, int, int, int, int, int, int, int>(
            source, arena, 1, 1, 1, 1, 1, 1, 1, 1,
            static (_, a, b, c, d, e, f, g, h) =>
            { a.Fill(1); b.Fill(2); c.Fill(3); d.Fill(4); e.Fill(5); f.Fill(6); g.Fill(7); h.Fill(8); },
            out _, out _, out _, out _, out _, out _, out _, out _));
        AssertDemand(arena.GetStatistics(), 36, 36);
        arena.RecycleScoped();
        AssertDemand(arena.GetStatistics(), 4, 36);
    }

    [Fact]
    public void SynchronizedPoolTracksOrdinaryScopedStaleAndTerminalDemand()
    {
        using NativeConcurrentPool<int> pool = new(preLease: 8, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Assert.Throws<InvalidOperationException>(() =>
        {
            using ConcurrentPooled<int> failed = pool.Rent(4, writer =>
            {
                writer.Write(1);
                AssertDemand(pool.GetStatistics(), 0, 0);
                throw new InvalidOperationException("Failed producer.");
            });
        });
        using ConcurrentPooled<int> ordinary = pool.Rent(4, static writer => writer.Fill(42));
        using ConcurrentPooled<int> scoped = pool.LeaseScoped(6, writer =>
        {
            writer.Fill(17);
            AssertDemand(pool.CaptureDiagnosticSnapshot(), 16, 16);
        });
        AssertDemand(pool.GetStatistics(), 40, 40);
        pool.RecycleScoped();
        AssertDemand(pool.GetStatistics(), 16, 40);
        scoped.Dispose();
        AssertDemand(pool.GetStatistics(), 16, 40);
        ordinary.Dispose();
        ordinary.Dispose();
        AssertDemand(pool.GetStatistics(), 0, 40);
        pool.ReturnMemoryToNativeMemory();
        pool.LeaseFromMemory();
        using (ConcurrentPooled<int> smaller = pool.Rent(2, static writer => writer.Fill(7)))
        {
            AssertDemand(pool.GetStatistics(), 8, 40);
        }
        pool.Dispose();
        AssertDemand(pool.CaptureDiagnosticSnapshot(), 0, 40);
    }

    [Fact]
    public void SynchronizedReferenceSlotsUseTheirActualNativeLogicalWidth()
    {
        using NativeConcurrentPool<object> pool = new(preLease: 3, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using ConcurrentPooled<object> lease = pool.Rent(3, static writer => writer.Fill(new object()));
        AssertDemand(pool.GetStatistics(), 3L * IntPtr.Size, 3L * IntPtr.Size);
        lease.Dispose();
        AssertDemand(pool.GetStatistics(), 0, 3L * IntPtr.Size);
    }

    [Fact]
    public void SynchronizedFastArenaRecyclingPreservesOrdinaryPublicationsAfterScopedOnes()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: 128, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        _ = arena.Scratch<int>(2, static writer => writer.Fill(1));
        _ = arena.ScratchScoped<int>(3, static writer => writer.Fill(2));
        _ = arena.Scratch<int>(5, static writer => writer.Fill(3));
        AssertDemand(arena.GetStatistics(), 40, 40);
        arena.RecycleScoped();
        AssertDemand(arena.GetStatistics(), 28, 40);
        arena.ReturnMemoryToNativeMemory();
        AssertDemand(arena.GetStatistics(), 0, 40);
        arena.LeaseFromMemory();
        _ = arena.Scratch<int>(1, static writer => writer.Write(4));
        AssertDemand(arena.GetStatistics(), 4, 40);
        arena.Dispose();
        AssertDemand(arena.CaptureDiagnosticSnapshot(), 0, 40);
    }

    [Fact]
    public void SynchronizedRegionPublicationUsesTheSameGaugeWithoutFastViewDuplication()
    {
        NativeOwnerKernel kernel = NativeOwnerKernel.CreateRegion(128, "InitializedDemand", NativeMemoryReturn.ToNativeMemory, false, false);
        try
        {
            _ = kernel.LeaseBumpInitialized<int>(3, sizeof(int), NativeTypeLayout.Alignment<int>(),
                scoped: false, containsReferences: false, static writer => writer.Fill(17));
            AssertDemand(kernel.GetStatistics(), 12, 12);
            _ = kernel.LeaseBumpInitialized<long>(1, sizeof(long), NativeTypeLayout.Alignment<long>(),
                scoped: true, containsReferences: false, static writer => writer.Write(42));
            AssertDemand(kernel.GetDiagnosticSnapshot(), 20, 20);
            kernel.RecycleScoped();
            AssertDemand(kernel.GetStatistics(), 12, 20);
            kernel.Dispose();
            AssertDemand(kernel.GetDiagnosticSnapshot(), 0, 20);
        }
        finally { kernel.Dispose(); }
    }

    [Fact]
    public void TransferSlotPreparationDoesNotPublishDemandAndAliasesReturnOnlyOnce()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: 128, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaTransferBatch<int> batch = arena.CreateTransferBatch<int>(2, 4);
        NativeArenaTransferBatchInitialization<int> initialization = batch.BeginInitialization(0);
        initialization.Values.Fill(42);
        NativeArenaTransferBatchPublication<int> publication = initialization.PreparePublication();
        initialization.Dispose();
        AssertDemand(arena.GetStatistics(), 0, 0);
        NativeArenaTransferBatchLease<int> lease = publication.Publish();
        NativeArenaTransferBatchLease<int> alias = lease;
        publication.Dispose();
        AssertDemand(arena.GetStatistics(), 16, 16);
        lease.Dispose();
        alias.Dispose();
        AssertDemand(arena.GetStatistics(), 0, 16);
        NativeArenaTransferBatchInitialization<int> abandoned = batch.BeginInitialization(1);
        abandoned.Values.Fill(17);
        abandoned.Dispose();
        AssertDemand(arena.GetStatistics(), 0, 16);
    }

    [Fact]
    public async Task ConcurrentPublicationsRecordTheCombinedPeakWithoutLostUpdates()
    {
        using NativeConcurrentPool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        const int count = 8;
        TaskCompletionSource allPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new(false);
        int published = 0;
        Task[] workers = new Task[count];
        try
        {
            foreach (ref Task worker in workers.AsSpan())
            {
                worker = Task.Run(() =>
                {
                    using ConcurrentPooled<int> lease = pool.Rent(4, static writer => writer.Fill(42));
                    if (Interlocked.Increment(ref published) == count) allPublished.SetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Publication workers did not receive their release.");
                });
            }
            await allPublished.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            AssertDemand(pool.GetStatistics(), 128, 128);
            AssertDemand(pool.CaptureDiagnosticSnapshot(), 128, 128);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        }
        AssertDemand(pool.GetStatistics(), 0, 128);
    }

    [Fact]
    public void SharedLogicalPeakIsTheOriginalExtentNotAnAliasOrSliceSum()
    {
        using NativeBuilder<int> builder = new(4);
        builder.Append([1, 2, 3, 4]);
        NativeTransfer<int>? unique = builder.Complete();
        NativeShared<int> owner = NativeShared<int>.Create(ref unique, new(3, 1));
        Assert.True(owner.TrySlice(0, 1, out NativeShared<int> slice, out _));
        Assert.True(owner.TryShare(out NativeShared<int> share, out _));
        Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
        Assert.Equal(16, weak.CaptureSnapshot().InitializedPayloadBytes);
        Assert.Equal(16, weak.CaptureSnapshot().PeakInitializedPayloadBytes);
        owner.Dispose(); slice.Dispose(); share.Dispose();
        Assert.Equal(0, weak.CaptureSnapshot().InitializedPayloadBytes);
        Assert.Equal(16, weak.CaptureSnapshot().PeakInitializedPayloadBytes);
        weak.Dispose();
    }

    [Fact]
    public void CapacityOverflowRejectsSegmentPublicationBeforeAnyConsumer()
    {
        NativeOwnerBackingHistory history = new();
        NativeGenerationOwner owner = new(1, null, 1);
        FieldInfo capacity = typeof(NativeGenerationOwner).GetField("_payloadCapacityBytes", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing capacity bound.");
        NativeSegment segment = NativeSegment.Allocate(64, "DemandOverflow", 1, "test", NativeOwnerLifecycle.Active, false, backingHistory: history);
        try
        {
            capacity.SetValue(owner, long.MaxValue - 32);
            Assert.Throws<OverflowException>(() => owner.AddSegment(segment));
            Assert.Equal(0, owner.SegmentListCapacityForTest());
            Assert.Equal(64, history.Capture().Outstanding);
        }
        finally { segment.FreeNow(); owner.ReleaseToNative(); }
        Assert.Equal(0, history.Capture().Outstanding);
        Assert.Equal(64, history.Capture().Peak);
    }

    [Fact]
    public void WarmCaptureDoesNotAllocateAndProcessMeasurementResetDoesNotResetHistory()
    {
        using NativeConcurrentPool<int> pool = new(preLease: 4, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using ConcurrentPooled<int> lease = pool.Rent(4, static writer => writer.Fill(42));
        AssertDemand(pool.CaptureDiagnosticSnapshot(), 16, 16);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long observed = 0;
        for (int index = 0; index < 1_024; index++)
        {
            observed += pool.CaptureDiagnosticSnapshot().InitializedPayloadBytes;
            observed += pool.GetStatistics().PeakInitializedPayloadBytes;
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(32_768, observed);
        try
        {
            NativeMemoryTestHooks.Reset();
            lease.Dispose();
            AssertDemand(pool.GetStatistics(), 0, 16);
            pool.Dispose();
            AssertDemand(pool.CaptureDiagnosticSnapshot(), 0, 16);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    private static void AssertDemand(NativeOwnerStatistics actual, long current, long peak)
    {
        Assert.Equal(current, actual.InitializedPayloadBytes);
        Assert.Equal(peak, actual.PeakInitializedPayloadBytes);
    }

    private static void AssertDemand(NativeOwnerDiagnosticSnapshot actual, long current, long peak)
    {
        Assert.Equal(current, actual.InitializedPayloadBytes);
        Assert.Equal(peak, actual.PeakInitializedPayloadBytes);
    }
}
