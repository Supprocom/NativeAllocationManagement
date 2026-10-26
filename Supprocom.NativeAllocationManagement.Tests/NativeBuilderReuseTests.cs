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
