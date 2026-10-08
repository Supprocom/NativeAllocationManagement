using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeArenaFinalizerAnalyzerTests
{
    [Theory]
    [InlineData("GC.SuppressFinalize(owner);", "", 2)]
    [InlineData("Forward(owner);", "private static void Forward(NativeArena value) => GC.SuppressFinalize(value);", 3)]
    public async Task PublicOwnerFinalizerCannotBeSuppressedDirectlyOrThroughAHelper(string operation, string helper, int expected)
    {
        string source = $$"""
            using System;
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativeArena owner = new(8, NativeMemoryReturn.ToGarbageCollector);
                    {{operation}}
                }
                {{helper}}
            }
            """;
        Assert.DoesNotContain(AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(source);
        Assert.DoesNotContain(diagnostics, static diagnostic => string.Equals(diagnostic.Id, "AD0001", StringComparison.Ordinal));
        Assert.Equal(expected, AnalyzerContractTests.NativeDiagnostics(diagnostics).Count(static id => string.Equals(id, "NAM1001", StringComparison.Ordinal)));
    }
}
