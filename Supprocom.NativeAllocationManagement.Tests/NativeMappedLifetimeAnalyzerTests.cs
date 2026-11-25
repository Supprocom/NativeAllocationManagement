using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeMappedLifetimeAnalyzerTests
{
    [Theory]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); ReadOnlySpan<int> s = v.AsReadOnlySpan(0, 1); v.Dispose(); _ = s[0]; f.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenReadWrite(p); var v = f.View<int>(); Span<int> s = v.AsSpan(0, 1); v.Dispose(); s[0] = 1; f.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); ReadOnlySpan<int> s = v.AsReadOnlySpan(0, 1); f.Dispose(); _ = s[0]; v.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); f.Dispose(); _ = v.Length; v.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); v.Dispose(); _ = v.AsReadOnlySpan(0, 1); f.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); ReadOnlySpan<int> s = f.View<int>().AsReadOnlySpan(0, 1); f.Dispose(); _ = s[0];")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); ReadOnlySpan<int> s = v.AsReadOnlySpan(0, 4); ReadOnlySpan<int> t = s.Slice(1); v.Dispose(); _ = t[0]; f.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); ReadOnlySpan<int> s = v.AsReadOnlySpan(0, 1); if (condition) { v.Dispose(); } _ = s[0]; f.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); using (var v = f.View<int>()) { } f.Dispose(); var w = f.View<int>(); w.Dispose();")]
    public async Task UseAfterDisposalIsRejected(string body)
    {
        string[] ids = await NativeIdsAsync(body);
        Assert.Contains("NAM1053", ids, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("using var f = NativeMappedFile.OpenRead(p); using var v = f.View<int>(); ReadOnlySpan<int> s = v.AsReadOnlySpan(0, 1); _ = s[0];")]
    [InlineData("using (var f = NativeMappedFile.OpenRead(p)) { using (var v = f.View<int>()) { ReadOnlySpan<int> s = v.AsReadOnlySpan(0, 1); _ = s[0]; } }")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); ReadOnlySpan<int> s = v.AsReadOnlySpan(0, 1); _ = s[0]; v.Dispose(); f.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); v.Dispose(); v.Dispose(); f.Dispose(); f.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); ReadOnlySpan<int> s = v.AsReadOnlySpan(0, 1); v.Dispose(); s = default; _ = s.Length; f.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var v = f.View<int>(); v.Dispose(); v = f.View<int>(); _ = v.AsReadOnlySpan(0, 1).Length; v.Dispose(); f.Dispose();")]
    [InlineData("var f = NativeMappedFile.OpenRead(p); var a = f.View<int>(); var b = f.View<int>(); ReadOnlySpan<int> s = b.AsReadOnlySpan(0, 1); a.Dispose(); _ = s[0]; b.Dispose(); f.Dispose();")]
    public async Task UseWhileTheMappingIsCurrentIsAccepted(string body)
    {
        string[] ids = await NativeIdsAsync(body);
        Assert.DoesNotContain("NAM1053", ids, StringComparer.Ordinal);
    }

    private static async Task<string[]> NativeIdsAsync(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            $$"""
            using System;
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run(string p, bool condition)
                {
                    {{body}}
                }
            }
            """).ConfigureAwait(false);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id.StartsWith("CS", StringComparison.Ordinal) && diagnostic.Severity == DiagnosticSeverity.Error);
        return AnalyzerContractTests.NativeDiagnostics(diagnostics);
    }
}
