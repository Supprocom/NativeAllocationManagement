using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class AnalyzerMessageTests
{
    private static async Task<string[]> MessagesAsync(string id, string body, string members = "", string types = "")
    {
        string source = $$"""
            using System;
            using Supprocom.NativeAllocationManagement;
            {{types}}
            public static class Sample
            {
                {{members}}
                public static void Run()
                {
                    {{body}}
                }
            }
            """;
        var diagnostics = await AnalyzerContractTests.AnalyzeAsync(source).ConfigureAwait(false);
        return diagnostics
            .Where(d => string.Equals(d.Id, id, StringComparison.Ordinal))
            .Select(d => d.GetMessage(CultureInfo.InvariantCulture))
            .ToArray();
    }

    [Fact]
    public async Task UnguardedAcquisitionNamesTheValueAndTheOperation()
    {
        string[] messages = await MessagesAsync("NAM1050",
            "NativeMemoryBudget budget = new(16); budget.TryReserve<int>(1, out NativeMemoryReservation<int>? permission, out _); permission.Value.Dispose();").ConfigureAwait(true);
        Assert.NotEmpty(messages);
        string message = messages[0];
        Assert.Contains("'permission'", message, StringComparison.Ordinal);
        Assert.DoesNotContain("{", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnguardedTryShareNamesTheValue()
    {
        string[] messages = await MessagesAsync("NAM1050", """
            NativeBuilder<int> builder = new(4);
            builder.Append(42);
            NativeTransfer<int>? source = builder.Complete();
            using NativeShared<int> shared = NativeShared<int>.Create(ref source, new(2, 1));
            shared.TryShare(out NativeShared<int> acquired, out _);
            acquired.Dispose();
            """).ConfigureAwait(true);
        Assert.NotEmpty(messages);
        Assert.All(messages, message =>
        {
            Assert.Contains("'acquired'", message, StringComparison.Ordinal);
            Assert.DoesNotContain("{", message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RegionLeaseReturnNamesTheExpressionAndTheReturnValue()
    {
        string[] messages = await MessagesAsync("NAM1012", "",
            "public static Local<int> Make() { using NativeRegion region = new(); return region.Lease<int>(4, 0); }").ConfigureAwait(true);
        Assert.NotEmpty(messages);
        string message = messages[0];
        Assert.Contains("region.Lease<int>(4, 0)", message, StringComparison.Ordinal);
        Assert.True(message.Contains("the return value", StringComparison.Ordinal), message);
        Assert.DoesNotContain("the new allocation", message, StringComparison.Ordinal);
        Assert.DoesNotContain("an escaping destination", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegionLeaseAssignedToFieldNamesTheField()
    {
        string[] messages = await MessagesAsync("NAM1012",
            "using NativeRegion region = new(); kept = region.Lease<int>(4, 0);",
            "private static Local<int> kept;").ConfigureAwait(true);
        Assert.NotEmpty(messages);
        string message = messages[0];
        Assert.Contains("'kept'", message, StringComparison.Ordinal);
        Assert.DoesNotContain("the new allocation", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("region.Lease<int>(4, 0);")]
    [InlineData("_ = region.Lease<int>(4, 0);")]
    public async Task DiscardedRegionLeaseIsNotAnEscape(string statement)
    {
        string[] messages = await MessagesAsync("NAM1012", $"using NativeRegion region = new(); {statement}").ConfigureAwait(true);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task DiscardedArenaScratchNamesTheExpressionAndTheDiscard()
    {
        string[] messages = await MessagesAsync("NAM1013", "using NativeArena arena = new(); arena.Scratch<int>(4, 0);").ConfigureAwait(true);
        Assert.NotEmpty(messages);
        string message = messages[0];
        Assert.Contains("arena.Scratch<int>(4, 0)", message, StringComparison.Ordinal);
        Assert.Contains("a discarded result", message, StringComparison.Ordinal);
        Assert.DoesNotContain("the new allocation", message, StringComparison.Ordinal);
        Assert.DoesNotContain("an escaping destination", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpressionBodiedOwnerCreationNamesTheOwnerAndTheReturn()
    {
        string[] messages = await MessagesAsync("NAM1001", "",
            "static NativePreparedPool<int> P() => new(new NativePoolPreparation(1, 1, 1), budget: null);").ConfigureAwait(true);
        Assert.NotEmpty(messages);
        string message = messages[0];
        Assert.Contains("NativePreparedPool<int>", message, StringComparison.Ordinal);
        Assert.True(message.Contains("the return value", StringComparison.Ordinal), message);
        Assert.DoesNotContain("{", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceParameterByValueIsDescribedWithoutTheRawRefKind()
    {
        string[] messages = await MessagesAsync("NAM1040", "", "static void Use(NativeWorkspace<int> workspace) { }").ConfigureAwait(true);
        Assert.NotEmpty(messages);
        string message = messages[0];
        Assert.Contains("'workspace'", message, StringComparison.Ordinal);
        Assert.Contains("by value", message, StringComparison.Ordinal);
        Assert.DoesNotContain("'None'", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NAM1027", "NativeTransfer<int>", "ref NativeTransfer<int> value")]
    [InlineData("NAM1033", "NativeBuilder<int>", "NativeBuilder<int> value")]
    [InlineData("NAM1040", "NativeWorkspace<int>", "NativeWorkspace<int> value")]
    public async Task ParameterRulesApplyInLocalFunctions(string id, string type, string parameter)
    {
        Assert.NotEmpty(type);
        string[] messages = await MessagesAsync(id, $"static void Helper({parameter}) {{ }}").ConfigureAwait(true);
        Assert.NotEmpty(messages);
    }

    [Fact]
    public async Task PrimitiveWriteStateCanBeReadInTheCallback()
    {
        string source = """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> owner = new(preLease: 4);
                    int value = 7;
                    owner.Write(1, in value, static (scoped NativeBuilderWriter<int> writer, scoped in int state) =>
                    {
                        writer.AsSpan()[0] = state;
                        writer.Commit(1);
                    });
                    NativeTransfer<int> transfer = owner.Complete();
                    transfer.Dispose();
                }
            }
            """;
        var diagnostics = await AnalyzerContractTests.AnalyzeAsync(source).ConfigureAwait(true);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }
}
