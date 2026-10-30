using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

// Per-thread record caches for synchronized pools of unmanaged values, and
// per-thread bump lanes for synchronized arenas; see NativePoolThreadCache for
// the protocol. Scoped, zero-length, transferable and reference-holding leases,
// large arena ranges, and every lease while a test injects faults, keep the
// locked path.
internal sealed partial class NativeOwnerKernel
{
    private const int ThreadCacheLimit = 256;
    private NativePoolThreadCache[] _threadCaches = [];
    private int _threadCacheCount;
    private int _threadCachePruneCountdown;

    private bool ThreadCacheEligible => _threadCacheEligible;

    // The thread caches of a pool, or of an arena's lanes, indexed by the
    // 4 KiB stack block their owner last found them from. The thread-local
    // lookup is a TLS call and a chain of dependent loads, so the fast paths
    // look here first: an entry counts when its cache's owner last found it
    // from the caller's own block and the cache is not revoked. Anything else
    // takes the thread-local tables, which record the entry. No slow path arms
    // a cache found here: each finds the thread's own cache through the
    // thread-local tables first (ClaimArenaLane included).
    //
    // Assumption, as for NativeOwnerThread: no two live threads have stack
    // frames in the same 4 KiB block. The stacks an operating system allocates
    // for threads are separate whole pages, so they cannot share one. A stack a
    // program supplies itself (pthread_attr_setstack) may begin or end inside a
    // block that another thread's stack also uses; two threads renting from one
    // pool while their frames are in that block could then use one cache at
    // once and corrupt its records. NAM cannot detect such stacks.
    //
    // After a cache's owner exits, a new thread whose stack reuses that memory
    // may take the exited owner's cache while it is armed. No other thread can
    // use it meanwhile, so its busy window works as it does for the owner. A
    // revocation drops it, since its owner is not alive, and the new thread's
    // leases from it fold into the generation like any cached lease's. A
    // revocation compares the revoker with each cache's owner by thread serial
    // (OwnerThreadSerial), so it never skips the barrier for a cache that
    // another thread may be using.
    private const int CacheBlockTableSize = 64;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativePoolThreadCache? FindOwnCache() =>
        FindOwnCache(NativeStackBlockForTest.Current());

    // Only a kernel with a table calls this (see _cacheByBlock).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativePoolThreadCache? FindOwnCache(nuint block)
    {
        NativePoolThreadCache? cache = Unsafe.Add(
            ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(_cacheByBlock!),
            (nint)(block & (CacheBlockTableSize - 1)));
        return cache is not null && cache.OwnerBlock == block && Volatile.Read(ref cache.Revoked) == 0
            ? cache
            : FindOwnCacheSlow(block);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativePoolThreadCache? FindOwnCacheSlow(nuint block)
    {
        NativePoolThreadCache? cache = NativePoolThreadCaches.Find(Id, _cacheSlot);
        if (cache is not null)
        {
            cache.OwnerBlock = block;
            _cacheByBlock![(int)(block & (CacheBlockTableSize - 1))] = cache;
        }

        return cache;
    }

    // This thread's cache, found through the thread-local tables.
    internal NativePoolThreadCache? ThreadCacheForTest() =>
        NativePoolThreadCaches.Find(Id, _cacheSlot);

    // A filled rent that looks the calling thread's cache up for the given stack
    // block, as RentFilled does for the caller's own: tests simulate a thread
    // whose stack reuses an exited thread's memory.
    internal NativePoolLease RentFilledAtBlockForTest<T>(nuint block, int length, T value)
    {
        if (length > 0
            && ThreadCacheEligible
            && !NativeMemoryTestHooks.FaultInjectionPending
            && FindOwnCache(block) is NativePoolThreadCache cache
            && cache.Enter())
        {
            if (cache.NextId <= cache.LastId && cache.TakeFree(length) is NativeAllocation allocation)
            {
                long allocationId = PublishCachedLease(cache, allocation, length, value, fill: true);
                cache.Exit();
                return new NativePoolLease(allocationId, allocation);
            }

            cache.Exit();
        }

        return RentFilledSlow(length, value, fill: true, transferable: false);
    }

    // Returns the cache too: once the busy window closes, a revocation on
    // another thread may clear the record's CacheOwner.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeAllocation? TakeCachedForRent(int length, out NativePoolThreadCache? cache)
    {
        cache = NativePoolThreadCaches.Find(Id, _cacheSlot);
        if (cache is not null && cache.Enter())
        {
            if (cache.NextId <= cache.LastId
                && (cache.TakeFree(length) ?? (cache.HasRecordRoom ? TakeRemoteFree(cache, length) : null))
                    is NativeAllocation allocation)
            {
                BeginCachedLease(cache, allocation, length);
                cache.Exit();
                return allocation;
            }

            cache.Exit();
        }

        return ClaimForThreadCache(length, ref cache);
    }

    // Takes a record for the cache under the owner lock: arms the cache, refills
    // its identities and leased set, then uses a cached record, an available
    // slab or a new slab. Null means this thread has no cache for the pool.
    private NativeAllocation? ClaimForThreadCache(int length, ref NativePoolThreadCache? cache)
    {
        NativeAllocation? claimed = null;
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked("Rent");
            if (cache is null)
            {
                if (_threadCacheCount == ThreadCacheLimit)
                {
                    // Threads past the limit use the locked path; now and then
                    // one of them checks whether a cached thread has exited.
                    if (--_threadCachePruneCountdown > 0 || !HasExitedThreadCacheLocked())
                    {
                        if (_threadCachePruneCountdown <= 0)
                        {
                            _threadCachePruneCountdown = 1024;
                        }

                        return null;
                    }

                    // Revocation drops the caches of exited threads.
                    RevokeThreadCachesLocked();
                }

                cache = CreateThreadCacheLocked();
            }

            if (cache.Generation is not null && !ReferenceEquals(cache.Generation, generation))
            {
                FoldThreadCacheLocked(cache);
            }

            // A pool grows only when no thread holds an idle record: idle records
            // cached by other threads, for example threads an async producer has
            // left, come back first. A budget counts every slab, too.
            if (!CacheHoldsFit(cache, length)
                && !HasAvailableSlabLocked(generation, length)
                && OtherThreadCachesHoldFreeLocked(cache))
            {
                RevokeThreadCachesLocked();
            }

            if (cache.NextId > cache.LastId)
            {
                cache.NextId = checked(_nextAllocationId + 1);
                cache.LastId = checked(_nextAllocationId + NativePoolThreadCache.IdBlockSize);
                _nextAllocationId = cache.LastId;
            }

            cache.ReserveRecord();
            cache.Generation = generation;
            Volatile.Write(ref cache.Revoked, 0);
            claimed = cache.TakeFree(length) ?? TakeRemoteFree(cache, length);
            if (claimed is null)
            {
                NativeSlab? slab = TakeSmallestAvailableSlabLocked(generation, length);
                if (slab is null)
                {
                    generation.Slabs.EnsureCapacity(checked(generation.Slabs.Count + 1));
                    slab = AddPoolSlabLocked(generation, length, "pool growth", _lifecycle);
                    generation.AddSlabOrdered(slab);
                }

                if (generation.ReusableAllocations.Count != 0)
                {
                    int last = generation.ReusableAllocations.Count - 1;
                    claimed = generation.ReusableAllocations[last];
                    generation.ReusableAllocations.RemoveAt(last);
                }
                else
                {
                    claimed = new NativeAllocation(
                        generation, 0, slab, bumpSegment: null, offsetBytes: 0, length, slab.Capacity,
                        storageBytes: 0, referenceRoots: null, scoped: false, generation.ScopeEpoch);
                }

                generation.HotPoolAllocation = null;

                // A record joins the cache in the shape ResetCached keeps.
                claimed.Reset(
                    0, slab, bumpSegment: null, offsetBytes: 0, length, slab.Capacity,
                    storageBytes: 0, referenceRoots: null, scoped: false, generation.ScopeEpoch);
                claimed.CacheOwner = cache;
                cache.AddRecord(claimed);
            }

            // The owner may open its busy window under the lock: no revoker can run.
            _ = cache.Enter();
            BeginCachedLease(cache, claimed, length);
            cache.Exit();
        }

        return claimed;
    }

    private bool HasExitedThreadCacheLocked()
    {
        foreach (ref readonly NativePoolThreadCache cache in _threadCaches.AsSpan(0, _threadCacheCount))
        {
            if (!cache.IsOwnerAlive)
            {
                return true;
            }
        }

        return false;
    }

