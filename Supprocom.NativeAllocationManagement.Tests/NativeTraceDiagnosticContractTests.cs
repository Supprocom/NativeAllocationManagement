using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Conformance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeTraceDiagnosticContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(16)]
    public void DirectOwnershipTransitionsMatchEveryField(int capacity) => NativeTraceDiagnosticOracle.RunDirect(capacity);

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(16)]
    public void ReservationTransitionsMatchEveryField(int capacity) => NativeTraceDiagnosticOracle.RunReservation(capacity);

    [Fact]
    public void OwnerlessRefusalHasUnavailableIdentitiesNotInventedZeros() => NativeTraceDiagnosticOracle.RunOwnerlessRefusal();

    [Fact]
    public void RealGenerationZeroDiffersFromUnavailableGeneration() => NativeTraceDiagnosticOracle.RunGenerationZero();

    [Fact]
    public void FailedBackingPreparationRetainsPermissionChargeAndRetryRecordsOnlyRealAcquisition()
    {
        NativeMemoryBudget budget = new(8, traceCapacity: 16);
        long before = Stopwatch.GetTimestamp();
        Assert.True(budget.TryReserve<int>(2, out NativeMemoryReservation<int>? permission, out _));
        long owner = permission.Value.Id;
        try
        {
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => permission.Value.PrepareBacking());
            permission.Value.PrepareBacking();
        }
        finally { permission.Value.Dispose(); }
        NativeTraceDiagnosticOracle.Expected[] expected =
        [
            NativeTraceDiagnosticOracle.Event(1, before, budget.Id, owner, NativeMemoryTraceKind.ReservationAdmitted, 8, 0, 8, correlation: owner),
            NativeTraceDiagnosticOracle.Event(2, before, budget.Id, owner, NativeMemoryTraceKind.ReservationBackingFailed, 8, 0, 8, correlation: owner),
            NativeTraceDiagnosticOracle.Event(3, before, budget.Id, owner, NativeMemoryTraceKind.Allocated, 8, 8, 0, ordinal: 1),
            NativeTraceDiagnosticOracle.Event(4, before, budget.Id, owner, NativeMemoryTraceKind.ReservationBackingPrepared, 8, 8, 0, ordinal: 1, correlation: owner),
            NativeTraceDiagnosticOracle.Event(5, before, budget.Id, owner, NativeMemoryTraceKind.Released, 8, 0, 0, ordinal: 1),
            NativeTraceDiagnosticOracle.Event(6, before, budget.Id, owner, NativeMemoryTraceKind.ReservationCancelled, 8, 0, 0, ordinal: 1, correlation: owner)
        ];
        NativeTraceDiagnosticOracle.VerifyHistory(budget, expected, Stopwatch.GetTimestamp());
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(1, budget.CaptureStatistics().FailedAllocationCount);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void SequenceExhaustionDoesNotWrapOverwriteOrPreventActualCleanup()
    {
        NativeMemoryBudget budget = new(4, traceCapacity: 2);
        // Inject the non-reachable lifetime boundary; subsequent events and
        // storage transitions are genuine public operations, not synthetic.
        typeof(NativeMemoryBudget).GetField("_traceSequence", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(budget, long.MaxValue - 1);
        long before = Stopwatch.GetTimestamp();
        long owner;
        using (NativeWorkspace<int> workspace = new(budget, 1)) { owner = workspace.Id; }
        long after = Stopwatch.GetTimestamp();
        NativeMemoryTraceEvent[] events = new NativeMemoryTraceEvent[2];
        Assert.Equal(1, budget.CopyTraceTo(events));
        NativeTraceDiagnosticOracle.Verify(events[0], NativeTraceDiagnosticOracle.Event(long.MaxValue, before, budget.Id,
            owner, NativeMemoryTraceKind.Admitted, 4, 0, 4), after);
        NativeMemoryBudgetStatistics expected = budget.CaptureStatistics();
        Assert.True(expected.TraceOverflowed);
        Assert.False(expected.HistoryOverflowed);
        Assert.Equal(2, expected.DroppedTraceEventCount);
        Assert.Equal(1, expected.AllocationCount);
        Assert.Equal(1, expected.FreeCount);
        Assert.Equal(0, expected.CommittedBytes);
        Assert.Equal(0, expected.ReservedBytes);
        Assert.Equal(1, budget.CopyTraceTo(events));
        Assert.Equal(expected, budget.CaptureStatistics());
    }

    [Fact]
    public void IndependentClockBoundsRejectOutsideValuesAndNullIsNotGenerationZero()
    {
        // Comparator rejection proof only: these deliberately synthetic values
        // are not presented as measurements from an allocator.
        NativeMemoryTraceEvent value = new(1, 100, 3, null, NativeMemoryTraceKind.GenerationReleased, 0, 0, 0, 0)
        { CorrelationId = 0 };
        NativeTraceDiagnosticOracle.Expected expected = NativeTraceDiagnosticOracle.Event(1, 99, 3, null,
            NativeMemoryTraceKind.GenerationReleased, 0, 0, 0, correlation: 0, generation: 0);
        NativeTraceDiagnosticOracle.Verify(value, expected, 101);
        Assert.Throws<InvalidOperationException>(() => NativeTraceDiagnosticOracle.Verify(value, expected with { TimestampTicks = 101 }, 102));
        Assert.Throws<InvalidOperationException>(() => NativeTraceDiagnosticOracle.Verify(value, expected, 99));
        Assert.Throws<InvalidOperationException>(() => NativeTraceDiagnosticOracle.Verify(value, expected with { Generation = null }, 101));
        Assert.Throws<InvalidOperationException>(() => NativeTraceDiagnosticOracle.Verify(value, expected with { OwnerId = 0 }, 101));
        Assert.Throws<InvalidOperationException>(() => NativeTraceDiagnosticOracle.Verify(value, expected with { CommittedBytes = 1 }, 101));
    }

    [Fact]
    public void TraceInventoryMatchesEveryTypedPropertyDefinitionAndProof()
    {
        string root = RepositoryTestPaths.Root;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "conformance", "native-trace-diagnostic-contracts.json")));
        JsonElement registry = document.RootElement;
        Assert.False(registry.GetProperty("completeReleaseInventory").GetBoolean());
        Assert.True(File.Exists(Path.Combine(root, registry.GetProperty("oracleSource").GetString()!)));
        VerifyTestReference(registry.GetProperty("packageProof").GetString()!);
        foreach (string key in new[] { "scope", "consistency", "resetPolicy", "historyOverflow", "availability", "unreviewedScope", "oracleScope" })
            Assert.False(string.IsNullOrWhiteSpace(registry.GetProperty(key).GetString()), key);
        JsonElement[] fields = registry.GetProperty("fields").EnumerateArray().ToArray();
        PropertyInfo[] actual = typeof(NativeMemoryTraceEvent).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();
        PropertyInfo[] expected = typeof(NativeTraceDiagnosticOracle.Expected).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();
        Assert.Equal(12, fields.Length);
        Assert.Equal(actual.Select(static property => (property.Name, property.PropertyType)), expected.Select(static property => (property.Name, property.PropertyType)));
        Assert.Equal(actual.Select(static property => property.Name), fields.Select(static field => field.GetProperty("name").GetString()!).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (JsonElement field in fields)
        {
            foreach (string key in new[] { "name", "units", "definition", "overflow" }) Assert.False(string.IsNullOrWhiteSpace(field.GetProperty(key).GetString()), key);
            string[] parts = field.GetProperty("implementation").GetString()!.Split('#', 2);
            Assert.Equal(2, parts.Length);
            Assert.Contains(parts[1], File.ReadAllText(Path.Combine(root, "Supprocom.NativeAllocationManagement", parts[0])), StringComparison.Ordinal);
            VerifyTestReference(field.GetProperty("proof").GetString()!);
        }
    }

    private static void VerifyTestReference(string reference)
    {
        string[] parts = reference.Split('.', 2);
        Assert.Equal(2, parts.Length);
        Type type = typeof(NativeTraceDiagnosticContractTests).Assembly.GetType(typeof(NativeTraceDiagnosticContractTests).Namespace + "." + parts[0], throwOnError: true)!;
        MethodInfo? method = type.GetMethod(parts[1], BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        Assert.True(method.IsDefined(typeof(FactAttribute)) || method.IsDefined(typeof(TheoryAttribute)), reference);
    }
}
