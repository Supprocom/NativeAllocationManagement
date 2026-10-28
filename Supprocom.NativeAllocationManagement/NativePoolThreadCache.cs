using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

// One thread's private stock of a synchronized pool's records, or its private
// bump lane in a synchronized arena. The owning
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
    internal readonly WeakReference<NativeOwnerKernel> Kernel;
    internal readonly WeakReference<Thread> OwnerThread;

    // The generation this cache serves while armed; null once folded.
    internal NativeGeneration? Generation;

    // Counts folds, so an operation that entered before a fold knows its
    // entry now belongs to the generation.
    internal int FoldEpoch;

    // Every record this cache holds, returned or leased; a record's CacheSlot
    // is its index here. Rents and returns move only slot numbers.
    internal NativeAllocation?[] Records = new NativeAllocation?[8];
    internal int RecordCount;

    // The slots of returned records, each still holding its slab, and the
    // capacities of those slabs.
    internal FreeSlotArray FreeSlots;
    internal FreeSlotArray FreeCapacities;
    internal int FreeCount;

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

    // Transferable leases rented through this cache. Any thread may return one,
    // so the list has its own short gate; a fold takes it too.
    private int _transferGate;
    internal NativeAllocation?[] Transfers = new NativeAllocation?[4];
    internal int TransferCount;

    // Transferable leases other threads returned, kept for this thread's next
    // rents so records flow back to the thread that rents them. Under the transfer gate.
    internal const int RemoteFreeCapacity = 64;
    internal readonly NativeAllocation?[] RemoteFree = new NativeAllocation?[RemoteFreeCapacity];
    internal int RemoteFreeCount;

    // An arena thread's private chunk of a shared bump segment while armed: it
    // allocates from LaneCursor up to LaneLimit. Chunks double per claim up to
    // the kernel's chunk limit.
    internal NativeBumpSegment? LaneSegment;
    internal nuint LaneCursor;
    internal nuint LaneLimit;
    internal nuint LaneChunkBytes = 4096;

    internal NativePoolThreadCache(NativeOwnerKernel kernel, Thread owner)
    {
        KernelId = kernel.Id;
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

    // The transfer gate spins: its holders move a few slots and never
    // allocate, block or throw while they hold it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnterTransferGate()
    {
        if (Interlocked.CompareExchange(ref _transferGate, 1, 0) != 0)
        {
            WaitForTransferGate();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitTransferGate() => Volatile.Write(ref _transferGate, 0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WaitForTransferGate()
    {
        SpinWait spinner = default;
        do
        {
            spinner.SpinOnce();
        }
        while (Volatile.Read(ref _transferGate) != 0
            || Interlocked.CompareExchange(ref _transferGate, 1, 0) != 0);
    }

    internal void WaitUntilIdle()
    {
        SpinWait spinner = default;
        while (Volatile.Read(ref Busy) != 0)
        {
            spinner.SpinOnce();
        }
    }

    internal bool HasRecordRoom => RecordCount < Records.Length;

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
            int capacity = FreeCapacities[index];
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

        int slot = FreeSlots[best];
        if (best != --count)
        {
            FreeSlots[best] = FreeSlots[count];
            FreeCapacities[best] = FreeCapacities[count];
        }

        FreeCount = count;
        return Records[slot];
    }

    // Marks a record this cache holds as returned. The caller checked for room.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddFree(NativeAllocation allocation)
    {
        int count = FreeCount;
        FreeSlots[count] = allocation.CacheSlot;
        FreeCapacities[count] = allocation.Capacity;
        FreeCount = count + 1;
    }

    // Removes the free record that has waited longest from the cache.
    internal NativeAllocation TakeOldestFree()
    {
        int slot = FreeSlots[0];
        int last = --FreeCount;
        FreeSlots[0] = FreeSlots[last];
        FreeCapacities[0] = FreeCapacities[last];
        NativeAllocation allocation = Records[slot]!;
        RemoveRecord(allocation);
        return allocation;
    }

    // Grows the record set before a record joins it, outside any busy window.
    internal void ReserveRecord()
    {
        if (RecordCount == Records.Length)
        {
            Array.Resize(ref Records, Records.Length * 2);
        }
    }

    // The caller made room.
    internal void AddRecord(NativeAllocation allocation)
    {
        int count = RecordCount;
        allocation.CacheSlot = count;
        Records[count] = allocation;
        RecordCount = count + 1;
    }

    internal void RemoveRecord(NativeAllocation allocation)
    {
        int slot = allocation.CacheSlot;
        int last = RecordCount - 1;
        if (slot != last)
        {
            NativeAllocation moved = Records[last]!;
            Records[slot] = moved;
            moved.CacheSlot = slot;
            for (int index = 0; index < FreeCount; index++)
            {
                if (FreeSlots[index] == last)
                {
                    FreeSlots[index] = slot;
                }
            }
        }

        Records[last] = null;
        RecordCount = last;
    }

    // Under the transfer gate, with room made by the owner.
    internal void AddTransfer(NativeAllocation allocation)
    {
        allocation.CacheSlot = TransferCount;
        Transfers[TransferCount++] = allocation;
    }

    // Under the transfer gate.
    internal void RemoveTransfer(NativeAllocation allocation)
    {
        int slot = allocation.CacheSlot;
        int last = TransferCount - 1;
        if (slot != last)
        {
            NativeAllocation moved = Transfers[last]!;
            Transfers[slot] = moved;
            moved.CacheSlot = slot;
        }

        Transfers[last] = null;
        TransferCount = last;
    }
}

[InlineArray(NativePoolThreadCache.FreeCapacity)]
internal struct FreeSlotArray
{
    private int _element;
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
