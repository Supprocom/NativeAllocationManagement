using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Supprocom.NativeAllocationManagement.Analyzers;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeBuilderAnalyzerTests
{
    [Fact]
    public async Task LocalBuilderCompletionAndTransferDisposalAreAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    using NativeBuilder<int> builder =
                        pool.CreateBuilder(initialCapacity: 1);
                    builder.Append(11);
                    builder.Append(new int[] { 13, 17 });
                    NativeTransfer<int> transfer = builder.Complete();
                    _ = transfer.Read(static view => view[0]);
                    transfer.Dispose();
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task LocalBuilderDisposalIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Append(19);
                    builder.Dispose();
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task DirectTypedCompletionReturnIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static NativeTransfer<int> Build(
                    NativePool<int> pool)
                {
                    using NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Append(23);
                    return builder.Complete();
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task OwnedTransferReceiverAcceptsCompletion()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    using NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Append(29);
                    Consume(builder.Complete());
                }

                private static void Consume(NativeTransfer<int> transfer)
                {
                    transfer.Dispose();
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task ExactTypedFieldAcceptsCompletedTransfer()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public sealed class Sample
            {
                private NativeTransfer<int>? _transfer;

                public void Build(NativePool<int> pool)
                {
                    using NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Append(31);
                    _transfer = builder.Complete();
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task BuilderCopyIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    NativeBuilder<int> builder = pool.CreateBuilder();
                    NativeBuilder<int> alias = builder;
                    alias.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1028", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task BuilderParameterAndArgumentAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    NativeBuilder<int> builder = pool.CreateBuilder();
                    Drop(builder);
                }

                private static void Drop(NativeBuilder<int> builder)
                {
                    builder.Dispose();
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1028", ids);
        Assert.Contains("NAM1033", ids);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("dynamic")]
    public async Task UntypedBuilderErasureIsRejected(
        string destinationType)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            $$"""
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    NativeBuilder<int> builder = pool.CreateBuilder();
                    {{destinationType}} erased = builder;
                    _ = erased;
                    builder.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1028", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task FieldAndPropertyBuilderAcquisitionAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public sealed class Sample
            {
                private NativeBuilder<int>? _field;
                private NativeBuilder<int>? Property { get; set; }

                public void Run(NativePool<int> pool)
                {
                    _field = pool.CreateBuilder();
                    Property = pool.CreateBuilder();
                }
            }
            """);

        Assert.Equal(
            2,
            NativeDiagnostics(diagnostics).Count(id => id == "NAM1032"));
    }

    [Fact]
    public async Task FactoryReturnDiscardAndAggregateAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static NativeBuilder<int> Return(NativePool<int> pool)
                {
                    return pool.CreateBuilder();
                }

                public static void Discard(NativePool<int> pool)
                {
                    _ = pool.CreateBuilder();
                    _ = (pool.CreateBuilder(), 1);
                }
            }
            """);

        Assert.Equal(
            3,
            NativeDiagnostics(diagnostics).Count(id => id == "NAM1032"));
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
                    NativeBuilder<int> builder = pool.CreateBuilder();
                    Action append = () => builder.Append(37);
                    append();
                    builder.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1028", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task UseAfterCompletionIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    NativeBuilder<int> builder = pool.CreateBuilder();
                    NativeTransfer<int> transfer = builder.Complete();
                    builder.Append(41);
                    transfer.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1029", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task DoubleCompletionIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    NativeBuilder<int> builder = pool.CreateBuilder();
                    NativeTransfer<int> first = builder.Complete();
                    NativeTransfer<int> second = builder.Complete();
                    first.Dispose();
                    second.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1030", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task UseAfterDisposalIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Dispose();
                    _ = builder.Count;
                }
            }
            """);

        Assert.Contains("NAM1029", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task IncompleteBuilderLifetimeIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool, bool dispose)
                {
                    NativeBuilder<int> builder = pool.CreateBuilder();
                    if (dispose)
                    {
                        builder.Dispose();
                    }
                }
            }
            """);

        Assert.Contains("NAM1031", NativeDiagnostics(diagnostics));
    }

    [Theory]
    [InlineData("object erased = builder.Complete();")]
    [InlineData("_ = (builder.Complete(), 1);")]
    [InlineData("_ = System.Threading.Tasks.Task.FromResult(builder.Complete());")]
    [InlineData("_ = builder.Complete();")]
    public async Task UntypedOrAggregateCompletionIsRejected(
        string completion)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            $$"""
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Append(43);
                    {{completion}}
                }
            }
            """);

        Assert.Contains("NAM1034", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ActiveBuilderOverwriteIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    NativeBuilder<int> builder = pool.CreateBuilder();
                    builder = pool.CreateBuilder();
                    builder.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1031", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ConditionalBuilderAliasIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(
                    NativePool<int> pool,
                    bool first)
                {
                    NativeBuilder<int> builder = pool.CreateBuilder();
                    NativeBuilder<int> alias = first
                        ? builder
                        : builder;
                    alias.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1028", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ProvenBoundedChannelAcceptsCompletion()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System.Threading.Channels;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static async Task Run(NativePool<int> pool)
                {
                    Channel<NativeTransfer<int>> channel =
                        Channel.CreateBounded<NativeTransfer<int>>(1);
                    using NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Append(47);
                    await channel.Writer.WriteAsync(builder.Complete());
                    NativeTransfer<int> transfer =
                        await channel.Reader.ReadAsync();
                    transfer.Dispose();
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task UnboundedChannelRejectsCompletion()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System.Threading.Channels;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static async Task Run(NativePool<int> pool)
                {
                    Channel<NativeTransfer<int>> channel =
                        Channel.CreateUnbounded<NativeTransfer<int>>();
                    NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Append(53);
                    await channel.Writer.WriteAsync(builder.Complete());
                }
            }
            """);

        Assert.Contains("NAM1034", NativeDiagnostics(diagnostics));
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
