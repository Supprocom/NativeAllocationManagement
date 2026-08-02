using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeTransferAnalyzerTests
{
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
}
