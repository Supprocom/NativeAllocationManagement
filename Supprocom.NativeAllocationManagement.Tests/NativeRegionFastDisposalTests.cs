namespace Supprocom.NativeAllocationManagement.Tests;

// A region's inlined disposal trusts two things: its kernel is active with
// nothing entered, and the kernel is in its thread's reuse slot, which implies
// one segment within the reuse limit. A kernel that grows past that leaves the
// slot. Each test runs on a new thread, which starts with an empty slot.
public sealed class NativeRegionFastDisposalTests
{
    [Fact]
    public void DisposalInsideABorrowOfAKernelInTheSlotIsRefused()
    {
        TestThread.Run(static () =>
        {
            new NativeRegion(preAllocateBytes: 256).Dispose();
            NativeRegion region = new(preAllocateBytes: 256);
            Assert.Equal(NativeOwnerLifecycle.Active, NativeThreadState.Current.ReusableRegion?.Lifecycle);
            Local<int> values = region.Lease<int>(4, 3);

            // The region itself is the processor's state: disposal while the
            // borrow is entered must be refused, not take the inlined path.
            Assert.Equal(1, values.Process<NativeRegion, int, DisposeInside>(in region));
            Assert.Equal(NativeOwnerLifecycle.Active, region.CurrentLifecycle);
            Assert.Equal(3, values.Read(static view => view[3]));
            region.Dispose();
            Assert.Equal(NativeOwnerLifecycle.Disposed, region.CurrentLifecycle);
            Assert.True(NativeRegionKernel.HasReusableKernelForTest);
        });
    }

    [Fact]
    public void AKernelInTheSlotThatGrowsASecondSegmentLeavesItAndReturnsTrimmed()
    {
        TestThread.Run(static () =>
        {
            new NativeRegion(preAllocateBytes: 256).Dispose();
            NativeThreadState thread = NativeThreadState.Current;
            NativeRegion region = new(preAllocateBytes: 256);
            NativeRegionKernel? kernel = thread.ReusableRegion;
            Assert.NotNull(kernel);
            Assert.Equal(NativeOwnerLifecycle.Active, kernel!.Lifecycle);

            _ = region.Lease<long>(8, 1);
            Assert.Same(kernel, thread.ReusableRegion);
            Local<long> wide = region.Lease<long>(1024, 2);
            Assert.Equal(2, region.GetStatistics().SegmentCount);
            Assert.Null(thread.ReusableRegion);
            Assert.Equal(2, wide.Read(static view => view[1023]));

            region.Dispose();
            Assert.Same(kernel, thread.ReusableRegion);
            Assert.Equal(NativeOwnerLifecycle.Disposed, kernel.Lifecycle);

            NativeRegion next = new();
            Assert.Equal(1, next.GetStatistics().SegmentCount);
            Assert.Equal(0, next.GetStatistics().FreshSegmentAllocationCount);
            Assert.Equal(5, next.Lease<long>(1024, 5).Read(static view => view[0]));
            next.Dispose();
        });
    }

    [Fact]
    public void AKernelInTheSlotWhoseFirstSegmentPassesTheLimitLeavesIt()
    {
        TestThread.Run(static () =>
        {
            // A region that never leased is kept without storage.
            new NativeRegion().Dispose();
            NativeThreadState thread = NativeThreadState.Current;
            NativeRegion region = new();
            NativeRegionKernel? kernel = thread.ReusableRegion;
            Assert.NotNull(kernel);
            Assert.Equal(0, region.GetStatistics().SegmentCount);

            Local<byte> large = region.Lease<byte>(2 * 1024 * 1024, 7);
            Assert.Equal(1, region.GetStatistics().SegmentCount);
            Assert.Null(thread.ReusableRegion);
            Assert.Equal(7, large.Read(static view => view[^1]));

            // Its disposal keeps the kernel, without the large segment.
            region.Dispose();
            Assert.Same(kernel, thread.ReusableRegion);
            NativeRegion next = new();
            Assert.Equal(0, next.GetStatistics().SegmentCount);
            next.Dispose();
        });
    }

    private readonly struct DisposeInside : INativeSpanProcessor<int, NativeRegion, int>
    {
        public static int Invoke(Span<int> values, in NativeRegion region)
        {
            try
            {
                region.Dispose();
                return 0;
            }
            catch (NativeAllocationInUseException)
            {
                return 1;
            }
        }
    }
}
