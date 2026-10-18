using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolFilledRentTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void FilledRentsPublishCompleteRangesAcrossReservationReuseAndGrowth(int length)
    {
        using NativePool<int> pool = new(8, NativeMemoryReturn.ToNativeMemory);
        using (Pooled<int> lease = pool.Rent(length, 42))
        {
            Assert.Equal(length, lease.Length);
            Assert.True(lease.Capacity >= length);
            Assert.All(lease.Read(static view => view.AsSpan().ToArray()), static item => Assert.Equal(42, item));
            Assert.Equal(0, pool.CurrentInitializationCountForTest);
            Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
            Assert.Equal(length * 4, pool.GetStatistics().RequestedBytes);
            Assert.Equal(length * 4, pool.CaptureDiagnosticSnapshot().InitializedPayloadBytes);
        }
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        using Pooled<int> reused = pool.Rent(length, 11);
        Assert.All(reused.Read(static view => view.AsSpan().ToArray()), static item => Assert.Equal(11, item));
    }

    [Fact]
    public void FillSupportsEveryUnmanagedShapeWithoutAWriterObject()
    {
        CheckFilled<byte>(7);
        CheckFilled<ushort>(0x0707);
        CheckFilled(0x07070707);
        CheckFilled(0x0707070707070707L);
        CheckFilled(new Guid("07070707-0707-0707-0707-070707070707"));
        CheckFilled(new System.Numerics.Vector3(7, 11, 13));
    }

    [Fact]
    public void CapacityFailureAndInvalidLengthCannotPublishOrCorruptAnExistingLease()
    {
        NativeMemoryBudget budget = new(64);
        using NativePool<int> pool = new(budget, 2, 0, NativeMemoryReturn.ToNativeMemory);
        using Pooled<int> current = pool.Rent(2, 7);
        Assert.Throws<ArgumentOutOfRangeException>(() => { using Pooled<int> invalid = pool.Rent(-1, 42); });
        Assert.Throws<NativeMemoryBudgetExceededException>(() => { using Pooled<int> invalid = pool.Rent(2, 42); });
        Assert.Equal(14, current.Read(static view => view[0] + view[1]));
        Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(8, pool.GetStatistics().RequestedBytes);
        Assert.Equal(64, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void NativeAllocationFailureRollsBackBeforeAnyFilledAuthorityIsPublished()
    {
        NativeMemoryBudget budget = new(128);
        using NativePool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory);
        NativeMemoryTestHooks.FailNextAllocation();
        Assert.Throws<NativeAllocationFailedException>(() => { using Pooled<int> invalid = pool.Rent(8, 7); });
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        using Pooled<int> recovered = pool.Rent(8, 11);
        Assert.Equal(22, recovered.Read(static view => view[0] + view[7]));
    }

    [Fact]
    public void ExhaustedTokensCannotTakeAFreeSlab()
    {
        using NativePool<int> pool = new(2, NativeMemoryReturn.ToNativeMemory);
        using (Pooled<int> lease = pool.Rent(2, 7)) { }
        NativeOwnerStatistics before = pool.GetStatistics();
        typeof(NativePool<int>).GetField("_leaseTokenCounter", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(pool, long.MaxValue);
        Assert.Throws<InvalidOperationException>(() => { using Pooled<int> invalid = pool.Rent(2, 42); });
        Assert.Equal(before, pool.GetStatistics());
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public void MoveReuseAndEnteredBorrowsRetainTheirOriginalAuthorityChecks()
    {
        using NativePool<int> pool = new(2, NativeMemoryReturn.ToNativeMemory);
        Pooled<int> source = pool.Rent(2, 7);
        Pooled<int> stale = source;
        using (Pooled<int> moved = Pooled<int>.Move(ref source))
        {
            AssertReturned(stale);
            moved.Access(_ =>
            {
                Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
                Assert.Throws<InvalidOperationException>(() => pool.Return(0, 2, 2));
                Assert.Throws<InvalidOperationException>(() => pool.Move(0, 2));
                Assert.Throws<InvalidOperationException>(pool.Dispose);
                Assert.Equal((nuint)0, pool.TrimRetainedMemory());
            });
        }
        using Pooled<int> reused = pool.Rent(2, 11);
        AssertReturned(stale);
        Assert.Equal(22, reused.Read(static view => view[0] + view[1]));
    }

    [Fact]
    public void CallbackFailureStillRollsBackWhileNestedFilledRentRemainsIndependent()
    {
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Assert.Throws<OperationCanceledException>(() =>
        {
            using Pooled<int> unpublished = pool.Rent(2, writer =>
            {
                using Pooled<int> nested = pool.Rent(2, 42);
                Assert.Equal(1, pool.CurrentInitializationCountForTest);
                Assert.Equal(2, pool.CurrentAllocationRecordCountForTest);
                Assert.Equal(84, nested.Read(static view => view[0] + view[1]));
                writer.Fill(7);
                throw new OperationCanceledException();
            });
        });
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        Assert.Equal(0, pool.CurrentInitializationCountForTest);
    }

    [Fact]
    public void ThreadAndDisposedChecksRunBeforeFill()
    {
        using NativePool<int> pool = new(2, NativeMemoryReturn.ToNativeMemory);
        Exception? failure = null;
        Thread worker = new(() => failure = Record.Exception(() =>
        {
            using Pooled<int> invalid = pool.Rent(2, 42);
        }));
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        Assert.IsType<NativeAllocationStateException>(failure);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        pool.Dispose();
        Assert.Throws<NativeAllocationDisposedException>(() => { using Pooled<int> invalid = pool.Rent(2, 42); });
    }

    [Theory]
    [InlineData("using Pooled<int> lease = pool.Rent(2, 7);", false)]
    [InlineData("Pooled<int> lease = pool.Rent(2, 7);", true)]
    [InlineData("using Pooled<int> lease = pool.Rent(value: 7, length: 2);", false)]
    public async Task FilledOverloadRetainsAnalyzerOwnershipObligations(string body, bool rejected)
    {
        string source = $$"""
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new(2, NativeMemoryReturn.ToNativeMemory);
                    {{body}}
                }
            }
            """;
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static item => item.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var diagnostics = await AnalyzerContractTests.AnalyzeAsync(source);
        Assert.DoesNotContain(diagnostics, static item => string.Equals(item.Id, "AD0001", StringComparison.Ordinal));
        Assert.Equal(rejected, diagnostics.Any(static item => string.Equals(item.Id, "NAM1003", StringComparison.Ordinal)));
    }

    private static void CheckFilled<T>(T value) where T : unmanaged
    {
        using NativePool<T> pool = new(8, NativeMemoryReturn.ToNativeMemory);
        using Pooled<T> lease = pool.Rent(8, value);
        Assert.All(lease.Read(static view => view.AsSpan().ToArray()), item => Assert.Equal(value, item));
        Assert.Equal(8, pool.GetStatistics().RequestedBytes / Unsafe.SizeOf<T>());
    }

    private static void AssertReturned(scoped Pooled<int> value)
    {
        try { value.Dispose(); Assert.Fail("Stale authority cannot return a filled slab."); }
        catch (NativeAllocationReturnedException) { }
    }
}
