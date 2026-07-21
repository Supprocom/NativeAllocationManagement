using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class ArenaAnalyzerContractTests
{
    [Fact]
    public async Task ArenaUsesScratchOnlyAndAcceptsACompletedGeneration()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativeArena arena = new();
                    { ArenaLease<int> values = arena.Scratch<int>(4); values[0] = 1; }
                    arena.ReleaseLeasesToNativeMemory();
                }
            }
            """);

        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ArenaStrictAndTolerantReleaseShareTheSameFindingWithDifferentSeverity()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativeArena arena = new();
                    ArenaLease<int> values = arena.Scratch<int>(1);
                    arena.ReleaseLeasesToNativeMemory();
                }
            }
            """;

        ImmutableArray<Diagnostic> strict = await AnalyzerContractTests.AnalyzeAsync(source);
        ImmutableArray<Diagnostic> tolerant = await AnalyzerContractTests.AnalyzeAsync(
            source.Replace("ReleaseLeasesToNativeMemory", "ReleaseLeasesToGarbageCollector", StringComparison.Ordinal));

        Diagnostic strictFinding = Assert.Single(strict.Where(diagnostic => diagnostic.Id == "NAM1007"));
        Diagnostic tolerantFinding = Assert.Single(tolerant.Where(diagnostic => diagnostic.Id == "NAM1017"));
        Assert.Equal(DiagnosticSeverity.Error, strictFinding.Severity);
        Assert.Equal(DiagnosticSeverity.Warning, tolerantFinding.Severity);
        Assert.Equal(
            strictFinding.Properties["NAM.Provenance"],
            tolerantFinding.Properties["NAM.Provenance"]);
    }

    [Fact]
    public async Task ScopedAcquisitionRequiresScopedLocalAndPostDominatingRecycle()
    {
        ImmutableArray<Diagnostic> valid = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativeArena arena = new();
                    { scoped ArenaLease<int> values = arena.ScratchScoped<int>(2); values[0] = 1; }
                    arena.RecycleScoped();
                    arena.Dispose();
                }
            }
            """);
        Assert.DoesNotContain("NAM1018", AnalyzerContractTests.NativeDiagnostics(valid));
        Assert.DoesNotContain("NAM1020", AnalyzerContractTests.NativeDiagnostics(valid));

        ImmutableArray<Diagnostic> missing = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativeArena arena = new();
                    { scoped ArenaLease<int> values = arena.ScratchScoped<int>(2); values[0] = 1; }
                }
            }
            """);
        Assert.Contains("NAM1020", AnalyzerContractTests.NativeDiagnostics(missing));

        ImmutableArray<Diagnostic> ordinary = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativeArena arena = new();
                    scoped ArenaLease<int> values = arena.Scratch<int>(2);
                    arena.Dispose();
                }
            }
            """);
        Assert.Contains("NAM1019", AnalyzerContractTests.NativeDiagnostics(ordinary));

        ImmutableArray<Diagnostic> escaped = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static ArenaLease<int> Run(NativeArena arena)
                    => arena.ScratchScoped<int>(2);
            }
            """);
        Assert.Contains("NAM1018", AnalyzerContractTests.NativeDiagnostics(escaped));
    }

    [Fact]
    public async Task RegionRequiresTheExplicitBracedUsingStatement()
    {
        ImmutableArray<Diagnostic> valid = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using (NativeRegion region = new())
                    {
                        Local<int> values = region.Lease<int>(2);
                        values[0] = 1;
                    }
                }
            }
            """);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(valid));

        ImmutableArray<Diagnostic> declaration = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativeRegion region = new();
                    Local<int> values = region.Lease<int>(2);
                    values[0] = 1;
                }
            }
            """);
        string[] ids = AnalyzerContractTests.NativeDiagnostics(declaration);
        Assert.Contains("NAM1006", ids);
        Assert.DoesNotContain("NAM1012", ids);
    }

    [Fact]
    public async Task DelayedActivationMustBeProvenBeforeEveryAcquisition()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run(bool activate)
                {
                    NativeArena arena = new(doNotLeaseOnDeclaration: true);
                    if (activate) arena.LeaseFromMemory();
                    ArenaLease<int> values = arena.Scratch<int>(1);
                    arena.Dispose();
                }
            }
            """);
        Assert.Contains("NAM1009", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task PoolAndRegionScopedAcquisitionsRequireTheMatchingCompletion()
    {
        ImmutableArray<Diagnostic> valid = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    { scoped Pooled<int> pooled = pool.LeaseScoped(2); pooled[0] = 1; }
                    pool.RecycleScoped();
                    using (NativeRegion region = new())
                    {
                        { scoped Local<int> local = region.LeaseScoped<int>(2); local[0] = 1; }
                        region.RecycleScoped();
                    }
                    pool.Dispose();
                }
            }
            """);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(valid));

        ImmutableArray<Diagnostic> invalid = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    Pooled<int> value = pool.LeaseScoped(1);
                    value.Dispose();
                    pool.Dispose();
                }
            }
            """);
        string[] invalidIds = AnalyzerContractTests.NativeDiagnostics(invalid);
        Assert.Contains("NAM1018", invalidIds);
        Assert.DoesNotContain("NAM1003", invalidIds);
    }

    [Fact]
    public async Task ScopedCompletionInFinallyIsAcceptedAndPartialNestedRecycleIsRejected()
    {
        ImmutableArray<Diagnostic> finallyDiagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativeArena arena = new();
                    try
                    {
                        { scoped ArenaLease<int> values = arena.ScratchScoped<int>(2); values[0] = 1; }
                    }
                    finally
                    {
                        arena.RecycleScoped();
                    }
                    arena.Dispose();
                }
            }
            """);
        Assert.DoesNotContain("NAM1020", AnalyzerContractTests.NativeDiagnostics(finallyDiagnostics));

        ImmutableArray<Diagnostic> partial = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativeArena arena = new();
                    { scoped ArenaLease<int> outer = arena.ScratchScoped<int>(1); { scoped ArenaLease<int> inner = arena.ScratchScoped<int>(1); } arena.RecycleScoped(); }
                    arena.RecycleScoped();
                    arena.Dispose();
                }
            }
            """);
        Assert.Contains("NAM1007", AnalyzerContractTests.NativeDiagnostics(partial));
    }

    [Fact]
    public async Task AmbiguousScopedPendingStateCannotBeDischargedByRecycle()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run(bool condition)
                {
                    NativeArena arena = new();
                    if (condition)
                    {
                        { scoped ArenaLease<int> value = arena.ScratchScoped<int>(1); }
                    }
                    arena.RecycleScoped();
                    arena.Dispose();
                }
            }
            """);

        Diagnostic error = Assert.Single(diagnostics.Where(diagnostic => diagnostic.Id == "NAM1007"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal("arena -> ambiguous scoped allocation", error.Properties["NAM.Provenance"]);
    }

    [Fact]
    public async Task RegionGarbageCollectorReturnUsesTheSharedWarningSeverityForALiveRoot()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using (NativeRegion region = new())
                    {
                        Local<int> value = region.Lease<int>(1);
                        region.ReturnMemoryToGarbageCollector();
                    }
                }
            }
            """);
        Diagnostic warning = Assert.Single(diagnostics.Where(diagnostic => diagnostic.Id == "NAM1017"));
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NAM1007");
    }

    [Fact]
    public async Task OwnerDisposeInsideAnEnteredBorrowIsRejectedBeforeRuntimeClosure()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    Pooled<int> value = pool.Rent(1);
                    value.Access(_ => pool.Dispose());
                }
            }
            """);

        Diagnostic error = Assert.Single(diagnostics.Where(diagnostic => diagnostic.Id == "NAM1007"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal("pool -> value -> scoped callback", error.Properties["NAM.Provenance"]);
    }
}
