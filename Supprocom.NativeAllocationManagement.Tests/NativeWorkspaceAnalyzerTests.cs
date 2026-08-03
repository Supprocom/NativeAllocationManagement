using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeWorkspaceAnalyzerTests
{
    [Fact]
    public async Task UsingWorkspaceAndBoundedOperationsAreAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static int Run(NativePool<int> pool)
                {
                    using NativeWorkspace<int> workspace =
                        pool.CreateWorkspace(16);
                    return workspace.Process(
                        16,
                        static values => values.Fill(7),
                        static values => values[0]);
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task ExplicitFinallyDisposalIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(
                    NativePool<int> pool,
                    bool stop)
                {
                    NativeWorkspace<int> workspace =
                        pool.CreateWorkspace(8);
                    try
                    {
                        if (stop)
                        {
                            return;
                        }

                        workspace.Process(
                            8,
                            static values => values.Fill(11),
                            static values => values[0]);
                    }
                    finally
                    {
                        workspace.Dispose();
                    }
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task ScopedReadOnlyHelperBorrowIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static int Run(NativePool<int> pool)
                {
                    using NativeWorkspace<int> workspace =
                        pool.CreateWorkspace(8);
                    return Read(in workspace);
                }

                private static int Read(
                    scoped in NativeWorkspace<int> workspace)
                {
                    return workspace.Process(
                        8,
                        static values => values.Fill(13),
                        static values => values[0]);
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task PersistentWorkspaceBorrowSurvivesNestedCleanupAndExceptionLoops()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using System.Threading;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(
                    CancellationToken token,
                    bool condition)
                {
                    using NativePool<float> pool = new(
                        preLease: 51_200,
                        returnMemoryOnDispose:
                            NativeMemoryReturn.ToNativeMemory);
                    using NativeWorkspace<float> workspace =
                        pool.CreateWorkspace(51_200);
                    int lastValue = 0;
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            if (!TryTake(out int value))
                            {
                                continue;
                            }

                            lastValue = value;
                            try
                            {
                                if (condition)
                                {
                                    using IDisposable scope = new Scope();
                                    continue;
                                }

                                Action<int> publish = static value =>
                                {
                                    _ = value;
                                };
                                while (!token.IsCancellationRequested
                                    && TryTake(out int batch))
                                {
                                    Generate(in workspace);
                                    publish(batch);
                                }
                            }
                            catch (Exception)
                            {
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception)
                    {
                        _ = lastValue;
                    }
                }

                private static void Generate(
                    scoped in NativeWorkspace<float> workspace)
                {
                    workspace.Process(
                        51_200,
                        static values => values.Fill(1),
                        static values => values[0]);
                }

                private static bool TryTake(out int value)
                {
                    value = 1;
                    return true;
                }

                private sealed class Scope : IDisposable
                {
                    public void Dispose()
                    {
                    }
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task WorkspaceCopyIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    NativeWorkspace<int> workspace =
                        pool.CreateWorkspace(8);
                    NativeWorkspace<int> alias = workspace;
                    alias.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1036", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task IncompleteWorkspaceLifetimeIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(
                    NativePool<int> pool,
                    bool dispose)
                {
                    NativeWorkspace<int> workspace =
                        pool.CreateWorkspace(8);
                    if (dispose)
                    {
                        workspace.Dispose();
                    }
                }
            }
            """);

        Assert.Contains("NAM1038", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task UseAfterDisposalAndDoubleDisposalAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    NativeWorkspace<int> workspace =
                        pool.CreateWorkspace(8);
                    workspace.Dispose();
                    _ = workspace.Capacity;
                    workspace.Dispose();
                }
            }
            """);

        Assert.Equal(
            2,
            NativeDiagnostics(diagnostics).Count(
                id => id == "NAM1037"));
    }

    [Fact]
    public async Task FactoryEscapesAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static NativeWorkspace<int> Return(
                    NativePool<int> pool)
                {
                    return pool.CreateWorkspace(8);
                }

                public static void Discard(NativePool<int> pool)
                {
                    _ = pool.CreateWorkspace(8);
                    Drop(pool.CreateWorkspace(8));
                }

                private static void Drop(
                    NativeWorkspace<int> workspace)
                {
                    workspace.Dispose();
                }
            }
            """);

        Assert.Equal(
            3,
            NativeDiagnostics(diagnostics).Count(
                id => id == "NAM1039"));
    }

    [Theory]
    [InlineData("NativeWorkspace<int> workspace")]
    [InlineData("in NativeWorkspace<int> workspace")]
    [InlineData("scoped ref NativeWorkspace<int> workspace")]
    [InlineData("out NativeWorkspace<int> workspace")]
    public async Task UnsupportedParameterShapesAreRejected(
        string parameter)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            $$"""
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Use({{parameter}})
                {
                }
            }
            """);

        Assert.Contains("NAM1040", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task BorrowedWorkspaceDisposalAndCopyAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Use(
                    scoped in NativeWorkspace<int> workspace)
                {
                    NativeWorkspace<int> alias = workspace;
                    workspace.Dispose();
                    alias.Dispose();
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1036", ids);
        Assert.Contains("NAM1037", ids);
    }

    [Fact]
    public async Task ClosureCaptureIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    NativeWorkspace<int> workspace =
                        pool.CreateWorkspace(8);
                    Action action = () => workspace.Reset();
                    action();
                    workspace.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1036", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task WorkspaceReturnAndRefStructStorageAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public ref struct WorkspaceHolder
            {
                public NativeWorkspace<int> Value;
            }

            public static class Sample
            {
                public static NativeWorkspace<int> Return(
                    NativePool<int> pool)
                {
                    NativeWorkspace<int> workspace =
                        pool.CreateWorkspace(8);
                    return workspace;
                }

                public static void Store(NativePool<int> pool)
                {
                    NativeWorkspace<int> workspace =
                        pool.CreateWorkspace(8);
                    WorkspaceHolder holder = default;
                    holder.Value = workspace;
                    workspace.Dispose();
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1036", ids);
        Assert.Contains("NAM1038", ids);
    }

    [Fact]
    public async Task WorkspaceAcrossAsyncSuspensionIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static async Task Run(NativePool<int> pool)
                {
                    NativeWorkspace<int> workspace =
                        pool.CreateWorkspace(8);
                    await Task.Yield();
                    workspace.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1036", NativeDiagnostics(diagnostics));
    }

    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source) =>
        AnalyzerContractTests.AnalyzeAsync(source);

    private static string[] NativeDiagnostics(
        ImmutableArray<Diagnostic> diagnostics) =>
        AnalyzerContractTests.NativeDiagnostics(diagnostics);

    private static void AssertNoNativeDiagnostics(
        ImmutableArray<Diagnostic> diagnostics)
    {
        Assert.True(
            NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }
}
