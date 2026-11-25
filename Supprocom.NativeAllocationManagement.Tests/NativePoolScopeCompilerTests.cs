using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

/// <summary>The scope pool and its leases cannot leave the callback: the C# compiler rejects each escape.</summary>
public sealed class NativePoolScopeCompilerTests
{
    // One case per escape. The expected id is what the compiler reports for that escape.
    [Theory]
    // Stored in a field.
    [InlineData("class C { NativeScopePool<int> F; }", "CS8345")]
    [InlineData("class C { Pooled<int> F; }", "CS8345")]
    [InlineData("class C { Pooled<int> P { get; set; } }", "CS8345")]
    // Stored in a static.
    [InlineData("class C { static NativeScopePool<int> S; }", "CS8345")]
    [InlineData("class C { static Pooled<int> S; }", "CS8345")]
    // Captured in a lambda.
    [InlineData("static int M() => NativePoolScope.Run<int, int, int>(0, (scoped ref NativeScopePool<int> pool, int s) => { Func<int> f = () => pool.Rent(1, 0).Length; return f(); });", "CS1628")]
    [InlineData("static int M() => NativePoolScope.Run<int, int, int>(0, (scoped ref NativeScopePool<int> pool, int s) => { Pooled<int> l = pool.Rent(1, 0); Func<int> f = () => l.Length; return f(); });", "CS8175")]
    // An async body.
    [InlineData("static void M() { NativePoolScope.Run<int, int, Task>(0, async (scoped ref NativeScopePool<int> pool, int s) => { await Task.Yield(); }); }", "CS1988")]
    // Held across an await.
    [InlineData("static async Task M(Pooled<int> l) { await Task.Yield(); _ = l.Length; }", "CS4012")]
    [InlineData("static async Task M(scoped ref NativeScopePool<int> p) { await Task.Yield(); _ = p.Rent(1, 0); }", "CS1988")]
    [InlineData("static void M() { NativePoolScope.Run<int, int, int>(0, (scoped ref NativeScopePool<int> pool, int s) => { async Task F() { await Task.Yield(); _ = pool.Rent(1, 0); } return 0; }); }", "CS1628")]
    // Returned out of the callback: as the result, or from a helper that holds only the scoped pool.
    [InlineData("static void M() { NativePoolScope.Run<int, int, Pooled<int>>(0, (scoped ref NativeScopePool<int> pool, int s) => pool.Rent(1, 0)); }", "CS9244")]
    [InlineData("static void M() { NativePoolScope.Run<int, int, Span<int>>(0, (scoped ref NativeScopePool<int> pool, int s) => pool.Rent(1, 0).AsSpan()); }", "CS9244")]
    [InlineData("static Pooled<int> M(scoped ref NativeScopePool<int> pool) { return pool.Rent(1, 0); }", "CS9075")]
    [InlineData("static Span<int> M(scoped ref NativeScopePool<int> pool) { Pooled<int> l = pool.Rent(1, 0); return l.AsSpan(); }", "CS8352")]
    [InlineData("static void M(scoped ref NativeScopePool<int> pool, out Pooled<int> o) { o = pool.Rent(1, 0); }", "CS9075")]
    public void EscapeIsACompileError(string member, string expectedId)
    {
        string[] ids = ErrorIds(member);
        Assert.Contains(expectedId, ids, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("static int M() => NativePoolScope.Run<int, int, int>(0, (scoped ref NativeScopePool<int> pool, int s) => { using Pooled<int> l = pool.Rent(4, 0); l.AsSpan().Fill(s); return l.AsSpan()[0]; });")]
    [InlineData("static void M() { NativePoolScope.Run<int, int>(0, (scoped ref NativeScopePool<int> pool, int s) => { Pooled<int> a = pool.Rent(4, 0); Pooled<int> b = pool.Rent(8, 0); a.Dispose(); b.Dispose(); }); }")]
    [InlineData("static int Use(scoped ref NativeScopePool<int> pool) { using Pooled<int> l = pool.Rent(1, 0); return l.Length; }")]
    public void InCallbackUseCompiles(string member)
    {
        string[] ids = ErrorIds(member);
        Assert.True(ids.Length == 0, string.Join(",", ids));
    }

    private static string[] ErrorIds(string member) =>
        [.. AnalyzerContractTests.Compile(
            $$"""
            using System;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                {{member}}
            }
            """)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id)
            .Distinct(StringComparer.Ordinal)];
}
