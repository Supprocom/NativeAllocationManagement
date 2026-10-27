using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

// One thread's private stock of a synchronized pool's records. The owning
// thread rents, enters, exits and returns its cached records with plain memory
// operations inside a short busy window and never takes the owner lock there,
// so threads that share a pool stop contending for its lock and counters.
//
// Anything that needs the pool's exact state (statistics, snapshots, generation
// transitions, trimming, disposal) first revokes the caches under the owner
// lock: it sets Revoked, runs a process-wide memory barrier, waits until each
// owner is out of its busy window and folds every cache back into the
// generation. The barrier makes the owner's plain "busy, then check Revoked"
// sequence safe: either the revoker sees the owner busy and waits, or the owner
// sees Revoked and takes the locked path. A revoked cache re-arms the next time
// its own thread takes the locked path.
//
// Only the owning thread writes a cache, inside a busy window or while holding
// the owner lock; a revoker reads it only after the owner left its window.
internal sealed class NativePoolThreadCache
{
    internal const int FreeCapacity = 4;
    internal const int IdBlockSize = 1024;

    internal int Busy;
    internal int Revoked = 1;
    internal readonly long KernelId;
    internal readonly int OwnerThreadId;
    internal readonly WeakReference<NativeOwnerKernel> Kernel;
    internal readonly WeakReference<Thread> OwnerThread;

    // The generation this cache serves while armed; null once folded.
    internal NativeGeneration? Generation;

    // Counts folds, so an operation that entered before a fold knows its
    // entry now belongs to the generation.
    internal int FoldEpoch;

    // Returned records, each still holding its slab.
    internal readonly NativeAllocation?[] Free = new NativeAllocation?[FreeCapacity];
    internal int FreeCount;

    // Records rented through this cache and not yet returned.
    internal NativeAllocation?[] Leased = new NativeAllocation?[4];
    internal int LeasedCount;

    // Allocation identities reserved for this thread: NextId through LastId.
    internal long NextId = 1;
    internal long LastId;

    // Initialized payload rented minus returned since the last fold, and its
    // high-water mark over the same span.
    internal long PayloadDelta;
    internal long MaxPayloadDelta;

    // Operations and initializers running on cached records since the last fold.
    internal int ActiveOperations;
    internal int Initializing;

    internal NativePoolThreadCache(NativeOwnerKernel kernel, Thread owner)
    {
        KernelId = kernel.Id;
        OwnerThreadId = NativeThreadIdentity.Current;
        Kernel = new WeakReference<NativeOwnerKernel>(kernel);
        OwnerThread = new WeakReference<Thread>(owner);
    }

    internal bool IsOwnerAlive =>
        OwnerThread.TryGetTarget(out Thread? owner) && owner.IsAlive;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Enter()
    {
        Volatile.Write(ref Busy, 1);
        if (Volatile.Read(ref Revoked) == 0)
        {
            return true;
        }

        Volatile.Write(ref Busy, 0);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Exit() => Volatile.Write(ref Busy, 0);

    internal void WaitUntilIdle()
    {
        SpinWait spinner = default;
        while (Volatile.Read(ref Busy) != 0)
        {
            spinner.SpinOnce();
        }
    }

    // Takes the smallest free record whose slab holds the length, like the
    // generation's own smallest-available-slab choice.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeAllocation? TakeFree(int length)
    {
        int count = FreeCount;
        int best = -1;
        int bestCapacity = int.MaxValue;
        for (int index = count - 1; index >= 0; index--)
        {
            int capacity = Free[index]!.Slab!.Capacity;
            if (capacity >= length && capacity < bestCapacity)
            {
                best = index;
                bestCapacity = capacity;
            }
        }

        if (best < 0)
        {
            return null;
        }

        NativeAllocation allocation = Free[best]!;
        if (best != --count)
        {
            Free[best] = Free[count];
        }

        Free[count] = null;
        FreeCount = count;
        return allocation;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddLeased(NativeAllocation allocation)
    {
        int count = LeasedCount;
        allocation.CacheSlot = count;
        Leased[count] = allocation;
        LeasedCount = count + 1;
    }

    // Grows the leased set before a rent changes any state.
    internal void ReserveLeased()
    {
        if (LeasedCount == Leased.Length)
        {
            Array.Resize(ref Leased, Leased.Length * 2);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RemoveLeased(NativeAllocation allocation)
    {
        int slot = allocation.CacheSlot;
        int last = LeasedCount - 1;
        if (slot != last)
        {
            NativeAllocation moved = Leased[last]!;
            Leased[slot] = moved;
            moved.CacheSlot = slot;
        }

        Leased[last] = null;
        LeasedCount = last;
    }
}

// The caches of the current thread, most recently used first.
internal static class NativePoolThreadCaches
{
    private const int PerThreadLimit = 8;

    [ThreadStatic]
    private static NativePoolThreadCache? t_recent;

    [ThreadStatic]
    private static NativePoolThreadCache?[]? t_all;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static NativePoolThreadCache? Find(long kernelId)
    {
        NativePoolThreadCache? recent = t_recent;
        return recent is not null && recent.KernelId == kernelId
            ? recent
            : FindSlow(kernelId);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativePoolThreadCache? FindSlow(long kernelId)
    {
        NativePoolThreadCache?[]? all = t_all;
        if (all is null)
        {
            return null;
        }

        foreach (NativePoolThreadCache? cache in all)
        {
            if (cache is not null && cache.KernelId == kernelId)
            {
                t_recent = cache;
                return cache;
            }
        }

        return null;
    }

    // Records a new cache for this thread. A full table hands back its least
    // recently used entry, which the caller detaches from its pool.
    internal static NativePoolThreadCache? Remember(NativePoolThreadCache cache)
    {
        NativePoolThreadCache?[] all = t_all ??= new NativePoolThreadCache?[PerThreadLimit];
        NativePoolThreadCache? evicted = all[^1];
        Array.Copy(all, 0, all, 1, all.Length - 1);
        all[0] = cache;
        t_recent = cache;
        return evicted;
    }

    internal static void Forget(NativePoolThreadCache cache)
    {
        if (ReferenceEquals(t_recent, cache))
        {
            t_recent = null;
        }

        NativePoolThreadCache?[]? all = t_all;
        if (all is null)
        {
            return;
        }

        int index = Array.IndexOf(all, cache);
        if (index >= 0)
        {
            Array.Copy(all, index + 1, all, index, all.Length - index - 1);
            all[^1] = null;
        }
    }
}
