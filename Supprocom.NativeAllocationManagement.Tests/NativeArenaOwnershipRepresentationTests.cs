using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeArenaOwnershipRepresentationTests
{
    [Fact]
    public void PublicArenaIsTheActualControlAndLeaseKeepsThatExactType()
    {
        Type owner = typeof(NativeArena);
        Assert.Null(owner.GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(owner.Assembly.GetType("Supprocom.NativeAllocationManagement.NativeArenaKernel"));
        Assert.Equal(owner, owner.GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(owner, owner.GetField("_ordinary", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(owner, owner.GetField("_budget", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(owner, typeof(ArenaLease<int>).GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        Assert.Equal(owner, owner.GetMethod("Finalize", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(5, owner.GetConstructors().Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyLeaseReferenceKeepsActualOwnerAndBackingAlive(bool prepared)
    {
        ArenaLease<int> lease = CreateLeaseOnlyOwner(prepared, out WeakReference<NativeArena> weak);
        NativeArena? closingOwner = null;
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.True(weak.TryGetTarget(out NativeArena? owner));
            Assert.Equal(42, lease.Read(values =>
            {
                Assert.Throws<NativeAllocationInUseException>(owner.Dispose);
                return values[0];
            }));
            Assert.Same(owner, owner.KernelForInitialization);
            Assert.Equal(NativeOwnerLifecycle.Active, owner.CaptureDiagnosticSnapshot().Lifecycle);
        }
        finally
        {
            _ = weak.TryGetTarget(out closingOwner);
            closingOwner?.Dispose();
        }
        Assert.NotNull(closingOwner);
        Assert.Equal(NativeOwnerLifecycle.Disposed, closingOwner.CaptureDiagnosticSnapshot().Lifecycle);
        Assert.Equal(0, closingOwner.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
        try
        {
            _ = lease.Read(static view => view.Length);
            Assert.Fail("An ended arena lease must reject access.");
        }
        catch (NativeAllocationDisposedException) { }
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
                0 => new(null!, 8, NativeMemoryReturn.ToNativeMemory),
                1 => new(new NativeArenaPreparation(8, 0), null!),
                _ => new(null!, new NativeArenaRetentionPolicy(4096, 4096), 8, NativeMemoryReturn.ToNativeMemory)
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
            using NativeArena owner = new(budget, default(NativeArenaRetentionPolicy), 8, NativeMemoryReturn.ToNativeMemory);
        });
        Assert.Equal("retentionPolicy", failure.ParamName);
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SuppressMessage("Reliability", "CA2000", Justification = "This adversarial helper transfers the sole owning lease to the caller, which closes its exact arena through the weak identity in finally; another strong owner root would invalidate the rooting proof.")]
    private static ArenaLease<int> CreateLeaseOnlyOwner(bool prepared, out WeakReference<NativeArena> weak)
    {
        NativeArena owner = prepared ? new(new NativeArenaPreparation(8, 0), new NativeMemoryBudget(128))
            : new(8, NativeMemoryReturn.ToNativeMemory);
        try
        {
            weak = new(owner);
            return owner.Scratch<int>(2, static writer => writer.Fill(42));
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }
}
