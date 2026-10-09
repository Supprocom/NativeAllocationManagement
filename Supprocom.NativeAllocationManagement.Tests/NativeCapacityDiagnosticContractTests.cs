using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Conformance;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeCapacityDiagnosticContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void PublicPoolTransitionsMatchEveryField(int traceCapacity) => NativeCapacityDiagnosticOracle.RunPool(traceCapacity);

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void PublicArenaTransitionsMatchEveryField(int traceCapacity) => NativeCapacityDiagnosticOracle.RunArena(traceCapacity);

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void PublicRetentionTransitionsMatchEveryField(int traceCapacity) => NativeCapacityDiagnosticOracle.RunRetention(traceCapacity);

    [Fact]
    public void InitializingSlotIsOccupiedBeforePublicationAndFailureRollsBackEveryGauge()
    {
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(1, 1, 1), budget: null);
        NativeCapacityDiagnosticOracle.Pool expected = new()
        {
            OwnerId = pool.Id,
            Lifecycle = NativeOwnerLifecycle.Active,
            Preparation = new(1, 1, 1),
            RetainedPageCount = 1,
            RetainedSlotCount = 1,
            AvailableSlotCount = 1,
            RetainedBytes = 4,
            PeakRetainedBytes = 4,
            ManagedBankBytes = 24 + 40,
            UnusedSlotBytes = 4
        };
        Assert.Throws<InvalidOperationException>(() => pool.Rent(1, _ =>
        {
            NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected with
            { OccupiedSlotCount = 1, PeakOccupiedSlotCount = 1, AvailableSlotCount = 0, UnusedSlotBytes = 0 }, "slot-entered-not-published");
            throw new InvalidOperationException("Injected producer failure.");
        }));
        expected = expected with { PeakOccupiedSlotCount = 1, InitializerFailureCount = 1 };
        NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected, "slot-failure-restores-authority-not-peak");
        using (PreparedPooled<int> retry = pool.Rent(1, static writer => writer.Write(42)))
        {
            NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected with
            { OccupiedSlotCount = 1, AvailableSlotCount = 0, UnusedSlotBytes = 0, SuccessfulRentCount = 1 }, "same-slot-retry");
            Assert.Equal(42, retry.Read(static view => view[0]));
        }
        NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), expected with { SuccessfulRentCount = 1 }, "retry-returned");
    }

    [Fact]
    public void InitializingArenaCountsAlignmentAndFailurePreservesEveryPeak()
    {
        long extent = NativeCapacityDiagnosticOracle.ArenaExtent(16);
        using NativeArena arena = new(new NativeArenaPreparation(16, 0), new NativeMemoryBudget(extent));
        Assert.True(arena.TryScratch<byte>(1, static writer => writer.Write(7), out ArenaLease<byte> prefix));
        NativeCapacityDiagnosticOracle.Arena expected = new()
        {
            OwnerId = arena.Id,
            Lifecycle = NativeOwnerLifecycle.Active,
            Preparation = new(16, 0),
            OrdinaryUsedBytes = 1,
            OrdinaryAvailableBytes = 15,
            PeakOrdinaryUsedBytes = 1,
            RetainedBytes = extent,
            PeakRetainedBytes = extent,
            SuccessfulScratchCount = 1
        };
        Assert.Throws<InvalidOperationException>(() => arena.TryScratch<long>(1, _ =>
        {
            NativeCapacityDiagnosticOracle.Verify(arena.CapturePreparedSnapshot(), expected with
            { OrdinaryUsedBytes = 16, OrdinaryAvailableBytes = 0, PeakOrdinaryUsedBytes = 16 }, "aligned-reservation-entered-not-published");
            throw new InvalidOperationException("Injected arena producer failure.");
        }, out _));
        expected = expected with { PeakOrdinaryUsedBytes = 16, InitializerFailureCount = 1 };
        NativeCapacityDiagnosticOracle.Verify(arena.CapturePreparedSnapshot(), expected, "aligned-failure-rollback");
        Assert.Equal(7, prefix.Read(static view => view[0]));
        Assert.True(arena.TryScratch<long>(1, static writer => writer.Write(42), out ArenaLease<long> retry));
        NativeCapacityDiagnosticOracle.Verify(arena.CapturePreparedSnapshot(), expected with
        { OrdinaryUsedBytes = 16, OrdinaryAvailableBytes = 0, SuccessfulScratchCount = 2 }, "aligned-retry-fits-exactly");
        Assert.Equal(42, retry.Read(static view => view[0]));
    }

    [Fact]
    public void PartialMappedTrimKeepsTheWholeProviderObligationUntilActualRelease()
    {
        using ProviderBuffer provider = new(128);
        NativeMemoryBudget budget = new(128);
        using NativeArena arena = new(provider, 0, new NativeArenaPreparation(64, 64), budget);
        NativeCapacityDiagnosticOracle.Arena expected = new()
        {
            OwnerId = arena.Id,
            Lifecycle = NativeOwnerLifecycle.Active,
            Preparation = new(64, 64),
            OrdinaryAvailableBytes = 64,
            ScopedAvailableBytes = 64,
            RetainedBytes = 128,
            PeakRetainedBytes = 128,
            ActiveBorrowedBytes = 128,
            RetainedBorrowedBytes = 128
        };
        NativeCapacityDiagnosticOracle.Verify(arena.CapturePreparedSnapshot(), expected, "mapped-both-lanes");
        Assert.True(arena.TryScratch<int>(1, static writer => writer.Write(42), out ArenaLease<int> value));
        expected = expected with { OrdinaryUsedBytes = 4, OrdinaryAvailableBytes = 60, PeakOrdinaryUsedBytes = 4, SuccessfulScratchCount = 1 };
        provider.Dispose();
        Assert.Equal(0, provider.ReleaseCount);
        Assert.Equal((nuint)0, arena.TrimRetainedMemory());
        expected = expected with { ScopedAvailableBytes = 0, ActiveBorrowedBytes = 64 };
        NativeCapacityDiagnosticOracle.Verify(arena.CapturePreparedSnapshot(), expected, "mapped-partial-trim-holds-full-range-and-headers");
        Assert.False(arena.TryScratchScoped<int>(1, static writer => writer.Write(0), out _));
        expected = expected with { RejectedCapacityCount = 1 };
        NativeCapacityDiagnosticOracle.Verify(arena.CapturePreparedSnapshot(), expected, "mapped-trimmed-lane-refuses-growth");
        Assert.Equal(42, value.Read(static view => view[0]));
        Assert.Equal(128, budget.CaptureStatistics().CommittedBytes);
        arena.Reset();
        expected = expected with { OrdinaryUsedBytes = 0, OrdinaryAvailableBytes = 64 };
        NativeCapacityDiagnosticOracle.Verify(arena.CapturePreparedSnapshot(), expected, "mapped-reset-does-not-recreate-lane");
        Assert.Equal((nuint)128, arena.TrimRetainedMemory());
        expected = expected with { OrdinaryAvailableBytes = 0, RetainedBytes = 0, ActiveBorrowedBytes = 0, RetainedBorrowedBytes = 0 };
        NativeCapacityDiagnosticOracle.Verify(arena.CapturePreparedSnapshot(), expected, "mapped-final-trim-actual-release");
        Assert.Equal(1, provider.ReleaseCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void DisabledRetentionIsARealInvariantAndGeneralTrimDoesNotInventPolicyCredit()
    {
        long extent = NativeCapacityDiagnosticOracle.ArenaExtent(64);
        long tailExtent = NativeCapacityDiagnosticOracle.ArenaExtent(128);
        NativeMemoryBudget budget = new(extent + tailExtent);
        using NativeArena arena = new(budget, 64, NativeMemoryReturn.ToNativeMemory);
        NativeCapacityDiagnosticOracle.Retention expected = new()
        { OwnerId = arena.Id, Lifecycle = NativeOwnerLifecycle.Active, RetainedBytes = extent, IdleBytes = extent };
        NativeCapacityDiagnosticOracle.Verify(arena.CaptureRetentionSnapshot(), expected, "disabled-policy-real-idle-storage");
        Assert.Throws<InvalidOperationException>(() => arena.MaintainRetention());
        NativeCapacityDiagnosticOracle.Verify(arena.CaptureRetentionSnapshot(), expected, "disabled-maintenance-no-history");
        _ = arena.Scratch<byte>(64, static writer => writer.Fill(7));
        _ = arena.Scratch<byte>(128, static writer => writer.Fill(11));
        arena.Reset();
        NativeCapacityDiagnosticOracle.Verify(arena.CaptureRetentionSnapshot(), expected with
        { RetainedBytes = extent + tailExtent, IdleBytes = extent + tailExtent }, "disabled-two-idle-segments-no-outlier-policy");
        // Ordinary trim keeps the current segment and frees only the idle tail.
        Assert.Equal((nuint)tailExtent, arena.TrimRetainedMemory());
        NativeCapacityDiagnosticOracle.Verify(arena.CaptureRetentionSnapshot(), expected, "unrelated-trim-not-policy-release");
        arena.Dispose();
        NativeCapacityDiagnosticOracle.Verify(arena.CaptureRetentionSnapshot(), expected with
        { Lifecycle = NativeOwnerLifecycle.Disposed, RetainedBytes = 0, IdleBytes = 0 }, "disabled-closed");
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SaturatedHistoriesKeepEveryGaugeAndCleanupExact(int kind)
    {
        NativeMemoryBudget budget = new(512);
        if (kind == 0)
        {
            using NativePreparedPool<int> pool = new(new NativePoolPreparation(1, 1, 1), budget);
            SetCounter(pool, "_successfulPreparedRents");
            using (PreparedPooled<int> value = pool.Rent(1, static writer => writer.Write(42)))
            {
                NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), new NativeCapacityDiagnosticOracle.Pool
                {
                    OwnerId = pool.Id,
                    Lifecycle = NativeOwnerLifecycle.Active,
                    Preparation = new(1, 1, 1),
                    RetainedPageCount = 1,
                    RetainedSlotCount = 1,
                    OccupiedSlotCount = 1,
                    PeakOccupiedSlotCount = 1,
                    RetainedBytes = 4,
                    PeakRetainedBytes = 4,
                    ManagedBankBytes = 64,
                    SuccessfulRentCount = long.MaxValue,
                    HistoryOverflowed = true
                }, "saturated-pool-published");
                Assert.Equal(42, value.Read(static view => view[0]));
            }
            pool.Dispose();
            NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), new NativeCapacityDiagnosticOracle.Pool
            {
                OwnerId = pool.Id,
                Lifecycle = NativeOwnerLifecycle.Disposed,
                Preparation = new(1, 1, 1),
                PeakOccupiedSlotCount = 1,
                PeakRetainedBytes = 4,
                ManagedBankBytes = 64,
                SuccessfulRentCount = long.MaxValue,
                HistoryOverflowed = true
            }, "saturated-pool-cleanup");
        }
        else if (kind == 1)
        {
            long extent = NativeCapacityDiagnosticOracle.ArenaExtent(8);
            using NativeArena arena = new(new NativeArenaPreparation(8, 0), budget);
            SetCounter(arena, "_preparedSuccessCount");
            Assert.True(arena.TryScratch<long>(1, static writer => writer.Write(42), out ArenaLease<long> value));
            NativeCapacityDiagnosticOracle.Arena expected = new()
            {
                OwnerId = arena.Id,
                Lifecycle = NativeOwnerLifecycle.Active,
                Preparation = new(8, 0),
                OrdinaryUsedBytes = 8,
                PeakOrdinaryUsedBytes = 8,
                RetainedBytes = extent,
                PeakRetainedBytes = extent,
                SuccessfulScratchCount = long.MaxValue,
                HistoryOverflowed = true
            };
            NativeCapacityDiagnosticOracle.Verify(arena.CapturePreparedSnapshot(), expected, "saturated-arena-published");
            Assert.Equal(42, value.Read(static view => view[0]));
            arena.Dispose();
            NativeCapacityDiagnosticOracle.Verify(arena.CapturePreparedSnapshot(), expected with
            { Lifecycle = NativeOwnerLifecycle.Disposed, OrdinaryUsedBytes = 0, RetainedBytes = 0 }, "saturated-arena-cleanup");
        }
        else
        {
            long extent = NativeCapacityDiagnosticOracle.ArenaExtent(128);
            using NativeArena arena = new(budget, new NativeArenaRetentionPolicy(64, 0), 128, NativeMemoryReturn.ToNativeMemory);
            SetCounter(arena, "_retentionMaintenanceCount");
            SetCounter(arena, "_retentionReleasedBytes");
            arena.Reset();
            NativeCapacityDiagnosticOracle.Retention expected = new()
            {
                OwnerId = arena.Id,
                Lifecycle = NativeOwnerLifecycle.Active,
                Enabled = true,
                Policy = new(64, 0),
                PeakOversizedBytes = extent,
                MaintenanceCount = long.MaxValue,
                ReleasedBytes = long.MaxValue,
                HistoryOverflowed = true
            };
            NativeCapacityDiagnosticOracle.Verify(arena.CaptureRetentionSnapshot(), expected, "saturated-policy-real-release");
            arena.Dispose();
            NativeCapacityDiagnosticOracle.Verify(arena.CaptureRetentionSnapshot(), expected with
            { Lifecycle = NativeOwnerLifecycle.Disposed }, "saturated-policy-closed");
        }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void IndependentBackingAndBankRepresentationMatchesActualStorage()
    {
        Type slab = typeof(NativePreparedPool<int>).GetField("_slabs", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType.GetElementType()!;
        Assert.False(slab.IsGenericType);
        Type page = typeof(NativePreparedPool<int>).GetField("_pages", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType.GetElementType()!;
        Assert.False(page.IsGenericType);
        MethodInfo size = typeof(NativeCapacityDiagnosticContractTests).GetMethod(nameof(ElementSize), BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(NativeCapacityDiagnosticOracle.PoolSlotBytes, (int)size.MakeGenericMethod(slab).Invoke(null, null)!);
        Assert.Equal(NativeCapacityDiagnosticOracle.PoolPageBytes, (int)size.MakeGenericMethod(page).Invoke(null, null)!);
        Type header = typeof(NativeArena).GetNestedType("ArenaSegmentHeader", BindingFlags.NonPublic)!;
        int headerSize = (int)size.MakeGenericMethod(header).Invoke(null, null)!;
        Assert.Equal(56, headerSize);
        Assert.Equal(NativeCapacityDiagnosticOracle.ArenaHeaderBytes, (headerSize + 63) / 64 * 64);
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(3, 17, 2), budget: null);
        Assert.Null(typeof(NativePreparedPool<int>).GetField("_freeHeads", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(typeof(NativePreparedPool<int>).GetField("_nonEmptyFreeClasses", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Equal(2, (int)typeof(NativePreparedPool<int>).GetField("_freeHead", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!);
        NativeCapacityDiagnosticOracle.Verify(pool.CapturePreparedSnapshot(), new NativeCapacityDiagnosticOracle.Pool
        {
            OwnerId = pool.Id,
            Lifecycle = NativeOwnerLifecycle.Active,
            Preparation = new(3, 17, 2),
            RetainedPageCount = 2,
            RetainedSlotCount = 3,
            AvailableSlotCount = 3,
            RetainedBytes = 204,
            PeakRetainedBytes = 204,
            ManagedBankBytes = 3 * 24 + 2 * 40,
            UnusedSlotBytes = 3 * 17 * sizeof(int)
        }, "two-pages-short-final-page-packed-slots");
    }

    [Fact]
    public void CapacityInventoriesMatchEveryDefinitionAndProof()
    {
        string root = RepositoryTestPaths.Root;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "conformance", "native-capacity-diagnostic-contracts.json")));
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
            Type expectedType = typeof(NativeCapacityDiagnosticOracle).GetNestedType(schema.GetProperty("expected").GetString()!, BindingFlags.NonPublic)!;
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
        Assert.Equal(45, total);
    }

    private static int ElementSize<T>() => Unsafe.SizeOf<T>();

    private static void SetCounter(object owner, string field)
    {
        object kernel = owner.GetType().GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner) ?? owner;
        kernel.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(kernel, long.MaxValue);
    }

    private static void VerifyTestReference(string reference)
    {
        string[] parts = reference.Split('.', 2);
        Assert.Equal(2, parts.Length);
        Type type = typeof(NativeCapacityDiagnosticContractTests).Assembly.GetType(typeof(NativeCapacityDiagnosticContractTests).Namespace + "." + parts[0], throwOnError: true)!;
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
