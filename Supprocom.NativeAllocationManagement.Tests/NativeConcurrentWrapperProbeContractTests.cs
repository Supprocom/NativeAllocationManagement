using System.Reflection;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeConcurrentWrapperProbeContractTests
{
    [Fact]
    public void PoolInitializerAndNestedBorrowsReportTheirDistinctActualUnits()
    {
        using NativeConcurrentPool<string> pool = new(preLease: 2, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        using ConcurrentPooled<string> lease = pool.Rent(2, writer =>
        {
            Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
            Assert.Equal(1, pool.CurrentInitializationCountForTest);
            Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
            Assert.Equal(0, pool.CurrentReferenceRootCountForTest);
            writer.Write("first");
            Assert.Equal(1, pool.CurrentReferenceRootCountForTest);
            Assert.Throws<NativeAllocationInUseException>(pool.Dispose);
            writer.Write("second");
        });
        Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, pool.CurrentInitializationCountForTest);
        Assert.Equal(2, pool.CurrentReferenceRootCountForTest);
        Assert.Equal("firstsecond", lease.Read(view =>
        {
            Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
            Assert.Equal(0, pool.CurrentInitializationCountForTest);
            using ConcurrentPooled<string> inner = pool.Rent(1, static writer => writer.Write("nested"));
            Assert.Equal("nested", inner.Read(nested =>
            {
                Assert.Equal(2, pool.CurrentGenerationActiveOperationsForTest);
                Assert.Equal(2, pool.CurrentAllocationRecordCountForTest);
                Assert.Throws<NativeAllocationInUseException>(pool.Dispose);
                return nested[0];
            }));
            Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
            return view[0] + view[1];
        }));
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        lease.Dispose();
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, pool.CurrentReferenceRootCountForTest);
    }

    [Fact]
    public void PoolFailureAndEmptyPublicationDoNotInventBackingOrActiveBorrows()
    {
        using NativeConcurrentPool<string> pool = new(preLease: 2, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        long[] physical = pool.CurrentSegmentOrdinalsForTest;
        Assert.Collection(physical, static ordinal => Assert.True(ordinal > 0));
        Assert.Throws<OperationCanceledException>(() => pool.Rent(2, writer =>
        {
            writer.Write("unpublished");
            Assert.Equal(1, pool.CurrentInitializationCountForTest);
            Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
            Assert.Equal(1, pool.CurrentReferenceRootCountForTest);
            throw new OperationCanceledException();
        }));
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, pool.CurrentInitializationCountForTest);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(0, pool.CurrentReferenceRootCountForTest);
        Assert.Equal(physical, pool.CurrentSegmentOrdinalsForTest);
        using ConcurrentPooled<string> empty = pool.Rent(0, writer =>
        {
            Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
            Assert.Equal(1, pool.CurrentInitializationCountForTest);
            Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
            writer.Fill(string.Empty);
        });
        Assert.Equal(1, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(0, empty.Read(view =>
        {
            Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
            Assert.Equal(0, pool.CurrentInitializationCountForTest);
            return view.Length;
        }));
        Assert.Equal(physical, pool.CurrentSegmentOrdinalsForTest);
        Assert.Equal(0, pool.CurrentReferenceRootCountForTest);
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
    }

    [Fact]
    public void PoolPhysicalOrdinalsAreDetachedCopiesAndMetadataCapacitiesAreNotCounts()
    {
        using NativeConcurrentPool<int> pool = new(preLease: 4, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Assert.Equal([1L], pool.CurrentSegmentOrdinalsForTest);
        var capacities = pool.CurrentBankCapacitiesForTest;
        Assert.True(capacities.Slabs >= 1);
        Assert.True(capacities.AvailableSlabs >= 1);
        Assert.Equal(0, capacities.Bumps);
        Assert.True(capacities.OwnerSegments >= 1);
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        long[] saved = pool.CurrentSegmentOrdinalsForTest;
        saved[0] = long.MaxValue;
        Assert.Equal([1L], pool.CurrentSegmentOrdinalsForTest);
        using (ConcurrentPooled<int> large = pool.Rent(8, static writer => writer.Fill(17)))
        {
            Assert.Equal([1L, 2L], pool.CurrentSegmentOrdinalsForTest);
            Assert.Equal(17, large.Read(static view => view[7]));
        }
        Assert.Equal(0, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal([1L, 2L], pool.CurrentSegmentOrdinalsForTest);
        Assert.True(pool.TrimRetainedMemory() > 0);
        Assert.Empty(pool.CurrentSegmentOrdinalsForTest);
        Assert.True(pool.CurrentBankCapacitiesForTest.Slabs >= 2);
        Assert.Equal(long.MaxValue, saved[0]);
        pool.ReturnMemoryToNativeMemory();
        Assert.Equal((0, 0, 0, 0), pool.CurrentBankCapacitiesForTest);
        Assert.Empty(pool.CurrentSegmentOrdinalsForTest);
    }

    [Fact]
    public void ArenaPhysicalOrdinalsAreDetachedCopiesAndRootedStorageHasRealRoots()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: 128, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        long[] saved = arena.CurrentSegmentOrdinalsForTest;
        Assert.Equal([1L], saved);
        saved[0] = long.MaxValue;
        Assert.Equal([1L], arena.CurrentSegmentOrdinalsForTest);
        var capacities = arena.CurrentBankCapacitiesForTest;
        Assert.Equal(0, capacities.Slabs);
        Assert.Equal(0, capacities.AvailableSlabs);
        Assert.True(capacities.Bumps >= 1);
        Assert.True(capacities.OwnerSegments >= 1);
        ConcurrentArenaLease<string> rooted = arena.Scratch<string>(1, static writer => writer.Write("real root"));
        Assert.Equal(1, arena.CurrentReferenceRootCountForTest);
        Assert.Equal(1, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal("real root", rooted.Read(static view => view[0]));
        arena.ReleaseLeasesToNativeMemory();
        Assert.Equal(0, arena.CurrentReferenceRootCountForTest);
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
        VerifyReturned(rooted);
        Assert.Equal([1L], arena.CurrentSegmentOrdinalsForTest);
        Assert.Equal(long.MaxValue, saved[0]);
        arena.ReturnMemoryToNativeMemory();
        Assert.Equal((0, 0, 0, 0), arena.CurrentBankCapacitiesForTest);
        Assert.Empty(arena.CurrentSegmentOrdinalsForTest);
    }

    [Fact]
    public void KernelAuthorityIdentityDoesNotResurrectReturnedOrDisposedOwners()
    {
        using NativeConcurrentPool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using NativeConcurrentArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeOwnerKernel poolAuthority = pool.KernelForTransfer;
        NativeOwnerKernel arenaAuthority = arena.KernelForInitialization;
        Assert.NotSame(poolAuthority, arenaAuthority);
        Assert.Equal(NativeOwnerLifecycle.Active, poolAuthority.Lifecycle);
        Assert.Equal(NativeOwnerLifecycle.Active, arenaAuthority.Lifecycle);
        pool.ReturnMemoryToNativeMemory();
        arena.ReturnMemoryToNativeMemory();
        Assert.Same(poolAuthority, pool.KernelForTransfer);
        Assert.Same(arenaAuthority, arena.KernelForInitialization);
        Assert.Equal(NativeOwnerLifecycle.Returned, pool.CurrentLifecycle);
        Assert.Equal(NativeOwnerLifecycle.Returned, arena.CurrentLifecycle);
        Assert.Throws<NativeAllocationReturnedException>(() => pool.Rent(1, static writer => writer.Write(17)));
        Assert.Throws<NativeAllocationReturnedException>(() => arena.Scratch<int>(1, static writer => writer.Write(17)));
        pool.Dispose();
        arena.Dispose();
        Assert.Same(poolAuthority, pool.KernelForTransfer);
        Assert.Same(arenaAuthority, arena.KernelForInitialization);
        Assert.Equal(NativeOwnerLifecycle.Disposed, poolAuthority.Lifecycle);
        Assert.Equal(NativeOwnerLifecycle.Disposed, arenaAuthority.Lifecycle);
        Assert.Throws<NativeAllocationDisposedException>(() => pool.Rent(1, static writer => writer.Write(17)));
        Assert.Throws<NativeAllocationDisposedException>(() => arena.Scratch<int>(1, static writer => writer.Write(17)));
    }

    [Fact]
    public void CurrentPoolEpochAndGenerationIdentityFollowActualLifecycleNotPhysicalOrdinals()
    {
        using NativeConcurrentPool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory, doNotLeaseOnDeclaration: true);
        Assert.Equal(NativeOwnerLifecycle.Unleased, pool.CurrentLifecycle);
        Assert.Equal(0, pool.CurrentScopeEpochForTest);
        Assert.Equal(0, pool.GenerationCounterForTest);
        pool.LeaseFromMemory();
        long generation = pool.GenerationCounterForTest;
        Assert.Equal(0, generation);
        Assert.Equal(0, pool.CurrentScopeEpochForTest);
        scoped ConcurrentPooled<int> scopedLease = pool.LeaseScoped(1, static writer => writer.Write(23));
        long scope = pool.CurrentScopeEpochForTest;
        Assert.Equal(0, scope);
        pool.RecycleScoped();
        Assert.True(pool.CurrentScopeEpochForTest > scope);
        Assert.Equal(generation, pool.GenerationCounterForTest);
        VerifyReturned(scopedLease);
        pool.ReturnMemoryToNativeMemory();
        Assert.Equal(0, pool.CurrentScopeEpochForTest);
        Assert.True(pool.GenerationCounterForTest > generation);
        Assert.Empty(pool.CurrentSegmentOrdinalsForTest);
    }

    [Fact]
    public void CompactMetricsSeparateRejectedAttemptsLifetimeSlotsAndCurrentDictionaryRecords()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: 128, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaTransferBatch<int> batch = arena.CreateTransferBatch<int>(2, 1);
        Assert.Equal((0L, 2L, 0), arena.CurrentTransferMetricsForTest);
        using (NativeArenaTransferBatchInitialization<int> held = batch.BeginInitialization(0))
        {
            held.Values.Fill(11);
            _ = arena.Scratch<int>(1, writer =>
            {
                Assert.Equal(2, arena.CurrentInitializationCountForTest);
                Assert.Throws<NativeAllocationInUseException>(() => BeginCompactInitialization(batch));
                Assert.Equal((1L, 2L, 0), arena.CurrentTransferMetricsForTest);
                Assert.Equal(1, arena.CurrentConcurrentReservationCountForTest);
                Assert.Equal(1, arena.CurrentAllocationRecordCountForTest);
                Assert.Equal(2, arena.CurrentInitializationCountForTest);
                writer.Write(17);
            });
        }
        Assert.Equal(0, arena.CurrentConcurrentReservationCountForTest);
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
        Assert.Equal((1L, 2L, 0), arena.CurrentTransferMetricsForTest);
        using NativeArenaTransferBatchInitialization<int> initialization = batch.BeginInitialization(0);
        initialization.Values.Fill(23);
        NativeArenaTransferBatchLease<int> compact = initialization.Publish();
        try
        {
            Assert.Equal((1L, 2L, 0), arena.CurrentTransferMetricsForTest);
            Assert.Equal(1, arena.CurrentConcurrentReservationCountForTest);
            Assert.Equal(1, arena.CurrentAllocationRecordCountForTest);
            Assert.Equal(23, compact.Read(static view => view[0]));
        }
        finally { compact.Dispose(); }
        Assert.Equal(0, arena.CurrentConcurrentReservationCountForTest);
        ConcurrentArenaLease<string> rooted = arena.Scratch<string>(1, static writer => writer.Write("dictionary record"));
        Assert.Equal("dictionary record", rooted.Read(static view => view[0]));
        Assert.Equal((1L, 2L, 1), arena.CurrentTransferMetricsForTest);
        Assert.Equal(1, arena.CurrentReferenceRootCountForTest);
        arena.ReleaseLeasesToNativeMemory();
        Assert.Equal((1L, 2L, 0), arena.CurrentTransferMetricsForTest);
        Assert.Equal(0, arena.CurrentReferenceRootCountForTest);
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
        arena.Dispose();
        Assert.Equal((1L, 2L, 0), arena.CurrentTransferMetricsForTest);
    }

    [Fact]
    public void InventoryExactlyBindsEveryDeclaredTypedPropertyAndExecutableProof()
    {
        string root = RepositoryTestPaths.Root;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "conformance", "native-concurrent-wrapper-probe-contracts.json")));
        JsonElement registry = document.RootElement;
        Assert.False(registry.GetProperty("completeReleaseInventory").GetBoolean());
        Assert.Equal(28, registry.GetProperty("observationPropertyCount").GetInt32());
        Assert.Equal(2, registry.GetProperty("authorityPropertyCount").GetInt32());
        Type[] owners = [typeof(NativeConcurrentPool<int>), typeof(NativeConcurrentArena)];
        JsonElement[] schemas = registry.GetProperty("schemas").EnumerateArray().ToArray();
        Assert.Equal(owners.Length, schemas.Length);
        Assert.Equal(owners.Select(static owner => owner.ToString()).Order(StringComparer.Ordinal), schemas.Select(static schema => schema.GetProperty("type").GetString()!).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (Type owner in owners)
        {
            JsonElement schema = schemas.First(item => string.Equals(item.GetProperty("type").GetString(), owner.ToString(), StringComparison.Ordinal));
            PropertyInfo[] properties = owner.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();
            JsonElement[] fields = schema.GetProperty("fields").EnumerateArray().ToArray();
            Assert.Equal(owner == typeof(NativeConcurrentPool<int>) ? 14 : 16, properties.Length);
            Assert.Equal(properties.Select(static property => property.Name), fields.Select(static field => field.GetProperty("name").GetString()!).Order(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (JsonElement field in fields)
            {
                foreach (string key in new[] { "role", "definition", "units", "consistency", "resetPolicy", "overflow", "allocation" })
                    Assert.False(string.IsNullOrWhiteSpace(field.GetProperty(key).GetString()), key);
                PropertyInfo property = properties.First(item => string.Equals(item.Name, field.GetProperty("name").GetString(), StringComparison.Ordinal));
                Assert.Equal(property.PropertyType.ToString(), field.GetProperty("valueType").GetString());
                string[] anchor = field.GetProperty("implementation").GetString()!.Split('#', 2);
                Assert.Equal(2, anchor.Length);
                Assert.Contains(anchor[1], File.ReadAllText(Path.Combine(root, "Supprocom.NativeAllocationManagement", anchor[0])), StringComparison.Ordinal);
                foreach (string direction in new[] { "positiveProof", "negativeProof" }) VerifyProof(field.GetProperty(direction).GetString()!);
            }
        }
        JsonElement[] mutations = registry.GetProperty("faultMutationMethods").EnumerateArray().ToArray();
        Assert.Equal(2, mutations.Length);
        foreach (JsonElement mutation in mutations)
        {
            MethodInfo method = typeof(NativeConcurrentPool<int>).GetMethod(mutation.GetProperty("name").GetString()!, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, [typeof(long)])!;
            Assert.NotNull(method);
            Assert.Equal(typeof(void), method.ReturnType);
            VerifyProof(mutation.GetProperty("proof").GetString()!);
        }
    }

    private static void VerifyProof(string proof)
    {
        string[] names = proof.Split('.', 2);
        Assert.Equal(2, names.Length);
        Type type = typeof(NativeConcurrentWrapperProbeContractTests).Assembly.GetType(typeof(NativeConcurrentWrapperProbeContractTests).Namespace + "." + names[0], throwOnError: true)!;
        MethodInfo method = type.GetMethod(names[1], BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)!;
        Assert.NotNull(method);
        Assert.True(method.IsDefined(typeof(FactAttribute)) || method.IsDefined(typeof(TheoryAttribute)));
    }

    private static void BeginCompactInitialization(NativeArenaTransferBatch<int> batch)
    {
        using NativeArenaTransferBatchInitialization<int> unexpected = batch.BeginInitialization(0);
    }

    private static void VerifyReturned<T>(scoped ConcurrentPooled<T> lease)
    {
        try
        {
            _ = lease.Read(static view => view.Length);
            Assert.Fail("A diagnostic observation must not resurrect a stale scoped lease.");
        }
        catch (NativeAllocationReturnedException)
        {
        }
    }

    private static void VerifyReturned<T>(scoped ConcurrentArenaLease<T> lease)
    {
        try
        {
            _ = lease.Read(static view => view.Length);
            Assert.Fail("A diagnostic observation must not resurrect a released arena lease.");
        }
        catch (NativeAllocationReturnedException)
        {
        }
    }
}
