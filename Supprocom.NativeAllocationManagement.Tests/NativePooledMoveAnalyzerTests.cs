using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePooledMoveAnalyzerTests
{
    [Theory]
    [InlineData("Pooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); using Pooled<int> destination = Pooled<int>.Move(ref source); _ = destination.Read(static view => view[0]);")]
    [InlineData("Pooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); source = Pooled<int>.Move(ref source); source.Dispose();")]
    [InlineData("if (!pool.TryRent(2, static writer => writer.Fill(7), out Pooled<int> source, out _)) return; using Pooled<int> destination = Pooled<int>.Move(ref source);")]
    [InlineData("Pooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); for (int i = 0; i < 16; i++) source = Pooled<int>.Move(ref source); source.Dispose();")]
    public async Task SupportedLexicalMovementTransfersCleanupToItsDestination(string body)
    {
        string source = Wrap(body);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Theory]
    [InlineData("Pooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); using Pooled<int> destination = Pooled<int>.Move(ref source); source.Dispose();", "NAM1004")]
    [InlineData("Pooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); Pooled<int> destination = Pooled<int>.Move(ref source); _ = destination.Read(static view => view[0]);", "NAM1003")]
    [InlineData("Pooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); _ = Pooled<int>.Move(ref source);", "NAM1013")]
    [InlineData("Pooled<int> source = default; using Pooled<int> destination = Pooled<int>.Move(ref source);", "NAM1004")]
    [InlineData("pool.TryRent(2, static writer => writer.Fill(7), out Pooled<int> source, out _); using Pooled<int> destination = Pooled<int>.Move(ref source);", "NAM1050")]
    [InlineData("Pooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); Pooled<int> destination = pool.Rent(2, static writer => writer.Fill(3)); destination = Pooled<int>.Move(ref source); destination.Dispose();", "NAM1003")]
    public async Task StaleUnprovenDroppedAndOverwrittenAuthorityAreRejected(string body, string expected)
    {
        string source = Wrap(body);
        AssertCompiles(source);
        Assert.Contains(expected, AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task AConditionalMoveDoesNotProveTheSourceReturnedOnEveryPath()
    {
        string source = Wrap("Pooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); if (condition) { using Pooled<int> destination = Pooled<int>.Move(ref source); } source.Dispose();");
        AssertCompiles(source);
        Assert.Contains("NAM1004", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    private static string Wrap(string body) => $$"""
        using Supprocom.NativeAllocationManagement;
        public static class Sample
        {
            public static void Run(bool condition)
            {
                using NativePool<int> pool = new(new NativePoolPreparation(2, 2, 2), null);
                {{body}}
            }
        }
        """;

    private static void AssertCompiles(string source) =>
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
