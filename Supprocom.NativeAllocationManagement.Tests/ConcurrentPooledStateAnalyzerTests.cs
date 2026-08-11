using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class ConcurrentPooledStateAnalyzerTests
{
    [Fact]
    public async Task StaticApplicationStateAndExactSourceCallbackAreAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public sealed class Renderer
            {
                public int Build(
                    Workspace workspace,
                    Span<int> values) =>
                    values[0] + workspace.Offset;
            }

            public sealed class Workspace
            {
                public int Offset { get; init; }
            }

            public readonly record struct ProcessState(
                Renderer Renderer,
                Workspace Workspace);

            public static class Sample
            {
                public static int Run()
                {
                    using NativeConcurrentPool<int> pool = new();
                    using ConcurrentPooled<int> values = pool.Rent(
                        2,
                        static writer => writer.Fill(7));
                    ProcessState state = new(
                        new Renderer(),
                        new Workspace { Offset = 5 });
                    return values.Process(
                        2,
                        in state,
                        static (view, current) =>
                            current.Renderer.Build(
                                current.Workspace,
                                view.AsSpan()));
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task NamedCallbackAndNestedScopedHelpersAreAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public readonly record struct ProcessState(int Offset);

            public static class Sample
            {
                public static int Run()
                {
                    using NativeConcurrentPool<int> pool = new();
                    using ConcurrentPooled<int> values = pool.Rent(
                        2,
                        static writer => writer.Fill(11));
                    ProcessState state = new(3);
                    return values.Process(
                        2,
                        in state,
                        Process);
                }

                private static int Process(
                    scoped NativeLeaseView<int> view,
                    ProcessState state) =>
                    Nested(view, in state);

                private static int Nested(
                    scoped NativeLeaseView<int> view,
                    scoped in ProcessState state) =>
                    view[0] + state.Offset;
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task StackBoundStateIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public readonly ref struct ProcessState
            {
                public ProcessState(ReadOnlySpan<int> values)
                {
                    Values = values;
                }

                public ReadOnlySpan<int> Values { get; }
            }

            public static class Sample
            {
                public static int Run()
                {
                    using NativeConcurrentPool<int> pool = new();
                    using ConcurrentPooled<int> values = pool.Rent(
                        1,
                        static writer => writer.Fill(1));
                    Span<int> source = stackalloc int[1];
                    source[0] = 13;
                    ProcessState state = new(source);
                    return values.Process(
                        1,
                        in state,
                        static (view, current) =>
                            view[0] + current.Values[0]);
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task CapturingAndIndirectCallbacksAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public readonly record struct ProcessState(int Offset);

            public static class Sample
            {
                public static void Run(bool first)
                {
                    using NativeConcurrentPool<int> pool = new();
                    using ConcurrentPooled<int> values = pool.Rent(
                        1,
                        static writer => writer.Fill(1));
                    ProcessState state = new(2);
                    int captured = 3;
                    _ = values.Process(
                        1,
                        in state,
                        (view, current) =>
                            view[0] + current.Offset + captured);

                    NativeLeaseStateFunc<int, ProcessState, int> stored =
                        Direct;
                    _ = values.Process(1, in state, stored);
                    _ = values.Process(
                        1,
                        in state,
                        Identity(Direct));
                    _ = values.Process(
                        1,
                        in state,
                        first ? Direct : stored);
                }

                private static int Direct(
                    scoped NativeLeaseView<int> view,
                    ProcessState state) =>
                    view[0] + state.Offset;

                private static NativeLeaseStateFunc<int, ProcessState, int>
                    Identity(
                        NativeLeaseStateFunc<int, ProcessState, int>
                            callback) =>
                    callback;
            }
            """);

        Assert.True(
            NativeDiagnostics(diagnostics)
                .Count(id => id == "NAM1047") >= 4,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task AsyncAndIteratorCallbacksAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public readonly record struct ProcessState(int Offset);

            public static class Sample
            {
                public static void Run()
                {
                    using NativeConcurrentPool<int> pool = new();
                    using ConcurrentPooled<int> values = pool.Rent(
                        1,
                        static writer => writer.Fill(1));
                    ProcessState state = new(2);
                    _ = values.Process(
                        1,
                        in state,
                        Async);
                    _ = values.Process(
                        1,
                        in state,
                        Iterate);
                }

                private static async Task<int> Async(
                    NativeLeaseView<int> view,
                    ProcessState state)
                {
                    await Task.Yield();
                    return view[0] + state.Offset;
                }

                private static IEnumerable<int> Iterate(
                    NativeLeaseView<int> view,
                    ProcessState state)
                {
                    yield return view[0] + state.Offset;
                }
            }
            """);

        Assert.True(
            NativeDiagnostics(diagnostics)
                .Count(id => id == "NAM1047") >= 2,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task OwnerBearingStatesAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public readonly record struct DirectState(
                NativeConcurrentPool<int> Pool);

            public readonly record struct NestedState(DirectState Owner);

            public static class Sample
            {
                public static void Run()
                {
                    using NativeConcurrentPool<int> pool = new();
                    using ConcurrentPooled<int> values = pool.Rent(
                        1,
                        static writer => writer.Fill(1));
                    DirectState direct = new(pool);
                    NestedState nested = new(direct);
                    (int Value, NativeConcurrentPool<int> Pool) tuple =
                        (1, pool);
                    _ = values.Process(
                        1,
                        in direct,
                        static (view, _) => view[0]);
                    _ = values.Process(
                        1,
                        in nested,
                        static (view, _) => view[0]);
                    _ = values.Process(
                        1,
                        in tuple,
                        static (view, _) => view[0]);
                }
            }
            """);

        Assert.True(
            NativeDiagnostics(diagnostics)
                .Count(id => id == "NAM1048") >= 3,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task StateAliasStorageBoxAndReturnAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public readonly record struct ProcessState(int Offset);

            public static class Sample
            {
                private static object? retained;

                public static void Run()
                {
                    using NativeConcurrentPool<int> pool = new();
                    using ConcurrentPooled<int> values = pool.Rent(
                        1,
                        static writer => writer.Fill(1));
                    ProcessState state = new(2);
                    _ = values.Process(
                        1,
                        in state,
                        static (view, current) =>
                        {
                            ProcessState alias = current;
                            retained = current;
                            object boxed = current;
                            Store(current);
                            _ = alias;
                            _ = boxed;
                            return current;
                        });
                }

                private static void Store(ProcessState state)
                {
                }
            }
            """);

        Assert.True(
            NativeDiagnostics(diagnostics)
                .Count(id => id == "NAM1049") >= 4,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task ViewAliasAndUnscopedForwardingAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public readonly record struct ProcessState(int Offset);

            public static class Sample
            {
                public static void Run()
                {
                    using NativeConcurrentPool<int> pool = new();
                    using ConcurrentPooled<int> values = pool.Rent(
                        1,
                        static writer => writer.Fill(1));
                    ProcessState state = new(2);
                    _ = values.Process(
                        1,
                        in state,
                        static (view, current) =>
                        {
                            NativeLeaseView<int> alias = view;
                            Store(view);
                            _ = alias;
                            return Echo(view)[0] + current.Offset;
                        });
                }

                private static void Store(NativeLeaseView<int> view)
                {
                }

                private static NativeLeaseView<int> Echo(
                    NativeLeaseView<int> view) =>
                    view;
            }
            """);

        Assert.True(
            NativeDiagnostics(diagnostics)
                .Count(id => id == "NAM1024") >= 3,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task NestedCallbacksCannotCaptureStateOrView()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public readonly record struct ProcessState(int Offset);

            public static class Sample
            {
                public static void Run()
                {
                    using NativeConcurrentPool<int> pool = new();
                    using ConcurrentPooled<int> values = pool.Rent(
                        1,
                        static writer => writer.Fill(1));
                    ProcessState state = new(2);
                    _ = values.Process(
                        1,
                        in state,
                        static (view, current) =>
                        {
                            Func<int> stateCapture = () => current.Offset;
                            Func<int> viewCapture = () => view[0];
                            return stateCapture() + viewCapture();
                        });
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1049", ids);
        Assert.Contains("NAM1024", ids);
    }

    [Fact]
    public async Task NestedHelpersCannotStoreOrReturnAuthority()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public readonly record struct ProcessState(int Offset);

            public static class Sample
            {
                private static object? retained;

                public static void Run()
                {
                    using NativeConcurrentPool<int> pool = new();
                    using ConcurrentPooled<int> values = pool.Rent(
                        1,
                        static writer => writer.Fill(1));
                    ProcessState state = new(2);
                    _ = values.Process(
                        1,
                        in state,
                        static (view, current) =>
                            Nested(view, in current));
                }

                private static int Nested(
                    scoped NativeLeaseView<int> view,
                    scoped in ProcessState state)
                {
                    retained = state;
                    NativeLeaseView<int> alias = view;
                    _ = alias;
                    return view[0];
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1049", ids);
        Assert.Contains("NAM1024", ids);
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
