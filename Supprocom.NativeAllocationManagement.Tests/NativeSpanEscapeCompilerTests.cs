using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

/// <summary>A lease's span and a region's lease are bound to the variable that owns them, so the C# compiler rejects every escape.</summary>
public sealed class NativeSpanEscapeCompilerTests
{
    [Theory]
    // (a) return, (b) store into a ref-struct field passed by ref, (c) assign to an out Span.
    [InlineData("public static Span<int> M() { using Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); return p.AsSpan(); }", "CS8168")]
    [InlineData("public static void M(ref Holder h) { using Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); h.S = p.AsSpan(); }", "CS8168")]
    [InlineData("public static void M(out Span<int> o) { using Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); o = p.AsSpan(); }", "CS8168")]
    [InlineData("public static Span<int> M() { return NativePool<int>.Shared.Rent(16, 0).AsSpan(); }", "CS8156")]
    [InlineData("public static Span<int> M() { using PreparedPooled<int> p = Prepared.Rent(16, 0); return p.AsSpan(); }", "CS8168")]
    [InlineData("public static void M(ref Holder h) { using PreparedPooled<int> p = Prepared.Rent(16, 0); h.S = p.AsSpan(); }", "CS8168")]
    [InlineData("public static void M(out Span<int> o) { using PreparedPooled<int> p = Prepared.Rent(16, 0); o = p.AsSpan(); }", "CS8168")]
    [InlineData("public static Span<int> M() { using NativeRegion r = new(); Local<int> l = r.Lease<int>(16, 0); return l.AsSpan(); }", "CS8352")]
    [InlineData("public static void M(ref Holder h) { using NativeRegion r = new(); Local<int> l = r.Lease<int>(16, 0); h.S = l.AsSpan(); }", "CS8352")]
    [InlineData("public static void M(out Span<int> o) { using NativeRegion r = new(); Local<int> l = r.Lease<int>(16, 0); o = l.AsSpan(); }", "CS8352")]
    [InlineData("public static Span<int> M() { using NativeRegion r = new(); return r.Lease<int>(16, 0).AsSpan(); }", "CS8168")]
    [InlineData("public static Span<int> M() { using NativeArena a = new(); ArenaLease<int> l = a.Scratch<int>(16, 0); return l.AsSpan(); }", "CS8168")]
    [InlineData("public static void M(ref Holder h) { using NativeArena a = new(); ArenaLease<int> l = a.Scratch<int>(16, 0); h.S = l.AsSpan(); }", "CS8168")]
    [InlineData("public static void M(out Span<int> o) { using NativeArena a = new(); ArenaLease<int> l = a.Scratch<int>(16, 0); o = l.AsSpan(); }", "CS8168")]
    [InlineData("public static Span<int> M(NativeArena a) { return a.Scratch<int>(16, 0).AsSpan(); }", "CS8156")]
    // The span of a lease from an inner block cannot be kept after the block.
    [InlineData("public static void M() { Span<int> s; { using Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); s = p.AsSpan(); } s[0] = 1; }", "CS8168")]
    // A local declared with default has the widest scope; declare it scoped to assign a lease's span later.
    [InlineData("public static void M() { using Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); Span<int> s = default; s = p.AsSpan(); s[0] = 1; }", "CS8168")]
    [InlineData("public static void M() { using Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); Holder h = default; h.S = p.AsSpan(); }", "CS8168")]
    // A helper that takes the lease by value would hide the owner from the compiler.
    [InlineData("public static Span<int> M(Pooled<int> p) { return p.AsSpan(); }", "CS8166")]
    // A region's lease stays in the region's scope: return of a Local, rvalue return, out Local, by-value region.
    [InlineData("public static Local<int> M() { using (NativeRegion region = new()) { Local<int> v = region.Lease<int>(4, 0); return v; } }", "CS8352")]
    [InlineData("public static Local<int> M() { using NativeRegion region = new(); Local<int> v = region.Lease<int>(4, 0); return v; }", "CS8352")]
    [InlineData("public static Local<int> M() { using (NativeRegion region = new()) { return region.Lease<int>(4, 0); } }", "CS8168")]
    [InlineData("public static void M(out Local<int> o) { using NativeRegion region = new(); o = region.Lease<int>(4, 0); }", "CS8168")]
    [InlineData("public static Local<int> M() { using NativeRegion region = new(); return region.LeaseUninitialized<int>(4); }", "CS8168")]
    [InlineData("public static Local<int> M(NativeRegion region) { return region.Lease<int>(4, 0); }", "CS8166")]
    public void EscapeIsACompileError(string member, string expectedId)
    {
        string[] ids = ErrorIds(member);
        Assert.Equal([expectedId], ids);
    }

