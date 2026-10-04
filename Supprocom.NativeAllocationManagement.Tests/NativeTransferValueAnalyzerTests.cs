using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeTransferValueAnalyzerTests
{
    [Theory]
    [InlineData("default")]
    [InlineData("new NativeTransfer<int>()")]
    public async Task DefaultValuesDoNotGainAnOwningBinding(string expression)
    {
        string source = Wrap($"NativeTransfer<int> value = {expression}; value.Dispose();");
        AssertCompiles(source);
        string[] diagnostics = AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source));
        Assert.Contains("NAM1022", diagnostics, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ExplicitNullableExtractionStillDiagnosesCopyingOwnership()
    {
        string source = Wrap(
            """
            NativeTransfer<int>? source = pool.RentTransferable(1, static writer => writer.Fill(42));
            NativeTransfer<int> alias = source.GetValueOrDefault();
            alias.Dispose();
            source?.Dispose();
            """);
        AssertCompiles(source);
        string[] diagnostics = AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source));
        Assert.Contains("NAM1021", diagnostics, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ExplicitNullableExtractionAfterMoveIsNotABypass()
    {
        string source = Wrap(
            """
            NativeTransfer<int>? source = pool.RentTransferable(1, static writer => writer.Fill(42));
            NativeTransfer<int> destination = NativeTransfer<int>.Move(ref source);
            _ = source.GetValueOrDefault().Length;
            destination.Dispose();
            """);
        AssertCompiles(source);
        string[] diagnostics = AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source));
        Assert.Contains("NAM1022", diagnostics, StringComparer.Ordinal);
    }

    [Fact]
    public async Task PresenceChecksAndActualConditionalCleanupAreAccepted()
    {
        string source = Wrap(
            """
            NativeTransfer<int>? source = pool.RentTransferable(1, static writer => writer.Fill(42));
            NativeTransfer<int> destination = NativeTransfer<int>.Move(ref source);
            destination.Dispose();
            if (source.HasValue) { source.Value.Dispose(); }
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Fact]
    public async Task ReadingPresenceDoesNotProveFieldCleanup()
    {
        const string source = """
            using System;
            using Supprocom.NativeAllocationManagement;
            public sealed class Sample : IDisposable
            {
                private NativeTransfer<int>? _value;
                public void Build()
                {
                    using NativeBuilder<int> builder = new(1);
                    builder.Append(42);
                    _value = builder.Complete();
                }
                public void Dispose() { if (_value.HasValue) { } }
            }
            """;
        AssertCompiles(source);
        string[] diagnostics = AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source));
        Assert.Contains("NAM1034", diagnostics, StringComparer.Ordinal);
    }

    private static string Wrap(string body) => $$"""
        using Supprocom.NativeAllocationManagement;
        public static class Sample
        {
            public static void Run()
            {
                using NativeConcurrentPool<int> pool = new();
                {{body}}
            }
        }
        """;

    private static void AssertCompiles(string source) =>
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
