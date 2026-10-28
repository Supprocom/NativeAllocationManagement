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

    private bool ThreadCacheEligible =>
        _kind == NativeOwnerKind.Pool && !_containsReferences;

    // Returns the cache too: once the busy window closes, a revocation on
    // another thread may clear the record's CacheOwner.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeAllocation? TakeCachedForRent(int length, out NativePoolThreadCache? cache)
    {
        cache = NativePoolThreadCaches.Find(Id);
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
        NativePoolThreadCache? evicted = null;
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

                cache = CreateThreadCacheLocked(out evicted);
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

        if (evicted is not null)
        {
            DetachEvictedThreadCache(evicted);
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

        if (Volatile.Read(ref cache.RemoteFreeCount) == 0)
        {
            return false;
        }

        cache.EnterTransferGate();
        bool fits = FindRemoteFreeFit(cache, length) >= 0;
        cache.ExitTransferGate();
        return fits;
    }

    private static bool HasAvailableSlabLocked(NativeGeneration generation, int length)
    {
        foreach (ref readonly NativeSlab slab in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(generation.AvailableSlabs))
        {
            if (slab.Capacity >= length)
            {
                return true;
            }
        }

        return false;
    }

    private bool OtherThreadCachesHoldFreeLocked(NativePoolThreadCache cache)
    {
        foreach (ref readonly NativePoolThreadCache other in _threadCaches.AsSpan(0, _threadCacheCount))
        {
            if (!ReferenceEquals(other, cache)
                && (Volatile.Read(ref other.FreeCount) != 0 || Volatile.Read(ref other.RemoteFreeCount) != 0))
            {
                return true;
            }
        }

        return false;
    }

    // In the owner's busy window or under the owner lock, with room for one
    // more record: takes the smallest record other threads returned that
    // holds the length.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeAllocation? TakeRemoteFree(NativePoolThreadCache cache, int length)
    {
        if (Volatile.Read(ref cache.RemoteFreeCount) == 0)
        {
            return null;
        }

        cache.EnterTransferGate();
        int best = FindRemoteFreeFit(cache, length);
        if (best < 0)
        {
            cache.ExitTransferGate();
            return null;
        }

        NativeAllocation allocation = cache.RemoteFree[best]!;
        int last = --cache.RemoteFreeCount;
        cache.RemoteFree[best] = cache.RemoteFree[last];
        cache.RemoteFree[last] = null;
        // While the cache has room, bring more of them along: in a pipeline
        // every return is remote, and each one carried now spares a later rent
        // the gate and the slow path.
        int extra = Math.Min(
            Math.Min(NativePoolThreadCache.FreeCapacity - cache.FreeCount, cache.Records.Length - cache.RecordCount - 1),
            cache.RemoteFreeCount);
        for (int index = 0; index < extra; index++)
        {
            int newest = --cache.RemoteFreeCount;
            cache.RemoteBatch[index] = cache.RemoteFree[newest];
            cache.RemoteFree[newest] = null;
        }

        cache.ExitTransferGate();
        allocation.CacheOwner = cache;
        cache.AddRecord(allocation);
        cache.PayloadDelta -= (long)allocation.StorageBytes;
        for (int index = 0; index < extra; index++)
        {
            NativeAllocation returned = cache.RemoteBatch[index]!;
            cache.RemoteBatch[index] = null;
            returned.CacheOwner = cache;
            cache.AddRecord(returned);
            cache.AddFree(returned);
            cache.PayloadDelta -= (long)returned.StorageBytes;
        }

        return allocation;
    }

    // Under the transfer gate. The most recent return usually fits: a
    // pipeline rents and returns one size.
    private static int FindRemoteFreeFit(NativePoolThreadCache cache, int length)
    {
        int newest = cache.RemoteFreeCount - 1;
        if (newest >= 0 && cache.RemoteFree[newest]!.Slab!.Capacity is int newestCapacity
            && newestCapacity >= length && newestCapacity <= 2 * length)
        {
            return newest;
        }

        int best = -1;
        int bestCapacity = int.MaxValue;
        for (int index = 0; index < cache.RemoteFreeCount; index++)
        {
            int capacity = cache.RemoteFree[index]!.Slab!.Capacity;
            if (capacity >= length && capacity < bestCapacity)
            {
                best = index;
                bestCapacity = capacity;
            }
        }

        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BeginCachedLease(NativePoolThreadCache cache, NativeAllocation allocation, int length)
    {
        NativeSlab slab = allocation.Slab!;
        allocation.ResetCached(
            cache.NextId++, length, (nuint)length * (nuint)_storageElementSize,
            cache.Generation!.ScopeEpoch, slab.Segment.Pointer);
        cache.Initializing++;
        if (slab.HasBeenUsed)
        {
            NativeMemoryAccounting.RecordReusedNativeSegment();
        }

        slab.HasBeenUsed = true;
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

    // Rents a lease filled with one value, or left unwritten, without an
    // initializer callback: a cached record is filled in place and published;
    // anything else takes the initializer path with an equivalent fill.
    // Elements that are or contain references are always written.
    internal NativePoolLease RentFilled<T>(int length, T value, bool fill, bool transferable = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > 0
            && ThreadCacheEligible
            && !NativeMemoryTestHooks.FaultInjectionPending
            && NativePoolThreadCaches.Find(Id) is NativePoolThreadCache cache
            && cache.Enter())
        {
            if (cache.NextId <= cache.LastId
                && cache.TakeFree(length) is NativeAllocation allocation)
            {
                if (!transferable
                    && (!fill || (nuint)length * (nuint)_storageElementSize <= InWindowFillBytes))
                {
                    long allocationId = PublishCachedLease(cache, allocation, length, value, fill);
                    cache.Exit();
                    return new NativePoolLease(allocationId, allocation);
                }

                BeginCachedLease(cache, allocation, length);
                cache.Exit();
                return FillCached(cache, allocation, value, fill, transferable);
            }

            cache.Exit();
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

    // Begins, fills and publishes a cached lease in one busy window: no
    // initializer runs, so the lease never shows as initializing.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long PublishCachedLease<T>(NativePoolThreadCache cache, NativeAllocation allocation, int length, T value, bool fill)
    {
        NativeSlab slab = allocation.Slab!;
        long allocationId = cache.NextId++;
        nuint storageBytes = (nuint)length * (nuint)_storageElementSize;
        allocation.ResetCached(allocationId, length, storageBytes, cache.Generation!.ScopeEpoch, slab.Segment.Pointer);
        if (slab.HasBeenUsed)
        {
            NativeMemoryAccounting.RecordReusedNativeSegment();
        }

        slab.HasBeenUsed = true;
        if (fill)
        {
            CachedSpan<T>(allocation, length).Fill(value);
        }

        long payload = cache.PayloadDelta + (long)storageBytes;
        cache.PayloadDelta = payload;
        if (payload > cache.MaxPayloadDelta)
        {
            cache.MaxPayloadDelta = payload;
        }

        allocation.Lifecycle = NativeAllocationLifecycle.Active;
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
                long payload = cache.PayloadDelta + (long)allocation.StorageBytes;
                cache.PayloadDelta = payload;
                if (payload > cache.MaxPayloadDelta)
                {
                    cache.MaxPayloadDelta = payload;
                }

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
        // Only this thread adds transfers; other threads and folds only remove
        // them, so a list with room now keeps it. Growing copies the list under
        // its gate into an array allocated outside it.
        if (transferable && Volatile.Read(ref cache.TransferCount) == cache.Transfers.Length)
        {
            NativeAllocation?[] grown = new NativeAllocation?[cache.Transfers.Length * 2];
            cache.EnterTransferGate();
            Array.Copy(cache.Transfers, grown, cache.TransferCount);
            cache.Transfers = grown;
            cache.ExitTransferGate();
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
                    cache.RemoveRecord(allocation);
                    allocation.CacheOwner = null;
                    cache.EnterTransferGate();
                    cache.AddTransfer(allocation);
                    allocation.TransferOwner = cache;
                    cache.ExitTransferGate();
                }

                allocation.InitializedLength = 0;
                long payload = cache.PayloadDelta + (long)allocation.StorageBytes;
                cache.PayloadDelta = payload;
                if (payload > cache.MaxPayloadDelta)
                {
                    cache.MaxPayloadDelta = payload;
                }

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

    // Puts this thread's valid, idle cached lease back into its cache.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryReturnCachedLease(
        NativePoolThreadCache cache,
        NativeGeneration generation,
        NativeAllocation allocation,
        long generationNumber,
        long allocationId)
    {
        if (cache.FreeCount < NativePoolThreadCache.FreeCapacity && cache.Enter())
        {
            if (ReferenceEquals(allocation.CacheOwner, cache)
                && allocation.Id == allocationId
                && allocation.Lifecycle == NativeAllocationLifecycle.Active
                && ReferenceEquals(cache.Generation, generation)
                && generation.Number == generationNumber
                && allocation.HasNoOwnerOperations)
            {
                cache.PayloadDelta -= (long)allocation.StorageBytes;
                allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                cache.AddFree(allocation);
                cache.Exit();
                return true;
            }

            cache.Exit();
        }

        return false;
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

            if (TryReturnCachedLease(cache, generationState, allocation, generationNumber, allocationId))
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
    // path, which reports entries to them.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryEnterCachedLeaseOperation(
        NativePoolThreadCache cache,
        NativeGeneration generation,
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        out int epoch)
    {
        if (!NativeMemoryTestHooks.OperationHooksEnabled && cache.Enter())
        {
            if (ReferenceEquals(allocation.CacheOwner, cache)
                && ReferenceEquals(cache.Generation, generation)
                && generation.Number == generationNumber
                && allocation.Id == allocationId
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
        NativeGeneration generation,
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

        ExitFoldedOperation(generation, allocation, cache, epoch);
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
            if (TryEnterCachedLeaseOperation(cache, generation, allocation, generationNumber, allocationId, out int epoch))
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
    // instead of the shared generation counter.
    private bool TryEnterTransferOperation(
        NativeGeneration generation,
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        string operation,
        out NativeOperationToken token)
    {
        NativePoolThreadCache? cache = NativePoolThreadCaches.Find(Id);
        if (cache is not null && cache.Enter())
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
            && NativePoolThreadCaches.Find(Id) is NativePoolThreadCache found
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
            && (known ?? NativePoolThreadCaches.Find(Id)) is NativePoolThreadCache found
            && found.Enter())
        {
            if (ReferenceEquals(found.Generation, allocation.GenerationState)
                && IsArenaFastHandleActive(allocation))
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
            ExitCachedLeaseOperation(generation, allocation, cache, epoch);
            return;
        }

        ExitCacheCountedOperation(generation, cache, epoch);
    }

    // Returns a transferable lease from any thread. A lease still on its renting
    // cache's transfer list leaves the list under that list's gate; its record
    // and slab then go back to the renting thread, into the returning thread's
    // cache, or to the generation. A lease a fold moved to the generation takes
    // the locked path. Its transfer control already turned away new borrows and
    // found none running, so the record's admission needs no closing here.
    internal void ReturnTransferLease(
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        string operation)
    {
        if (allocation.TransferOwner is NativePoolThreadCache owner)
        {
            NativePoolThreadCache? local = NativePoolThreadCaches.Find(Id);
            bool remote = !ReferenceEquals(local, owner);
            owner.EnterTransferGate();
            if (ReferenceEquals(allocation.TransferOwner, owner))
            {
                if (allocation.Id != allocationId
                    || allocation.Lifecycle != NativeAllocationLifecycle.Active
                    || allocation.GenerationState.Number != generationNumber)
                {
                    owner.ExitTransferGate();
                    return;
                }

                owner.RemoveTransfer(allocation);
                allocation.TransferOwner = null;
                allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                if (remote && owner.RemoteFreeCount < NativePoolThreadCache.RemoteFreeCapacity)
                {
                    // The renting thread takes it back on a later rent; its
                    // payload leaves that thread's count then.
                    owner.RemoteFree[owner.RemoteFreeCount++] = allocation;
                    owner.ExitTransferGate();
                    return;
                }

                owner.ExitTransferGate();
                RecycleReturnedTransfer(allocation, local);
                return;
            }

            owner.ExitTransferGate();
        }

        ReturnLease(generationNumber, allocationId, operation);
    }

    // The returned record is in no list yet; a transition that runs now treats
    // its slab as idle, which it is.
    private void RecycleReturnedTransfer(NativeAllocation allocation, NativePoolThreadCache? cache)
    {
        NativeGeneration generation = allocation.GenerationState;
        long payloadBytes = (long)allocation.StorageBytes;
        if (cache is not null
            && !NativeMemoryTestHooks.FaultInjectionPending
            && cache.Enter())
        {
            if (ReferenceEquals(cache.Generation, generation)
                && cache.FreeCount < NativePoolThreadCache.FreeCapacity
                && cache.HasRecordRoom)
            {
                allocation.CacheOwner = cache;
                cache.AddRecord(allocation);
                cache.AddFree(allocation);
                cache.PayloadDelta -= payloadBytes;
                cache.Exit();
                return;
            }

            cache.Exit();
        }

        lock (_gate)
        {
            if (ReferenceEquals(generation, _current))
            {
                generation.ReusableAllocations.Add(allocation);
                generation.AddAvailableSlabOrdered(allocation.Slab!);
                generation.HotPoolAllocation = null;
            }

            PublishThreadCachePeak(generation);
            RecordPayloadReturn(generation, payloadBytes, scoped: false);
        }
    }

    // The generation's payload is about to change on the locked path. This
    // thread's cached high-water mark was reached against the payload before
    // that change, so it is recorded first; single-threaded peaks stay exact.
    private void PublishThreadCachePeak(NativeGeneration generation)
    {
        if (_threadCacheCount == 0
            || NativePoolThreadCaches.Find(Id) is not NativePoolThreadCache cache
            || !cache.Enter())
        {
            return;
        }

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

    private NativePoolThreadCache CreateThreadCacheLocked(out NativePoolThreadCache? evicted)
    {
        NativePoolThreadCache cache = new(this, Thread.CurrentThread);
        if (_threadCacheCount == _threadCaches.Length)
        {
            NativePoolThreadCache[] grown = new NativePoolThreadCache[Math.Max(4, _threadCacheCount * 2)];
            _threadCaches.AsSpan(0, _threadCacheCount).CopyTo(grown);
            _threadCaches = grown;
        }

        _threadCaches[_threadCacheCount++] = cache;
        evicted = NativePoolThreadCaches.Remember(cache);
        return cache;
    }

    // A thread that uses many pools drops its least recently used cache.
    private static void DetachEvictedThreadCache(NativePoolThreadCache evicted)
    {
        if (evicted.Kernel.TryGetTarget(out NativeOwnerKernel? kernel))
        {
            kernel.RemoveThreadCache(evicted);
        }
    }

    private void RemoveThreadCache(NativePoolThreadCache cache)
    {
        lock (_gate)
        {
            RevokeThreadCacheLocked(cache);
            RemoveThreadCacheLocked(cache);
        }
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

        bool armed = false;
        foreach (ref readonly NativePoolThreadCache cache in _threadCaches.AsSpan(0, count))
        {
            if (Volatile.Read(ref cache.Revoked) == 0)
            {
                Volatile.Write(ref cache.Revoked, 1);
                armed = true;
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
            Interlocked.MemoryBarrierProcessWide();
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
        // remote-free list or take them off, so the two counts together cannot
        // grow: room made now holds, and nothing allocates under the gate.
        cache.EnterTransferGate();
        int transferBound = cache.TransferCount + cache.RemoteFreeCount;
        cache.ExitTransferGate();
        generation.Allocations.EnsureCapacity(checked(generation.Allocations.Count + transferBound));
        generation.ReusableAllocations.EnsureCapacity(checked(generation.ReusableAllocations.Count + transferBound));
        generation.AvailableSlabs.EnsureCapacity(checked(generation.AvailableSlabs.Count + transferBound));
        cache.EnterTransferGate();
        try
        {
            for (int index = 0; index < cache.TransferCount; index++)
            {
                NativeAllocation allocation = cache.Transfers[index]!;
                cache.Transfers[index] = null;
                allocation.TransferOwner = null;
                generation.Allocations.Add(allocation.Id, allocation);
            }

            cache.TransferCount = 0;
            for (int index = 0; index < cache.RemoteFreeCount; index++)
            {
                NativeAllocation allocation = cache.RemoteFree[index]!;
                cache.RemoteFree[index] = null;
                cache.PayloadDelta -= (long)allocation.StorageBytes;
                if (current)
                {
                    generation.ReusableAllocations.Add(allocation);
                    generation.AddAvailableSlabOrdered(allocation.Slab!);
                    generation.HotPoolAllocation = null;
                }
            }

            cache.RemoteFreeCount = 0;
        }
        finally
        {
            cache.ExitTransferGate();
        }

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

    // Ordinary unmanaged scratch on a thread other than the arena's own fast
    // thread: bump-allocates from the thread's private lane segment without the
    // owner lock. False sends the caller to the locked path.
    internal bool TryLeaseArenaLaneInitialized<T>(
        int length,
        int elementSize,
        nuint alignment,
        NativeLeaseInitializer<T> initializer,
        out NativeArenaAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        allocation = default;
        nuint byteLength = CalculateArenaByteLength(length, elementSize);
        if (byteLength == 0 || NativeMemoryTestHooks.FaultInjectionPending)
        {
            return false;
        }

        NativePoolThreadCache? cache = NativePoolThreadCaches.Find(Id);
        if (!TryReserveArenaLane(cache, byteLength, alignment, out ArenaLaneReservation reservation)
            && !ClaimArenaLane(ref cache, byteLength, alignment, out reservation))
        {
            return false;
        }

        IntPtr pointer = AddPointer(reservation.Segment.Segment.Pointer, reservation.Offset);
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

        NativePoolThreadCache? cache = NativePoolThreadCaches.Find(Id);
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
                    IntPtr direct = AddPointer(segment.Segment.Pointer, aligned);
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
            && !ClaimArenaLane(ref cache, byteLength, alignment, out reservation))
        {
            return false;
        }

        IntPtr pointer = AddPointer(reservation.Segment.Segment.Pointer, reservation.Offset);
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
                reservation = new ArenaLaneReservation(cache.Generation!, lane, aligned, cursor, cache.FoldEpoch);
                cache.Exit();
                return true;
            }
        }

        cache.Exit();
        return false;
    }

    // Under the owner lock: arms the thread's cache, gives it a lane with room
    // for the request and reserves the range, so no revocation can come
    // between. A full lane goes back to the generation as an ordinary segment.
    // False means the arena is not active or has no room for another cache;
    // the locked path then reports or serves the request.
    private bool ClaimArenaLane(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] ref NativePoolThreadCache? cache,
        nuint byteLength,
        nuint alignment,
        out ArenaLaneReservation reservation)
    {
        reservation = default;
        NativePoolThreadCache? evicted = null;
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

                cache = CreateThreadCacheLocked(out evicted);
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
            cache.LaneCursor = chunkStart;
            cache.LaneLimit = segment.LowCursor;
            cache.Generation = generation;
            Volatile.Write(ref cache.Revoked, 0);
            if (!TryReserveArenaLane(cache, byteLength, alignment, out reservation))
            {
                throw new InvalidOperationException("A new arena lane could not hold the range it was chosen for.");
            }
        }

        if (evicted is not null)
        {
            DetachEvictedThreadCache(evicted);
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
