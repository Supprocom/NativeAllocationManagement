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
        NativeMemoryTestMetrics afterFirst = NativeMemoryTestHooks.Snapshot();
        Assert.Equal(afterFirst.AllocationCount, afterFirst.FreeCount);
        Assert.Equal(0, afterFirst.OutstandingNativeBytes);

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
        NativeMemoryTestMetrics end = NativeMemoryTestHooks.Snapshot();
        Assert.Equal(end.AllocationCount, end.FreeCount);
        Assert.Equal(0, end.OutstandingNativeBytes);
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
        NativeTransfer<byte> large = NativeTransfer<byte>.Create(60 * 1024, 1);
        NativeTransfer<byte> small = NativeTransfer<byte>.Create(8 * 1024, 2);
        object smallControl = small.ControlForTest!;
        large.Dispose();
        small.Dispose();
        // The large block parked first; the small one would exceed 64 KiB, so it was freed.
        NativeTransfer<byte> next = NativeTransfer<byte>.Create(8 * 1024, 3);
        Assert.NotSame(smallControl, next.ControlForTest);
        next.Dispose();
        Assert.Equal(0, NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes);
    }

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
        RunOnNewThread(() =>
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

        Assert.Equal(0, NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes);
    }

    [Fact]
    public void DisposeLeavesAControlAloneOnceAnotherThreadReusedIt()
    {
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
        NativeBuilderControl<int>.AfterSharedHandOffForTest = () => RunOnNewThread(() => reused = NativeTransfer<int>.Create(4, 7));
        try
        {
            overflow.Dispose();
        }
        finally
        {
            NativeBuilderControl<int>.AfterSharedHandOffForTest = null;
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
