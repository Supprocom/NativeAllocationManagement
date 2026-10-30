using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement.Tests;

// A region adds its cursor, its alignment and its requested bytes unchecked
// (NativeRegionKernel.Reserve, AlignUp and RequestedAfter). These tests sit on
// the bounds those additions rely on: leases that end exactly at the end of
// the largest segment a thread keeps for reuse, taken through the inlined
// reactivation, and the largest lease a region can be asked for. Kept large
// blocks are process-wide, so this runs alone.
[Collection(ProcessCounterGroup.Name)]
public sealed class NativeRegionBoundTests
{
    private const int Limit = (int)NativeRegionKernel.ReuseLimitBytes;

    [Fact]
    public void AnElementAlignedLeaseEndingAtTheLargestKeptSegmentsEndStaysInside()
    {
        TestThread.Run(static () =>
        {
            NativeRegion region = ReactivateLargestKeptSegment();
            Local<byte> head = region.Lease<byte>(1, 1);
            Local<long> word = region.Lease<long>(1, 2);

            // 16 bytes are used; the rest is too large to start vector-aligned at
            // 32, so it starts at 16 and ends exactly at the segment's end.
            Local<byte> rest = region.LeaseUninitialized<byte>(Limit - 16);
            rest.AsSpan()[0] = 3;
            rest.AsSpan()[^1] = 4;
            Assert.Equal(1, region.GetStatistics().SegmentCount);
            Assert.Equal(16, (int)Unsafe.ByteOffset(ref head.AsSpan()[0], ref rest.AsSpan()[0]));

            // Nothing is left: one more byte grows a new segment.
            Local<byte> next = region.Lease<byte>(1, 5);
            Assert.Equal(2, region.GetStatistics().SegmentCount);
            Assert.Equal(1, head.AsSpan()[0]);
            Assert.Equal(2, word.AsSpan()[0]);
            Assert.Equal(3, rest.AsSpan()[0]);
            Assert.Equal(4, rest.AsSpan()[^1]);
            Assert.Equal(5, next.AsSpan()[0]);
            Assert.Equal(Limit - 6, region.GetStatistics().RequestedBytes);
            region.Dispose();
        });
    }

    [Fact]
    public void AVectorAlignedLeaseEndingAtTheLargestKeptSegmentsEndStaysInside()
    {
        TestThread.Run(static () =>
        {
            NativeRegion region = ReactivateLargestKeptSegment();
            Local<byte> head = region.Lease<byte>(1, 1);

            // A range of 1 KiB or more starts 32-byte aligned when it fits: this
            // one starts at 32 and ends exactly at the segment's end.
            Local<byte> rest = region.LeaseUninitialized<byte>(Limit - 32);
            rest.AsSpan()[0] = 3;
            rest.AsSpan()[^1] = 4;
            Assert.Equal(1, region.GetStatistics().SegmentCount);
            Assert.Equal(32, (int)Unsafe.ByteOffset(ref head.AsSpan()[0], ref rest.AsSpan()[0]));

            // Nothing is left: one more byte grows a new segment.
            Local<byte> next = region.Lease<byte>(1, 5);
            Assert.Equal(2, region.GetStatistics().SegmentCount);
            Assert.Equal(1, head.AsSpan()[0]);
            Assert.Equal(3, rest.AsSpan()[0]);
            Assert.Equal(4, rest.AsSpan()[^1]);
            Assert.Equal(5, next.AsSpan()[0]);
            region.Dispose();
        });
    }

    // int.MaxValue elements of a 64 KiB struct: about 2^47 bytes, the largest
    // byte count a lease of this size class can ask for. A budget refuses it
    // before any allocation, on every platform; a 32-bit process refuses its
    // byte count first.
    [Fact]
    public void TheLargestLeaseARegionCanBeAskedForIsRefusedWithoutMovingItsCursorOrDemand()
    {
        NativeMemoryBudget budget = new(1 << 20);
        NativeRegion region = new(budget, 4096, NativeMemoryReturn.Free);
        Local<int> first = region.Lease<int>(1, 7);
        Type expected = IntPtr.Size == 8 ? typeof(NativeMemoryBudgetExceededException) : typeof(OverflowException);

        Assert.Equal(expected, RefusedFilled(region));
        Assert.Equal(expected, RefusedUninitialized(region));
        Assert.Equal(expected, RefusedInitialized(region));

        Assert.Equal(4, region.GetStatistics().RequestedBytes);
        Assert.Equal(1, region.GetStatistics().SegmentCount);
        Local<int> next = region.Lease<int>(1, 8);
        Assert.Equal(4, (int)Unsafe.ByteOffset(ref first.AsSpan()[0], ref next.AsSpan()[0]));
        Assert.Equal(7, first.AsSpan()[0]);
        Assert.Equal(8, next.AsSpan()[0]);
        region.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    // A fresh thread's first region allocates the largest segment the reuse
    // slot keeps and puts its kernel there on disposal; the next region takes
    // that kernel through the inlined reactivation.
    private static NativeRegion ReactivateLargestKeptSegment()
    {
        NativeLargeBlocks.Trim(highLoad: true);
        NativeRegion warm = new(preAllocateBytes: (nuint)Limit);
        Assert.Equal(Limit, warm.GetStatistics().UsableCapacityBytes);
        warm.Dispose();

        NativeRegion region = new();
        Assert.Equal(1, region.GetStatistics().SegmentCount);
        Assert.Equal(0, region.GetStatistics().FreshSegmentAllocationCount);
        Assert.Equal(Limit, region.GetStatistics().UsableCapacityBytes);
        return region;
    }

    private static Type? RefusedFilled(NativeRegion region)
    {
        try
        {
            _ = region.Lease<Huge>(int.MaxValue, default(Huge));
            return null;
        }
        catch (NativeMemoryBudgetExceededException exception)
        {
            return exception.GetType();
        }
        catch (OverflowException exception)
        {
            return exception.GetType();
        }
    }

    private static Type? RefusedUninitialized(NativeRegion region)
    {
        try
        {
            _ = region.LeaseUninitialized<Huge>(int.MaxValue);
            return null;
        }
        catch (NativeMemoryBudgetExceededException exception)
        {
            return exception.GetType();
        }
        catch (OverflowException exception)
        {
            return exception.GetType();
        }
    }

    private static Type? RefusedInitialized(NativeRegion region)
    {
        try
        {
            _ = region.Lease<Huge>(int.MaxValue, static _ => throw new InvalidOperationException("The initializer must not run."));
            return null;
        }
        catch (NativeMemoryBudgetExceededException exception)
        {
            return exception.GetType();
        }
        catch (OverflowException exception)
        {
            return exception.GetType();
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 64 * 1024)]
    private struct Huge
    {
    }
}
