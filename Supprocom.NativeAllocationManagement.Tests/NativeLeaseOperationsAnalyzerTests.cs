using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeLeaseOperationsAnalyzerTests
{
    [Fact]
    public async Task MeshCompositeAccessAcceptsAllDirectViewsWithOwnerCleanup()
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
                    scoped Pooled<int> value = pool.LeaseScoped(1);
                    try
                    {
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

}
