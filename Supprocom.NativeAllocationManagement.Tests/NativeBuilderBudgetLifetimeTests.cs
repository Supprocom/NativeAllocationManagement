using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeBuilderBudgetLifetimeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ActiveBuilderKeepsItsDomainThroughEmptyAndNonemptyGrowth(int initialCapacity)
    {
        using NativeBuilder<int> builder = CreateActive(initialCapacity, 64, 128, out WeakReference<NativeMemoryBudget> budget);
        Collect();
        Assert.True(IsAlive(budget));
        Assert.Equal(initialCapacity * sizeof(int), Observe(budget).CommittedBytes);
        Assert.True(builder.TryEnsureCapacity(4));
        builder.Append(42);
        Assert.True(builder.TryEnsureCapacity(8));
        NativeMemoryBudgetStatistics grown = Observe(budget);
        Assert.Equal(32, grown.CommittedBytes);
        Assert.Equal(0, grown.ReservedBytes);
        Assert.Equal(48, grown.PeakAdmittedBytes);
        Assert.Equal(1, grown.ActiveAllocationCount);
        Assert.Equal(initialCapacity == 0 ? 2 : 1, grown.ReallocationCount);
        Assert.Equal(16, grown.AcquiredBackingBytes);
        Assert.Equal(32, grown.ReplacementBackingBytes);
        Assert.Equal(builder.Id, builder.CaptureDiagnosticSnapshot().OwnerId);
        AssertTerminalCharge(DisposeAndObserve(builder, budget), expectedFrees: 1);
        Collect();
        Assert.False(IsAlive(budget));
        Assert.Equal(NativeOwnerLifecycle.Disposed, builder.GetStatistics().Lifecycle);
        GC.KeepAlive(builder);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    public void RefusalPreservesTheOnlyLiveBudgetReferenceAndPrefix(int traceCapacity)
    {
        using NativeBuilder<int> builder = CreateActive(4, 16, traceCapacity, out WeakReference<NativeMemoryBudget> budget);
        builder.Append(42);
        Collect();
        Assert.True(IsAlive(budget));
        Assert.False(builder.TryEnsureCapacity(8));
        Assert.Equal(1, builder.Count);
        Assert.Equal(4, builder.Capacity);
        NativeMemoryBudgetStatistics refused = Observe(budget);
        Assert.Equal(16, refused.CommittedBytes);
        Assert.Equal(0, refused.ReservedBytes);
        Assert.Equal(1, refused.RejectedAllocationCount);
        NativeTransfer<int> transfer = builder.Complete();
        NativeMemoryBudgetStatistics terminal;
        try { Assert.Equal(42, transfer.Read(static view => view[0])); }
        finally { terminal = ReturnAndObserve(transfer, budget); }
        AssertTerminalCharge(terminal, expectedFrees: 1);
        Collect();
        Assert.False(IsAlive(budget));
        Assert.Equal(NativeOwnerLifecycle.Returned, builder.GetStatistics().Lifecycle);
        GC.KeepAlive(builder);
        GC.KeepAlive(transfer);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void CompletionTransfersTheDomainWithoutKeepingItInTerminalMetadata(int initialCapacity)
    {
        using NativeBuilder<int> builder = CreateCompleted(initialCapacity,
            out NativeTransfer<int> transfer, out WeakReference<NativeMemoryBudget> budget);
        NativeTransfer<int> staleObservation = transfer;
        NativeMemoryBudgetStatistics terminal;
        try
        {
            Collect();
            Assert.True(IsAlive(budget));
            NativeMemoryBudgetStatistics live = Observe(budget);
            Assert.Equal(16, live.CommittedBytes);
            Assert.Equal(1, live.ActiveAllocationCount);
            Assert.Equal(0, builder.GetStatistics().OutstandingNativeBytes);
            Assert.Equal(NativeOwnerLifecycle.Returned, builder.GetStatistics().Lifecycle);
            Assert.Equal(builder.Id, transfer.Id);
            Assert.Equal(42, transfer.Read(static view => view[0]));
        }
        finally { terminal = ReturnAndObserve(transfer, budget); }
        AssertTerminalCharge(terminal, expectedFrees: 1);
        Collect();
        Assert.False(IsAlive(budget));
        Assert.Equal(1, staleObservation.CaptureSnapshot().PayloadReturnCount);
        Assert.False(staleObservation.CaptureSnapshot().HasReturnObligation);
        Assert.Equal(builder.Id, staleObservation.CaptureSnapshot().OwnerId);
        GC.KeepAlive(builder);
        GC.KeepAlive(staleObservation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void TerminalPathsDoNotRootTheBudgetOrItsDiagnosticBuffer(int path)
    {
        using NativeBuilder<int> builder = CreateTerminal(path, out WeakReference<NativeMemoryBudget> budget,
            out NativeMemoryBudgetStatistics charge);
        AssertTerminalCharge(charge, expectedFrees: path == 0 ? 0 : 1);
        Collect();
        Assert.False(IsAlive(budget));
        NativeOwnerStatistics terminal = builder.GetStatistics();
        Assert.Equal(path == 2 ? NativeOwnerLifecycle.Returned : NativeOwnerLifecycle.Disposed, terminal.Lifecycle);
        Assert.Equal(0, terminal.OutstandingNativeBytes);
        Assert.Equal(0, terminal.InitializedPayloadBytes);
        Assert.Equal(builder.Id, builder.CaptureDiagnosticSnapshot().OwnerId);
        if (path == 2) Assert.Throws<InvalidOperationException>(() => _ = builder.Count);
        else Assert.Throws<ObjectDisposedException>(() => _ = builder.Count);
        GC.KeepAlive(builder);
    }

    [Fact]
    public void BuilderStoresItsDomainOnlyWithTheBackingResponsibility()
    {
        FieldInfo[] builderFields = typeof(NativeBuilder<int>).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(builderFields, static field => field.FieldType == typeof(NativeMemoryBudget));
        int backingFields = 0;
        foreach (FieldInfo field in builderFields)
            if (field.FieldType == typeof(NativeBlock)) backingFields++;
        Assert.Equal(1, backingFields);
        Assert.Contains(typeof(NativeBlock).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
            static field => field.FieldType == typeof(NativeMemoryBudget));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeBuilder<int> CreateActive(int initialCapacity, long capacityBytes, int traceCapacity,
        out WeakReference<NativeMemoryBudget> observed)
    {
        NativeMemoryBudget budget = new(capacityBytes, traceCapacity);
        observed = new(budget);
        return new(budget, initialCapacity);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeBuilder<int> CreateCompleted(int initialCapacity, out NativeTransfer<int> transfer,
        out WeakReference<NativeMemoryBudget> observed)
    {
        using NativeBuilder<int> builder = CreateActive(initialCapacity, 64, 128, out observed);
        builder.Append(42);
        transfer = builder.Complete();
        return builder;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeBuilder<int> CreateTerminal(int path, out WeakReference<NativeMemoryBudget> observed,
        out NativeMemoryBudgetStatistics terminal)
    {
        NativeMemoryBudget budget = new(64, 4_096);
        observed = new(budget);
        NativeBuilder<int> builder;
        using (builder = new(budget, path == 0 ? 0 : 4))
        {
            if (path != 0) builder.Append(42);
            switch (path)
            {
                case 0:
                case 1:
                    break;
                case 2:
                    using (NativeTransfer<int> transfer = builder.Complete())
                        Assert.Equal(42, transfer.Read(static view => view[0]));
                    break;
                case 3:
                    Assert.Throws<OperationCanceledException>(() => builder.Complete(new CancellationToken(canceled: true)));
                    break;
                case 4:
                    Assert.Throws<ArithmeticException>(() => builder.Write(1, static _ => throw new ArithmeticException("initializer failure")));
                    break;
                case 5:
                    NativeMemoryTestHooks.FailNextAllocation();
                    try { Assert.Throws<NativeAllocationFailedException>(() => builder.TryEnsureCapacity(8)); }
                    finally { NativeMemoryTestHooks.Reset(); }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(path));
            }
        }
        terminal = budget.CaptureStatistics();
        GC.KeepAlive(budget);
        return builder;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<NativeMemoryBudget> observed) => observed.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeMemoryBudgetStatistics Observe(WeakReference<NativeMemoryBudget> observed)
    {
        Assert.True(observed.TryGetTarget(out NativeMemoryBudget? budget));
        return budget.CaptureStatistics();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeMemoryBudgetStatistics DisposeAndObserve(NativeBuilder<int> builder,
        WeakReference<NativeMemoryBudget> observed)
    {
        Assert.True(observed.TryGetTarget(out NativeMemoryBudget? budget));
        builder.Dispose();
        return budget.CaptureStatistics();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeMemoryBudgetStatistics ReturnAndObserve(NativeTransfer<int> transfer,
        WeakReference<NativeMemoryBudget> observed)
    {
        Assert.True(observed.TryGetTarget(out NativeMemoryBudget? budget));
        transfer.Dispose();
        return budget.CaptureStatistics();
    }

    private static void AssertTerminalCharge(NativeMemoryBudgetStatistics terminal, long expectedFrees)
    {
        Assert.Equal(0, terminal.CommittedBytes);
        Assert.Equal(0, terminal.ReservedBytes);
        Assert.Equal(0, terminal.ActiveAllocationCount);
        Assert.Equal(expectedFrees, terminal.FreeCount);
    }

    private static void Collect()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
