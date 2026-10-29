using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeArenaOwnershipRepresentationTests
{
    // The public arena is one lifetime of a kernel its thread reuses: it holds
    // only the kernel and that lifetime, the kernel holds the state, and leases
    // name the kernel.
    [Fact]
    public void PublicArenaBindsOneLifetimeOfItsKernelAndLeasesNameTheKernel()
    {
        Type owner = typeof(NativeArena);
        Type kernel = typeof(NativeArenaKernel);
        Assert.Equal(kernel, owner.GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        Assert.Equal(typeof(int), owner.GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        Assert.Null(owner.GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.False(kernel.IsPublic);
        Assert.Equal(kernel, kernel.GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(kernel, kernel.GetField("_ordinary", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(kernel, kernel.GetField("_budget", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(kernel, typeof(ArenaLease<int>).GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        // The emergency finalizer is a guard the kernel takes from its thread,
        // so creating an owner is not a finalizable allocation.
        Assert.Equal(typeof(object), owner.GetMethod("Finalize", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(typeof(object), kernel.GetMethod("Finalize", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(typeof(NativeFinalizationGuard), kernel.GetField("_guard", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        Assert.Equal(5, owner.GetConstructors().Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyLeaseReferenceKeepsKernelAndBackingAlive(bool prepared)
    {
        ArenaLease<int> lease = CreateLeaseOnlyOwner(prepared, out WeakReference<NativeArenaKernel> weak);
        NativeArenaKernel? closingKernel = null;
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.True(weak.TryGetTarget(out NativeArenaKernel? kernel));
            Assert.Equal(42, lease.Read(values =>
            {
                Assert.Throws<NativeAllocationInUseException>(kernel.Dispose);
                return values[0];
            }));
            Assert.Equal(NativeOwnerLifecycle.Active, kernel.GetDiagnosticSnapshot().Lifecycle);
        }
        finally
        {
            _ = weak.TryGetTarget(out closingKernel);
            closingKernel?.Dispose();
        }
        Assert.NotNull(closingKernel);
        Assert.Equal(NativeOwnerLifecycle.Disposed, closingKernel.GetDiagnosticSnapshot().Lifecycle);
        Assert.Equal(0, closingKernel.GetDiagnosticSnapshot().OutstandingNativeBytes);
        try
        {
            _ = lease.Read(static view => view.Length);
            Assert.Fail("An ended arena lease must reject access.");
        }
        catch (NativeAllocationDisposedException) { }
    }

    // A disposed arena's object stays disposed after its kernel serves the
    // next arena on the thread, and so do its leases.
    [Fact]
    public void AnArenaWhoseKernelServesTheNextArenaStaysDisposed()
    {
        NativeArena first = new(64);
        ArenaLease<int> stale = first.Scratch<int>(2, 5);
        NativeArenaKernel kernel = first.KernelForInitialization.KernelFor("test");
        long firstId = first.Id;
        first.Dispose();
        using NativeArena second = new();
        Assert.Same(kernel, second.KernelForInitialization.KernelFor("test"));
        Assert.NotEqual(firstId, second.Id);
        Assert.Equal(1, second.GetStatistics().Generation);
        Assert.Equal(NativeOwnerLifecycle.Disposed, first.CurrentLifecycle);
        Assert.Equal(NativeOwnerLifecycle.Active, second.CurrentLifecycle);
        Assert.Throws<NativeAllocationDisposedException>(() => first.Scratch<int>(1, 0));
        Assert.Throws<NativeAllocationDisposedException>(() => first.GetStatistics());
        Assert.Equal(NativeOwnerLifecycle.Disposed, first.CaptureDiagnosticSnapshot().Lifecycle);
        Assert.Equal(firstId, first.CaptureDiagnosticSnapshot().OwnerId);
        first.Dispose();
        try
        {
            _ = stale.Read(static view => view[0]);
            Assert.Fail("A lease of an earlier lifetime must reject access.");
        }
        catch (NativeAllocationDisposedException) { }

        Assert.Equal(7, second.Scratch<int>(1, 7).Read(static view => view[0]));
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
            using NativeArena owner = overload switch
            {
                0 => new(8, (NativeMemoryReturn)int.MaxValue),
                1 => new(budget, 8, (NativeMemoryReturn)int.MaxValue),
                _ => new(budget, new NativeArenaRetentionPolicy(4096, 4096), 8, (NativeMemoryReturn)int.MaxValue)
            };
        });
        Assert.Equal("returnMemoryOnDispose", failure.ParamName);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PublicBudgetConstructorsStillRejectNullBeforeBacking(int overload)
    {
        ArgumentNullException failure = Assert.Throws<ArgumentNullException>(() =>
        {
            using NativeArena owner = overload switch
            {
                0 => new(null!, 8, NativeMemoryReturn.Free),
                1 => new(new NativeArenaPreparation(8, 0), null!),
                _ => new(null!, new NativeArenaRetentionPolicy(4096, 4096), 8, NativeMemoryReturn.Free)
            };
        });
        Assert.Equal("budget", failure.ParamName);
    }

    [Fact]
    public void InvalidRetentionConfigurationNamesTheActualPublicParameterBeforeBacking()
    {
        NativeMemoryBudget budget = new(128);
        ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using NativeArena owner = new(budget, default(NativeArenaRetentionPolicy), 8, NativeMemoryReturn.Free);
        });
        Assert.Equal("retentionPolicy", failure.ParamName);
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SuppressMessage("Reliability", "CA2000", Justification = "This adversarial helper drops the public owner; the caller closes its exact kernel through the weak identity in finally, and another strong root would invalidate the rooting proof.")]
    private static ArenaLease<int> CreateLeaseOnlyOwner(bool prepared, out WeakReference<NativeArenaKernel> weak)
    {
        NativeArena owner = prepared ? new(new NativeArenaPreparation(8, 0), new NativeMemoryBudget(128))
            : new(8, NativeMemoryReturn.Free);
        try
        {
            weak = new(owner.KernelForInitialization.KernelFor("test"));
            return owner.Scratch<int>(2, static writer => writer.Fill(42));
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }
}
