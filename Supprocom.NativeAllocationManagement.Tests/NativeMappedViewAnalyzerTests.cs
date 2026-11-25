using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

// A mapped file follows the pool rules and its views follow the lease rules (NAM1004).
public sealed class NativeMappedViewAnalyzerTests
{
    [Theory]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); f.Dispose(); _ = v.Read(0); v.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); v.Dispose(); _ = v.AsReadOnlySpan(0, 1); f.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); v.Dispose(); v.Flush(); f.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenReadWrite(p); var v = f.View<int>(); v.Dispose(); v.Write(0, 1); f.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); if (condition) { v.Dispose(); } _ = v.Read(0); v.Dispose(); f.Dispose();")]
    public async Task ViewUseAfterItsOrItsFilesDisposeIsRejected(string body)
    {
        string[] ids = await NativeIdsAsync(body);
        Assert.Contains("NAM1004", ids, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("using var f = NativeMappedFile.OpenRead(p); using var v = f.View<int>(); _ = v.Read(0); _ = v.AsReadOnlySpan(0, 1).Length;")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); _ = v.Read(0); v.Dispose(); f.Dispose();")]
    [InlineData("using (var f = NativeMappedFile.OpenRead(p)) { using (var v = f.View<int>()) { _ = v.Read(0); } }")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var a = f.View<int>(); var b = f.View<int>(); a.Dispose(); _ = b.Read(0); b.Dispose(); f.Dispose();")]
    public async Task ViewUseWhileTheMappingIsCurrentIsAccepted(string body)
    {
        string[] ids = await NativeIdsAsync(body);
        Assert.DoesNotContain("NAM1004", ids, StringComparer.Ordinal);
    }

    private static async Task<string[]> NativeIdsAsync(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            $$"""
            using System;
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run(bool condition, string p)
                {
                    {{body}}
                }
            }
            """).ConfigureAwait(false);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id.StartsWith("CS", StringComparison.Ordinal) && diagnostic.Severity == DiagnosticSeverity.Error);
        return AnalyzerContractTests.NativeDiagnostics(diagnostics);
    }
}
