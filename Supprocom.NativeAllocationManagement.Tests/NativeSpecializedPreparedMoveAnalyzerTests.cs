using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeSpecializedPreparedMoveAnalyzerTests
{
    [Theory]
    [InlineData("PreparedPooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); using PreparedPooled<int> destination = PreparedPooled<int>.Move(ref source); _ = destination.Read(static view => view[0]);")]
    [InlineData("PreparedPooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); source = PreparedPooled<int>.Move(ref source); source.Dispose();")]
    [InlineData("if (!pool.TryRent(2, static writer => writer.Fill(7), out PreparedPooled<int> source, out _)) return; using PreparedPooled<int> destination = PreparedPooled<int>.Move(ref source);")]
    [InlineData("PreparedPooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); for (int i = 0; i < 16; i++) source = PreparedPooled<int>.Move(ref source); source.Dispose();")]
    public async Task SupportedLexicalMovementTransfersCleanupToItsDestination(string body)
    {
        string source = Wrap(body);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Theory]
    [InlineData("PreparedPooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); using PreparedPooled<int> destination = PreparedPooled<int>.Move(ref source); source.Dispose();", "NAM1004")]
    [InlineData("PreparedPooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); PreparedPooled<int> destination = PreparedPooled<int>.Move(ref source); _ = destination.Read(static view => view[0]);", "NAM1003")]
    [InlineData("PreparedPooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); _ = PreparedPooled<int>.Move(ref source);", "NAM1013")]
    [InlineData("PreparedPooled<int> source = default; using PreparedPooled<int> destination = PreparedPooled<int>.Move(ref source);", "NAM1004")]
    [InlineData("pool.TryRent(2, static writer => writer.Fill(7), out PreparedPooled<int> source, out _); using PreparedPooled<int> destination = PreparedPooled<int>.Move(ref source);", "NAM1050")]
    [InlineData("PreparedPooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); PreparedPooled<int> destination = pool.Rent(2, static writer => writer.Fill(3)); destination = PreparedPooled<int>.Move(ref source); destination.Dispose();", "NAM1003")]
    public async Task StaleUnprovenDroppedAndOverwrittenAuthorityAreRejected(string body, string expected)
    {
        string source = Wrap(body);
        AssertCompiles(source);
        Assert.Contains(expected, AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task AConditionalMoveDoesNotProveTheSourceReturnedOnEveryPath()
    {
        string source = Wrap("PreparedPooled<int> source = pool.Rent(2, static writer => writer.Fill(7)); if (condition) { using PreparedPooled<int> destination = PreparedPooled<int>.Move(ref source); } source.Dispose();");
        AssertCompiles(source);
        Assert.Contains("NAM1004", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    private static string Wrap(string body) => $$"""
        using Supprocom.NativeAllocationManagement;
        public static class Sample
        {
            public static void Run(bool condition)
            {
                using NativePreparedPool<int> pool = new(new NativePoolPreparation(2, 2, 2), null);
                {{body}}
            }
        }
        """;

    private static void AssertCompiles(string source) =>
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
