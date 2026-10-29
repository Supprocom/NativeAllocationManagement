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

    private static long s_serials;

    // Unlike a managed thread id, never given to another thread, so an
    // owner's thread check (NativeOwnerThread) compares it.
    internal readonly long Serial = Interlocked.Increment(ref s_serials);

    // NativeOwnerThread: checks that missed the stack block, counted for tests.
    internal int OwnerThreadSlowChecks;

    // Once it ends, NativeEndedThreads returns what this state keeps.
    internal readonly Thread Thread = Thread.CurrentThread;

    // NativeMemoryAccounting: this thread's outstanding-bytes stripe.
    internal NativeMemoryAccounting.GaugeStripe? Gauge;

    // NativeRegionKernel: one disposed default-policy region kept for reuse,
    // and more behind it for nested scopes.
    internal NativeRegionKernel? ReusableRegion;
    internal NativeReuseStack<NativeRegionKernel>? MoreReusableRegions;

    // NativeArena: one disposed default-policy arena kernel, with its segment,
    // for the next arena on this thread, and more behind it for nested scopes.
    internal NativeArenaKernel? ReusableArena;
    internal NativeReuseStack<NativeArenaKernel>? MoreReusableArenas;

    // One disposed budgeted region and arena kernel, kept for this thread's
    // next owner with the same budget. Unlike the slots above, other threads
    // take them too, when their budget refuses an admission: only
    // NativeKeptStorage touches them.
    internal NativeRegionKernel? KeptBudgetedRegion;
    internal NativeArenaKernel? KeptBudgetedArena;

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
    private static NativeThreadState Create()
    {
        NativeThreadState state = new();
        t_current = state;
        NativeEndedThreads.Register(state);
        return state;
    }

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

    // Returns the storage this ended thread kept for its next owners; see
    // NativeEndedThreads.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Like the emergency finalizers this cleanup replaces, it must not fail the thread that runs it.")]
    internal void ReleaseStorageOfEndedThread()
    {
        try
        {
            NativeRegionKernel.ReleaseRetained(this);
            NativeArenaKernel.ReleaseRetained(this);
            foreach (object? typed in _typed)
            {
                (typed as INativeThreadStorage)?.ReleaseStorageOfEndedThread();
            }

            SpareFinalizationGuard?.Disarm();
            SpareFinalizationGuard = null;
        }
        catch
        {
        }
    }

    // Frees what this thread kept for owners with this budget; called from
    // any thread. A slot created while this runs may be missed: its storage
    // was kept after the refusal began.
    internal void ReleaseKeptFor(NativeMemoryBudget budget)
    {
        NativeKeptStorage.Release(ref KeptBudgetedRegion, budget);
        NativeKeptStorage.Release(ref KeptBudgetedArena, budget);
        foreach (object? typed in Volatile.Read(ref _typed))
        {
            (typed as INativeKeptBudgetStorage)?.ReleaseKeptFor(budget);
        }
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

// Per element type state that keeps native storage for the thread's next owners.
internal interface INativeThreadStorage
{
    void ReleaseStorageOfEndedThread();
}

// Per element type state that keeps budgeted storage, which other threads
// may take back for the budget.
internal interface INativeKeptBudgetStorage
{
    void ReleaseKeptFor(NativeMemoryBudget budget);
}
