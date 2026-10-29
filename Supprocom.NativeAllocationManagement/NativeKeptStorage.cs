namespace Supprocom.NativeAllocationManagement;

// A disposed budgeted owner's kernel or slabs, kept for its thread's next
// owner of the same kind with the same budget; see
// NativeRegionKernel.TryKeepBudgeted.
internal interface INativeKeptKernel
{
    // Whether the kept storage is charged to the budget; null matches any.
    bool IsKeptFor(NativeMemoryBudget? budget);

    // Frees what was kept and releases its budget charge. Only the thread
    // that took the kernel out of its slot calls this.
    void ReleaseKept();
}

// The per-thread slots that hold kept budgeted kernels. Unlike the other
// reuse slots, other threads take from these too, when a budget refuses an
// admission (NativeEndedThreads.ReleaseKeptFor), so every access is atomic:
// the owner thread puts a kernel in with an exchange and takes it with a
// compare-and-exchange, and whichever thread takes a kernel out owns it
// alone. A kernel in a slot is idle, and the kernels themselves need no
// synchronization.
//
// A budget that traces never has storage kept for it: kept storage would
// serve owners with other identities than the one its trace recorded. Its
// owners detach their storage for finalization, as documented.
internal static class NativeKeptStorage
{
    private static volatile bool s_keptAny;

    // False until any budgeted kernel was kept, so a budget that never had
    // one refuses without searching the threads.
    internal static bool MayHoldAny => s_keptAny;

    // The kept kernel in the slot if it is charged to the budget, or to any
    // budget when none is given; null if there is none or another thread
    // took it first.
    internal static T? Take<T>(ref T? slot, NativeMemoryBudget? budget)
        where T : class, INativeKeptKernel
    {
        T? kept = Volatile.Read(ref slot);
        return kept is not null
            && kept.IsKeptFor(budget)
            && Interlocked.CompareExchange(ref slot, null, kept) == kept
                ? kept
                : null;
    }

    // Keeps the kernel; one this slot held before is freed.
    internal static void Keep<T>(ref T? slot, T kernel)
        where T : class, INativeKeptKernel
    {
        if (!s_keptAny)
        {
            s_keptAny = true;
        }

        Interlocked.Exchange(ref slot, kernel)?.ReleaseKept();
    }

    internal static void Release<T>(ref T? slot, NativeMemoryBudget? budget)
        where T : class, INativeKeptKernel =>
        Take(ref slot, budget)?.ReleaseKept();
}
