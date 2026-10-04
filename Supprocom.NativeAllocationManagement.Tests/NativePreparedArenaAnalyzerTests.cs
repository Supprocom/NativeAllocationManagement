using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

// Compiler success is required before ownership diagnostics can establish proof.
public sealed class NativePreparedArenaAnalyzerTests
{
    [Theory]
    [InlineData("TryScratch")]
    [InlineData("TryScratchScoped")]
    public async Task AProvenSuccessfulPreparedScratchIsUsable(string method)
    {
        string source = Wrap($$"""
            {
                if (arena.{{method}}<int>(2, static writer => writer.Fill(42), out ArenaLease<int> lease))
                {
                    lease.Clear();
                }
            }
            {{(string.Equals(method, "TryScratchScoped", StringComparison.Ordinal) ? "arena.RecycleScoped();" : string.Empty)}}
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Fact]
    public async Task NegatedEarlyReturnProvesScratchInitialization()
    {
        string source = Wrap("""
            if (!arena.TryScratch<int>(2, static writer => writer.Fill(42), out ArenaLease<int> lease)) { return; }
            lease.Clear();
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Fact]
    public async Task IgnoringThePreparedResultDoesNotProveInitialization()
    {
        string source = Wrap("""
            arena.TryScratch<int>(2, static writer => writer.Fill(42), out ArenaLease<int> lease);
            lease.Clear();
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1050", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task TheRefusedBranchHasNoLeaseAuthority()
    {
        string source = Wrap("""
            if (arena.TryScratch<int>(2, static writer => writer.Fill(42), out ArenaLease<int> lease)) { return; }
            lease.Clear();
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1004", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task NamedArgumentsRetainTheSameProvenOutputBinding()
    {
        string source = Wrap("""
            if (arena.TryScratch<int>(lease: out ArenaLease<int> lease, initializer: static writer => writer.Fill(42), length: 2))
            {
                lease.Clear();
            }
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Fact]
    public async Task PreparedConstructionStillRequiresDeterministicCleanup()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativeArena arena = new(new NativeArenaPreparation(32, 32), new NativeMemoryBudget(4096));
                }
            }
            """;
        AssertCompiles(source);
        Assert.Contains("NAM1003", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ScopedPreparedAcquisitionCannotUseABorrowedOwnerParameter()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run(NativeArena arena)
                {
                    if (arena.TryScratchScoped<int>(2, static writer => writer.Fill(42), out ArenaLease<int> lease))
                    {
                        lease.Clear();
                    }
                }
            }
            """;
        AssertCompiles(source);
        Assert.Contains("NAM1018", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    private static string Wrap(string body) => $$"""
        using Supprocom.NativeAllocationManagement;
        public static class Sample
        {
            public static void Run()
            {
                using NativeArena arena = new(new NativeArenaPreparation(32, 32), new NativeMemoryBudget(4096));
                {{body}}
            }
        }
        """;

    private static void AssertCompiles(string source) =>
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