    [Theory]
    [InlineData("public static void M() { using Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); Span<int> s = p.AsSpan(); s[0] = 1; }")]
    [InlineData("public static void M() { using Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); Take(p.AsSpan()); TakeScoped(p.AsSpan()); Span<int> s = p.AsSpan(); Take(s); TakeScoped(s); }")]
    [InlineData("public static void M() { using Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); Span<int> t = p.AsSpan().Slice(1, 2); t[0] = 3; p.AsSpan().Fill(4); }")]
    [InlineData("public static void M() { using Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); foreach (ref int x in p.AsSpan()) { x = 1; } }")]
    [InlineData("public static void M() { using Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); Span<int> u = true ? p.AsSpan() : default; u[0] = 1; }")]
    [InlineData("public static void M() { using Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); scoped Span<int> s = default; s = p.AsSpan(); s[0] = 1; }")]
    [InlineData("public static void M() { using Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); scoped Holder h = default; h.S = p.AsSpan(); h.S[0] = 1; }")]
    [InlineData("public static int M() { return NativePool<int>.Shared.Rent(16, 0).AsSpan().Length; }")]
    [InlineData("public static int M() { using Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); return p.AsSpan()[0] + Take(p.AsSpan()); }")]
    [InlineData("public static void M() { using Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); Span<int> s = ByIn(in p); s[0] = 1; }")]
    [InlineData("public static void M() { Pooled<int> p = NativePool<int>.Shared.Rent(16, 0); Span<int> t = ByRef(ref p); t[0] = 1; p.Dispose(); }")]
    [InlineData("public static Span<int> M(in Pooled<int> p) { return p.AsSpan(); }")]
    [InlineData("public static Span<int> M(ref Pooled<int> p) { return p.AsSpan(); }")]
    [InlineData("public static void M() { using PreparedPooled<int> p = Prepared.Rent(16, 0); Span<int> s = p.AsSpan(); s[0] = 1; TakeScoped(s); }")]
    [InlineData("public static void M() { using NativeRegion r = new(); Local<int> l = r.Lease<int>(16, 0); Span<int> s = l.AsSpan(); s[0] = 1; TakeScoped(s); Take(s); }")]
    [InlineData("public static void M() { using NativeRegion r = new(); Local<int> v = r.Lease<int>(4, 0); Local<int> w = r.LeaseUninitialized<int>(4); v.Clear(); w.Clear(); }")]
    [InlineData("public static void M() { using NativeArena a = new(); ArenaLease<int> l = a.Scratch<int>(16, 0); Span<int> s = l.AsSpan(); s[0] = 1; TakeScoped(s); }")]
    public void InMethodUseCompiles(string member)
    {
        string[] ids = ErrorIds(member);
        Assert.True(ids.Length == 0, string.Join(",", ids));
    }

    private static string[] ErrorIds(string member) =>
        [.. AnalyzerContractTests.Compile(
            $$"""
            using System;
            using Supprocom.NativeAllocationManagement;
            public ref struct Holder { public Span<int> S; }
            public static class Sample
            {
                private static readonly NativePreparedPool<int> Prepared = new(new NativePoolPreparation(2, 16, 2), null);
                private static int Take(Span<int> s) => s.Length;
                private static int TakeScoped(scoped Span<int> s) => s.Length;
                private static Span<int> ByIn(in Pooled<int> p) => p.AsSpan();
                private static Span<int> ByRef(ref Pooled<int> p) => p.AsSpan();
                {{member}}
            }
            """)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id)
            .Distinct(StringComparer.Ordinal)];
}
