using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeLeaseOperationsAnalyzerTests
{
    [Fact]
    public async Task UnaryCompositeAccessAcceptsDirectPooledViewWithOwnerCleanup()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    using Pooled<int> value = pool.Rent(1);
                    NativeLeaseOperations.Access(value, static view => view[0] = 1);
                }
            }
            """);

        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task QuintupleCompositeAccessAcceptsAllDirectViewsWithOwnerCleanup()
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
                    using Pooled<int> faces = pool.Rent(1);
                    using Pooled<int> vertices = pool.Rent(1);
                    using Pooled<int> indices = pool.Rent(1);
                    ArenaLease<int> slices = arena.Scratch<int>(1);
                    ArenaLease<byte> upload = arena.Scratch<byte>(1);
                    NativeLeaseOperations.Access(
                        faces,
                        vertices,
                        indices,
                        slices,
                        upload,
                        static (faceView, vertexView, indexView, sliceView, uploadView) =>
                        {
                            faceView[0] = vertexView[0] + indexView[0] + sliceView[0] + uploadView[0];
                        });
                }
            }
            """);

        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task CompositeAccessStillRequiresPoolAndArenaOwnershipCleanup()
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
                    Pooled<int> faces = pool.Rent(1);
                    Pooled<int> vertices = pool.Rent(1);
                    Pooled<int> indices = pool.Rent(1);
                    ArenaLease<int> slices = arena.Scratch<int>(1);
                    ArenaLease<byte> upload = arena.Scratch<byte>(1);
                    NativeLeaseOperations.Access(
                        faces,
                        vertices,
                        indices,
                        slices,
                        upload,
                        static (_, _, _, _, _) => { });
                }
            }
            """);

        string[] ids = AnalyzerContractTests.NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1003", ids);
    }

    [Fact]
    public async Task CompositeScopedRootsRequireCompletionOnEveryControlFlowPath()
    {
        ImmutableArray<Diagnostic> missing = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    using NativePool<int> pool = new();
                    {
                        scoped Pooled<int> faces = pool.LeaseScoped(1);
                        scoped Pooled<int> vertices = pool.LeaseScoped(1);
                        NativeLeaseOperations.Access(faces, vertices, static (_, _) => { });
                        if (condition)
                        {
                            pool.RecycleScoped();
                        }
                    }
                }
            }
            """);
        Assert.Contains("NAM1020", AnalyzerContractTests.NativeDiagnostics(missing));

        ImmutableArray<Diagnostic> premature = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    scoped Pooled<int> value = pool.LeaseScoped(1);
                    pool.RecycleScoped();
                    _ = value.Length;
                }
            }
            """);
        Assert.Contains("NAM1007", AnalyzerContractTests.NativeDiagnostics(premature));
    }

    [Fact]
    public async Task CompositeOperationsRejectUnknownHandleRetentionBeforeEntry()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    using Pooled<int> value = pool.Rent(1);
                    Retain(value);
                    NativeLeaseOperations.Access(value, value, static (_, _) => { });
                }

                private static void Retain(Pooled<int> value) { }
            }
            """);

        string[] ids = AnalyzerContractTests.NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1016", ids);
    }

    [Fact]
    public async Task CompositeScopedFlowChecksRemainConservativeAcrossBranchesAndExceptions()
    {
        ImmutableArray<Diagnostic> branch = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    using NativePool<int> first = new();
                    using NativePool<int> second = new();
                    scoped Pooled<int> left = first.LeaseScoped(1);
                    scoped Pooled<int> right = second.LeaseScoped(1);
                    NativeLeaseOperations.Access(left, right, static (_, _) => { });
                    if (condition)
                    {
                        first.RecycleScoped();
                    }
                    second.RecycleScoped();
                }
            }
            """);
        Assert.Contains("NAM1020", AnalyzerContractTests.NativeDiagnostics(branch));

        ImmutableArray<Diagnostic> exceptional = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    using NativePool<int> pool = new();
                    try
                    {
                        scoped Pooled<int> value = pool.LeaseScoped(1);
                        NativeLeaseOperations.Access(value, value, static (_, _) => { });
                        if (condition)
                        {
                            throw new System.InvalidOperationException();
                        }
                    }
                    finally
                    {
                        pool.RecycleScoped();
                    }
                }
            }
            """);
        Assert.DoesNotContain("NAM1020", AnalyzerContractTests.NativeDiagnostics(exceptional));

        ImmutableArray<Diagnostic> premature = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    using NativePool<int> first = new();
                    using NativePool<int> second = new();
                    scoped Pooled<int> left = first.LeaseScoped(1);
                    scoped Pooled<int> right = second.LeaseScoped(1);
                    first.RecycleScoped();
                    NativeLeaseOperations.Access(left, right, static (_, _) => { });
                    second.RecycleScoped();
                }
            }
            """);
        Assert.Contains("NAM1007", AnalyzerContractTests.NativeDiagnostics(premature));
    }

    [Fact]
    public async Task CompositeScopedRootsRejectEarlyAndLoopExitsBeforeRecycle()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void ReturnBeforeRecycle(bool condition)
                {
                    using NativePool<int> pool = new();
                    scoped Pooled<int> value = pool.LeaseScoped(1);
                    if (condition)
                    {
                        return;
                    }

                    pool.RecycleScoped();
                }

                public static void ThrowBeforeRecycle(bool condition)
                {
                    using NativePool<int> pool = new();
                    scoped Pooled<int> value = pool.LeaseScoped(1);
                    if (condition)
                    {
                        throw new System.InvalidOperationException();
                    }

                    pool.RecycleScoped();
                }

                public static void ContinueBeforeRecycle()
                {
                    using NativePool<int> pool = new();
                    for (int index = 0; index != 2; index++)
                    {
                        scoped Pooled<int> value = pool.LeaseScoped(1);
                        if (index == 0)
                        {
                            continue;
                        }

                        pool.RecycleScoped();
                    }
                }

                public static void BreakBeforeRecycle()
                {
                    using NativePool<int> pool = new();
                    while (true)
                    {
                        scoped Pooled<int> value = pool.LeaseScoped(1);
                        break;
                    }
                }

                public static void GotoBeforeRecycle()
                {
                    using NativePool<int> pool = new();
                    scoped Pooled<int> value = pool.LeaseScoped(1);
                    goto Exit;
                    pool.RecycleScoped();
                Exit:
                    _ = pool;
                }
            }
            """);

        Assert.Contains("NAM1020", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task CompositeScopedRecycleRejectsAnOuterLiveRootAndAmbiguousAcquisition()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void OuterRoot()
                {
                    using NativePool<int> pool = new();
                    scoped Pooled<int> outer = pool.LeaseScoped(1);
                    {
                        scoped Pooled<int> inner = pool.LeaseScoped(1);
                        pool.RecycleScoped();
                    }
                }

                public static void AmbiguousAcquisition(bool condition)
                {
                    using NativePool<int> pool = new();
                    scoped Pooled<int> value = condition
                        ? pool.LeaseScoped(1)
                        : pool.LeaseScoped(2);
                    if (condition)
                    {
                        pool.RecycleScoped();
                    }
                }
            }
            """);

        Assert.Contains("NAM1020", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task FinallyRecycleCannotDischargeAnOuterScopedRoot()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    scoped Pooled<int> outer = pool.LeaseScoped(1);
                    try
                    {
                        {
                            scoped Pooled<int> inner = pool.LeaseScoped(1);
                        }
                    }
                    finally
                    {
                        pool.RecycleScoped();
                    }
                }
            }
            """);

        Assert.True(
            diagnostics.Any(item => item.Id == "NAM1007"),
            string.Join(Environment.NewLine, diagnostics.Select(item => item.ToString())));
        Diagnostic diagnostic = diagnostics.First(item => item.Id == "NAM1007");
        Assert.Contains("outer", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("RecycleScoped", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(16, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public async Task FinallyRecycleMustRunOnEveryCleanupPath()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    using NativePool<int> pool = new();
                    try
                    {
                        scoped Pooled<int> value = pool.LeaseScoped(1);
                    }
                    finally
                    {
                        if (condition)
                        {
                            pool.RecycleScoped();
                        }
                    }
                }
            }
            """);

        Assert.True(
            diagnostics.Count(item => item.Id == "NAM1020") == 1,
            string.Join(Environment.NewLine, diagnostics.Select(item => item.ToString())));
        Diagnostic diagnostic = diagnostics.First(item => item.Id == "NAM1020");
        Assert.Contains("pool", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, item => item.Id == "NAM1007");
    }

    [Fact]
    public async Task FinallyCleanupReportsEachEarlyExitIndependently()
    {
        string[] bodies =
        [
            "if (condition) { return; }",
            "if (condition) { throw new System.InvalidOperationException(); }",
            "while (condition) { scoped Pooled<int> inner = pool.LeaseScoped(1); break; }",
            "while (condition) { scoped Pooled<int> inner = pool.LeaseScoped(1); continue; }",
            "if (condition) { goto Exit; }"
        ];

        foreach (string body in bodies)
        {
            ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
                $$"""
                using Supprocom.NativeAllocationManagement;

                public static class Sample
                {
                    public static void Run(bool condition)
                    {
                        using NativePool<int> pool = new();
                        scoped Pooled<int> value = pool.LeaseScoped(1);
                        {{body}}
                        pool.RecycleScoped();
                    Exit:
                        _ = pool;
                    }
                }
                """);

            Diagnostic[] missing = diagnostics.Where(item => item.Id == "NAM1020").ToArray();
            Assert.True(missing.Length == 1, string.Join(Environment.NewLine, diagnostics));
            Assert.Contains("pool", missing[0].GetMessage(), StringComparison.Ordinal);
            Assert.Equal("NAM1020", missing[0].Properties["NAM.DiagnosticId"]);
            Assert.Equal("Scoped native storage is not recycled on every exit", missing[0].Properties["NAM.Operation"]);
            Assert.Contains("pool", missing[0].Properties["NAM.Provenance"]!);
            Assert.Contains("pool.LeaseScoped", missing[0].Location.SourceTree!.GetText().ToString(missing[0].Location.SourceSpan));
        }
    }

    [Fact]
    public async Task NestedFinallyRequiresCleanupOnEveryNestedExit()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    using NativePool<int> pool = new();
                    try
                    {
                        scoped Pooled<int> value = pool.LeaseScoped(1);
                        try
                        {
                            if (condition)
                            {
                                throw new System.InvalidOperationException();
                            }
                        }
                        finally
                        {
                            if (condition)
                            {
                                pool.RecycleScoped();
                            }
                        }
                    }
                    finally
                    {
                        pool.RecycleScoped();
                    }
                }
            }
            """);

        Diagnostic[] liveRoot = diagnostics.Where(item => item.Id == "NAM1007").ToArray();
        Assert.True(liveRoot.Length == 1, string.Join(Environment.NewLine, diagnostics));
        Assert.Contains("value", liveRoot[0].GetMessage(), StringComparison.Ordinal);
        Assert.Equal("NAM1007", liveRoot[0].Properties["NAM.DiagnosticId"]);
        Assert.Equal("Native return has live generation state", liveRoot[0].Properties["NAM.Operation"]);
        Assert.Contains("value", liveRoot[0].Properties["NAM.Provenance"]!);
        Assert.Contains("pool.RecycleScoped", liveRoot[0].Location.SourceTree!.GetText().ToString(liveRoot[0].Location.SourceSpan));
    }

    [Fact]
    public async Task RoslynFinallyEdgesCoverCatchFiltersAndNestedFinallyWithoutFalseMissingCleanup()
    {
        ImmutableArray<Diagnostic> catchFilter = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void CatchFilter(bool condition)
                {
                    using NativePool<int> pool = new();
                    try
                    {
                        scoped Pooled<int> value = pool.LeaseScoped(1);
                        if (condition)
                        {
                            throw new System.InvalidOperationException();
                        }
                    }
                    catch (System.InvalidOperationException) when (condition)
                    {
                    }
                    finally
                    {
                        pool.RecycleScoped();
                    }
                }
            }
            """);

        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(catchFilter));

        ImmutableArray<Diagnostic> nested = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Nested(bool condition)
                {
                    using NativePool<int> pool = new();
                    try
                    {
                        scoped Pooled<int> value = pool.LeaseScoped(1);
                        try
                        {
                            if (condition)
                            {
                                throw new System.InvalidOperationException();
                            }
                        }
                        finally
                        {
                        }
                    }
                    finally
                    {
                        pool.RecycleScoped();
                    }
                }
            }
            """);

        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(nested));
    }

    [Fact]
    public async Task RoslynFinallyEdgesRejectUncleanCatchFilterAndNestedFinallyExits()
    {
        ImmutableArray<Diagnostic> catchFilter = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void CatchFilter(bool condition)
                {
                    using NativePool<int> pool = new();
                    try
                    {
                        scoped Pooled<int> value = pool.LeaseScoped(1);
                        throw new System.InvalidOperationException();
                    }
                    catch (System.InvalidOperationException) when (condition)
                    {
                        pool.RecycleScoped();
                    }
                }
            }
            """);

        Diagnostic[] catchMissing = catchFilter.Where(item => item.Id == "NAM1020").ToArray();
        Assert.Single(catchMissing);
        Assert.Contains("pool", catchMissing[0].Properties["NAM.Provenance"]!, StringComparison.OrdinalIgnoreCase);

        ImmutableArray<Diagnostic> nestedFinally = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void NestedFinally(bool condition)
                {
                    using NativePool<int> pool = new();
                    try
                    {
                        scoped Pooled<int> value = pool.LeaseScoped(1);
                    }
                    finally
                    {
                        try
                        {
                            if (condition)
                            {
                                pool.RecycleScoped();
                            }
                        }
                        finally
                        {
                        }
                    }
                }
            }
            """);

        Diagnostic[] nestedMissing = nestedFinally.Where(item => item.Id == "NAM1020").ToArray();
        Assert.Single(nestedMissing);
        Assert.Contains("pool", nestedMissing[0].Properties["NAM.Provenance"]!);
    }

    [Fact]
    public async Task FinallyLoopFixedPointReportsOneStableMissingCompletion()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool condition)
                {
                    using NativePool<int> pool = new();
                    try
                    {
                        scoped Pooled<int> value = pool.LeaseScoped(1);
                    }
                    finally
                    {
                        for (int index = 0; index < 2; index++)
                        {
                            if (condition && index == 1)
                            {
                                pool.RecycleScoped();
                            }
                        }
                    }
                }
            }
            """);

        Diagnostic[] missing = diagnostics.Where(item => item.Id == "NAM1020").ToArray();
        Assert.Single(missing);
        Assert.Equal("NAM1020", missing[0].Properties["NAM.DiagnosticId"]);
        Assert.Equal("Scoped native storage is not recycled on every exit", missing[0].Properties["NAM.Operation"]);
        Assert.Contains("pool", missing[0].Properties["NAM.Provenance"]!);
        Diagnostic[] ambiguous = diagnostics.Where(item => item.Id == "NAM1007").ToArray();
        Assert.Single(ambiguous);
        Assert.Equal("NAM1007", ambiguous[0].Properties["NAM.DiagnosticId"]);
        Assert.Contains("ambiguous scoped allocation", ambiguous[0].Properties["NAM.Provenance"]!);
    }

}