    private static bool CacheHoldsFit(NativePoolThreadCache cache, int length)
    {
        for (int index = 0; index < cache.FreeCount; index++)
        {
            if (cache.FreeCapacities[index] >= length)
            {
                return true;
            }
        }

        for (int index = 0; index < cache.StashCount; index++)
        {
            if (cache.Stash[index]!.Capacity >= length)
            {
                return true;
            }
        }

        NativeRemoteReturns remote = cache.Remote;
        if (Volatile.Read(ref remote.Count) == 0)
        {
            return false;
        }

        bool fits = false;
        remote.Enter();
        for (int index = 0; index < remote.Count && !fits; index++)
        {
            fits = remote.Items[index]!.Capacity >= length;
        }

        remote.Exit();
        return fits;
    }

    private static bool HasAvailableSlabLocked(NativeGeneration generation, int length) =>
        generation.AvailableSlabs.HasCapacity(length);

    private bool OtherThreadCachesHoldFreeLocked(NativePoolThreadCache cache)
    {
        foreach (ref readonly NativePoolThreadCache other in _threadCaches.AsSpan(0, _threadCacheCount))
        {
            if (!ReferenceEquals(other, cache)
                && (Volatile.Read(ref other.FreeCount) != 0
                    || Volatile.Read(ref other.StashCount) != 0
                    || Volatile.Read(ref other.Remote.Count) != 0))
            {
                return true;
            }
        }

        return false;
    }

    // In the owner's busy window or under the owner lock, with room for one
    // more record: takes a returned transferable record that holds the length
    // into the leased set.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeAllocation? TakeRemoteFree(NativePoolThreadCache cache, int length)
    {
        if (TakeReturnedTransfer(cache, length) is not NativeAllocation allocation)
        {
            return null;
        }

        cache.RemoveTransfer(allocation);
        allocation.CacheOwner = cache;
        cache.AddRecord(allocation);
        return allocation;
    }

    // In the owner's busy window or under the owner lock: a returned
    // transferable record from the stash, which takes in what other threads
    // returned when it holds none that fits. The record stays on the transfer list.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NativeAllocation? TakeReturnedTransfer(NativePoolThreadCache cache, int length) =>
        cache.TakeStashed(length)
        ?? (Volatile.Read(ref cache.Remote.Count) != 0 ? TakeRemoteReturns(cache, length) : null);

