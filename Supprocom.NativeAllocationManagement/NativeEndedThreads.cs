namespace Supprocom.NativeAllocationManagement;

// Storage NAM keeps on a thread for the thread's next owners (reuse slots,
// parked transfer blocks, shared pools) used to wait, once the thread ended,
// for a garbage collection to finalize it, which may not come for a long time
// in a program that allocates little managed memory. Threads started one
// after another, each disposing one 900 KiB pool, left 124 MB in malloc after
// 2,000 of them, and NativePool<byte>.Shared with 4 MiB each 496 MB; each
// thread also allocated its storage afresh.
//
// Each thread that starts using NAM now checks some registered threads, and
// returns the storage of those that ended as their finalizers would: large
// blocks to NativeLargeBlocks, where the new thread finds them, the rest to
// native memory. An ended thread runs no code, and everything it kept for
// reuse is idle, so another thread can return it. Registered states are
// weakly held, so a collection still finalizes those no check reached.
internal static class NativeEndedThreads
{
    // Threads checked per registration: enough to keep pace with threads that
    // end as fast as new ones start, while a process with many live threads
    // checks only some of them each time.
    private const int ChecksPerRegistration = 32;

    private static readonly Lock s_gate = new();
    private static WeakReference<NativeThreadState>[] s_states = new WeakReference<NativeThreadState>[16];
    private static int s_count;
    private static int s_next;

    // Every registered thread's state, live or ended, not yet collected.
    internal static List<NativeThreadState> StatesForTest()
    {
        List<NativeThreadState> states = [];
        lock (s_gate)
        {
            foreach (ref readonly WeakReference<NativeThreadState> reference in s_states.AsSpan(0, s_count))
            {
                if (reference.TryGetTarget(out NativeThreadState? state))
                {
                    states.Add(state);
                }
            }
        }

        return states;
    }

    // Checks every registered thread. The release records frees on the calling
    // thread's state, which must exist first: creating it inside the lock
    // would release from within this release.
    internal static void ReleaseEndedForTest()
    {
        _ = NativeThreadState.Current;
        lock (s_gate)
        {
            ReleaseEnded(s_count);
        }
    }

    // A budget refused an admission: every registered thread, live or ended,
    // gives back the kernels it kept charged to that budget. Each is taken
    // from its slot with a compare-and-exchange (NativeKeptStorage), so its
    // own thread either took it first or finds the slot empty.
    internal static void ReleaseKeptFor(NativeMemoryBudget budget)
    {
        // The release records frees on the calling thread's state, which must
        // exist before the lock: creating it registers it under the same lock.
        _ = NativeThreadState.Current;
        lock (s_gate)
        {
            foreach (ref readonly WeakReference<NativeThreadState> reference in s_states.AsSpan(0, s_count))
            {
                if (reference.TryGetTarget(out NativeThreadState? state))
                {
                    state.ReleaseKeptFor(budget);
                }
            }
        }
    }

    internal static void Register(NativeThreadState state)
    {
        lock (s_gate)
        {
            ReleaseEnded(ChecksPerRegistration);
            if (s_count == s_states.Length)
            {
                Array.Resize(ref s_states, s_count * 2);
            }

            s_states[s_count++] = new WeakReference<NativeThreadState>(state);
        }
    }

    private static void ReleaseEnded(int checks)
    {
        bool fenced = false;
        for (checks = Math.Min(s_count, checks); checks > 0; checks--)
        {
            if (s_next >= s_count)
            {
                s_next = 0;
            }

            if (s_states[s_next].TryGetTarget(out NativeThreadState? state))
            {
                if (state.Thread.IsAlive)
                {
                    s_next++;
                    continue;
                }

                // Everything the ended thread wrote before it ended is visible
                // after this fence.
                if (!fenced)
                {
                    Interlocked.MemoryBarrier();
                    fenced = true;
                }

                state.ReleaseStorageOfEndedThread();
            }

            // The last entry, not yet checked, takes this one's place.
            s_states[s_next] = s_states[--s_count];
            s_states[s_count] = null!;
        }
    }
}
