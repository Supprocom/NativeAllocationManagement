using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolBorrowLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LastLeaseReferenceKeepsItsOwnerAliveThroughTheCallback(bool process)
    {
        NativeMemoryBudget budget = new(1024);
        Assert.Equal(7, UseLastReference(budget, process, out WeakReference<NativePool<int>> weak));
        for (int attempt = 0; attempt < 8 && budget.CaptureStatistics().CommittedBytes != 0; attempt++)
        {
            Collect();
        }
        Assert.False(weak.TryGetTarget(out _));
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ActiveAllocationCount);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SuppressMessage("Reliability", "CA2000", Justification = "This adversarial last-reference test deliberately omits lease disposal so no later owner use can keep the callback owner alive accidentally. The caller forces emergency finalization and verifies exact native budget reclamation after the callback.")]
    private static int UseLastReference(NativeMemoryBudget budget, bool process, out WeakReference<NativePool<int>> weak)
    {
        Pooled<int> lease = CreateLease(budget, out weak);
        if (process)
        {
            return lease.Process(8, weak, static (values, reference) =>
            {
                Collect();
                Assert.True(reference.TryGetTarget(out NativePool<int>? owner));
                Assert.Equal(1, owner.CurrentGenerationActiveOperationsForTest);
                int value = values[7];
                GC.KeepAlive(owner);
                return value;
            });
        }
        WeakReference<NativePool<int>> expected = weak;
        return lease.Read(values =>
        {
            Collect();
            Assert.True(expected.TryGetTarget(out NativePool<int>? owner));
            Assert.Equal(1, owner.CurrentGenerationActiveOperationsForTest);
            int value = values[7];
            GC.KeepAlive(owner);
            return value;
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SuppressMessage("Reliability", "CA2000", Justification = "This adversarial helper transfers the only strong owner through a lease. The caller intentionally abandons that lease after its final callback, then verifies actual emergency finalization and exact budget reclamation. Construction failure still disposes deterministically.")]
    private static Pooled<int> CreateLease(NativeMemoryBudget budget, out WeakReference<NativePool<int>> weak)
    {
        NativePool<int> owner = new(budget, 8, 0, NativeMemoryReturn.ToNativeMemory);
        try
        {
            weak = new(owner);
            return owner.Rent(8, 7);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
