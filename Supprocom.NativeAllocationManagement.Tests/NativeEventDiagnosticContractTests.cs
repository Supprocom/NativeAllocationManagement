using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Conformance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeEventDiagnosticContractTests
{
    [Fact]
    public void EveryActualEventKindHasACompleteDefinitionAndOrderedHistoryProof()
    {
        string root = RepositoryTestPaths.Root;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "conformance", "native-event-diagnostic-contracts.json")));
        JsonElement registry = document.RootElement;
        Assert.False(registry.GetProperty("completeReleaseInventory").GetBoolean());
        Assert.True(File.Exists(Path.Combine(root, registry.GetProperty("oracleSource").GetString()!)));
        foreach (string key in new[] { "consistency", "resetPolicy", "identityPolicy", "units", "overflow", "unreviewedScope" })
            Assert.False(string.IsNullOrWhiteSpace(registry.GetProperty(key).GetString()), key);
        JsonElement[] entries = registry.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(37, entries.Length);
        Assert.Equal(Enum.GetNames<NativeMemoryTraceKind>().Order(StringComparer.Ordinal),
            entries.Select(static entry => entry.GetProperty("name").GetString()!).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (JsonElement entry in entries)
        {
            foreach (string key in new[] { "definition", "negative" }) Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty(key).GetString()), key);
            string[] source = entry.GetProperty("implementation").GetString()!.Split('#', 2);
            Assert.Equal(2, source.Length);
            Assert.Contains(source[1], File.ReadAllText(Path.Combine(root, "Supprocom.NativeAllocationManagement", source[0])), StringComparison.Ordinal);
            MethodInfo? proof = GetType().GetMethod(entry.GetProperty("proof").GetString()!, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(proof);
            Assert.True(proof.IsDefined(typeof(FactAttribute)) || proof.IsDefined(typeof(TheoryAttribute)));
        }
        MethodInfo? packageProof = typeof(PackageSmokeTests).GetMethod(registry.GetProperty("packageProof").GetString()!.Split('.', 2)[1]);
        Assert.NotNull(packageProof);
        Assert.True(packageProof.IsDefined(typeof(FactAttribute)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public void OrdinaryBackingTransitionsVerifyEveryEventField(int capacity) => NativeEventDiagnosticOracle.RunOrdinary(capacity);
    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public void PermissionMovementAndActivationVerifyEveryEventField(int capacity) => NativeEventDiagnosticOracle.RunPermission(capacity);
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(64, false)]
    [InlineData(64, true)]
    public void PendingAndPreparedCancellationVerifyEveryEventField(int capacity, bool prepared) => NativeEventDiagnosticOracle.RunCancelled(capacity, prepared);
    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public void LayoutPreparationInitializationAndDetachVerifyEveryEventField(int capacity) => NativeEventDiagnosticOracle.RunLayout(capacity);
    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public void SharingUpgradeRefusalAndReturnVerifyEveryEventField(int capacity) => NativeEventDiagnosticOracle.RunSharing(capacity);

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public void PreparedPageReuseAndWholeIdleTrimVerifyEveryEventField(int capacity) => NativeEventDiagnosticOracle.RunPreparedPages(capacity);

    [Fact]
    public void EnteredUniqueMovementRetiresUntilTheActualBorrowEnds()
    {
        NativeMemoryBudget budget = new(8, 64);
        long before = Stopwatch.GetTimestamp();
        using NativeBuilder<int> builder = new(budget, 2);
        builder.Append([17, 19]);
        NativeTransfer<int>? source = builder.Complete();
        NativeTransfer<int> numericAlias = source.Value;
        numericAlias.Access(view =>
        {
            Assert.Throws<InvalidOperationException>(() => NativeTransfer<int>.Move(ref source));
            Assert.Null(source);
            Assert.Equal(36, view[0] + view[1]);
            Assert.Equal(8, budget.CaptureStatistics().CommittedBytes);
        });
        NativeEventDiagnosticOracle.Verify(budget, before,
        [
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Admitted, 8, 0, 8, builder.Id),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Allocated, 8, 8, 0, builder.Id, ordinal: 1),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Retired, 8, 8, 0, builder.Id, ordinal: 1, correlation: builder.Id),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Released, 8, 0, 0, builder.Id, ordinal: 1),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.UniqueReturned, 8, 0, 0, builder.Id, ordinal: 1, correlation: builder.Id)
        ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetiredAndQuarantinedGenerationsVerifyEveryEventField(bool quarantine)
    {
        nuint extent = OperatingSystem.IsWindows() ? 4u : 64u;
        NativeMemoryBudget budget = new(128, 64);
        long before = Stopwatch.GetTimestamp();
        NativeConcurrentPool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToNativeMemory, false);
        long owner = pool.Id;
        try
        {
            using ConcurrentPooled<int> value = pool.Rent(1, static writer => writer.Write(42));
            NativeAllocationQuarantinedException? failure = null;
            try
            {
                value.Access(view =>
                {
                    pool.ReleaseLeasesToGarbageCollector();
                    Assert.Equal(42, view[0]);
                    Assert.Equal((long)extent, budget.CaptureStatistics().CommittedBytes);
                    if (quarantine) NativeMemoryTestHooks.FailAfterCommitBoundary(1);
                });
            }
            catch (NativeAllocationQuarantinedException exception) { failure = exception; }
            if (quarantine) Assert.IsType<NativeAllocationQuarantinedException>(failure);
            else Assert.Null(failure);
        }
        finally { NativeMemoryTestHooks.Reset(); pool.Dispose(); }
        List<NativeTraceDiagnosticOracle.Expected> expected =
        [
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Admitted, extent, 0, (long)extent, owner),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Allocated, extent, (long)extent, 0, owner, ordinal: 1),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.GenerationRetired, extent, (long)extent, 0, owner, correlation: 0, generation: 0)
        ];
        if (quarantine)
        {
            expected.Add(NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.GenerationQuarantined, extent, (long)extent, 0, owner, correlation: 0, generation: 0));
            expected.Add(NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.GenerationReleased, 0, (long)extent, 0, owner, correlation: 1, generation: 1));
            expected.Add(NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Released, extent, 0, 0, owner, ordinal: 1));
            expected.Add(NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.GenerationReleased, extent, 0, 0, owner, correlation: 0, generation: 0));
        }
        else
        {
            expected.Add(NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.GenerationReleased, 0, (long)extent, 0, owner, correlation: 0, generation: 0));
            expected.Add(NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Released, extent, 0, 0, owner, ordinal: 1));
            expected.Add(NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.GenerationReleased, extent, 0, 0, owner, correlation: 1, generation: 1));
        }
        NativeEventDiagnosticOracle.Verify(budget, before, expected.ToArray());
    }

    [Fact]
    public void DetachedGenerationCompletesOneOfTheTwoLegalFinalizerOrders()
    {
        nuint extent = OperatingSystem.IsWindows() ? 4u : 64u;
        NativeMemoryBudget budget = new(128, 64);
        long before = Stopwatch.GetTimestamp();
        long owner = DetachGeneration(budget);
        for (int attempt = 0; attempt < 8 && budget.CaptureStatistics().CommittedBytes != 0; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        NativeMemoryTraceEvent[] actual = new NativeMemoryTraceEvent[5];
        Assert.Equal(5, budget.CopyTraceTo(actual));
        long after = Stopwatch.GetTimestamp();
        NativeTraceDiagnosticOracle.Expected[] expected =
        [
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Admitted, extent, 0, (long)extent, owner),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Allocated, extent, (long)extent, 0, owner, ordinal: 1),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.GenerationDetached, extent, (long)extent, 0, owner, correlation: 0, generation: 0),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Released, extent, 0, 0, owner, ordinal: 1),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.GenerationReleased, extent, 0, 0, owner, correlation: 0, generation: 0)
        ];
        for (int index = 0; index < actual.Length; index++)
        {
            NativeTraceDiagnosticOracle.Expected declared = expected[index] with { Sequence = index + 1, TimestampTicks = before, BudgetId = budget.Id };
            if (index == 4)
            {
                // Generation and segment finalizers are independently scheduled.
                // Both complete, predeclared legal models must retain the exact
                // physical free event; zero does not mean fabricated release.
                Assert.True(actual[index].RequestedBytes == 0 || actual[index].RequestedBytes == extent);
                Exception? retainedModel = Record.Exception(() => NativeTraceDiagnosticOracle.Verify(actual[index], declared, after));
                if (retainedModel is not null) NativeTraceDiagnosticOracle.Verify(actual[index], declared with { RequestedBytes = 0 }, after);
            }
            else NativeTraceDiagnosticOracle.Verify(actual[index], declared, after);
            if (index != 0) Assert.True(actual[index].TimestampTicks >= actual[index - 1].TimestampTicks);
        }
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
        GC.KeepAlive(budget);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long DetachGeneration(NativeMemoryBudget budget)
    {
        using NativeConcurrentPool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToGarbageCollector, false);
        long owner = pool.Id;
        pool.ReturnMemoryToGarbageCollector();
        return owner;
    }

    [Fact]
    public void FailedBackendAcquisitionHasNoInventedAllocatedOrReleaseEvent()
    {
        NativeMemoryBudget budget = new(8, 64);
        long before = Stopwatch.GetTimestamp();
        using NativeBuilder<int> builder = new(budget, 0);
        long owner = builder.Id;
        try
        {
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => builder.TryEnsureCapacity(2));
            NativeEventDiagnosticOracle.Verify(budget, before,
            [
                NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Admitted, 8, 0, 8, owner),
                NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.AcquisitionFailed, 8, 0, 0, owner)
            ]);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void MetadataPreparationFailureIsOwnerlessAndUncharged()
    {
        NativeMemoryBudget budget = new(8, 64);
        long before = Stopwatch.GetTimestamp();
        try
        {
            NativeMemoryTestHooks.FailAtManagedPublicationBoundary(6);
            Assert.Throws<InvalidOperationException>(() => budget.TryReserve<int>(2, out _, out _));
            NativeEventDiagnosticOracle.Verify(budget, before,
                [NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.ReservationPreparationFailed, 8, 0, 0, null)]);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void FailedPreparedBackingThenRetryRecordsOnlyActualAcquisition()
    {
        NativeMemoryBudget budget = new(8, 64);
        long before = Stopwatch.GetTimestamp();
        Assert.True(budget.TryReserve<int>(2, out NativeMemoryReservation<int>? permission, out _));
        long owner = permission.Value.Id;
        try
        {
            NativeMemoryTestHooks.FailNextAllocation();
            Assert.Throws<NativeAllocationFailedException>(() => permission.Value.PrepareBacking());
            permission.Value.PrepareBacking();
        }
        finally { permission.Value.Dispose(); NativeMemoryTestHooks.Reset(); }
        NativeEventDiagnosticOracle.Verify(budget, before,
        [
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.ReservationAdmitted, 8, 0, 8, owner, correlation: owner),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.ReservationBackingFailed, 8, 0, 8, owner, correlation: owner),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Allocated, 8, 8, 0, owner, ordinal: 1),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.ReservationBackingPrepared, 8, 8, 0, owner, ordinal: 1, correlation: owner),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Released, 8, 0, 0, owner, ordinal: 1),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.ReservationCancelled, 8, 0, 0, owner, ordinal: 1, correlation: owner)
        ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedProducerAndFailedReturnPreserveExactEventTiming(bool failReturn)
    {
        NativeMemoryBudget budget = new(8, 64);
        long before = Stopwatch.GetTimestamp();
        Assert.True(budget.TryReserve<int>(2, out NativeMemoryReservation<int>? permission, out _));
        long owner = permission.Value.Id;
        NativeMemoryReservation<int> numericAlias = permission.Value;
        try
        {
            Exception? failure = Record.Exception(() => NativeMemoryReservation<int>.Activate(ref permission, _ =>
            {
                if (failReturn) NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
                throw new InvalidOperationException("Injected producer failure.");
            }));
            if (failReturn) Assert.IsType<AggregateException>(failure);
            else Assert.IsType<InvalidOperationException>(failure);
            Assert.Null(permission);
            if (failReturn)
            {
                Assert.Equal(8, budget.CaptureStatistics().CommittedBytes);
                Assert.True(numericAlias.TryCompletePayloadReturn());
            }
        }
        finally { NativeMemoryTestHooks.Reset(); }
        List<NativeTraceDiagnosticOracle.Expected> expected =
        [
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.ReservationAdmitted, 8, 0, 8, owner, correlation: owner),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Allocated, 8, 8, 0, owner, ordinal: 1),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.ReservationBackingPrepared, 8, 8, 0, owner, ordinal: 1, correlation: owner),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.ReservationInitializationFailed, 8, 8, 0, owner, ordinal: 1, correlation: owner)
        ];
        if (failReturn) expected.Add(NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.ReservationReturnFailed, 8, 8, 0, owner, ordinal: 1, correlation: owner));
        expected.Add(NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Released, 8, 0, 0, owner, ordinal: 1));
        expected.Add(NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.ReservationReturned, 8, 0, 0, owner, ordinal: 1, correlation: owner));
        NativeEventDiagnosticOracle.Verify(budget, before, expected.ToArray());
    }

    [Fact]
    public void CheckedAuthorityExhaustionReturnsPermissionWithoutWrappedMove()
    {
        NativeMemoryBudget budget = new(8, 64);
        long before = Stopwatch.GetTimestamp();
        Assert.True(budget.TryReserve<int>(2, out NativeMemoryReservation<int>? permission, out _));
        long owner = permission.Value.Id;
        var control = (NativeMemoryReservationControl<int>)permission.Value.ControlForTest!;
        typeof(NativeTransferControl<int>).GetField("_authorityVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(control, long.MaxValue);
        permission = new NativeMemoryReservation<int>(control, long.MaxValue);
        Assert.Throws<OverflowException>(() => NativeMemoryReservation<int>.Move(ref permission));
        Assert.Null(permission);
        NativeEventDiagnosticOracle.Verify(budget, before,
        [
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.ReservationAdmitted, 8, 0, 8, owner, correlation: owner),
            NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.ReservationAuthorityExhausted, 8, 0, 0, owner, correlation: owner)
        ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmergencyPermissionReturnRecordsActualAbandonment(bool prepared)
    {
        NativeMemoryBudget budget = new(8, 64);
        long before = Stopwatch.GetTimestamp();
        (WeakReference control, long owner) = Abandon(budget, prepared);
        for (int attempt = 0; attempt < 8 && control.IsAlive; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(control.IsAlive);
        List<NativeTraceDiagnosticOracle.Expected> expected =
            [NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.ReservationAdmitted, 8, 0, 8, owner, correlation: owner)];
        if (prepared)
        {
            expected.Add(NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Allocated, 8, 8, 0, owner, ordinal: 1));
            expected.Add(NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.ReservationBackingPrepared, 8, 8, 0, owner, ordinal: 1, correlation: owner));
            expected.Add(NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.Released, 8, 0, 0, owner, ordinal: 1));
        }
        expected.Add(NativeEventDiagnosticOracle.Item(NativeMemoryTraceKind.ReservationAbandoned, 8, 0, 0, owner,
            ordinal: prepared ? 1 : null, correlation: owner));
        NativeEventDiagnosticOracle.Verify(budget, before, expected.ToArray());
        GC.KeepAlive(budget);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Control, long Owner) Abandon(NativeMemoryBudget budget, bool prepared)
    {
        Assert.True(budget.TryReserve<int>(2, out NativeMemoryReservation<int>? permission, out _));
        if (prepared) permission.Value.PrepareBacking();
        return (new WeakReference(permission.Value.ControlForTest!), permission.Value.Id);
    }
}
