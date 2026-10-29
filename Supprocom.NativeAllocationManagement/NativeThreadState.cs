using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

// Everything NAM keeps per thread, behind one thread-static reference. Each
// [ThreadStatic] read is a TLS call (about 1.9 ns on the reference host), and
// a direct transfer's round trip used to make seven of them; hot paths read
// Current once and pass it along.
internal sealed class NativeThreadState
{
    [ThreadStatic]
    private static NativeThreadState? t_current;

    private static int s_typedSlotCount;

    // NativeOwnerIdentity: the identities this thread claimed and has not handed out.
    internal long NextOwnerId;
    internal long OwnerIdEnd;

    // NativeStorageReuse: this thread's reuse slots from an older epoch are discarded.
    internal int ReuseEpoch;

    // Managed thread ids start at one and never change for a thread.
    internal readonly int ManagedThreadId = Environment.CurrentManagedThreadId;

    // NativeMemoryAccounting: this thread's outstanding-bytes stripe.
    internal NativeMemoryAccounting.GaugeStripe? Gauge;

    // NativeRegionKernel: one disposed default-policy region kept for reuse,
    // and more behind it for nested scopes.
    internal NativeRegionKernel? ReusableRegion;
    internal NativeReuseStack<NativeRegionKernel>? MoreReusableRegions;

    // NativeArena: one disposed default-policy arena whose segment the next
    // arena on this thread takes, and more behind it for nested scopes.
    internal NativeArena? ReusableArena;
    internal NativeReuseStack<NativeArena>? MoreReusableArenas;

    // NativeFinalizationGuard: one guard, still registered for finalization,
    // waiting for the next arena or pool created on this thread.
    internal NativeFinalizationGuard? SpareFinalizationGuard;

    // Per element type state, such as parked transfer controls, by slot index.
    private object?[] _typed = new object?[8];

    internal static NativeThreadState Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => t_current ?? Create();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeThreadState Create() => t_current = new NativeThreadState();

    // Called once per generic instantiation from a static readonly initializer,
    // which tiered compilation then folds into a constant.
    internal static int AllocateTypedSlot() => Interlocked.Increment(ref s_typedSlotCount) - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal TSlot? GetTyped<TSlot>(int slot)
        where TSlot : class
    {
        object?[] typed = _typed;
        return (uint)slot < (uint)typed.Length ? Unsafe.As<TSlot>(typed[slot]) : null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void SetTyped(int slot, object? value)
    {
        if ((uint)slot >= (uint)_typed.Length)
        {
            Array.Resize(ref _typed, Math.Max(slot + 1, _typed.Length * 2));
        }

        _typed[slot] = value;
    }
}
