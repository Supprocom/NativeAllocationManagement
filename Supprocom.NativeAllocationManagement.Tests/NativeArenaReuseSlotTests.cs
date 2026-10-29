using System.Runtime.CompilerServices;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

// An arena's kernel stays in its thread's reuse slot while the arena uses it.
// These tests end threads and compare process-wide counters, so they run alone.
[Collection(EndedThreadGroup.Name)]
public sealed class NativeArenaReuseSlotTests
{
    [Fact]
    public void AnArenaItsEndedThreadNeverDisposedIsFreedOnceByItsGuard()
    {
        Quiesce();
        long baseline = NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes;

        NativeThreadState ended = TestThread.Run(LeaveAnArenaInUse);
        Assert.True(HoldsAKernelInUse(ended));
        Assert.True(NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes > baseline);

        // The release skips the kernel in use and clears the slot. The kernel's
        // guard frees its storage once the kernel is unreachable, and only then.
        NativeEndedThreads.ReleaseEndedForTest();
        Assert.Null(ended.ReusableArena);
        Assert.True(NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes > baseline);
        GC.KeepAlive(ended);
        Quiesce();
        Assert.Equal(baseline, NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes);
    }

    [Fact]
    public void ANestedArenaTakesAKernelFromBehindTheOneInUse()
    {
        TestThread.Run(static () =>
        {
            // Two nested arenas leave one kernel in the slot and one behind it.
            NativeArena outer = new(4096);
            NativeArena inner = new(4096);
            inner.Dispose();
            outer.Dispose();

            // The next outer arena takes the slot's kernel and leaves it there;
            // the arena nested in it takes the kernel behind, not that one.
            using NativeArena outerAgain = new(4096);
            ArenaLease<int> outerValues = outerAgain.Scratch<int>(4, 3);
            Assert.Equal(0, outerAgain.GetStatistics().FreshSegmentAllocationCount);
            using (NativeArena innerAgain = new(4096))
            {
                Assert.Equal(0, innerAgain.GetStatistics().FreshSegmentAllocationCount);
                Assert.Equal(5, innerAgain.Scratch<int>(4, 5).AsSpan()[3]);
            }

            // Disposing the nested arena ended only its own kernel's lifetime.
            Assert.Equal(3, outerValues.AsSpan()[3]);
            Assert.Equal(7, outerAgain.Scratch<int>(4, 7).AsSpan()[3]);
        });
    }

    // Disposes one arena, so its kernel waits in the slot, then ends inside a
    // second arena that took that kernel and is never disposed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The test leaves the second arena undisposed on purpose, as an abandoned arena.")]
    private static void LeaveAnArenaInUse()
    {
        NativeArena first = new(4096);
        Assert.Equal(1, first.Scratch<int>(4, 1).AsSpan()[3]);
        first.Dispose();
        NativeArena second = new(4096);
        Assert.Equal(0, second.GetStatistics().FreshSegmentAllocationCount);
        Assert.Equal(7, second.Scratch<int>(4, 7).AsSpan()[3]);
    }

    // Out of line, so no temporary of the test keeps the kernel reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HoldsAKernelInUse(NativeThreadState state) =>
        state.ReusableArena is { CurrentLifecycle: NativeOwnerLifecycle.Active };

    // Full collections also free large blocks no owner reused since the
    // previous one, which earlier tests may have left: three settle them.
    private static void Quiesce()
    {
        for (int round = 0; round < 3; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            NativeEndedThreads.ReleaseEndedForTest();
        }
    }
}
