using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolProcessTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void FilledScopePublishesExactPrefixAndReturnsAfterResult(int length)
    {
        using NativePool<int> pool = new(8, NativeMemoryReturn.ToNativeMemory);
        int result = pool.Process(length, 42, 11, (values, state) =>
        {
            Assert.Equal(length, values.Length);
            Assert.Equal(-1, values.IndexOfAnyExcept(42));
            Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
            Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
            Assert.Equal(0, pool.CurrentInitializationCountForTest);
            Assert.Equal(length * 4, pool.GetStatistics().RequestedBytes);
            Assert.Equal(length * 4, pool.CaptureDiagnosticSnapshot().InitializedPayloadBytes);
            values.Fill(state);
            return state + values.Length;
        });
        Assert.Equal(length + 11, result);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        using Pooled<int> reused = pool.Rent(length, 7);
        Assert.Equal(-1, reused.Read(static view => view.AsSpan().IndexOfAnyExcept(7)));
    }

    [Fact]
    public void FailureReturnsTheScopeAfterNestedGrowthReplacesTheMetadataBank()
    {
        using NativePool<int> pool = new(2, NativeMemoryReturn.ToNativeMemory);
        OperationCanceledException expected = new("processor failure");
        OperationCanceledException actual = Assert.Throws<OperationCanceledException>(() =>
            pool.Process<int, int>(2, 7, 0, (values, _) =>
            {
                using Pooled<int> first = pool.Rent(2, 11);
                using Pooled<int> second = pool.Rent(2, 13);
                using Pooled<int> third = pool.Rent(2, 17);
                using Pooled<int> fourth = pool.Rent(2, 19);
                Assert.True(pool.CurrentBankCapacitiesForTest.Slabs > 4);
                Assert.Equal(5, pool.CurrentAllocationRecordCountForTest);
                Assert.Equal(40, pool.GetStatistics().RequestedBytes);
                Assert.Equal(-1, values.IndexOfAnyExcept(7));
                throw expected;
            }));
        Assert.Same(expected, actual);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(0, pool.GetStatistics().RequestedBytes);
        Assert.Equal(40, pool.GetStatistics().PeakInitializedPayloadBytes);
        using Pooled<int> reused = pool.Rent(2, 23);
        Assert.Equal(46, reused.Read(static view => view[0] + view[1]));
    }

    [Fact]
    public void NestedScopesRemainActiveAcrossInnerSuccessAndFailure()
    {
        using NativePool<int> pool = new(2, NativeMemoryReturn.ToNativeMemory);
        int result = pool.Process(2, 7, 0, (outer, _) =>
        {
            Assert.Equal(22, pool.Process(2, 11, 0, (inner, _) =>
            {
                Assert.Equal(2, pool.CurrentGenerationActiveOperationsForTest);
                return inner[0] + inner[1];
            }));
            Assert.Throws<OperationCanceledException>(() => pool.Process<int, int>(2, 13, 0,
                static (_, _) => throw new OperationCanceledException()));
            Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
            Assert.Throws<InvalidOperationException>(pool.Dispose);
            Assert.Throws<InvalidOperationException>(pool.Retire);
            Assert.Equal((nuint)64, pool.TrimRetainedMemory());
            Assert.Equal(8, pool.GetStatistics().RequestedBytes);
            return outer[0] + outer[1];
        });
        Assert.Equal(14, result);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal((nuint)64, pool.TrimRetainedMemory());
    }

    [Fact]
    public void OldTokensCannotBorrowOrReturnThePrivateRental()
    {
        using NativePool<int> pool = new(2, NativeMemoryReturn.ToNativeMemory);
        using (Pooled<int> old = pool.Rent(2, 5)) { }
        Assert.Equal(14, pool.Process(2, 7, 0, (values, _) =>
        {
            Assert.Throws<NativeAllocationReturnedException>(() => pool.EnterBorrow(0, 1, "stale"));
            Assert.Throws<NativeAllocationReturnedException>(() => pool.Return(0, 1, 2));
            Assert.Throws<InvalidOperationException>(() => pool.Return(0, 2, 2));
            Assert.Throws<InvalidOperationException>(() => pool.Move(0, 2));
            Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
            return values[0] + values[1];
        }));
    }

    [Fact]
    public void RejectedEntryNeverInvokesProcessorOrClaimsASlab()
    {
        NativeMemoryBudget budget = new(64);
        using NativePool<int> pool = new(budget, 2, 0, NativeMemoryReturn.ToNativeMemory);
        int calls = 0;
        int CountCall(scoped Span<int> _, int __) => ++calls;
        Assert.Throws<ArgumentNullException>(() => pool.Process<int, int>(2, 7, 0, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Process(-1, 7, 0, CountCall));
        using (Pooled<int> existing = pool.Rent(2, 11))
        {
            Assert.Throws<NativeMemoryBudgetExceededException>(() => pool.Process(2, 7, 0, CountCall));
            Assert.Equal(22, existing.Read(static view => view[0] + view[1]));
        }
        typeof(NativePool<int>).GetField("_leaseTokenCounter", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(pool, long.MaxValue);
        NativeOwnerStatistics before = pool.GetStatistics();
        Assert.Throws<InvalidOperationException>(() => pool.Process(2, 7, 0, CountCall));
        Assert.Equal(before, pool.GetStatistics());
        Assert.Equal(0, calls);
    }

    [Fact]
    public void FailedBackingAdmissionRollsBackBeforeProcessorEntry()
    {
        NativeMemoryBudget budget = new(128);
        using NativePool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory);
        NativeMemoryTestHooks.FailNextAllocation();
        Assert.Throws<NativeAllocationFailedException>(() => pool.Process(2, 7, 0, static (_, _) => 1));
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(22, pool.Process(2, 11, 0, static (values, _) => values[0] + values[1]));
    }

    [Fact]
    public void WrongThreadAndDisposedOwnerCannotEnterAProcessor()
    {
        using NativePool<int> pool = new(2, NativeMemoryReturn.ToNativeMemory);
        Exception? failure = null;
        Thread worker = new(() => failure = Record.Exception(() => pool.Process(2, 7, 0, static (_, _) => 1)));
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        Assert.IsType<NativeAllocationStateException>(failure);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        pool.Dispose();
        Assert.Throws<NativeAllocationDisposedException>(() => pool.Process(2, 7, 0, static (_, _) => 1));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("pool.Dispose();", "NAM1005")]
    [InlineData("pool.Retire();", "NAM1009")]
    public async Task AnalyzerTracksScopeOwnerAndSourceVisibleHelper(string before, string? expected)
    {
        string source = $$"""
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static int Run()
                {
                    using NativePool<int> pool = new(2, NativeMemoryReturn.ToNativeMemory);
                    {{before}}
                    int direct = pool.Process(2, 7, 0, static (values, state) => values[0] + values[1] + state);
                    return direct + Helper(pool);
                }
                private static int Helper(NativePool<int> pool) =>
                    pool.Process(processor: static (values, state) => values[0] + state, state: 0, value: 11, length: 2);
            }
            """;
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static item => item.Severity == DiagnosticSeverity.Error);
        var diagnostics = await AnalyzerContractTests.AnalyzeAsync(source);
        Assert.DoesNotContain(diagnostics, static item => string.Equals(item.Id, "AD0001", StringComparison.Ordinal));
        if (expected is not null)
            Assert.Contains(expected, AnalyzerContractTests.NativeDiagnostics(diagnostics), StringComparer.Ordinal);
        else
            Assert.Empty(AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Theory]
    [InlineData("pool.Dispose();")]
    [InlineData("pool.Retire();")]
    [InlineData("_ = pool.Process(1, 11, 0, static (inner, _) => inner[0]); pool.Retire();")]
    public async Task AnalyzerRejectsLifecycleChangesInsideTheOwningScope(string change)
    {
        string source = $$"""
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static int Run()
                {
                    NativePool<int> pool = new(2, NativeMemoryReturn.ToNativeMemory);
                    try
                    {
                        return pool.Process(2, 7, 0, (values, _) =>
                        {
                            {{change}}
                            return values[0];
                        });
                    }
                    finally { pool.Dispose(); }
                }
            }
            """;
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static item => item.Severity == DiagnosticSeverity.Error);
        var diagnostics = await AnalyzerContractTests.AnalyzeAsync(source);
        Assert.DoesNotContain(diagnostics, static item => string.Equals(item.Id, "AD0001", StringComparison.Ordinal));
        Assert.Contains("NAM1007", AnalyzerContractTests.NativeDiagnostics(diagnostics), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ConcurrentGenerationDiagnosticsRemainConservativeInsideCallback()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativeConcurrentPool<int> pool = new();
                    ConcurrentPooled<int> values = pool.Rent(1, static writer => writer.Fill(7));
                    values.Access(_ =>
                    {
                        pool.ReturnMemoryToGarbageCollector();
                        pool.LeaseFromMemory();
                        pool.ReturnMemoryToNativeMemory();
                    });
                    pool.Dispose();
                }
            }
            """;
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static item => item.Severity == DiagnosticSeverity.Error);
        var diagnostics = await AnalyzerContractTests.AnalyzeAsync(source);
        Assert.DoesNotContain(diagnostics, static item => string.Equals(item.Id, "AD0001", StringComparison.Ordinal));
        Assert.Equal(["NAM1017", "NAM1007"], AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public void ScopedSpanCannotEscapeThroughAProcessorResult()
    {
        const string source = """
            using System;
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static Span<int> Run()
                {
                    using NativePool<int> pool = new(2, NativeMemoryReturn.ToNativeMemory);
                    return pool.Process(2, 7, 0, static (values, _) => values);
                }
            }
            """;
        Assert.Contains(AnalyzerContractTests.Compile(source), static item =>
            item.Severity == DiagnosticSeverity.Error && string.Equals(item.Id, "CS9244", StringComparison.Ordinal));
    }
}
