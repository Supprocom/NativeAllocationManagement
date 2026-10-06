namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class ReleaseDocumentationContractTests
{
    [Fact]
    public void EntryPointsSeparateReleasedInstallationFromUnreleasedSource()
    {
        string readme = Read("README.md");
        string guide = Read("docs", "getting-started.md");
        string status = Read("docs", "version-status.md");
        Assert.Contains("unreleased 0.3.0 development", readme, StringComparison.Ordinal);
        Assert.Contains("unreleased 0.3.0 development", guide, StringComparison.Ordinal);
        Assert.Contains("released `0.2.3` package", readme, StringComparison.Ordinal);
        Assert.Contains("--version 0.2.3", guide, StringComparison.Ordinal);
        Assert.Contains("Version=\"0.2.3\"", guide, StringComparison.Ordinal);
        Assert.Contains("not an accepted or published 0.3.0 package", status, StringComparison.Ordinal);
        Assert.Contains("do not publish development bytes", status, StringComparison.Ordinal);
        Assert.Contains("docs/version-status.md", readme, StringComparison.Ordinal);
        Assert.Contains("(version-status.md)", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationMatchesTheCurrentReadonlyValueRepresentation()
    {
        string transfer = Read("Supprocom.NativeAllocationManagement", "NativeTransfer.cs");
        string status = Read("docs", "version-status.md");
        Assert.Contains("public readonly struct NativeTransfer<T>", transfer, StringComparison.Ordinal);
        Assert.Contains("changes from a class in 0.2.3 to a readonly value capability", status, StringComparison.Ordinal);
        Assert.Contains("Rebuild consumers", status, StringComparison.Ordinal);
        Assert.Contains("not binary compatibility", status, StringComparison.Ordinal);
        Assert.Contains("Nullable<NativeTransfer<T>>", status, StringComparison.Ordinal);
        Assert.Contains("Presence is not proof of live ownership", status, StringComparison.Ordinal);
        Assert.Contains("serialization, reflection and generic constraints", status, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationDoesNotTurnCopiesDefaultsOrFinalizersIntoOwnership()
    {
        string status = Read("docs", "version-status.md");
        Assert.Contains("failure also consumes", status, StringComparison.Ordinal);
        Assert.Contains("Assignment does not acquire another owner", status, StringComparison.Ordinal);
        Assert.Contains("even `Dispose` rejects a default value", status, StringComparison.Ordinal);
        Assert.Contains("Emergency finalization is not timely", status, StringComparison.Ordinal);
        Assert.Contains("Sharing is\nnot imposed on ordinary unique", status, StringComparison.Ordinal);
        Assert.Contains("Weak observers do not own payload", status, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoricalPerformanceCannotMasqueradeAsCurrentAcceptance()
    {
        string readme = Read("README.md");
        string status = Read("docs", "version-status.md");
        Assert.Contains("Historical voxel runs", readme, StringComparison.Ordinal);
        Assert.Contains("historical, workload-specific estimates, not an accepted 0.3.0", readme, StringComparison.Ordinal);
        Assert.Contains("unfavorable", readme, StringComparison.Ordinal);
        Assert.Contains("failed or inconclusive required gates", status, StringComparison.Ordinal);
        Assert.Contains("not only the hot", status, StringComparison.Ordinal);
        Assert.Contains("essential feature cannot be renamed optional", status, StringComparison.Ordinal);
        Assert.Contains("maintained NAM baseline", status, StringComparison.Ordinal);
        Assert.Contains("Independent Review must accept the exact final boundary", status, StringComparison.Ordinal);
    }

    [Fact]
    public void BudgetAndTraceDomainsDoNotPromiseTotalProcessMemory()
    {
        string status = Read("docs", "version-status.md");
        Assert.Contains("does not bound process RSS, managed metadata", status, StringComparison.Ordinal);
        Assert.Contains("provider-owned external storage", status, StringComparison.Ordinal);
        Assert.Contains("Disabled tracing does not disable required counters", status, StringComparison.Ordinal);
        Assert.Contains("Unavailable\nobservations are not measured zero", status, StringComparison.Ordinal);
        Assert.Contains("zero requires a verified invariant", status, StringComparison.Ordinal);
        Assert.Contains("Both the accepted 0.2.3 source and current development target .NET 10", status, StringComparison.Ordinal);
        Assert.Contains("must publish and run on each required Windows/Linux and x64/ARM64 target", status, StringComparison.Ordinal);
    }

    [Fact]
    public void BudgetIntroductionLinksImplementedPreparationInsteadOfStaleMissingWork()
    {
        string budget = Read("docs", "memory-budgets.md");
        Assert.Contains("unreleased 0.3.0 development", budget, StringComparison.Ordinal);
        Assert.Contains("(prepared-pools.md)", budget, StringComparison.Ordinal);
        Assert.Contains("(prepared-arenas.md)", budget, StringComparison.Ordinal);
        Assert.Contains("(immutable-sharing.md)", budget, StringComparison.Ordinal);
        Assert.Contains("(application-admission.md)", budget, StringComparison.Ordinal);
        Assert.Contains("(typed-layouts.md)", budget, StringComparison.Ordinal);
        Assert.Contains("not accepted full-cost improvements", budget, StringComparison.Ordinal);
        Assert.DoesNotContain("shared-pointer controls are\nstill required", budget, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(RepositoryTestPaths.Root, "docs", "prepared-pools.md")));
        Assert.True(File.Exists(Path.Combine(RepositoryTestPaths.Root, "docs", "prepared-arenas.md")));
        Assert.True(File.Exists(Path.Combine(RepositoryTestPaths.Root, "docs", "immutable-sharing.md")));
        Assert.True(File.Exists(Path.Combine(RepositoryTestPaths.Root, "docs", "application-admission.md")));
        Assert.True(File.Exists(Path.Combine(RepositoryTestPaths.Root, "docs", "typed-layouts.md")));
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryTestPaths.Root, .. parts]));
}
