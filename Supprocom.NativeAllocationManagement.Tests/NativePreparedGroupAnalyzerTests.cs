using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedGroupAnalyzerTests
{
    [Fact]
    public async Task NegatedReturnProvesEveryOutputAndFinallyRecyclesTheCompleteSet()
    {
        string source = Wrap("""
            try
            {
                if (!NativeLeaseOperations.TryInitializeScoped<int, int, int, int, int>(input, arena,
                    1, 1, 1, 1, static (source, a, b, c, d) =>
                    { a.Fill(source[0]); b.Fill(2); c.Fill(3); d.Fill(4); },
                    out ArenaLease<int> a, out ArenaLease<int> b, out ArenaLease<int> c, out ArenaLease<int> d)) return;
                NativeLeaseOperations.Access(a, b, c, d, static (_, _, _, _) => { });
            }
            finally { arena.RecycleScoped(); }
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Fact]
    public async Task IgnoredResultCannotPublishAnyGroupOutput()
    {
        string source = Wrap("""
            try
            {
                NativeLeaseOperations.TryInitializeScoped<int, int, int, int, int>(input, arena,
                    1, 1, 1, 1, static (_, a, b, c, d) =>
                    { a.Fill(1); b.Fill(2); c.Fill(3); d.Fill(4); },
                    out ArenaLease<int> a, out ArenaLease<int> b, out ArenaLease<int> c, out ArenaLease<int> d);
                d.Clear();
            }
            finally { arena.RecycleScoped(); }
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1050", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task RefusedGroupOutputsAreInactiveRatherThanMerelyUnguarded()
    {
        string source = Wrap("""
            try
            {
                if (NativeLeaseOperations.TryInitializeScoped<int, int, int, int, int>(input, arena,
                    1, 1, 1, 1, static (_, a, b, c, d) =>
                    { a.Fill(1); b.Fill(2); c.Fill(3); d.Fill(4); },
                    out ArenaLease<int> a, out ArenaLease<int> b, out ArenaLease<int> c, out ArenaLease<int> d)) return;
                d.Clear();
            }
            finally { arena.RecycleScoped(); }
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1004", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task NamedAndShortCircuitGuardsProveTheActualParameterOutputs()
    {
        string source = Wrap("""
            try
            {
                if (NativeLeaseOperations.TryInitializeScoped<int, int, int, int, int>(
                    fourth: out ArenaLease<int> d, first: out ArenaLease<int> a,
                    source: input, arena: arena, firstLength: 1, secondLength: 1,
                    thirdLength: 1, fourthLength: 1, second: out ArenaLease<int> b,
                    third: out ArenaLease<int> c,
                    initializer: static (_, a, b, c, d) => { a.Fill(1); b.Fill(2); c.Fill(3); d.Fill(4); })
                    && d.Length == 1) d.Clear();
            }
            finally { arena.RecycleScoped(); }
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Fact]
    public async Task OctetSuccessProvesTheLastOutput()
    {
        string source = Wrap("""
            try
            {
                if (NativeLeaseOperations.TryInitializeScoped<int, int, int, int, int, int, int, int, int>(input, arena,
                    1, 1, 1, 1, 1, 1, 1, 1, static (_, a, b, c, d, e, f, g, h) =>
                    { a.Fill(1); b.Fill(2); c.Fill(3); d.Fill(4); e.Fill(5); f.Fill(6); g.Fill(7); h.Fill(8); },
                    out ArenaLease<int> a, out ArenaLease<int> b, out ArenaLease<int> c, out ArenaLease<int> d,
                    out ArenaLease<int> e, out ArenaLease<int> f, out ArenaLease<int> g, out ArenaLease<int> h)) h.Clear();
            }
            finally { arena.RecycleScoped(); }
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Fact]
    public async Task RefusedGroupDoesNotRequireRecyclingANeverAcquiredBatch()
    {
        string source = Wrap("""
            {
                if (!NativeLeaseOperations.TryInitializeScoped<int, int, int, int, int>(input, arena,
                    1, 1, 1, 1, static (_, a, b, c, d) =>
                    { a.Fill(1); b.Fill(2); c.Fill(3); d.Fill(4); },
                    out ArenaLease<int> a, out ArenaLease<int> b, out ArenaLease<int> c, out ArenaLease<int> d)) return;
                a.Clear();
            }
            arena.RecycleScoped();
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Fact]
    public async Task RefusalCannotDischargePreviouslyPendingScopedWork()
    {
        string source = Wrap("""
            {
                scoped ArenaLease<int> earlier = arena.ScratchScoped<int>(1, static writer => writer.Write(1));
                if (!NativeLeaseOperations.TryInitializeScoped<int, int, int, int, int>(input, arena,
                    1, 1, 1, 1, static (_, a, b, c, d) =>
                    { a.Fill(1); b.Fill(2); c.Fill(3); d.Fill(4); },
                    out ArenaLease<int> a, out ArenaLease<int> b, out ArenaLease<int> c, out ArenaLease<int> d)) return;
            }
            arena.RecycleScoped();
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1020", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    private static string Wrap(string body) => $$"""
        using Supprocom.NativeAllocationManagement;
        public static class Sample
        {
            public static void Run()
            {
                using NativeArena arena = new(new NativeArenaPreparation(64, 64), new NativeMemoryBudget(4096));
                ArenaLease<int> input = arena.Scratch<int>(1, static writer => writer.Write(17));
                {{body}}
            }
        }
        """;

    private static void AssertCompiles(string source) =>
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