    // In a pipeline every return is remote: the whole batch moves to the stash
    // in one short gate, by swapping arrays when the stash is empty.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeAllocation? TakeRemoteReturns(NativePoolThreadCache cache, int length)
    {
        NativeRemoteReturns remote = cache.Remote;
        int first = cache.StashCount;
        remote.Enter();
        int count = remote.Count;
        if (first == 0)
        {
            NativeAllocation?[] returned = remote.Items;
            remote.Items = cache.Stash;
            cache.Stash = returned;
        }
        else
        {
            Array.Copy(remote.Items, 0, cache.Stash, first, count);
            Array.Clear(remote.Items, 0, count);
        }

        Volatile.Write(ref remote.Count, 0);
        remote.Exit();
        int last = first + count;
        cache.StashCount = last;
        long payload = cache.PayloadDelta;
        for (int index = first; index < last; index++)
        {
            payload -= (long)cache.Stash[index]!.StorageBytes;
        }

        cache.PayloadDelta = payload;
        return cache.TakeStashed(length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BeginCachedLease(NativePoolThreadCache cache, NativeAllocation allocation, int length)
    {
        NativeSlab slab = allocation.Slab!;
        allocation.ResetCached(
            cache.NextId++, length, (nuint)length * (nuint)_storageElementSize,
            NativeAllocationLifecycle.Initializing);
        cache.Initializing++;
        if (slab.HasBeenUsed)
        {
            NativeMemoryAccounting.RecordReusedNativeSegment();
        }
        else
        {
            slab.HasBeenUsed = true;
        }
    }

    private NativePoolLease InitializeCached<T>(
        NativePoolThreadCache cache,
        NativeAllocation allocation,
        NativeLeaseInitializer<T> initializer,
        bool transferable)
    {
        long allocationId = allocation.Id;
        int initializedLength = 0;
        try
        {
            NativeLeaseWriter<T> writer = RuntimeHelpers.IsReferenceOrContainsReferences<T>()
                ? new(allocation, ref initializedLength)
                : new(allocation, CachedSpan<T>(allocation, allocation.Length), ref initializedLength);
            initializer(writer);
            allocation.InitializedLength = initializedLength;
            if (transferable || !TryCompleteCachedInitialization(cache, allocation))
            {
                CompleteCachedInitialization(cache, allocation, transferable);
            }

            return new NativePoolLease(allocationId, allocation);
        }
        catch
        {
            allocation.InitializedLength = initializedLength;
            AbortCachedInitialization(cache, allocation);
            throw;
        }
    }

    // A returned transferable record initialized where it is, on the transfer
    // list, as a filled transferable rent reuses one: it never joins the leased
    // set, so it is not moved there and back. A fold meanwhile moves it to the
    // generation like any record on the list.
    private NativePoolLease InitializeReturnedTransfer<T>(
        NativePoolThreadCache cache,
        NativeAllocation allocation,
        NativeLeaseInitializer<T> initializer)
    {
        long allocationId = allocation.Id;
        int initializedLength = 0;
        try
        {
            NativeLeaseWriter<T> writer = RuntimeHelpers.IsReferenceOrContainsReferences<T>()
                ? new(allocation, ref initializedLength)
                : new(allocation, CachedSpan<T>(allocation, allocation.Length), ref initializedLength);
            initializer(writer);
            allocation.InitializedLength = initializedLength;
            CompleteReturnedTransferInitialization(cache, allocation);
            return new NativePoolLease(allocationId, allocation);
        }
        catch
        {
            allocation.InitializedLength = initializedLength;
            AbortReturnedTransferInitialization(cache, allocation);
            throw;
        }
    }

    // This thread's cache lists, read without folding the cache.
    internal (int Records, int Transfers, int Stashed) ThreadCacheListsForTest() =>
        NativePoolThreadCaches.Find(Id, _cacheSlot) is NativePoolThreadCache cache
            ? (cache.RecordCount, cache.TransferCount, cache.StashCount)
            : default;

    private void CompleteReturnedTransferInitialization(NativePoolThreadCache cache, NativeAllocation allocation)
    {
        if (cache.Enter())
        {
            if (ReferenceEquals(allocation.TransferOwner, cache))
            {
                if (allocation.InitializedLength != allocation.Length)
                {
                    cache.Exit();
                    throw new InvalidOperationException(
                        "The native lease initializer did not write all logical elements.");
                }

                allocation.InitializedLength = 0;
                cache.AddPayload((long)allocation.StorageBytes);

                allocation.Lifecycle = NativeAllocationLifecycle.Active;
                cache.Initializing--;
                cache.Exit();
                return;
            }

            cache.Exit();
        }

        // A fold moved the record and its initializer count to the generation.
        WaitForThreadCacheFold();
        CompleteInitialization(allocation.GenerationState, allocation, scoped: false);
    }

    private void AbortReturnedTransferInitialization(NativePoolThreadCache cache, NativeAllocation allocation)
    {
        if (cache.Enter())
        {
            // Back on the stash, returned, as before this rent. The stash grows
            // with the transfer list, which still holds the record, so it has
            // room.
            if (ReferenceEquals(allocation.TransferOwner, cache)
                && allocation.Lifecycle == NativeAllocationLifecycle.Initializing)
            {
                allocation.TransferOwner = null;
                allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                allocation.InitializedLength = 0;
                cache.Initializing--;
                cache.Stash[cache.StashCount++] = allocation;
                cache.Exit();
                return;
            }

            cache.Exit();
        }

        lock (_gate)
        {
            if (ReferenceEquals(allocation.TransferOwner, cache))
            {
                RevokeThreadCacheLocked(cache);
            }
        }

        AbortPoolInitialization(allocation.GenerationState, allocation);
    }

    // Rents a lease filled with one value, or left unwritten, without an
    // initializer callback: a cached record is filled in place and published;
    // anything else takes the initializer path with an equivalent fill.
    // Elements that are or contain references are always written.
    // The common case, a small cached lease that stays on this thread, is kept
    // short so callers' loops can still inline their lease operations.
    internal NativePoolLease RentFilled<T>(int length, T value, bool fill, bool transferable = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (transferable)
        {
            return RentTransferableFilled(length, value, fill);
        }

        if (length > 0
            && ThreadCacheEligible
            && !NativeMemoryTestHooks.FaultInjectionPending
            && FindOwnCache() is NativePoolThreadCache cache
            && cache.Enter())
        {
            if (cache.NextId <= cache.LastId
                && (!fill || (nuint)length * StorageSize<T>() <= InWindowFillBytes)
                && cache.TakeFree(length) is NativeAllocation allocation)
            {
                long allocationId = PublishCachedLease(cache, allocation, length, value, fill);
                cache.Exit();
                return new NativePoolLease(allocationId, allocation);
            }

            cache.Exit();
        }

        return RentFilledSlow(length, value, fill, transferable: false);
    }

    // A transferable rent on a thread whose returned transferable records
    // wait in its stash or remote returns reuses one in place, in one window.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativePoolLease RentTransferableFilled<T>(int length, T value, bool fill)
    {
        if (length > 0
            && ThreadCacheEligible
            && !NativeMemoryTestHooks.FaultInjectionPending
            && FindOwnCache() is NativePoolThreadCache cache
            && cache.Enter())
        {
            if (cache.NextId <= cache.LastId
                && (!fill || (nuint)length * StorageSize<T>() <= InWindowFillBytes)
                && TakeReturnedTransfer(cache, length) is NativeAllocation returned)
            {
                long returnedId = PublishCachedLease(cache, returned, length, value, fill);
                returned.TransferOwner = cache;
                cache.Exit();
                return new NativePoolLease(returnedId, returned);
            }

            cache.Exit();
        }

        return RentFilledSlow(length, value, fill, transferable: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativePoolLease RentFilledSlow<T>(int length, T value, bool fill, bool transferable)
    {
        if (length > 0
            && ThreadCacheEligible
            && !NativeMemoryTestHooks.FaultInjectionPending
            && NativePoolThreadCaches.Find(Id, _cacheSlot) is NativePoolThreadCache cache)
        {
            if (transferable)
            {
                ReserveTransfer(cache);
            }

            if (cache.Enter())
            {
                if (cache.NextId <= cache.LastId)
                {
                    bool inWindow = !fill || (nuint)length * StorageSize<T>() <= InWindowFillBytes;

                    // A returned transferable record serves the next
                    // transferable rent in place on the transfer list.
                    if (transferable && inWindow && TakeReturnedTransfer(cache, length) is NativeAllocation returned)
                    {
                        long returnedId = PublishCachedLease(cache, returned, length, value, fill);
                        returned.TransferOwner = cache;
                        cache.Exit();
                        return new NativePoolLease(returnedId, returned);
                    }

                    if ((cache.TakeFree(length) ?? (cache.HasRecordRoom ? TakeRemoteFree(cache, length) : null))
                        is NativeAllocation allocation)
                    {
                        if (inWindow)
                        {
                            long allocationId = PublishCachedLease(cache, allocation, length, value, fill);
                            if (transferable)
                            {
                                MoveToTransfers(cache, allocation);
                            }

                            cache.Exit();
                            return new NativePoolLease(allocationId, allocation);
                        }

                        BeginCachedLease(cache, allocation, length);
                        cache.Exit();
                        return FillCached(cache, allocation, value, fill, transferable);
                    }
                }

                cache.Exit();
            }
        }

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            return RentInitialized(length, scoped: false, FillInitializer(value), transferable);
        }

        // Elements without references: a cached initializer writes defaults,
        // and the value goes into the published lease before anyone else can
        // reach it, so this path allocates no closure.
        NativePoolLease lease = RentInitialized(length, scoped: false, DefaultInitializer<T>.Instance, transferable);
        if (fill && length != 0)
        {
            lease.AllocationState.AsSpan<T>()[..length].Fill(value);
        }

        return lease;
    }

    private static class DefaultInitializer<T>
    {
        internal static readonly NativeLeaseInitializer<T> Instance = static writer => writer.Fill(default!);
    }

    // A fill this small finishes inside the rent's busy window, so a revoking
    // thread waits at most that long.
    private const nuint InWindowFillBytes = 16 * 1024;

    // The storage size of the pool's element type, which every generic rent
    // here is called with: the kernel was made with the same size, and the
    // JIT folds this one into a constant where the field would be a load.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private nuint StorageSize<T>()
    {
        System.Diagnostics.Debug.Assert(_storageElementSize == NativeTypeLayout.StorageSize<T>());
        return (nuint)NativeTypeLayout.StorageSize<T>();
    }

    // Begins, fills and publishes a cached lease in one busy window: no
    // initializer runs, so the lease never shows as initializing.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long PublishCachedLease<T>(NativePoolThreadCache cache, NativeAllocation allocation, int length, T value, bool fill)
    {
        NativeSlab slab = allocation.Slab!;
        long allocationId = cache.NextId++;
        nuint storageBytes = (nuint)length * StorageSize<T>();
        allocation.ResetCached(allocationId, length, storageBytes, NativeAllocationLifecycle.Active);
        if (slab.HasBeenUsed)
        {
            NativeMemoryAccounting.RecordReusedNativeSegment();
        }
        else
        {
            slab.HasBeenUsed = true;
        }

        if (fill)
        {
            CachedSpan<T>(allocation, length).Fill(value);
        }

        cache.AddPayload((long)storageBytes);
        return allocationId;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static NativeLeaseInitializer<T> FillInitializer<T>(T value) =>
        writer => writer.Fill(value);

    // A fill runs no user code and cannot fail, so there is nothing to abort.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativePoolLease FillCached<T>(NativePoolThreadCache cache, NativeAllocation allocation, T value, bool fill, bool transferable)
    {
        long allocationId = allocation.Id;
        int length = allocation.Length;
        if (fill)
        {
            CachedSpan<T>(allocation, length).Fill(value);
        }

        allocation.InitializedLength = length;
        if (transferable || !TryCompleteCachedInitialization(cache, allocation))
        {
            CompleteCachedInitialization(cache, allocation, transferable);
        }

        return new NativePoolLease(allocationId, allocation);
    }

    // Publishes a fully initialized lease that is still in this thread's cache.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryCompleteCachedInitialization(NativePoolThreadCache cache, NativeAllocation allocation)
    {
        if (allocation.InitializedLength == allocation.Length && cache.Enter())
        {
            if (ReferenceEquals(allocation.CacheOwner, cache))
            {
                allocation.InitializedLength = 0;
                cache.AddPayload((long)allocation.StorageBytes);

                allocation.Lifecycle = NativeAllocationLifecycle.Active;
                cache.Initializing--;
                cache.Exit();
                return true;
            }

            cache.Exit();
        }

        return false;
    }

    // The storage of a cached lease, which holds values without references.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe Span<T> CachedSpan<T>(NativeAllocation allocation, int length) =>
        new((void*)allocation.CachedAddress, length);

    // A transferable lease leaves the owner-only leased set for the transfer
    // list, because its NativeTransfer<T> may be used and returned anywhere.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CompleteCachedInitialization(NativePoolThreadCache cache, NativeAllocation allocation, bool transferable)
    {
        if (transferable)
        {
            ReserveTransfer(cache);
        }

        if (cache.Enter())
        {
            if (ReferenceEquals(allocation.CacheOwner, cache))
            {
                if (allocation.InitializedLength != allocation.Length)
                {
                    cache.Exit();
                    throw new InvalidOperationException(
                        "The native lease initializer did not write all logical elements.");
                }

                if (transferable)
                {
                    MoveToTransfers(cache, allocation);
                }

                allocation.InitializedLength = 0;
                cache.AddPayload((long)allocation.StorageBytes);

                allocation.Lifecycle = NativeAllocationLifecycle.Active;
                cache.Initializing--;
                cache.Exit();
                return;
            }

            cache.Exit();
        }

        // A fold moved the record and its initializer count to the generation.
        WaitForThreadCacheFold();
        CompleteInitialization(allocation.GenerationState, allocation, scoped: false);
    }

    // Only this thread changes its transfer list and a fold only empties it,
    // so a list with room now keeps it. Growing allocates outside any window.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReserveTransfer(NativePoolThreadCache cache)
    {
        if (Volatile.Read(ref cache.TransferCount) == cache.Transfers.Length)
        {
            GrowTransfers(cache);
        }
    }

    // The stash and the remote returns grow with the list, so they always
    // have room for every record on it. A fold may read all three, so they
    // change under the remote returns' gate.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void GrowTransfers(NativePoolThreadCache cache)
    {
        int length = cache.Transfers.Length * 2;
        NativeAllocation?[] transfers = new NativeAllocation?[length];
        NativeAllocation?[] stash = new NativeAllocation?[length];
        NativeAllocation?[] returned = new NativeAllocation?[length];
        NativeRemoteReturns remote = cache.Remote;
        remote.Enter();
        Array.Copy(cache.Transfers, transfers, cache.TransferCount);
        Array.Copy(cache.Stash, stash, cache.StashCount);
        Array.Copy(remote.Items, returned, remote.Count);
        cache.Transfers = transfers;
        cache.Stash = stash;
        remote.Items = returned;
        remote.Exit();
    }

    // In the owner's busy window, after ReserveTransfer: a transferable lease
    // leaves the owner-only leased set for the transfer list.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MoveToTransfers(NativePoolThreadCache cache, NativeAllocation allocation)
    {
        cache.RemoveRecord(allocation);
        allocation.CacheOwner = null;
        cache.AddTransfer(allocation);
        allocation.TransferOwner = cache;
    }

    private void AbortCachedInitialization(NativePoolThreadCache cache, NativeAllocation allocation)
    {
        if (cache.Enter())
        {
            if (ReferenceEquals(allocation.CacheOwner, cache)
                && allocation.Lifecycle == NativeAllocationLifecycle.Initializing
                && cache.FreeCount < NativePoolThreadCache.FreeCapacity)
            {
                allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                allocation.InitializedLength = 0;
                cache.Initializing--;
                cache.AddFree(allocation);
                cache.Exit();
                return;
            }

            cache.Exit();
        }

        lock (_gate)
        {
            if (ReferenceEquals(allocation.CacheOwner, cache))
            {
                RevokeThreadCacheLocked(cache);
            }
        }

        AbortPoolInitialization(allocation.GenerationState, allocation);
    }

    // Puts this thread's valid, idle cached lease back into its cache. The
    // caller read the cache from the record's CacheOwner, which only a fold
    // changes; a fold revokes the cache first, or runs on this thread outside
    // any window, so a successful Enter means the record is still this cache's.
    // Allocation ids are never reused within a pool, so the id and the active
    // lifecycle identify this rental: its generation, fixed for a record and
    // the cache's own while the record is cached, needs no check of its own.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryReturnCachedLease(
        NativePoolThreadCache cache,
        NativeAllocation allocation,
        long allocationId)
    {
        if (cache.FreeCount < NativePoolThreadCache.FreeCapacity && cache.Enter())
        {
            if (allocation.Id == allocationId
                && allocation.Lifecycle == NativeAllocationLifecycle.Active
                && allocation.HasNoOwnerOperations)
            {
                cache.DeferReturnedPayload((long)allocation.StorageBytes);
                allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                cache.AddFree(allocation);
                cache.Exit();
                return true;
            }

            cache.Exit();
        }

        return false;
    }

    // A one-shot rental (NativeConcurrentPool<T>.Process): takes a cached
    // record, publishes it filled and enters an operation on it in one busy
    // window, where a rent, an operation and a return take one each.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryRentCachedOperation<T>(
        int length,
        T value,
        [NotNullWhen(true)] out NativePoolThreadCache? cache,
        [NotNullWhen(true)] out NativeAllocation? allocation,
        out long allocationId,
        out int epoch)
    {
        if (length > 0
            && ThreadCacheEligible
            && !NativeMemoryTestHooks.FaultInjectionPending
            && !NativeMemoryTestHooks.OperationHooksEnabled
            && FindOwnCache() is NativePoolThreadCache found
            && found.Enter())
        {
            if (found.NextId <= found.LastId
                && (nuint)length * StorageSize<T>() <= InWindowFillBytes
                && found.TakeFree(length) is NativeAllocation taken)
            {
                allocationId = PublishCachedLease(found, taken, length, value, fill: true);
                taken.EnterOwnerOperation();
                found.ActiveOperations++;
                epoch = found.FoldEpoch;
                found.Exit();
                cache = found;
                allocation = taken;
                return true;
            }

            found.Exit();
        }

        cache = null;
        allocation = null;
        allocationId = 0;
        epoch = 0;
        return false;
    }

    // Ends a one-shot rental's operation and returns its record in one busy
    // window; anything else (a fold during the callback, a full free list)
    // takes the locked paths an operation exit and a return would.
    internal void ExitCachedOperationAndReturn(
        NativePoolThreadCache cache,
        NativeAllocation allocation,
        long allocationId,
        int epoch)
    {
        NativeGeneration generation = allocation.GenerationState;
        long generationNumber = generation.Number;
        if (cache.Enter())
        {
            if (cache.FoldEpoch == epoch)
            {
                allocation.ExitOwnerOperation();
                cache.ActiveOperations--;
                if (cache.FreeCount < NativePoolThreadCache.FreeCapacity
                    && ReferenceEquals(allocation.CacheOwner, cache)
                    && allocation.Id == allocationId
                    && allocation.Lifecycle == NativeAllocationLifecycle.Active
                    && allocation.HasNoOwnerOperations)
                {
                    cache.DeferReturnedPayload((long)allocation.StorageBytes);
                    allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                    cache.AddFree(allocation);
                    cache.Exit();
                    return;
                }

                cache.Exit();
                ReturnLease(generation, allocation, generationNumber, allocationId, "NativeConcurrentPool.Process");
                return;
            }

            cache.Exit();
        }

        ExitFoldedOperation(generation, allocation, cache, epoch);
        ReturnLease(generation, allocation, generationNumber, allocationId, "NativeConcurrentPool.Process");
    }

    // Returns a lease. A cached record goes back into its cache, and a stale
    // handle is ignored, as the locked path does; everything else takes the
    // locked path, which first folds a cache that still holds the record. A
    // ConcurrentPooled<T> is a ref struct, so it is disposed on the thread that
    // rented it, which owns the cache.
    internal void ReturnLease(
        NativeGeneration generationState,
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        string operation = "Pooled.Dispose")
    {
        if (allocation.CacheOwner is NativePoolThreadCache cache)
        {
            if (cache.FreeCount == NativePoolThreadCache.FreeCapacity)
            {
                SpillThreadCacheFree(cache);
            }

            if (TryReturnCachedLease(cache, allocation, allocationId))
            {
                return;
            }

            if (cache.Enter())
            {
                if (ReferenceEquals(allocation.CacheOwner, cache))
                {
                    if (allocation.Id != allocationId
                        || allocation.Lifecycle != NativeAllocationLifecycle.Active
                        || !ReferenceEquals(cache.Generation, generationState)
                        || generationState.Number != generationNumber)
                    {
                        cache.Exit();
                        return;
                    }

                    int activeOperations = allocation.ActiveOperations;
                    if (activeOperations != 0)
                    {
                        cache.Exit();
                        throw CreateInUseException(
                            operation,
                            generationNumber,
                            allocationId,
                            activeOperations,
                            "The lease has an active native operation. No storage was cleared or requeued.");
                    }
                }

                cache.Exit();
            }
        }

        ReturnLease(generationNumber, allocationId, operation);
    }

    // An operation on this thread's cached lease: the busy window replaces the
    // generation and record admission atomics. Operation hooks need the locked
    // path, which reports entries to them. The caller read the cache from the
    // record's CacheOwner; as for a return (TryReturnCachedLease), a successful
    // Enter keeps it this cache's, and the id identifies the rental.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryEnterCachedLeaseOperation(
        NativePoolThreadCache cache,
        NativeAllocation allocation,
        long allocationId,
        out int epoch)
    {
        if (!NativeMemoryTestHooks.OperationHooksEnabled && cache.Enter())
        {
            if (allocation.Id == allocationId
                && allocation.Lifecycle == NativeAllocationLifecycle.Active)
            {
                allocation.EnterOwnerOperation();
                cache.ActiveOperations++;
                epoch = cache.FoldEpoch;
                cache.Exit();
                return true;
            }

            cache.Exit();
        }

        epoch = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitCachedLeaseOperation(
        NativeAllocation allocation,
        NativePoolThreadCache cache,
        int epoch)
    {
        if (cache.Enter())
        {
            if (cache.FoldEpoch == epoch)
            {
                allocation.ExitOwnerOperation();
                cache.ActiveOperations--;
                cache.Exit();
                return;
            }

            cache.Exit();
        }

        ExitFoldedOperation(allocation.GenerationState, allocation, cache, epoch);
    }

    // A fold moved this operation's count into the generation.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ExitFoldedOperation(NativeGeneration generation, NativeAllocation allocation, NativePoolThreadCache cache, int epoch)
    {
        WaitForCacheFold(cache, epoch);
        allocation.ExitOperation();
        ExitGenerationOperation(generation);
    }

    // The busy window replaces the generation and record admission atomics for
    // an operation on a cached record. Cached records back only ConcurrentPooled<T>
    // leases, ref structs that stay on the renting thread, which owns the cache.
    // Operation hooks need the locked path, which reports entries to them.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryEnterCachedOperation(
        NativeGeneration generation,
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        string operation,
        out NativeOperationToken token)
    {
        if (allocation.CacheOwner is NativePoolThreadCache cache)
        {
            if (TryEnterCachedLeaseOperation(cache, allocation, allocationId, out int epoch))
            {
                token = new NativeOperationToken(this, generation, allocation, cache, epoch, recordEntered: true, operation);
                return true;
            }
        }
        else if (allocation.TransferOwner is not null
            && !NativeMemoryTestHooks.OperationHooksEnabled)
        {
            return TryEnterTransferOperation(generation, allocation, generationNumber, allocationId, operation, out token);
        }

        token = default;
        return false;
    }

    // A transferable lease is used only through its transfer control, whose own
    // admission already arbitrates borrows on several threads and keeps
    // disposal and moves out while any runs; the record's admission would only
    // repeat it. The generation's count goes to the operating thread's cache
    // instead of the shared generation counter; a thread without one gets one
    // here, so its later operations take the inline path.
    private bool TryEnterTransferOperation(
        NativeGeneration generation,
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        string operation,
        out NativeOperationToken token)
    {
        if (EnterOperationCache() is NativePoolThreadCache cache)
        {
            if (ReferenceEquals(cache.Generation, generation)
                && generation.Number == generationNumber
                && allocation.Id == allocationId
                && allocation.Lifecycle == NativeAllocationLifecycle.Active)
            {
                cache.ActiveOperations++;
                int epoch = cache.FoldEpoch;
                cache.Exit();
                token = new NativeOperationToken(this, generation, allocation, cache, epoch, recordEntered: false, operation);
                return true;
            }

            cache.Exit();
        }

        token = default;
        return false;
    }

    // The operation fast path of a transfer control over a transferable lease
    // rented through a thread cache; see TryEnterTransferOperation.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryEnterCachedTransferOperation(
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        [NotNullWhen(true)] out NativePoolThreadCache? cache,
        out int epoch)
    {
        if (allocation.TransferOwner is not null
            && !NativeMemoryTestHooks.OperationHooksEnabled
            && FindOwnCache() is NativePoolThreadCache found
            && found.Enter())
        {
            NativeGeneration generation = allocation.GenerationState;
            if (ReferenceEquals(found.Generation, generation)
                && generation.Number == generationNumber
                && allocation.Id == allocationId
                && allocation.Lifecycle == NativeAllocationLifecycle.Active)
            {
                found.ActiveOperations++;
                epoch = found.FoldEpoch;
                found.Exit();
                cache = found;
                return true;
            }

            found.Exit();
        }

        cache = null;
        epoch = 0;
        return false;
    }

