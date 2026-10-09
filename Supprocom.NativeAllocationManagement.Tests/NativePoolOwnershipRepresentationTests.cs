using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePoolOwnershipRepresentationTests
{
    [Fact]
    public void PublicPoolIsTheActualControlAndLeaseKeepsThatExactType()
    {
        Type owner = typeof(NativePool<int>);
        Assert.Null(owner.GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(owner.Assembly.GetType("Supprocom.NativeAllocationManagement.NativePoolKernel`1"));
        Assert.Equal(owner, owner.GetField("_leaseTokenCounter", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(owner, owner.GetField("_slabs", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(owner, owner.GetField("_budget", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(owner, typeof(Pooled<int>).GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        Assert.Equal(owner, owner.GetMethod("Finalize", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(3, owner.GetConstructors().Length);
    }

    [Fact]
    public void VariablePoolHasNoFixedShapeModeOrPageBank()
    {
        Type owner = typeof(NativePool<int>);
        foreach (string field in new[]
        {
            "_preparation", "_pages", "_peakOccupiedSlots", "_successfulPreparedRents",
            "_rejectedPreparedShapes", "_rejectedPreparedFull", "_preparedInitializerFailures"
        })
            Assert.Null(owner.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(owner.GetNestedType("Page", BindingFlags.NonPublic));
        foreach (string method in new[]
        {
            "TryRent", "CapturePreparedSnapshot", "GetPreparedStatistics",
            "InitializePreparedSlab", "ValidatePrepared", "IsPageIdle", "FreePage"
        })
            Assert.Null(owner.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.DoesNotContain(owner.GetConstructors(), static constructor =>
            constructor.GetParameters().Any(static parameter => parameter.ParameterType == typeof(NativePoolPreparation)));
        using NativePool<int> ordinary = new(returnMemoryOnDispose: NativeMemoryReturn.ToNativeMemory);
        Assert.Equal((4, 4, 0, 0), ordinary.CurrentBankCapacitiesForTest);
        using NativePreparedPool<int> prepared = new(new NativePoolPreparation(3, 2, 2), budget: null);
        Assert.Equal((3, 3, 0, 2), prepared.CurrentBankCapacitiesForTest);
        Assert.Equal(2, prepared.GetStatistics().SegmentCount);
    }

    [Theory]
    [InlineData("using NativePool<int> pool = new(new NativePoolPreparation(2, 4, 2), null);", "CS1503")]
    [InlineData("using NativePool<int> pool = new(4, NativeMemoryReturn.ToNativeMemory); _ = pool.TryRent(4, static writer => writer.Fill(42), out Pooled<int> lease, out _);", "CS1061")]
    [InlineData("using NativePool<int> pool = new(4, NativeMemoryReturn.ToNativeMemory); _ = pool.CapturePreparedSnapshot();", "CS1061")]
    public void LegacyFixedShapeSurfaceFailsAtCompilation(string body, string expected)
    {
        string source = $$"""
            using Supprocom.NativeAllocationManagement;
            public static class Sample
            {
                public static void Run() { {{body}} }
            }
            """;
        Assert.Contains(AnalyzerContractTests.Compile(source), diagnostic =>
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
            && string.Equals(diagnostic.Id, expected, StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyLeaseReferenceKeepsActualOwnerAndBackingAlive()
    {
        Pooled<int> lease = CreateLeaseOnlyOwner(out WeakReference<NativePool<int>> weak);
        NativePool<int>? closingOwner = null;
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.True(weak.TryGetTarget(out NativePool<int>? owner));
            Assert.Equal(42, lease.Read(static view => view[0]));
            Assert.Equal(1, owner.CurrentAllocationRecordCountForTest);
            Assert.Throws<InvalidOperationException>(owner.Dispose);
            Assert.Equal(NativeOwnerLifecycle.Active, owner.CaptureDiagnosticSnapshot().Lifecycle);
        }
        finally
        {
            _ = weak.TryGetTarget(out closingOwner);
            try { lease.Dispose(); }
            finally { closingOwner?.Dispose(); }
        }
        Assert.NotNull(closingOwner);
        Assert.Equal(NativeOwnerLifecycle.Disposed, closingOwner.CaptureDiagnosticSnapshot().Lifecycle);
        Assert.Equal(0, closingOwner.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryOrdinaryConstructorRejectsInvalidCleanupBeforeBacking(int overload)
    {
        NativeMemoryBudget budget = new(128);
        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using NativePool<int> owner = overload switch
            {
                0 => new(2, (NativeMemoryReturn)int.MaxValue),
                1 => new(2, 8, (NativeMemoryReturn)int.MaxValue),
                _ => new(budget, 2, 8, (NativeMemoryReturn)int.MaxValue)
            };
        });
        Assert.Equal("returnMemoryOnDispose", failure.ParamName);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
    }

    [Fact]
    public void PublicBudgetConstructorStillRejectsNullBeforeBacking()
    {
        ArgumentNullException failure = Assert.Throws<ArgumentNullException>(() =>
        {
            using NativePool<int> owner = new(null!, 2, 8, NativeMemoryReturn.ToNativeMemory);
        });
        Assert.Equal("budget", failure.ParamName);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SuppressMessage("Reliability", "CA2000", Justification = "This adversarial lifetime helper transfers the sole owning lease to the calling test, which returns it and disposes the exact owner through its weak identity in finally. The intentional absence of another strong owner reference is the behavior under test.")]
    private static Pooled<int> CreateLeaseOnlyOwner(out WeakReference<NativePool<int>> weak)
    {
        NativePool<int> owner = new(2, NativeMemoryReturn.ToNativeMemory);
        try
        {
            weak = new(owner);
            return owner.Rent(2, static writer => writer.Fill(42));
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }
}
