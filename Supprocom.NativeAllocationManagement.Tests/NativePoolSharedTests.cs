using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolSharedTests
{
    [Fact]
    public void SharedIsOneActivePoolPerThread()
    {
        NativePool<int> first = NativePool<int>.Shared;
        Assert.Same(first, NativePool<int>.Shared);
        Assert.NotSame(first, NativePool<long>.Shared);

        NativePool<int>? other = null;
        Thread thread = new(() => other = NativePool<int>.Shared);
        thread.Start();
        thread.Join();
        Assert.NotNull(other);
        Assert.NotSame(first, other);
        first.Dispose();
    }

    [Fact]
    public void DisposedSharedPoolIsReplacedOnNextAccess()
    {
        NativePool<int> first = NativePool<int>.Shared;
        first.Dispose();
        NativePool<int> second = NativePool<int>.Shared;
        Assert.NotSame(first, second);

        using (Pooled<int> lease = second.Rent(16, 7))
        {
            Assert.Equal(7, lease.AsSpan()[15]);
        }

        second.Dispose();
    }

    [Fact]
    public void SharedRentalsReuseRetainedStorage()
    {
        NativePool<int> pool = NativePool<int>.Shared;
        using (Pooled<int> lease = NativePool<int>.Shared.Rent(32, 0))
        {
            lease.AsSpan()[0] = 1;
        }

        long allocations = NativeMemoryTestHooks.Snapshot().AllocationCount;
        using (Pooled<int> lease = NativePool<int>.Shared.Rent(32, 0))
        {
            Assert.Equal(0, lease.AsSpan()[0]);
        }

        Assert.Equal(allocations, NativeMemoryTestHooks.Snapshot().AllocationCount);
        Assert.Equal(16, NativePool<int>.Shared.Process(16, 0, 0, static (values, _) => values.Length));
        pool.Dispose();
    }

    [Theory]
    [InlineData("using Pooled<int> lease = NativePool<int>.Shared.Rent(16, 0); lease.AsSpan()[0] = 1;")]
    [InlineData("NativePool<int> pool = NativePool<int>.Shared; using Pooled<int> lease = pool.Rent(16, 0); lease.AsSpan()[0] = 1;")]
    [InlineData("var pool = NativePool<int>.Shared; using Pooled<int> lease = pool.Rent(16, 0); lease.AsSpan()[0] = 1;")]
    [InlineData("NativePool<int> pool; pool = NativePool<int>.Shared; using Pooled<int> lease = pool.Rent(16, 0);")]
    public async Task SharedPoolNeedsNoDisposalOfItsOwn(string body)
    {
        Assert.Empty(await NativeIdsAsync(body));
    }

    [Theory]
    [InlineData("NativePool<int> pool = NativePool<int>.Shared; Pooled<int> lease = pool.Rent(16, 0); lease.AsSpan()[0] = 1;", "NAM1003")]
    [InlineData("var pool = NativePool<int>.Shared; using Pooled<int> lease = pool.Rent(4, 0); Span<int> s = lease.AsSpan(); lease.Dispose(); s[0] = 1;", "NAM1051")]
    [InlineData("using NativePool<int> owned = new(4); NativePool<int> alias = owned;", "NAM1001")]
    public async Task LeasesFromTheSharedPoolStayTracked(string body, string expected)
    {
        Assert.Contains(expected, await NativeIdsAsync(body), StringComparer.Ordinal);
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
