using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeArenaRetentionAnalyzerTests
{
    [Fact]
    public async Task MaintenancePreservesLiveScratchAuthority()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static int Run()
                {
                    using NativeArena arena = new(new NativeMemoryBudget(512), new NativeArenaRetentionPolicy(256, 0), 256, NativeMemoryReturn.ToNativeMemory);
                    ArenaLease<int> value = arena.Scratch<int>(1, static writer => writer.Write(42));
                    arena.MaintainRetention();
                    _ = arena.CaptureRetentionSnapshot();
                    return value.Read(static view => view[0]);
                }
            }
            """);
        Assert.True(AnalyzerContractTests.NativeDiagnostics(diagnostics).Length == 0, string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task EnteredCallbackCannotMaintainItsBackingOwner()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    using NativeArena arena = new(new NativeMemoryBudget(512), new NativeArenaRetentionPolicy(256, 0), 256, NativeMemoryReturn.ToNativeMemory);
                    ArenaLease<int> value = arena.Scratch<int>(1, static writer => writer.Write(42));
                    value.Access(view => { arena.MaintainRetention(); view[0] = 17; });
                }
            }
            """);
        Assert.Contains("NAM1009", AnalyzerContractTests.NativeDiagnostics(diagnostics), StringComparer.Ordinal);
    }

    [Fact]
    public async Task DisposedOwnerCannotRunPolicyMaintenance()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerContractTests.AnalyzeAsync(
            """
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run()
                {
                    NativeArena arena = new(new NativeMemoryBudget(512), new NativeArenaRetentionPolicy(256, 0), 256, NativeMemoryReturn.ToNativeMemory);
                    arena.Dispose();
                    arena.MaintainRetention();
                }
            }
            """);
        Assert.Contains("NAM1009", AnalyzerContractTests.NativeDiagnostics(diagnostics), StringComparer.Ordinal);
    }
}
