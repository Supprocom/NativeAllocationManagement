using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeSpanProcessorTests
{
    [Fact]
    public void PoolScopedProcessorFillsRunsAndReturnsTheSlab()
    {
        using NativePool<int> pool = new(preLease: 8);
        int offset = 5;
        Assert.Equal(7 + 5, pool.Process<int, int, WriteIndexPlusState>(8, -1, in offset));
        Assert.Equal(-8, pool.Process<int, int, SumValues>(8, -1, in offset));
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        Assert.Equal(1, pool.GetStatistics().AvailableSegmentCount);
    }

    [Fact]
    public void PoolScopedProcessorFailureReturnsTheSlab()
    {
        using NativePool<int> pool = new(preLease: 4);
        int state = 0;
        Assert.Throws<FormatException>(() => pool.Process<int, int, Throwing>(4, 0, in state));
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        using Pooled<int> lease = pool.Rent(4, 3);
        Assert.Equal(12, lease.Process<int, int, SumValues>(4, in state));
    }

    [Fact]
    public void LeaseProcessorsBorrowTheirValidatedRanges()
    {
        int state = 2;
        using NativePool<int> pool = new(preLease: 8);
        Pooled<int> pooled = pool.Rent(8, 1);
        Assert.Equal(5 + 2, pooled.Process<int, int, WriteIndexPlusState>(6, in state));
        Assert.Equal(2 + 3 + 4 + 5 + 6 + 7 + 1 + 1, pooled.Process<int, int, SumValues>(8, in state));
        Assert.IsType<ArgumentOutOfRangeException>(CaptureProcess(pooled, 9));
        pooled.Dispose();

        using NativeArena arena = new(preAllocateBytes: 1024);
        ArenaLease<int> scratch = arena.Scratch<int>(4, 1);
        Assert.Equal(3 + 2, scratch.Process<int, int, WriteIndexPlusState>(in state));
        Assert.Equal(2 + 3 + 4 + 5, scratch.Process<int, int, SumValues>(in state));

        using (NativeRegion region = new(256, NativeMemoryReturn.ToNativeMemory))
        {
            Local<int> local = region.Lease<int>(3, 1);
            Assert.Equal(2 + 2, local.Process<int, int, WriteIndexPlusState>(in state));
            Assert.Equal(2 + 3 + 4, local.Process<int, int, SumValues>(in state));
        }

        using NativeWorkspace<int> workspace = new(preLease: 4);
        Assert.Equal(3 + 2, workspace.Process<int, int, WriteIndexPlusState>(4, in state));
        Assert.Equal(2 + 3 + 4 + 5, workspace.Process<int, int, SumValues>(4, in state));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => workspace.Process<int, int, SumValues>(4, in state, cancellation.Token));
        Assert.Equal(2 + 3 + 4 + 5, workspace.Process<int, int, SumValues>(4, in state));
    }

    [Fact]
    public void LeaseProcessorHoldsOneBorrowWhileItRuns()
    {
        using NativePool<int> pool = new(preLease: 4);
        using Pooled<int> lease = pool.Rent(4, 0);
        NativePool<int> owner = pool;
        Holder.Pool = owner;
        try
        {
            int state = 0;
            Assert.Equal(1, lease.Process<int, int, ReturnsDuringBorrow>(4, in state));
        }
        finally
        {
            Holder.Pool = null;
        }
    }

    [Fact]
    public void SmartPointerAndConcurrentProcessorsReadTheirOwnedRanges()
    {
        int state = 1;
        NativeTransfer<int> direct = BuildTransfer([3, 4, 5]);
        Assert.Equal(12, direct.Process<int, int, SumValues>(in state));
        Assert.Equal(2 + 1, direct.Process<int, int, WriteIndexPlusState>(in state));
        NativeTransfer<int>? source = direct;
        NativeShared<int> shared = NativeShared<int>.Create(ref source, new NativeSharingPreparation(2, 0));
        Assert.Equal(1 + 2 + 3, shared.Process<int, int, SumReadOnly>(in state));
        shared.Dispose();

        using NativeConcurrentPool<int> pool = new(preLease: 8);
        NativeTransfer<int> pooled = pool.RentTransferable(3, static writer => writer.Fill(2));
        Assert.Equal(6, pooled.Process<int, int, SumValues>(in state));
        pooled.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pooled.Process<int, int, SumValues>(in state));
        using ConcurrentPooled<int> lease = pool.Rent(4, static writer => writer.Fill(5));
        Assert.Equal(10, lease.Process<int, int, SumValues>(2, in state));
    }

    [Theory]
    [InlineData("int s = 1; using NativeBuilder<int> builder = new(4); builder.Append(1); NativeTransfer<int> t = builder.Complete(); try { _ = t.Process<int, int, P>(in s); } finally { t.Dispose(); }")]
    [InlineData("int s = 1; using NativeConcurrentPool<int> pool = new(4); using ConcurrentPooled<int> lease = pool.Rent(4, static w => w.Fill(0)); _ = lease.Process<int, int, P>(4, in s);")]
    [InlineData("int s = 1; using NativePool<int> pool = new(4); _ = pool.Process<int, int, P>(4, 0, in s);")]
    [InlineData("int s = 1; using NativePool<int> pool = new(4); using Pooled<int> lease = pool.Rent(4, 0); _ = lease.Process<int, int, P>(4, in s);")]
    [InlineData("int s = 1; using NativeArena arena = new(); { ArenaLease<int> lease = arena.Scratch<int>(4, 0); _ = lease.Process<int, int, P>(in s); } arena.Reset();")]
    [InlineData("int s = 1; using (NativeRegion region = new(64, NativeMemoryReturn.ToNativeMemory)) { Local<int> lease = region.Lease<int>(4, 0); _ = lease.Process<int, int, P>(in s); }")]
    [InlineData("int s = 1; using NativeWorkspace<int> workspace = new(4); _ = workspace.Process<int, int, P>(4, in s);")]
    public async Task StaticProcessorsAreAcceptedOwnershipShapes(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            $$"""
            using System;
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                private readonly struct P : INativeSpanProcessor<int, int, int>
                {
                    public static int Invoke(scoped Span<int> values, scoped in int state) => values.Length + state;
                }

                public static void Run()
                {
                    {{body}}
                }
            }
            """);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(diagnostics));
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id.StartsWith("CS", StringComparison.Ordinal) && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task StaticProcessorsDoNotHideAnUnreturnedLease()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                private readonly struct P : INativeSpanProcessor<int, int, int>
                {
                    public static int Invoke(scoped Span<int> values, scoped in int state) => values.Length + state;
                }

                public static void Run()
                {
                    int s = 1;
                    using NativePool<int> pool = new(4);
                    Pooled<int> lease = pool.Rent(4, 0);
                    _ = lease.Process<int, int, P>(4, in s);
                }
            }
            """);
        Assert.Contains("NAM1003", AnalyzerContractTests.NativeDiagnostics(diagnostics), StringComparer.Ordinal);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "The test captures the argument failure for assertions.")]
    private static Exception CaptureProcess(Pooled<int> lease, int length)
    {
        int state = 0;
        try
        {
            _ = lease.Process<int, int, SumValues>(length, in state);
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("The oversized processor range was accepted.");
    }

    private static class Holder
    {
        internal static NativePool<int>? Pool;
    }

    private readonly struct WriteIndexPlusState : INativeSpanProcessor<int, int, int>
    {
        public static int Invoke(scoped Span<int> values, scoped in int state)
        {
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = index + state;
            }

            return values.IsEmpty ? state : values[^1];
        }
    }

    private readonly struct SumValues : INativeSpanProcessor<int, int, int>
    {
        public static int Invoke(scoped Span<int> values, scoped in int state)
        {
            int sum = 0;
            foreach (ref readonly int value in values)
            {
                sum += value;
            }

            return sum;
        }
    }

    private readonly struct SumReadOnly : INativeReadOnlySpanProcessor<int, int, int>
    {
        public static int Invoke(scoped ReadOnlySpan<int> values, scoped in int state)
        {
            int sum = 0;
            foreach (ref readonly int value in values)
            {
                sum += value;
            }

            return sum;
        }
    }

    private static NativeTransfer<int> BuildTransfer(int[] values)
    {
        using NativeBuilder<int> builder = new(values.Length);
        builder.Append(values);
        return builder.Complete();
    }

    private readonly struct Throwing : INativeSpanProcessor<int, int, int>
    {
        public static int Invoke(scoped Span<int> values, scoped in int state) =>
            throw new FormatException("Expected processor failure.");
    }

    private readonly struct ReturnsDuringBorrow : INativeSpanProcessor<int, int, int>
    {
        public static int Invoke(scoped Span<int> values, scoped in int state)
        {
            // The pool's statistics see one entered borrow on the active lease.
            Assert.Equal(1, Holder.Pool!.CurrentGenerationActiveOperationsForTest);
            return 1;
        }
    }
}
