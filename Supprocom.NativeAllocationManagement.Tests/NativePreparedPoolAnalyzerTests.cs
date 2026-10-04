using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedPoolAnalyzerTests
{
    [Fact]
    public async Task GuardedPublicationAndCleanupAreAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new(new NativePoolPreparation(4, 16, 2), null);
                    if (pool.TryRent(4, static writer => writer.Fill(42), out Pooled<int> lease, out var reason))
                    {
                        try { _ = lease.Read(static view => view[0]); }
                        finally { lease.Dispose(); }
                    }
                    _ = pool.CapturePreparedSnapshot();
                }
            }
            """);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task NegatedGuardEarlyReturnAndCleanupAreAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new(new NativePoolPreparation(4, 16, 2), null);
                    if (!pool.TryRent(4, static writer => writer.Fill(42), out Pooled<int> lease, out _)) return;
                    _ = lease.Read(static view => view[0]);
                    lease.Dispose();
                }
            }
            """);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task MissingCleanupOnTheSuccessfulPathIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new(new NativePoolPreparation(4, 16, 2), null);
                    if (pool.TryRent(4, static writer => writer.Fill(42), out Pooled<int> lease, out _))
                        _ = lease.Read(static view => view[0]);
                }
            }
            """);
        Assert.Contains("NAM1003", AnalyzerContractTests.NativeDiagnostics(diagnostics), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("if (!pool.TryRent(4, static writer => writer.Fill(42), out Pooled<int> lease, out _)) lease.Dispose();", "NAM1004")]
    [InlineData("pool.TryRent(4, static writer => writer.Fill(42), out Pooled<int> lease, out _); lease.Dispose();", "NAM1050")]
    public async Task FailedOrUnguardedCapabilitiesCannotBeUsed(string body, string expected)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            $$"""
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new(new NativePoolPreparation(4, 16, 2), null);
                    {{body}}
                }
            }
            """);
        Assert.Contains(expected, AnalyzerContractTests.NativeDiagnostics(diagnostics), StringComparer.Ordinal);
    }

    [Fact]
    public async Task PreparedOwnerWithoutCleanupIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new(new NativePoolPreparation(4, 16, 2), null);
                }
            }
            """);
        Assert.Contains("NAM1003", AnalyzerContractTests.NativeDiagnostics(diagnostics), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ReorderedNamedArgumentsRetainAuthenticatedAcquisitionProof()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new(new NativePoolPreparation(4, 16, 2), null);
                    if (pool.TryRent(reason: out _, lease: out Pooled<int> lease,
                        initializer: static writer => writer.Fill(42), length: 4))
                        lease.Dispose();
                }
            }
            """);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ShortCircuitConditionDoesNotHideAnUndisposedSuccessfulLease()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run(bool condition)
                {
                    using NativePool<int> pool = new(new NativePoolPreparation(4, 16, 2), null);
                    if (pool.TryRent(4, static writer => writer.Fill(42), out Pooled<int> lease, out _) && condition)
                        lease.Dispose();
                }
            }
            """);
        Assert.Contains("NAM1003", AnalyzerContractTests.NativeDiagnostics(diagnostics), StringComparer.Ordinal);
    }
}
