using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class BorrowedLeaseParameterTests
{
    [Theory]
    [InlineData("in")]
    [InlineData("ref")]
    [InlineData("ref readonly")]
    [InlineData("scoped in")]
    [InlineData("scoped ref")]
    public async Task HelperThatBorrowsALeaseDoesNotOwnIt(string modifier)
    {
        string source = $$"""
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static int First({{modifier}} Pooled<int> lease) => lease.AsSpan()[0];

                public static int Sum({{modifier}} Pooled<int> lease)
                {
                    int total = 0;
                    foreach (int value in lease.AsSpan())
                    {
                        total += value;
                    }

                    return total;
                }
            }
            """;

        var diagnostics = await AnalyzerContractTests.AnalyzeAsync(source).ConfigureAwait(true);

        Assert.DoesNotContain(diagnostics, static diagnostic => string.Equals(diagnostic.Id, "NAM1003", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeaseThatIsNeverDisposedStillReportsNAM1003()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static int Leak()
                {
                    using NativePool<int> pool = new();
                    Pooled<int> lease = pool.Rent(8, 1);
                    return lease.AsSpan()[0];
                }
            }
            """;

        var diagnostics = await AnalyzerContractTests.AnalyzeAsync(source).ConfigureAwait(true);

#pragma warning disable HLQ005 // Xunit.Assert.Single enforces one report; First would conceal duplicates.
        Diagnostic leak = Assert.Single(diagnostics, static diagnostic => string.Equals(diagnostic.Id, "NAM1003", StringComparison.Ordinal));
#pragma warning restore HLQ005
        Assert.Equal(7, leak.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public async Task LeakingCallerStillReportsNAM1003WhenAHelperBorrowsTheLease()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static int Leak()
                {
                    using NativePool<int> pool = new();
                    Pooled<int> lease = pool.Rent(8, 1);
                    return Peek(in lease);
                }

                private static int Peek(in Pooled<int> lease) => lease.AsSpan()[0];
            }
            """;

        var diagnostics = await AnalyzerContractTests.AnalyzeAsync(source).ConfigureAwait(true);

#pragma warning disable HLQ005 // Xunit.Assert.Single enforces one report; First would conceal duplicates.
        Diagnostic leak = Assert.Single(diagnostics, static diagnostic => string.Equals(diagnostic.Id, "NAM1003", StringComparison.Ordinal));
#pragma warning restore HLQ005
        Assert.Equal(7, leak.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public async Task PassingTheLeaseItselfToAHelperIsStillNAM1016()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static int Run()
                {
                    using NativePool<int> pool = new();
                    using Pooled<int> lease = pool.Rent(8, 1);
                    return Peek(in lease);
                }

                private static int Peek(in Pooled<int> lease) => lease.AsSpan()[0];
            }
            """;

        var diagnostics = await AnalyzerContractTests.AnalyzeAsync(source).ConfigureAwait(true);

        Assert.Contains(diagnostics, static diagnostic => string.Equals(diagnostic.Id, "NAM1016", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, static diagnostic => string.Equals(diagnostic.Id, "NAM1003", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefStructThatStoresALeaseInAFieldIsNAM1013()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;

            public ref struct Wrapper
            {
                private Pooled<int> _lease;

                public Wrapper(NativePool<int> pool)
                {
                    _lease = pool.Rent(8, 0);
                }

                public void Dispose() => _lease.Dispose();
            }
            """;

        var diagnostics = await AnalyzerContractTests.AnalyzeAsync(source).ConfigureAwait(true);

        Assert.Contains(diagnostics, static diagnostic => string.Equals(diagnostic.Id, "NAM1013", StringComparison.Ordinal));
    }
}
