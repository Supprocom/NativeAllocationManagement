using System.Reflection;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Conformance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeBudgetDiagnosticContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void PublicOperationsMatchEveryBudgetField(int traceCapacity) => NativeBudgetDiagnosticOracle.Run(traceCapacity);

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void OrdinaryBackendFailureRollsBackEveryBudgetGauge(int traceCapacity)
    {
        NativeMemoryBudget budget = new(64, traceCapacity);
        NativeMemoryTestHooks.FailNextAllocation();
        try
        {
            Assert.Throws<NativeAllocationFailedException>(() => new NativeWorkspace<int>(budget, 2));
            NativeBudgetDiagnosticOracle.Expected expected = NativeBudgetDiagnosticOracle.Expected.Empty(budget.Id, 64, traceCapacity)
                with
            { PeakAdmittedBytes = 8, FailedAllocationCount = 1 };
            NativeBudgetDiagnosticOracle.Verify(budget, expected.WithEvents(2), "ordinary-backend-failure:rolled-back");
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ApplicationBackendFailureKeepsPendingAdmissionForRetry(int traceCapacity)
    {
        NativeMemoryBudget budget = new(16, traceCapacity);
        Assert.True(budget.TryReserve<int>(4, out NativeMemoryReservation<int>? permission, out _));
        NativeBudgetDiagnosticOracle.Expected expected = NativeBudgetDiagnosticOracle.Expected.Empty(budget.Id, 16, traceCapacity)
            with
        { ReservedBytes = 16, PeakAdmittedBytes = 16, FailedAllocationCount = 1 };
        try
        {
            using (NativeMemoryReservation<int> reservation = NativeMemoryReservation<int>.Move(ref permission))
            {
                NativeMemoryTestHooks.FailNextAllocation();
                Assert.Throws<NativeAllocationFailedException>(() => reservation.PrepareBacking());
                NativeBudgetDiagnosticOracle.Verify(budget, expected.WithEvents(3), "application-backend-failure:still-pending");
                reservation.PrepareBacking();
                expected = expected with
                {
                    ReservedBytes = 0,
                    CommittedBytes = 16,
                    PeakCommittedBytes = 16,
                    AllocationCount = 1,
                    AcquiredBackingBytes = 16,
                    ActiveAllocationCount = 1
                };
                NativeBudgetDiagnosticOracle.Verify(budget, expected.WithEvents(5), "application-backend-failure:retry-prepared");
            }
            expected = expected with { CommittedBytes = 0, ActiveAllocationCount = 0, FreeCount = 1 };
            NativeBudgetDiagnosticOracle.Verify(budget, expected.WithEvents(7), "application-backend-failure:cancelled-and-released");
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void IdentityExhaustionCannotWrapOrChangeExistingBudget()
    {
        NativeMemoryBudget existing = new(0);
        NativeBudgetDiagnosticOracle.Expected expected = NativeBudgetDiagnosticOracle.Expected.Empty(existing.Id, 0, 0);
        FieldInfo sequence = typeof(NativeMemoryBudget).GetField("_nextId", BindingFlags.Static | BindingFlags.NonPublic)!;
        long saved = (long)sequence.GetValue(null)!;
        // Test assembly disables parallel execution. Restore the process-local
        // identity sequence even if construction or verification fails.
        try
        {
            sequence.SetValue(null, long.MaxValue);
            Assert.Throws<OverflowException>(() => new NativeMemoryBudget(0));
            Assert.Equal(long.MaxValue, (long)sequence.GetValue(null)!);
            NativeBudgetDiagnosticOracle.Verify(existing, expected, "identity-exhaustion:existing-budget");
        }
        finally { sequence.SetValue(null, saved); }
        NativeMemoryBudget next = new(0);
        Assert.True(next.Id > existing.Id);
    }

    [Fact]
    public void InvalidConfigurationDoesNotCreateABudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeMemoryBudget(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeMemoryBudget(0, -1));
        NativeMemoryBudget zero = new(0);
        NativeBudgetDiagnosticOracle.Verify(zero, NativeBudgetDiagnosticOracle.Expected.Empty(zero.Id, 0, 0), "zero-configuration");
    }

    [Fact]
    public void BudgetContractInventoryMatchesEveryPublishedPropertyAndProof()
    {
        string root = RepositoryTestPaths.Root;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "conformance", "native-budget-diagnostic-contracts.json")));
        JsonElement contract = document.RootElement;
        Assert.Equal(typeof(NativeMemoryBudgetStatistics).FullName, contract.GetProperty("schema").GetString());
        Assert.False(contract.GetProperty("completeReleaseInventory").GetBoolean());
        foreach (string key in new[] { "scope", "consistency", "resetPolicy", "availability", "unreviewedScope" })
            Assert.False(string.IsNullOrWhiteSpace(contract.GetProperty(key).GetString()), key);
        Assert.True(File.Exists(Path.Combine(root, contract.GetProperty("oracleSource").GetString()!)));
        foreach (string key in new[] { "wholeSnapshotProof", "ordinaryFaultProof", "applicationFaultProof", "packageProof" })
            VerifyTestReference(contract.GetProperty(key).GetString()!);

        string[] actual = typeof(NativeMemoryBudgetStatistics).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static property => property.Name).Order(StringComparer.Ordinal).ToArray();
        string[] expected = typeof(NativeBudgetDiagnosticOracle.Expected).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static property => property.Name).Order(StringComparer.Ordinal).ToArray();
        JsonElement[] fields = contract.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(19, actual.Length);
        Assert.Equal(actual, expected);
        Assert.Equal(actual, fields.Select(static field => field.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray());
        foreach (JsonElement field in fields)
        {
            foreach (string key in new[] { "name", "units", "role", "definition", "overflow" })
                Assert.False(string.IsNullOrWhiteSpace(field.GetProperty(key).GetString()), key);
            JsonElement[] implementation = field.GetProperty("implementation").EnumerateArray().ToArray();
            Assert.NotEmpty(implementation);
            foreach (JsonElement link in implementation)
            {
                string[] parts = link.GetString()!.Split('#', 2);
                Assert.Equal(2, parts.Length);
                string path = Path.Combine(root, "Supprocom.NativeAllocationManagement", parts[0]);
                Assert.Contains(parts[1], File.ReadAllText(path), StringComparison.Ordinal);
            }
            JsonElement[] proofs = field.GetProperty("additionalProofs").EnumerateArray().ToArray();
            Assert.NotEmpty(proofs);
            foreach (JsonElement proof in proofs) VerifyTestReference(proof.GetString()!);
        }
    }

    private static void VerifyTestReference(string reference)
    {
        string[] parts = reference.Split('.', 2);
        Assert.Equal(2, parts.Length);
        Type type = typeof(NativeBudgetDiagnosticContractTests).Assembly.GetType(typeof(NativeBudgetDiagnosticContractTests).Namespace + "." + parts[0], throwOnError: true)!;
        MethodInfo? method = type.GetMethod(parts[1], BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        Assert.True(method.IsDefined(typeof(FactAttribute)) || method.IsDefined(typeof(TheoryAttribute)), reference);
    }
}
