using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeExceptionDiagnosticContractTests
{
    private const string PoolKind = "NativeConcurrentPool<System.Int32>";

    [Fact]
    public void DefaultHandleContainsActualAbsenceNotInventedOwnerState()
    {
        ConcurrentPooled<int> value = default;
        try
        {
            _ = value.Length;
            Assert.Fail("An uninitialized handle must not expose storage.");
        }
        catch (NativeAllocationUninitializedException failure)
        {
            Verify(failure, "ConcurrentPooled", 0, 0, "Length", 0, 0, NativeOwnerLifecycle.Uninitialized);
            Assert.Null(failure.InnerException);
        }
    }

    [Fact]
    public void ReturnedDecisionPreservesSourceIdentityAfterRolloverAndDisposal()
    {
        NativeMemoryBudget budget = new(4096);
        using NativeConcurrentPool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory, false);
        ConcurrentPooled<int> value = pool.Rent(1, static writer => writer.Write(42));
        pool.ReleaseLeasesToNativeMemory();
        NativeAllocationReturnedException? held = null;
        try
        {
            _ = value.Read(static view => view[0]);
            Assert.Fail("Returned storage must not be entered by a stale handle.");
        }
        catch (NativeAllocationReturnedException failure) { held = failure; }
        Assert.NotNull(held);
        Verify(held, PoolKind, 0, 1, "Read", 0, 1, NativeOwnerLifecycle.Active);
        NativeMemoryBudgetStatistics beforeObservation = budget.CaptureStatistics();
        _ = held.Message;
        Verify(held, PoolKind, 0, 1, "Read", 0, 1, NativeOwnerLifecycle.Active);
        Assert.Equal(beforeObservation, budget.CaptureStatistics());
        pool.Dispose();
        Verify(held, PoolKind, 0, 1, "Read", 0, 1, NativeOwnerLifecycle.Active);
        NativeAllocationDisposedException disposed = Assert.Throws<NativeAllocationDisposedException>(() => pool.Rent(1, static writer => writer.Write(7)));
        Verify(disposed, PoolKind, 2, 2, "Rent", 0, 0, NativeOwnerLifecycle.Disposed);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void UnleasedAndAlreadyActiveFailuresDoNotChangeTheObservedDecision()
    {
        using NativeConcurrentPool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory, doNotLeaseOnDeclaration: true);
        NativeAllocationStateException unleased = Assert.Throws<NativeAllocationStateException>(() => pool.Rent(1, static writer => writer.Write(7)));
        Verify(unleased, PoolKind, 0, 0, "Rent", 0, 0, NativeOwnerLifecycle.Unleased);
        pool.LeaseFromMemory();
        NativeAllocationStateException active = Assert.Throws<NativeAllocationStateException>(pool.LeaseFromMemory);
        Verify(active, PoolKind, 0, 0, "LeaseFromMemory", 0, 0, NativeOwnerLifecycle.Active);
        Verify(unleased, PoolKind, 0, 0, "Rent", 0, 0, NativeOwnerLifecycle.Unleased);
        using ConcurrentPooled<int> usable = pool.Rent(1, static writer => writer.Write(7));
        Assert.Equal(7, usable.Read(static view => view[0]));
    }

    [Fact]
    public void NestedBorrowRefusalReportsEnteredOperationsNotLeaseCount()
    {
        using NativeConcurrentPool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using ConcurrentPooled<int> first = pool.Rent(1, static writer => writer.Write(11));
        first.Access(view =>
        {
            Assert.Equal(11, view[0]);
            using ConcurrentPooled<int> second = pool.Rent(1, static writer => writer.Write(42));
            second.Access(nested =>
            {
                Assert.Equal(42, nested[0]);
                NativeAllocationInUseException failure = Assert.Throws<NativeAllocationInUseException>(pool.ReturnMemoryToNativeMemory);
                Verify(failure, PoolKind, 0, 0, "ReturnMemoryToNativeMemory", 2, 0, NativeOwnerLifecycle.Active, ownerWideBusy: 1);
                Assert.Equal(2, pool.CurrentGenerationActiveOperationsForTest);
            });
        });
        Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
        Assert.Equal(11, first.Read(static view => view[0]));
        first.Dispose();
        pool.ReturnMemoryToNativeMemory();
    }

    [Fact]
    public void UnpublishedInitializerIsARealOwnerWideReturnObligationNotABorrow()
    {
        using NativeConcurrentPool<int> pool = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        using ConcurrentPooled<int> value = pool.Rent(1, writer =>
        {
            NativeAllocationInUseException failure = Assert.Throws<NativeAllocationInUseException>(pool.Dispose);
            Verify(failure, PoolKind, 0, 0, "Dispose", 0, 0, NativeOwnerLifecycle.Active, ownerWideReturns: 1, ownerWideBusy: 1);
            Assert.Equal(1, pool.CurrentInitializationCountForTest);
            Assert.Equal(0, pool.CurrentGenerationActiveOperationsForTest);
            writer.Write(42);
        });
        Assert.Equal(42, value.Read(static view => view[0]));
        Assert.Equal(0, pool.CurrentInitializationCountForTest);
    }

    [Fact]
    public void QuarantineDecisionNamesActualEndedGenerationSegmentAndFailureBoundary()
    {
        NativeMemoryTestHooks.Reset();
        NativeMemoryBudget budget = new(4096);
        NativeConcurrentPool<int> pool = new(budget, 0, 0, NativeMemoryReturn.ToNativeMemory, false);
        try
        {
            ConcurrentPooled<int> value = pool.Rent(1, static writer => writer.Write(42));
            NativeAllocationQuarantinedException? held = null;
            try
            {
                value.Access(view =>
                {
                    pool.ReleaseLeasesToGarbageCollector();
                    Assert.Equal(42, view[0]);
                    NativeMemoryTestHooks.FailAfterCommitBoundary(1);
                });
                Assert.Fail("Injected post-commit cleanup failure must quarantine.");
            }
            catch (NativeAllocationQuarantinedException failure) { held = failure; }
            Assert.NotNull(held);
            Verify(held, PoolKind, 0, 1, "DrainRetiredGeneration", 0, 1, NativeOwnerLifecycle.Active);
            Assert.Equal(1L, held.SegmentOrdinal);
            Assert.Equal("clear", held.Boundary);
            Assert.IsType<InvalidOperationException>(held.InnerException);
            Assert.Equal(1, pool.QuarantinedGenerationCountForTest);
            Assert.Equal(1, pool.QuarantinedSegmentCountForTest);
            Assert.True(budget.CaptureStatistics().CommittedBytes > 0);
            Assert.Equal(0, budget.CaptureStatistics().FreeCount);
            NativeMemoryTestHooks.Reset();
            pool.Dispose();
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            Verify(held, PoolKind, 0, 1, "DrainRetiredGeneration", 0, 1, NativeOwnerLifecycle.Active);
            Assert.Equal(1L, held.SegmentOrdinal);
            Assert.Equal("clear", held.Boundary);
        }
        finally { NativeMemoryTestHooks.Reset(); pool.Dispose(); }
    }

    [Fact]
    public void BackendFailureReportsRequestedExtentWithoutInventingPublishedAllocation()
    {
        NativeMemoryTestHooks.Reset();
        NativeMemoryBudget budget = new(16);
        try
        {
            NativeMemoryTestHooks.FailNextAllocation();
            NativeAllocationFailedException failure = Assert.Throws<NativeAllocationFailedException>(() => new NativeWorkspace<int>(budget, 3));
            Verify(failure, "NativeWorkspace", 0, 0, "NativeWorkspace.Constructor", 0, 0, NativeOwnerLifecycle.Active);
            Assert.Equal((nuint)12, failure.RequestedBytes);
            Assert.Null(failure.InnerException);
            NativeMemoryBudgetStatistics after = budget.CaptureStatistics();
            Assert.Equal(0, after.CommittedBytes);
            Assert.Equal(0, after.ReservedBytes);
            Assert.Equal(0, after.AllocationCount);
            Assert.Equal(1, after.FailedAllocationCount);
            using NativeWorkspace<int> retry = new(budget, 3);
            Assert.Equal((nuint)12, failure.RequestedBytes);
            Assert.Equal(12, budget.CaptureStatistics().CommittedBytes);
        }
        finally { NativeMemoryTestHooks.Reset(); }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void BudgetRefusalSubtractsActualPendingAndOwnedExtentsAndStaysImmutable()
    {
        NativeMemoryBudget budget = new(32);
        using NativeWorkspace<byte> owned = new(budget, 8);
        Assert.True(budget.TryReserve<byte>(16, out NativeMemoryReservation<byte>? pending, out _));
        NativeMemoryReservation<byte> permission = NativeMemoryReservation<byte>.Move(ref pending);
        try
        {
            NativeMemoryBudgetExceededException failure = Assert.Throws<NativeMemoryBudgetExceededException>(() => new NativeWorkspace<byte>(budget, 9));
            Assert.Equal(budget.Id, failure.BudgetId);
            Assert.Equal(32, failure.CapacityBytes);
            Assert.Equal((nuint)9, failure.RequestedBytes);
            Assert.Equal(8, failure.AvailableBytes);
            Assert.Equal(8, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(16, budget.CaptureStatistics().ReservedBytes);
            permission.Dispose();
            owned.Dispose();
            Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
            Assert.Equal(8, failure.AvailableBytes);
            Assert.Equal((nuint)9, failure.RequestedBytes);
        }
        finally { if (permission.CaptureSnapshot().BindingIsActive) permission.Dispose(); }
    }

    [Fact]
    public void InventoryExactlyBindsAllRuntimeExceptionTypesAndNamDeclaredFields()
    {
        string root = RepositoryTestPaths.Root;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "conformance", "native-exception-diagnostic-contracts.json")));
        JsonElement registry = document.RootElement;
        Assert.False(registry.GetProperty("completeReleaseInventory").GetBoolean());
        Assert.Equal(16, registry.GetProperty("distinctPropertyCount").GetInt32());
        Assembly runtime = typeof(NativeAllocationException).Assembly;
        Type[] exceptions = runtime.GetExportedTypes().Where(static type => typeof(Exception).IsAssignableFrom(type)).OrderBy(static type => type.FullName, StringComparer.Ordinal).ToArray();
        JsonElement[] schemas = registry.GetProperty("schemas").EnumerateArray().ToArray();
        Assert.Equal(9, exceptions.Length);
        Assert.Equal(exceptions.Select(static type => type.FullName), schemas.Select(static schema => schema.GetProperty("type").GetString()).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        XDocument xml = XDocument.Load(Path.ChangeExtension(runtime.Location, ".xml"));
        Dictionary<string, XElement> definitions = xml.Descendants("member").ToDictionary(static member => (string)member.Attribute("name")!, StringComparer.Ordinal);
        JsonElement[] fields = registry.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(16, fields.Length);
        PropertyInfo[] distinct = exceptions.SelectMany(static type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)).OrderBy(static property => property.DeclaringType!.FullName + "." + property.Name, StringComparer.Ordinal).ToArray();
        Assert.Equal(distinct.Select(static property => property.DeclaringType!.FullName + "." + property.Name), fields.Select(static field => field.GetProperty("key").GetString()).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (Type exception in exceptions)
        {
            JsonElement schema = schemas.First(item => string.Equals(item.GetProperty("type").GetString(), exception.FullName, StringComparison.Ordinal));
            string[] actual = exception.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.DeclaringType!.Assembly == runtime).Select(static property => property.DeclaringType!.FullName + "." + property.Name).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(actual, schema.GetProperty("properties").EnumerateArray().Select(static property => property.GetString()).Order(StringComparer.Ordinal), StringComparer.Ordinal);
            VerifyProof(schema.GetProperty("eventProof").GetString()!);
        }
        foreach (JsonElement field in fields)
        {
            string key = field.GetProperty("key").GetString()!;
            PropertyInfo property = distinct.First(item => string.Equals(item.DeclaringType!.FullName + "." + item.Name, key, StringComparison.Ordinal));
            Assert.Equal(property.PropertyType.ToString(), field.GetProperty("valueType").GetString());
            Assert.Null(property.SetMethod);
            Assert.True(definitions.TryGetValue("P:" + key, out XElement? definition), key);
            Assert.False(string.IsNullOrWhiteSpace(definition.Value), key);
            foreach (string name in new[] { "definition", "units", "consistency", "resetPolicy", "overflow", "availability" })
                Assert.False(string.IsNullOrWhiteSpace(field.GetProperty(name).GetString()), name);
            string[] anchor = field.GetProperty("implementation").GetString()!.Split('#', 2);
            Assert.Equal(2, anchor.Length);
            Assert.Contains(anchor[1], File.ReadAllText(Path.Combine(root, "Supprocom.NativeAllocationManagement", anchor[0])), StringComparison.Ordinal);
            VerifyProof(field.GetProperty("positiveProof").GetString()!);
            VerifyProof(field.GetProperty("negativeProof").GetString()!);
        }
    }

    private static void Verify(NativeAllocationException failure, string kind, long generation, long current, string operation,
        int active, long allocation, NativeOwnerLifecycle lifecycle, int ownerWideReturns = 0, int ownerWideBusy = 0)
    {
        Assert.Equal(kind, failure.OwnerKind);
        Assert.Equal(generation, failure.Generation);
        Assert.Equal(current, failure.CurrentGeneration);
        Assert.Equal(operation, failure.Operation);
        Assert.Equal(active, failure.ActiveOperationCount);
        Assert.Equal(allocation, failure.AllocationId);
        Assert.Equal(lifecycle, failure.CurrentLifecycle);
        Assert.Equal(ownerWideReturns, failure.OwnerWideLeaseReturnCount);
        Assert.Equal(ownerWideBusy, failure.OwnerWideBusyGenerationCount);
    }

    private static void VerifyProof(string proof)
    {
        string[] names = proof.Split('.', 2);
        Assert.Equal(2, names.Length);
        Type type = typeof(NativeExceptionDiagnosticContractTests).Assembly.GetType(typeof(NativeExceptionDiagnosticContractTests).Namespace + "." + names[0], throwOnError: true)!;
        MethodInfo method = type.GetMethod(names[1], BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        Assert.NotNull(method);
        Assert.True(method.IsDefined(typeof(FactAttribute)) || method.IsDefined(typeof(TheoryAttribute)));
    }
}
