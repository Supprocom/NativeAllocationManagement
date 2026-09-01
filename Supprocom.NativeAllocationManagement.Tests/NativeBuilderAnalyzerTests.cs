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
                    using NativeConcurrentPool<int> pool = new();
                    using NativeBuilder<int> builder =
                        new NativeBuilder<int>(preLease: 1);
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    using NativeBuilder<int> builder =
                        new NativeBuilder<int>(preLease: 4);
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    using NativeBuilder<int> builder =
                        new NativeBuilder<int>(preLease: 2);
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
                public static void Run(NativeConcurrentPool<uint> pool)
                {
                    using NativeBuilder<uint> builder =
                        new NativeBuilder<int>(preLease: 4);
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    NativeBuilderWriteAction<int> action =
                        static writer => writer.Commit(0);
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
                    builder.Write(0, action);
                    NativeTransfer<int> transfer = builder.Complete();
                    transfer.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1042", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task WrappedWriteCallbacksFailClosedAndAnalyzeBodies()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(
                    NativeConcurrentPool<int> pool,
                    bool first)
                {
                    NativeBuilderWriteAction<int> indirect =
                        static writer => writer.Commit(0);
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
                    builder.Write(
                        1,
                        Identity(
                            static writer =>
                            {
                                NativeBuilderWriter<int> alias = writer;
                                alias.Commit(0);
                            }));
                    builder.Write(
                        1,
                        Factory(
                            static writer =>
                            {
                                Retain(writer.AsSpan());
                                writer.Commit(0);
                            }));
                    builder.Write(1, Create());
                    builder.Write(
                        1,
                        first
                            ? static writer =>
                            {
                                NativeBuilderWriter<int> alias = writer;
                                alias.Commit(0);
                            }
                            : indirect);
                    builder.Dispose();
                }

                private static NativeBuilderWriteAction<int> Identity(
                    NativeBuilderWriteAction<int> action) => action;

                private static NativeBuilderWriteAction<int> Factory(
                    NativeBuilderWriteAction<int> action) =>
                    static writer => writer.Commit(0);

                private static NativeBuilderWriteAction<int> Create() =>
                    static writer => writer.Commit(0);

                private static void Retain(Span<int> values)
                {
                }
            }
            """);

        Assert.True(diagnostics.Count(diagnostic =>
            diagnostic.Id == "NAM1042"
            && diagnostic.GetMessage().Contains(
                "an indirect callback",
                StringComparison.Ordinal)) >= 4);
        Assert.True(diagnostics.Count(diagnostic =>
            diagnostic.Id == "NAM1042"
            && diagnostic.GetMessage().Contains(
                "an alias or helper",
                StringComparison.Ordinal)) >= 2);
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Id == "NAM1041");
    }

    [Fact]
    public async Task IndirectBorrowWriteCallbackIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    NativeBuilderWriteAction<int> action =
                        static writer => writer.Commit(0);
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
                    builder.Borrow(
                        (scoped ref NativeBuilderBorrow<int> borrow) =>
                            borrow.Write(0, action));
                    builder.Dispose();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    NativeBuilderBorrowAction<int> action =
                        static (scoped ref NativeBuilderBorrow<int> borrow) =>
                            borrow.Append(1);
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
                    builder.Borrow(action);
                    builder.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1044", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task WrappedBorrowCallbacksFailClosedAndAnalyzeBodies()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(
                    NativeConcurrentPool<int> pool,
                    bool first)
                {
                    NativeBuilderBorrow<int> escaped = default;
                    NativeBuilderBorrowAction<int> indirect =
                        static (scoped ref NativeBuilderBorrow<int> borrow) =>
                            borrow.Append(0);
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
                    builder.Borrow(
                        Identity(
                            (scoped ref NativeBuilderBorrow<int> borrow) =>
                                escaped = borrow));
                    builder.Borrow(
                        Factory(
                            (scoped ref NativeBuilderBorrow<int> borrow) =>
                                Forward(ref borrow)));
                    builder.Borrow(Create());
                    builder.Borrow(
                        first
                            ? new NativeBuilderBorrowAction<int>(Direct)
                            : indirect);
                    builder.Dispose();
                }

                private static NativeBuilderBorrowAction<int> Identity(
                    NativeBuilderBorrowAction<int> action) => action;

                private static NativeBuilderBorrowAction<int> Factory(
                    NativeBuilderBorrowAction<int> action) =>
                    static (scoped ref NativeBuilderBorrow<int> borrow) =>
                        borrow.Append(0);

                private static NativeBuilderBorrowAction<int> Create() =>
                    static (scoped ref NativeBuilderBorrow<int> borrow) =>
                        borrow.Append(0);

                private static void Direct(
                    scoped ref NativeBuilderBorrow<int> borrow) =>
                    Forward(ref borrow);

                private static void Forward(
                    ref NativeBuilderBorrow<int> borrow) =>
                    borrow.Append(0);
            }
            """);

        Assert.True(
            diagnostics.Count(diagnostic =>
                diagnostic.Id == "NAM1044"
                && diagnostic.GetMessage().Contains(
                    "an indirect callback",
                    StringComparison.Ordinal)) >= 4,
            string.Join(
                Environment.NewLine,
                diagnostics.Select(diagnostic =>
                    $"{diagnostic.Id}: {diagnostic.GetMessage()}")));
        Assert.True(diagnostics.Count(diagnostic =>
            diagnostic.Id == "NAM1043") >= 1);
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Id == "NAM1044"
                && diagnostic.GetMessage().Contains(
                    "an alias or unscoped helper",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectDelegateConstructionKeepsExactAuthority()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    using NativeBuilder<int> builder = new();
                    builder.Write(
                        1,
                        new NativeBuilderWriteAction<int>(
                            static writer =>
                            {
                                writer.AsSpan()[0] = 7;
                                writer.Commit(1);
                            }));
                    builder.Borrow(
                        new NativeBuilderBorrowAction<int>(
                            static (
                                scoped ref NativeBuilderBorrow<int> borrow) =>
                                borrow.Append(8)));
                    NativeTransfer<int> transfer = builder.Complete();
                    transfer.Dispose();
                }
            }
            """);

        Assert.DoesNotContain(
            diagnostics,
            diagnostic => diagnostic.Id is "NAM1041"
                or "NAM1042"
                or "NAM1043"
                or "NAM1044");
    }

    [Fact]
    public async Task LocalBuilderDisposalIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    NativeBuilder<int> builder = new NativeBuilder<int>();
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
                    NativeConcurrentPool<int> pool)
                {
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
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

                public void Build(NativeConcurrentPool<int> pool)
                {
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
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

                public void Build(NativeConcurrentPool<int> pool)
                {
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
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

                public void Build(NativeConcurrentPool<int> pool)
                {
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
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

                public void Build(NativeConcurrentPool<int> pool)
                {
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    NativeBuilder<int> builder = new NativeBuilder<int>();
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

                public void Run(NativeConcurrentPool<int> pool)
                {
                    _field = new NativeBuilder<int>();
                    Property = new NativeBuilder<int>();
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
                public static NativeBuilder<int> Return(NativeConcurrentPool<int> pool)
                {
                    return new NativeBuilder<int>();
                }

                public static void Discard(NativeConcurrentPool<int> pool)
                {
                    _ = new NativeBuilder<int>();
                    _ = (new NativeBuilder<int>(), 1);
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool, bool dispose)
                {
                    NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static void Run(NativeConcurrentPool<int> pool)
                {
                    NativeBuilder<int> builder = new NativeBuilder<int>();
                    builder = new NativeBuilder<int>();
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
                    NativeConcurrentPool<int> pool,
                    bool first)
                {
                    NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static async Task Run(NativeConcurrentPool<int> pool)
                {
                    Channel<NativeTransfer<int>> channel =
                        Channel.CreateBounded<NativeTransfer<int>>(1);
                    using NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static async Task Run(NativeConcurrentPool<int> pool)
                {
                    Channel<NativeTransfer<int>> channel =
                        Channel.CreateUnbounded<NativeTransfer<int>>();
                    NativeBuilder<int> builder = new NativeBuilder<int>();
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
                public static async Task Run(NativeConcurrentPool<uint> pool)
                {
                    Channel<ChunkRender> channel =
                        Channel.CreateBounded<ChunkRender>(1);
                    using NativeBuilder<uint> opaque =
                        new NativeBuilder<int>(preLease: 4);
                    using NativeBuilder<uint> transparent =
                        new NativeBuilder<int>(preLease: 4);
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

    [Fact]
    public async Task CompositeBorrowAcceptsTwoOwnersAndNestedHelpers()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<uint> opaque =
                        new NativeBuilder<uint>(preLease: 4);
                    using NativeBuilder<uint> transparent =
                        new NativeBuilder<uint>(preLease: 4);
                    opaque.Borrow(transparent, Emit);
                    NativeTransfer<uint> opaqueTransfer = opaque.Complete();
                    NativeTransfer<uint> transparentTransfer =
                        transparent.Complete();
                    opaqueTransfer.Dispose();
                    transparentTransfer.Dispose();
                }

                private static void Emit(
                    scoped ref NativeBuilderBorrow<uint> opaque,
                    scoped ref NativeBuilderBorrow<uint> transparent)
                {
                    EmitOpaque(ref opaque);
                    EmitTransparent(ref transparent);
                }

                private static void EmitOpaque(
                    scoped ref NativeBuilderBorrow<uint> opaque) =>
                    opaque.Append(1U);

                private static void EmitTransparent(
                    scoped ref NativeBuilderBorrow<uint> transparent) =>
                    transparent.Append(2U);
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task CompositeBorrowRejectsIndirectCallbackAuthority()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    NativeBuilderPairBorrowAction<int> action = Direct;
                    using NativeBuilder<int> first = new();
                    using NativeBuilder<int> second = new();
                    first.Borrow(second, Identity(action));
                    first.Dispose();
                    second.Dispose();
                }

                private static NativeBuilderPairBorrowAction<int> Identity(
                    NativeBuilderPairBorrowAction<int> action) => action;

                private static void Direct(
                    scoped ref NativeBuilderBorrow<int> first,
                    scoped ref NativeBuilderBorrow<int> second)
                {
                    first.Append(1);
                    second.Append(2);
                }
            }
            """);

        Assert.Contains("NAM1044", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task CompositeBorrowRejectsEscapeCaptureAndOwnerUse()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> first = new();
                    using NativeBuilder<int> second = new();
                    NativeBuilderBorrow<int> escaped = default;
                    first.Borrow(
                        second,
                        (
                            scoped ref NativeBuilderBorrow<int> firstBorrow,
                            scoped ref NativeBuilderBorrow<int> secondBorrow) =>
                        {
                            escaped = firstBorrow;
                            Action capture = () => _ = secondBorrow.Count;
                            capture();
                            first.Append(1);
                            second.Dispose();
                            first.Borrow(
                                static (
                                    scoped ref NativeBuilderBorrow<int> nested) =>
                                    nested.Append(2));
                        });
                    first.Dispose();
                    second.Dispose();
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1043", ids);
        Assert.Contains("NAM1044", ids);
    }

    [Fact]
    public async Task StateWriteAcceptsStaticCallbacksAndNestedHelpers()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public readonly record struct WriteState(int Start, int Count);

            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> owner = new(preLease: 4);
                    WriteState first = new(3, 2);
                    owner.Write(first.Count, in first, Write);
                    WriteState second = new(7, 2);
                    owner.Borrow(
                        in second,
                        static (
                            scoped ref NativeBuilderBorrow<int> borrow,
                            scoped in WriteState state) =>
                            borrow.Write(
                                state.Count,
                                in state,
                                static (
                                    scoped NativeBuilderWriter<int> writer,
                                    scoped in WriteState writeState) =>
                                    WriteNested(
                                        ref writer,
                                        in writeState)));
                    NativeTransfer<int> transfer = owner.Complete();
                    transfer.Dispose();
                }

                private static void Write(
                    scoped NativeBuilderWriter<int> writer,
                    scoped in WriteState state) =>
                    WriteNested(ref writer, in state);

                private static void WriteNested(
                    scoped ref NativeBuilderWriter<int> writer,
                    scoped in WriteState state)
                {
                    for (int index = 0; index < state.Count; index++)
                    {
                        writer.AsSpan()[index] = state.Start + index;
                    }

                    writer.Commit(state.Count);
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task StateWriteRejectsCapturingAndIndirectCallbacks()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public readonly record struct WriteState(int Value);

            public static class Sample
            {
                public static void Run(bool useFirst)
                {
                    using NativeBuilder<int> builder = new(preLease: 2);
                    WriteState state = new(5);
                    int captured = 7;
                    builder.Write(
                        1,
                        in state,
                        (
                            scoped NativeBuilderWriter<int> writer,
                            scoped in WriteState callbackState) =>
                        {
                            writer.AsSpan()[0] =
                                callbackState.Value + captured;
                            writer.Commit(1);
                        });
                    NativeBuilderWriteStateAction<int, WriteState> stored =
                        Direct;
                    builder.Write(1, in state, stored);
                    builder.Write(
                        1,
                        in state,
                        Identity(Direct));
                    builder.Dispose();
                }

                private static NativeBuilderWriteStateAction<int, WriteState>
                    Identity(
                        NativeBuilderWriteStateAction<int, WriteState> action) =>
                    action;

                private static void Direct(
                    scoped NativeBuilderWriter<int> writer,
                    scoped in WriteState state)
                {
                    writer.AsSpan()[0] = state.Value;
                    writer.Commit(1);
                }
            }
            """);

        Assert.True(
            NativeDiagnostics(diagnostics)
                .Count(id => id == "NAM1046") >= 3,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task StateWriteRejectsEscapeBoxingAndUnscopedForwarding()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public readonly record struct WriteState(int Value);

            public static class Sample
            {
                private static WriteState _stored;

                public static void Run()
                {
                    using NativeBuilder<int> builder = new(preLease: 2);
                    WriteState state = new(11);
                    builder.Write(1, in state, Escape);
                    builder.Dispose();
                }

                private static void Escape(
                    scoped NativeBuilderWriter<int> writer,
                    scoped in WriteState state)
                {
                    WriteState alias = state;
                    _stored = state;
                    object boxed = state;
                    Forward(state);
                    Action capture = () => Console.WriteLine(state.Value);
                    capture();
                    writer.Commit(0);
                }

                private static void Forward(WriteState state)
                {
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1045", ids);
        Assert.Contains("NAM1046", ids);
    }

    [Fact]
    public async Task StateWriteRejectsOwnershipBearingState()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public readonly record struct OwnerState(
                NativeBuilder<int> Builder,
                int Value);

            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> builder = new(preLease: 2);
                    OwnerState state = new(builder, 13);
                    builder.Write(1, in state, Write);
                    builder.Dispose();
                }

                private static void Write(
                    scoped NativeBuilderWriter<int> writer,
                    scoped in OwnerState state)
                {
                    writer.AsSpan()[0] = state.Value;
                    writer.Commit(1);
                }
            }
            """);

        Assert.Contains("NAM1046", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task CompileTimeStateWriteAcceptsOwnerAndBorrowedWrites()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public readonly record struct WriteState(int Start, int Count);

            public readonly struct WriteAction
                : INativeBuilderWriteAction<int, WriteState>
            {
                public static void Invoke(
                    scoped NativeBuilderWriter<int> writer,
                    scoped in WriteState state) =>
                    WriteNested(ref writer, in state);

                private static void WriteNested(
                    scoped ref NativeBuilderWriter<int> writer,
                    scoped in WriteState state)
                {
                    for (int index = 0; index < state.Count; index++)
                    {
                        writer.AsSpan()[index] = state.Start + index;
                    }

                    writer.Commit(state.Count);
                }
            }

            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> builder = new(preLease: 4);
                    WriteState first = new(3, 2);
                    builder.Write<WriteState, WriteAction>(
                        first.Count,
                        in first);
                    WriteState second = new(7, 2);
                    builder.Borrow(
                        in second,
                        static (
                            scoped ref NativeBuilderBorrow<int> borrow,
                            scoped in WriteState state) =>
                            borrow.Write<WriteState, WriteAction>(
                                state.Count,
                                in state));
                    NativeTransfer<int> transfer = builder.Complete();
                    transfer.Dispose();
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task CompileTimeStateWriteRejectsEscapedAuthorityAndState()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public readonly record struct WriteState(int Value);

            public readonly struct InvalidAction
                : INativeBuilderWriteAction<int, WriteState>
            {
                private static WriteState _stored;

                public static void Invoke(
                    scoped NativeBuilderWriter<int> writer,
                    scoped in WriteState state)
                {
                    NativeBuilderWriter<int> alias = writer;
                    Span<int> view = writer.AsSpan();
                    Store(view);
                    _stored = state;
                    object boxed = state;
                    alias.Commit(0);
                    _ = boxed;
                }

                private static void Store(Span<int> values)
                {
                }
            }

            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> builder = new(preLease: 1);
                    WriteState state = new(5);
                    builder.Write<WriteState, InvalidAction>(1, in state);
                    builder.Dispose();
                }
            }
            """);

        string[] ids = NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1041", ids);
        Assert.Contains("NAM1042", ids);
        Assert.Contains("NAM1045", ids);
        Assert.Contains("NAM1046", ids);
    }

    [Fact]
    public async Task CompileTimeStateWriteRejectsOwnerStateAndMissingAction()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public readonly record struct OwnerState(
                NativeBuilder<int> Builder,
                int Value);

            public readonly struct OwnerAction
                : INativeBuilderWriteAction<int, OwnerState>
            {
                public static void Invoke(
                    scoped NativeBuilderWriter<int> writer,
                    scoped in OwnerState state)
                {
                    writer.AsSpan()[0] = state.Value;
                    writer.Commit(1);
                }
            }

            public readonly struct MissingAction
                : INativeBuilderWriteAction<int, int>
            {
            }

            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> builder = new(preLease: 1);
                    OwnerState state = new(builder, 5);
                    builder.Write<OwnerState, OwnerAction>(1, in state);
                    int value = 7;
                    builder.Write<int, MissingAction>(1, in value);
                    builder.Dispose();
                }
            }
            """);

        Assert.True(
            NativeDiagnostics(diagnostics)
                .Count(id => id == "NAM1046") >= 2,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task CompileTimeStateWriteRejectsAsyncOrIteratorAction()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public readonly struct AsyncAction
                : INativeBuilderWriteAction<int, int>
            {
                public static async void Invoke(
                    scoped NativeBuilderWriter<int> writer,
                    scoped in int state)
                {
                    await Task.Yield();
                    writer.Commit(0);
                }
            }

            public readonly struct IteratorAction
                : INativeBuilderWriteAction<int, int>
            {
                public static IEnumerable<int> Invoke(
                    scoped NativeBuilderWriter<int> writer,
                    scoped in int state)
                {
                    writer.Commit(0);
                    yield return state;
                }
            }

            public static class Sample
            {
                public static void Run(bool useAsync)
                {
                    using NativeBuilder<int> builder = new(preLease: 1);
                    int state = 0;
                    if (useAsync)
                    {
                        builder.Write<int, AsyncAction>(0, in state);
                    }
                    else
                    {
                        builder.Write<int, IteratorAction>(0, in state);
                    }

                    builder.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1046", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task CallbackLocalByteAdapterCanUseMemoryMarshalWrite()
    {
        const string Source =
            """
            using System;
            using System.Runtime.InteropServices;
            using Supprocom.NativeAllocationManagement;

            public readonly record struct State(long Value);

            public ref struct Adapter
            {
                private Span<byte> _bytes;

                public Adapter(Span<byte> bytes)
                {
                    _bytes = bytes;
                }

                public void Write(scoped in State value) =>
                    MemoryMarshal.Write(_bytes, in value);
            }

            public static class Sample
            {
                public static void Run()
                {
                    const int ByteCount = sizeof(long);
                    using NativeBuilder<byte> builder = new(
                        preLease: ByteCount);
                    State state = new(17);
                    builder.Write(
                        ByteCount,
                        in state,
                        static (
                            scoped NativeBuilderWriter<byte> writer,
                            scoped in State current) =>
                        {
                            Adapter adapter = new(writer.AsSpan());
                            adapter.Write(in current);
                            writer.Commit(ByteCount);
                        });
                    NativeTransfer<byte> transfer = builder.Complete();
                    transfer.Dispose();
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source);

        Assert.DoesNotContain(
            AnalyzerContractTests.Compile(Source),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task MemoryMarshalWriteAuthorityRejectsIndirection()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using System.Runtime.InteropServices;
            using Supprocom.NativeAllocationManagement;

            public readonly record struct State(long Value);

            public delegate void StoredWrite(
                Span<byte> bytes,
                in State value);

            public ref struct HelperAdapter
            {
                private Span<byte> _bytes;

                public HelperAdapter(Span<byte> bytes)
                {
                    _bytes = bytes;
                }

                public void Write(scoped in State value) =>
                    Forward(_bytes, in value);

                private static void Forward(
                    Span<byte> bytes,
                    in State value) =>
                    MemoryMarshal.Write(bytes, in value);
            }

            public ref struct DelegateAdapter
            {
                private Span<byte> _bytes;

                public DelegateAdapter(Span<byte> bytes)
                {
                    _bytes = bytes;
                }

                public void Write(scoped in State value)
                {
                    StoredWrite action = MemoryMarshal.Write;
                    action(_bytes, in value);
                }
            }

            public ref struct ImpostorAdapter
            {
                private Span<byte> _bytes;

                public ImpostorAdapter(Span<byte> bytes)
                {
                    _bytes = bytes;
                }

                public void Write(scoped in State value) =>
                    Impostor.Write(_bytes, in value);
            }

            public static class Impostor
            {
                public static void Write<T>(
                    Span<byte> bytes,
                    in T value)
                    where T : struct
                {
                }
            }

            public static class Sample
            {
                public static void Run()
                {
                    const int ByteCount = sizeof(long);
                    State state = new(17);

                    using NativeBuilder<byte> first = new(
                        preLease: ByteCount);
                    first.Write(
                        ByteCount,
                        in state,
                        static (
                            scoped NativeBuilderWriter<byte> writer,
                            scoped in State current) =>
                        {
                            HelperAdapter adapter = new(writer.AsSpan());
                            adapter.Write(in current);
                            writer.Commit(ByteCount);
                        });

                    using NativeBuilder<byte> second = new(
                        preLease: ByteCount);
                    second.Write(
                        ByteCount,
                        in state,
                        static (
                            scoped NativeBuilderWriter<byte> writer,
                            scoped in State current) =>
                        {
                            DelegateAdapter adapter = new(writer.AsSpan());
                            adapter.Write(in current);
                            writer.Commit(ByteCount);
                        });

                    using NativeBuilder<byte> third = new(
                        preLease: ByteCount);
                    third.Write(
                        ByteCount,
                        in state,
                        static (
                            scoped NativeBuilderWriter<byte> writer,
                            scoped in State current) =>
                        {
                            ImpostorAdapter adapter = new(writer.AsSpan());
                            adapter.Write(in current);
                            writer.Commit(ByteCount);
                        });
                }
            }
            """);

        Assert.True(
            NativeDiagnostics(diagnostics)
                .Count(id => id == "NAM1041") >= 3,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task MemoryMarshalWriteDoesNotAuthorizeAdapterEscape()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using System.Runtime.InteropServices;
            using Supprocom.NativeAllocationManagement;

            public readonly record struct State(long Value);

            public ref struct EscapingAdapter
            {
                private Span<byte> _bytes;

                public EscapingAdapter(Span<byte> bytes)
                {
                    _bytes = bytes;
                }

                public void Write(scoped in State value) =>
                    MemoryMarshal.Write(_bytes, in value);

                public Span<byte> Expose() => _bytes;
            }

            public static class Sample
            {
                public static void Run()
                {
                    const int ByteCount = sizeof(long);
                    using NativeBuilder<byte> builder = new(
                        preLease: ByteCount);
                    State state = new(17);
                    builder.Write(
                        ByteCount,
                        in state,
                        static (
                            scoped NativeBuilderWriter<byte> writer,
                            scoped in State current) =>
                        {
                            EscapingAdapter adapter = new(
                                writer.AsSpan());
                            adapter.Write(in current);
                            Retain(adapter.Expose());
                            writer.Commit(ByteCount);
                        });
                }

                private static void Retain(Span<byte> bytes)
                {
                }
            }
            """);

        Assert.Contains("NAM1041", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task MemoryMarshalAdapterRejectsBoxingAndAsyncUse()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using System.Runtime.InteropServices;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public readonly record struct State(long Value);

            public ref struct BoxingAdapter
            {
                private Span<byte> _bytes;

                public BoxingAdapter(Span<byte> bytes)
                {
                    _bytes = bytes;
                }

                public void Write(scoped in State value) =>
                    MemoryMarshal.Write(_bytes, in value);
            }

            public ref struct AsyncAdapter
            {
                private Span<byte> _bytes;

                public AsyncAdapter(Span<byte> bytes)
                {
                    _bytes = bytes;
                }

                public async void Write(scoped in State value)
                {
                    await Task.Yield();
                    MemoryMarshal.Write(_bytes, in value);
                }
            }

            public static class Sample
            {
                public static void Run()
                {
                    const int ByteCount = sizeof(long);
                    State state = new(17);

                    using NativeBuilder<byte> first = new(
                        preLease: ByteCount);
                    first.Write(
                        ByteCount,
                        in state,
                        static (
                            scoped NativeBuilderWriter<byte> writer,
                            scoped in State current) =>
                        {
                            BoxingAdapter adapter = new(writer.AsSpan());
                            object boxed = adapter;
                            _ = boxed;
                            adapter.Write(in current);
                            writer.Commit(ByteCount);
                        });

                    using NativeBuilder<byte> second = new(
                        preLease: ByteCount);
                    second.Write(
                        ByteCount,
                        in state,
                        static (
                            scoped NativeBuilderWriter<byte> writer,
                            scoped in State current) =>
                        {
                            AsyncAdapter adapter = new(writer.AsSpan());
                            adapter.Write(in current);
                            writer.Commit(ByteCount);
                        });
                }
            }
            """);

        Assert.True(
            NativeDiagnostics(diagnostics)
                .Count(id => id == "NAM1041") >= 2,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task CallbackLocalSpanAdapterAndScopedHelpersAreAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public ref struct RectangleWriter
            {
                private Span<int> _values;

                public RectangleWriter(Span<int> values)
                {
                    _values = values;
                }

                public void Set(int index, int value) =>
                    _values[index] = value;

                public int Get(int index) => _values[index];
            }

            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> builder = new(preLease: 4);
                    builder.Write(
                        4,
                        static writer =>
                        {
                            RectangleWriter adapter = new(
                                writer.AsSpan());
                            Fill(ref adapter);
                            Verify(in adapter);
                            writer.Commit(4);
                        });
                    NativeTransfer<int> transfer = builder.Complete();
                    transfer.Dispose();
                }

                private static void Fill(
                    scoped ref RectangleWriter adapter)
                {
                    for (int index = 0; index < 4; index++)
                    {
                        adapter.Set(index, index + 1);
                    }
                }

                private static void Verify(
                    scoped in RectangleWriter adapter)
                {
                    _ = adapter.Get(0);
                }
            }
            """);

        AssertNoNativeDiagnostics(diagnostics);
    }

    [Fact]
    public async Task SpanAdapterRejectsConstructorSideEscape()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public ref struct EscapingWriter
            {
                private Span<int> _values;

                public EscapingWriter(Span<int> values)
                {
                    Inspect(values);
                    _values = values;
                }

                private static void Inspect(Span<int> values)
                {
                }
            }

            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> builder = new(preLease: 1);
                    builder.Write(
                        1,
                        static writer =>
                        {
                            EscapingWriter adapter = new(
                                writer.AsSpan());
                            writer.Commit(0);
                        });
                    builder.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1041", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task SpanAdapterRejectsMemberViewEscape()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public ref struct EscapingWriter
            {
                private Span<int> _values;

                public EscapingWriter(Span<int> values)
                {
                    _values = values;
                }

                public Span<int> Expose() => _values;

                public void Forward() => Store(_values);

                private static void Store(Span<int> values)
                {
                }
            }

            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> builder = new(preLease: 1);
                    builder.Write(
                        1,
                        static writer =>
                        {
                            EscapingWriter adapter = new(
                                writer.AsSpan());
                            adapter.Forward();
                            writer.Commit(0);
                        });
                    builder.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1041", NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task SpanAdapterRejectsAliasesCaptureAndUnscopedForwarding()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public ref struct RectangleWriter
            {
                private Span<int> _values;

                public RectangleWriter(Span<int> values)
                {
                    _values = values;
                }

                public void Set(int index, int value) =>
                    _values[index] = value;
            }

            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> builder = new(preLease: 1);
                    builder.Write(
                        1,
                        static writer =>
                        {
                            RectangleWriter adapter = new(
                                writer.AsSpan());
                            RectangleWriter alias = adapter;
                            Forward(adapter);
                            Action capture = () => adapter.Set(0, 1);
                            capture();
                            alias.Set(0, 2);
                            writer.Commit(1);
                        });
                    builder.Dispose();
                }

                private static void Forward(RectangleWriter adapter)
                {
                }
            }
            """);

        Assert.True(
            NativeDiagnostics(diagnostics)
                .Count(id => id == "NAM1041") >= 3,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task SpanAdapterRejectsNonlocalConstructionAndReturn()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public ref struct RectangleWriter
            {
                private Span<int> _values;

                public RectangleWriter(Span<int> values)
                {
                    _values = values;
                }
            }

            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> builder = new(preLease: 1);
                    RectangleWriter outer = default;
                    builder.Write(
                        1,
                        writer =>
                        {
                            outer = new RectangleWriter(writer.AsSpan());
                            writer.Commit(0);
                        });
                    builder.Dispose();
                }

                private static RectangleWriter Return(
                    scoped NativeBuilderWriter<int> writer) =>
                    new(writer.AsSpan());
            }
            """);

        Assert.Contains("NAM1041", NativeDiagnostics(diagnostics));
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
