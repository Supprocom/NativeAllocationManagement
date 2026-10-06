using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class PressureRuntimeDiagnosticContractTests
{
    [Fact]
    public void CapturedProcessAndCumulativeCountersUseActualBoundedSources()
    {
        using Process process = Process.GetCurrentProcess();
        DateTime beforeUtc = DateTime.UtcNow;
        long beforeAllocated = GC.GetTotalAllocatedBytes(precise: true);
        int[] beforeCollections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        double beforePause = GC.GetTotalPauseDuration().TotalMilliseconds;
        double beforeCpu = process.TotalProcessorTime.TotalMilliseconds;
        PressureRuntimeSnapshot observed = PressureRuntimeSnapshot.Capture();
        process.Refresh();
        Assert.Equal(DateTimeKind.Utc, observed.Utc.Kind);
        Assert.InRange(observed.Utc, beforeUtc, DateTime.UtcNow);
        Assert.InRange(observed.TotalAllocatedBytes, beforeAllocated, GC.GetTotalAllocatedBytes(precise: true));
        Assert.InRange(observed.Gen0Collections, beforeCollections[0], GC.CollectionCount(0));
        Assert.InRange(observed.Gen1Collections, beforeCollections[1], GC.CollectionCount(1));
        Assert.InRange(observed.Gen2Collections, beforeCollections[2], GC.CollectionCount(2));
        Assert.InRange(observed.TotalPauseMilliseconds, beforePause, GC.GetTotalPauseDuration().TotalMilliseconds);
        Assert.InRange(observed.ProcessCpuMilliseconds, beforeCpu, process.TotalProcessorTime.TotalMilliseconds);
        Assert.True(observed.ProcessWorkingSetBytes >= 0);
        Assert.Equal(Environment.ProcessorCount, observed.ProcessorCount);
        Assert.True(observed.ProcessorCount > 0);
        if (observed.Cgroup.Available) Assert.True(observed.Cgroup.CurrentBytes >= 0);
        else Assert.Null(observed.Cgroup.CurrentBytes);
    }

    [Fact]
    public void RealManagedWorkAdvancesHistoriesWithoutClaimingPhysicalFreeing()
    {
        PressureRuntimeSnapshot before = PressureRuntimeSnapshot.Capture();
        byte[] payload = GC.AllocateUninitializedArray<byte>(8192);
        payload.AsSpan().Fill(17);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        PressureRuntimeSnapshot after = PressureRuntimeSnapshot.Capture();
        Assert.True(after.TotalAllocatedBytes >= before.TotalAllocatedBytes + payload.Length);
        Assert.True(after.Gen0Collections > before.Gen0Collections);
        Assert.True(after.Gen1Collections > before.Gen1Collections);
        Assert.True(after.Gen2Collections > before.Gen2Collections);
        Assert.True(after.TotalPauseMilliseconds >= before.TotalPauseMilliseconds);
        Assert.True(after.LastCompletedGcIndex > 0);
        Assert.NotNull(after.HeapSizeBytes);
        Assert.NotNull(after.LargeObjectHeapBytes);
        Assert.True(after.HeapSizeBytes >= after.LargeObjectHeapBytes);
        Assert.Equal(17, payload[0]);
        GC.KeepAlive(payload);
        // Neither a live byte counter nor a collected object proves RSS release.
    }

    [Fact]
    public void MissingGcObservationMeansUnavailableNotMeasuredMemoryZero()
    {
        PressureRuntimeSnapshot observed = CaptureFromGcObservation(default, hasCollection: false);
        foreach (string name in CompletedGcProperties)
            Assert.Null(typeof(PressureRuntimeSnapshot).GetProperty(name)!.GetValue(observed));
        Assert.Equal(DateTimeKind.Utc, observed.Utc.Kind);
        Assert.True(observed.TotalAllocatedBytes > 0);
        Assert.NotNull(observed.GcConfiguration);
        Assert.Equal(Environment.ProcessorCount, observed.ProcessorCount);
        Assert.Null(default(PressureRuntimeSnapshot).LastCompletedGcIndex);
        Assert.Equal(default, default(PressureRuntimeSnapshot).Utc);
        Assert.Null(default(PressureRuntimeSnapshot).GcConfiguration);
        Assert.Null(default(PressureRuntimeSnapshot).CompilationConfiguration.TieredCompilation);
        Assert.Null(default(PressureRuntimeSnapshot).CompilationConfiguration.TieredPgo);
        string implementation = File.ReadAllText(Path.Combine(RepositoryTestPaths.Root, "SharedContract", "Contracts", "PressureProtocolContract.cs"));
        Assert.Contains("return Capture(memory, memory.Index > 0);", implementation, StringComparison.Ordinal);
        // This executes the core's explicit missing-observation branch and
        // checks the actual public predicate. It is not fresh-process no-GC proof.
    }

    [Fact]
    public void CompletedGcFieldsMapOneRealObservationWithoutResamplingItsIdentity()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        Assert.True(memory.Index > 0);
        PressureRuntimeSnapshot observed = CaptureFromGcObservation(memory, hasCollection: true);
        Assert.Equal(memory.Index, observed.LastCompletedGcIndex);
        Assert.Equal(memory.TotalAvailableMemoryBytes, observed.TotalAvailableMemoryBytes);
        Assert.Equal(memory.MemoryLoadBytes, observed.MemoryLoadBytes);
        Assert.Equal(memory.HighMemoryLoadThresholdBytes, observed.HighMemoryLoadThresholdBytes);
        Assert.Equal(memory.TotalCommittedBytes, observed.TotalCommittedBytes);
        Assert.Equal(memory.HeapSizeBytes, observed.HeapSizeBytes);
        Assert.Equal(memory.FragmentedBytes, observed.FragmentedBytes);
        if (memory.GenerationInfo.Length > 3) Assert.Equal(memory.GenerationInfo[3].SizeAfterBytes, observed.LargeObjectHeapBytes);
        else Assert.Null(observed.LargeObjectHeapBytes);
    }

    [Fact]
    public void GcConfigurationIsActualInvariantRuntimeStateAndIndependentlyCaptured()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            PressureRuntimeSnapshot observed = PressureRuntimeSnapshot.Capture();
            IReadOnlyDictionary<string, object> actual = GC.GetConfigurationVariables();
            Assert.Equal(actual.Keys.Order(StringComparer.Ordinal), observed.GcConfiguration.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> entry in actual)
                Assert.Equal(Convert.ToString(entry.Value, CultureInfo.InvariantCulture) ?? string.Empty, observed.GcConfiguration[entry.Key]);
            Assert.NotSame(observed.GcConfiguration, PressureRuntimeSnapshot.Capture().GcConfiguration);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void CompilationVariablesAreRawValuesAndAbsenceDoesNotInferEffectiveJitState()
    {
        string? previousTiering = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation");
        string? previousPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_TieredCompilation", null);
            Environment.SetEnvironmentVariable("DOTNET_TieredPGO", null);
            PressureCompilationConfiguration absent = PressureCompilationConfiguration.Capture();
            Assert.Equal(string.Empty, absent.TieredCompilation);
            Assert.Equal(string.Empty, absent.TieredPgo);
            Assert.False(PressureCompilationPolicy.HasEquivalentDisabledTiering(
                default(PressureRuntimeSnapshot) with { CompilationConfiguration = absent },
                default(PressureRuntimeSnapshot) with { CompilationConfiguration = absent }));
            Environment.SetEnvironmentVariable("DOTNET_TieredCompilation", "1");
            Environment.SetEnvironmentVariable("DOTNET_TieredPGO", "0");
            Assert.Equal(new PressureCompilationConfiguration("1", "0"), PressureCompilationConfiguration.Capture());
            Assert.Equal(new PressureCompilationConfiguration("1", "0"), PressureRuntimeSnapshot.Capture().CompilationConfiguration);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_TieredCompilation", previousTiering);
            Environment.SetEnvironmentVariable("DOTNET_TieredPGO", previousPgo);
        }
    }

    [Fact]
    public void JsonPreservesActualNestedObservationsAndUnavailableCompletedGcFields()
    {
        foreach (PressureRuntimeSnapshot observed in new[] { PressureRuntimeSnapshot.Capture(), CaptureFromGcObservation(default, hasCollection: false) })
        {
            string json = JsonSerializer.Serialize(observed, VoxelJson.Options);
            PressureRuntimeSnapshot restored = JsonSerializer.Deserialize<PressureRuntimeSnapshot>(json, VoxelJson.Options);
            Assert.Equal(json, JsonSerializer.Serialize(restored, VoxelJson.Options));
            Assert.Equal(observed.Cgroup, restored.Cgroup);
            Assert.Equal(observed.CompilationConfiguration, restored.CompilationConfiguration);
            Assert.Equal(observed.GcConfiguration.OrderBy(pair => pair.Key, StringComparer.Ordinal),
                restored.GcConfiguration.OrderBy(pair => pair.Key, StringComparer.Ordinal));
            if (observed.LastCompletedGcIndex is null)
            {
                using JsonDocument document = JsonDocument.Parse(json);
                foreach (string name in CompletedGcProperties)
                    Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty(JsonNamingPolicy.CamelCase.ConvertName(name)).ValueKind);
            }
        }
    }

    [Fact]
    public void EveryRuntimeAndCompilationPropertyHasAnExactDefinitionAndExecutedProof()
    {
        using JsonDocument registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "pressure-runtime-diagnostic-contracts.json")));
        Assert.False(registry.RootElement.GetProperty("completeReleaseInventory").GetBoolean());
        string definitions = File.ReadAllText(Path.Combine(RepositoryTestPaths.Root, "docs", "demo-runtime-metrics.md"));
        Dictionary<string, Type> schemas = new(StringComparer.Ordinal)
        {
            [nameof(PressureRuntimeSnapshot)] = typeof(PressureRuntimeSnapshot),
            [nameof(PressureCompilationConfiguration)] = typeof(PressureCompilationConfiguration)
        };
        Assert.Equal(schemas.Count, registry.RootElement.GetProperty("schemas").GetArrayLength());
        foreach (JsonElement schema in registry.RootElement.GetProperty("schemas").EnumerateArray())
        {
            string name = schema.GetProperty("name").GetString()!;
            Assert.Equal(schemas[name].FullName, schema.GetProperty("qualifiedName").GetString());
            PropertyInfo[] actual = schemas[name].GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            Assert.Equal(string.Equals(name, nameof(PressureRuntimeSnapshot), StringComparison.Ordinal) ? 20 : 2, actual.Length);
            JsonElement properties = schema.GetProperty("properties");
            Assert.Equal(actual.Select(property => property.Name).Order(StringComparer.Ordinal),
                properties.EnumerateArray().Select(property => property.GetProperty("name").GetString()!).Order(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (JsonElement property in properties.EnumerateArray())
            {
                string propertyName = property.GetProperty("name").GetString()!;
                Assert.Equal(actual.Single(candidate => string.Equals(candidate.Name, propertyName, StringComparison.Ordinal)).PropertyType.ToString(), property.GetProperty("valueType").GetString());
                Assert.Contains("`" + propertyName + "`", definitions, StringComparison.Ordinal);
                foreach (string field in new[] { "units", "source", "availability", "lifetime", "consistency" })
                    Assert.False(string.IsNullOrWhiteSpace(property.GetProperty(field).GetString()));
                foreach (string field in new[] { "positiveProof", "negativeProof" })
                {
                    MethodInfo proof = typeof(PressureRuntimeDiagnosticContractTests).GetMethod(property.GetProperty(field).GetString()!)!;
                    Assert.NotNull(proof);
                    Assert.NotNull(proof.GetCustomAttribute<FactAttribute>());
                }
            }
        }
    }

    private static readonly string[] CompletedGcProperties = [nameof(PressureRuntimeSnapshot.TotalAvailableMemoryBytes),
        nameof(PressureRuntimeSnapshot.MemoryLoadBytes), nameof(PressureRuntimeSnapshot.HighMemoryLoadThresholdBytes),
        nameof(PressureRuntimeSnapshot.TotalCommittedBytes), nameof(PressureRuntimeSnapshot.HeapSizeBytes),
        nameof(PressureRuntimeSnapshot.FragmentedBytes), nameof(PressureRuntimeSnapshot.LargeObjectHeapBytes),
        nameof(PressureRuntimeSnapshot.LastCompletedGcIndex)];

    private static PressureRuntimeSnapshot CaptureFromGcObservation(GCMemoryInfo memory, bool hasCollection) =>
        (PressureRuntimeSnapshot)typeof(PressureRuntimeSnapshot).GetMethod(nameof(PressureRuntimeSnapshot.Capture),
            BindingFlags.NonPublic | BindingFlags.Static, binder: null, types: [typeof(GCMemoryInfo), typeof(bool)], modifiers: null)!.Invoke(null, [memory, hasCollection])!;
}
