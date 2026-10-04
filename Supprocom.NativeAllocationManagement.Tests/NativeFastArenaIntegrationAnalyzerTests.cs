using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeFastArenaIntegrationAnalyzerTests
{
    [Fact]
    public async Task PreparedFastGroupIsPublishedAndRecycledAsOneCompleteBatch()
    {
        string source = Wrap("""
            try
            {
                NativeLeaseOperations.InitializeScoped<int, int, long, byte, int>(input, arena,
                    1, 1, 1, 1, static (source, a, b, c, d) =>
                    { a.Fill(source[0]); b.Fill(2); c.Fill(3); d.Fill(4); },
                    out ArenaLease<int> a, out ArenaLease<long> b,
                    out ArenaLease<byte> c, out ArenaLease<int> d);
                NativeLeaseOperations.Access(input, a, b, c, d, static (_, _, _, _, _) => { });
            }
            finally { arena.RecycleScoped(); }
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Fact]
    public async Task FastGroupStillRequiresScopedCompletion()
    {
        string source = Wrap("""
            NativeLeaseOperations.InitializeScoped<int, int, long, byte, int>(input, arena,
                1, 1, 1, 1, static (_, a, b, c, d) =>
                { a.Fill(1); b.Fill(2); c.Fill(3); d.Fill(4); },
                out ArenaLease<int> a, out ArenaLease<long> b,
                out ArenaLease<byte> c, out ArenaLease<int> d);
            a.Clear();
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1020", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task StaleFastGroupSourceCannotAuthorizeNewOutputs()
    {
        string source = Wrap("""
            arena.Reset();
            try
            {
                NativeLeaseOperations.InitializeScoped<int, int, long, byte, int>(input, arena,
                    1, 1, 1, 1, static (_, a, b, c, d) =>
                    { a.Fill(1); b.Fill(2); c.Fill(3); d.Fill(4); },
                    out ArenaLease<int> a, out ArenaLease<long> b,
                    out ArenaLease<byte> c, out ArenaLease<int> d);
            }
            finally { arena.RecycleScoped(); }
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1004", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task CompositeCallbackCannotCrossTheAdmittedOwnerBoundary()
    {
        string source = Wrap("""
            ArenaLease<int> other = arena.Scratch<int>(1, static writer => writer.Write(2));
            NativeLeaseOperations.Access(input, other, (_, _) => arena.Reset());
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1007", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ASourceTypeWithTheCompositeDisplayNameReceivesNoRuntimeExemption()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;
            namespace Supprocom.NativeAllocationManagement
            {
                public static class NativeLeaseOperations
                {
                    public static void Access(scoped ArenaLease<int> input) { }
                }
            }
            public static class Sample
            {
                public static void Run()
                {
                    using NativeArena arena = new();
                    ArenaLease<int> input = arena.Scratch<int>(1, static writer => writer.Write(17));
                    NativeLeaseOperations.Access(input);
                }
            }
            """;
        AssertCompiles(source);
        Assert.Contains("NAM1016", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task StaleInputToFastCompositeAccessIsNotAnExemptArgument()
    {
        string source = Wrap("""
            arena.Reset();
            NativeLeaseOperations.Access(input, input, static (_, _) => { });
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1004", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
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
