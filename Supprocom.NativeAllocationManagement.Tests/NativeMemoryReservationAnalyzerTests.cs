using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeMemoryReservationAnalyzerTests
{
    [Fact]
    public async Task GuardedNullableActivationAndFinallyCleanupAreAccepted()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativeMemoryBudget budget = new(16);
                    if (!budget.TryReserve<int>(4, out NativeMemoryReservation<int>? permission, out _)) return;
                    try
                    {
                        permission.Value.PrepareBacking();
                        using NativeTransfer<int> owner = NativeMemoryReservation<int>.Activate(ref permission, static writer => writer.Fill(42));
                        _ = owner.Read(static view => view[0]);
                    }
                    finally { permission?.Dispose(); }
                }
            }
            """;
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Theory]
    [InlineData("budget.TryReserve<int>(1, out NativeMemoryReservation<int>? permission, out _); permission.Value.Dispose();", "NAM1050")]
    [InlineData("if (budget.TryReserve<int>(1, out NativeMemoryReservation<int>? permission, out _)) permission.Value.Dispose(); else permission.Value.Dispose();", "NAM1022")]
    [InlineData("if (!budget.TryReserve<int>(1, out NativeMemoryReservation<int>? permission, out _)) return;", "NAM1025")]
    [InlineData("if (!budget.TryReserve<int>(1, out NativeMemoryReservation<int>? permission, out var reason) || reason != NativeMemoryAdmissionExhaustionReason.None) return; permission.Value.Dispose();", "NAM1025")]
    public async Task RefusedUnguardedOrUnreleasedPermissionsAreRejected(string body, string expected)
    {
        string source = Wrap(body);
        AssertCompiles(source);
        Assert.Contains(expected, AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task MovingConsumesOnlyTheSourceAndRequiresDestinationCleanup()
    {
        string source = Wrap("""
            if (!budget.TryReserve<int>(1, out NativeMemoryReservation<int>? permission, out _)) return;
            using NativeMemoryReservation<int> moved = NativeMemoryReservation<int>.Move(ref permission);
            moved.PrepareBacking();
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
        string invalid = source.Replace("moved.PrepareBacking();", "permission.Value.PrepareBacking();", StringComparison.Ordinal);
        Assert.Contains("NAM1022", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(invalid)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task CopyAndDiscardCannotCreateOrDropProducerOwnership()
    {
        string source = Wrap("""
            if (!budget.TryReserve<int>(1, out NativeMemoryReservation<int>? permission, out _)) return;
            NativeMemoryReservation<int>? alias = permission;
            permission.Value.Dispose();
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1021", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
        string discarded = Wrap("budget.TryReserve<int>(1, out _, out _);");
        AssertCompiles(discarded);
        Assert.Contains("NAM1026", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(discarded)), StringComparer.Ordinal);
    }

    private static string Wrap(string body) => $$"""
        using Supprocom.NativeAllocationManagement;
        public static class Sample
        {
            public static void Run()
            {
                NativeMemoryBudget budget = new(16);
                {{body}}
            }
        }
        """;

    private static void AssertCompiles(string source) => Assert.DoesNotContain(
        AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
