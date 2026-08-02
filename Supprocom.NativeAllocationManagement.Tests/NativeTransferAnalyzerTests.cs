using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeTransferAnalyzerTests
{
    [Fact]
    public async Task BoundedDirectSpanInitializationIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativeArena arena = new();
                    NativeTransfer<int> transfer =
                        arena.ScratchTransferable<int>(
                            32,
                            static writer => writer.InitializeRemaining(
                                static values =>
                                {
                                    for (int index = 0;
                                        index < values.Length;
                                        index++)
                                    {
                                        values[index] = index + 1;
                                    }
                                }));
                    _ = transfer.Read(static view => view[0]);
                    transfer.Dispose();
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task DestructiveMovesToAFieldAndBoundedChannelAreAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System.Threading.Channels;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public sealed class Holder
            {
                public NativeTransfer<int>? Transfer;
            }

            public static class Sample
            {
                public static async Task Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int>? first = pool.RentTransferable(
                        4,
                        static writer => writer.Fill(7));
                    Holder holder = new();
                    holder.Transfer = NativeTransfer<int>.Move(ref first);
                    holder.Transfer.Access(static view => view[0] = 11);
                    holder.Transfer.Dispose();

                    NativeTransfer<int>? second = pool.RentTransferable(
                        4,
                        static writer => writer.Fill(13));
                    Channel<NativeTransfer<int>> channel =
                        Channel.CreateBounded<NativeTransfer<int>>(1);
                    await channel.Writer.WriteAsync(
                        NativeTransfer<int>.Move(ref second));
                    NativeTransfer<int> received =
                        await channel.Reader.ReadAsync();
                    _ = received.Read(static view => view[0]);
                    received.Dispose();
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task SourceUseAfterMoveIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int>? source = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    NativeTransfer<int> destination =
                        NativeTransfer<int>.Move(ref source);
                    _ = source!.Length;
                    destination.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1022", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task DoubleMoveIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int>? source = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    NativeTransfer<int> destination =
                        NativeTransfer<int>.Move(ref source);
                    _ = NativeTransfer<int>.Move(ref source);
                    destination.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1023", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task DirectAliasCopyIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int> source = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    NativeTransfer<int> alias = source;
                    source.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1021", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task OldAliasUseAfterMoveIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int>? source = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    NativeTransfer<int> alias = source;
                    NativeTransfer<int> destination =
                        NativeTransfer<int>.Move(ref source);
                    alias.Access(static view => view[0] = 2);
                    destination.Dispose();
                }
            }
            """);

        string[] ids = AnalyzerContractTests.NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1021", ids);
        Assert.Contains("NAM1022", ids);
    }

    [Fact]
    public async Task DoubleDisposalIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int> transfer = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    transfer.Dispose();
                    transfer.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1022", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task UsingBoundTransferCannotMove()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    using NativeTransfer<int>? transfer = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    _ = NativeTransfer<int>.Move(ref transfer);
                }
            }
            """);

        Assert.Contains("NAM1023", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ChannelWriteMustReceiveAMoveExpression()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System.Threading.Channels;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int> transfer = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    Channel<NativeTransfer<int>> channel =
                        Channel.CreateBounded<NativeTransfer<int>>(1);
                    _ = channel.Writer.TryWrite(transfer);
                    transfer.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1021", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task FactoryCannotInitializeAFieldDirectly()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public sealed class Sample
            {
                private readonly NativePool<int> _pool = new();
                private NativeTransfer<int>? _transfer;

                public void Run()
                {
                    _transfer = _pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                }
            }
            """);

        Assert.Contains("NAM1026", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ActiveLocalTransferMustEnd()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int> transfer = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    _ = transfer.Length;
                }
            }
            """);

        Assert.Contains("NAM1025", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task UnscopedCallbackViewEscapeIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System.Diagnostics.CodeAnalysis;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int> transfer = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    transfer.Access(static view => Consume(view));
                    transfer.Dispose();
                }

                private static void Consume(
                    [UnscopedRef] NativeLeaseView<int> view)
                {
                    _ = view.Length;
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Contains("NAM1024"),
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task ScopedViewHelperIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int> transfer = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    transfer.Access(static view => Consume(view));
                    transfer.Dispose();
                }

                private static void Consume(scoped NativeLeaseView<int> view)
                {
                    _ = view.Length;
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task AmbiguousBranchMoveRejectsLaterUse()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(bool move)
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int>? source = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    NativeTransfer<int>? destination = null;
                    if (move)
                    {
                        destination = NativeTransfer<int>.Move(ref source);
                    }

                    _ = source!.Length;
                    destination?.Dispose();
                    source?.Dispose();
                }
            }
            """);

        Assert.Contains("NAM1022", AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task MoveReturnTransfersOwnershipWithoutEscapeDiagnostic()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static NativeTransfer<int> Create(
                    NativePool<int> pool)
                {
                    NativeTransfer<int>? source = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    return NativeTransfer<int>.Move(ref source);
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task ExpressionBodiedMoveReturnTransfersOwnership()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static NativeTransfer<int> Forward(
                    NativeTransfer<int>? source) =>
                    NativeTransfer<int>.Move(ref source);
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Theory]
    [InlineData("Field")]
    [InlineData("Property")]
    public async Task MoveToUntypedStorageIsRejected(string destination)
    {
        string member = destination == "Field"
            ? "public object? Value;"
            : "public object? Value { get; set; }";
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            $$"""
            using Supprocom.NativeAllocationManagement;

            public sealed class Holder
            {
                {{member}}
            }

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int>? source = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    Holder holder = new();
                    holder.Value = NativeTransfer<int>.Move(ref source);
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Theory]
    [InlineData("object")]
    [InlineData("dynamic")]
    public async Task MoveToUntypedReturnIsRejected(string returnType)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            $$"""
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static {{returnType}} Create(NativePool<int> pool)
                {
                    NativeTransfer<int>? source = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    return NativeTransfer<int>.Move(ref source);
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task MoveInsideTupleReturnIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static (NativeTransfer<int> Transfer, int Count) Create(
                    NativePool<int> pool)
                {
                    NativeTransfer<int>? source = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    return (NativeTransfer<int>.Move(ref source), 1);
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task MoveInsideAggregateReturnIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static object[] Create(NativePool<int> pool)
                {
                    NativeTransfer<int>? source = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    return new object[]
                    {
                        NativeTransfer<int>.Move(ref source)
                    };
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ExpressionBodiedObjectReceiverIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativeTransfer<int>? source) =>
                    Drop(NativeTransfer<int>.Move(ref source));

                private static void Drop(object value)
                {
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task GenericReceiverDoesNotGainOwnershipAfterSubstitution()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativeTransfer<int>? source)
                {
                    Drop(NativeTransfer<int>.Move(ref source));
                }

                private static void Drop<T>(T value)
                {
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task TaskAggregateDoesNotGainTransferOwnership()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativeTransfer<int>? source)
                {
                    _ = Task.FromResult(
                        NativeTransfer<int>.Move(ref source));
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task UnboundedChannelDoesNotGainTransferOwnership()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System.Threading.Channels;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static async Task Run(NativeTransfer<int>? source)
                {
                    Channel<NativeTransfer<int>> channel =
                        Channel.CreateUnbounded<NativeTransfer<int>>();
                    await channel.Writer.WriteAsync(
                        NativeTransfer<int>.Move(ref source));
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task UnprovenChannelParameterDoesNotGainTransferOwnership()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System.Threading.Channels;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static async Task Run(
                    Channel<NativeTransfer<int>> channel,
                    NativeTransfer<int>? source)
                {
                    await channel.Writer.WriteAsync(
                        NativeTransfer<int>.Move(ref source));
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task TryWriteDoesNotConsumeTransferOwnership()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System.Threading.Channels;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativeTransfer<int>? source)
                {
                    Channel<NativeTransfer<int>> channel =
                        Channel.CreateBounded<NativeTransfer<int>>(1);
                    _ = channel.Writer.TryWrite(
                        NativeTransfer<int>.Move(ref source));
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ReassignedChannelDoesNotKeepBoundedProvenance()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System.Threading.Channels;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static async Task Run(NativeTransfer<int>? source)
                {
                    Channel<NativeTransfer<int>> channel =
                        Channel.CreateBounded<NativeTransfer<int>>(1);
                    channel = Channel.CreateUnbounded<NativeTransfer<int>>();
                    await channel.Writer.WriteAsync(
                        NativeTransfer<int>.Move(ref source));
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ProvenBoundedChannelReceivesTransferOwnership()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System.Threading.Channels;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static async Task Run(NativeTransfer<int>? source)
                {
                    Channel<NativeTransfer<int>> channel =
                        Channel.CreateBounded<NativeTransfer<int>>(1);
                    await channel.Writer.WriteAsync(
                        NativeTransfer<int>.Move(ref source));
                    NativeTransfer<int> received =
                        await channel.Reader.ReadAsync();
                    received.Dispose();
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task GenericExactTransferDeclarationReceivesOwnership()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativeTransfer<int>? source)
                {
                    Consume(NativeTransfer<int>.Move(ref source));
                }

                private static void Consume<T>(NativeTransfer<T> transfer)
                    where T : unmanaged
                {
                    transfer.Dispose();
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task DirectMoveToDroppingReceiverIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int>? source = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    Drop(NativeTransfer<int>.Move(ref source));
                }

                private static void Drop(NativeTransfer<int> transfer)
                {
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task OwnedReceiverDisposalIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int>? source = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    Consume(NativeTransfer<int>.Move(ref source));
                }

                private static void Consume(NativeTransfer<int> transfer)
                {
                    transfer.Dispose();
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task OwnedReceiverDisposalInFinallyIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static int Consume(
                    NativeTransfer<int> transfer)
                {
                    try
                    {
                        return transfer.Read(
                            static view => view[0]);
                    }
                    finally
                    {
                        transfer.Dispose();
                    }
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task OwnedReceiverDisposalInFinallyWithoutReturnIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Consume(
                    NativeTransfer<int> transfer)
                {
                    try
                    {
                        transfer.Access(
                            static view => view[0] = 1);
                    }
                    finally
                    {
                        transfer.Dispose();
                    }
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task OwnedLambdaParameterDisposalIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    Action<NativeTransfer<int>> consume =
                        transfer => transfer.Dispose();
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task OwnedLambdaParameterMustEnd()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    Action<NativeTransfer<int>> drop =
                        transfer => { };
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task NestedCallbacksCanMoveAndConditionallyCleanTransferOwnership()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public sealed class Owner : IDisposable
            {
                private NativeTransfer<float>? _transfer;

                public Owner(NativeTransfer<float>? transfer)
                {
                    _transfer = NativeTransfer<float>.Move(ref transfer);
                }

                public void Dispose()
                {
                    _transfer?.Dispose();
                }
            }

            public static class Sample
            {
                public static void Run(
                    NativePool<float> pool,
                    Owner?[] owners)
                {
                    Parallel.For(0, owners.Length, index =>
                    {
                        NativeTransfer<float>? transfer =
                            pool.RentTransferable(
                                4,
                                static writer => writer.Fill(1));
                        try
                        {
                            owners[index] = new Owner(
                                NativeTransfer<float>.Move(ref transfer));
                        }
                        finally
                        {
                            transfer?.Dispose();
                        }
                    });

                    void Create(int index)
                    {
                        NativeTransfer<float>? transfer =
                            pool.RentTransferable(
                                4,
                                static writer => writer.Fill(2));
                        try
                        {
                            owners[index] = new Owner(
                                NativeTransfer<float>.Move(ref transfer));
                        }
                        finally
                        {
                            if (transfer is not null)
                            {
                                transfer.Dispose();
                            }
                        }
                    }

                    Parallel.For(0, owners.Length, Create);
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task ParallelCallbacksCanPublishConcurrentArenaTransfers()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public sealed class Owner : IDisposable
            {
                private NativeTransfer<float>? _transfer;

                public Owner(NativeTransfer<float>? transfer)
                {
                    _transfer = NativeTransfer<float>.Move(ref transfer);
                }

                public void Dispose()
                {
                    _transfer?.Dispose();
                }
            }

            public static class Sample
            {
                public static void Run(
                    NativeArena arena,
                    Owner?[] owners)
                {
                    Parallel.For(0, owners.Length, index =>
                    {
                        NativeTransfer<float>? transfer =
                            arena.ScratchTransferable<float>(
                                25_600,
                                writer => writer.Fill(index));
                        try
                        {
                            owners[index] = new Owner(
                                NativeTransfer<float>.Move(ref transfer));
                        }
                        finally
                        {
                            transfer?.Dispose();
                        }
                    });
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task ParallelArenaCallbackStillRequiresTransferCleanup()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativeArena arena)
                {
                    Parallel.For(0, 1, _ =>
                    {
                        NativeTransfer<float> transfer =
                            arena.ScratchTransferable<float>(
                                4,
                                static writer => writer.Fill(1));
                        _ = transfer.Length;
                    });
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task NamedHelperCanMoveAndConditionallyCleanTransferOwnership()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System;
            using Supprocom.NativeAllocationManagement;

            public sealed class Owner : IDisposable
            {
                private NativeTransfer<float>? _transfer;

                public Owner(NativeTransfer<float>? transfer)
                {
                    _transfer = NativeTransfer<float>.Move(ref transfer);
                }

                public void Dispose()
                {
                    _transfer?.Dispose();
                }
            }

            public static class Sample
            {
                public static void Create(
                    NativePool<float> pool,
                    Owner?[] owners,
                    int index)
                {
                    NativeTransfer<float>? transfer = pool.RentTransferable(
                        4,
                        static writer => writer.Fill(1));
                    try
                    {
                        owners[index] = new Owner(
                            NativeTransfer<float>.Move(ref transfer));
                    }
                    finally
                    {
                        transfer?.Dispose();
                    }
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task TaskCallbackCanMoveAndExplicitlyCleanTransferOwnership()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public sealed class Owner
            {
                private NativeTransfer<float>? _transfer;

                public Owner(NativeTransfer<float>? transfer)
                {
                    _transfer = NativeTransfer<float>.Move(ref transfer);
                }

                public void Dispose()
                {
                    _transfer?.Dispose();
                }
            }

            public static class Sample
            {
                public static void Run(NativePool<float> pool)
                {
                    Owner? owner = null;
                    Task.Run(() =>
                    {
                        NativeTransfer<float>? transfer =
                            pool.RentTransferable(
                                4,
                                static writer => writer.Fill(1));
                        try
                        {
                            owner = new Owner(
                                NativeTransfer<float>.Move(ref transfer));
                        }
                        finally
                        {
                            if (transfer != null)
                            {
                                transfer.Dispose();
                            }
                        }
                    }).GetAwaiter().GetResult();
                    owner?.Dispose();
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task NestedCallbackStillRejectsUseAfterMove()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<float> pool)
                {
                    Parallel.For(0, 1, _ =>
                    {
                        NativeTransfer<float>? source =
                            pool.RentTransferable(
                                4,
                                static writer => writer.Fill(1));
                        NativeTransfer<float> destination =
                            NativeTransfer<float>.Move(ref source);
                        source!.Access(static view => view[0] = 2);
                        destination.Dispose();
                    });
                }
            }
            """);

        Assert.Contains(
            "NAM1022",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task NestedCallbackStillRequiresFinalOwnershipCleanup()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<float> pool)
                {
                    Parallel.For(0, 1, _ =>
                    {
                        NativeTransfer<float> transfer =
                            pool.RentTransferable(
                                4,
                                static writer => writer.Fill(1));
                        _ = transfer.Length;
                    });
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task NamedHelperStillRejectsUseAfterMove()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<float> pool)
                {
                    NativeTransfer<float>? source = pool.RentTransferable(
                        4,
                        static writer => writer.Fill(1));
                    NativeTransfer<float> destination =
                        NativeTransfer<float>.Move(ref source);
                    source!.Access(static view => view[0] = 2);
                    destination.Dispose();
                }
            }
            """);

        Assert.Contains(
            "NAM1022",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task ParallelCallbackCannotCaptureTransferOwnership()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<float> pool)
                {
                    NativeTransfer<float>? source = pool.RentTransferable(
                        4,
                        static writer => writer.Fill(1));
                    Parallel.For(0, 1, _ =>
                    {
                        NativeTransfer<float> destination =
                            NativeTransfer<float>.Move(ref source);
                        destination.Dispose();
                    });
                    source?.Dispose();
                }
            }
            """);

        Assert.Contains(
            "NAM1022",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task RefLambdaTransferParameterIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public delegate void RefConsumer(
                ref NativeTransfer<int> transfer);

            public static class Sample
            {
                public static void Run()
                {
                    RefConsumer consume =
                        (ref NativeTransfer<int> transfer) => { };
                }
            }
            """);

        Assert.Contains(
            "NAM1027",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task OwnedReceiverMoveReturnIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int>? source = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    NativeTransfer<int> destination = Forward(
                        NativeTransfer<int>.Move(ref source));
                    destination.Dispose();
                }

                private static NativeTransfer<int> Forward(
                    NativeTransfer<int>? transfer)
                {
                    return NativeTransfer<int>.Move(ref transfer);
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task OwnedReceiverMustEndOnEveryPath()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Consume(
                    NativeTransfer<int> transfer,
                    bool drop)
                {
                    if (drop)
                    {
                        return;
                    }

                    transfer.Dispose();
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task MoveToUntypedReceiverIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int>? source = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    Drop(NativeTransfer<int>.Move(ref source));
                }

                private static void Drop(object value)
                {
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task DirectMoveToInReceiverIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int>? source = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(1));
                    Drop(NativeTransfer<int>.Move(ref source));
                }

                private static void Drop(
                    in NativeTransfer<int> transfer)
                {
                }
            }
            """);

        string[] ids = AnalyzerContractTests.NativeDiagnostics(diagnostics);
        Assert.Contains("NAM1027", ids);
        Assert.Contains("NAM1025", ids);
    }

    [Fact]
    public async Task InTransferParameterIsRejectedBeforeDisposal()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void DisposeBorrow(
                    in NativeTransfer<int> transfer)
                {
                    transfer.Dispose();
                }
            }
            """);

        Assert.Contains(
            "NAM1027",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task RefTransferParameterIsRejectedBeforeMove()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void MoveBorrow(
                    ref NativeTransfer<int>? transfer)
                {
                    _ = NativeTransfer<int>.Move(ref transfer);
                }
            }
            """);

        Assert.Contains(
            "NAM1027",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task RefTransferParameterIsRejectedBeforeDisposal()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void DisposeBorrow(
                    ref NativeTransfer<int> transfer)
                {
                    transfer.Dispose();
                }
            }
            """);

        Assert.Contains(
            "NAM1027",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task OutTransferParameterIsRejected()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Create(
                    out NativeTransfer<int>? transfer)
                {
                    transfer = null;
                }
            }
            """);

        Assert.Contains(
            "NAM1027",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Fact]
    public async Task AccessCallbackIsTheBorrowBoundary()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativePool<int> pool = new();
                    NativeTransfer<int> transfer = pool.RentTransferable(
                        1,
                        static writer => writer.Fill(7));
                    transfer.Access(
                        static view =>
                        {
                            view[0] = 11;
                            _ = view[0];
                        });
                    _ = transfer.Read(static view => view[0]);
                    transfer.Dispose();
                }
            }
            """);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task DisposalInFinallyInsideThreadCallbackIsAccepted()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using System.Threading;
            using System.Threading.Channels;
            using Supprocom.NativeAllocationManagement;

            Channel<NativeTransfer<int>> channel =
                Channel.CreateBounded<NativeTransfer<int>>(1);
            Thread receiver = new(
                () =>
                {
                    try
                    {
                        NativeTransfer<int> inbound = channel.Reader
                            .ReadAsync()
                            .AsTask()
                            .GetAwaiter()
                            .GetResult();
                        try
                        {
                            _ = inbound.Read(static view => view[0]);
                        }
                        finally
                        {
                            inbound.Dispose();
                        }
                    }
                    catch
                    {
                    }
                });
            receiver.Start();
            """,
            OutputKind.ConsoleApplication);

        Assert.True(
            AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0,
            string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task ActiveTransferDeclaredInTryIsRejectedAtTryExit()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run(NativePool<int> pool)
                {
                    try
                    {
                        NativeTransfer<int> transfer = pool.RentTransferable(
                            1,
                            static writer => writer.Fill(1));
                    }
                    catch
                    {
                    }
                }
            }
            """);

        Assert.Contains(
            "NAM1025",
            AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }
}
