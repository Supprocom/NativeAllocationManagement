using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Conformance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeProcessDiagnosticContractTests
{
    [Fact]
    public void PublicOperationsMatchEveryProcessField() => NativeProcessDiagnosticOracle.Run();

    [Fact]
    public void FailedAcquisitionCannotInventAnyProcessField()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeProcessDiagnosticOracle.Expected expected = NativeProcessDiagnosticOracle.Expected.FromBaseline(NativeMemoryDiagnostics.Snapshot());
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => new NativeWorkspace<int>(preLease: 4));
            NativeProcessDiagnosticOracle.Verify(expected, "failed-acquisition:no-successful-event-or-clear");
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void FailedReplacementCreditsOnlyActualOldBackingRelease()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeProcessDiagnosticOracle.Expected expected = NativeProcessDiagnosticOracle.Expected.FromBaseline(NativeMemoryDiagnostics.Snapshot());
            using (NativeBuilder<int> builder = new(preLease: 2))
            {
                expected = expected.WithAcquisition(8);
                NativeMemoryTestHooks.FailNextAllocation();
                Assert.Throws<NativeAllocationFailedException>(() => builder.TryEnsureCapacity(3));
                expected = expected.WithRelease(8);
                NativeProcessDiagnosticOracle.Verify(expected, "failed-replacement:old-storage-actually-released");
            }
            NativeProcessDiagnosticOracle.Verify(expected, "failed-replacement:terminal-dispose-not-another-free");
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void RetiredProcessExtentIsObservedDuringBorrowAndRemovedAfterDrain()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            long extent = OperatingSystem.IsWindows() ? 4 : 64;
            using NativeConcurrentPool<int> pool = new(preLease: 1, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
            using ConcurrentPooled<int> lease = pool.Rent(1, static writer => writer.Write(42));
            lease.Access(view =>
            {
                pool.ReleaseLeasesToGarbageCollector();
                NativeMemoryStatistics retired = NativeMemoryDiagnostics.Snapshot();
                Assert.Equal(extent, retired.OutstandingNativeBytes);
                Assert.Equal(extent, retired.RetiredNativeBytes);
                Assert.Equal(extent, retired.RetainedNativeBytes);
                Assert.Equal(0, retired.DetachedNativeBytes);
                Assert.Equal(42, view[0]);
            });
            NativeMemoryStatistics drained = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(extent, drained.OutstandingNativeBytes);
            Assert.Equal(0, drained.RetiredNativeBytes);
            Assert.Equal(0, drained.FreeCount);
            pool.Dispose();
            NativeMemoryStatistics released = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(0, released.OutstandingNativeBytes);
            Assert.Equal(0, released.RetiredNativeBytes);
            Assert.Equal(0, released.RetainedNativeBytes);
            Assert.Equal(1, released.FreeCount);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void RetiredAndDetachedProcessSubsetsOverlapUntilEnteredBorrowDrains()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeMemoryBudget budget = DetachEnteredRetiredGeneration();
            Assert.Equal(0, NativeMemoryDiagnostics.Snapshot().RetiredNativeBytes);
            for (int attempt = 0; attempt < 8 && budget.CaptureStatistics().CommittedBytes != 0; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            NativeMemoryStatistics released = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(0, released.OutstandingNativeBytes);
            Assert.Equal(0, released.DetachedNativeBytes);
            Assert.Equal(0, released.RetiredNativeBytes);
            Assert.Equal(0, released.RetainedNativeBytes);
            Assert.Equal(1, released.FreeCount);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeMemoryBudget DetachEnteredRetiredGeneration()
    {
        long extent = OperatingSystem.IsWindows() ? 4 : 64;
        NativeMemoryBudget budget = new(128);
        using NativeConcurrentPool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToNativeMemory, false);
        using ConcurrentPooled<int> lease = pool.Rent(1, static writer => writer.Write(42));
        Assert.Equal(extent, budget.CaptureStatistics().CommittedBytes);
        lease.Access(view =>
        {
            pool.ReleaseLeasesToGarbageCollector();
            Assert.Equal(extent, NativeMemoryDiagnostics.Snapshot().RetiredNativeBytes);
            pool.ReturnMemoryToGarbageCollector();
            NativeMemoryStatistics overlapping = NativeMemoryDiagnostics.Snapshot();
            Assert.Equal(extent, overlapping.OutstandingNativeBytes);
            Assert.Equal(extent, overlapping.RetiredNativeBytes);
            Assert.Equal(extent, overlapping.DetachedNativeBytes);
            Assert.Equal(0, overlapping.RetainedNativeBytes);
            Assert.Equal(0, overlapping.FreeCount);
            Assert.Equal(42, view[0]);
        });
        return budget;
    }

    [Fact]
    public void ProcessContractInventoryMatchesEveryPublishedPropertyAndProof()
    {
        string root = RepositoryTestPaths.Root;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "conformance", "native-process-diagnostic-contracts.json")));
        JsonElement contract = document.RootElement;
        Assert.Equal(typeof(NativeMemoryStatistics).FullName, contract.GetProperty("schema").GetString());
        Assert.False(contract.GetProperty("completeReleaseInventory").GetBoolean());
        foreach (string key in new[] { "scope", "consistency", "resetPolicy", "availability", "oracleScope", "unreviewedScope" })
            Assert.False(string.IsNullOrWhiteSpace(contract.GetProperty(key).GetString()), key);
        Assert.True(File.Exists(Path.Combine(root, contract.GetProperty("oracleSource").GetString()!)));
        foreach (string key in new[] { "wholeSnapshotProof", "ordinaryFaultProof", "reallocationFaultProof", "packageProof" })
            VerifyTestReference(contract.GetProperty(key).GetString()!);
        string[] actual = typeof(NativeMemoryStatistics).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static property => property.Name).Order(StringComparer.Ordinal).ToArray();
        string[] expected = typeof(NativeProcessDiagnosticOracle.Expected).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static property => property.Name).Order(StringComparer.Ordinal).ToArray();
        JsonElement[] fields = contract.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(20, actual.Length);
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
                Assert.Contains(parts[1], File.ReadAllText(Path.Combine(root, "Supprocom.NativeAllocationManagement", parts[0])), StringComparison.Ordinal);
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
        Type type = typeof(NativeProcessDiagnosticContractTests).Assembly.GetType(typeof(NativeProcessDiagnosticContractTests).Namespace + "." + parts[0], throwOnError: true)!;
        MethodInfo? method = type.GetMethod(parts[1], BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        Assert.True(method.IsDefined(typeof(FactAttribute)) || method.IsDefined(typeof(TheoryAttribute)), reference);
    }
}
