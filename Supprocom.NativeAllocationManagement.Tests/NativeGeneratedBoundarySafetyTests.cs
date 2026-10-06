using Xunit.Abstractions;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeGeneratedBoundarySafetyTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(23, 0)]
    [InlineData(149, 0)]
    [InlineData(541, 0)]
    [InlineData(887, 0)]
    [InlineData(23, 64)]
    [InlineData(149, 64)]
    [InlineData(541, 64)]
    [InlineData(887, 64)]
    public async Task MultipleEnteredReadersHoldCompleteRetiredAndQuarantinedBacking(int seed, int traceCapacity)
    {
        List<string> trace = [];
        try { await NativeGeneratedScenarios.RunGenerationSchedulesAsync(seed, 32, traceCapacity, trace.Add); }
        finally { SaveTrace(seed, traceCapacity, "generations", trace); }
    }

    [Theory]
    [InlineData(23, 0)]
    [InlineData(149, 0)]
    [InlineData(541, 0)]
    [InlineData(887, 0)]
    [InlineData(23, 64)]
    [InlineData(149, 64)]
    [InlineData(541, 64)]
    [InlineData(887, 64)]
    public void SparsePagesKeepOneSurvivorAndRejectEndedAuthorityAcrossReuse(int seed, int traceCapacity) =>
        Run(seed, traceCapacity, "sparse-pages", NativeGeneratedScenarios.RunSparsePageReuse);

    [Theory]
    [InlineData(23, 0)]
    [InlineData(149, 0)]
    [InlineData(541, 0)]
    [InlineData(887, 0)]
    [InlineData(23, 64)]
    [InlineData(149, 64)]
    [InlineData(541, 64)]
    [InlineData(887, 64)]
    public void TighterGrowthAdmitsExactReplacementOverlapAndPreservesOutputOnRefusal(int seed, int traceCapacity) =>
        Run(seed, traceCapacity, "tight-growth", NativeGeneratedScenarios.RunTighterGrowthAtTheCap);

    [Theory]
    [InlineData(23, 0)]
    [InlineData(149, 0)]
    [InlineData(541, 0)]
    [InlineData(887, 0)]
    [InlineData(23, 64)]
    [InlineData(149, 64)]
    [InlineData(541, 64)]
    [InlineData(887, 64)]
    public void MappedProviderAndCompositeFailureKeepEveryRealBackingObligation(int seed, int traceCapacity) =>
        Run(seed, traceCapacity, "mapped-groups", NativeGeneratedScenarios.RunMappedCompositeFailures);

    [Theory]
    [InlineData(23, 0)]
    [InlineData(149, 0)]
    [InlineData(541, 0)]
    [InlineData(887, 0)]
    [InlineData(23, 64)]
    [InlineData(149, 64)]
    [InlineData(541, 64)]
    [InlineData(887, 64)]
    public void LayoutFailuresMatchIndependentFullFieldModelsAtEveryPublicationBoundary(int seed, int traceCapacity) =>
        Run(seed, traceCapacity, "layout-faults", NativeGeneratedScenarios.RunLayoutFaultPrograms);

    private void Run(int seed, int traceCapacity, string family, Action<int, int, int, Action<string>> scenario)
    {
        List<string> trace = [];
        try { scenario(seed, 32, traceCapacity, trace.Add); }
        finally { SaveTrace(seed, traceCapacity, family, trace); }
    }

    private void SaveTrace(int seed, int traceCapacity, string family, List<string> trace)
    {
        output.WriteLine($"generatedBoundarySeed={seed};family={family};traceCapacity={traceCapacity};operations={trace.Count}");
        if (!PackageFixtureEvidence.IsEnabled(Environment.GetEnvironmentVariable("NAM_RETAIN_PACKAGE_EVIDENCE"))) return;
        string directory = Path.Combine(Path.GetTempPath(), "nam-generated-boundaries", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "operations.log"), trace);
        output.WriteLine($"generatedBoundaryEvidence={directory}");
        // Each iteration has its own fresh owner/domain and explicit dimensions.
        // A retained failure prefix is replay input, not a falsely claimed
        // globally minimal counterexample. Original traces remain immutable.
    }
}