    // A transfer's shape, if its lease is current in this thread's cache: the
    // checks TryEnterCachedTransferOperation grants an operation on, in one busy
    // window, without entering an operation, since the shape is no storage.
    // False leaves the decision to ValidateHandle.
    internal bool TryReadCachedTransferShape(
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        out NativeHandleMetadata shape)
    {
        if (allocation.TransferOwner is not null
            && !NativeMemoryTestHooks.OperationHooksEnabled
            && FindOwnCache() is NativePoolThreadCache found
            && found.Enter())
        {
            NativeGeneration generation = allocation.GenerationState;
            bool current = ReferenceEquals(found.Generation, generation)
                && generation.Number == generationNumber
                && allocation.Id == allocationId
                && allocation.Lifecycle == NativeAllocationLifecycle.Active;
            shape = current ? new NativeHandleMetadata(allocation.Length, allocation.Capacity) : default;
            found.Exit();
            return current;
        }

        shape = default;
        return false;
    }

    // This thread's cache in its busy window, armed for operations if need be.
    private NativePoolThreadCache? EnterOperationCache()
    {
        NativePoolThreadCache? cache = NativePoolThreadCaches.Find(Id, _cacheSlot);
        if (cache is not null && cache.Enter())
        {
            return cache;
        }

        cache = ArmOperationCache();
        return cache is not null && cache.Enter() ? cache : null;
    }

