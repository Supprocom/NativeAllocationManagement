using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeTransferDiagnosticsAnalyzerTests
{
    [Fact]
    public async Task ReturnedUniqueBindingsCanObserveAndCompleteCleanupWithoutNewAuthority()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> builder = new(4);
                    builder.Append(42);
                    NativeTransfer<int>? source = builder.Complete();
                    NativeTransfer<int> owner = NativeTransfer<int>.Move(ref source);
                    owner.Dispose();
                    _ = owner.CaptureSnapshot();
                    _ = owner.TryCompletePayloadReturn();
                }
            }
            """;
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
        string invalid = source.Replace("_ = owner.CaptureSnapshot();", "_ = owner.Read(static view => view[0]);", StringComparison.Ordinal);
        Assert.Contains("NAM1022", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(invalid)), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("CaptureSnapshot")]
    [InlineData("TryCompletePayloadReturn")]
    public async Task DefaultUniqueDiagnosticsCannotInventOwnership(string operation)
    {
        string source = $$"""
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativeTransfer<int> value = default;
                    _ = value.{{operation}}();
                }
            }
            """;
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("NAM1022", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task FinallyCleanupAcrossEarlyReturnsPreservesConstructedObservationNotAuthority()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static int Run()
                {
                    using NativeBuilder<int> builder = new(4);
                    builder.Append(42);
                    NativeTransfer<int>? source = builder.Complete();
                    NativeTransfer<int> owner = NativeTransfer<int>.Move(ref source);
                    try
                    {
                        if (owner.Read(static view => view[0]) != 42) return 1;
                        if (owner.CaptureSnapshot().OwnedBackingBytes != 16) return 2;
                    }
                    finally { owner.Dispose(); }
                    _ = owner.CaptureSnapshot();
                    _ = owner.TryCompletePayloadReturn();
                    return 0;
                }
            }
            """;
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Fact]
    public async Task DefaultAssignmentOnOnePathCannotInheritConstructedControlProof()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run(bool useDefault)
                {
                    using NativeBuilder<int> builder = new(4);
                    builder.Append(42);
                    NativeTransfer<int> owner = builder.Complete();
                    owner.Dispose();
                    if (useDefault) owner = default;
                    _ = owner.CaptureSnapshot();
                }
            }
            """;
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("NAM1022", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }
}
