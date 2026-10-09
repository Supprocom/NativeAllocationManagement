using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePooledAcquisitionOverwriteAnalyzerTests
{
    [Theory]
    [InlineData(false, "if (left.TryRent(4, static writer => writer.Fill(2), out lease, out _)) lease.Dispose();")]
    [InlineData(true, "if (left.TryRent(4, static writer => writer.Fill(2), out lease, out _)) lease.Dispose();")]
    [InlineData(false, "if (right.TryRent(4, static writer => writer.Fill(2), out lease, out _)) lease.Dispose();")]
    [InlineData(true, "if (right.TryRent(4, static writer => writer.Fill(2), out lease, out _)) lease.Dispose();")]
    [InlineData(false, "if (left.TryRent(reason: out _, lease: out lease, initializer: static writer => writer.Fill(2), length: 4)) lease.Dispose();")]
    [InlineData(true, "if (left.TryRent(reason: out _, lease: out lease, initializer: static writer => writer.Fill(2), length: 4)) lease.Dispose();")]
    [InlineData(false, "left.TryRent(4, static writer => writer.Fill(2), out lease, out _);")]
    [InlineData(true, "left.TryRent(4, static writer => writer.Fill(2), out lease, out _);")]
    [InlineData(false, "lease = left.Rent(4, static writer => writer.Fill(2)); lease.Dispose();")]
    [InlineData(true, "lease = right.Rent(4, static writer => writer.Fill(2)); lease.Dispose();")]
    [InlineData(false, "if (condition) lease.Dispose(); if (left.TryRent(4, static writer => writer.Fill(2), out lease, out _)) lease.Dispose();")]
    [InlineData(true, "if (condition) lease.Dispose(); if (left.TryRent(4, static writer => writer.Fill(2), out lease, out _)) lease.Dispose();")]
    [InlineData(false, "lease = default;")]
    [InlineData(true, "lease = new();")]
    public async Task ReplacementCannotEraseAnActivePooledObligation(bool regularFirst, string replacement)
    {
        string first = regularFirst
            ? "PreparedPooled<int> lease = left.Rent(4, static writer => writer.Fill(1));"
            : "if (!left.TryRent(4, static writer => writer.Fill(1), out PreparedPooled<int> lease, out _)) return;";
        string source = Wrap($$"""
            {{first}}
            /* replacement-start */
            {{replacement}}
            /* replacement-end */
            """);
        AssertCompiles(source);
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(source);
        Assert.DoesNotContain(diagnostics, static diagnostic => string.Equals(diagnostic.Id, "AD0001", StringComparison.Ordinal));
        int start = source.IndexOf("/* replacement-start */", StringComparison.Ordinal);
        int end = source.IndexOf("/* replacement-end */", StringComparison.Ordinal);
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Id, "NAM1003", StringComparison.Ordinal)
            && diagnostic.Location.SourceSpan.Start > start && diagnostic.Location.SourceSpan.End < end);
    }

    [Theory]
    [InlineData("lease.Dispose(); if (left.TryRent(4, static writer => writer.Fill(2), out lease, out _)) lease.Dispose();")]
    [InlineData("lease.Dispose(); if (right.TryRent(4, static writer => writer.Fill(2), out lease, out _)) lease.Dispose();")]
    [InlineData("lease.Dispose(); lease = right.Rent(4, static writer => writer.Fill(2)); lease.Dispose();")]
    [InlineData("if (condition) lease.Dispose(); else lease.Dispose(); if (left.TryRent(4, static writer => writer.Fill(2), out lease, out _)) lease.Dispose();")]
    public async Task ProvenCompletedBindingCanBeReused(string body)
    {
        string source = Wrap($$"""
            if (!left.TryRent(4, static writer => writer.Fill(1), out PreparedPooled<int> lease, out _)) return;
            {{body}}
            """);
        AssertCompiles(source);
        await AssertAcceptedAsync(source);
    }

    [Fact]
    public async Task RefusedOrCompletedConditionalAcquisitionDoesNotInventAnOldObligation()
    {
        string source = Wrap("""
            if (left.TryRent(4, static writer => writer.Fill(1), out PreparedPooled<int> lease, out _)) lease.Dispose();
            if (left.TryRent(4, static writer => writer.Fill(2), out lease, out _)) lease.Dispose();
            """);
        AssertCompiles(source);
        await AssertAcceptedAsync(source);
    }

    [Fact]
    public async Task CompletedLoopIterationsDoNotInventAnActivePreviousAcquisition()
    {
        string source = Wrap("""
            PreparedPooled<int> lease = default;
            for (int index = 0; index < 3; index++)
                if (left.TryRent(4, static writer => writer.Fill(1), out lease, out _)) lease.Dispose();
            """);
        AssertCompiles(source);
        await AssertAcceptedAsync(source);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("new()")]
    public async Task EmptyCapabilityDoesNotInventOwnership(string value)
    {
        string source = Wrap("PreparedPooled<int> lease = " + value + ";");
        AssertCompiles(source);
        await AssertAcceptedAsync(source);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("new()")]
    public async Task EmptyCapabilityHasNoPayloadAccessAuthority(string value)
    {
        string source = Wrap("PreparedPooled<int> lease = " + value + "; _ = lease.Read(static view => view[0]);");
        AssertCompiles(source);
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(source);
        Assert.DoesNotContain(diagnostics, static diagnostic => string.Equals(diagnostic.Id, "AD0001", StringComparison.Ordinal));
        Assert.Contains("NAM1004", AnalyzerContractTests.NativeDiagnostics(diagnostics), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ClearingACompletedBindingAndReusingItDoesNotInventAnObligation()
    {
        string source = Wrap("""
            if (!left.TryRent(4, static writer => writer.Fill(1), out PreparedPooled<int> lease, out _)) return;
            lease.Dispose();
            lease = default;
            if (left.TryRent(4, static writer => writer.Fill(2), out lease, out _)) lease.Dispose();
            """);
        AssertCompiles(source);
        await AssertAcceptedAsync(source);
    }

    [Theory]
    [InlineData("if (left.TryRent(4, static writer => writer.Fill(1), out lease, out _)) _ = lease.Read(static view => view[0]);")]
    [InlineData("if (left.TryRent(4, static writer => writer.Fill(1), out lease, out _) && condition) lease.Dispose();")]
    public async Task IncompleteLoopIterationsRetainTheirCleanupObligation(string body)
    {
        string source = Wrap($$"""
            PreparedPooled<int> lease = default;
            for (int index = 0; index < 3; index++) { {{body}} }
            """);
        AssertCompiles(source);
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(source);
        Assert.DoesNotContain(diagnostics, static diagnostic => string.Equals(diagnostic.Id, "AD0001", StringComparison.Ordinal));
        Assert.Contains("NAM1003", AnalyzerContractTests.NativeDiagnostics(diagnostics), StringComparer.Ordinal);
    }

    [Fact]
    public async Task LookalikeNamesDoNotCreateNativeCleanupAuthority()
    {
        const string source = """
            using System;
            using Other;
            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    pool.TryRent(4, static () => { }, out Pooled<int> lease, out _);
                    pool.TryRent(4, static () => { }, out lease, out _);
                }
            }
            namespace Other
            {
                public sealed class NativePool<T> : IDisposable
                {
                    public bool TryRent(int length, Action initializer, out Pooled<T> lease, out int reason)
                    { lease = default; reason = 0; return false; }
                    public void Dispose() { }
                }
                public readonly ref struct Pooled<T> { public void Dispose() { } }
            }
            """;
        AssertCompiles(source);
        await AssertAcceptedAsync(source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommonLifetimeArenaViewCanBeRebound(bool prepared)
    {
        string replacement = prepared
            ? "if (arena.TryScratch<int>(1, static writer => writer.Fill(2), out view)) view.Clear();"
            : "view = arena.Scratch<int>(1, static writer => writer.Fill(2)); view.Clear();";
        string source = $$"""
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativeArena arena = new(new NativeArenaPreparation(64, 64), null);
                    ArenaLease<int> view = arena.Scratch<int>(1, static writer => writer.Fill(1));
                    {{replacement}}
                }
            }
            """;
        AssertCompiles(source);
        await AssertAcceptedAsync(source);
    }

    private static string Wrap(string body) => $$"""
        using Supprocom.NativeAllocationManagement;
        public static class Sample
        {
            public static void Run(bool condition)
            {
                using NativePreparedPool<int> left = new(new NativePoolPreparation(4, 16, 2), null);
                using NativePreparedPool<int> right = new(new NativePoolPreparation(4, 16, 2), null);
                {{body}}
            }
        }
        """;

    private static void AssertCompiles(string source) =>
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    private static async Task AssertAcceptedAsync(string source)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(source).ConfigureAwait(true);
        Assert.DoesNotContain(diagnostics, static diagnostic => string.Equals(diagnostic.Id, "AD0001", StringComparison.Ordinal));
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }
}