    // A thread that operates on this pool's transferable leases without renting
    // from it, such as the consuming end of a pipeline, gets an empty cache, so
    // its operations count there instead of in the generation's shared
    // counters. A revoked cache re-arms the same way. Null when the pool is
    // not active, holds its limit of caches, or the thread would have to drop
    // another pool's cache.
    private NativePoolThreadCache? ArmOperationCache()
    {
        NativePoolThreadCache? cache = NativePoolThreadCaches.Find(Id, _cacheSlot);
        if (NativeMemoryTestHooks.FaultInjectionPending
            || cache is null
                && Volatile.Read(ref _threadCacheCount) == ThreadCacheLimit)
        {
            return null;
        }

        lock (_gate)
        {
            if (_lifecycle != NativeOwnerLifecycle.Active || _current is not NativeGeneration generation)
            {
                return null;
            }

            if (cache is null)
            {
                if (_threadCacheCount == ThreadCacheLimit)
                {
                    return null;
                }

                cache = CreateThreadCacheLocked();
            }

            if (cache.Generation is not null && !ReferenceEquals(cache.Generation, generation))
            {
                FoldThreadCacheLocked(cache);
            }

            cache.Generation = generation;
            Volatile.Write(ref cache.Revoked, 0);
            return cache;
        }
    }

