using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeOwnerIdentityAnalyzerTests
{
    [Fact]
    public async Task CanonicalImmutableIdentitiesRemainReadableAfterAuthorityEnds()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> builder = new(preLease: 1);
                    _ = builder.Id;
                    NativeTransfer<int> transfer = builder.Complete();
                    _ = builder.Id;
                    transfer.Dispose();
                    _ = transfer.Id;
                    using NativeBuilder<int> disposed = new(preLease: 1);
                    disposed.Dispose();
                    _ = disposed.Id;
                    using NativeWorkspace<int> workspace = new(preLease: 1);
                    workspace.Dispose();
                    _ = workspace.Id;
                }
            }
            """);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(diagnostics));
    }

    [Theory]
    [InlineData("builder", "Count", "NAM1029")]
    [InlineData("builder", "Capacity", "NAM1029")]
    [InlineData("transfer", "Length", "NAM1022")]
    [InlineData("transfer", "Capacity", "NAM1022")]
    [InlineData("workspace", "Length", "NAM1037")]
    [InlineData("workspace", "Capacity", "NAM1037")]
    public async Task IdentityExemptionDoesNotAuthorizeTerminalPayloadOrCapacity(
        string owner, string property, string expectedDiagnostic)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            $$"""
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> builder = new(preLease: 1);
                    NativeTransfer<int> transfer = builder.Complete();
                    transfer.Dispose();
                    using NativeWorkspace<int> workspace = new(preLease: 1);
                    workspace.Dispose();
                    _ = {{owner}}.Id;
                    _ = {{owner}}.{{property}};
                }
            }
            """);
        Assert.Contains(expectedDiagnostic, AnalyzerContractTests.NativeDiagnostics(diagnostics), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ReadingAnIdentityDoesNotAuthorizeAnOwnerAlias()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;

            public static class Sample
            {
                public static void Run()
                {
                    using NativeBuilder<int> builder = new(preLease: 1);
                    NativeBuilder<int> alias = builder;
                    _ = alias.Id;
                }
            }
            """);
        Assert.Contains("NAM1028", AnalyzerContractTests.NativeDiagnostics(diagnostics), StringComparer.Ordinal);
    }
}
