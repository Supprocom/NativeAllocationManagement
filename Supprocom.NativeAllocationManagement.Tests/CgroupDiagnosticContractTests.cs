using System.Reflection;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class CgroupDiagnosticContractTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("-1", null)]
    [InlineData("max", null)]
    [InlineData("1.5", null)]
    [InlineData("0", 0L)]
    [InlineData("999", 0L)]
    [InlineData("1000", 1L)]
    [InlineData("1001", 1L)]
    [InlineData("9223372036854775807", 9_223_372_036_854_775L)]
    [InlineData("18446744073709551615", 18_446_744_073_709_551L)]
    [InlineData("18446744073709551616", null)]
    public void KernelNanosecondsRetainTheirActualUnsignedRangeAndUnit(string? text, long? expected) =>
        Assert.Equal(expected, ExternalObservation.ParseNanoseconds(text));

    [Theory]
    [InlineData("0::/job", "1 0 0:1 / /cg rw - cgroup2 cgroup rw", null, "/cg/job")]
    [InlineData("0::/", "1 0 0:1 / /cg rw shared:4 - cgroup2 cgroup rw", null, "/cg")]
    [InlineData("0::/tenant/job", "1 0 0:1 /tenant /bound rw - cgroup2 cgroup rw", null, "/bound/job")]
    [InlineData("0::/tenant/job", "1 0 0:1 / /all rw - cgroup2 cgroup rw\n2 0 0:1 /tenant /bound rw - cgroup2 cgroup rw", null, "/bound/job")]
    [InlineData("7:cpu,cpuacct:/job", "1 0 0:1 / /cpu rw - cgroup cgroup rw,cpuacct,cpu", "cpu", "/cpu/job")]
    [InlineData("7:cpu,cpuacct:/job", "1 0 0:1 / /cpu rw - cgroup cgroup rw,cpuacct,cpu", "cpuacct", "/cpu/job")]
    [InlineData("3:memory:/tenant/job", "1 0 0:1 /tenant /memory rw - cgroup cgroup rw,memory", "memory", "/memory/job")]
    [InlineData("0::/tenant space/job", "1 0 0:1 /tenant\\040space /mounted\\040space rw - cgroup2 cgroup rw", null, "/mounted space/job")]
    [InlineData("0::/job", "1 0 0:1 / /literal\\134040 rw - cgroup2 cgroup rw", null, "/literal\\040/job")]
    [InlineData("0::/tenant-other/job", "1 0 0:1 /tenant /bound rw - cgroup2 cgroup rw", null, null)]
    [InlineData("7:cpuacct:/job", "1 0 0:1 / /cpu rw - cgroup cgroup rw,cpuacct", "cpu", null)]
    [InlineData("0::/../job", "1 0 0:1 / /cg rw - cgroup2 cgroup rw", null, null)]
    [InlineData("0::/job", "1 0 0:1 / /cg/../other rw - cgroup2 cgroup rw", null, null)]
    [InlineData("0::/job", "1 0 0:1 / /cg\\999 rw - cgroup2 cgroup rw", null, null)]
    [InlineData("0::/job\n0::/other", "1 0 0:1 / /cg rw - cgroup2 cgroup rw", null, null)]
    [InlineData("0::/job", "1 0 0:1 / /first rw - cgroup2 cgroup rw\n2 0 0:1 / /second rw - cgroup2 cgroup rw", null, null)]
    [InlineData("0::/job", "1 0 0:1 / /wrong rw - ext4 root rw", null, null)]
    [InlineData(null, "1 0 0:1 / /cg rw - cgroup2 cgroup rw", null, null)]
    [InlineData("0::/job", null, null, null)]
    [InlineData("0::/job", "malformed", null, null)]
    public void ActualMountRootsControllersEscapesAndAmbiguityAreResolved(string? membership, string? mounts, string? controller, string? expected) =>
        Assert.Equal(expected, ExternalObservation.ResolveCgroupDirectory(membership, mounts, controller));

    [Fact]
    public void V1CpuCountersMapAllAdvertisedValuesWithoutGuessingUserHz()
    {
        CgroupMemorySnapshot snapshot = CgroupMemorySnapshot.FromFiles(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["memory.usage_in_bytes"] = "0",
            ["cpuacct.usage"] = "18446744073709551615",
            ["cpuacct.usage_user"] = "11001",
            ["cpuacct.usage_sys"] = "0",
            ["cpuacct.stat"] = "user 999\nsystem 888\n",
            ["cpu.stat"] = "nr_periods 19\nnr_throttled 7\nthrottled_time 18446744073709551615\n"
        });
        Assert.True(snapshot.Available);
        Assert.Equal(1, snapshot.Version);
        Assert.Equal(0L, snapshot.CurrentBytes);
        Assert.Equal(18_446_744_073_709_551L, snapshot.CpuUsageMicroseconds);
        Assert.Equal(11L, snapshot.CpuUserMicroseconds);
        Assert.Equal(0L, snapshot.CpuSystemMicroseconds);
        Assert.Equal(19L, snapshot.CpuPeriods);
        Assert.Equal(7L, snapshot.CpuThrottledPeriods);
        Assert.Equal(18_446_744_073_709_551L, snapshot.CpuThrottledMicroseconds);
        Assert.Null(snapshot.CpuSourcePath);
        Assert.Null(snapshot.CpuAccountingSourcePath);
    }

    [Fact]
    public void MissingInvalidOrDuplicateCpuInputsAreUnavailableNotMeasuredZero()
    {
        CgroupMemorySnapshot snapshot = CgroupMemorySnapshot.FromFiles(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["memory.usage_in_bytes"] = "0",
            ["cpuacct.stat"] = "user 100\nsystem 200\n",
            ["cpuacct.usage"] = "18446744073709551616",
            ["cpuacct.usage_user"] = "invalid",
            ["cpu.stat"] = "nr_periods 2\nnr_periods 2\nnr_throttled -1\nthrottled_time 0\nthrottled_time 1\n"
        });
        Assert.True(snapshot.Available);
        Assert.Null(snapshot.CpuUsageMicroseconds);
        Assert.Null(snapshot.CpuUserMicroseconds);
        Assert.Null(snapshot.CpuSystemMicroseconds);
        Assert.Null(snapshot.CpuPeriods);
        Assert.Null(snapshot.CpuThrottledPeriods);
        Assert.Null(snapshot.CpuThrottledMicroseconds);
        Assert.Equal(default, CgroupMemorySnapshot.ReadFromProcFiles(null, null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AbsentMemoryControllerDoesNotEraseAnActualCpuObservation(int version)
    {
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        if (version == 1) files.Add("cpuacct.usage", "17000");
        else files.Add("cpu.stat", "usage_usec 17\n");
        CgroupMemorySnapshot snapshot = CgroupMemorySnapshot.FromFiles(files);
        Assert.False(snapshot.Available);
        Assert.Null(snapshot.CurrentBytes);
        Assert.Equal(version, snapshot.Version);
        Assert.Equal(17L, snapshot.CpuUsageMicroseconds);
        Assert.Null(snapshot.SourcePath);
    }

    [Fact]
    public void ExplicitCpuHierarchiesReadRealSeparateFilesAndKeepTheirProvenance()
    {
        string root = Path.Combine(Path.GetTempPath(), "nam-cgroup-source-" + Guid.NewGuid().ToString("N"));
        string memory = Path.Combine(root, "memory");
        string cpu = Path.Combine(root, "cpu");
        string accounting = Path.Combine(root, "accounting");
        Directory.CreateDirectory(memory);
        Directory.CreateDirectory(cpu);
        Directory.CreateDirectory(accounting);
        try
        {
            File.WriteAllText(Path.Combine(memory, "memory.usage_in_bytes"), "32");
            File.WriteAllText(Path.Combine(cpu, "cpu.stat"), "nr_periods 7\nnr_throttled 3\nthrottled_time 12001\n");
            File.WriteAllText(Path.Combine(accounting, "cpuacct.usage"), "17001");
            File.WriteAllText(Path.Combine(accounting, "cpuacct.usage_user"), "11001");
            File.WriteAllText(Path.Combine(accounting, "cpuacct.usage_sys"), "6000");
            CgroupMemorySnapshot snapshot = CgroupMemorySnapshot.ReadFromDirectories(memory, cpu, accounting);
            Assert.True(snapshot.Available);
            Assert.Equal(32L, snapshot.CurrentBytes);
            Assert.Equal(17L, snapshot.CpuUsageMicroseconds);
            Assert.Equal(11L, snapshot.CpuUserMicroseconds);
            Assert.Equal(6L, snapshot.CpuSystemMicroseconds);
            Assert.Equal(7L, snapshot.CpuPeriods);
            Assert.Equal(3L, snapshot.CpuThrottledPeriods);
            Assert.Equal(12L, snapshot.CpuThrottledMicroseconds);
            Assert.Equal(memory, snapshot.SourcePath);
            Assert.Equal(cpu, snapshot.CpuSourcePath);
            Assert.Equal(accounting, snapshot.CpuAccountingSourcePath);
            Assert.Equal(snapshot, JsonSerializer.Deserialize<CgroupMemorySnapshot>(JsonSerializer.Serialize(snapshot, VoxelJson.Options), VoxelJson.Options));
            Assert.Null(CgroupMemorySnapshot.ReadFromDirectories(memory, cpuRoot: null, accountingRoot: null).CpuSourcePath);
            Assert.Null(CgroupMemorySnapshot.ReadFromDirectories(memory, cpuRoot: null, accountingRoot: null).CpuAccountingSourcePath);
            Assert.Null(CgroupMemorySnapshot.ReadFromDirectories(memory, cpuRoot: null, accountingRoot: null).CpuUsageMicroseconds);
            if (OperatingSystem.IsLinux())
            {
                string membership = "2:memory:/job\n3:cpu:/worker\n4:cpuacct:/accounted\n";
                string mounts = $"1 0 0:1 /job {memory} rw - cgroup cgroup rw,memory\n2 0 0:2 /worker {cpu} rw - cgroup cgroup rw,cpu\n3 0 0:3 /accounted {accounting} rw - cgroup cgroup rw,cpuacct\n";
                Assert.Equal(snapshot, CgroupMemorySnapshot.ReadFromProcFiles(membership, mounts));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void V2UsesOneActualCpuSourceAndDoesNotReadLegacyAccountingFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "nam-cgroup-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "memory.current"), "0");
            File.WriteAllText(Path.Combine(root, "cpu.stat"), "usage_usec 17\nuser_usec 11\nsystem_usec 6\nnr_periods 3\nnr_throttled 0\nthrottled_usec 0\n");
            File.WriteAllText(Path.Combine(root, "cpuacct.usage"), "900000");
            CgroupMemorySnapshot snapshot = CgroupMemorySnapshot.ReadFromDirectory(root);
            Assert.Equal(2, snapshot.Version);
            Assert.Equal(17L, snapshot.CpuUsageMicroseconds);
            Assert.Equal(0L, snapshot.CpuThrottledMicroseconds);
            Assert.Equal(root, snapshot.SourcePath);
            Assert.Equal(root, snapshot.CpuSourcePath);
            Assert.Null(snapshot.CpuAccountingSourcePath);
            if (OperatingSystem.IsLinux())
                Assert.Equal(snapshot, CgroupMemorySnapshot.ReadFromProcFiles("0::/", $"1 0 0:1 / {root} rw - cgroup2 cgroup rw\n"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DefaultSchemaContainsUnavailableOptionalValuesNotFakeCounterZeros()
    {
        CgroupMemorySnapshot absent = CgroupMemorySnapshot.FromFiles(new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.False(absent.Available);
        foreach (PropertyInfo property in typeof(CgroupMemorySnapshot).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            if (!string.Equals(property.Name, nameof(CgroupMemorySnapshot.Available), StringComparison.Ordinal)) Assert.Null(property.GetValue(absent));
        }
    }

    [Fact]
    public void EveryCgroupPropertyHasAnExactDefinitionAndPositiveNegativeProof()
    {
        using JsonDocument registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cgroup-diagnostic-contracts.json")));
        Assert.False(registry.RootElement.GetProperty("completeReleaseInventory").GetBoolean());
        JsonElement properties = registry.RootElement.GetProperty("properties");
        PropertyInfo[] actual = typeof(CgroupMemorySnapshot).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.Equal(29, actual.Length);
        Assert.Equal(actual.Select(property => property.Name).Order(StringComparer.Ordinal),
            properties.EnumerateArray().Select(property => property.GetProperty("name").GetString()!).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        string definitions = File.ReadAllText(Path.Combine(RepositoryTestPaths.Root, "docs", "demo-kernel-metrics.md"));
        foreach (JsonElement property in properties.EnumerateArray())
        {
            string name = property.GetProperty("name").GetString()!;
            PropertyInfo observed = actual.Single(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
            Assert.Equal(observed.PropertyType.ToString(), property.GetProperty("valueType").GetString());
            Assert.False(string.IsNullOrWhiteSpace(property.GetProperty("units").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(property.GetProperty("source").GetString()));
            Assert.Contains("`" + name + "`", definitions, StringComparison.Ordinal);
            VerifyProof(property.GetProperty("positiveProof").GetString()!);
            VerifyProof(property.GetProperty("negativeProof").GetString()!);
        }
    }

    private static void VerifyProof(string name)
    {
        string[] parts = name.Split('.');
        Assert.Equal(2, parts.Length);
        Type? type = typeof(CgroupDiagnosticContractTests).Assembly.GetType(typeof(CgroupDiagnosticContractTests).Namespace + "." + parts[0]);
        Assert.NotNull(type);
        MethodInfo? method = type.GetMethod(parts[1], BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        Assert.NotNull(method.GetCustomAttribute<FactAttribute>());
    }
}
