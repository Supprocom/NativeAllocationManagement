using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

// Internal entries for NativePoolScope. The pool is private to one Run call and is reachable
// only through a scoped ref struct on the calling thread, so these skip the owner-thread check
// (the precedent is NativePool<T>.ValidateLeaseOwner for Pooled<T>). Everything after
// validation is the helper Rent and RentUninitialized share.
public sealed unsafe partial class NativePool<T>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Pooled<T> RentForScope(int length, T value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateLeaseOwner(nameof(NativePoolScope.Run));
        return RentFilled(length, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Pooled<T> RentUninitializedForScope(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateLeaseOwner(nameof(NativePoolScope.Run));
        return RentUnfilled(length);
    }

    // Frees every slab, including ones with live leases, which Dispose refuses to do.
    // No Pooled<T> can outlive the scope: it is a ref struct, and the only way to rent
    // from this pool is through the scoped ref NativeScopePool<T> that Run lends to the
    // callback, so each lease is bound to that parameter and ends with the callback. A
    // lease smuggled out by unsafe code still fails: the pool is Disposed here, and Return
    // and every lease operation check the lifecycle (ValidateLeaseOwner).
    internal void EndScope()
    {
        if (_lifecycle == NativeOwnerLifecycle.Disposed)
        {
            return;
        }

        _lifecycle = NativeOwnerLifecycle.Disposed;
        _liveLeaseCount = 0;
        FreeAll();
        ReleaseGuard();
    }
}
