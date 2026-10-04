using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeTransferValueTests
{
    [Fact]
    public void RepeatedMovesReuseOneControlWithoutManagedOrBackingAllocation()
    {
        NativeMemoryBudget budget = new(64);
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(42);
        NativeTransfer<int>? binding = builder.Complete();
        object control = binding.Value.ControlForTest!;
        NativeTransfer<int> current = NativeTransfer<int>.Move(ref binding);
        binding = current;
        NativeMemoryBudgetStatistics before = budget.CaptureStatistics();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int move = 0; move < 10_000; move++)
        {
            current = NativeTransfer<int>.Move(ref binding);
            binding = current;
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(0, delta);
        Assert.Same(control, current.ControlForTest);
        Assert.Equal(before.AllocationCount, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(before.CommittedBytes, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(42, current.Read(static view => view[0]));
        current.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void AStaleReleaseCannotClaimOrFreeTheNewBinding()
    {
        NativeMemoryBudget budget = new(64);
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(42);
        NativeTransfer<int>? source = builder.Complete();
        NativeTransfer<int> stale = source.Value;
        NativeTransfer<int> current = NativeTransfer<int>.Move(ref source);
        Assert.Throws<InvalidOperationException>(stale.Dispose);
        Assert.Throws<InvalidOperationException>(() => stale.Read(static view => view[0]));
        Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(42, current.Read(static view => view[0]));
        current.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        GC.KeepAlive(stale);
    }

    [Fact]
    public void AStaleMoveDoesNotConsumeTheCurrentOwner()
    {
        using NativeBuilder<int> builder = new(4);
        builder.Append(42);
        NativeTransfer<int>? source = builder.Complete();
        NativeTransfer<int>? stale = source;
        NativeTransfer<int> current = NativeTransfer<int>.Move(ref source);
        Assert.Throws<InvalidOperationException>(() => NativeTransfer<int>.Move(ref stale));
        Assert.Null(stale);
        Assert.Equal(42, current.Read(static view => view[0]));
        current.Dispose();
    }

    [Fact]
    public void StaleDisposalInsideAnEnteredCurrentBorrowDoesNotCloseAdmission()
    {
        using NativeBuilder<int> builder = new(4);
        builder.Append(42);
        NativeTransfer<int>? source = builder.Complete();
        NativeTransfer<int> stale = source.Value;
        NativeTransfer<int> current = NativeTransfer<int>.Move(ref source);
        current.Access(view =>
        {
            Assert.Throws<InvalidOperationException>(stale.Dispose);
            Assert.Equal(42, view[0]);
        });
        Assert.Equal(42, current.Read(static view => view[0]));
        current.Dispose();
    }

    [Fact]
    public void DefaultCapabilitiesHaveNoOwnershipOrReleaseAuthority()
    {
        NativeTransfer<int> value = default;
        Assert.Throws<NativeAllocationUninitializedException>(value.Dispose);
        Assert.Throws<NativeAllocationUninitializedException>(() => value.Access(static _ => { }));
        Assert.Throws<NativeAllocationUninitializedException>(() => value.Length);
        Assert.Throws<NativeAllocationUninitializedException>(() => value.Id);
        NativeTransfer<int>? source = value;
        Assert.Throws<NativeAllocationUninitializedException>(() => NativeTransfer<int>.Move(ref source));
        Assert.Null(source);
    }

    [Fact]
    public void AuthorityExhaustionConsumesAndReleasesInsteadOfWrapping()
    {
        NativeMemoryBudget budget = new(64);
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(42);
        NativeTransfer<int> value = builder.Complete();
        object control = value.ControlForTest!;
        control.GetType().GetField("_authorityVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(control, long.MaxValue);
        object boxed = value;
        typeof(NativeTransfer<int>).GetField("_authorityVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(boxed, long.MaxValue);
        NativeTransfer<int>? source = (NativeTransfer<int>)boxed;
        Assert.Throws<OverflowException>(() => NativeTransfer<int>.Move(ref source));
        Assert.Null(source);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ActiveAllocationCount);
        Assert.Throws<ObjectDisposedException>(value.Dispose);
    }

    [Fact]
    public void AcquisitionControlFinalizesAbandonedDirectBacking()
    {
        NativeMemoryBudget budget = new(64);
        WeakReference control = CreateAbandonedControl(budget);
        for (int attempt = 0; attempt < 10 && control.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(control.IsAlive);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ActiveAllocationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackDrainingBeforeRetirementPublicationStillReturnsBacking(bool pooled)
    {
        NativeMemoryBudget budget = new(1_024);
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(42);
        using NativeConcurrentPool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeTransfer<int>? source = pooled
            ? pool.RentTransferable(1, static writer => writer.Fill(42))
            : builder.Complete();
        NativeTransfer<int> alias = source.Value;
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        using ManualResetEventSlim exited = new(false);
        Task callback = Task.Run(() =>
        {
            try
            {
                alias.Access(view =>
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                    Assert.Equal(42, view[0]);
                });
            }
            finally
            {
                exited.Set();
            }
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            NativeMemoryTestHooks.SetBeforeOperationEntry(operation =>
            {
                if (string.Equals(operation, "NativeTransfer.Retire", StringComparison.Ordinal))
                {
                    release.Set();
                    Assert.True(exited.Wait(TimeSpan.FromSeconds(5)));
                }
            });
            Assert.Throws<InvalidOperationException>(() => NativeTransfer<int>.Move(ref source));
            Assert.Null(source);
            await callback.ConfigureAwait(true);
            Assert.Throws<ObjectDisposedException>(() => alias.Read(static view => view[0]));
            Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
            // Pooled backing is cached until the owner closes; direct backing is released now.
            pool.Dispose();
            builder.Dispose();
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(0, budget.CaptureStatistics().ActiveAllocationCount);
        }
        finally
        {
            NativeMemoryTestHooks.SetBeforeOperationEntry(null);
            release.Set();
            await callback.ConfigureAwait(true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAbandonedControl(NativeMemoryBudget budget)
    {
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(42);
        NativeTransfer<int>? source = builder.Complete();
        NativeTransfer<int> destination = NativeTransfer<int>.Move(ref source);
        return new WeakReference(destination.ControlForTest!);
    }
}
