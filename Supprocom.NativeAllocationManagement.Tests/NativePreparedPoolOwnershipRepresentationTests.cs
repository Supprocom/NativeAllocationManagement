using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedPoolOwnershipRepresentationTests
{
    [Fact]
    public void PublicPoolIsTheActualControlAndLeaseKeepsThatExactType()
    {
        Type owner = typeof(NativePreparedPool<int>);
        Assert.Null(owner.GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(owner.Assembly.GetType("Supprocom.NativeAllocationManagement.NativePoolKernel`1"));
        Assert.Equal(owner, owner.GetField("_leaseTokenCounter", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(owner, owner.GetField("_slabs", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(owner, owner.GetField("_budget", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(owner, typeof(PreparedPooled<int>).GetField("_kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        Assert.Equal(owner, owner.GetMethod("Finalize", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.True(owner.GetConstructors().Length is 1);
    }

    [Fact]
    public void OnlyLeaseReferenceKeepsActualOwnerAndBackingAlive()
    {
        PreparedPooled<int> lease = CreateLeaseOnlyOwner(out WeakReference<NativePreparedPool<int>> weak);
        NativePreparedPool<int>? closingOwner = null;
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.True(weak.TryGetTarget(out NativePreparedPool<int>? owner));
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SuppressMessage("Reliability", "CA2000", Justification = "This adversarial lifetime helper transfers the sole owning lease to the calling test, which returns it and disposes the exact owner through its weak identity in finally. The intentional absence of another strong owner reference is the behavior under test.")]
    private static PreparedPooled<int> CreateLeaseOnlyOwner(out WeakReference<NativePreparedPool<int>> weak)
    {
        NativePreparedPool<int> owner = new(new NativePoolPreparation(1, 2, 1), budget: null);
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
