using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolTerminalBackingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TerminalCleanupReconcilesEveryRemainingPhysicalSlab(int path)
    {
        NativeMemoryBudget budget = new(1024, traceCapacity: 32);
        WeakReference owner = ReleaseMultipleSlabs(budget, path, out NativeOwnerDiagnosticSnapshot terminal);
        Assert.Equal(448, terminal.PeakOutstandingNativeBytes);
        Assert.Equal(0, terminal.InitializedPayloadBytes);
        Assert.Equal(path == 0 ? 0 : path == 3 ? 384 : 448, terminal.OutstandingNativeBytes);
        Assert.Equal(path is 1 or 3 ? terminal.OutstandingNativeBytes : 0, terminal.DetachedNativeBytes);
        Assert.Equal(path == 2 ? NativeOwnerLifecycle.Active : NativeOwnerLifecycle.Disposed, terminal.Lifecycle);
        NativeMemoryBudgetStatistics before = budget.CaptureStatistics();
        Assert.Equal(3, before.AllocationCount);
        Assert.Equal(path == 0 ? 3 : path == 3 ? 1 : 0, before.FreeCount);
        for (int attempt = 0; attempt < 8 && (owner.IsAlive || budget.CaptureStatistics().CommittedBytes != 0); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(owner.IsAlive);
        NativeMemoryBudgetStatistics returned = budget.CaptureStatistics();
        Assert.Equal(0, returned.CommittedBytes);
        Assert.Equal(0, returned.ReservedBytes);
        Assert.Equal(0, returned.ActiveAllocationCount);
        Assert.Equal(returned.AllocationCount, returned.FreeCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SuppressMessage("Reliability", "CA2000", Justification = "This adversarial helper intentionally releases the last strong owner in the abandoned path; the test observes actual emergency finalization and exact budget return. Other paths dispose deterministically or exercise the explicit GC-return policy, with live leases returned in finally.")]
    private static WeakReference ReleaseMultipleSlabs(NativeMemoryBudget budget, int path, out NativeOwnerDiagnosticSnapshot terminal)
    {
        NativePool<byte> pool = new(budget, 1, 0, path == 0 ? NativeMemoryReturn.ToNativeMemory : NativeMemoryReturn.ToGarbageCollector);
        try
        {
            using (Pooled<byte> middle = pool.Rent(65, static writer => writer.Fill(7)))
            using (Pooled<byte> largest = pool.Rent(129, static writer => writer.Fill(11)))
            {
                Assert.Equal(7, middle.Read(static values => values[64]));
                Assert.Equal(11, largest.Read(static values => values[128]));
                if (path == 3) Assert.Equal((nuint)64, pool.TrimRetainedMemoryByBytes(1));
            }
            if (path != 2)
            {
                pool.Dispose();
                pool.Dispose();
            }
            terminal = pool.CaptureDiagnosticSnapshot();
            return new WeakReference(pool);
        }
        catch
        {
            pool.Dispose();
            throw;
        }
    }
}
