using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Conformance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeSharingDiagnosticContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void PublicTransitionsMatchEverySharingField(int traceCapacity) => NativeSharingDiagnosticOracle.Run(traceCapacity);

    [Fact]
    public void FailedReturnAndRetryReconcileEveryField()
    {
        NativeMemoryTestHooks.Reset();
        try
        {
            NativeMemoryBudget budget = new(16);
            using NativeBuilder<int> builder = new(budget, 4);
            builder.Append(42);
            NativeTransfer<int>? source = builder.Complete();
            NativeShared<int> owner = NativeShared<int>.Create(ref source, new(1, 1));
            Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
            NativeSharingDiagnosticOracle.Expected expected = NativeSharingDiagnosticOracle.Expected.Initial(owner.Id, builder.Id, 16, 4, new(1, 1))
                with
            { WeakBindingCount = 1, PeakWeakBindingCount = 1, WeakCreationCount = 1 };
            try
            {
                NativeMemoryTestHooks.FailAtManagedPublicationBoundary(3);
                Assert.Throws<InvalidOperationException>(owner.Dispose);
                expected = expected with { StrongBindingCount = 0, Expired = true, ManagedBankBytes = 16, PayloadReturnFailureCount = 1 };
                NativeSharingDiagnosticOracle.Verify(weak.CaptureSnapshot(), expected, "failed-return:no-credit-or-lost-extent");
                Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
                Assert.True(weak.TryCompletePayloadReturn());
                expected = expected.AfterPayloadReturn();
                NativeSharingDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected, "successful-retry");
                Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
                Assert.True(owner.TryCompletePayloadReturn());
                NativeSharingDiagnosticOracle.Verify(weak.CaptureSnapshot(), expected, "idempotent-retry");
            }
            finally { weak.Dispose(); }
            expected = expected with { WeakBindingCount = 0, ManagedBankBytes = 0 };
            NativeSharingDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected, "all-observer-storage-dropped");
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void EnteredLastReleaseReconcilesEveryFieldBeforeAndAfterDrain()
    {
        NativeMemoryBudget budget = new(16);
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(42);
        NativeTransfer<int>? source = builder.Complete();
        NativeShared<int> owner = NativeShared<int>.Create(ref source, new(1, 1));
        Assert.True(owner.TryDowngrade(out NativeWeak<int> weak, out _));
        NativeSharingDiagnosticOracle.Expected expected = NativeSharingDiagnosticOracle.Expected.Initial(owner.Id, builder.Id, 16, 4, new(1, 1))
            with
        { WeakBindingCount = 1, PeakWeakBindingCount = 1, WeakCreationCount = 1 };
        try
        {
            owner.Access(view =>
            {
                NativeSharingDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected with { ActiveReadCount = 1 }, "read-entered");
                owner.Access(_ => NativeSharingDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected with { ActiveReadCount = 2 }, "nested-read-entered"));
                NativeSharingDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected with { ActiveReadCount = 1 }, "nested-read-ended");
                owner.Dispose();
                expected = expected with { StrongBindingCount = 0, ActiveReadCount = 1, Expired = true, ManagedBankBytes = 16 };
                NativeSharingDiagnosticOracle.Verify(weak.CaptureSnapshot(), expected, "last-release-before-drain");
                Assert.False(weak.TryCompletePayloadReturn());
                NativeSharingDiagnosticOracle.Verify(weak.CaptureSnapshot(), expected, "pending-read-is-not-a-cleanup-failure");
                Assert.Equal(42, view[0]);
                Assert.Equal(16, budget.CaptureStatistics().CommittedBytes);
            });
            expected = (expected with { ActiveReadCount = 0 }).AfterPayloadReturn();
            NativeSharingDiagnosticOracle.Verify(weak.CaptureSnapshot(), expected, "actual-drain-and-return");
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        }
        finally { weak.Dispose(); }
        NativeSharingDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected with { WeakBindingCount = 0, ManagedBankBytes = 0 }, "drained-final-observer");
    }

    [Fact]
    public void ProviderBackingIsObservedSeparatelyAndReleasedOnlyAtItsActualBoundary()
    {
        // Internal region-to-transfer construction exercises the actual provider
        // path; this is not a claim that a new public conversion API exists.
        using ProviderBuffer provider = new(128);
        using NativeConcurrentArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeOwnerKernel kernel = arena.KernelForInitialization;
        Assert.Equal((nuint)128, kernel.ReserveExternalMemory(provider, 0, 128));
        NativeRegionAllocation allocation = kernel.LeaseBumpInitialized(1, sizeof(int), sizeof(int), false, false,
            static (NativeLeaseWriter<int> writer) => writer.Write(42));
        NativeTransfer<int>? source = NativeTransfer<int>.Create(kernel, allocation, "contract-provider");
        NativeShared<int> owner = NativeShared<int>.Create(ref source, new(1, 0));
        NativeSharingDiagnosticOracle.Expected expected = NativeSharingDiagnosticOracle.Expected.Initial(owner.Id, kernel.Id, 0, 4, new(1, 0))
            with
        { BorrowedBackingBytes = 128 };
        NativeSharingDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected, "registered-provider-extent");
        provider.Dispose();
        Assert.Equal(0, provider.ReleaseCount);
        Assert.Equal(42, owner.Read(static view => view[0]));
        owner.Dispose();
        expected = expected.AfterPayloadReturn();
        NativeSharingDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected, "authority-return-not-provider-free");
        Assert.Equal(0, provider.ReleaseCount);
        arena.Dispose();
        Assert.Equal(1, provider.ReleaseCount);
        NativeSharingDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected, "actual-provider-release");
    }

    [Fact]
    public void IndependentBindingRepresentationMatchesTheRealElementExtent()
    {
        using NativeBuilder<int> builder = new(1);
        builder.Append(42);
        NativeTransfer<int>? source = builder.Complete();
        using NativeShared<int> owner = NativeShared<int>.Create(ref source, new(3, 2));
        Type slot = typeof(NativeSharingBindingBank).GetNestedType("Slot", BindingFlags.NonPublic)!;
        FieldInfo[] fields = slot.GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(new[] { typeof(long), typeof(int), typeof(bool) }, fields.Select(static field => field.FieldType).ToArray());
        Assert.Equal(0, Marshal.OffsetOf(slot, "Version").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf(slot, "Next").ToInt32());
        Assert.Equal(12, Marshal.OffsetOf(slot, "Live").ToInt32());
        Assert.Equal(NativeSharingDiagnosticOracle.BindingSlotBytes, Marshal.SizeOf(slot));
        Assert.Equal(5L * NativeSharingDiagnosticOracle.BindingSlotBytes, owner.CaptureSnapshot().ManagedBankBytes);
    }

    [Fact]
    public void SharingInventoryMatchesEveryPropertyDefinitionAndProof()
    {
        string root = RepositoryTestPaths.Root;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "conformance", "native-sharing-diagnostic-contracts.json")));
        JsonElement contract = document.RootElement;
        Assert.Equal(typeof(NativeSharingStatistics).FullName, contract.GetProperty("schema").GetString());
        Assert.False(contract.GetProperty("completeReleaseInventory").GetBoolean());
        foreach (string key in new[] { "scope", "consistency", "resetPolicy", "availability", "oracleScope", "unreviewedScope" })
            Assert.False(string.IsNullOrWhiteSpace(contract.GetProperty(key).GetString()), key);
        Assert.True(File.Exists(Path.Combine(root, contract.GetProperty("oracleSource").GetString()!)));
        foreach (string key in new[] { "wholeSnapshotProof", "ordinaryFaultProof", "packageProof" }) VerifyTestReference(contract.GetProperty(key).GetString()!);
        string[] actual = typeof(NativeSharingStatistics).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static property => property.Name).Order(StringComparer.Ordinal).ToArray();
        string[] expected = typeof(NativeSharingDiagnosticOracle.Expected).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static property => property.Name).Order(StringComparer.Ordinal).ToArray();
        JsonElement[] fields = contract.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(26, actual.Length);
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
        Type type = typeof(NativeSharingDiagnosticContractTests).Assembly.GetType(typeof(NativeSharingDiagnosticContractTests).Namespace + "." + parts[0], throwOnError: true)!;
        MethodInfo? method = type.GetMethod(parts[1], BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        Assert.True(method.IsDefined(typeof(FactAttribute)) || method.IsDefined(typeof(TheoryAttribute)), reference);
    }

    private sealed unsafe class ProviderBuffer : SafeBuffer
    {
        internal ProviderBuffer(nuint bytes) : base(ownsHandle: true)
        {
            void* storage = NativeMemory.AlignedAlloc(bytes, 64);
            if (storage == null) throw new InvalidOperationException("Provider allocation failed.");
            SetHandle((IntPtr)storage);
            Initialize(checked((ulong)bytes));
        }

        internal int ReleaseCount { get; private set; }

        protected override bool ReleaseHandle()
        {
            NativeMemory.AlignedFree((void*)handle);
            ReleaseCount++;
            return true;
        }
    }
}
