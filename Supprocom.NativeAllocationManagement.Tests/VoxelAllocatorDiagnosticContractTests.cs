using System.Reflection;
using System.Text.Json;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.NAM;
using Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.SharedContract;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class VoxelAllocatorDiagnosticContractTests
{
    [VoxelDemonstrationFact]
    public void EveryDeclaredDemoFieldHasAnExactRealMappingAndExecutedProof()
    {
        string root = RepositoryTestPaths.Root;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "conformance", "voxel-allocator-diagnostic-contracts.json")));
        JsonElement registry = document.RootElement;
        Assert.False(registry.GetProperty("completeReleaseInventory").GetBoolean());
        foreach (string key in new[] { "scope", "consistency", "resetPolicy", "availability", "unreviewedScope" })
            Assert.False(string.IsNullOrWhiteSpace(registry.GetProperty(key).GetString()), key);
        PropertyInfo[] properties = typeof(PressureAllocatorDiagnosticSnapshot)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();
        JsonElement[] fields = registry.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(26, fields.Length);
        Assert.Equal(properties.Select(static property => property.Name),
            fields.Select(static field => field.GetProperty("name").GetString()!).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (JsonElement field in fields)
        {
            foreach (string key in new[] { "name", "units", "definition", "overflow" })
                Assert.False(string.IsNullOrWhiteSpace(field.GetProperty(key).GetString()), key);
            string[] source = field.GetProperty("implementation").GetString()!.Split('#', 2);
            Assert.Equal(2, source.Length);
            Assert.Contains(source[1], File.ReadAllText(Path.Combine(root, source[0])), StringComparison.Ordinal);
            string name = field.GetProperty("name").GetString()!;
            if (!string.Equals(name, nameof(PressureAllocatorDiagnosticSnapshot.Available), StringComparison.Ordinal))
            {
                PropertyInfo actual = typeof(NativeOwnerDiagnosticSnapshot).GetProperty(name)!;
                Assert.NotNull(actual);
                PropertyInfo mapped = properties.Single(property => string.Equals(property.Name, name, StringComparison.Ordinal));
                Assert.Equal(mapped.PropertyType == typeof(string) ? mapped.PropertyType : Nullable.GetUnderlyingType(mapped.PropertyType),
                    actual.PropertyType.IsEnum ? typeof(string) : actual.PropertyType);
            }
            foreach (JsonElement proof in field.GetProperty("proofs").EnumerateArray())
            {
                MethodInfo? method = typeof(VoxelAllocatorDiagnosticContractTests).GetMethod(proof.GetString()!, BindingFlags.Public | BindingFlags.Instance);
                Assert.NotNull(method);
                Assert.True(method.IsDefined(typeof(FactAttribute)), proof.GetString());
            }
        }
    }

    [VoxelDemonstrationFact]
    public void RootedScopedAndFastStatesMapEveryFieldWithoutInventingRecords()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: 128, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        VerifyCompleteMapping(arena.CaptureDiagnosticSnapshot());
        ConcurrentArenaLease<string> ordinary = arena.Scratch<string>(1, static writer => writer.Write("ordinary"));
        ConcurrentArenaLease<string> scoped = arena.ScratchScoped<string>(1, static writer => writer.Write("scoped"));
        PressureAllocatorDiagnosticSnapshot active = VerifyCompleteMapping(arena.CaptureDiagnosticSnapshot());
        Assert.Equal(2, active.ActiveRecords);
        Assert.Equal(1, active.ScopedRecords);
        Assert.Equal(2, active.ReferenceRoots);
        Assert.True(active.RetainedSegmentCount > 0);
        Assert.True(active.OrdinaryTraversalIndex >= 0);
        Assert.True(active.ScopedTraversalIndex >= 0);
        Assert.True(active.InitializedPayloadBytes > 0);
        Assert.Equal("ordinary", ordinary.Read(static view => view[0]));
        Assert.Equal("scoped", scoped.Read(static view => view[0]));
        arena.RecycleScoped();
        PressureAllocatorDiagnosticSnapshot recycled = VerifyCompleteMapping(arena.CaptureDiagnosticSnapshot());
        Assert.Equal(0, recycled.ScopedRecords);
        Assert.Equal(1, recycled.ReferenceRoots);
        Assert.True(recycled.ScopeEpoch > active.ScopeEpoch);
        arena.ReleaseLeasesToNativeMemory();
        PressureAllocatorDiagnosticSnapshot reset = VerifyCompleteMapping(arena.CaptureDiagnosticSnapshot());
        Assert.Equal(0, reset.ReferenceRoots);
        Assert.Equal(0, reset.InitializedPayloadBytes);
        Assert.True(reset.AvailableSegmentCount > 0);
        Assert.True(reset.Generation > active.Generation);
        Assert.Equal(active.OwnerId, reset.OwnerId);
        Assert.Equal(active.PeakInitializedPayloadBytes, reset.PeakInitializedPayloadBytes);
        arena.Dispose();
        VerifyNativeCleanup(arena.CaptureDiagnosticSnapshot());

        using NativeArena fast = new(preAllocateBytes: 128, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        _ = fast.Scratch<int>(1, static writer => writer.Write(17));
        _ = fast.ScratchScoped<int>(1, static writer => writer.Write(19));
        PressureAllocatorDiagnosticSnapshot fastActive = VerifyCompleteMapping(fast.CaptureDiagnosticSnapshot());
        Assert.Equal(NativeOwnerModel.ThreadConfinedArena.ToString(), fastActive.Model);
        Assert.Equal(0, fastActive.ActiveRecords);
        Assert.Equal(0, fastActive.ScopedRecords);
        Assert.Equal(0, fastActive.ReferenceRoots);
        Assert.Equal(0, fastActive.RetiredGenerationCount);
        Assert.Equal(0, fastActive.QuarantinedGenerationCount);
        Assert.False(fastActive.CurrentGenerationQuarantined);
        Assert.Equal(8, fastActive.InitializedPayloadBytes);
        fast.Reset();
        PressureAllocatorDiagnosticSnapshot fastReset = VerifyCompleteMapping(fast.CaptureDiagnosticSnapshot());
        Assert.Equal(0, fastReset.InitializedPayloadBytes);
        Assert.Equal(8, fastReset.PeakInitializedPayloadBytes);
        fast.Dispose();
        VerifyNativeCleanup(fast.CaptureDiagnosticSnapshot());
    }

    [VoxelDemonstrationFact]
    public void RetiredAndQuarantinedBanksKeepTheirRealChargeAndHealthyCurrentFlag()
    {
        NativeMemoryBudget budget = new(128);
        using NativeConcurrentPool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToNativeMemory, false);
        long extent = budget.CaptureStatistics().CommittedBytes;
        Assert.True(extent > 0);
        try
        {
            using ConcurrentPooled<int> value = pool.Rent(1, static writer => writer.Write(23));
            NativeAllocationQuarantinedException? failure = null;
            try
            {
                value.Access(view =>
                {
                    pool.ReleaseLeasesToGarbageCollector();
                    PressureAllocatorDiagnosticSnapshot retired = VerifyCompleteMapping(pool.CaptureDiagnosticSnapshot());
                    Assert.Equal(1, retired.RetiredGenerationCount);
                    Assert.Equal(1, retired.RetiredSegmentCount);
                    Assert.Equal(extent, retired.RetiredBytes);
                    Assert.Equal(extent, retired.OutstandingNativeBytes);
                    Assert.Equal(0, retired.QuarantinedGenerationCount);
                    Assert.False(retired.CurrentGenerationQuarantined);
                    Assert.Equal(23, view[0]);
                    NativeMemoryTestHooks.FailAfterCommitBoundary(1);
                });
            }
            catch (NativeAllocationQuarantinedException exception) { failure = exception; }
            Assert.NotNull(failure);
            PressureAllocatorDiagnosticSnapshot quarantined = VerifyCompleteMapping(pool.CaptureDiagnosticSnapshot());
            Assert.Equal(0, quarantined.RetiredGenerationCount);
            Assert.Equal(1, quarantined.RetiredSegmentCount);
            Assert.Equal(1, quarantined.QuarantinedGenerationCount);
            Assert.Equal(1, quarantined.QuarantinedSegmentCount);
            Assert.False(quarantined.CurrentGenerationQuarantined);
            Assert.Equal(extent, quarantined.RetiredBytes);
            Assert.Equal(extent, budget.CaptureStatistics().CommittedBytes);
            try
            {
                _ = value.Read(static view => view[0]);
                Assert.Fail("A saved snapshot cannot restore returned payload authority.");
            }
            catch (NativeAllocationReturnedException)
            {
            }
        }
        finally { NativeMemoryTestHooks.Reset(); pool.Dispose(); }
        VerifyNativeCleanup(pool.CaptureDiagnosticSnapshot());
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [VoxelDemonstrationFact]
    public void SaturatedHistoryAndMetricsResetPreserveOwnerPeaksAfterPhysicalCleanup()
    {
        NativeMemoryBudget budget = new(512);
        using NativeConcurrentPool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory, false);
        NativeOwnerKernel kernel = pool.KernelForTransfer;
        typeof(NativeOwnerKernel).GetField("_freshSegmentAllocationCount", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(kernel, long.MaxValue);
        try
        {
            using (ConcurrentPooled<int> value = pool.Rent(1, static writer => writer.Write(29)))
            {
                Assert.Equal(29, value.Read(static view => view[0]));
                PressureAllocatorDiagnosticSnapshot active = VerifyCompleteMapping(pool.CaptureDiagnosticSnapshot());
                Assert.True(active.HistoryOverflowed);
                Assert.Equal(4, active.InitializedPayloadBytes);
                Assert.Equal(4, active.PeakInitializedPayloadBytes);
                long epoch = active.MetricsEpoch!.Value;
                NativeMemoryTestHooks.Reset();
                PressureAllocatorDiagnosticSnapshot measured = VerifyCompleteMapping(pool.CaptureDiagnosticSnapshot());
                Assert.Equal(checked(epoch + 1), measured.MetricsEpoch);
                Assert.Equal(active.OwnerId, measured.OwnerId);
                Assert.Equal(active.PeakOutstandingNativeBytes, measured.PeakOutstandingNativeBytes);
                Assert.Equal(active.PeakInitializedPayloadBytes, measured.PeakInitializedPayloadBytes);
                Assert.True(measured.HistoryOverflowed);
            }
            Assert.True(pool.TrimRetainedMemory() > 0);
            PressureAllocatorDiagnosticSnapshot trimmed = VerifyCompleteMapping(pool.CaptureDiagnosticSnapshot());
            Assert.Equal(0, trimmed.OutstandingNativeBytes);
            Assert.Equal(0, trimmed.InitializedPayloadBytes);
            Assert.True(trimmed.PeakOutstandingNativeBytes > 0);
            Assert.Equal(4, trimmed.PeakInitializedPayloadBytes);
            Assert.True(trimmed.HistoryOverflowed);
        }
        finally { NativeMemoryTestHooks.Reset(); pool.Dispose(); }
        VerifyNativeCleanup(pool.CaptureDiagnosticSnapshot());
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [VoxelDemonstrationFact]
    public void DetachedBackingRemainsMeasuredAndChargedUntilActualFinalization()
    {
        NativeMemoryBudget budget = new(128);
        using NativePool<byte> pool = new(budget, 0, 64, NativeMemoryReturn.ToGarbageCollector);
        pool.Dispose();
        PressureAllocatorDiagnosticSnapshot detached = VerifyCompleteMapping(pool.CaptureDiagnosticSnapshot());
        Assert.Equal(64, detached.OutstandingNativeBytes);
        Assert.Equal(64, detached.DetachedNativeBytes);
        Assert.Equal(64, detached.PeakOutstandingNativeBytes);
        Assert.Equal(NativeOwnerLifecycle.Disposed.ToString(), detached.Lifecycle);
        Assert.Equal(0, detached.AvailableSegmentCount);
        Assert.Equal(64, budget.CaptureStatistics().CommittedBytes);
        Assert.Throws<NativeAllocationDisposedException>(() => pool.Rent(1, static writer => writer.Write(31)));
        GC.KeepAlive(pool);
    }

    [VoxelDemonstrationFact]
    public void UnspecifiedSnapshotSerializesEveryUnsupportedFieldAsNullNotZero()
    {
        PressureAllocatorDiagnosticSnapshot unavailable = NativePressureSession.CaptureAllocatorDiagnostic(default);
        Assert.False(unavailable.Available);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(unavailable, VoxelJson.Options));
        JsonProperty[] fields = document.RootElement.EnumerateObject().ToArray();
        Assert.Equal(26, fields.Length);
        foreach (JsonProperty field in fields)
            Assert.Equal(string.Equals(field.Name, "available", StringComparison.Ordinal) ? JsonValueKind.False : JsonValueKind.Null, field.Value.ValueKind);
        Assert.Equal(default, JsonSerializer.Deserialize<PressureAllocatorDiagnosticSnapshot>(document.RootElement.GetRawText(), VoxelJson.Options));
    }

    private static PressureAllocatorDiagnosticSnapshot VerifyCompleteMapping(NativeOwnerDiagnosticSnapshot source)
    {
        PressureAllocatorDiagnosticSnapshot mapped = NativePressureSession.CaptureAllocatorDiagnostic(source);
        Assert.True(mapped.Available);
        Assert.Equal(source.Lifecycle.ToString(), mapped.Lifecycle);
        Assert.Equal(source.Model.ToString(), mapped.Model);
        Assert.Equal(source.OwnerId, mapped.OwnerId);
        Assert.Equal(source.Generation, mapped.Generation);
        Assert.Equal(source.ScopeEpoch, mapped.ScopeEpoch);
        Assert.Equal(source.MetricsEpoch, mapped.MetricsEpoch);
        Assert.Equal(source.ActiveRecords, mapped.ActiveRecords);
        Assert.Equal(source.ScopedRecords, mapped.ScopedRecords);
        Assert.Equal(source.ReferenceRoots, mapped.ReferenceRoots);
        Assert.Equal(source.OrdinaryTraversalIndex, mapped.OrdinaryTraversalIndex);
        Assert.Equal(source.ScopedTraversalIndex, mapped.ScopedTraversalIndex);
        Assert.Equal(source.RetainedSegmentCount, mapped.RetainedSegmentCount);
        Assert.Equal(source.AvailableSegmentCount, mapped.AvailableSegmentCount);
        Assert.Equal(source.RetiredGenerationCount, mapped.RetiredGenerationCount);
        Assert.Equal(source.RetiredSegmentCount, mapped.RetiredSegmentCount);
        Assert.Equal(source.RetiredBytes, mapped.RetiredBytes);
        Assert.Equal(source.QuarantinedGenerationCount, mapped.QuarantinedGenerationCount);
        Assert.Equal(source.QuarantinedSegmentCount, mapped.QuarantinedSegmentCount);
        Assert.Equal(source.CurrentGenerationQuarantined, mapped.CurrentGenerationQuarantined);
        Assert.Equal(source.HistoryOverflowed, mapped.HistoryOverflowed);
        Assert.Equal(source.OutstandingNativeBytes, mapped.OutstandingNativeBytes);
        Assert.Equal(source.DetachedNativeBytes, mapped.DetachedNativeBytes);
        Assert.Equal(source.PeakOutstandingNativeBytes, mapped.PeakOutstandingNativeBytes);
        Assert.Equal(source.InitializedPayloadBytes, mapped.InitializedPayloadBytes);
        Assert.Equal(source.PeakInitializedPayloadBytes, mapped.PeakInitializedPayloadBytes);
        Assert.Equal(mapped, JsonSerializer.Deserialize<PressureAllocatorDiagnosticSnapshot>(JsonSerializer.Serialize(mapped, VoxelJson.Options), VoxelJson.Options));
        return mapped;
    }

    private static void VerifyNativeCleanup(NativeOwnerDiagnosticSnapshot source)
    {
        PressureAllocatorDiagnosticSnapshot terminal = VerifyCompleteMapping(source);
        Assert.Equal(NativeOwnerLifecycle.Disposed.ToString(), terminal.Lifecycle);
        Assert.Equal(0, terminal.OutstandingNativeBytes);
        Assert.Equal(0, terminal.DetachedNativeBytes);
        Assert.Equal(0, terminal.RetainedSegmentCount);
        Assert.Equal(0, terminal.RetiredSegmentCount);
        Assert.Equal(0, terminal.RetiredBytes);
        Assert.Equal(0, terminal.RetiredGenerationCount);
        Assert.Equal(0, terminal.QuarantinedGenerationCount);
        Assert.Equal(0, terminal.QuarantinedSegmentCount);
        Assert.Equal(0, terminal.InitializedPayloadBytes);
    }
}
