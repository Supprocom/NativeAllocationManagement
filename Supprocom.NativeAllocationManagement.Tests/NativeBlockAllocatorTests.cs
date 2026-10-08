using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeBlockAllocatorTests
{
    [Fact]
    public void ExtentIsOneImmutableFieldAndSurvivesDescriptorCopies()
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo extent = typeof(NativeBlock).GetField(nameof(NativeBlock.ByteLength), Fields)!;
        Assert.NotNull(extent);
        Assert.True(extent.IsInitOnly);
        Assert.Equal(typeof(nuint), extent.FieldType);
        Assert.Null(typeof(NativeBlock).GetProperty(nameof(NativeBlock.ByteLength), Fields));
        Assert.Equal(5, typeof(NativeBlock).GetFields(Fields).Length);
        NativeMemoryBudget budget = new(64);
        NativeBlock descriptor = new((IntPtr)17, 64, 3, budget, 5);
        NativeBlock copied = descriptor with { Pointer = IntPtr.Zero };
        Assert.Equal((nuint)64, copied.ByteLength);
        Assert.Equal(3, copied.MetricsEpoch);
        Assert.Same(budget, copied.Budget);
        Assert.Equal(5, copied.OwnerId);
        (IntPtr pointer, nuint bytes, long epoch, NativeMemoryBudget? copiedBudget, long owner) = copied;
        Assert.Equal(IntPtr.Zero, pointer);
        Assert.Equal((nuint)64, bytes);
        Assert.Equal(3, epoch);
        Assert.Same(budget, copiedBudget);
        Assert.Equal(5, owner);
        Assert.Equal((nuint)0, default(NativeBlock).ByteLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    public void TypedExtentsShareExactPhysicalAndBudgetContract(int count)
    {
        VerifyExtent<byte>(count);
        VerifyExtent<short>(count);
        VerifyExtent<int>(count);
        VerifyExtent<long>(count);
        VerifyExtent<float>(count);
        VerifyExtent<double>(count);
        VerifyExtent<Guid>(count);
        VerifyExtent<Vector4>(count);
    }

    [Fact]
    public void NegativeExtentRejectsBeforeAdmissionOrBacking()
    {
        NativeMemoryBudget budget = new(16);
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeBuilder<Guid>(budget, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeWorkspace<Guid>(budget, -1));
        Assert.Equal(0, budget.CaptureStatistics().PeakAdmittedBytes);
        Assert.Equal(before.AllocationCount, NativeMemoryDiagnostics.Snapshot().AllocationCount);
    }

    [Fact]
    public void BudgetRefusesLargeTypedExtentBeforeBackend()
    {
        NativeMemoryBudget budget = new(16);
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        NativeMemoryBudgetExceededException error = Assert.Throws<NativeMemoryBudgetExceededException>(() =>
            NativeBlockAllocator.Allocate(272, "TypedExtentTest", "Refused", budget,
                NativeOwnerIdentity.NextWithoutPreparation()));
        Assert.Equal((nuint)272, error.RequestedBytes);
        Assert.Equal(before.AllocationCount, NativeMemoryDiagnostics.Snapshot().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(1, budget.CaptureStatistics().RejectedAllocationCount);
    }

    [Fact]
    public void PublicTypedExtentCannotWrapBeforeBudgetAdmission()
    {
        NativeMemoryBudget budget = new(16);
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        if (IntPtr.Size == sizeof(long))
        {
            NativeMemoryBudgetExceededException error = Assert.Throws<NativeMemoryBudgetExceededException>(() =>
                new NativeBuilder<Guid>(budget, int.MaxValue));
            Assert.Equal((ulong)int.MaxValue * 16, (ulong)error.RequestedBytes);
        }
        else
        {
            Assert.Throws<OverflowException>(() => new NativeBuilder<Guid>(budget, int.MaxValue));
        }
        Assert.Equal(before.AllocationCount, NativeMemoryDiagnostics.Snapshot().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
    }

    [Fact]
    public void PreReservedExtentCommitsAndReturnsExactlyOnce()
    {
        NativeMemoryBudget budget = new(16);
        long ownerId = NativeOwnerIdentity.NextWithoutPreparation();
        budget.Reserve(16, ownerId);
        NativeBlock block = NativeBlockAllocator.Allocate(16, "TypedExtentTest", "Reserved", budget,
            ownerId, alreadyReserved: true);
        try
        {
            Assert.Equal((nuint)16, block.ByteLength);
            Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
            Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        }
        finally { NativeBlockAllocator.Free(block); }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void BackendFailurePreservesTypedErrorAndCancelsAdmission()
    {
        NativeMemoryTestHooks.FailNextAllocation();
        NativeMemoryBudget budget = new(16);
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        NativeAllocationFailedException error = Assert.Throws<NativeAllocationFailedException>(() =>
            NativeBlockAllocator.Allocate(16, "TypedExtentTest", "Failed", budget,
                NativeOwnerIdentity.NextWithoutPreparation()));
        Assert.Equal((nuint)16, error.RequestedBytes);
        Assert.Equal("TypedExtentTest", error.OwnerKind);
        Assert.Equal("Failed", error.Operation);
        Assert.Equal(before.AllocationCount, NativeMemoryDiagnostics.Snapshot().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FailedAllocationCount);
    }

    private static void VerifyExtent<T>(int count) where T : unmanaged
    {
        nuint bytes = checked((nuint)count * (nuint)Unsafe.SizeOf<T>());
        NativeMemoryBudget budget = new((long)bytes);
        long ownerId = NativeOwnerIdentity.NextWithoutPreparation();
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        NativeBlock block = NativeBlockAllocator.Allocate(bytes, "TypedExtentTest", "Acquire", budget, ownerId);
        try
        {
            Assert.Equal(bytes, block.ByteLength);
            Assert.Equal(ownerId, block.OwnerId);
            Assert.Same(budget, block.Budget);
            Assert.Equal(count == 0, block.Pointer == 0);
            Assert.Equal((long)bytes, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(before.OutstandingNativeBytes + (long)bytes,
                NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes);
        }
        finally { NativeBlockAllocator.Free(block); }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(count == 0 ? 0 : 1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(count == 0 ? 0 : 1, budget.CaptureStatistics().FreeCount);
        Assert.Equal(before.OutstandingNativeBytes, NativeMemoryDiagnostics.Snapshot().OutstandingNativeBytes);
    }
}
