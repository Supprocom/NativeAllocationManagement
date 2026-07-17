using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Supprocom.NativeAllocationManagement.Analyzers;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class AnalyzerContractTests
{
    [Fact]
    public async Task ScopedPoolAndLeaseHaveNoOwnershipDiagnostics()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    using Pooled<int> values = pool.Rent(4);
                    values.Access(static span => Fill(span));
                }

                private static void Fill(scoped Span<int> span)
                {
                    span[0] = 1;
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ReturnedHandleUseIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    Pooled<int> oldValues = pool.Rent(1);
                    pool.ReturnToNativeMemory();
                    _ = oldValues.Length;
                    pool.LeaseFromMemory();
                    Pooled<int> currentValues = pool.Rent(1);
                    _ = currentValues.Length;
                    pool.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1004", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task AliasUnknownCallAndLifetimeEscapeAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    Pooled<int> values = pool.Rent(1);
                    Pooled<int> alias = values;
                    Consume(values);
                }

                private static void Consume(Pooled<int> values)
                {
                    _ = values.Length;
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1002", ids);
        Assert.Contains("NAM1016", ids);
        Assert.Contains("NAM1003", ids);
    }

    [Fact]
    public async Task RegionRequiresUsingAndRejectsNestedRegions()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativeRegion unscoped = new();
                    Local<int> first = unscoped.Allocate<int>(1);
                    using NativeRegion outer = new();
                    using NativeRegion inner = new();
                    Local<int> second = outer.Allocate<int>(1);
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1006", ids);
        Assert.Contains("NAM1010", ids);
    }

    [Fact]
    public async Task BorrowedCallbackCannotReturnItsPool()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    using Pooled<int> values = pool.Rent(1);
                    values.Access(span => pool.ReturnToNativeMemory());
                }
            }
            """);

        Assert.Contains("NAM1007", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ScopedPoolCannotUseExplicitLifecycleOrLeaseAfterReturn()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    pool.ReturnToGarbageCollector();
                    pool.LeaseFromMemory();
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1005", ids);
        Assert.Equal(2, ids.Count(id => id == "NAM1005"));
    }

    [Fact]
    public async Task DeterministicFieldRequiresAnExplicitDisposePath()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public sealed class Sample
            {
                private readonly NativePool<int> _pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
            }
            """);

        Assert.Contains("NAM1015", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task DeterministicFieldWithAnEffectiveDisposePathIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            using System;

            public sealed class Sample : IDisposable
            {
                private readonly NativePool<int> _pool = new(returnOnDispose: NativeReturn.ToNativeMemory);

                public void Dispose()
                {
                    _pool.Dispose();
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task AwaitCannotCrossAnActiveLease()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static async Task Run()
                {
                    NativePool<int> pool = new();
                    Pooled<int> values = pool.Rent(1);
                    await Task.Yield();
                    _ = values.Length;
                    pool.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1011", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task BranchesMayReturnTheSameGenerationExactlyOnce()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    NativePool<int> pool = new();
                    if (condition)
                    {
                        pool.ReturnToNativeMemory();
                    }
                    else
                    {
                        pool.ReturnToNativeMemory();
                    }

                    pool.LeaseFromMemory();
                    Pooled<int> values = pool.Rent(1);
                    values.Dispose();
                    pool.Dispose();
                }
            }
            """);

        Assert.DoesNotContain("NAM1009", NativeDiagnostics(diagnostics));
        Assert.DoesNotContain("NAM1003", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task LoopStateUsesTheLeastPermissivePostLoopFact()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    NativePool<int> pool = new();
                    while (condition)
                    {
                        pool.ReturnToNativeMemory();
                    }

                    _ = pool.Rent(1);
                }
            }
            """);

        Assert.Contains("NAM1009", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task FinallyDisposesAHandleOnEveryExceptionalPath()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                    Pooled<int> values = pool.Rent(1);
                    try
                    {
                        values[0] = 1;
                    }
                    finally
                    {
                        values.Dispose();
                    }

                    pool.Dispose();
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task TupleAndLocalFunctionEscapesAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    Pooled<int> values = pool.Rent(1);
                    var tuple = (values, 1);

                    void ReadLater()
                    {
                        _ = values.Length;
                    }

                    ReadLater();
                    values.Dispose();
                    pool.Dispose();
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1013", ids);
    }

    [Fact]
    public async Task RegionLocalsCannotBeUsedOutsideTheirRegion()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    Local<int> value;
                    using (NativeRegion region = new())
                    {
                        value = region.Allocate<int>(1);
                    }

                    _ = value.Length;
                }
            }
            """);

        Assert.Contains("NAM1012", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task SequentialRegionStatementsAreAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using (NativeRegion first = new())
                    {
                        Local<int> value = first.Allocate<int>(1);
                        value[0] = 1;
                    }

                    using (NativeRegion second = new())
                    {
                        Local<int> value = second.Allocate<int>(1);
                        value[0] = 2;
                    }
                }
            }
            """);

        Assert.DoesNotContain("NAM1010", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task SourceVisibleLifecycleHelpersCarryOwnerEffects()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    ReturnPool(pool);
                    pool.LeaseFromMemory();
                    Pooled<int> values = pool.Rent(1);
                    values.Dispose();
                    pool.Dispose();
                }

                private static void ReturnPool(NativePool<int> pool)
                {
                    pool.ReturnToNativeMemory();
                }
            }
            """);

        Assert.DoesNotContain("NAM1001", NativeDiagnostics(diagnostics));
        Assert.DoesNotContain("NAM1009", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task EarlyReturnCannotAbandonAnActiveNativeValue()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                    Pooled<int> values = pool.Rent(1);
                    if (condition)
                    {
                        return;
                    }

                    values.Dispose();
                    pool.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1003", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task FinallyCleanupMakesAnEarlyReturnValid()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    NativePool<int> pool = new(returnOnDispose: NativeReturn.ToNativeMemory);
                    Pooled<int> values = pool.Rent(1);
                    try
                    {
                        if (condition)
                        {
                            return;
                        }

                        values[0] = 1;
                    }
                    finally
                    {
                        values.Dispose();
                        pool.Dispose();
                    }
                }
            }
            """);

        Assert.Empty(NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task YieldCannotCrossAnActiveLease()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System.Collections.Generic;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static IEnumerable<int> Run()
                {
                    NativePool<int> pool = new();
                    Pooled<int> values = pool.Rent(1);
                    yield return 1;
                    values.Dispose();
                    pool.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1011", NativeDiagnostics(diagnostics));
    }

    private static string[] NativeDiagnostics(ImmutableArray<Diagnostic> diagnostics)
    {
        return diagnostics
            .Where(diagnostic => diagnostic.Id.StartsWith("NAM", StringComparison.Ordinal))
            .Select(diagnostic => diagnostic.Id)
            .ToArray();
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        CSharpParseOptions parseOptions = new(LanguageVersion.Preview);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        List<MetadataReference> references =
        [
            ..GetTrustedPlatformReferences(),
            MetadataReference.CreateFromFile(typeof(NativePool<int>).Assembly.Location)
        ];

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "NativeAllocationAnalyzerContract",
            syntaxTrees: [tree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        CompilationWithAnalyzers analyzed = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new NativeAllocationAnalyzer()));
        return await analyzed.GetAnalyzerDiagnosticsAsync();
    }

    private static IEnumerable<MetadataReference> GetTrustedPlatformReferences()
    {
        string trustedAssemblies =
            AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("The test host did not expose trusted platform assemblies.");

        return trustedAssemblies
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
    }
}
