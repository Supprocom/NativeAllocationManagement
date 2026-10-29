using System.Runtime.CompilerServices;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

// A region's kernel stays in its thread's reuse slot while the region uses it.
// These tests end threads and compare process-wide counters, so they run alone.
[Collection(EndedThreadGroup.Name)]
public sealed class NativeRegionReuseSlotTests
{
    [Fact]
    public void ARegionItsEndedThreadNeverDisposedIsFreedOnceByItsGuard()
    {
        Quiesce();
        long baseline = NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes;

        NativeThreadState ended = TestThread.Run(LeaveARegionInUse);
        Assert.True(HoldsAKernelInUse(ended));
        Assert.True(NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes > baseline);

        // The release skips the kernel in use and clears the slot. The kernel's
        // guard frees its storage once the kernel is unreachable, and only then.
        NativeEndedThreads.ReleaseEndedForTest();
        Assert.Null(ended.ReusableRegion);
        Assert.True(NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes > baseline);
        GC.KeepAlive(ended);
        Quiesce();
        Assert.Equal(baseline, NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes);
    }

    [Fact]
    public void ANestedRegionTakesAKernelFromBehindTheOneInUse()
    {
        TestThread.Run(static () =>
        {
            // Two nested regions leave one kernel in the slot and one behind it.
            NativeRegion outer = new(preAllocateBytes: 256);
            NativeRegion inner = new(preAllocateBytes: 256);
            inner.Dispose();
            outer.Dispose();
            Assert.True(NativeRegionKernel.HasReusableKernelForTest);

            // The next outer region takes the slot's kernel and leaves it there;
            // the region nested in it takes the kernel behind, not that one.
            NativeRegion outerAgain = new(preAllocateBytes: 256);
            NativeRegion innerAgain = new(preAllocateBytes: 256);
            Assert.False(NativeRegionKernel.HasReusableKernelForTest);
            Assert.Equal(0, outerAgain.GetStatistics().FreshSegmentAllocationCount);
            Assert.Equal(0, innerAgain.GetStatistics().FreshSegmentAllocationCount);
            Local<int> outerValues = outerAgain.Lease<int>(4, 3);
            Local<int> innerValues = innerAgain.Lease<int>(4, 5);
            Assert.Equal(5, innerValues.Read(static view => view[3]));
            innerAgain.Dispose();

            // Disposing the nested region ended only its own kernel's lifetime.
            Assert.Equal(NativeOwnerLifecycle.Active, outerAgain.CurrentLifecycle);
            Assert.Equal(3, outerValues.Read(static view => view[3]));
            outerAgain.Dispose();
            Assert.True(NativeRegionKernel.HasReusableKernelForTest);
        });
    }

    // Disposes one region, so its kernel waits in the slot, then ends inside a
    // second region that took that kernel and is never disposed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The test leaves the second region undisposed on purpose, as an abandoned region.")]
    private static void LeaveARegionInUse()
    {
        NativeRegion first = new(preAllocateBytes: 256);
        Assert.Equal(1, first.Lease<int>(4, 1).Read(static view => view[3]));
        first.Dispose();
        NativeRegion second = new(preAllocateBytes: 256);
        Assert.Equal(0, second.GetStatistics().FreshSegmentAllocationCount);
        Assert.Equal(7, second.Lease<int>(4, 7).Read(static view => view[3]));
    }

    // Out of line, so no temporary of the test keeps the kernel reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HoldsAKernelInUse(NativeThreadState state) =>
        state.ReusableRegion is { Lifecycle: NativeOwnerLifecycle.Active };

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
