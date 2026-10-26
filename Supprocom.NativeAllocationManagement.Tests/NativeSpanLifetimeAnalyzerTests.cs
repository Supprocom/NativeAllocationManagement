using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeSpanLifetimeAnalyzerTests
{
    [Theory]
    [InlineData("using NativePool<int> pool = new(4); Pooled<int> lease = pool.Rent(4, 0); Span<int> s = lease.AsSpan(); lease.Dispose(); s[0] = 1;")]
    [InlineData("using NativePool<int> pool = new(4); Pooled<int> lease = pool.Rent(4, 0); Span<int> s = lease.AsSpan(); Pooled<int> moved = Pooled<int>.Move(ref lease); s[0] = 1; moved.Dispose();")]
    [InlineData("using NativePool<int> pool = new(4); Pooled<int> lease = pool.Rent(4, 0); Span<int> s = lease.AsSpan(); Span<int> t = s.Slice(1); lease.Dispose(); t[0] = 1;")]
    [InlineData("using NativePool<int> pool = new(4); Pooled<int> lease = pool.Rent(4, 0); ReadOnlySpan<int> s = lease.AsSpan(); lease.Dispose(); _ = s[0];")]
    [InlineData("using NativePool<int> pool = new(4); Pooled<int> lease = pool.Rent(4, 0); Span<int> s = lease.AsSpan(); if (condition) { lease.Dispose(); } s[0] = 1; if (!condition) { lease.Dispose(); }")]
    [InlineData("using NativeArena arena = new(); ArenaLease<int> a = arena.Scratch<int>(4, 0); Span<int> s = a.AsSpan(); arena.Reset(); s[0] = 1;")]
    [InlineData("using NativeArena arena = new(); Span<int> s = arena.Scratch<int>(4, 0).AsSpan(); arena.Reset(); s[0] = 1;")]
    [InlineData("NativeArena arena = new(); Span<int> s = arena.Scratch<int>(4, 0).AsSpan(); arena.Dispose(); s.Clear();")]
    [InlineData("using NativeArena arena = new(); Span<int> s = arena.ScratchScoped<int>(4, 0).AsSpan(); arena.RecycleScoped(); s[0] = 1;")]
    [InlineData("Span<int> s = default; using (NativeRegion region = new(64, NativeMemoryReturn.ToNativeMemory)) { Local<int> local = region.Lease<int>(4, 0); s = local.AsSpan(); } s[0] = 1;")]
    public async Task SpanUseAfterItsLeaseEndsIsRejected(string body)
    {
        string[] ids = await NativeIdsAsync(body);
        Assert.Contains("NAM1051", ids, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("using NativePool<int> pool = new(4); using Pooled<int> lease = pool.Rent(4, 0); Span<int> s = lease.AsSpan(); s[0] = 1; s.Clear();")]
    [InlineData("using NativePool<int> pool = new(4); for (int i = 0; i < 3; i++) { using Pooled<int> lease = pool.Rent(4, 0); Span<int> s = lease.AsSpan(); s[0] = i; }")]
    [InlineData("using NativePool<int> pool = new(4); Span<int> s; Pooled<int> first = pool.Rent(4, 0); s = first.AsSpan(); s[0] = 1; first.Dispose(); Pooled<int> second = pool.Rent(4, 0); s = second.AsSpan(); s[0] = 2; second.Dispose();")]
    [InlineData("using NativeArena arena = new(); ArenaLease<int> a = arena.Scratch<int>(4, 0); Span<int> s = a.AsSpan(); s[0] = 1; arena.Reset();")]
    [InlineData("using NativeArena arena = new(); Span<int> s = arena.Scratch<int>(4, 0).AsSpan(); arena.RecycleScoped(); s[0] = 1; arena.Reset();")]
    [InlineData("using NativeArena arena = new(); for (int i = 0; i < 3; i++) { Span<int> s = arena.Scratch<int>(4, 0).AsSpan(); s[0] = i; arena.Reset(); }")]
    [InlineData("using (NativeRegion region = new(64, NativeMemoryReturn.ToNativeMemory)) { Local<int> local = region.Lease<int>(4, 0); Span<int> s = local.AsSpan(); s[0] = 1; }")]
    public async Task SpanUseWhileItsLeaseIsCurrentIsAccepted(string body)
    {
        string[] ids = await NativeIdsAsync(body);
        Assert.DoesNotContain("NAM1051", ids, StringComparer.Ordinal);
    }

    private static async Task<string[]> NativeIdsAsync(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            $$"""
            using System;
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run(bool condition)
                {
                    {{body}}
                }
            }
            """).ConfigureAwait(false);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id.StartsWith("CS", StringComparison.Ordinal) && diagnostic.Severity == DiagnosticSeverity.Error);
        return AnalyzerContractTests.NativeDiagnostics(diagnostics);
    }
}
