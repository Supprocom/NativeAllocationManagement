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
                        pool.CreateBuilder(preLease: 1);
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
    public async Task BoundedDirectWriteIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    using NativeBuilder<int> builder =
                        pool.CreateBuilder(preLease: 4);
                    builder.Write(
                        4,
                        static writer =>
                        {
                            Span<int> values = writer.AsSpan();
                            values[0] = 2;
                            values[1] = 3;
                            values[2] = 5;
                            writer.Commit(3);
                        });
                    NativeTransfer<int> transfer = builder.Complete();
                    transfer.Dispose();
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task NamedScopedDirectWriterIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    using NativeBuilder<int> builder =
                        pool.CreateBuilder(preLease: 2);
                    builder.Write(2, WriteValues);
                    NativeTransfer<int> transfer = builder.Complete();
                    transfer.Dispose();
                }

                private static void WriteValues(
                    scoped NativeBuilderWriter<int> writer)
                {
                    writer.AsSpan().Fill(7);
                    writer.Commit(writer.Length);
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task ExclusiveBorrowAndNestedScopedHelpersAreAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<uint> pool)
                {
                    using NativeBuilder<uint> builder =
                        pool.CreateBuilder(preLease: 4);
                    builder.Borrow(Emit);
                    NativeTransfer<uint> transfer = builder.Complete();
                    transfer.Dispose();
                }

                private static void Emit(
                    scoped ref NativeBuilderBorrow<uint> borrow) =>
                    EmitNested(ref borrow);

                private static void EmitNested(
                    scoped ref NativeBuilderBorrow<uint> borrow)
                {
                    borrow.Write(4, WriteWords);
                }

                private static void WriteWords(
                    scoped NativeBuilderWriter<uint> writer) =>
                    WriteWordsNested(ref writer);

                private static void WriteWordsNested(
                    scoped ref NativeBuilderWriter<uint> writer)
                {
                    writer.AsSpan().Fill(7);
                    writer.Commit(writer.Length);
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task BuilderWriteViewEscapeIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    using NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Write(
                        2,
                        static writer =>
                        {
                            Retain(writer.AsSpan());
                            writer.Commit(2);
                        });
                    builder.Dispose();
                }

                private static void Retain(Span<int> values)
                {
                    _ = values.Length;
                }
            }
            """);

        Assert.Contains("NAM1041", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task BuilderWriterAliasIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    using NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Write(
                        1,
                        static writer =>
                        {
                            NativeBuilderWriter<int> alias = writer;
                            alias.Commit(0);
                        });
                    builder.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1042", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task BuilderWriterHelperAuthorityIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    using NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Write(
                        1,
                        static writer => Commit(writer));
                    builder.Dispose();
                }

                private static void Commit(
                    NativeBuilderWriter<int> writer) =>
                    writer.Commit(0);
            }
            """);

        Assert.Contains("NAM1042", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task IndirectBuilderWriteCallbackIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    NativeBuilderWriteAction<int> action =
                        static writer => writer.Commit(0);
                    using NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Write(0, action);
                    NativeTransfer<int> transfer = builder.Complete();
                    transfer.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1042", NativeDiagnostics(diagnostics));
    }

    [Theory]
    [InlineData("NativeBuilderBorrow<int> borrow")]
    [InlineData("in NativeBuilderBorrow<int> borrow")]
    [InlineData("ref NativeBuilderBorrow<int> borrow")]
    [InlineData("out NativeBuilderBorrow<int> borrow")]
    public async Task NonScopedRefBorrowParametersAreRejected(
        string parameter)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            $$"""
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Invalid({{parameter}})
                {
                    {{(parameter.StartsWith("out", StringComparison.Ordinal) ? "borrow = default;" : "_ = borrow.Count;")}}
                }
            }
            """);

        Assert.Contains("NAM1044", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task BorrowStorageAndUseAfterCallbackAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    using NativeBuilder<int> builder = pool.CreateBuilder();
                    NativeBuilderBorrow<int> escaped = default;
                    builder.Borrow(
                        (scoped ref NativeBuilderBorrow<int> borrow) =>
                            escaped = borrow);
                    escaped.Append(1);
                    builder.Dispose();
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1043", ids);
        Assert.Contains("NAM1044", ids);
    }

    [Fact]
    public async Task BorrowReturnIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static NativeBuilderBorrow<int> Return(
                    scoped ref NativeBuilderBorrow<int> borrow) =>
                    borrow;
            }
            """);

        Assert.Contains("NAM1043", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task BorrowCaptureIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    using NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Borrow(
                        (scoped ref NativeBuilderBorrow<int> borrow) =>
                        {
                            Action capture = () => _ = borrow.Count;
                            capture();
                        });
                    builder.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1043", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task OwnerUseAndNestedBorrowAreRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    using NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Borrow(
                        (scoped ref NativeBuilderBorrow<int> borrow) =>
                        {
                            builder.Append(1);
                            builder.Borrow(
                                static (scoped ref NativeBuilderBorrow<int> nested) =>
                                    nested.Append(2));
                        });
                    builder.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1044", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task IndirectBorrowCallbackIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    NativeBuilderBorrowAction<int> action =
                        static (scoped ref NativeBuilderBorrow<int> borrow) =>
                            borrow.Append(1);
                    using NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Borrow(action);
                    builder.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1044", NativeDiagnostics(diagnostics));
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
            using System;
            using Supprocom.NativeAllocationManagement;

            public sealed class Sample : IDisposable
            {
                private NativeTransfer<int>? _transfer;

                public void Build(NativePool<int> pool)
                {
                    using NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Append(31);
                    _transfer = builder.Complete();
                }

                public void Dispose() => _transfer?.Dispose();
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task CompletionFieldRequiresDeterministicDisposalPath()
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

        Assert.Contains("NAM1034", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task CompletionPropertyIsNotOwnershipAuthority()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public sealed class Sample
            {
                private NativeTransfer<int>? Transfer { get; set; }

                public void Build(NativePool<int> pool)
                {
                    using NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Append(31);
                    Transfer = builder.Complete();
                }
            }
            """);

        Assert.Contains("NAM1034", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ConditionalFieldCleanupDoesNotProvideAuthority()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public sealed class Sample : IDisposable
            {
                private readonly bool _cleanup;
                private NativeTransfer<int>? _transfer;

                public void Build(NativePool<int> pool)
                {
                    using NativeBuilder<int> builder = pool.CreateBuilder();
                    builder.Append(31);
                    _transfer = builder.Complete();
                }

                public void Dispose()
                {
                    if (_cleanup)
                    {
                        _transfer?.Dispose();
                    }
                }
            }
            """);

        Assert.Contains("NAM1034", NativeDiagnostics(diagnostics));
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

    [Fact]
    public async Task VoxelRenderOwnerAcceptsTwoBuilderCompletions()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using System.Threading.Channels;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public sealed class ChunkRender : IDisposable
            {
                private NativeTransfer<uint>? _opaque;
                private NativeTransfer<uint>? _transparent;

                public ChunkRender(
                    NativeTransfer<uint>? opaque,
                    NativeTransfer<uint>? transparent)
                {
                    try
                    {
                        _opaque = NativeTransfer<uint>.Move(ref opaque);
                        _transparent = NativeTransfer<uint>.Move(
                            ref transparent);
                    }
                    catch
                    {
                        try
                        {
                            _opaque?.Dispose();
                        }
                        finally
                        {
                            _transparent?.Dispose();
                        }

                        throw;
                    }
                    finally
                    {
                        opaque?.Dispose();
                        transparent?.Dispose();
                    }
                }

                public void Access()
                {
                    _opaque!.Access(static view => _ = view.Length);
                    _transparent!.Access(static view => _ = view.Length);
                }

                public void Dispose()
                {
                    _opaque?.Dispose();
                    _transparent?.Dispose();
                }
            }

            public static class Sample
            {
                public static async Task Run(NativePool<uint> pool)
                {
                    Channel<ChunkRender> channel =
                        Channel.CreateBounded<ChunkRender>(1);
                    using NativeBuilder<uint> opaque =
                        pool.CreateBuilder(preLease: 4);
                    using NativeBuilder<uint> transparent =
                        pool.CreateBuilder(preLease: 4);
                    opaque.Append(new uint[] { 1, 2, 3, 4 });
                    transparent.Append(new uint[] { 5, 6 });
                    await channel.Writer.WriteAsync(new ChunkRender(
                        opaque.Complete(),
                        transparent.Complete()));
                    ChunkRender render = await channel.Reader.ReadAsync();
                    render.Access();
                    render.Dispose();
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
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
