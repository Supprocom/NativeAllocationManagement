using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedFilledRentAnalyzerTests
{
    [Theory]
    [InlineData("using PreparedPooled<int> lease = pool.Rent(4, 42); _ = lease.Read(static view => view[0]);")]
    [InlineData("if (pool.TryRent(4, 42, out PreparedPooled<int> lease, out _)) lease.Dispose();")]
    [InlineData("if (!pool.TryRent(4, 42, out PreparedPooled<int> lease, out _)) return; lease.Dispose();")]
    [InlineData("if (pool.TryRent(reason: out _, lease: out PreparedPooled<int> lease, value: 42, length: 4)) lease.Dispose();")]
    public async Task FilledAcquisitionsRetainTheExistingAuthenticatedLifetimeProof(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(body);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(diagnostics));
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id.StartsWith("CS", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("PreparedPooled<int> lease = pool.Rent(4, 42);", "NAM1003")]
    [InlineData("if (pool.TryRent(4, 42, out PreparedPooled<int> lease, out _)) _ = lease.Read(static view => view[0]);", "NAM1003")]
    [InlineData("if (!pool.TryRent(4, 42, out PreparedPooled<int> lease, out _)) lease.Dispose();", "NAM1004")]
    [InlineData("pool.TryRent(4, 42, out PreparedPooled<int> lease, out _); lease.Dispose();", "NAM1050")]
    [InlineData("if (pool.TryRent(4, 42, out PreparedPooled<int> lease, out _) && condition) lease.Dispose();", "NAM1003")]
    public async Task FilledAcquisitionsDoNotBypassCleanupOrConditionalAvailability(string body, string expected)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(body);
        Assert.Contains(expected, AnalyzerContractTests.NativeDiagnostics(diagnostics), StringComparer.Ordinal);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id.StartsWith("CS", StringComparison.Ordinal));
    }

    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string body) =>
        AnalyzerContractTests.AnalyzeAsync(
            $$"""
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run(bool condition)
                {
                    using NativePreparedPool<int> pool = new(new NativePoolPreparation(1, 4, 1), null);
                    {{body}}
                }
            }
            """,
            treatWarningsAsErrors: true);
}
