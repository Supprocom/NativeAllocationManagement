using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class VoxelDiagnosticWireIntegrityTests
{
    [Theory]
    [InlineData(nameof(PressureRuntimeSnapshot.Utc))]
    [InlineData(nameof(PressureRuntimeSnapshot.TotalAllocatedBytes))]
    [InlineData(nameof(PressureRuntimeSnapshot.Gen0Collections))]
    [InlineData(nameof(PressureRuntimeSnapshot.Gen1Collections))]
    [InlineData(nameof(PressureRuntimeSnapshot.Gen2Collections))]
    [InlineData(nameof(PressureRuntimeSnapshot.TotalPauseMilliseconds))]
    [InlineData(nameof(PressureRuntimeSnapshot.TotalAvailableMemoryBytes))]
    [InlineData(nameof(PressureRuntimeSnapshot.MemoryLoadBytes))]
    [InlineData(nameof(PressureRuntimeSnapshot.HighMemoryLoadThresholdBytes))]
    [InlineData(nameof(PressureRuntimeSnapshot.TotalCommittedBytes))]
    [InlineData(nameof(PressureRuntimeSnapshot.HeapSizeBytes))]
    [InlineData(nameof(PressureRuntimeSnapshot.FragmentedBytes))]
    [InlineData(nameof(PressureRuntimeSnapshot.LargeObjectHeapBytes))]
    [InlineData(nameof(PressureRuntimeSnapshot.ProcessWorkingSetBytes))]
    [InlineData(nameof(PressureRuntimeSnapshot.ProcessCpuMilliseconds))]
    [InlineData(nameof(PressureRuntimeSnapshot.ProcessorCount))]
    [InlineData(nameof(PressureRuntimeSnapshot.Cgroup))]
    [InlineData(nameof(PressureRuntimeSnapshot.GcConfiguration))]
    [InlineData(nameof(PressureRuntimeSnapshot.CompilationConfiguration))]
    [InlineData(nameof(PressureRuntimeSnapshot.LastCompletedGcIndex))]
    public void MissingRuntimeMemberIsRejectedInsteadOfInventingAnObservation(string property)
    {
        JsonObject payload = (JsonObject)JsonSerializer.SerializeToNode(PressureRuntimeSnapshot.Capture(), VoxelJson.Options)!;
        string member = JsonNamingPolicy.CamelCase.ConvertName(property);
        Assert.True(payload.Remove(member));
        JsonException error = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PressureRuntimeSnapshot>(payload.ToJsonString(VoxelJson.Options), VoxelJson.Options));
        Assert.Contains(member, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(PressureCompilationConfiguration.TieredCompilation))]
    [InlineData(nameof(PressureCompilationConfiguration.TieredPgo))]
    public void MissingCompilationMemberCannotBecomeAnAbsentRuntimeSetting(string property)
    {
        JsonObject payload = (JsonObject)JsonSerializer.SerializeToNode(PressureRuntimeSnapshot.Capture(), VoxelJson.Options)!;
        JsonObject compilation = (JsonObject)payload["compilationConfiguration"]!;
        string member = JsonNamingPolicy.CamelCase.ConvertName(property);
        Assert.True(compilation.Remove(member));
        JsonException error = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PressureRuntimeSnapshot>(payload.ToJsonString(VoxelJson.Options), VoxelJson.Options));
        Assert.Contains(member, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(ChildRunResult), nameof(ChildRunResult.LargeObjectHeapBytesAfterRun))]
    [InlineData(nameof(ChildRunResult), nameof(ChildRunResult.ColdManagedAllocatedBytes))]
    [InlineData(nameof(ChildRunResult), nameof(ChildRunResult.ColdElapsedMilliseconds))]
    [InlineData(nameof(BenchmarkSummary), nameof(BenchmarkSummary.SafeMeanGenerationMilliseconds))]
    [InlineData(nameof(BenchmarkSummary), nameof(BenchmarkSummary.SafeMeanFaceDerivationMilliseconds))]
    [InlineData(nameof(BenchmarkSummary), nameof(BenchmarkSummary.SafeMeanTransparentMaskMilliseconds))]
    [InlineData(nameof(BenchmarkSummary), nameof(BenchmarkSummary.SafeMeanOpaquePackingMilliseconds))]
    [InlineData(nameof(BenchmarkSummary), nameof(BenchmarkSummary.SafeMeanTransparentPackingMilliseconds))]
    [InlineData(nameof(BenchmarkSummary), nameof(BenchmarkSummary.SafeMeanCoordinateRecycleMilliseconds))]
    [InlineData(nameof(BenchmarkSummary), nameof(BenchmarkSummary.SafeMeanFaceRecycleMilliseconds))]
    [InlineData(nameof(BenchmarkSummary), nameof(BenchmarkSummary.SafeMeanMaskRecycleMilliseconds))]
    [InlineData(nameof(BenchmarkSummary), nameof(BenchmarkSummary.SafeMeanPackingRecycleMilliseconds))]
    [InlineData(nameof(BenchmarkSummary), nameof(BenchmarkSummary.SafeMeanColdElapsedMilliseconds))]
    [InlineData(nameof(BenchmarkSummary), nameof(BenchmarkSummary.NamMeanColdElapsedMilliseconds))]
    public void OptionalLegacyMeasurementDistinguishesOmissionFromExplicitZero(string schema, string property)
    {
        Type type = schema switch
        {
            nameof(ChildRunResult) => typeof(ChildRunResult),
            nameof(BenchmarkSummary) => typeof(BenchmarkSummary),
            _ => throw new ArgumentOutOfRangeException(nameof(schema))
        };
        object absent = JsonSerializer.Deserialize("{}", type, VoxelJson.Options)!;
        Assert.Null(type.GetProperty(property)!.GetValue(absent));
        JsonObject absentJson = (JsonObject)JsonSerializer.SerializeToNode(absent, type, VoxelJson.Options)!;
        string member = JsonNamingPolicy.CamelCase.ConvertName(property);
        Assert.True(absentJson.ContainsKey(member));
        Assert.Null(absentJson[member]);
        JsonObject explicitZero = new() { [member] = 0 };
        object measured = JsonSerializer.Deserialize(explicitZero.ToJsonString(VoxelJson.Options), type, VoxelJson.Options)!;
        Assert.NotNull(type.GetProperty(property)!.GetValue(measured));
        using JsonDocument measuredJson = JsonDocument.Parse(JsonSerializer.Serialize(measured, type, VoxelJson.Options));
        Assert.Equal(JsonValueKind.Number, measuredJson.RootElement.GetProperty(member).ValueKind);
        Assert.Equal(0, measuredJson.RootElement.GetProperty(member).GetDouble());
    }

    [Fact]
    public void InventoryBindsEveryLiveMemberAndEveryOptionalNumericLegacyParameter()
    {
        using JsonDocument registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "voxel-diagnostic-wire-contracts.json")));
        Assert.False(registry.RootElement.GetProperty("completeReleaseInventory").GetBoolean());
        Dictionary<string, Type> types = new(StringComparer.Ordinal)
        {
            [nameof(PressureRuntimeSnapshot)] = typeof(PressureRuntimeSnapshot),
            [nameof(PressureCompilationConfiguration)] = typeof(PressureCompilationConfiguration),
            [nameof(ChildRunResult)] = typeof(ChildRunResult),
            [nameof(BenchmarkSummary)] = typeof(BenchmarkSummary)
        };
        Assert.Equal(types.Count, registry.RootElement.GetProperty("schemas").GetArrayLength());
        foreach (JsonElement schema in registry.RootElement.GetProperty("schemas").EnumerateArray())
        {
            Type type = types[schema.GetProperty("name").GetString()!];
            Assert.Equal(type.FullName, schema.GetProperty("qualifiedName").GetString());
            Assert.True(File.Exists(Path.Combine(RepositoryTestPaths.Root, schema.GetProperty("source").GetString()!)));
            string[] members = schema.GetProperty("members").EnumerateArray().Select(static member => member.GetString()!).ToArray();
            bool required = string.Equals(schema.GetProperty("role").GetString(), "required", StringComparison.Ordinal);
            if (required)
            {
                PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                Assert.Equal(properties.Select(static property => property.Name).Order(StringComparer.Ordinal), members.Order(StringComparer.Ordinal), StringComparer.Ordinal);
                foreach (PropertyInfo property in properties) Assert.NotNull(property.GetCustomAttribute<JsonRequiredAttribute>());
            }
            else
            {
                string[] optional = type.GetConstructors().SelectMany(static constructor => constructor.GetParameters())
                    .Where(static parameter => parameter.HasDefaultValue && (parameter.ParameterType == typeof(long?) || parameter.ParameterType == typeof(double?)
                        || parameter.ParameterType == typeof(long) || parameter.ParameterType == typeof(double)))
                    .Select(static parameter => parameter.Name!).ToArray();
                Assert.Equal(optional.Order(StringComparer.Ordinal), members.Order(StringComparer.Ordinal), StringComparer.Ordinal);
                foreach (string member in members) Assert.NotNull(Nullable.GetUnderlyingType(type.GetProperty(member)!.PropertyType));
            }
            MethodInfo proof = typeof(VoxelDiagnosticWireIntegrityTests).GetMethod(schema.GetProperty("proof").GetString()!)!;
            Assert.NotNull(proof);
            Assert.NotNull(proof.GetCustomAttribute<TheoryAttribute>());
        }
    }
}
