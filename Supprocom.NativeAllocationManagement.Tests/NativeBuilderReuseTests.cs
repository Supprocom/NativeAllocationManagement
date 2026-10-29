namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeBuilderReuseTests
{
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861", Justification = "One-shot expected arrays stay local to the assertions.")]
    public void ReturnedControlServesTheNextBuilderAndKeepsStaleValuesReturned()
    {
        NativeMemoryTestHooks.Reset();
        NativeTransfer<int> first = Build([1, 2, 3], preLease: 8);
        object control = first.ControlForTest!;
        long firstId = first.Id;
        first.Dispose();
        // The block is parked with its control, still counted.
        NativeMemoryTestMetrics afterFirst = NativeMemoryTestHooks.Snapshot();
        Assert.Equal(1, afterFirst.AllocationCount);
        KeptStorage.AssertBalanced<int>(afterFirst);

        NativeTransfer<int> second = Build([4, 5], preLease: 8);
        Assert.Same(control, second.ControlForTest);
        Assert.NotEqual(firstId, second.Id);
        Assert.Equal(new[] { 4, 5 }, second.Read(static view => view.AsSpan().ToArray()));
        Assert.Equal(0, second.CaptureSnapshot().MoveCount);

        Assert.Throws<InvalidOperationException>(() => first.Read(static view => view[0]));
        Assert.Throws<InvalidOperationException>(first.Dispose);
        NativeTransferStatistics stale = first.CaptureSnapshot();
        Assert.Equal(NativeTransferLifecycle.Returned, stale.Lifecycle);
        Assert.False(stale.BindingIsActive);
        Assert.Equal(1, stale.PayloadReturnCount);
        Assert.Equal(new[] { 4, 5 }, second.Read(static view => view.AsSpan().ToArray()));

        second.Dispose();
        // Reusing the parked block recorded no allocation.
        NativeMemoryTestMetrics end = NativeMemoryTestHooks.Snapshot();
        Assert.Equal(1, end.AllocationCount);
        KeptStorage.AssertBalanced<int>(end);
    }

    [Fact]
    public void BuilderDisposedBeforeCompletionAlsoParksItsControl()
    {
        NativeBuilder<int> abandoned = new(preLease: 4);
        abandoned.Append(9);
        abandoned.Dispose();
        NativeTransfer<int> next = Build([7], preLease: 4);
        Assert.Equal(7, next.Read(static view => view[0]));
        next.Dispose();
    }

    [Fact]
    public void BudgetedBuildersNeverReuseAParkedControl()
    {
        NativeTransfer<int> unbudgeted = Build([1], preLease: 4);
        object parked = unbudgeted.ControlForTest!;
        unbudgeted.Dispose();
        NativeMemoryBudget budget = new(1024);
        using NativeBuilder<int> budgeted = new(budget, 4);
        budgeted.Append(2);
        NativeTransfer<int> transfer = budgeted.Complete();
        Assert.NotSame(parked, transfer.ControlForTest);
        transfer.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void OversizedParkedBlockIsNotPinnedByASmallRequest()
    {
        NativeTransfer<int> large = Build(new int[4_000], preLease: 4_000);
        large.Dispose();
        NativeTransfer<int> small = Build([3], preLease: 1);
        Assert.InRange(small.Capacity, 1, 256);
        small.Dispose();
    }

    [Fact]
    public void SeveralReturnedTransfersAreAllReused()
    {
        NativeTransfer<int>[] first = [NativeTransfer<int>.Create(4, 1), NativeTransfer<int>.Create(4, 2), NativeTransfer<int>.Create(4, 3)];
        object[] controls = [.. first.Select(static transfer => transfer.ControlForTest!)];
        foreach (NativeTransfer<int> transfer in first)
        {
            transfer.Dispose();
        }

        NativeTransfer<int>[] second = [NativeTransfer<int>.Create(4, 4), NativeTransfer<int>.Create(4, 5), NativeTransfer<int>.Create(4, 6)];
        Assert.All(second, transfer => Assert.Contains(transfer.ControlForTest, controls));
        Assert.Equal(3, second.Select(static transfer => transfer.ControlForTest).Distinct().Count());
        foreach (NativeTransfer<int> transfer in second)
        {
            transfer.Dispose();
        }
    }

    [Fact]
    public void ParkedBlocksStayWithinThePerThreadByteCeiling()
    {
        NativeMemoryTestHooks.Reset();
        NativeBuilderControl<byte>.DepotDisabledForTest = true;
        try
        {
            NativeTransfer<byte> large = NativeTransfer<byte>.Create(1000 * 1024, 1);
            NativeTransfer<byte> small = NativeTransfer<byte>.Create(64 * 1024, 2);
            object smallControl = small.ControlForTest!;
            large.Dispose();
            small.Dispose();
            // The large block parked first; the small one would exceed 1 MiB, so it was freed.
            NativeTransfer<byte> next = NativeTransfer<byte>.Create(64 * 1024, 3);
            Assert.NotSame(smallControl, next.ControlForTest);
            next.Dispose();
            KeptStorage.AssertBalanced<byte>(NativeMemoryTestHooks.Snapshot());
        }
        finally
        {
            NativeBuilderControl<byte>.DepotDisabledForTest = false;
        }
    }

    [Fact]
    public void AFullStackTravelsToAnotherThreadAsOneMagazine()
    {
        NativeMemoryTestHooks.Reset();
        // An element type no other test uses keeps the shared depot to this test.
        NativeTransfer<MagazineProbe>[] first = new NativeTransfer<MagazineProbe>[9];
        for (int index = 0; index < first.Length; index++)
        {
            first[index] = NativeTransfer<MagazineProbe>.Create(2, new MagazineProbe(index));
        }

        HashSet<object> controls = [.. first.Select(static transfer => transfer.ControlForTest!)];
        NativeThreadState worker = TestThread.Run(() =>
        {
            // Eight fill the stack; the ninth sends those eight to the depot.
            foreach (NativeTransfer<MagazineProbe> transfer in first)
            {
                transfer.Dispose();
            }
        });

        NativeTransfer<MagazineProbe>[] second = new NativeTransfer<MagazineProbe>[8];
        for (int index = 0; index < second.Length; index++)
        {
            second[index] = NativeTransfer<MagazineProbe>.Create(2, new MagazineProbe(100 + index));
        }

        Assert.Equal(8, second.Count(transfer => controls.Contains(transfer.ControlForTest!)));
        for (int index = 0; index < second.Length; index++)
        {
            Assert.Equal(100 + index, second[index].Read(static view => view[1].Value));
            Assert.ThrowsAny<InvalidOperationException>(() => first[index].Read(static view => view[0].Value));
            second[index].Dispose();
        }

        // Every block is freed or kept: the ninth waits on the ended thread
        // until its release.
        NativeEndedThreads.ReleaseEndedForTest();
        KeptStorage.AssertBalanced<MagazineProbe>(NativeMemoryTestHooks.Snapshot());
        // The ended thread's state stays reachable until its storage is
        // released: a collected state leaves its parked blocks to their
        // finalizers, which free them later.
        GC.KeepAlive(worker);
    }

    private readonly record struct MagazineProbe(int Value);

    // The depot frees at a full collection the blocks no thread reused since
    // the previous one: one trim leaves a spilled magazine, unless a
    // collection's own trim ran in between, and a second frees it.
    [Fact]
    public void FullCollectionsFreeDepotBlocksNoThreadReused()
    {
        NativeMemoryTestHooks.Reset();
        NativeThreadState worker = SpillOneMagazine<TrimProbe>();
        Assert.Equal(8, KeptStorage.InCurrentEpoch<TrimProbe>().Count);
        int collections = GC.CollectionCount(2);
        NativeBuilderControl<TrimProbe>.TrimShared(highLoad: false);
        if (GC.CollectionCount(2) == collections)
        {
            Assert.Equal(8, KeptStorage.InCurrentEpoch<TrimProbe>().Count);
        }

        NativeBuilderControl<TrimProbe>.TrimShared(highLoad: false);
        Assert.Equal(0, KeptStorage.InCurrentEpoch<TrimProbe>().Count);
        KeptStorage.AssertBalanced<TrimProbe>(NativeMemoryTestHooks.Snapshot());
        GC.KeepAlive(worker);
    }

    // A magazine taken back and spilled again after a trim is stamped anew, so
    // the next trim leaves it.
    [Fact]
    public void ADepotBlockReusedSinceTheLastCollectionStays()
    {
        NativeMemoryTestHooks.Reset();
        NativeThreadState first = SpillOneMagazine<ReuseProbe>();
        NativeBuilderControl<ReuseProbe>.TrimShared(highLoad: false);
        NativeThreadState second = SpillOneMagazine<ReuseProbe>();
        int collections = GC.CollectionCount(2);
        NativeBuilderControl<ReuseProbe>.TrimShared(highLoad: false);
        if (GC.CollectionCount(2) == collections)
        {
            Assert.Equal(8, KeptStorage.InCurrentEpoch<ReuseProbe>().Count);
        }

        NativeBuilderControl<ReuseProbe>.TrimShared(highLoad: false);
        Assert.Equal(0, KeptStorage.InCurrentEpoch<ReuseProbe>().Count);
        KeptStorage.AssertBalanced<ReuseProbe>(NativeMemoryTestHooks.Snapshot());
        GC.KeepAlive(first);
        GC.KeepAlive(second);
    }

    // High memory load frees every block the shared partitions hold.
    [Fact]
    public void HighMemoryLoadFreesEverySharedBlock()
    {
        NativeMemoryTestHooks.Reset();
        NativeTransfer<LoadProbe>[] transfers = new NativeTransfer<LoadProbe>[16];
        for (int index = 0; index < transfers.Length; index++)
        {
            transfers[index] = NativeTransfer<LoadProbe>.Create(4, new LoadProbe(index));
        }

        NativeThreadState worker = TestThread.Run(() =>
        {
            // Without the depot, what the stack cannot hold goes to the shared
            // partitions.
            NativeBuilderControl<LoadProbe>.DepotDisabledForTest = true;
            foreach (NativeTransfer<LoadProbe> transfer in transfers)
            {
                transfer.Dispose();
            }
        });
        NativeEndedThreads.ReleaseEndedForTest();
        Assert.Equal(8, KeptStorage.InCurrentEpoch<LoadProbe>().Count);
        NativeBuilderControl<LoadProbe>.TrimShared(highLoad: true);
        Assert.Equal(0, KeptStorage.InCurrentEpoch<LoadProbe>().Count);
        KeptStorage.AssertBalanced<LoadProbe>(NativeMemoryTestHooks.Snapshot());
        GC.KeepAlive(worker);
    }

    // Nine transfers of the probe type are disposed on a thread that ends:
    // eight fill its stack and the ninth sends them to the depot as one
    // magazine; the ninth itself is freed with the ended thread's storage. The
    // returned state stays reachable until the caller is done.
    private static NativeThreadState SpillOneMagazine<TProbe>()
        where TProbe : unmanaged
    {
        NativeTransfer<TProbe>[] transfers = new NativeTransfer<TProbe>[9];
        foreach (ref NativeTransfer<TProbe> transfer in transfers.AsSpan())
        {
            transfer = NativeTransfer<TProbe>.Create(2, default(TProbe));
        }

        NativeThreadState worker = TestThread.Run(() =>
        {
            foreach (NativeTransfer<TProbe> transfer in transfers)
            {
                transfer.Dispose();
            }
        });
        NativeEndedThreads.ReleaseEndedForTest();
        return worker;
    }

    private readonly record struct TrimProbe(int Value);

    private readonly record struct ReuseProbe(int Value);

    private readonly record struct LoadProbe(int Value);

    [Fact]
    public void AThreadCyclingThroughSizesKeepsAParkedBlockForEach()
    {
        NativeMemoryTestHooks.Reset();
        NativeThreadState worker = TestThread.Run(static () =>
        {
            NativeTransfer<CycleProbe> small = NativeTransfer<CycleProbe>.Create(16, new CycleProbe(1));
            object smallControl = small.ControlForTest!;
            small.Dispose();
            // The parked small block cannot hold this one, so it stays parked
            // and a new control serves the large transfer.
            NativeTransfer<CycleProbe> large = NativeTransfer<CycleProbe>.Create(4096, new CycleProbe(2));
            object largeControl = large.ControlForTest!;
            Assert.NotSame(smallControl, largeControl);
            large.Dispose();
            NativeTransfer<CycleProbe> smallAgain = NativeTransfer<CycleProbe>.Create(16, new CycleProbe(3));
            Assert.Same(smallControl, smallAgain.ControlForTest);
            NativeTransfer<CycleProbe> largeAgain = NativeTransfer<CycleProbe>.Create(4096, new CycleProbe(4));
            Assert.Same(largeControl, largeAgain.ControlForTest);
            Assert.Equal(new CycleProbe(4), largeAgain.Read(static view => view[4095]));
            smallAgain.Dispose();
            largeAgain.Dispose();
        });
        NativeEndedThreads.ReleaseEndedForTest();
        KeptStorage.AssertBalanced<CycleProbe>(NativeMemoryTestHooks.Snapshot());
        // The ended thread's state stays reachable until its storage is
        // released: a collected state leaves its parked blocks to their
        // finalizers, which free them later.
        GC.KeepAlive(worker);
    }

    private readonly record struct CycleProbe(int Value);

    [Fact]
    public void TransfersReturnedOnAnotherThreadServeTheCreatingThread()
    {
        NativeMemoryTestHooks.Reset();
        NativeTransfer<int>[] first = new NativeTransfer<int>[32];
        for (int index = 0; index < first.Length; index++)
        {
            first[index] = NativeTransfer<int>.Create(4, index);
        }

        HashSet<object> controls = [.. first.Select(static transfer => transfer.ControlForTest!)];
        NativeThreadState worker = TestThread.Run(() =>
        {
            foreach (NativeTransfer<int> transfer in first)
            {
                transfer.Dispose();
            }
        });

        // The other thread kept eight on its own stack; the rest overflowed into
        // the shared partitions, which this thread's empty stack draws from.
        NativeTransfer<int>[] second = new NativeTransfer<int>[32];
        for (int index = 0; index < second.Length; index++)
        {
            second[index] = NativeTransfer<int>.Create(4, index);
        }

        Assert.True(second.Count(transfer => controls.Contains(transfer.ControlForTest!)) >= 16);
        Assert.All(second, static (transfer, index) => Assert.Equal(index, transfer.Read(static view => view[3])));
        foreach (NativeTransfer<int> transfer in second)
        {
            transfer.Dispose();
        }

        NativeEndedThreads.ReleaseEndedForTest();
        KeptStorage.AssertBalanced<int>(NativeMemoryTestHooks.Snapshot());
        // The ended thread's state stays reachable until its storage is
        // released: a collected state leaves its parked blocks to their
        // finalizers, which free them later.
        GC.KeepAlive(worker);
    }

    [Fact]
    public void DisposeLeavesAControlAloneOnceAnotherThreadReusedIt()
    {
        // Without the depot, a full stack sends the next return to a shared partition.
        NativeBuilderControl<int>.DepotDisabledForTest = true;
        // Fill this thread's stack so the next return overflows into a shared partition.
        NativeTransfer<int>[] fillers = new NativeTransfer<int>[8];
        for (int index = 0; index < fillers.Length; index++)
        {
            fillers[index] = NativeTransfer<int>.Create(4, index);
        }

        NativeTransfer<int> overflow = NativeTransfer<int>.Create(4, 99);
        object control = overflow.ControlForTest!;
        foreach (NativeTransfer<int> filler in fillers)
        {
            filler.Dispose();
        }

        NativeTransfer<int>? reused = null;
        NativeBuilderControl<int>.AfterSharedHandOffForTest = () => RunOnNewThread(() =>
        {
            NativeBuilderControl<int>.DepotDisabledForTest = true;
            reused = NativeTransfer<int>.Create(4, 7);
        });
        try
        {
            overflow.Dispose();
        }
        finally
        {
            NativeBuilderControl<int>.AfterSharedHandOffForTest = null;
            NativeBuilderControl<int>.DepotDisabledForTest = false;
        }

        NativeTransfer<int> current = reused!.Value;
        Assert.Same(control, current.ControlForTest);
        Assert.Equal(7, current.Read(static view => view[0]));
        Assert.ThrowsAny<InvalidOperationException>(() => overflow.Read(static view => view[0]));
        current.Dispose();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The worker failure is rethrown on the test thread.")]
    private static void RunOnNewThread(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The worker thread failed.", failure);
        }
    }

    [Fact]
    public void InjectedAllocationFailureBypassesReuse()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            Build([1], preLease: 4).Dispose();
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => new NativeBuilder<int>(preLease: 4));
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
        }
    }

    [Fact]
    public void AbandonedBuilderOnAReusedControlStillReturnsItsBlock()
    {
        NativeMemoryTestHooks.Reset();
        ParkOneControl();
        WeakReference control = AbandonBuilder();
        for (int attempt = 0; attempt < 10 && control.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(control.IsAlive);
        Assert.Equal(0, NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes);
    }

    // Separate frame: a struct temporary holding the control would otherwise stay
    // reported live for the whole test method.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ParkOneControl() => Build([1], preLease: 4).Dispose();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The builder is deliberately abandoned to exercise emergency cleanup.")]
    private static WeakReference AbandonBuilder()
    {
        // Takes the control parked by the previous builder, then is abandoned.
        NativeBuilder<int> builder = new(preLease: 4);
        builder.Append(5);
        return new WeakReference(builder.ControlForTest);
    }

    private static NativeTransfer<int> Build(int[] values, int preLease)
    {
        using NativeBuilder<int> builder = new(preLease);
        builder.Append(values);
        return builder.Complete();
    }
}
