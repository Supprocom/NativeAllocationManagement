using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeMemoryLifetimeAnalyzerTests
{
    [Theory]
    [InlineData("NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(16); Memory<byte> mem = buffer.AsMemory(); buffer.Dispose(); _ = mem.Length; mem.Span[0] = 1;")]
    [InlineData("NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(16); Memory<byte> mem = buffer.AsMemory(); buffer.Dispose(); Fill(mem);")]
    [InlineData("NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(16); Memory<byte> mem = buffer.AsMemory(); Memory<byte> part = mem.Slice(1, 4); buffer.Dispose(); Fill(part);")]
    [InlineData("NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(16); Memory<byte> mem = buffer.AsMemory(); Memory<byte> part = mem[2..]; buffer.Dispose(); Fill(part);")]
    [InlineData("NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(16); ReadOnlyMemory<byte> mem = buffer.AsMemory(); buffer.Dispose(); Consume(mem);")]
    [InlineData("NativeTransfer<byte>? source = NativeTransfer<byte>.CreateUninitialized(16); Memory<byte> mem = source.Value.AsMemory(); NativeTransfer<byte> moved = NativeTransfer<byte>.Move(ref source); Fill(mem); moved.Dispose();")]
    [InlineData("NativeTransfer<byte>? source = NativeTransfer<byte>.CreateUninitialized(16); Memory<byte> mem = source.Value.AsMemory(); NativeShared<byte> shared = NativeShared<byte>.Create(ref source, new NativeSharingPreparation(1, 0)); Fill(mem); shared.Dispose();")]
    [InlineData("NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(16); Memory<byte> mem = buffer.AsMemory(); using (buffer) { Fill(mem); } Fill(mem);")]
    [InlineData("NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(16); Memory<byte> mem = buffer.AsMemory(); if (condition) { buffer.Dispose(); } Fill(mem); if (!condition) { buffer.Dispose(); }")]
    public async Task MemoryUsedAfterTransferEndsIsRejected(string body)
    {
        string[] ids = await NativeIdsAsync(body);
        if (!ids.Contains("NAM1052", StringComparer.Ordinal))
        {
            Assert.Fail("NAM1052 was not reported for: " + body);
        }
    }

    [Fact]
    public async Task AwaitedReceiveIntoMemoryAfterDisposeIsRejected()
    {
        string[] ids = await AnalyzeAsync(
            """
            public static async Task Run(Stream stream)
            {
                NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(64 * 1024);
                Memory<byte> mem = buffer.AsMemory();
                buffer.Dispose();
                int read = await stream.ReadAsync(mem);
                _ = read;
            }
            """);

        Assert.Contains("NAM1052", ids, StringComparer.Ordinal);
    }

    [Fact]
    public async Task AwaitedReceiveAfterAnEarlierAwaitAndDisposeIsRejected()
    {
        string[] ids = await AnalyzeAsync(
            """
            public static async Task Run(Stream stream)
            {
                NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(64);
                Memory<byte> mem = buffer.AsMemory();
                _ = await stream.ReadAsync(mem);
                buffer.Dispose();
                await stream.WriteAsync(mem);
            }
            """);

        Assert.Contains("NAM1052", ids, StringComparer.Ordinal);
    }

    [Fact]
    public async Task MemoryAfterMoveInAnAsyncMethodIsRejected()
    {
        string[] ids = await AnalyzeAsync(
            """
            public static async Task Run(Stream stream)
            {
                NativeTransfer<byte>? source = NativeTransfer<byte>.CreateUninitialized(64);
                Memory<byte> mem = source.Value.AsMemory();
                using NativeTransfer<byte> moved = NativeTransfer<byte>.Move(ref source);
                _ = await stream.ReadAsync(mem);
            }
            """);

        Assert.Contains("NAM1052", ids, StringComparer.Ordinal);
    }

    [Fact]
    public async Task SharedMemoryAfterDisposeIsRejected()
    {
        string[] ids = await NativeIdsAsync(
            "NativeShared<byte> shared = default; ReadOnlyMemory<byte> mem = shared.AsMemory(); shared.Dispose(); Consume(mem);");

        Assert.Contains("NAM1052", ids, StringComparer.Ordinal);
    }

    [Fact]
    public async Task LeaseMemoryAfterDisposeIsRejected()
    {
        string[] ids = await NativeIdsAsync(
            "using NativeConcurrentPool<byte> pool = new(); ConcurrentPooled<byte> lease = pool.Rent(16, 0); Memory<byte> mem = lease.AsMemory(); lease.Dispose(); Fill(mem);");

        Assert.Contains("NAM1052", ids, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("using NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(16); Memory<byte> mem = buffer.AsMemory(); Fill(mem); _ = mem.Span.Length;")]
    [InlineData("NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(16); Memory<byte> mem = buffer.AsMemory(); Fill(mem); buffer.Dispose();")]
    [InlineData("NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(16); Memory<byte> mem = buffer.AsMemory(); Fill(mem); buffer.Dispose(); mem = default; _ = mem.Length;")]
    [InlineData("NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(16); buffer.Dispose(); NativeTransfer<byte> next = NativeTransfer<byte>.CreateUninitialized(16); Memory<byte> mem = next.AsMemory(); Fill(mem); next.Dispose();")]
    [InlineData("NativeTransfer<byte>? source = NativeTransfer<byte>.CreateUninitialized(16); NativeTransfer<byte> moved = NativeTransfer<byte>.Move(ref source); Memory<byte> mem = moved.AsMemory(); Fill(mem); moved.Dispose();")]
    [InlineData("for (int i = 0; i < 3; i++) { using NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(16); Memory<byte> mem = buffer.AsMemory(); Fill(mem); }")]
    [InlineData("NativeTransfer<byte> a = NativeTransfer<byte>.CreateUninitialized(16); NativeTransfer<byte> b = NativeTransfer<byte>.CreateUninitialized(16); Memory<byte> mem = b.AsMemory(); a.Dispose(); Fill(mem); b.Dispose();")]
    [InlineData("using NativeConcurrentPool<byte> pool = new(); using ConcurrentPooled<byte> lease = pool.Rent(16, 0); Memory<byte> mem = lease.AsMemory(); Fill(mem);")]
    public async Task MemoryUsedWhileItsOwnerIsCurrentIsAccepted(string body)
    {
        string[] ids = await NativeIdsAsync(body);
        Assert.DoesNotContain("NAM1052", ids, StringComparer.Ordinal);
    }

    [Fact]
    public async Task UsingDeclarationScopeAroundAwaitedReadIsAccepted()
    {
        string[] ids = await AnalyzeAsync(
            """
            public static async Task Run(Stream stream)
            {
                using NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(64 * 1024);
                Memory<byte> mem = buffer.AsMemory();
                int read = await stream.ReadAsync(mem);
                await stream.WriteAsync(mem[..read]);
            }
            """);

        Assert.DoesNotContain("NAM1052", ids, StringComparer.Ordinal);
    }

    [Fact]
    public async Task MemoryPassedToACallThatCompletesBeforeDisposeIsAccepted()
    {
        string[] ids = await AnalyzeAsync(
            """
            public static async Task Run(Stream stream)
            {
                NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(64);
                try
                {
                    Memory<byte> mem = buffer.AsMemory();
                    int read = await stream.ReadAsync(mem);
                    Fill(mem[..read]);
                }
                finally
                {
                    buffer.Dispose();
                }
            }
            """);

        Assert.DoesNotContain("NAM1052", ids, StringComparer.Ordinal);
    }

    [Fact]
    public async Task EachLoopIterationOwnsItsMemory()
    {
        string[] ids = await AnalyzeAsync(
            """
            public static async Task Run(Stream stream)
            {
                for (int i = 0; i < 3; i++)
                {
                    NativeTransfer<byte> buffer = NativeTransfer<byte>.CreateUninitialized(64);
                    Memory<byte> mem = buffer.AsMemory();
                    _ = await stream.ReadAsync(mem);
                    buffer.Dispose();
                }
            }
            """);

        Assert.DoesNotContain("NAM1052", ids, StringComparer.Ordinal);
    }

    private static Task<string[]> NativeIdsAsync(string body) =>
        AnalyzeAsync(
            $$"""
            public static void Run(bool condition)
            {
                {{body}}
            }
            """);

    private static async Task<string[]> AnalyzeAsync(string members)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            $$"""
            using System;
            using System.IO;
            using System.Threading.Tasks;
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                private static void Fill(Memory<byte> memory) => memory.Span.Clear();

                private static void Consume(ReadOnlyMemory<byte> memory) => _ = memory.Length;

                {{members}}
            }
            """).ConfigureAwait(true);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id.StartsWith("CS", StringComparison.Ordinal) && diagnostic.Severity == DiagnosticSeverity.Error);
        return AnalyzerContractTests.NativeDiagnostics(diagnostics);
    }
}
