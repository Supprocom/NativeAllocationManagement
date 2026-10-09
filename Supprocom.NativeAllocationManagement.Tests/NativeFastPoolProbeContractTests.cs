using System.Reflection;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeFastPoolProbeContractTests
{
    [Fact]
    public void CompleteOrdinaryProbeStateTracksInitializationAndNestedBorrows()
    {
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Verify(pool, new());
        using (Pooled<int> first = pool.Rent(1, static writer => writer.Write(17)))
        {
            Verify(pool, new() { Records = 1, Ordinals = [1] });
            using Pooled<int> second = pool.Rent(1, writer =>
            {
                Verify(pool, new() { Records = 2, Initializations = 1, Operations = 1, Ordinals = [1, 2] });
                using Pooled<int> entered = pool.Rent(1, static inner => inner.Write(17));
                _ = entered.Read(view =>
                {
                    Verify(pool, new() { Records = 3, Initializations = 1, Operations = 2, Ordinals = [1, 2, 3] });
                    using Pooled<int> nestedLease = pool.Rent(1, static inner => inner.Write(17));
                    int nested = nestedLease.Read(inner =>
                    {
                        Verify(pool, new() { Records = 4, Initializations = 1, Operations = 3, Ordinals = [1, 2, 3, 4] });
                        return inner[0];
                    });
                    Assert.Equal(17, nested);
                    Assert.Throws<InvalidOperationException>(pool.Dispose);
                    Assert.Throws<InvalidOperationException>(pool.Retire);
                    return view[0];
                });
                Verify(pool, new() { Records = 3, Initializations = 1, Operations = 1, Ordinals = [1, 2, 3, 4] });
                writer.Write(19);
            });
            Verify(pool, new() { Records = 2, Ordinals = [1, 2, 3, 4] });
            Assert.Equal(19, second.Read(static view => view[0]));
        }
        Verify(pool, new() { Ordinals = [1, 2, 3, 4] });
        _ = pool.TrimRetainedMemory();
        Verify(pool, new());
        pool.Dispose();
        Verify(pool, new() { Lifecycle = NativeOwnerLifecycle.Disposed });
    }

    [Fact]
    public void FailedAndEmptyInitializationDoNotInventPublicationOrPhysicalStorage()
    {
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Assert.Throws<InvalidOperationException>(() => pool.Rent(2, writer =>
        {
            Verify(pool, new() { Records = 1, Initializations = 1, Operations = 1, Ordinals = [1] });
            writer.Write(17);
        }));
        Verify(pool, new() { Ordinals = [1] });
        Assert.Throws<OperationCanceledException>(() => pool.Rent(2, writer =>
        {
            Verify(pool, new() { Records = 1, Initializations = 1, Operations = 1, Ordinals = [1] });
            writer.Write(19);
            throw new OperationCanceledException();
        }));
        Verify(pool, new() { Ordinals = [1] });
        using (Pooled<int> empty = pool.Rent(0, writer =>
        {
            Verify(pool, new() { Records = 1, Initializations = 1, Operations = 1, Ordinals = [1] });
            writer.Fill(0);
        }))
        {
            Verify(pool, new() { Records = 1, Ordinals = [1] });
            Assert.Equal(0, empty.Read(view =>
            {
                Verify(pool, new() { Records = 1, Operations = 1, Ordinals = [1] });
                return view.Length;
            }));
        }
        Verify(pool, new() { Ordinals = [1] });
        pool.Dispose();
        Verify(pool, new() { Lifecycle = NativeOwnerLifecycle.Disposed });
    }

    [Fact]
    public void PreparedProbesDistinguishSlotBanksFromWholePageRetention()
    {
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(4, 1, 2), budget: null);
        Expected expected = new() { PageCapacity = 2, Ordinals = [1, 2] };
        Verify(pool, expected);
        using (PreparedPooled<int> survivor = pool.Rent(1, static writer => writer.Write(23)))
        {
            Verify(pool, expected with { Records = 1 });
            using (PreparedPooled<int> other = pool.Rent(1, static writer => writer.Write(29)))
            {
                Verify(pool, expected with { Records = 2 });
                other.Access(_ => Verify(pool, expected with { Records = 2, Operations = 1 }));
            }
            Verify(pool, expected with { Records = 1 });
            Assert.Equal((nuint)8, pool.TrimRetainedMemory());
            expected = expected with { Ordinals = [2] };
            Verify(pool, expected with { Records = 1 });
            Assert.Equal(23, survivor.Read(static view => view[0]));
        }
        Verify(pool, expected);
        Assert.Equal((nuint)8, pool.TrimRetainedMemory());
        expected = expected with { Ordinals = [] };
        Verify(pool, expected);
        Assert.False(pool.TryRent(1, static writer => writer.Write(31), out _, out NativePoolExhaustionReason reason));
        Assert.Equal(NativePoolExhaustionReason.NoAvailableSlot, reason);
        Verify(pool, expected);
        pool.Dispose();
        Verify(pool, expected with { Lifecycle = NativeOwnerLifecycle.Disposed });
    }

    [Fact]
    public void OrdinalObservationsAreIndependentCopiesAndReuseDoesNotResurrectIdentity()
    {
        using NativePool<int> pool = new(preLease: 1, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Verify(pool, new() { Ordinals = [1] });
        long[] saved = pool.CurrentSegmentOrdinalsForTest;
        saved[0] = long.MaxValue;
        Verify(pool, new() { Ordinals = [1] });
        using (Pooled<int> reused = pool.Rent(1, static writer => writer.Write(37)))
        {
            Verify(pool, new() { Records = 1, Ordinals = [1] });
        }
        _ = pool.TrimRetainedMemory();
        Verify(pool, new());
        using (Pooled<int> fresh = pool.Rent(1, static writer => writer.Write(41)))
        {
            Verify(pool, new() { Records = 1, Ordinals = [2] });
        }
        Assert.Equal(long.MaxValue, saved[0]);
        Verify(pool, new() { Ordinals = [2] });
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "This test transfers the pool to its retired coordinator contract. Finally unconditionally releases retired storage, or disposes the still-active owner. IDisposable.Dispose deliberately rejects retired cleanup; using cannot represent this tested lifecycle.")]
    public void RetiredAndReleasedProbesCannotGrantFreshLeaseAuthority()
    {
        NativePool<int> pool = new(preLease: 1, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        bool retired = false;
        try
        {
            Verify(pool, new() { Ordinals = [1] });
            pool.Retire();
            retired = true;
            Verify(pool, new() { Lifecycle = NativeOwnerLifecycle.Returned, Ordinals = [1] });
            Assert.Throws<NativeAllocationDisposedException>(() => pool.Rent(1, static writer => writer.Write(43)));
        }
        finally
        {
            if (retired) pool.ReleaseRetiredStorage();
            else pool.Dispose();
        }
        Verify(pool, new() { Lifecycle = NativeOwnerLifecycle.Disposed });
        Assert.Throws<NativeAllocationDisposedException>(() => pool.Rent(1, static writer => writer.Write(47)));
    }

    [Fact]
    public void MissingGenerationMutationHooksStayUnsupportedWithoutChangingRealZeros()
    {
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Assert.Throws<NotSupportedException>(() => pool.SetScopeEpochForTest(long.MaxValue));
        Assert.Throws<NotSupportedException>(() => pool.SetGenerationCounterForTest(long.MaxValue));
        Verify(pool, new());
        pool.Dispose();
        Assert.Throws<NotSupportedException>(() => pool.SetScopeEpochForTest(1));
        Assert.Throws<NotSupportedException>(() => pool.SetGenerationCounterForTest(1));
        Verify(pool, new() { Lifecycle = NativeOwnerLifecycle.Disposed });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinalCaptureRequiresOwnerThreadEvenAfterRelease(bool disposed)
    {
        using NativePool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        if (disposed) pool.Dispose();
        Exception? failure = null;
        Thread other = new(() =>
        {
            try { _ = pool.CurrentSegmentOrdinalsForTest; }
            catch (InvalidOperationException error) { failure = error; }
        });
        other.Start();
        Assert.True(other.Join(TimeSpan.FromSeconds(10)));
        Assert.IsType<NativeAllocationStateException>(failure);
        Verify(pool, new() { Lifecycle = disposed ? NativeOwnerLifecycle.Disposed : NativeOwnerLifecycle.Active });
    }

    [Fact]
    public void ProbeInventoryExactlyMatchesAllTypedObservationsAndExecutableProofs() =>
        VerifyInventory(typeof(NativePool<int>), "native-fast-pool-probe-contracts.json", 13);

    [Fact]
    public void PreparedProbeInventoryExactlyMatchesAllActualTypedObservationsAndProofs() =>
        VerifyInventory(typeof(NativePreparedPool<int>), "native-prepared-pool-probe-contracts.json", 6);

    private static void VerifyInventory(Type ownerType, string fileName, int expectedCount)
    {
        string root = RepositoryTestPaths.Root;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "conformance", fileName)));
        JsonElement registry = document.RootElement;
        Assert.False(registry.GetProperty("completeReleaseInventory").GetBoolean());
        foreach (string key in new[] { "scope", "consistency", "resetPolicy", "unreviewedScope" })
            Assert.False(string.IsNullOrWhiteSpace(registry.GetProperty(key).GetString()), key);
        PropertyInfo[] observations = ownerType.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();
        JsonElement[] fields = registry.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(expectedCount, observations.Length);
        Assert.Equal(observations.Select(static property => property.Name), fields.Select(static field => field.GetProperty("name").GetString()!).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (JsonElement field in fields)
        {
            foreach (string key in new[] { "name", "units", "definition", "overflow", "allocation" })
                Assert.False(string.IsNullOrWhiteSpace(field.GetProperty(key).GetString()), key);
            PropertyInfo actual = observations.Single(property => string.Equals(property.Name, field.GetProperty("name").GetString(), StringComparison.Ordinal));
            Assert.Equal(actual.PropertyType.ToString(), field.GetProperty("valueType").GetString());
            string[] anchor = field.GetProperty("implementation").GetString()!.Split('#', 2);
            Assert.Equal(2, anchor.Length);
            Assert.Contains(anchor[1], File.ReadAllText(Path.Combine(root, "Supprocom.NativeAllocationManagement", anchor[0])), StringComparison.Ordinal);
            string[] proof = field.GetProperty("proof").GetString()!.Split('.', 2);
            Assert.Equal(2, proof.Length);
            Type type = typeof(NativeFastPoolProbeContractTests).Assembly.GetType(typeof(NativeFastPoolProbeContractTests).Namespace + "." + proof[0], throwOnError: true)!;
            MethodInfo method = type.GetMethod(proof[1], BindingFlags.Public | BindingFlags.Instance)!;
            Assert.NotNull(method);
            Assert.True(method.IsDefined(typeof(FactAttribute)) || method.IsDefined(typeof(TheoryAttribute)));
        }
    }

    private static void Verify(NativePool<int> pool, Expected expected)
    {
        Assert.Equal(expected.Lifecycle, pool.CurrentLifecycle);
        Assert.Equal(expected.Records, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(expected.Initializations, pool.CurrentInitializationCountForTest);
        Assert.Equal(expected.Operations, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal((expected.SlabCapacity, expected.SlabCapacity, 0, expected.PageCapacity), pool.CurrentBankCapacitiesForTest);
        Assert.Equal(0, pool.CurrentReferenceRootCountForTest);
        Assert.Equal(0, pool.QuarantinedSegmentCountForTest);
        Assert.Equal(0, pool.QuarantinedGenerationCountForTest);
        Assert.Equal(0, pool.RetiredGenerationCountForTest);
        Assert.Equal(0, pool.QuarantineCapacityForTest);
        Assert.Equal(0, pool.CurrentScopeEpochForTest);
        Assert.Equal(0, pool.GenerationCounterForTest);
        Assert.Equal(expected.Ordinals, pool.CurrentSegmentOrdinalsForTest);
    }

    private static void Verify(NativePreparedPool<int> pool, Expected expected)
    {
        Assert.Equal(expected.Lifecycle, pool.CurrentLifecycle);
        Assert.Equal(expected.Records, pool.CurrentAllocationRecordCountForTest);
        Assert.Equal(expected.Initializations, pool.CurrentInitializationCountForTest);
        Assert.Equal(expected.Operations, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal((expected.SlabCapacity, expected.SlabCapacity, 0, expected.PageCapacity), pool.CurrentBankCapacitiesForTest);
        Assert.Equal(expected.Ordinals, pool.CurrentSegmentOrdinalsForTest);
        NativeOwnerDiagnosticSnapshot actual = pool.CaptureDiagnosticSnapshot();
        Assert.Equal(expected.Lifecycle, actual.Lifecycle);
        Assert.Equal(expected.Records, actual.ActiveRecords);
        Assert.Equal(0, actual.ReferenceRoots);
        Assert.Equal(0, actual.RetiredGenerationCount);
        Assert.Equal(0, actual.QuarantinedGenerationCount);
        Assert.Equal(0, actual.QuarantinedSegmentCount);
        Assert.Equal(0, actual.ScopeEpoch);
        Assert.Equal(0, actual.Generation);
    }

    private sealed record Expected
    {
        internal NativeOwnerLifecycle Lifecycle { get; init; } = NativeOwnerLifecycle.Active;
        internal int Records { get; init; }
        internal int Initializations { get; init; }
        internal int Operations { get; init; }
        internal int SlabCapacity { get; init; } = 4;
        internal int PageCapacity { get; init; }
        internal long[] Ordinals { get; init; } = [];
    }
}
