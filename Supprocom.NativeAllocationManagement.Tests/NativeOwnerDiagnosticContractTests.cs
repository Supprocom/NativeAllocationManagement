using System.Reflection;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Conformance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeOwnerDiagnosticContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void BuilderTransitionsMatchAllOwnerFields(int traceCapacity) => NativeOwnerDiagnosticOracle.RunBuilder(traceCapacity);

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void WorkspaceTransitionsMatchAllOwnerFields(int traceCapacity) => NativeOwnerDiagnosticOracle.RunWorkspace(traceCapacity);

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void PoolTransitionsMatchAllOwnerFields(int traceCapacity) => NativeOwnerDiagnosticOracle.RunPool(traceCapacity);

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void ArenaTransitionsMatchAllOwnerFields(int traceCapacity) => NativeOwnerDiagnosticOracle.RunArena(traceCapacity);

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void RegionTransitionsMatchAllOwnerFields(int traceCapacity) => NativeOwnerDiagnosticOracle.RunRegion(traceCapacity);

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void SynchronizedRootsAndRecordsMatchAllOwnerFields(int traceCapacity) => NativeOwnerDiagnosticOracle.RunSynchronizedPool(traceCapacity);

    [Fact]
    public void EnteredWorkspaceDemandIsRealButDoesNotPublishPersistentAuthority()
    {
        long epoch = NativeMemoryDiagnostics.Snapshot().MetricsEpoch;
        using NativeWorkspace<int> workspace = new(4);
        NativeOwnerDiagnosticOracle.Statistics expected = NativeOwnerDiagnosticOracle.Statistics.Active(workspace.Id,
            NativeOwnerModel.ThreadConfinedWorkspace, 16, 16, 1);
        Assert.Throws<InvalidOperationException>(() => workspace.Process(3, _ =>
        {
            NativeOwnerDiagnosticOracle.Statistics entered = expected with
            { RequestedBytes = 12, InitializedPayloadBytes = 12, PeakInitializedPayloadBytes = 12, AvailableSegmentCount = 0 };
            NativeOwnerDiagnosticOracle.VerifyBoth(workspace.GetStatistics(), workspace.CaptureDiagnosticSnapshot(), entered,
                NativeOwnerDiagnosticOracle.Structural.From(entered, epoch) with { ActiveRecords = 1 });
            throw new InvalidOperationException("Injected entered producer failure.");
        }, static view => view[0]));
        expected = expected with { PeakInitializedPayloadBytes = 12 };
        NativeOwnerDiagnosticOracle.VerifyBoth(workspace.GetStatistics(), workspace.CaptureDiagnosticSnapshot(), expected,
            NativeOwnerDiagnosticOracle.Structural.From(expected, epoch) with { ActiveRecords = 1 });
        Assert.Throws<InvalidOperationException>(() => workspace.Read(static view => view[0]));
    }

    [Fact]
    public void FailedBuilderReallocationKeepsRealPriorPeaksButNoSurvivingBacking()
    {
        long epoch = NativeMemoryDiagnostics.Snapshot().MetricsEpoch;
        NativeMemoryBudget budget = new(64);
        using NativeBuilder<int> builder = new(budget, 2);
        builder.Append([17, 19]);
        try
        {
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => builder.Append(23));
            NativeOwnerDiagnosticOracle.Statistics expected = new()
            {
                OwnerId = builder.Id,
                Model = NativeOwnerModel.SingleWriterBuilder,
                Lifecycle = NativeOwnerLifecycle.Disposed,
                FreshSegmentAllocationCount = 1,
                PeakOutstandingNativeBytes = 8,
                PeakInitializedPayloadBytes = 8
            };
            NativeOwnerDiagnosticOracle.VerifyBoth(builder.GetStatistics(), builder.CaptureDiagnosticSnapshot(), expected,
                NativeOwnerDiagnosticOracle.Structural.From(expected, epoch));
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnteredRetirementAndQuarantineKeepEveryFieldChargedUntilActualCleanup(bool quarantine)
    {
        long epoch = NativeMemoryDiagnostics.Snapshot().MetricsEpoch;
        long extent = OperatingSystem.IsWindows() ? 4 : 64;
        NativeMemoryBudget budget = new(128);
        NativeConcurrentPool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeOwnerDiagnosticOracle.Statistics expected = new()
        {
            OwnerId = pool.Id,
            Model = NativeOwnerModel.SynchronizedPool,
            Lifecycle = NativeOwnerLifecycle.Active,
            Generation = 1,
            RetiredBytes = extent,
            RetiredSegmentCount = 1,
            OutstandingNativeBytes = extent,
            PeakOutstandingNativeBytes = extent,
            PeakInitializedPayloadBytes = 4,
            FreshSegmentAllocationCount = 1
        };
        try
        {
            using ConcurrentPooled<int> value = pool.Rent(1, static writer => writer.Write(42));
            NativeAllocationQuarantinedException? failure = null;
            try
            {
                value.Access(view =>
                {
                    pool.ReleaseLeasesToGarbageCollector();
                    NativeOwnerDiagnosticOracle.VerifyBoth(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot(), expected,
                        NativeOwnerDiagnosticOracle.Structural.From(expected, epoch) with { OrdinaryTraversalIndex = 0, RetiredGenerationCount = 1 });
                    Assert.Equal(42, view[0]);
                    Assert.Equal(extent, budget.CaptureStatistics().CommittedBytes);
                    if (quarantine) NativeMemoryTestHooks.FailAfterCommitBoundary(1);
                });
            }
            catch (NativeAllocationQuarantinedException exception) { failure = exception; }
            if (quarantine)
            {
                Assert.NotNull(failure);
                NativeOwnerDiagnosticOracle.VerifyBoth(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot(), expected,
                    NativeOwnerDiagnosticOracle.Structural.From(expected, epoch) with
                    { OrdinaryTraversalIndex = 0, QuarantinedGenerationCount = 1, QuarantinedSegmentCount = 1 });
            }
            else
            {
                Assert.Null(failure);
                expected = expected with
                {
                    RetiredBytes = 0,
                    RetiredSegmentCount = 0,
                    RetainedBytes = extent,
                    UsableCapacityBytes = 4,
                    SegmentCount = 1,
                    AvailableSegmentCount = 1
                };
                NativeOwnerDiagnosticOracle.VerifyBoth(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot(), expected,
                    NativeOwnerDiagnosticOracle.Structural.From(expected, epoch) with { OrdinaryTraversalIndex = 0 });
            }
            Assert.Equal(0, budget.CaptureStatistics().FreeCount);
        }
        finally { NativeMemoryTestHooks.Reset(); pool.Dispose(); }
        expected = expected with
        {
            Lifecycle = NativeOwnerLifecycle.Disposed,
            Generation = 2,
            RetiredBytes = 0,
            RetiredSegmentCount = 0,
            RetainedBytes = 0,
            UsableCapacityBytes = 0,
            SegmentCount = 0,
            AvailableSegmentCount = 0,
            OutstandingNativeBytes = 0
        };
        NativeOwnerDiagnosticOracle.VerifyBoth(pool.GetStatistics(), pool.CaptureDiagnosticSnapshot(), expected,
            NativeOwnerDiagnosticOracle.Structural.From(expected, checked(epoch + 1)));
        // The explicit hook reset in finally advances only the process identity;
        // all independently modeled owner peaks/histories remain unchanged.
        Assert.Equal(checked(epoch + 1), NativeMemoryDiagnostics.Snapshot().MetricsEpoch);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void OwnerInventoriesMatchEveryTypedPropertyDefinitionAndProof()
    {
        string root = RepositoryTestPaths.Root;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "conformance", "native-owner-diagnostic-contracts.json")));
        JsonElement registry = document.RootElement;
        Assert.False(registry.GetProperty("completeReleaseInventory").GetBoolean());
        Assert.True(File.Exists(Path.Combine(root, registry.GetProperty("oracleSource").GetString()!)));
        VerifyTestReference(registry.GetProperty("packageProof").GetString()!);
        foreach (string key in new[] { "availability", "unreviewedScope", "oracleScope" }) Assert.False(string.IsNullOrWhiteSpace(registry.GetProperty(key).GetString()), key);
        int total = 0;
        foreach (JsonElement schema in registry.GetProperty("schemas").EnumerateArray())
        {
            Type actualType = typeof(NativeMemoryBudget).Assembly.GetType(schema.GetProperty("schema").GetString()!, throwOnError: true)!;
            Type expectedType = typeof(NativeOwnerDiagnosticOracle).GetNestedType(schema.GetProperty("expected").GetString()!, BindingFlags.NonPublic)!;
            PropertyInfo[] actual = actualType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();
            PropertyInfo[] expected = expectedType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();
            JsonElement[] fields = schema.GetProperty("fields").EnumerateArray().ToArray();
            Assert.Equal(actual.Select(static property => (property.Name, property.PropertyType)), expected.Select(static property => (property.Name, property.PropertyType)));
            Assert.Equal(actual.Select(static property => property.Name), fields.Select(static field => field.GetProperty("name").GetString()!).Order(StringComparer.Ordinal), StringComparer.Ordinal);
            total += fields.Length;
            foreach (string key in new[] { "scope", "consistency", "resetPolicy", "historyOverflow" }) Assert.False(string.IsNullOrWhiteSpace(schema.GetProperty(key).GetString()), key);
            foreach (JsonElement field in fields)
            {
                foreach (string key in new[] { "name", "units", "definition", "overflow" }) Assert.False(string.IsNullOrWhiteSpace(field.GetProperty(key).GetString()), key);
                string[] parts = field.GetProperty("implementation").GetString()!.Split('#', 2);
                Assert.Equal(2, parts.Length);
                Assert.Contains(parts[1], File.ReadAllText(Path.Combine(root, "Supprocom.NativeAllocationManagement", parts[0])), StringComparison.Ordinal);
                VerifyTestReference(field.GetProperty("proof").GetString()!);
            }
        }
        Assert.Equal(47, total);
    }

    private static void VerifyTestReference(string reference)
    {
        string[] parts = reference.Split('.', 2);
        Assert.Equal(2, parts.Length);
        Type type = typeof(NativeOwnerDiagnosticContractTests).Assembly.GetType(typeof(NativeOwnerDiagnosticContractTests).Namespace + "." + parts[0], throwOnError: true)!;
        MethodInfo? method = type.GetMethod(parts[1], BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        Assert.True(method.IsDefined(typeof(FactAttribute)) || method.IsDefined(typeof(TheoryAttribute)), reference);
    }
}
