using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

// A mapped file follows the pool rules and its views follow the lease rules (NAM1004).
public sealed class NativeMappedViewAnalyzerTests
{
    [Theory]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); f.Dispose(); _ = v.Get(0); v.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); v.Dispose(); _ = v.AsReadOnlySpan(0, 1); f.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); v.Dispose(); v.Flush(); f.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenReadWrite(p); var v = f.View<int>(); v.Dispose(); v.Set(0, 1); f.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); if (condition) { v.Dispose(); } _ = v.Get(0); v.Dispose(); f.Dispose();")]
    public async Task ViewUseAfterItsOrItsFilesDisposeIsRejected(string body)
    {
        string[] ids = await NativeIdsAsync(body);
        Assert.Contains("NAM1004", ids, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("using var f = NativeMappedFile.OpenRead(p); using var v = f.View<int>(); _ = v.Get(0); _ = v.AsReadOnlySpan(0, 1).Length;")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); _ = v.Get(0); v.Dispose(); f.Dispose();")]
    [InlineData("using (var f = NativeMappedFile.OpenRead(p)) { using (var v = f.View<int>()) { _ = v.Get(0); } }")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var a = f.View<int>(); var b = f.View<int>(); a.Dispose(); _ = b.Get(0); b.Dispose(); f.Dispose();")]
    public async Task ViewUseWhileTheMappingIsCurrentIsAccepted(string body)
    {
        string[] ids = await NativeIdsAsync(body);
        Assert.DoesNotContain("NAM1004", ids, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("using var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); _ = v.Get(0);")]
    public async Task UndisposedViewIsReported(string body)
    {
        string[] ids = await NativeIdsAsync(body);
        Assert.Contains("NAM1003", ids, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("using var f = NativeMappedFile.OpenRead(p); using var v = f.View<int>(0, 1); _ = v.Get(0);")]
    [InlineData("using var f = NativeMappedFile.OpenRead(p); using var v = f.View<long>(8, 2); using var w = f.View<byte>(); _ = v.Get(0) + w.Get(0);")]
    public async Task ViewCallWithRangeArgumentsReportsNoOwnerDiagnostic(string body)
    {
        string[] ids = await NativeIdsAsync(body);
        Assert.True(ids.Length == 0, "DIAG " + string.Join(" | ", await MessagesAsync(body).ConfigureAwait(true)));
    }

    [Fact]
    public async Task ViewOnAnotherTypeIsNotAnAcquisition()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;
            public sealed class Other : IDisposable { public Other View() => this; public void Dispose() { } }
            public static class Sample
            {
                public static void Run()
                {
                    Other o = new(); Other v = o.View(); o.Dispose(); v.Dispose(); o.View();
                }
            }
            """).ConfigureAwait(true);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    private static async Task<string[]> MessagesAsync(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            "using System; using Supprocom.NativeAllocationManagement; public static class S { public static void Run(bool condition, string p) { " + body + " } }").ConfigureAwait(false);
        return diagnostics.Where(d => d.Id.StartsWith("NAM", StringComparison.Ordinal)).Select(d => d.ToString()).ToArray();
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
            """).ConfigureAwait(true);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id.StartsWith("CS", StringComparison.Ordinal) && diagnostic.Severity == DiagnosticSeverity.Error);
        return AnalyzerContractTests.NativeDiagnostics(diagnostics);
    }
}
