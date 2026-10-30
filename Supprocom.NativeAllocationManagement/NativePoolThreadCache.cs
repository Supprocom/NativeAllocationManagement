using System.Numerics;
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
    // Deep enough for a thread that holds several leases at once: a return to a
    // full free list or a rent from an empty one takes the owner lock. Idle
    // records belong to the pool either way, and a pool takes other threads'
    // idle records back before it grows.
    internal const int FreeCapacity = 16;
    internal const int IdBlockSize = 1024;

    internal int Busy;
    internal int Revoked = 1;
    internal readonly long KernelId;
    internal readonly WeakReference<NativeOwnerKernel> Kernel;
    internal readonly WeakReference<Thread> OwnerThread;
    // The pool's slot in each thread's table; see NativePoolThreadCaches.
    internal readonly int Slot;
    // Caches are created on their owner thread. A revocation from that thread
    // needs no process-wide barrier. A thread serial, never given to another
    // thread: a thread whose stack reuses an exited owner's memory may use the
    // owner's cache (see NativeOwnerKernel.FindOwnCache), and a recycled managed
    // thread id would let a third thread skip the barrier against it.
    internal readonly long OwnerThreadSerial;

    // The 4 KiB stack block where the owner last found this cache through the
    // thread-local tables; see NativeOwnerKernel.FindOwnCache.
    internal nuint OwnerBlock;

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

    // A cached return whose payload is still in PayloadDelta: its bytes plus
    // one, zero when there is none. The owner's next rent of the same size
    // takes its place (AddPayload); anything that reads PayloadDelta or resets
    // MaxPayloadDelta settles it first (SettleDeferredReturn). Arena lanes
    // never return payload this way, so theirs stays zero.
    internal long DeferredReturn;

    // Operations and initializers running on cached records since the last fold.
    internal int ActiveOperations;
    internal int Initializing;

    // Transferable records rented through this cache: on loan, or returned and
    // waiting in the stash or the remote returns, still in place here. Only
    // the owner adds or removes entries, in its busy window; a fold reads the
    // list while the owner is idle, under the remote returns' gate.
    internal NativeAllocation?[] Transfers = new NativeAllocation?[4];
    internal int TransferCount;

    // Returned transferable records this thread holds for its next rents, its
    // own returns and those it took from Remote. Their payload has left
    // PayloadDelta. Owner only.
    internal NativeAllocation?[] Stash = new NativeAllocation?[4];
    internal int StashCount;

    // Transferable records other threads returned, still counted in
    // PayloadDelta. The stash, the remote returns and the transfer list grow
    // together, and both hold only records on the list, so neither overflows.
    internal readonly NativeRemoteReturns Remote = new(4);

    // An arena thread's private chunk of a shared bump segment while armed: it
    // allocates from LaneCursor up to LaneLimit. Chunks double per claim up to
    // the kernel's chunk limit.
    internal NativeBumpSegment? LaneSegment;
    internal nuint LaneCursor;
    internal nuint LaneLimit;
    // The lane segment's storage address, set with LaneSegment: a lane rent
    // reaches its storage in one load. A segment keeps its address until it is
    // freed, which revokes every cache first.
    internal IntPtr LaneBase;
    internal nuint LaneChunkBytes = 4096;

    internal NativePoolThreadCache(NativeOwnerKernel kernel, Thread owner)
    {
        KernelId = kernel.Id;
        Slot = kernel.CacheSlot;
        Kernel = new WeakReference<NativeOwnerKernel>(kernel);
        OwnerThread = new WeakReference<Thread>(owner);
        OwnerThreadSerial = NativeThreadState.Current.Serial;
    }

    // A rent's payload, in the owner's busy window. A deferred return of the
    // same size gives this lease its place, so the payload and its high-water
    // mark stand; otherwise the deferred return leaves first, so the mark is
    // taken from the exact payload.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddPayload(long bytes)
    {
        long deferred = DeferredReturn;
        if (deferred == bytes + 1)
        {
            DeferredReturn = 0;
            return;
        }

        if (deferred != 0)
        {
            DeferredReturn = 0;
            bytes -= deferred - 1;
        }

        long payload = PayloadDelta + bytes;
        PayloadDelta = payload;
        if (payload > MaxPayloadDelta)
        {
            MaxPayloadDelta = payload;
        }
    }

    // A cached return's payload, in the owner's busy window: left counted for
    // the next rent (AddPayload). One deferred earlier leaves now.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void DeferReturnedPayload(long bytes)
    {
        long previous = DeferredReturn;
        DeferredReturn = bytes + 1;
        if (previous != 0)
        {
            PayloadDelta -= previous - 1;
        }
    }

    // In the owner's busy window, or in a fold while the owner is idle.
    internal void SettleDeferredReturn()
    {
        long deferred = DeferredReturn;
        if (deferred != 0)
        {
            DeferredReturn = 0;
            PayloadDelta -= deferred - 1;
        }
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

    internal bool HasRecordRoom => RecordCount < Records.Length;

    // Takes the smallest free record whose slab holds the length, like the
    // generation's own smallest-available-slab choice. Slabs are as long as
    // the rent that made them, so a thread renting one size stops at the
    // newest record.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeAllocation? TakeFree(int length)
    {
        int count = FreeCount;
        // A thread renting one size finds it in the newest free record.
        int newest = count - 1;
        if (newest >= 0 && FreeCapacities[newest] == length)
        {
            FreeCount = newest;
            return Records[FreeSlots[newest]];
        }

        int best = -1;
        int bestCapacity = int.MaxValue;
        for (int index = count - 1; index >= 0; index--)
        {
            int capacity = FreeCapacities[index];
            if (capacity >= length && capacity < bestCapacity)
            {
                best = index;
                bestCapacity = capacity;
                if (capacity == length)
                {
                    break;
                }
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

    // In the owner's busy window, with room made by the owner.
    internal void AddTransfer(NativeAllocation allocation)
    {
        allocation.CacheSlot = TransferCount;
        Transfers[TransferCount] = allocation;
        Volatile.Write(ref TransferCount, TransferCount + 1);
    }

    // In the owner's busy window.
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

    // In the owner's busy window: removes and returns a stashed record that
    // holds the length, the most recent return when it fits within twice the
    // length (a pipeline rents one size), else the smallest that fits.
    internal NativeAllocation? TakeStashed(int length)
    {
        int newest = StashCount - 1;
        if (newest < 0)
        {
            return null;
        }

        int best = newest;
        int bestCapacity = Stash[newest]!.Capacity;
        if (bestCapacity < length || bestCapacity > 2 * length)
        {
            best = -1;
            bestCapacity = int.MaxValue;
            for (int index = 0; index <= newest; index++)
            {
                int capacity = Stash[index]!.Capacity;
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
        }

        NativeAllocation allocation = Stash[best]!;
        Stash[best] = Stash[newest];
        Stash[newest] = null;
        StashCount = newest;
        return allocation;
    }
}

// Transferable records other threads returned to one thread cache, kept for
// that thread's next rents. Returning threads take this gate instead of the
// cache's busy window, and the fields sit apart from neighbouring objects, so
// a return does not take the renting thread's cache line away.
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 192)]
internal sealed class NativeRemoteReturns
{
    [System.Runtime.InteropServices.FieldOffset(64)]
    internal NativeAllocation?[] Items;

    [System.Runtime.InteropServices.FieldOffset(72)]
    internal int Count;

    [System.Runtime.InteropServices.FieldOffset(76)]
    private int _gate;

    internal NativeRemoteReturns(int capacity) => Items = new NativeAllocation?[capacity];

    // The gate spins: its holders move a few slots and never allocate, block
    // or throw while they hold it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Enter()
    {
        if (Interlocked.CompareExchange(ref _gate, 1, 0) != 0)
        {
            Wait();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Exit() => Volatile.Write(ref _gate, 0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Wait()
    {
        SpinWait spinner = default;
        do
        {
            spinner.SpinOnce();
        }
        while (Volatile.Read(ref _gate) != 0
            || Interlocked.CompareExchange(ref _gate, 1, 0) != 0);
    }
}

[InlineArray(NativePoolThreadCache.FreeCapacity)]
internal struct FreeSlotArray
{
    private int _element;
}

// The caches of the current thread, indexed by their pool's cache slot, a
// small number each pool holds while it lives. A thread can use any number of
// pools without dropping caches: a fixed table of the most recently used ones
// thrashed (create one cache, evict another, under both pools' locks) once a
// thread cycled through one more pool than it held. Their own thread-statics:
// every cached rent looks one up, and a thread-state field sits one
// dependent load further away.
internal static class NativePoolThreadCaches
{
    [ThreadStatic]
    private static NativePoolThreadCache? t_recent;

    [ThreadStatic]
    private static NativePoolThreadCache?[]? t_bySlot;

    private static readonly Lock s_slotGate = new();
    private static int[] s_freeSlots = new int[16];
    private static int s_freeSlotCount;
    private static int s_nextSlot;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static NativePoolThreadCache? Find(long kernelId, int slot)
    {
        NativePoolThreadCache? recent = t_recent;
        return recent is not null && recent.KernelId == kernelId
            ? recent
            : FindSlow(kernelId, slot);
    }

    // A slot reused by a later pool may still hold an earlier pool's emptied
    // cache on this thread: the pool identity tells them apart.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativePoolThreadCache? FindSlow(long kernelId, int slot)
    {
        NativePoolThreadCache?[]? caches = t_bySlot;
        if (caches is not null
            && (uint)slot < (uint)caches.Length
            && caches[slot] is NativePoolThreadCache cache
            && cache.KernelId == kernelId)
        {
            t_recent = cache;
            return cache;
        }

        return null;
    }

    // Records a new cache for this thread in its pool's slot.
    internal static void Remember(NativePoolThreadCache cache)
    {
        int slot = cache.Slot;
        NativePoolThreadCache?[]? caches = t_bySlot;
        if (caches is null || slot >= caches.Length)
        {
            NativePoolThreadCache?[] grown = new NativePoolThreadCache?[Math.Max(8, (int)BitOperations.RoundUpToPowerOf2((uint)slot + 1))];
            caches?.AsSpan().CopyTo(grown);
            t_bySlot = caches = grown;
        }

        caches[slot] = cache;
        t_recent = cache;
    }

    internal static void Forget(NativePoolThreadCache cache)
    {
        if (ReferenceEquals(t_recent, cache))
        {
            t_recent = null;
        }

        NativePoolThreadCache?[]? caches = t_bySlot;
        int slot = cache.Slot;
        if (caches is not null && (uint)slot < (uint)caches.Length && ReferenceEquals(caches[slot], cache))
        {
            caches[slot] = null;
        }
    }

    // A pool takes a slot when created and gives it back when disposed or
    // finalized; slots are reused, so the tables stay as long as the most
    // pools alive at once.
    internal static int AllocateSlot()
    {
        lock (s_slotGate)
        {
            return s_freeSlotCount != 0 ? s_freeSlots[--s_freeSlotCount] : s_nextSlot++;
        }
    }

    internal static void ReleaseSlot(int slot)
    {
        lock (s_slotGate)
        {
            if (s_freeSlotCount == s_freeSlots.Length)
            {
                Array.Resize(ref s_freeSlots, s_freeSlots.Length * 2);
            }

            s_freeSlots[s_freeSlotCount++] = slot;
        }
    }
}
