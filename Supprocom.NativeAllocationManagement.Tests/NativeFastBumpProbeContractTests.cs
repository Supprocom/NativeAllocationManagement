using System.Reflection;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeFastBumpProbeContractTests
{
    [Fact]
    public void DefaultRegionObservationsDoNotInventOwnerOrPayloadAuthority()
    {
        NativeRegion region = default;
        Verify(region, NativeOwnerLifecycle.Uninitialized);
        bool denied = false;
        try { _ = region.Id; }
        catch (NativeAllocationUninitializedException failure)
        {
            Assert.Equal(NativeOwnerLifecycle.Uninitialized, failure.CurrentLifecycle);
            Assert.Equal(nameof(NativeRegion), failure.OwnerKind);
            Assert.Equal(nameof(NativeRegion.Id), failure.Operation);
            denied = true;
        }
        Assert.True(denied);
        denied = false;
        try { _ = region.Lease<int>(1, static writer => writer.Write(17)); }
        catch (NativeAllocationUninitializedException) { denied = true; }
        Assert.True(denied);
        denied = false;
        try { region.Dispose(); }
        catch (NativeAllocationUninitializedException) { denied = true; }
        Assert.True(denied);
        Verify(region, NativeOwnerLifecycle.Uninitialized);
    }

    [Fact]
    public void LiveRegionRangesAndTerminalStorageNeverBecomeAllocationRecords()
    {
        using NativeRegion region = new(64, NativeMemoryReturn.ToNativeMemory);
        Verify(region, NativeOwnerLifecycle.Active);
        Local<int> first = region.Lease<int>(3, static writer => writer.Fill(19));
        Local<long> second = region.Lease<long>(1, static writer => writer.Write(23));
        Local<byte> empty = region.Lease<byte>(0, static writer => writer.Fill(0));
        Verify(region, NativeOwnerLifecycle.Active);
        Assert.Equal(19, first.Read(static view => view[2]));
        Assert.Equal(23, second.Read(static view => view[0]));
        Assert.Equal(0, empty.Read(static view => view.Length));
        Assert.Equal(20, region.GetStatistics().RequestedBytes);
        Assert.True(region.GetStatistics().RetainedBytes >= 64);
        region.Dispose();
        Verify(region, NativeOwnerLifecycle.Disposed);
        Assert.Equal(0, region.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
        bool denied = false;
        try { _ = first.Read(static view => view[0]); }
        catch (NativeAllocationDisposedException) { denied = true; }
        Assert.True(denied);
        Verify(region, NativeOwnerLifecycle.Disposed);
    }

    [Fact]
    public void RegionIncompleteInitializationPreservesRealLifecycleAndReusesReservation()
    {
        using NativeRegion region = new(64, NativeMemoryReturn.ToNativeMemory);
        bool denied = false;
        try { _ = region.Lease<int>(2, static writer => writer.Write(29)); }
        catch (InvalidOperationException failure)
        {
            Assert.Equal("The native lease initializer wrote 1 of 2 required elements.", failure.Message);
            denied = true;
        }
        Assert.True(denied);
        Verify(region, NativeOwnerLifecycle.Active);
        Assert.Equal(0, region.GetStatistics().RequestedBytes);
        Assert.Equal(1, region.GetStatistics().SegmentCount);
        Local<int> complete = region.Lease<int>(2, static writer => writer.Fill(31));
        Assert.Equal(31, complete.Read(static view => view[1]));
        Assert.Equal(1, region.GetStatistics().FreshSegmentAllocationCount);
        Verify(region, NativeOwnerLifecycle.Active);
        region.Dispose();
        Verify(region, NativeOwnerLifecycle.Disposed);
    }

    [Fact]
    public void ArenaRangesEnteredCallbacksAndScopeReuseHaveNoRecordTable()
    {
        using NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Verify(arena, NativeOwnerLifecycle.Active);
        ArenaLease<int> ordinary = arena.Scratch<int>(2, static writer => writer.Fill(37));
        ArenaLease<int> scoped = arena.ScratchScoped<int>(1, static writer => writer.Write(41));
        _ = ordinary.Read(view =>
        {
            Verify(arena, NativeOwnerLifecycle.Active);
            Assert.Throws<NativeAllocationInUseException>(arena.Dispose);
            Assert.Throws<NativeAllocationInUseException>(arena.Reset);
            return view[1];
        });
        Assert.Equal(41, scoped.Read(static view => view[0]));
        Assert.True(arena.GetStatistics().RequestedBytes >= 12);
        arena.RecycleScoped();
        Verify(arena, NativeOwnerLifecycle.Active);
        Assert.Equal(37, ordinary.Read(static view => view[0]));
        bool denied = false;
        try { _ = scoped.Read(static view => view[0]); }
        catch (NativeAllocationReturnedException) { denied = true; }
        Assert.True(denied);
        arena.Reset();
        Verify(arena, NativeOwnerLifecycle.Active);
        Assert.Equal(0, arena.GetStatistics().RequestedBytes);
        _ = arena.Scratch<byte>(0, static writer => writer.Fill(0));
        Verify(arena, NativeOwnerLifecycle.Active);
        arena.Dispose();
        Verify(arena, NativeOwnerLifecycle.Disposed);
        Assert.Equal(0, arena.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
        Assert.Throws<NativeAllocationDisposedException>(() => arena.Scratch<int>(1, static writer => writer.Write(43)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArenaFailureRollbackAndPreparedReusePreserveActualProbeState(bool prepared)
    {
        using NativeArena arena = prepared
            ? new NativeArena(new NativeArenaPreparation(64, 64), new NativeMemoryBudget(4096))
            : new NativeArena(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Verify(arena, NativeOwnerLifecycle.Active);
        Assert.Throws<OperationCanceledException>(() => arena.Scratch<int>(2, writer =>
        {
            Verify(arena, NativeOwnerLifecycle.Active);
            writer.Write(47);
            throw new OperationCanceledException();
        }));
        Verify(arena, NativeOwnerLifecycle.Active);
        Assert.Equal(0, arena.GetStatistics().RequestedBytes);
        ArenaLease<int> complete = arena.Scratch<int>(2, writer =>
        {
            Verify(arena, NativeOwnerLifecycle.Active);
            writer.Fill(53);
        });
        Assert.Equal(53, complete.Read(static view => view[1]));
        Verify(arena, NativeOwnerLifecycle.Active);
        arena.Reset();
        Verify(arena, NativeOwnerLifecycle.Active);
        Assert.Equal(0, arena.GetStatistics().RequestedBytes);
        arena.Dispose();
        Verify(arena, NativeOwnerLifecycle.Disposed);
    }

    [Fact]
    public void InitializationKernelPropertyIsAuthorityNotNumericTelemetry()
    {
        using NativeArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaKernel kernel = arena.KernelForInitialization;
        Assert.Same(kernel, arena.KernelForInitialization);
        Assert.Equal(arena.Id, kernel.Id);
        Assert.Equal(NativeOwnerLifecycle.Active, kernel.Lifecycle);
        _ = arena.Scratch<int>(1, static writer => writer.Write(59));
        Verify(arena, NativeOwnerLifecycle.Active);
        arena.Dispose();
        Assert.Equal(NativeOwnerLifecycle.Disposed, kernel.Lifecycle);
        Verify(arena, NativeOwnerLifecycle.Disposed);
    }

    [Fact]
    public void InventoryAccountsForEveryPrivatePropertyWithoutHidingInitializationAuthority()
    {
        string root = RepositoryTestPaths.Root;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "conformance", "native-fast-bump-probe-contracts.json")));
        JsonElement registry = document.RootElement;
        Assert.False(registry.GetProperty("completeReleaseInventory").GetBoolean());
        int observations = 0;
        int authorities = 0;
        foreach (JsonElement schema in registry.GetProperty("schemas").EnumerateArray())
        {
            foreach (string key in new[] { "scope", "consistency", "resetPolicy" })
                Assert.False(string.IsNullOrWhiteSpace(schema.GetProperty(key).GetString()), key);
            Type type = typeof(NativeArena).Assembly.GetType(schema.GetProperty("schema").GetString()!, throwOnError: true)!;
            PropertyInfo[] actual = type.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();
            JsonElement[] entries = schema.GetProperty("members").EnumerateArray().ToArray();
            Assert.Equal(actual.Select(static property => property.Name), entries.Select(static entry => entry.GetProperty("name").GetString()!).Order(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (JsonElement entry in entries)
            {
                foreach (string key in new[] { "name", "kind", "units", "definition", "overflow", "allocation" })
                    Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty(key).GetString()), key);
                PropertyInfo property = actual.Single(property => string.Equals(property.Name, entry.GetProperty("name").GetString(), StringComparison.Ordinal));
                Assert.Equal(property.PropertyType.ToString(), entry.GetProperty("valueType").GetString());
                string[] anchor = entry.GetProperty("implementation").GetString()!.Split('#', 2);
                Assert.Equal(2, anchor.Length);
                Assert.Contains(anchor[1], File.ReadAllText(Path.Combine(root, "Supprocom.NativeAllocationManagement", anchor[0])), StringComparison.Ordinal);
                string[] proof = entry.GetProperty("proof").GetString()!.Split('.', 2);
                Type testType = typeof(NativeFastBumpProbeContractTests).Assembly.GetType(typeof(NativeFastBumpProbeContractTests).Namespace + "." + proof[0], throwOnError: true)!;
                MethodInfo method = testType.GetMethod(proof[1], BindingFlags.Public | BindingFlags.Instance)!;
                Assert.NotNull(method);
                Assert.True(method.IsDefined(typeof(FactAttribute)) || method.IsDefined(typeof(TheoryAttribute)));
                if (string.Equals(entry.GetProperty("kind").GetString(), "authority", StringComparison.Ordinal))
                {
                    Assert.Equal(nameof(NativeArena.KernelForInitialization), property.Name);
                    Assert.Equal(typeof(NativeArenaKernel), property.PropertyType);
                    authorities++;
                }
                else
                {
                    Assert.Equal("observation", entry.GetProperty("kind").GetString());
                    Assert.True(property.PropertyType == typeof(int) || property.PropertyType == typeof(NativeOwnerLifecycle));
                    observations++;
                }
            }
        }
        Assert.Equal(4, observations);
        Assert.Equal(1, authorities);
    }

    private static void Verify(scoped NativeRegion region, NativeOwnerLifecycle expected)
    {
        Assert.Equal(expected, region.CurrentLifecycle);
        Assert.Equal(0, region.CurrentAllocationRecordCountForTest);
    }

    private static void Verify(NativeArena arena, NativeOwnerLifecycle expected)
    {
        Assert.Equal(expected, arena.CurrentLifecycle);
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
    }
}
