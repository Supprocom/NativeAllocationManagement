using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

// A region admits a new lease only while it is active with no initializer
// entered; a bounded borrow does not refuse one. One test of the kernel's
// packed state decides both, for every kind of lease.
public sealed class NativeRegionLeaseAdmissionTests
{
    [Fact]
    public void EveryLeaseKindInsideAnInitializerIsRefusedBeforeMovingTheCursor()
    {
        NativeRegionKernel region = new(64, NativeMemoryReturn.Free);
        Local<int> value = region.LeaseInitialized<int>(1, writer =>
        {
            Assert.Throws<NativeAllocationInUseException>(() => { _ = region.LeaseFilled<int>(2, 5); });
            Assert.Throws<NativeAllocationInUseException>(() => { _ = region.LeaseUninitialized<int>(2); });
            Assert.Throws<NativeAllocationInUseException>(() => { _ = region.LeaseInitialized<int>(2, static nested => nested.Fill(6)); });
            Assert.Equal(0, region.GetStatistics().RequestedBytes);
            writer.Write(42);
        });
        Local<int> next = region.LeaseFilled<int>(2, 7);

        Assert.Equal(42, value.Read(static view => view[0]));
        Assert.Equal(7, next.Read(static view => view[1]));
        Assert.Equal(sizeof(int), (int)Unsafe.ByteOffset(ref value.AsSpan()[0], ref next.AsSpan()[0]));
        Assert.Equal(12, region.GetStatistics().RequestedBytes);
        region.Dispose();
    }

    [Fact]
    public void EveryLeaseKindInsideABorrowIsAdmitted()
    {
        NativeRegionKernel region = new(64, NativeMemoryReturn.Free);
        Local<int> first = region.LeaseFilled<int>(1, 1);
        int sum = first.Read(view =>
        {
            Local<int> filled = region.LeaseFilled<int>(2, 2);
            Local<int> uninitialized = region.LeaseUninitialized<int>(2);
            uninitialized.AsSpan().Fill(3);
            Local<int> initialized = region.LeaseInitialized<int>(2, static writer => writer.Fill(4));
            return view[0] + filled.AsSpan()[1] + uninitialized.AsSpan()[1] + initialized.AsSpan()[1];
        });

        Assert.Equal(10, sum);
        Assert.Equal(28, region.GetStatistics().RequestedBytes);
        region.Dispose();
    }

    [Fact]
    public void EveryLeaseKindAfterDisposalIsRefusedAsDisposed()
    {
        NativeRegionKernel region = new(64, NativeMemoryReturn.Free);
        region.Dispose();

        Assert.Throws<NativeAllocationDisposedException>(() => { _ = region.LeaseFilled<int>(2, 5); });
        Assert.Throws<NativeAllocationDisposedException>(() => { _ = region.LeaseUninitialized<int>(2); });
        Assert.Throws<NativeAllocationDisposedException>(() => { _ = region.LeaseInitialized<int>(2, static writer => writer.Fill(6)); });
    }

    // A disposed default region's kernel waits in its thread's reuse slot; the
    // next region reactivates it under a new generation.
    [Fact]
    public void ARegionDisposedIntoTheSlotRefusesEveryLeaseKindBeforeAndAfterReuse()
    {
        TestThread.Run(static () =>
        {
            NativeRegion first = new();
            Assert.Equal(1, first.Lease<int>(1, 1).AsSpan()[0]);
            first.Dispose();
            Assert.Equal(3, CountRefusedAsDisposed(first));

            NativeRegion second = new();
            Assert.Equal(3, CountRefusedAsDisposed(first));
            Assert.Equal(2, second.Lease<int>(1, 2).AsSpan()[0]);
            second.Dispose();
        });
    }

    private static int CountRefusedAsDisposed(NativeRegion region)
    {
        int refused = 0;
        try
        {
            _ = region.Lease<int>(1, 3);
        }
        catch (NativeAllocationDisposedException)
        {
            refused++;
        }

        try
        {
            _ = region.LeaseUninitialized<int>(1);
        }
        catch (NativeAllocationDisposedException)
        {
            refused++;
        }

        try
        {
            _ = region.Lease<int>(1, static writer => writer.Fill(3));
        }
        catch (NativeAllocationDisposedException)
        {
            refused++;
        }

        return refused;
    }
}
