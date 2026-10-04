namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeRegionInitializerSafetyTests
{
    [Theory]
    [InlineData(NativeMemoryReturn.ToNativeMemory)]
    [InlineData(NativeMemoryReturn.ToGarbageCollector)]
    public void InitializerCannotCloseItsBackingBeforePublication(NativeMemoryReturn policy)
    {
        NativeMemoryBudget budget = new(512);
        NativeRegionKernel region = new(16, policy, budget);
        long committed = budget.CaptureStatistics().CommittedBytes;
        Local<int> value = region.LeaseInitialized<int>(1, writer =>
        {
            NativeAllocationInUseException failure = Assert.Throws<NativeAllocationInUseException>(region.Dispose);
            Assert.Equal(1, failure.ActiveOperationCount);
            Assert.Equal(committed, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(NativeOwnerLifecycle.Active, region.GetDiagnosticSnapshot().Lifecycle);
            writer.Write(42);
        });
        Assert.Equal(42, value.Read(static view => view[0]));
        Assert.Equal(4, region.GetStatistics().RequestedBytes);
        region.Dispose();
    }

    [Fact]
    public void NestedInitializationRejectsBeforeChangingTheCursorOrLogicalDemand()
    {
        NativeMemoryBudget budget = new(512);
        NativeRegionKernel region = new(16, NativeMemoryReturn.ToNativeMemory, budget);
        Local<int> value = region.LeaseInitialized<int>(1, writer =>
        {
            NativeAllocationInUseException failure = Assert.Throws<NativeAllocationInUseException>(() =>
            {
                region.LeaseInitialized<int>(1, static nested => nested.Write(99));
            });
            Assert.Equal(1, failure.ActiveOperationCount);
            Assert.Equal(0, region.GetStatistics().RequestedBytes);
            writer.Write(42);
        });
        Assert.Equal(42, value.Read(static view => view[0]));
        Assert.Equal(4, region.GetStatistics().RequestedBytes);
        Local<int> next = region.LeaseInitialized<int>(1, static writer => writer.Write(7));
        Assert.Equal(7, next.Read(static view => view[0]));
        Assert.Equal(8, region.GetStatistics().RequestedBytes);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        region.Dispose();
    }

    [Fact]
    public void FailedInitializerRollsBackAndReopensAdmission()
    {
        NativeMemoryBudget budget = new(512);
        NativeRegionKernel region = new(16, NativeMemoryReturn.ToNativeMemory, budget);
        Assert.Throws<InvalidOperationException>(() =>
        {
            region.LeaseInitialized<int>(2, static writer =>
            {
                writer.Write(1);
                throw new InvalidOperationException("Injected region initializer failure.");
            });
        });
        Assert.Equal(0, region.GetStatistics().RequestedBytes);
        Local<int> value = region.LeaseInitialized<int>(4, static writer => writer.Fill(42));
        Assert.Equal(42, value.Read(static view => view[3]));
        Assert.Equal(16, region.GetStatistics().RequestedBytes);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        region.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void ExistingInitializedAccessAndOrdinaryBorrowAllocationRemainPermitted()
    {
        NativeMemoryBudget budget = new(512);
        NativeRegionKernel region = new(32, NativeMemoryReturn.ToNativeMemory, budget);
        Local<int> first = region.LeaseInitialized<int>(1, static writer => writer.Write(17));
        first.Access(_ =>
        {
            Local<int> second = region.LeaseInitialized<int>(1, static writer => writer.Write(19));
            Assert.Equal(19, second.Read(view =>
            {
                NativeAllocationInUseException failure = Assert.Throws<NativeAllocationInUseException>(region.Dispose);
                Assert.Equal(2, failure.ActiveOperationCount);
                return view[0];
            }));
        });
        Assert.Equal(17, first.Read(static view => view[0]));
        Assert.Equal(8, region.GetStatistics().RequestedBytes);
        region.Dispose();
    }

    [Fact]
    public void InitializerCanEnterABoundedBorrowAndReportsBothActiveOperations()
    {
        NativeRegionKernel region = new(32, NativeMemoryReturn.ToNativeMemory);
        Local<int> value = region.LeaseInitialized<int>(1, writer =>
        {
            region.EnterBorrow("initializer bounded borrow test");
            try
            {
                NativeAllocationInUseException failure = Assert.Throws<NativeAllocationInUseException>(region.Dispose);
                Assert.Equal(2, failure.ActiveOperationCount);
            }
            finally { region.ExitBorrow(); }
            writer.Write(19);
        });
        Assert.Equal(19, value.Read(static view => view[0]));
        Assert.Equal(4, region.GetStatistics().RequestedBytes);
        region.Dispose();
    }

    [Fact]
    public void InitializationAdmissionDoesNotAllocateDuringBoundedOrdinaryExecution()
    {
        NativeMemoryBudget budget = new(8_192);
        using NativeRegion region = new(budget, 4_004, NativeMemoryReturn.ToNativeMemory);
        NativeLeaseInitializer<int> fill = static writer => writer.Write(42);
        NativeLeaseFunc<int, int> read = static view => view[0];
        Assert.Equal(42, region.Lease(1, fill).Read(read));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_000; index++)
        {
            if (region.Lease(1, fill).Read(read) != 42)
            {
                throw new InvalidOperationException("Region output parity failed.");
            }
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(4_004, region.GetStatistics().RequestedBytes);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
    }
}
