using System.Reflection;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeConcurrentArenaProbeContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void FastInitializationReportsRealReservationAndRollback(int length)
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: 128, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        _ = arena.Scratch<int>(length, writer =>
        {
            Assert.Equal(1, arena.CurrentInitializationCountForTest);
            Assert.Equal(0, arena.CurrentGenerationActiveOperationsForTest);
            Assert.Throws<NativeAllocationInUseException>(arena.Dispose);
            writer.Fill(17);
        });
        Assert.Equal(0, arena.CurrentInitializationCountForTest);
        Assert.Throws<OperationCanceledException>(() => FailInitialization(arena, length));
        Assert.Equal(0, arena.CurrentInitializationCountForTest);
        Assert.Equal(0, arena.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(23, arena.Scratch<int>(1, static writer => writer.Write(23)).Read(static view => view[0]));
    }

    [Fact]
    public void FastNestedBorrowsReportDepthWithoutInventingAllocationRecords()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: 128, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        ConcurrentArenaLease<int> outer = arena.Scratch<int>(1, static writer => writer.Write(29));
        int result = outer.Read(view =>
        {
            Assert.Equal(1, arena.CurrentGenerationActiveOperationsForTest);
            Assert.Equal(0, arena.CurrentInitializationCountForTest);
            ConcurrentArenaLease<int> inner = arena.Scratch<int>(1, writer =>
            {
                Assert.Equal(1, arena.CurrentInitializationCountForTest);
                Assert.Equal(1, arena.CurrentGenerationActiveOperationsForTest);
                writer.Write(31);
            });
            int value = inner.Read(nested =>
            {
                Assert.Equal(2, arena.CurrentGenerationActiveOperationsForTest);
                Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
                Assert.Throws<NativeAllocationInUseException>(arena.Dispose);
                return nested[0];
            });
            Assert.Equal(1, arena.CurrentGenerationActiveOperationsForTest);
            return view[0] + value;
        });
        Assert.Equal(60, result);
        Assert.Equal(0, arena.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(0, arena.CurrentInitializationCountForTest);
    }

    [Fact]
    public void CompactInitializersAndPreparedPublicationKeepTheirActualCounts()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: 128, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaTransferBatch<int> batch = arena.CreateTransferBatch<int>(3, 1);
        using NativeArenaTransferBatchInitialization<int> first = batch.BeginInitialization(0);
        Assert.Equal(1, arena.CurrentInitializationCountForTest);
        using NativeArenaTransferBatchInitialization<int> second = batch.BeginInitialization(1);
        Assert.Equal(2, arena.CurrentInitializationCountForTest);
        Assert.Equal(0, arena.CurrentGenerationActiveOperationsForTest);
        first.Values.Fill(37);
        second.Values.Fill(41);
        NativeArenaTransferBatchPublication<int> pending = first.PreparePublication();
        try
        {
            Assert.Equal(2, arena.CurrentInitializationCountForTest);
            NativeArenaTransferBatchLease<int> published = pending.Publish();
            try
            {
                Assert.Equal(1, arena.CurrentInitializationCountForTest);
                Assert.Equal(37, published.Read(static view => view[0]));
                second.Dispose();
                Assert.Equal(0, arena.CurrentInitializationCountForTest);
                Assert.Equal(1, arena.CurrentConcurrentReservationCountForTest);
                Assert.Throws<NativeAllocationInUseException>(() => InUseInitializer(arena, batch));
                Assert.Equal(0, arena.CurrentInitializationCountForTest);
            }
            finally { published.Dispose(); }
        }
        finally { pending.Dispose(); }
    }

    [Fact]
    public void CompactAndFastNestedBorrowsAreBothVisibleAndCloseExactly()
    {
        using NativeConcurrentArena arena = new(preAllocateBytes: 128, returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        NativeArenaTransferBatch<int> batch = arena.CreateTransferBatch<int>(1, 1);
        using NativeArenaTransferBatchInitialization<int> initialization = batch.BeginInitialization(0);
        initialization.Values.Fill(43);
        NativeArenaTransferBatchLease<int> published = initialization.Publish();
        try
        {
            Assert.Equal(43, published.Read(view =>
            {
                Assert.Equal(1, arena.CurrentGenerationActiveOperationsForTest);
                ConcurrentArenaLease<int> local = arena.Scratch<int>(1, static writer => writer.Write(47));
                Assert.Equal(47, local.Read(nested =>
                {
                    Assert.Equal(2, arena.CurrentGenerationActiveOperationsForTest);
                    Assert.Equal(1, arena.CurrentConcurrentReservationCountForTest);
                    Assert.Throws<NativeAllocationInUseException>(arena.Dispose);
                    return nested[0];
                }));
                Assert.Equal(1, arena.CurrentGenerationActiveOperationsForTest);
                return view[0];
            }));
        }
        finally { published.Dispose(); }
        Assert.Equal(0, arena.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(0, arena.CurrentConcurrentReservationCountForTest);
        Assert.Equal(0, arena.CurrentAllocationRecordCountForTest);
    }

    [Fact]
    public void TraversalDistinguishesRealSynchronizedFrontierFromFastCacheAndReset()
    {
        using NativeConcurrentArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        for (int index = 0; index < 8; index++)
            _ = arena.Scratch<byte>(4096, static writer => writer.Fill(53));
        NativeOwnerDiagnosticSnapshot ordinary = arena.CaptureDiagnosticSnapshot();
        Assert.Equal(3, ordinary.OrdinaryTraversalIndex);
        Assert.Equal((0, 3, 4), arena.CurrentBumpTraversalForTest);
        _ = arena.ScratchScoped<byte>(4096, static writer => writer.Fill(59));
        NativeOwnerDiagnosticSnapshot scoped = arena.CaptureDiagnosticSnapshot();
        Assert.Equal(3, scoped.OrdinaryTraversalIndex);
        Assert.Equal(3, scoped.ScopedTraversalIndex);
        Assert.Equal((0, 3, 4), arena.CurrentBumpTraversalForTest);
        ConcurrentArenaLease<string> rooted = arena.Scratch<string>(1, static writer => writer.Write("actual root"));
        Assert.Equal("actual root", rooted.Read(static view => view[0]));
        Assert.Equal((4, 4, 5), arena.CurrentBumpTraversalForTest);
        Assert.Equal(3, arena.CaptureDiagnosticSnapshot().OrdinaryTraversalIndex);
        arena.ReleaseLeasesToNativeMemory();
        Assert.Equal((0, 4, 5), arena.CurrentBumpTraversalForTest);
        Assert.Equal(0, arena.CurrentReferenceRootCountForTest);
        Assert.Equal(0, arena.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(0, arena.CurrentInitializationCountForTest);
    }

    [Fact]
    public void AbsentAndClosedGenerationZerosDoNotResurrectNativeAuthority()
    {
        using NativeConcurrentArena arena = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory, doNotLeaseOnDeclaration: true);
        Assert.Equal(0, arena.CurrentInitializationCountForTest);
        Assert.Equal(0, arena.CurrentGenerationActiveOperationsForTest);
        Assert.Equal((0, -1, 0), arena.CurrentBumpTraversalForTest);
        Assert.Throws<NativeAllocationStateException>(() => arena.Scratch<int>(1, static writer => writer.Write(61)));
        arena.LeaseFromMemory();
        ConcurrentArenaLease<int> stale = arena.Scratch<int>(1, static writer => writer.Write(61));
        arena.ReturnMemoryToNativeMemory();
        Assert.Equal(0, arena.CurrentInitializationCountForTest);
        Assert.Equal(0, arena.CurrentGenerationActiveOperationsForTest);
        Assert.Equal((0, -1, 0), arena.CurrentBumpTraversalForTest);
        try
        {
            _ = stale.Read(static view => view[0]);
            Assert.Fail("A diagnostic zero cannot make a returned handle active.");
        }
        catch (NativeAllocationReturnedException)
        {
        }
        arena.Dispose();
        Assert.Equal(0, arena.CurrentInitializationCountForTest);
        Assert.Equal(0, arena.CurrentGenerationActiveOperationsForTest);
        Assert.Equal((0, -1, 0), arena.CurrentBumpTraversalForTest);
    }

    private static void FailInitialization(NativeConcurrentArena arena, int length)
    {
        _ = arena.Scratch<int>(length, _ =>
        {
            Assert.Equal(1, arena.CurrentInitializationCountForTest);
            throw new OperationCanceledException();
        });
    }

    [Fact]
    public void RepairedProbeDefinitionsBindExactTypedGettersAndBothProofDirections()
    {
        string root = RepositoryTestPaths.Root;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "conformance", "native-concurrent-arena-observation-contracts.json")));
        JsonElement registry = document.RootElement;
        Assert.False(registry.GetProperty("completeReleaseInventory").GetBoolean());
        Assert.Equal(typeof(NativeConcurrentArena).FullName, registry.GetProperty("schema").GetString());
        foreach (string key in new[] { "scope", "unreviewedScope", "consistency", "resetPolicy" })
            Assert.False(string.IsNullOrWhiteSpace(registry.GetProperty(key).GetString()), key);
        string[] names = [nameof(NativeConcurrentArena.CurrentInitializationCountForTest), nameof(NativeConcurrentArena.CurrentGenerationActiveOperationsForTest), nameof(NativeConcurrentArena.CurrentBumpTraversalForTest)];
        JsonElement[] fields = registry.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(names.Order(StringComparer.Ordinal), fields.Select(static field => field.GetProperty("name").GetString()!).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (JsonElement field in fields)
        {
            foreach (string key in new[] { "definition", "units", "overflow", "allocation" })
                Assert.False(string.IsNullOrWhiteSpace(field.GetProperty(key).GetString()), key);
            PropertyInfo property = typeof(NativeConcurrentArena).GetProperty(field.GetProperty("name").GetString()!, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
            Assert.NotNull(property);
            Assert.Equal(property.PropertyType.ToString(), field.GetProperty("valueType").GetString());
            string[] anchor = field.GetProperty("implementation").GetString()!.Split('#', 2);
            Assert.Equal(2, anchor.Length);
            Assert.Contains(anchor[1], File.ReadAllText(Path.Combine(root, "Supprocom.NativeAllocationManagement", anchor[0])), StringComparison.Ordinal);
            foreach (string direction in new[] { "positiveProof", "negativeProof" })
            {
                MethodInfo proof = typeof(NativeConcurrentArenaProbeContractTests).GetMethod(field.GetProperty(direction).GetString()!, BindingFlags.Public | BindingFlags.Instance)!;
                Assert.NotNull(proof);
                Assert.True(proof.IsDefined(typeof(FactAttribute)) || proof.IsDefined(typeof(TheoryAttribute)));
            }
        }
    }

    private static void InUseInitializer(NativeConcurrentArena arena, NativeArenaTransferBatch<int> batch)
    {
        using NativeArenaTransferBatchInitialization<int> held = batch.BeginInitialization(2);
        Assert.Equal(1, arena.CurrentInitializationCountForTest);
        arena.Dispose();
    }
}
