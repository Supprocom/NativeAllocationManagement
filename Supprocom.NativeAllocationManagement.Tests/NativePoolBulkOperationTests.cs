using System.Diagnostics.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolBulkOperationTests
{
    private static readonly int[] Input = [3, 5, 7, 11];

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void BulkOperationsPreserveExactRangesAndActualHistories(int length)
    {
        using NativePool<int> pool = new(4, NativeMemoryReturn.ToNativeMemory);
        using Pooled<int> lease = pool.Rent(length, 42);
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        Span<int> output = stackalloc int[5];
        output.Fill(-1);
        lease.CopyFrom(Input.AsSpan(0, length));
        lease.CopyTo(output);
        Assert.True(output[..length].SequenceEqual(Input.AsSpan(0, length)));
        Assert.Equal(-1, output[length..].IndexOfAnyExcept(-1));
        lease.Clear();
        Assert.Equal(-1, lease.Read(static view => view.AsSpan().IndexOfAnyExcept(0)));
        NativeMemoryStatistics after = NativeMemoryDiagnostics.Snapshot();
        Assert.Equal(length * 8, after.CopiedBytes - before.CopiedBytes);
        Assert.Equal(length == 0 ? 0 : 1, after.StorageClearCount - before.StorageClearCount);
        Assert.Equal(length * 4, after.StorageClearBytes - before.StorageClearBytes);
        Assert.Equal(length * 4, after.WrittenClearBytes - before.WrittenClearBytes);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
    }

    [Theory]
    [InlineData("Clear")]
    [InlineData("CopyFrom")]
    [InlineData("CopyTo")]
    public void DefaultStaleForeignAndDisposedAuthorityCannotReachPayload(string operation)
    {
        Assert.Throws<NativeAllocationUninitializedException>(() => Execute(default, operation));
        using NativePool<int> pool = new(4, NativeMemoryReturn.ToNativeMemory);
        using (Pooled<int> original = pool.Rent(4, 42))
        {
            Exception? failure = null;
            Thread worker = new(() => failure = Record.Exception(() => ExecuteAlias(pool, 1, operation)));
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal(operation, Assert.IsType<NativeAllocationStateException>(failure).Operation);
            Assert.Equal(-1, original.Read(static view => view.AsSpan().IndexOfAnyExcept(42)));
        }
        using (Pooled<int> current = pool.Rent(4, 17))
        {
            NativeAllocationReturnedException stale = Assert.Throws<NativeAllocationReturnedException>(() => ExecuteAlias(pool, 1, operation));
            Assert.Equal(operation, stale.Operation);
            Assert.Equal(0, stale.ActiveOperationCount);
            Assert.Equal(-1, current.Read(static view => view.AsSpan().IndexOfAnyExcept(17)));
        }
        pool.Dispose();
        Assert.Throws<NativeAllocationDisposedException>(() => ExecuteAlias(pool, 2, operation));
    }

    [Fact]
    public void SameStartBulkCopiesPreserveTheOuterBorrowAndDoNotInventHistory()
    {
        using NativePool<int> pool = new(4, NativeMemoryReturn.ToNativeMemory);
        using Pooled<int> lease = pool.Rent(4, 42);
        long before = NativeMemoryDiagnostics.Snapshot().CopiedBytes;
        lease.Access(view =>
        {
            // Internal fixture authority reproduces an alias without capturing a ref struct.
            Pooled<int> alias = new(pool, 0, 1, 4, 4);
            alias.CopyFrom(view.AsSpan());
            alias.CopyTo(view.AsSpan());
            Assert.Equal(-1, view.AsSpan().IndexOfAnyExcept(42));
            Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
            Assert.Throws<InvalidOperationException>(() => pool.Return(0, 1, 4));
        });
        Assert.Equal(before, NativeMemoryDiagnostics.Snapshot().CopiedBytes);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
    }

    [Fact]
    public void InvalidCopyLengthRetainsArgumentFailureBeforeDefaultAuthorityFailure()
    {
        Assert.Throws<ArgumentException>(static () =>
        {
            Pooled<int> value = default;
            value.CopyFrom(Input);
        });
        using NativePool<int> pool = new(4, NativeMemoryReturn.ToNativeMemory);
        using Pooled<int> current = pool.Rent(4, 42);
        Assert.Throws<ArgumentException>(() =>
        {
            Pooled<int> alias = new(pool, 0, 1, 4, 4);
            alias.CopyFrom([]);
        });
        Assert.Throws<ArgumentException>(() =>
        {
            Pooled<int> alias = new(pool, 0, 1, 4, 4);
            alias.CopyTo([]);
        });
        Assert.Equal(-1, current.Read(static view => view.AsSpan().IndexOfAnyExcept(42)));
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
    }

    [SuppressMessage("Reliability", "CA2000", Justification = "This adversarial fixture forges a non-owning stale or foreign-thread alias. The test retains and disposes the actual owning lease; disposing the forged authority would obscure the tested rejection or return another lease.")]
    private static void ExecuteAlias(NativePool<int> pool, long token, string operation)
    {
        Pooled<int> alias = new(pool, 0, token, 4, 4);
        Execute(alias, operation);
    }

    private static void Execute(scoped Pooled<int> lease, string operation)
    {
        Span<int> output = stackalloc int[4];
        switch (operation)
        {
            case "Clear": lease.Clear(); break;
            case "CopyFrom": lease.CopyFrom(Input.AsSpan(0, lease.Length)); break;
            case "CopyTo": lease.CopyTo(output); break;
            default: throw new ArgumentException("Unknown fixture operation.", nameof(operation));
        }
    }
}
