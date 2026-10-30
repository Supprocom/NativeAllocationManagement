using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

    // The calling thread's serial, or zero, which no thread is given, when the
    // thread has no state yet: reading it creates none.
    internal static long CurrentSerialOrZero => t_current?.Serial ?? 0;

    // Current finds the calling thread's state by the 4 KiB stack block of the
    // caller's frame, before the thread-static, which linux-x64 reaches through
    // a call and four dependent loads (about 3 ns of a region with one lease).
    // s_byBlock maps a block to the state last found from it, and each state
    // keeps the blocks it was found from. As for NativeOwnerThread, no two live
    // threads have frames in one block, so a state found from the caller's block
    // is the caller's, unless its thread ended and the caller is a new thread
    // whose stack reuses that memory: the lookup refuses a state whose thread
    // ended (NativeThreadEnded). Where that mark can't be read, Current reads the
    // thread-static only. Correctness never depends on a hit: a miss reads the
    // thread-static and records the block.
    private const int BlockTableSize = 1024;
    private const int BlockSlotCount = 8;
    private static readonly NativeThreadState?[] s_byBlock = CreateBlockTable();

    // The blocks this state was found from, by the block's low three bits.
    private BlockSlots _blocks;

    // Lookups that missed the table, counted for tests.
    internal int BlockMisses;

    internal static NativeThreadState Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (!NativeThreadEnded.CanRead)
            {
                return t_current ?? Create();
            }

            nuint block = NativeStackBlockForTest.Current();
            NativeThreadState? state = Unsafe.Add(
                ref MemoryMarshal.GetArrayDataReference(s_byBlock),
                (nint)BlockSlot(block));
            return state is not null
                && state._blocks[(int)(block & (BlockSlotCount - 1))] == block
                && !NativeThreadEnded.Read(state.Thread)
                ? state
                : FindByBlock(block);
        }
    }

    // The calling thread's state from the thread-static, for tests.
    internal static NativeThreadState FromThreadStaticForTest => t_current ?? Create();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeThreadState Create()
    {
        NativeThreadState state = new();
        t_current = state;
        NativeEndedThreads.Register(state);
        return state;
    }

    // A miss: the thread-static, then the block in the state and the state in
    // the table. Only the state's own thread writes its blocks, so another
    // thread never finds its own block there.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeThreadState FindByBlock(nuint block)
    {
        NativeThreadState state = t_current ?? Create();
        state.BlockMisses++;
        state._blocks[(int)(block & (BlockSlotCount - 1))] = block;
        s_byBlock[(int)BlockSlot(block)] = state;
        return state;
    }

    // Thread stacks lie a fixed distance apart, a multiple of 1 MiB on Windows:
    // mixing in the higher bits spreads their frames at the same depth over the
    // table instead of sharing one slot.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint BlockSlot(nuint block) => (block ^ (block >> 8)) & (BlockTableSize - 1);

    internal static int BlockSlotForTest(nuint block) => (int)BlockSlot(block);

    internal static NativeThreadState? AtBlockSlotForTest(nuint block) => Volatile.Read(ref s_byBlock[(int)BlockSlot(block)]);

    private static NativeThreadState?[] CreateBlockTable()
    {
        if (NativeThreadEnded.CanRead)
        {
            _ = new EndedSweeper();
        }

        return new NativeThreadState?[BlockTableSize];
    }

    // NativeEndedThreads released this ended thread's state: the table drops it
    // too, so that it holds the state no longer than those weak references do.
    internal static void ForgetEnded(NativeThreadState state)
    {
        foreach (ref NativeThreadState? slot in s_byBlock.AsSpan())
        {
            if (ReferenceEquals(Volatile.Read(ref slot), state))
            {
                Interlocked.CompareExchange(ref slot, null, state);
            }
        }
    }

    // Drops the states whose thread ended, which no registration released, so
    // that a collection finalizes them and what they kept, as before the table.
    private static void ForgetAllEnded()
    {
        foreach (ref NativeThreadState? slot in s_byBlock.AsSpan())
        {
            NativeThreadState? state = Volatile.Read(ref slot);
            if (state is not null && NativeThreadEnded.Read(state.Thread))
            {
                Interlocked.CompareExchange(ref slot, null, state);
            }
        }
    }

    [InlineArray(BlockSlotCount)]
    private struct BlockSlots
    {
        private nuint _element;
    }

    // Finalized at every collection of its generation and registered again, so
    // once promoted it runs at each full collection (as NativeLargeBlocks.Trimmer).
    private sealed class EndedSweeper
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "A sweep failure must not terminate the process; the next collection retries.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0055", Justification = "The finalizer is the full-collection callback that sweeps the table.")]
        ~EndedSweeper()
        {
            try
            {
                ForgetAllEnded();
            }
            catch
            {
            }

            GC.ReRegisterForFinalize(this);
        }
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
