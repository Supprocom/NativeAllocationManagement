using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeRegionManagedAllocationAnalyzerTests
{
    [Fact]
    public async Task ManagedAllocationKindsReportExactSourceLocations()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public sealed class Box;
            public sealed record Item(int Value);

            public sealed class Sample
            {
                private int _value;

                public void Run(int input)
                {
                    Item item = new(1);
                    using (NativeRegion region = new())
                    {
                        object instance = new Box();
                        int[] array = new int[2];
                        var anonymous = new { Value = input };
                        Action handler = Handle;
                        Func<int> lambda = () => input;
                        int local = input;
                        int Capture() => local;
                        Func<int> localFunction = Capture;
                        object boxed = input;
                        Item clone = item with { Value = 2 };
                        string concatenated = input.ToString() + " value";
                        string interpolated = $"value {input}";
                        List<int> collection = [1, 2];
                        AcceptParams(1, 2);
                        _ = WorkAsync();
                        _ = Values();
                    }
                }

                private void Handle() => _value++;

                private static void AcceptParams(params int[] values) => _ = values.Length;

                private static async Task WorkAsync() => await Task.Yield();

                private static IEnumerable<int> Values()
                {
                    yield return 1;
                }
            }
            """);

        Diagnostic[] warnings = ManagedAllocationDiagnostics(diagnostics);
        AssertNoAnalyzerFailures(diagnostics);
        AssertKind(warnings, "class object creation");
        AssertKind(warnings, "array creation");
        AssertKind(warnings, "anonymous object creation");
        AssertKind(warnings, "delegate creation");
        AssertKind(warnings, "capturing lambda closure creation");
        AssertKind(warnings, "capturing local-function closure creation");
        AssertKind(warnings, "boxing conversion");
        AssertKind(warnings, "record-class with-expression cloning");
        AssertKind(warnings, "string concatenation");
        AssertKind(warnings, "interpolated string creation");
        AssertKind(warnings, "collection expression with managed storage");
        AssertKind(warnings, "implicit params-array creation");
        AssertKind(warnings, "async state-machine creation");
        AssertKind(warnings, "iterator state-machine creation");
        Assert.All(warnings, warning =>
        {
            FileLinePositionSpan line = warning.Location.GetLineSpan();
            string sourceSuffix = ":"
                + (line.StartLinePosition.Line + 1)
                + ":"
                + (line.StartLinePosition.Character + 1);
            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.True(warning.Location.IsInSource);
            Assert.Contains("<in-memory>:", warning.GetMessage(), StringComparison.Ordinal);
            Assert.EndsWith(sourceSuffix, warning.Properties["NAM.Source"]!, StringComparison.Ordinal);
            Assert.Contains(
                warning.Properties["NAM.AllocationSource"]!,
                warning.GetMessage(),
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task NamAllowAppliesOnlyToItsExactStatement()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativeRegion region = new();

                    // NAMALLOW
                    object leading = new object();

                    object trailing = new object(); // NAMALLOW

                    // NAMALOW
                    object misspelled = new object();

                    // NAMALLOW
                    _ = 1;
                    object stale = new object();

                    // NAM: managed allocation accepted
                    object oldMarker = new object();

                    // NAMALLOW
                    {
                        object outer = new object();
                    }

                    object[] multiple = { new object(), new object() };

                    // NAMALLOW
                    object[] acceptedMultiple = { new object(), new object() };

                    _ = AcceptedHelper();
                }

                private static object AcceptedHelper()
                {
                    // NAMALLOW
                    return new object();
                }
            }
            """);

        Diagnostic[] warnings = ManagedAllocationDiagnostics(diagnostics);
        AssertNoAnalyzerFailures(diagnostics);
        Assert.Equal(7, warnings.Length);
        Assert.DoesNotContain(warnings, diagnostic =>
            diagnostic.Location.SourceTree!.GetText()
                .ToString(diagnostic.Location.SourceSpan)
                .Contains("leading", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, diagnostic =>
            diagnostic.Location.SourceTree!.GetText()
                .ToString(diagnostic.Location.SourceSpan)
                .Contains("trailing", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, diagnostic =>
            diagnostic.Location.GetLineSpan().StartLinePosition.Line >= 30);
    }

    [Fact]
    public async Task LexicalScopesDoNotWarnBeforeActivationOrAfterDisposal()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    object before = new object();
                    using (NativeRegion outer = new())
                    {
                        using (NativeRegion inner = new())
                        {
                            object nested = new object();
                        }
                    }

                    object between = new object();
                    using NativeRegion declaration = new();
                    object active = new object();
                    declaration.Dispose();
                    object afterDispose = new object();
                }
            }
            """);

        Diagnostic[] warnings = ManagedAllocationDiagnostics(diagnostics);
        AssertNoAnalyzerFailures(diagnostics);
        Assert.Equal(2, warnings.Length);
        Assert.DoesNotContain("NAM1006", AnalyzerContractTests.NativeDiagnostics(diagnostics));
        Assert.DoesNotContain("NAM1010", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task AliasedUsingDeclarationsAndControlFlowKeepTheLexicalScope()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using RegionAlias = Supprocom.NativeAllocationManagement.NativeRegion;

            public static class Sample
            {
                public static object Early(bool stop)
                {
                    using RegionAlias region = new();
                    if (stop)
                    {
                        return new object();
                    }

                    try
                    {
                        throw new System.InvalidOperationException();
                    }
                    catch
                    {
                        return new object();
                    }
                }
            }
            """);

        Diagnostic[] warnings = ManagedAllocationDiagnostics(diagnostics);
        AssertNoAnalyzerFailures(diagnostics);
        Assert.Equal(3, warnings.Length);
        Assert.DoesNotContain("NAM1006", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task TopLevelUsingDeclarationStopsAtManualDispose()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            using NativeRegion region = new();
            object active = new object();
            region.Dispose();
            object afterDispose = new object();
            """,
            OutputKind.ConsoleApplication);

        Diagnostic warning = Assert.Single(ManagedAllocationDiagnostics(diagnostics));
        AssertNoAnalyzerFailures(diagnostics);
        Assert.Contains("class object creation", warning.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeferredBodiesWarnOnlyWhenCreationOrInvocationAllocates()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(int input)
                {
                    using NativeRegion region = new();
                    static object NeverCalled() => new object();
                    Func<object> cached = static () => new object();
                    object Local() => new object();
                    _ = Local();
                    Func<object> invoked = static () => new object();
                    _ = invoked();
                    Func<int> capture = () => input;
                    _ = capture();
                }
            }
            """);

        Diagnostic[] warnings = ManagedAllocationDiagnostics(diagnostics);
        AssertNoAnalyzerFailures(diagnostics);
        Assert.Equal(4, warnings.Length);
        Assert.Equal(2, warnings.Count(warning =>
            warning.GetMessage().Contains("source method", StringComparison.Ordinal)));
        AssertKind(warnings, "delegate creation");
        AssertKind(warnings, "capturing lambda closure creation");
    }

    [Fact]
    public async Task CanonicalStaticLambdaInitializersReportManagedAllocations()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    using NativeArena arena = new();
                    using NativeRegion region = new();
                    Local<int> local = region.Lease<int>(
                        1,
                        static writer =>
                        {
                            _ = new object();
                            writer.Fill(1);
                        });
                    ArenaLease<int> scratch = arena.Scratch<int>(
                        1,
                        static writer =>
                        {
                            _ = new object();
                            writer.Fill(2);
                        });
                    Pooled<int> pooled = pool.Rent(
                        1,
                        static writer =>
                        {
                            _ = new object();
                            writer.Fill(3);
                        });
                    _ = local[0] + scratch[0] + pooled[0];
                    pooled.Dispose();
                }
            }
            """);

        Diagnostic[] warnings = ManagedAllocationDiagnostics(diagnostics);
        AssertNoAnalyzerFailures(diagnostics);
        Assert.Equal(3, warnings.Length);
        Assert.All(warnings, warning => Assert.Equal(
            "new object()",
            warning.Location.SourceTree!.GetText()
                .ToString(warning.Location.SourceSpan)));
    }

    [Fact]
    public async Task CanonicalSourceMethodInitializersReportManagedAllocations()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    using NativeArena arena = new();
                    using NativeRegion region = new();
                    Local<int> local = region.Lease<int>(1, InitializeRegion);
                    ArenaLease<int> scratch = arena.Scratch<int>(1, InitializeArena);
                    Pooled<int> pooled = pool.Rent(1, InitializePool);
                    _ = local[0] + scratch[0] + pooled[0];
                    pooled.Dispose();
                }

                private static void InitializeRegion(
                    scoped NativeLeaseWriter<int> writer)
                {
                    _ = new object();
                    writer.Fill(1);
                }

                private static void InitializeArena(
                    scoped NativeLeaseWriter<int> writer)
                {
                    _ = new object();
                    writer.Fill(2);
                }

                private static void InitializePool(
                    scoped NativeLeaseWriter<int> writer)
                {
                    _ = new object();
                    writer.Fill(3);
                }
            }
            """);

        Diagnostic[] warnings = ManagedAllocationDiagnostics(diagnostics);
        AssertNoAnalyzerFailures(diagnostics);
        Assert.Equal(3, warnings.Length);
        Assert.All(warnings, warning => Assert.Equal(
            "new object()",
            warning.Location.SourceTree!.GetText()
                .ToString(warning.Location.SourceSpan)));
    }

    [Fact]
    public async Task AllocationFreeNativeInitializerCallbacksDoNotWarn()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    using NativeArena arena = new();
                    using NativeRegion region = new();
                    Local<int> local = region.Lease<int>(
                        1,
                        static writer => writer.Fill(1));
                    ArenaLease<int> scratch = arena.Scratch<int>(
                        1,
                        Initialize);
                    Pooled<int> pooled = pool.Rent(1, Initialize);
                    _ = local[0] + scratch[0] + pooled[0];
                    pooled.Dispose();
                }

                private static void Initialize(
                    scoped NativeLeaseWriter<int> writer) =>
                    writer.Fill(2);
            }
            """);

        AssertNoAnalyzerFailures(diagnostics);
        Assert.Empty(ManagedAllocationDiagnostics(diagnostics));
    }

    [Fact]
    public async Task CanonicalNativeAllocationFamiliesDoNotWarn()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    using NativeArena arena = new();
                    using NativeRegion region = new();
                    Local<int> local = region.Lease<int>(1, static writer => writer.Fill(1));
                    ArenaLease<int> scratch = arena.Scratch<int>(1, static writer => writer.Fill(2));
                    Pooled<int> pooled = pool.Rent(1, static writer => writer.Fill(3));
                    _ = local[0] + scratch[0] + pooled[0];
                    pooled.Dispose();
                }
            }
            """);

        AssertNoAnalyzerFailures(diagnostics);
        Assert.Empty(ManagedAllocationDiagnostics(diagnostics));
    }

    [Fact]
    public async Task UserTypesWithNativeNamesDoNotSpoofCanonicalSymbols()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            namespace Spoof
            {
                public sealed class Local<T>;
                public sealed class ArenaLease<T>;
                public sealed class Pooled<T>;

                public sealed class NativeRegion
                {
                    public Local<T> Lease<T>() => new();
                }

                public sealed class NativeArena
                {
                    public ArenaLease<T> Scratch<T>() => new();
                }

                public sealed class NativePool<T>
                {
                    public Pooled<T> Rent() => new();
                }
            }

            public static class Sample
            {
                public static void Run()
                {
                    Spoof.NativeRegion spoofRegion = new();
                    Spoof.NativeArena spoofArena = new();
                    Spoof.NativePool<int> spoofPool = new();
                    using NativeRegion region = new();
                    _ = spoofRegion.Lease<int>();
                    _ = spoofArena.Scratch<int>();
                    _ = spoofPool.Rent();
                }
            }
            """);

        Diagnostic[] warnings = ManagedAllocationDiagnostics(diagnostics);
        AssertNoAnalyzerFailures(diagnostics);
        Assert.Equal(3, warnings.Length);
        Assert.All(warnings, warning =>
            Assert.Contains("source method", warning.GetMessage(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task UserWrappersReturningNamHandlesDoNotGetCanonicalExemption()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativePool<int> pool = new();
                    NativeArena arena = new();
                    using NativeRegion region = new();
                    Local<int> local = WrapRegion(region);
                    ArenaLease<int> scratch = WrapArena(arena);
                    Pooled<int> pooled = WrapPool(pool);
                    _ = local[0] + scratch[0] + pooled[0];
                    pooled.Dispose();
                    arena.Dispose();
                    pool.Dispose();
                }

                private static Local<int> WrapRegion(NativeRegion region)
                {
                    _ = new object();
                    return region.Lease<int>(1, static writer => writer.Fill(1));
                }

                private static ArenaLease<int> WrapArena(NativeArena arena)
                {
                    _ = new object();
                    return arena.Scratch<int>(1, static writer => writer.Fill(2));
                }

                private static Pooled<int> WrapPool(NativePool<int> pool)
                {
                    _ = new object();
                    return pool.Rent(1, static writer => writer.Fill(3));
                }
            }
            """);

        Diagnostic[] warnings = ManagedAllocationDiagnostics(diagnostics);
        AssertNoAnalyzerFailures(diagnostics);
        Assert.Equal(3, warnings.Length);
        Assert.All(warnings, warning =>
            Assert.Contains("source method", warning.GetMessage(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExternalReferenceReturnDoesNotProveAnAllocation()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativeRegion region = new();
                    string? value = Environment.GetEnvironmentVariable("NAM_TEST_VALUE");
                    _ = value;
                }
            }
            """);

        AssertNoAnalyzerFailures(diagnostics);
        Assert.Empty(ManagedAllocationDiagnostics(diagnostics));
    }

    [Fact]
    public async Task NamAllowDoesNotSuppressOtherNamDiagnostics()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                private static NativePool<int>? _retained;

                public static void Run()
                {
                    NativePool<int> pool = new();
                    using NativeRegion region = new();
                    // NAMALLOW
                    Retain(pool, new object());
                    pool.Dispose();
                }

                private static void Retain(NativePool<int> pool, object value)
                {
                    _retained = pool;
                    _ = value;
                }
            }
            """);

        AssertNoAnalyzerFailures(diagnostics);
        Assert.Empty(ManagedAllocationDiagnostics(diagnostics));
        Assert.Contains("NAM1001", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ConsumerWarningsAsErrorsPolicyPromotesNam1035()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativeRegion region = new();
                    object value = new object();
                }
            }
            """,
            treatWarningsAsErrors: true);

        Diagnostic error = Assert.Single(ManagedAllocationDiagnostics(diagnostics));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    private static Diagnostic[] ManagedAllocationDiagnostics(
        ImmutableArray<Diagnostic> diagnostics)
    {
        return diagnostics
            .Where(diagnostic => diagnostic.Id == "NAM1035")
            .OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
            .ToArray();
    }

    private static void AssertKind(
        IEnumerable<Diagnostic> diagnostics,
        string kind)
    {
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.GetMessage().Contains(kind, StringComparison.Ordinal));
    }

    private static void AssertNoAnalyzerFailures(
        ImmutableArray<Diagnostic> diagnostics)
    {
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
    }
}