    // An operation on an arena scratch range from a thread whose cache holds a
    // lane of the range's generation: the count goes to that cache instead of
    // the generation's shared counter. Scratch leases are ref structs, so the
    // operating thread is the one that scratched the range.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryEnterArenaLaneOperation(
        NativeArenaAllocation allocation,
        [NotNullWhen(true)] out NativePoolThreadCache? cache,
        out int epoch) =>
        TryEnterArenaLaneOperation(null, allocation, out cache, out epoch);

    // A lease that kept its scratching thread's cache passes it and skips the
    // lookup; a cache the thread has since dropped is revoked and refuses.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryEnterArenaLaneOperation(
        NativePoolThreadCache? known,
        NativeArenaAllocation allocation,
        [NotNullWhen(true)] out NativePoolThreadCache? cache,
        out int epoch)
    {
        if (!NativeMemoryTestHooks.OperationHooksEnabled
            && (known ?? FindOwnCache()) is NativePoolThreadCache found
            && found.Enter())
        {
            // Every change of the current generation or the lifecycle revokes
            // the caches first, so a cache armed for the lease's generation
            // proves that generation current and the arena active.
            if (ReferenceEquals(found.Generation, allocation.GenerationState)
                && (!allocation.Scoped || allocation.ScopeEpoch == allocation.GenerationState.ScopeEpoch))
            {
                found.ActiveOperations++;
                epoch = found.FoldEpoch;
                found.Exit();
                cache = found;
                return true;
            }

            found.Exit();
        }

        cache = null;
        epoch = 0;
        return false;
    }

    // Ends an operation counted in a thread's cache without a record admission.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitCacheCountedOperation(NativeGeneration generation, NativePoolThreadCache cache, int epoch)
    {
        if (cache.Enter())
        {
            if (cache.FoldEpoch == epoch)
            {
                cache.ActiveOperations--;
                cache.Exit();
                return;
            }

            cache.Exit();
        }

        ExitFoldedCacheCountedOperation(generation, cache, epoch);
    }

    // A fold moved this operation's count into the generation.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ExitFoldedCacheCountedOperation(NativeGeneration generation, NativePoolThreadCache cache, int epoch)
    {
        WaitForCacheFold(cache, epoch);
        ExitGenerationOperation(generation);
    }

    internal void ExitCachedOperation(
        NativeGeneration generation,
        NativeAllocation allocation,
        NativePoolThreadCache cache,
        int epoch,
        bool recordEntered)
    {
        if (recordEntered)
        {
            ExitCachedLeaseOperation(allocation, cache, epoch);
            return;
        }

        ExitCacheCountedOperation(generation, cache, epoch);
    }

    // Returns a transferable lease from any thread. A lease still on its renting
    // cache's transfer list stays there for that thread's next rents: the
    // renting thread stashes it in its busy window, any other thread adds it to
    // the cache's remote returns under their gate. A lease a fold moved to the
    // generation takes the locked path. Its transfer control already turned
    // away new borrows and found none running, so the record's admission needs
    // no closing here.
    internal void ReturnTransferLease(
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        string operation)
    {
        if (allocation.TransferOwner is NativePoolThreadCache owner)
        {
            if (ReferenceEquals(FindOwnCache(), owner) && owner.Enter())
            {
                // No fold runs in the busy window, and only the lease's holder
                // returns it.
                if (ReferenceEquals(allocation.TransferOwner, owner))
                {
                    if (IsCurrentTransfer(allocation, generationNumber, allocationId))
                    {
                        allocation.TransferOwner = null;
                        allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                        owner.PayloadDelta -= (long)allocation.StorageBytes;
                        owner.Stash[owner.StashCount++] = allocation;
                    }

                    owner.Exit();
                    return;
                }

                owner.Exit();
            }
            else
            {
                NativeRemoteReturns remote = owner.Remote;
                remote.Enter();
                if (ReferenceEquals(allocation.TransferOwner, owner))
                {
                    if (IsCurrentTransfer(allocation, generationNumber, allocationId))
                    {
                        // Its payload leaves the renting thread's count when
                        // that thread takes the record back.
                        allocation.TransferOwner = null;
                        allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                        remote.Items[remote.Count] = allocation;
                        Volatile.Write(ref remote.Count, remote.Count + 1);
                    }

                    remote.Exit();
                    return;
                }

                remote.Exit();
            }
        }

        ReturnLease(generationNumber, allocationId, operation);
    }

    private static bool IsCurrentTransfer(NativeAllocation allocation, long generationNumber, long allocationId) =>
        allocation.Id == allocationId
        && allocation.Lifecycle == NativeAllocationLifecycle.Active
        && allocation.GenerationState.Number == generationNumber;

    // The generation's payload is about to change on the locked path. This
    // thread's cached high-water mark was reached against the payload before
    // that change, so it is recorded first; single-threaded peaks stay exact.
    private void PublishThreadCachePeak(NativeGeneration generation)
    {
        if (_threadCacheCount == 0
            || NativePoolThreadCaches.Find(Id, _cacheSlot) is not NativePoolThreadCache cache
            || !cache.Enter())
        {
            return;
        }

        cache.SettleDeferredReturn();
        if (ReferenceEquals(cache.Generation, generation) && cache.MaxPayloadDelta != 0)
        {
            RecordPayloadPeak(Volatile.Read(ref generation.InitializedPayloadBytes) + cache.MaxPayloadDelta);
            cache.MaxPayloadDelta = cache.PayloadDelta;
        }

        cache.Exit();
    }

    private void RecordPayloadPeak(long candidate)
    {
        long peak = Volatile.Read(ref _peakInitializedPayloadBytes);
        while (candidate > peak)
        {
            long observed = Interlocked.CompareExchange(ref _peakInitializedPayloadBytes, candidate, peak);
            if (observed == peak)
            {
                break;
            }

            peak = observed;
        }
    }

    // Revoked is set under the owner lock before the fold that follows it, so a
    // thread that found its cache revoked takes the lock once to see the fold
    // finished before it uses the generation's counts.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WaitForThreadCacheFold()
    {
        lock (_gate)
        {
            _ = _threadCacheCount;
        }
    }

    // An operation entered at a fold epoch waits for that fold to finish
    // without the owner lock, which a busy revoking thread may hold for long:
    // an operation kept open that long makes releases retain its whole
    // generation. A revocation folds every cache it revokes, right after the
    // owner leaves its busy window, and publishes the next epoch last.
    private static void WaitForCacheFold(NativePoolThreadCache cache, int epoch)
    {
        SpinWait spinner = default;
        while (Volatile.Read(ref cache.FoldEpoch) == epoch)
        {
            spinner.SpinOnce();
        }
    }

    // A full free list hands its oldest record to the generation under the lock.
    private void SpillThreadCacheFree(NativePoolThreadCache cache)
    {
        lock (_gate)
        {
            NativeGeneration? generation = cache.Generation;
            if (generation is null)
            {
                return;
            }

            generation.ReusableAllocations.EnsureCapacity(checked(generation.ReusableAllocations.Count + 1));
            generation.AvailableSlabs.EnsureCapacity(checked(generation.AvailableSlabs.Count + 1));
            if (!cache.Enter())
            {
                return;
            }

            NativeAllocation? allocation = null;
            if (cache.FreeCount != 0)
            {
                allocation = cache.TakeOldestFree();
                allocation.CacheOwner = null;
            }

            cache.Exit();
            if (allocation is not null)
            {
                generation.ReusableAllocations.Add(allocation);
                generation.AddAvailableSlabOrdered(allocation.Slab!);
                generation.HotPoolAllocation = null;
            }
        }
    }

    private NativePoolThreadCache CreateThreadCacheLocked()
    {
        NativePoolThreadCache cache = new(this, Thread.CurrentThread);
        if (_threadCacheCount == _threadCaches.Length)
        {
            NativePoolThreadCache[] grown = new NativePoolThreadCache[Math.Max(4, _threadCacheCount * 2)];
            _threadCaches.AsSpan(0, _threadCacheCount).CopyTo(grown);
            _threadCaches = grown;
        }

        _threadCaches[_threadCacheCount++] = cache;
        NativePoolThreadCaches.Remember(cache);
        return cache;
    }

    private void RemoveThreadCacheLocked(NativePoolThreadCache cache)
    {
        int index = Array.IndexOf(_threadCaches, cache, 0, _threadCacheCount);
        if (index < 0)
        {
            return;
        }

        _threadCaches[index] = _threadCaches[--_threadCacheCount];
        _threadCaches[_threadCacheCount] = null!;
    }

    // Brings every cache back into the generation; see NativePoolThreadCache.
    private void RevokeThreadCachesLocked()
    {
        int count = _threadCacheCount;
        if (count == 0)
        {
            return;
        }

        // The barrier makes another thread's busy window visible; the calling
        // thread is never inside its own cache's window here, so revoking only
        // its own cache (the single-threaded case) needs none.
        bool armed = false;
        long current = NativeThreadState.CurrentSerialOrZero;
        foreach (ref readonly NativePoolThreadCache cache in _threadCaches.AsSpan(0, count))
        {
            if (Volatile.Read(ref cache.Revoked) == 0)
            {
                Volatile.Write(ref cache.Revoked, 1);
                armed |= cache.OwnerThreadSerial != current;
            }
        }

        if (armed)
        {
            Interlocked.MemoryBarrierProcessWide();
        }

        for (int index = count - 1; index >= 0; index--)
        {
            NativePoolThreadCache cache = _threadCaches[index];
            cache.WaitUntilIdle();
            FoldThreadCacheLocked(cache);
            if (!cache.IsOwnerAlive)
            {
                RemoveThreadCacheLocked(cache);
            }
        }
    }

    private void RevokeThreadCacheLocked(NativePoolThreadCache cache)
    {
        if (Volatile.Read(ref cache.Revoked) == 0)
        {
            Volatile.Write(ref cache.Revoked, 1);
            if (cache.OwnerThreadSerial != NativeThreadState.CurrentSerialOrZero)
            {
                Interlocked.MemoryBarrierProcessWide();
            }
        }

        cache.WaitUntilIdle();
        FoldThreadCacheLocked(cache);
    }

    // Moves a revoked or stale cache's records and counts into its generation:
    // free records become reusable with available slabs, cached leases join the
    // allocation table, and the payload, initializer and operation counts move
    // to the generation. The owner is out of its busy window.
    private void FoldThreadCacheLocked(NativePoolThreadCache cache)
    {
        NativeGeneration? generation = cache.Generation;
        if (generation is null)
        {
            return;
        }

        generation.ReusableAllocations.EnsureCapacity(checked(generation.ReusableAllocations.Count + cache.FreeCount));
        generation.AvailableSlabs.EnsureCapacity(checked(generation.AvailableSlabs.Count + cache.FreeCount));
        generation.Allocations.EnsureCapacity(checked(generation.Allocations.Count + cache.RecordCount - cache.FreeCount));
        bool current = ReferenceEquals(generation, _current);
        for (int index = 0; index < cache.FreeCount; index++)
        {
            int slot = cache.FreeSlots[index];
            NativeAllocation allocation = cache.Records[slot]!;
            cache.Records[slot] = null;
            allocation.CacheOwner = null;
            if (current)
            {
                generation.ReusableAllocations.Add(allocation);
                generation.AddAvailableSlabOrdered(allocation.Slab!);
                generation.HotPoolAllocation =
                    generation.ReusableAllocations.Count == 1
                    && generation.AvailableSlabs.Count == 1
                        ? allocation
                        : null;
            }
        }

        cache.FreeCount = 0;
        for (int index = 0; index < cache.RecordCount; index++)
        {
            if (cache.Records[index] is NativeAllocation allocation)
            {
                cache.Records[index] = null;
                allocation.CacheOwner = null;
                generation.Allocations.Add(allocation.Id, allocation);
            }
        }

        cache.RecordCount = 0;

        // While the owner is idle, other threads only move its transfers to the
        // remote returns, which stay on the transfer list: its length bounds
        // every record below, so nothing allocates under the gate.
        int transferBound = cache.TransferCount;
        generation.Allocations.EnsureCapacity(checked(generation.Allocations.Count + transferBound));
        generation.ReusableAllocations.EnsureCapacity(checked(generation.ReusableAllocations.Count + transferBound));
        generation.AvailableSlabs.EnsureCapacity(checked(generation.AvailableSlabs.Count + transferBound));
        NativeRemoteReturns remote = cache.Remote;
        remote.Enter();
        try
        {
            for (int index = 0; index < cache.TransferCount; index++)
            {
                NativeAllocation allocation = cache.Transfers[index]!;
                cache.Transfers[index] = null;
                if (ReferenceEquals(allocation.TransferOwner, cache))
                {
                    allocation.TransferOwner = null;
                    generation.Allocations.Add(allocation.Id, allocation);
                }
            }

            cache.TransferCount = 0;
            for (int index = 0; index < cache.StashCount; index++)
            {
                NativeAllocation allocation = cache.Stash[index]!;
                cache.Stash[index] = null;
                ReuseFoldedTransfer(generation, allocation, current);
            }

            cache.StashCount = 0;
            for (int index = 0; index < remote.Count; index++)
            {
                NativeAllocation allocation = remote.Items[index]!;
                remote.Items[index] = null;
                cache.PayloadDelta -= (long)allocation.StorageBytes;
                ReuseFoldedTransfer(generation, allocation, current);
            }

            Volatile.Write(ref remote.Count, 0);
        }
        finally
        {
            remote.Exit();
        }

        cache.SettleDeferredReturn();
        long delta = cache.PayloadDelta;
        long max = cache.MaxPayloadDelta;
        if (delta != 0 || max != 0)
        {
            long before = Interlocked.Add(ref generation.InitializedPayloadBytes, delta) - delta;
            RecordPayloadPeak(before + max);
        }

        if (cache.LaneSegment is not null)
        {
            ReturnArenaLaneTailLocked(cache);
        }

        if (cache.Initializing != 0)
        {
            generation.AddInitializations(cache.Initializing);
            Volatile.Write(ref generation.ArenaInitializationsInProgress, generation.InitializationsInProgress);
        }

        if (cache.ActiveOperations != 0)
        {
            generation.AddOperations(cache.ActiveOperations);
        }

        // Unused identities go back when no later block was claimed, so one
        // thread's identities stay consecutive.
        if (_nextAllocationId == cache.LastId)
        {
            _nextAllocationId = cache.NextId - 1;
        }

        cache.NextId = 1;
        cache.LastId = 0;
        cache.PayloadDelta = 0;
        cache.MaxPayloadDelta = 0;
        cache.Initializing = 0;
        cache.ActiveOperations = 0;
        cache.Generation = null;
        Volatile.Write(ref cache.FoldEpoch, cache.FoldEpoch + 1);
        Volatile.Write(ref cache.Revoked, 1);
    }

    private static void ReuseFoldedTransfer(NativeGeneration generation, NativeAllocation allocation, bool current)
    {
        if (current)
        {
            generation.ReusableAllocations.Add(allocation);
            generation.AddAvailableSlabOrdered(allocation.Slab!);
            generation.HotPoolAllocation = null;
        }
    }

    // Ordinary unmanaged scratch on a thread other than the arena's own fast
    // thread: bump-allocates from the thread's private lane segment without the
    // owner lock. False sends the caller to the locked path. The lane's cache
    // goes to the lease, whose operations then skip looking it up.
    internal bool TryLeaseArenaLaneInitialized<T>(
        int length,
        int elementSize,
        nuint alignment,
        NativeLeaseInitializer<T> initializer,
        out NativeArenaAllocation allocation,
        out NativePoolThreadCache? laneCache)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        allocation = default;
        laneCache = null;
        nuint byteLength = CalculateArenaByteLength(length, elementSize);
        if (byteLength == 0 || NativeMemoryTestHooks.FaultInjectionPending)
        {
            return false;
        }

        NativePoolThreadCache? cache = FindOwnCache();
        if (!TryReserveArenaLane(cache, byteLength, alignment, out ArenaLaneReservation reservation)
            && !ClaimArenaLane(out cache, byteLength, alignment, out reservation))
        {
            return false;
        }

        IntPtr pointer = AddPointer(reservation.Base, reservation.Offset);
        int initializedLength = 0;
        try
        {
            NativeLeaseWriter<T> writer = new(pointer, length, ref initializedLength);
            initializer(writer);
            if (initializedLength != length)
            {
                throw new InvalidOperationException(
                    "The native lease initializer did not write all logical elements.");
            }

            CompleteArenaLane(cache, reservation, checked((long)byteLength));
        }
        catch
        {
            AbortArenaLane(cache, reservation, checked(reservation.Offset + byteLength));
            throw;
        }

        allocation = new NativeArenaAllocation(reservation.Generation, pointer, reservation.OriginalCursor, length);
        laneCache = cache;
        return true;
    }

    // A lane range filled with one value, or left unwritten, without an
    // initializer callback; see TryLeaseArenaLaneInitialized.
    internal unsafe bool TryLeaseArenaLaneFilled<T>(
        int length,
        int elementSize,
        nuint alignment,
        T value,
        bool fill,
        out NativeArenaAllocation allocation,
        out NativePoolThreadCache? laneCache)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        allocation = default;
        laneCache = null;
        nuint byteLength = CalculateArenaByteLength(length, elementSize);
        if (byteLength == 0 || NativeMemoryTestHooks.FaultInjectionPending)
        {
            return false;
        }

        NativePoolThreadCache? cache = FindOwnCache();
        // A lane with room takes, fills and publishes the range in one busy
        // window: no initializer runs, and a small fill delays a revocation
        // no longer than an operation does.
        if (cache is not null
            && (!fill || byteLength <= InWindowFillBytes)
            && cache.Enter())
        {
            NativeBumpSegment? segment = cache.LaneSegment;
            if (segment is not null)
            {
                nuint cursor = cache.LaneCursor;
                nuint aligned = AlignArenaRange(cursor, cache.LaneLimit, byteLength, alignment);
                if (aligned <= cache.LaneLimit && byteLength <= cache.LaneLimit - aligned)
                {
                    cache.LaneCursor = aligned + byteLength;
                    IntPtr direct = AddPointer(cache.LaneBase, aligned);
                    if (fill)
                    {
                        new Span<T>((void*)direct, length).Fill(value);
                    }

                    long payload = cache.PayloadDelta + (long)byteLength;
                    cache.PayloadDelta = payload;
                    if (payload > cache.MaxPayloadDelta)
                    {
                        cache.MaxPayloadDelta = payload;
                    }

                    NativeGeneration generation = cache.Generation!;
                    cache.Exit();
                    allocation = new NativeArenaAllocation(generation, direct, cursor, length);
                    laneCache = cache;
                    return true;
                }
            }

            cache.Exit();
        }

        if (!TryReserveArenaLane(cache, byteLength, alignment, out ArenaLaneReservation reservation)
            && !ClaimArenaLane(out cache, byteLength, alignment, out reservation))
        {
            return false;
        }

        IntPtr pointer = AddPointer(reservation.Base, reservation.Offset);
        if (fill)
        {
            new Span<T>((void*)pointer, length).Fill(value);
        }

        CompleteArenaLane(cache, reservation, checked((long)byteLength));
        allocation = new NativeArenaAllocation(reservation.Generation, pointer, reservation.OriginalCursor, length);
        laneCache = cache;
        return true;
    }

    private readonly record struct ArenaLaneReservation(
        NativeGeneration Generation,
        NativeBumpSegment Segment,
        IntPtr Base,
        nuint Offset,
        nuint OriginalCursor,
        int Epoch);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReserveArenaLane(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] NativePoolThreadCache? cache,
        nuint byteLength,
        nuint alignment,
        out ArenaLaneReservation reservation)
    {
        reservation = default;
        if (cache is null || !cache.Enter())
        {
            return false;
        }

        NativeBumpSegment? lane = cache.LaneSegment;
        if (lane is not null)
        {
            nuint cursor = cache.LaneCursor;
            nuint aligned = AlignArenaRange(cursor, cache.LaneLimit, byteLength, alignment);
            if (aligned <= cache.LaneLimit && byteLength <= cache.LaneLimit - aligned)
            {
                cache.LaneCursor = aligned + byteLength;
                cache.Initializing++;
                reservation = new ArenaLaneReservation(cache.Generation!, lane, cache.LaneBase, aligned, cursor, cache.FoldEpoch);
                cache.Exit();
                return true;
            }
        }

        cache.Exit();
        return false;
    }

    // Under the owner lock: arms this thread's own cache, found through the
    // thread-local tables, gives it a lane with room for the request and
    // reserves the range, so no revocation can come between. The cache the
    // stack-block table gave the caller is not used: it may be an exited
    // thread's, which a revocation may have dropped since. A full lane goes
    // back to the generation as an ordinary segment. False means the arena is
    // not active or has no room for another cache; the locked path then
    // reports or serves the request.
    private bool ClaimArenaLane(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NativePoolThreadCache? cache,
        nuint byteLength,
        nuint alignment,
        out ArenaLaneReservation reservation)
    {
        cache = NativePoolThreadCaches.Find(Id, _cacheSlot);
        reservation = default;
        lock (_gate)
        {
            if (_lifecycle != NativeOwnerLifecycle.Active || _current is not NativeGeneration generation)
            {
                return false;
            }

            if (cache is null)
            {
                if (_threadCacheCount == ThreadCacheLimit)
                {
                    return false;
                }

                cache = CreateThreadCacheLocked();
            }

            if (cache.Generation is not null && !ReferenceEquals(cache.Generation, generation))
            {
                FoldThreadCacheLocked(cache);
            }

            if (cache.LaneSegment is not null)
            {
                ReturnArenaLaneTailLocked(cache);
            }

            // A full chunk if any segment has room, else just this range in a
            // segment that has room for it, before the arena grows.
            nuint requestBytes = RequiredFreshBumpBytes(byteLength, alignment);
            nuint chunkBytes = Math.Max(requestBytes, cache.LaneChunkBytes);
            cache.LaneChunkBytes = Math.Min(cache.LaneChunkBytes * 2, ArenaLaneChunkLimit);
            NativeBumpSegment? segment = FindArenaLaneSpaceLocked(generation, ref chunkBytes, requestBytes, alignment);
            if (segment is null
                && _budget is not null
                && OtherThreadCachesHoldLaneLocked(cache))
            {
                // A budget counts every segment, so other threads' unused chunk
                // tails go back before the arena grows.
                RevokeThreadCachesLocked();
                segment = FindArenaLaneSpaceLocked(generation, ref chunkBytes, requestBytes, alignment);
            }

            if (segment is null)
            {
                segment = AllocateBumpSegmentLocked(
                    generation,
                    ChooseBumpSegmentBytes(generation, chunkBytes),
                    chunkBytes,
                    "arena lane growth",
                    _lifecycle);
                try
                {
                    AppendBumpSegmentLocked(generation, segment);
                    generation.Owner.AddSegment(segment.Segment);
                    NativeOwnerHistory.Increment(ref _freshSegmentAllocationCount, ref _historyOverflowed);
                }
                catch
                {
                    generation.BumpSegments.Remove(segment);
                    generation.Owner.RemoveSegment(segment.Segment);
                    segment.Segment.FreeNow();
                    ResetBumpTraversal(generation);
                    throw;
                }
            }

            nuint chunkStart = AlignUp(segment.LowCursor, alignment);
            segment.LowCursor = checked(chunkStart + chunkBytes);
            cache.LaneSegment = segment;
            cache.LaneBase = segment.Segment.Pointer;
            cache.LaneCursor = chunkStart;
            cache.LaneLimit = segment.LowCursor;
            cache.Generation = generation;
            Volatile.Write(ref cache.Revoked, 0);
            if (!TryReserveArenaLane(cache, byteLength, alignment, out reservation))
            {
                throw new InvalidOperationException("A new arena lane could not hold the range it was chosen for.");
            }
        }

        return true;
    }

    // Arena lane chunks double per claim up to this size.
    private const nuint ArenaLaneChunkLimit = 64 * 1024;

    // The unused tail of a thread's chunk goes back to its segment when nothing
    // was allocated after the chunk; otherwise it stays unused until the
    // generation resets.
    private static void ReturnArenaLaneTailLocked(NativePoolThreadCache cache)
    {
        NativeBumpSegment segment = cache.LaneSegment!;
        if (segment.LowCursor == cache.LaneLimit)
        {
            segment.LowCursor = cache.LaneCursor;
        }

        cache.LaneSegment = null;
        cache.LaneBase = IntPtr.Zero;
        cache.LaneCursor = 0;
        cache.LaneLimit = 0;
    }

    private static NativeBumpSegment? FindArenaLaneSpaceLocked(
        NativeGeneration generation,
        ref nuint chunkBytes,
        nuint requestBytes,
        nuint alignment)
    {
        NativeBumpSegment? segment = FindBumpSpaceLocked(generation, chunkBytes, alignment, scoped: false);
        if (segment is null && requestBytes < chunkBytes)
        {
            ResetBumpTraversal(generation);
            segment = FindBumpSpaceLocked(generation, requestBytes, alignment, scoped: false);
            if (segment is not null)
            {
                chunkBytes = requestBytes;
            }
        }

        return segment;
    }

    private bool OtherThreadCachesHoldLaneLocked(NativePoolThreadCache cache)
    {
        foreach (ref readonly NativePoolThreadCache other in _threadCaches.AsSpan(0, _threadCacheCount))
        {
            if (!ReferenceEquals(other, cache) && other.LaneSegment is not null)
            {
                return true;
            }
        }

        return false;
    }

    private void CompleteArenaLane(
        NativePoolThreadCache cache,
        ArenaLaneReservation reservation,
        long payloadBytes)
    {
        if (cache.Enter())
        {
            if (cache.FoldEpoch == reservation.Epoch)
            {
                cache.Initializing--;
                long payload = cache.PayloadDelta + payloadBytes;
                cache.PayloadDelta = payload;
                if (payload > cache.MaxPayloadDelta)
                {
                    cache.MaxPayloadDelta = payload;
                }

                cache.Exit();
                return;
            }

            cache.Exit();
        }

        // A fold moved this initializer's count to the generation. The revoker
        // holds the owner lock from revocation through the fold, so taking it
        // here sees the fold finished.
        NativeGeneration generation = reservation.Generation;
        lock (_gate)
        {
            generation.InitializationsInProgress--;
            Volatile.Write(ref generation.ArenaInitializationsInProgress, generation.InitializationsInProgress);
        }

        RecordInitialization(generation, payloadBytes, scoped: false);
    }

    // A failed initializer gives its range back when nothing was allocated after
    // it; otherwise the range stays unused until the generation resets.
    private void AbortArenaLane(
        NativePoolThreadCache cache,
        ArenaLaneReservation reservation,
        nuint end)
    {
        NativeBumpSegment segment = reservation.Segment;
        if (cache.Enter())
        {
            if (cache.FoldEpoch == reservation.Epoch)
            {
                if (cache.LaneCursor == end)
                {
                    cache.LaneCursor = reservation.OriginalCursor;
                }

                cache.Initializing--;
                cache.Exit();
                return;
            }

            cache.Exit();
        }

        // The fold returned the chunk's tail, so the segment cursor ends at this
        // range only if nothing was allocated after it. Taking the owner lock
        // sees that fold finished.
        NativeGeneration generation = reservation.Generation;
        lock (_gate)
        {
            if (segment.LowCursor == end)
            {
                segment.LowCursor = reservation.OriginalCursor;
            }

            generation.InitializationsInProgress--;
            Volatile.Write(ref generation.ArenaInitializationsInProgress, generation.InitializationsInProgress);
        }
    }
}
