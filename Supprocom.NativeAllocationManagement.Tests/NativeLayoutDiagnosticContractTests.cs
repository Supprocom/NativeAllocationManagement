using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Conformance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeLayoutDiagnosticContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void PublicLinearTransitionsMatchAllLayoutAndNestedFields(int traceCapacity) => NativeLayoutDiagnosticOracle.Run(traceCapacity);

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(16, false)]
    [InlineData(16, true)]
    public void EmptyDescriptorsAndFieldsMatchEveryRealControlField(int traceCapacity, bool emptyField) =>
        NativeLayoutDiagnosticOracle.RunEmpty(traceCapacity, emptyField);

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void ActualAcquiredPointerIndependentlyDeterminesEveryAlignedField(int alignment)
    {
        NativeLayoutBuilder builder = new(2);
        NativeLayoutField<byte> first = builder.Add<byte>(3);
        NativeLayoutField<int> second = builder.Add<int>(2, alignment);
        NativeLayout layout = builder.Build();
        int extent = alignment + 8;
        int backing = extent + alignment - 1;
        NativeMemoryBudget budget = new(backing);
        Assert.True(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        NativeLayoutDiagnosticOracle.Statistics expected = NativeLayoutDiagnosticOracle.Pending(layout.Id, budget.Id,
            permission.Value.Id, 2, extent, 11, alignment);
        try
        {
            NativeLayoutDiagnosticOracle.Verify(permission.Value.CaptureSnapshot(), expected, "aligned-pending");
            permission.Value.PrepareBacking();
            int offset = ActualPayloadOffset(permission.Value.ControlForTest!, alignment);
            expected = NativeLayoutDiagnosticOracle.Prepared(expected, offset);
            NativeLayoutDiagnosticOracle.Verify(permission.Value.CaptureSnapshot(), expected, "aligned-prepared");
            NativeAdmissionDiagnosticOracle.Verify(layout.Describe(first), new NativeLayoutDiagnosticOracle.Region(layout.Id, 0, 3, 1, 0, 3, 1), "aligned-first");
            NativeAdmissionDiagnosticOracle.Verify(layout.Describe(second), new NativeLayoutDiagnosticOracle.Region(layout.Id, 1, 2, 4, alignment, 8, alignment), "aligned-second");
            NativeLayoutOwner owner = NativeLayoutReservation.Activate(ref permission, (first, second), static (writer, fields) =>
            { writer.Region(fields.first).Fill(3); writer.Region(fields.second).Fill(17); });
            expected = NativeLayoutDiagnosticOracle.Activated(expected, 11);
            try
            {
                NativeLayoutDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected, "aligned-active");
                Assert.Equal(37, owner.Read((first, second), static (view, fields) => view.Region(fields.first)[0]
                    + view.Region(fields.second)[0] + view.Region(fields.second)[1]));
                expected = expected with { Ownership = expected.Ownership with { PeakBorrowCount = 1 } };
                NativeLayoutDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected, "aligned-read-ended");
            }
            finally { owner.Dispose(); }
            NativeLayoutDiagnosticOracle.Verify(owner.CaptureSnapshot(), NativeLayoutDiagnosticOracle.Returned(expected), "aligned-returned");
        }
        finally { permission?.Dispose(); }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteAndCancelledPublicationMatchEveryTerminalField(bool cancellation)
    {
        NativeLayoutBuilder builder = new(2);
        NativeLayoutField<byte> first = builder.Add<byte>(3);
        NativeLayoutField<byte> second = builder.Add<byte>(2);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(5);
        Assert.True(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        NativeLayoutReservation numericAlias = permission.Value;
        NativeLayoutDiagnosticOracle.Statistics expected = NativeLayoutDiagnosticOracle.Prepared(
            NativeLayoutDiagnosticOracle.Pending(layout.Id, budget.Id, permission.Value.Id, 2, 5, 5, 1));
        using CancellationTokenSource stop = new();
        Exception? failure = Record.Exception(() => NativeLayoutReservation.Activate(ref permission, (first, second, stop, cancellation),
            static (writer, state) =>
            {
                writer.Region(state.first).Fill(17);
                if (state.cancellation)
                {
                    writer.Region(state.second).Fill(19);
                    state.stop.Cancel();
                }
            }, stop.Token));
        if (cancellation) Assert.IsType<OperationCanceledException>(failure);
        else Assert.IsType<InvalidOperationException>(failure);
        Assert.Null(permission);
        expected = expected with
        {
            InitializationCompleted = cancellation,
            Ownership = expected.Ownership.Returned(),
            Reservation = expected.Reservation with
            {
                BindingIsActive = false,
                HasReservationReturnObligation = false,
                OwnedBackingBytes = 0,
                Outcome = cancellation ? NativeMemoryReservationOutcome.Cancelled : NativeMemoryReservationOutcome.InitializationFailed
            }
        };
        NativeLayoutDiagnosticOracle.Verify(numericAlias.CaptureSnapshot(), expected, "failed-publication-terminal");
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void FailedBackingObservationAndAlignmentRetryMatchEveryField()
    {
        NativeLayoutBuilder builder = new(1);
        builder.Add<byte>(3, 64);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(66);
        Assert.True(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        NativeLayoutDiagnosticOracle.Statistics expected = NativeLayoutDiagnosticOracle.Pending(layout.Id, budget.Id,
            permission.Value.Id, 1, 3, 3, 64);
        try
        {
            NativeMemoryTestHooks.FailAtManagedPublicationBoundary(7);
            Assert.Throws<InvalidOperationException>(() => permission.Value.PrepareBacking());
            expected = NativeLayoutDiagnosticOracle.Prepared(expected) with { LayoutIsPrepared = false };
            expected = expected with { Reservation = expected.Reservation with { BackingPreparationFailureCount = 1 } };
            NativeLayoutDiagnosticOracle.Verify(permission.Value.CaptureSnapshot(), expected, "backing-observation-failed");
            permission.Value.PrepareBacking();
            expected = expected with { LayoutIsPrepared = true, PayloadOffsetBytes = ActualPayloadOffset(permission.Value.ControlForTest!, 64) };
            NativeLayoutDiagnosticOracle.Verify(permission.Value.CaptureSnapshot(), expected, "alignment-retry");
            Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        }
        finally { permission.Value.Dispose(); NativeMemoryTestHooks.Reset(); }
        expected = expected with
        {
            Ownership = expected.Ownership.Returned(),
            Reservation = expected.Reservation with
            { BindingIsActive = false, Outcome = NativeMemoryReservationOutcome.Cancelled, HasReservationReturnObligation = false, OwnedBackingBytes = 0 }
        };
        NativeLayoutDiagnosticOracle.Verify(permission.Value.CaptureSnapshot(), expected, "prepared-cancelled");
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void FailedCopyPublicationPreservesActualCopyButNoPublishedOwner()
    {
        using NativeLayoutOwner owner = CreateByteOwner(out NativeLayoutField<byte> field, out NativeMemoryBudget budget,
            out NativeLayoutDiagnosticOracle.Statistics expected);
        try
        {
            NativeMemoryTestHooks.FailAtManagedPublicationBoundary(8);
            Assert.Throws<InvalidOperationException>(() => owner.DetachField(field, budget));
            expected = expected with { CopiedBytes = 3, Ownership = expected.Ownership with { PeakBorrowCount = 1 } };
            NativeLayoutDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected, "copied-publication-failed");
            Assert.Equal(3, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(1, budget.CaptureStatistics().FreeCount);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void SaturatedRealCopyHistoriesPreserveAllExactFields()
    {
        using NativeLayoutOwner owner = CreateByteOwner(out NativeLayoutField<byte> field, out NativeMemoryBudget budget,
            out NativeLayoutDiagnosticOracle.Statistics expected);
        object control = owner.ControlForTest!;
        typeof(NativeLayoutControl).GetField("_copiedBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(control, long.MaxValue);
        typeof(NativeLayoutControl).GetField("_detachedOwners", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(control, long.MaxValue);
        using NativeTransfer<byte> copy = owner.DetachField(field, budget);
        expected = expected with
        { CopiedBytes = long.MaxValue, DetachedOwnerCount = long.MaxValue, HistoryOverflowed = true, Ownership = expected.Ownership with { PeakBorrowCount = 1 } };
        NativeLayoutDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected, "real-saturating-copy");
        Assert.Equal(6, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(34, copy.Read(static view => view[0] + view[2]));
    }

    [Fact]
    public void FailedTerminalReturnAndRetryMatchEveryChargedField()
    {
        NativeLayoutBuilder builder = new(1);
        builder.Add<byte>(3);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(3);
        Assert.True(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        NativeLayoutReservation numericAlias = permission.Value;
        NativeLayoutDiagnosticOracle.Statistics expected = NativeLayoutDiagnosticOracle.Prepared(
            NativeLayoutDiagnosticOracle.Pending(layout.Id, budget.Id, permission.Value.Id, 1, 3, 3, 1));
        try
        {
            Assert.Throws<AggregateException>(() => NativeLayoutReservation.Activate(ref permission, 0, static (_, _) =>
            {
                NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
                throw new InvalidOperationException("Injected producer failure.");
            }));
            Assert.Null(permission);
            expected = expected with
            {
                Ownership = expected.Ownership with { Lifecycle = NativeTransferLifecycle.Retiring, PayloadReturnFailureCount = 1 },
                Reservation = expected.Reservation with
                { BindingIsActive = false, Outcome = NativeMemoryReservationOutcome.InitializationFailed, ReturnFailureCount = 1 }
            };
            NativeLayoutDiagnosticOracle.Verify(numericAlias.CaptureSnapshot(), expected, "return-failed-charged");
            Assert.Equal(3, budget.CaptureStatistics().CommittedBytes);
            Assert.True(numericAlias.TryCompletePayloadReturn());
            expected = expected with
            {
                Ownership = expected.Ownership.Returned(),
                Reservation = expected.Reservation with { OwnedBackingBytes = 0, HasReservationReturnObligation = false }
            };
            NativeLayoutDiagnosticOracle.Verify(numericAlias.CaptureSnapshot(), expected, "retry-returned");
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(1, budget.CaptureStatistics().FreeCount);
        }
        finally { NativeMemoryTestHooks.Reset(); }
    }

    [Fact]
    public void LayoutInventoriesMatchEveryTypedPropertyDefinitionAndProof()
    {
        Assert.Equal(IntPtr.Size + 4 * sizeof(int), Unsafe.SizeOf<NativeLayoutRegion>());
        string root = RepositoryTestPaths.Root;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "conformance", "native-layout-diagnostic-contracts.json")));
        JsonElement registry = document.RootElement;
        Assert.False(registry.GetProperty("completeReleaseInventory").GetBoolean());
        Assert.True(File.Exists(Path.Combine(root, registry.GetProperty("oracleSource").GetString()!)));
        VerifyTestReference(registry.GetProperty("packageProof").GetString()!);
        foreach (string key in new[] { "availability", "unreviewedScope", "oracleScope" }) Assert.False(string.IsNullOrWhiteSpace(registry.GetProperty(key).GetString()), key);
        int total = 0;
        foreach (JsonElement schema in registry.GetProperty("schemas").EnumerateArray())
        {
            Type actualType = typeof(NativeMemoryBudget).Assembly.GetType(schema.GetProperty("schema").GetString()!, throwOnError: true)!;
            Type expectedType = typeof(NativeLayoutDiagnosticOracle).GetNestedType(schema.GetProperty("expected").GetString()!, BindingFlags.NonPublic)!;
            PropertyInfo[] actual = actualType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();
            PropertyInfo[] expected = expectedType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();
            Assert.Equal(actual.Length, expected.Length);
            for (int index = 0; index < actual.Length; index++)
            {
                Assert.Equal(actual[index].Name, expected[index].Name);
                Type expectedProperty = expected[index].Name switch
                {
                    nameof(NativeLayoutStatistics.Ownership) => typeof(NativeTransferStatistics),
                    nameof(NativeLayoutStatistics.Reservation) => typeof(NativeMemoryReservationStatistics),
                    _ => expected[index].PropertyType
                };
                Assert.Equal(actual[index].PropertyType, expectedProperty);
            }
            JsonElement[] fields = schema.GetProperty("fields").EnumerateArray().ToArray();
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
        Assert.Equal(24, total);
    }

    private static int ActualPayloadOffset(object control, int alignment)
    {
        NativeBlock block = (NativeBlock)typeof(NativeTransferControl<byte>).GetField("_block", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control)!;
        nuint remainder = (nuint)block.Pointer % (nuint)alignment;
        return remainder == 0 ? 0 : checked(alignment - (int)remainder);
    }

    private static NativeLayoutOwner CreateByteOwner(out NativeLayoutField<byte> field, out NativeMemoryBudget budget,
        out NativeLayoutDiagnosticOracle.Statistics expected)
    {
        NativeLayoutBuilder builder = new(1);
        field = builder.Add<byte>(3);
        NativeLayout layout = builder.Build();
        budget = new(6);
        Assert.True(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        expected = NativeLayoutDiagnosticOracle.Activated(NativeLayoutDiagnosticOracle.Prepared(
            NativeLayoutDiagnosticOracle.Pending(layout.Id, budget.Id, permission.Value.Id, 1, 3, 3, 1)), 3);
        try { return NativeLayoutReservation.Activate(ref permission, field, static (writer, token) => writer.Region(token).Fill(17)); }
        finally { permission?.Dispose(); }
    }

    private static void VerifyTestReference(string reference)
    {
        string[] parts = reference.Split('.', 2);
        Assert.Equal(2, parts.Length);
        Type type = typeof(NativeLayoutDiagnosticContractTests).Assembly.GetType(typeof(NativeLayoutDiagnosticContractTests).Namespace + "." + parts[0], throwOnError: true)!;
        MethodInfo? method = type.GetMethod(parts[1], BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        Assert.True(method.IsDefined(typeof(FactAttribute)) || method.IsDefined(typeof(TheoryAttribute)), reference);
    }
}
