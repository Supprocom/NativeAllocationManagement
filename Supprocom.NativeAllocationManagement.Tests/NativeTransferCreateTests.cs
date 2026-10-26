using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeTransferCreateTests
{
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861", Justification = "One-shot expected arrays stay local to the assertions.")]
    public void CreateCopiesOrFillsAndDisposesExactlyOnce()
    {
        NativeMemoryTestHooks.Reset();
        NativeTransfer<int> copied = NativeTransfer<int>.Create([3, 1, 4]);
        NativeTransfer<int> filled = NativeTransfer<int>.Create(5, 9);
        Assert.Equal(new[] { 3, 1, 4 }, copied.Read(static view => view.AsSpan().ToArray()));
        Assert.Equal(3, copied.Length);
        Assert.Equal(new[] { 9, 9, 9, 9, 9 }, filled.Read(static view => view.AsSpan().ToArray()));
        Assert.NotEqual(copied.Id, filled.Id);
        copied.Dispose();
        filled.Dispose();
        Assert.ThrowsAny<InvalidOperationException>(copied.Dispose);
        Assert.ThrowsAny<InvalidOperationException>(() => filled.Read(static view => view[0]));
        Assert.Equal(0, NativeMemoryTestHooks.Snapshot().OutstandingNativeBytes);
    }

    [Fact]
    public void EmptyAndNegativeLengths()
    {
        NativeTransfer<int> empty = NativeTransfer<int>.Create([]);
        Assert.Equal(0, empty.Length);
        empty.Dispose();
        Assert.Throws<ArgumentOutOfRangeException>(static () => NativeTransfer<int>.Create(-1, 0));
    }

    [Fact]
    public void ReturnedTransferStorageServesTheNextCreate()
    {
        NativeTransfer<long> first = NativeTransfer<long>.Create(8, 1);
        object control = first.ControlForTest!;
        first.Dispose();
        NativeTransfer<long> second = NativeTransfer<long>.Create(8, 2);
        Assert.Same(control, second.ControlForTest);
        Assert.Equal(16, second.Read(static view => view[0] + view[7] + 12));
        second.Dispose();
    }

    [Fact]
    public async Task CreatedTransferMovesToAnotherThreadAndReturnsThere()
    {
        NativeTransfer<int>? source = NativeTransfer<int>.Create([5, 6, 7]);
        NativeTransfer<int> moved = NativeTransfer<int>.Move(ref source);
        int sum = await Task.Run(() =>
        {
            try
            {
                return moved.Read(static view => view[0] + view[1] + view[2]);
            }
            finally
            {
                moved.Dispose();
            }
        });
        Assert.Equal(18, sum);
    }

    [Theory]
    [InlineData("NativeTransfer<int> t = NativeTransfer<int>.Create(4, 0);", "NAM1025")]
    [InlineData("NativeTransfer<int> t = NativeTransfer<int>.Create(4, 0); NativeTransfer<int> u = t; u.Dispose();", "NAM1021")]
    [InlineData("_ = NativeTransfer<int>.Create(4, 0);", "NAM1026")]
    public async Task AnalyzerTracksCreateLikeOtherAcquisitions(string body, string expected)
    {
        Assert.Contains(expected, await NativeIdsAsync(body), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("NativeTransfer<int> t = NativeTransfer<int>.Create(4, 0); t.Dispose();")]
    [InlineData("NativeTransfer<int> t = NativeTransfer<int>.Create([1, 2]); try { _ = t.Read(static v => v[0]); } finally { t.Dispose(); }")]
    [InlineData("NativeTransfer<int>? t = NativeTransfer<int>.Create(4, 0); NativeTransfer<int> u = NativeTransfer<int>.Move(ref t); u.Dispose();")]
    public async Task AnalyzerAcceptsDisposedOrMovedCreate(string body)
    {
        Assert.Empty(await NativeIdsAsync(body));
    }

    private static async Task<string[]> NativeIdsAsync(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            $$"""
            using System;
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    {{body}}
                }
            }
            """).ConfigureAwait(false);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id.StartsWith("CS", StringComparison.Ordinal) && diagnostic.Severity == DiagnosticSeverity.Error);
        return AnalyzerContractTests.NativeDiagnostics(diagnostics);
    }
}
