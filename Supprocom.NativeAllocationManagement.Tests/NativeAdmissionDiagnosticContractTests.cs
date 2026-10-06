using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Conformance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeAdmissionDiagnosticContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void PublicTransitionsMatchEveryAdmissionReservationAndUniqueField(int traceCapacity) => NativeAdmissionDiagnosticOracle.Run(traceCapacity);

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void OrdinaryUniqueTransitionsMatchEveryField(int traceCapacity) => NativeAdmissionDiagnosticOracle.RunOrdinaryUnique(traceCapacity);

    [Fact]
    public void StaleReservationObservationFollowsActivationAndUniqueMovementWithoutReopeningPermission()
    {
        NativeMemoryBudget budget = new(12);
        Assert.True(budget.TryReserve<int>(3, out NativeMemoryReservation<int>? source, out _));
        NativeMemoryReservation<int> stale = source.Value;
        NativeAdmissionDiagnosticOracle.Reservation expected = NativeAdmissionDiagnosticOracle.Reservation.Pending(budget.Id, stale.Id, 3, 12);
        NativeMemoryReservation<int>? moved = NativeMemoryReservation<int>.Move(ref source);
        try
        {
            expected = expected with { AuthorityVersion = 2, BindingIsActive = false, ReservationMoveCount = 1 };
            NativeAdmissionDiagnosticOracle.Verify(stale.CaptureSnapshot(), expected, "stale-permission-after-move");
            moved.Value.PrepareBacking();
            NativeTransfer<int>? unique = NativeMemoryReservation<int>.Activate(ref moved, static writer => writer.Fill(42));
            expected = expected with
            {
                Outcome = NativeMemoryReservationOutcome.Activated,
                ReservedBytes = 0,
                OwnedBackingBytes = 12,
                PeakOwnedBackingBytes = 12,
                BackingIsPrepared = true,
                HasReservationReturnObligation = false
            };
            try
            {
                NativeAdmissionDiagnosticOracle.Verify(stale.CaptureSnapshot(), expected, "same-control-after-activation");
                NativeTransfer<int> destination = NativeTransfer<int>.Move(ref unique);
                expected = expected with { AuthorityVersion = 3 };
                try
                {
                    NativeAdmissionDiagnosticOracle.Verify(stale.CaptureSnapshot(), expected, "unique-move-does-not-count-as-permission-move");
                    Assert.Throws<InvalidOperationException>(stale.Dispose);
                    Assert.Throws<InvalidOperationException>(() => stale.PrepareBacking());
                    Assert.Equal(42, destination.Read(static view => view[2]));
                }
                finally { destination.Dispose(); }
                NativeAdmissionDiagnosticOracle.Verify(stale.CaptureSnapshot(), expected with { OwnedBackingBytes = 0 }, "same-control-after-unique-return");
                Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            }
            finally { unique?.Dispose(); }
        }
        finally { moved?.Dispose(); }
    }

    [Fact]
    public void BackendFailureAndRetryMatchEveryAdmissionAndReservationField()
    {
        NativeMemoryTestHooks.Reset();
        NativeMemoryBudget budget = new(12);
        Assert.True(budget.TryReserve<int>(3, out NativeMemoryReservation<int>? source, out _));
        NativeMemoryReservation<int> permission = NativeMemoryReservation<int>.Move(ref source);
        NativeAdmissionDiagnosticOracle.Reservation reservation = NativeAdmissionDiagnosticOracle.Reservation.Pending(budget.Id, permission.Id, 3, 12)
            with
        { BindingVersion = 2, AuthorityVersion = 2, ReservationMoveCount = 1, BackingPreparationFailureCount = 1 };
        NativeAdmissionDiagnosticOracle.Admission admission = NativeAdmissionDiagnosticOracle.Admission.Empty(budget.Id, 12) with
        {
            OutstandingReservationCount = 1,
            PeakOutstandingReservationCount = 1,
            PendingBytes = 12,
            PeakPendingBytes = 12,
            AdmittedReservationCount = 1,
            LedgerFieldBytes = 120,
            BackingPreparationFailureCount = 1,
            BackendAllocationFailureCount = 1
        };
        try
        {
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => permission.PrepareBacking());
            NativeAdmissionDiagnosticOracle.Verify(permission.CaptureSnapshot(), reservation, "backend-failed-permission-preserved");
            NativeAdmissionDiagnosticOracle.Verify(budget.CaptureAdmissionStatistics(), admission, "backend-failed-domain");
            permission.PrepareBacking();
            reservation = reservation with
            {
                Outcome = NativeMemoryReservationOutcome.Prepared,
                ReservedBytes = 0,
                OwnedBackingBytes = 12,
                PeakOwnedBackingBytes = 12,
                BackingIsPrepared = true
            };
            admission = admission with
            { PendingBytes = 0, PreparedUnpublishedBytes = 12, PeakPreparedUnpublishedBytes = 12, BackingPreparationCount = 1 };
            NativeAdmissionDiagnosticOracle.Verify(permission.CaptureSnapshot(), reservation, "successful-retry");
            NativeAdmissionDiagnosticOracle.Verify(budget.CaptureAdmissionStatistics(), admission, "successful-retry-domain");
            permission.Dispose();
            NativeAdmissionDiagnosticOracle.Verify(permission.CaptureSnapshot(), reservation with
            { Outcome = NativeMemoryReservationOutcome.Cancelled, BindingIsActive = false, OwnedBackingBytes = 0, HasReservationReturnObligation = false }, "actual-return");
            NativeAdmissionDiagnosticOracle.Verify(budget.CaptureAdmissionStatistics(), admission with
            { OutstandingReservationCount = 0, PreparedUnpublishedBytes = 0, CancelledReservationCount = 1 }, "actual-return-domain");
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
            if (permission.CaptureSnapshot().BindingIsActive) permission.Dispose();
        }
    }

    [Fact]
    public void IncompleteInitializerAndFailedCleanupKeepEveryObligationUntilRetry()
    {
        NativeMemoryTestHooks.Reset();
        NativeMemoryBudget budget = new(12);
        Assert.True(budget.TryReserve<int>(3, out NativeMemoryReservation<int>? source, out _));
        NativeMemoryReservation<int> observed = source.Value;
        try
        {
            NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
            AggregateException failure = Assert.Throws<AggregateException>(() => NativeMemoryReservation<int>.Activate(ref source, static writer => writer.Write(42)));
            Assert.IsType<InvalidOperationException>(failure.InnerExceptions[0]);
            Assert.IsType<InvalidOperationException>(failure.InnerExceptions[1]);
            Assert.Null(source);
            NativeAdmissionDiagnosticOracle.Reservation reservation = NativeAdmissionDiagnosticOracle.Reservation.Pending(budget.Id, observed.Id, 3, 12) with
            {
                Outcome = NativeMemoryReservationOutcome.InitializationFailed,
                BindingIsActive = false,
                ReservedBytes = 0,
                OwnedBackingBytes = 12,
                PeakOwnedBackingBytes = 12,
                BackingIsPrepared = true,
                ReturnFailureCount = 1
            };
            NativeAdmissionDiagnosticOracle.Admission admission = NativeAdmissionDiagnosticOracle.Admission.Empty(budget.Id, 12) with
            {
                OutstandingReservationCount = 1,
                PeakOutstandingReservationCount = 1,
                PeakPendingBytes = 12,
                PreparedUnpublishedBytes = 12,
                PeakPreparedUnpublishedBytes = 12,
                AdmittedReservationCount = 1,
                BackingPreparationCount = 1,
                InitializationFailureCount = 1,
                ReturnFailureCount = 1,
                LedgerFieldBytes = 120
            };
            NativeAdmissionDiagnosticOracle.Verify(observed.CaptureSnapshot(), reservation, "failed-consumed-cleanup");
            NativeAdmissionDiagnosticOracle.Verify(budget.CaptureAdmissionStatistics(), admission, "failed-consumed-domain");
            Assert.Equal(12, budget.CaptureStatistics().CommittedBytes);
            Assert.True(observed.TryCompletePayloadReturn());
            NativeAdmissionDiagnosticOracle.Verify(observed.CaptureSnapshot(), reservation with
            { OwnedBackingBytes = 0, HasReservationReturnObligation = false }, "terminal-retry");
            NativeAdmissionDiagnosticOracle.Verify(budget.CaptureAdmissionStatistics(), admission with
            { OutstandingReservationCount = 0, PreparedUnpublishedBytes = 0 }, "terminal-retry-domain");
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(1, budget.CaptureStatistics().FreeCount);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
            source?.Dispose();
            observed.TryCompletePayloadReturn();
        }
    }

    [Fact]
    public void UniqueReturnFailureAndNestedBorrowMatchEveryField()
    {
        NativeMemoryTestHooks.Reset();
        NativeMemoryBudget budget = new(16);
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(42);
        NativeTransfer<int> owner = builder.Complete();
        NativeAdmissionDiagnosticOracle.Unique expected = NativeAdmissionDiagnosticOracle.Unique.Initial(builder.Id, 16, 4);
        try
        {
            owner.Access(view =>
            {
                NativeAdmissionDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected with { ActiveBorrowCount = 1, PeakBorrowCount = 1 }, "first-entry");
                owner.Access(_ => NativeAdmissionDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected with { ActiveBorrowCount = 2, PeakBorrowCount = 2 }, "nested-entry"));
                NativeAdmissionDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected with { ActiveBorrowCount = 1, PeakBorrowCount = 2 }, "nested-exit");
                Assert.Equal(42, view[0]);
                Assert.Throws<InvalidOperationException>(owner.Dispose);
                NativeAdmissionDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected with { ActiveBorrowCount = 1, PeakBorrowCount = 2 }, "borrow-refusal-is-not-return-failure");
            });
            expected = expected with { PeakBorrowCount = 2, PayloadReturnFailureCount = 1 };
            NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
            Assert.Throws<InvalidOperationException>(owner.Dispose);
            NativeAdmissionDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected, "failed-return-still-active");
            owner.Dispose();
            NativeAdmissionDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected.Returned(), "successful-return-with-history");
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        }
        finally
        {
            NativeMemoryTestHooks.Reset();
            if (owner.CaptureSnapshot().BindingIsActive) owner.Dispose();
        }
    }

    [Fact]
    public void ProviderUniqueSnapshotCountsRegisteredExtentAndActualAuthorityReturn()
    {
        using ProviderBuffer provider = new(128);
        using NativeConcurrentArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeOwnerKernel kernel = arena.KernelForInitialization;
        Assert.Equal((nuint)128, kernel.ReserveExternalMemory(provider, 0, 128));
        NativeRegionAllocation allocation = kernel.LeaseBumpInitialized(1, sizeof(int), sizeof(int), false, false,
            static (NativeLeaseWriter<int> writer) => writer.Write(42));
        NativeTransfer<int> owner = NativeTransfer<int>.Create(kernel, allocation, "unique-contract-provider");
        NativeAdmissionDiagnosticOracle.Unique expected = NativeAdmissionDiagnosticOracle.Unique.Initial(kernel.Id, 0, 4) with
        { AllocationId = allocation.AllocationId, BorrowedBackingBytes = 128 };
        try
        {
            NativeAdmissionDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected, "provider-registered");
            provider.Dispose();
            Assert.Equal(0, provider.ReleaseCount);
            Assert.Equal(42, owner.Read(static view => view[0]));
            expected = expected with { PeakBorrowCount = 1 };
        }
        finally { owner.Dispose(); }
        NativeAdmissionDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected.Returned(), "provider-authority-returned");
        Assert.Equal(0, provider.ReleaseCount);
        arena.Dispose();
        Assert.Equal(1, provider.ReleaseCount);
    }

    [Fact]
    public void AdmissionReservationAndUniqueInventoriesMatchEveryDefinitionAndProof()
    {
        string root = RepositoryTestPaths.Root;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "conformance", "native-admission-diagnostic-contracts.json")));
        JsonElement registry = document.RootElement;
        Assert.False(registry.GetProperty("completeReleaseInventory").GetBoolean());
        Assert.True(File.Exists(Path.Combine(root, registry.GetProperty("oracleSource").GetString()!)));
        VerifyTestReference(registry.GetProperty("packageProof").GetString()!);
        foreach (string key in new[] { "availability", "unreviewedScope", "oracleScope" }) Assert.False(string.IsNullOrWhiteSpace(registry.GetProperty(key).GetString()), key);
        JsonElement[] schemas = registry.GetProperty("schemas").EnumerateArray().ToArray();
        Assert.Equal(3, schemas.Length);
        int total = 0;
        foreach (JsonElement schema in schemas)
        {
            Type actualType = typeof(NativeMemoryBudget).Assembly.GetType(schema.GetProperty("schema").GetString()!, throwOnError: true)!;
            Type expectedType = typeof(NativeAdmissionDiagnosticOracle).GetNestedType(schema.GetProperty("expected").GetString()!, BindingFlags.NonPublic)!;
            string[] actual = actualType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(static property => property.Name).Order(StringComparer.Ordinal).ToArray();
            string[] expected = expectedType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(static property => property.Name).Order(StringComparer.Ordinal).ToArray();
            JsonElement[] fields = schema.GetProperty("fields").EnumerateArray().ToArray();
            Assert.Equal(actual, expected);
            Assert.Equal(actual, fields.Select(static field => field.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray());
            total += fields.Length;
            foreach (string key in new[] { "scope", "consistency", "resetPolicy", "historyOverflow" }) Assert.False(string.IsNullOrWhiteSpace(schema.GetProperty(key).GetString()), key);
            foreach (string key in new[] { "wholeSnapshotProof", "faultProof" }) VerifyTestReference(schema.GetProperty(key).GetString()!);
            foreach (JsonElement field in fields)
            {
                foreach (string key in new[] { "name", "units", "definition", "overflow" }) Assert.False(string.IsNullOrWhiteSpace(field.GetProperty(key).GetString()), key);
                string[] parts = field.GetProperty("implementation").GetString()!.Split('#', 2);
                Assert.Equal(2, parts.Length);
                Assert.Contains(parts[1], File.ReadAllText(Path.Combine(root, "Supprocom.NativeAllocationManagement", parts[0])), StringComparison.Ordinal);
                VerifyTestReference(field.GetProperty("proof").GetString()!);
            }
        }
        Assert.Equal(60, total);
    }

    private static void VerifyTestReference(string reference)
    {
        string[] parts = reference.Split('.', 2);
        Assert.Equal(2, parts.Length);
        Type type = typeof(NativeAdmissionDiagnosticContractTests).Assembly.GetType(typeof(NativeAdmissionDiagnosticContractTests).Namespace + "." + parts[0], throwOnError: true)!;
        MethodInfo? method = type.GetMethod(parts[1], BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        Assert.True(method.IsDefined(typeof(FactAttribute)) || method.IsDefined(typeof(TheoryAttribute)), reference);
    }

    private sealed unsafe class ProviderBuffer : SafeBuffer
    {
        internal ProviderBuffer(nuint bytes) : base(ownsHandle: true)
        {
            SetHandle((IntPtr)NativeMemory.AlignedAlloc(bytes, 64));
            if (IsInvalid) throw new InvalidOperationException("The test provider could not acquire its backing.");
            Initialize((ulong)bytes);
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
