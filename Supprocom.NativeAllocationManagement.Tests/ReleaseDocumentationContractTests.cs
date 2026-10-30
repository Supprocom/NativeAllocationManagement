namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class ReleaseDocumentationContractTests
{
    [Fact]
    public void EntryPointsInstallTheReleasedPackage()
    {
        string readme = Read("README.md");
        string guide = Read("docs", "getting-started.md");
        Assert.DoesNotContain("unreleased", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unreleased", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("released `0.3.1` package", readme, StringComparison.Ordinal);
        Assert.Contains("released `0.3.1` package", guide, StringComparison.Ordinal);
        Assert.Contains("--version 0.3.1", guide, StringComparison.Ordinal);
        Assert.Contains("Version=\"0.3.1\"", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("version-status.md", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("version-status.md", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationMatchesTheCurrentReadonlyValueRepresentation()
    {
        string transfer = Read("Supprocom.NativeAllocationManagement", "NativeTransfer.cs");
        string guide = Read("docs", "getting-started.md");
        Assert.Contains("public readonly struct NativeTransfer<T>", transfer, StringComparison.Ordinal);
        Assert.Contains("not binary compatibility", guide, StringComparison.Ordinal);
        Assert.Contains("Rebuild consumers", guide, StringComparison.Ordinal);
        Assert.Contains("A nullable binding uses `binding.Value` after checking", guide, StringComparison.Ordinal);
        Assert.Contains("Copies of the previous value cannot access or", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationDoesNotTurnCopiesDefaultsOrFinalizersIntoOwnership()
    {
        string guide = Read("docs", "getting-started.md");
        Assert.Contains("A failed move consumes its source binding", guide, StringComparison.Ordinal);
        Assert.Contains("they do not create another owner", guide, StringComparison.Ordinal);
        Assert.Contains("uninitialized and reject use, including disposal", guide, StringComparison.Ordinal);
        Assert.Contains("Never use finalization as timely budget", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void PerformanceDocumentationExplainsWorkloadsAndRunnableComparisons()
    {
        string readme = Read("README.md");
        string benchmark = Read("docs", "builder-benchmark.md");
        Assert.Contains("Earlier runs of the voxel demo", readme, StringComparison.Ordinal);
        Assert.Contains("Results depend on workload", readme, StringComparison.Ordinal);
        Assert.Contains("total managed/native allocation", readme, StringComparison.Ordinal);
        Assert.Contains("docs/builder-benchmark.md", readme, StringComparison.Ordinal);
        Assert.Contains("ManagedExactArray", benchmark, StringComparison.Ordinal);
        Assert.Contains("GC allocation\nplus successful requested backing bytes", benchmark, StringComparison.Ordinal);
    }

    [Fact]
    public void BudgetAndTraceDomainsDoNotPromiseTotalProcessMemory()
    {
        string budget = Read("docs", "memory-budgets.md");
        string accounting = Read("docs", "production-accounting-contract.md");
        Assert.Contains("does not claim to bound process RSS", budget, StringComparison.Ordinal);
        Assert.Contains("external storage owned by another provider", budget, StringComparison.Ordinal);
        Assert.Contains("Borrowed storage and managed metadata require separate accounting", budget, StringComparison.Ordinal);
        Assert.Contains("Accounting is active in ordinary runtime operation, without enabling tests or\ntracing", accounting, StringComparison.Ordinal);
    }

    [Fact]
    public void BudgetIntroductionLinksImplementedPreparationInsteadOfStaleMissingWork()
    {
        string budget = Read("docs", "memory-budgets.md");
        Assert.Contains("available from version 0.3.0", budget, StringComparison.Ordinal);
        Assert.Contains("(prepared-pools.md)", budget, StringComparison.Ordinal);
        Assert.Contains("(prepared-arenas.md)", budget, StringComparison.Ordinal);
        Assert.Contains("(immutable-sharing.md)", budget, StringComparison.Ordinal);
        Assert.Contains("(application-admission.md)", budget, StringComparison.Ordinal);
        Assert.Contains("(typed-layouts.md)", budget, StringComparison.Ordinal);
        Assert.DoesNotContain("shared-pointer controls are\nstill required", budget, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(RepositoryTestPaths.Root, "docs", "prepared-pools.md")));
        Assert.True(File.Exists(Path.Combine(RepositoryTestPaths.Root, "docs", "prepared-arenas.md")));
        Assert.True(File.Exists(Path.Combine(RepositoryTestPaths.Root, "docs", "immutable-sharing.md")));
        Assert.True(File.Exists(Path.Combine(RepositoryTestPaths.Root, "docs", "application-admission.md")));
        Assert.True(File.Exists(Path.Combine(RepositoryTestPaths.Root, "docs", "typed-layouts.md")));
    }

    [Fact]
    public void InternalRunResultsAndVersionStatusAreNotPublicDocuments()
    {
        Assert.False(Directory.Exists(Path.Combine(RepositoryTestPaths.Root, "docs", "retained-results")));
        Assert.False(File.Exists(Path.Combine(RepositoryTestPaths.Root, "docs", "version-status.md")));
        string ignored = Read(".gitignore");
        Assert.Contains("/docs/retained-results/", ignored, StringComparison.Ordinal);
        Assert.Contains("/docs/version-status.md", ignored, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/builder-benchmark.md")]
    [InlineData("docs/memory-budgets.md")]
    [InlineData("docs/demo-kernel-metrics.md")]
    [InlineData("docs/demo-runtime-metrics.md")]
    [InlineData("docs/production-accounting-contract.md")]
    [InlineData("docs/diagnostics.md")]
    [InlineData("docs/immutable-sharing.md")]
    [InlineData("docs/application-admission.md")]
    public void PublicDeveloperDocsExcludeMaintainerCheckpointTracking(string path)
    {
        string document = Read(path);
        Assert.DoesNotContain("inconclusive", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permission to publish", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Independent Review", document, StringComparison.Ordinal);
        Assert.DoesNotContain("release gate", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("release acceptance", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("performance verdict", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("performance acceptance", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accepted full-cost", document, StringComparison.OrdinalIgnoreCase);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryTestPaths.Root, .. parts]));
}
