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
                && cache.LeasedCount < cache.Leased.Length
                && cache.TakeFree(length) is NativeAllocation allocation)
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

            // A budget counts every slab, so a budgeted pool reuses idle records
            // other threads cache before it grows.
            if (_budget is not null
                && !CacheHoldsFit(cache, length)
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

            cache.ReserveLeased();
            cache.Generation = generation;
            Volatile.Write(ref cache.Revoked, 0);
            claimed = cache.TakeFree(length);
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
                claimed.SetSlabForCache(slab);
                claimed.CacheOwner = cache;
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
        foreach (ref readonly NativeAllocation? allocation in cache.Free.AsSpan(0, cache.FreeCount))
        {
            if (allocation!.Slab!.Capacity >= length)
            {
                return true;
            }
        }

        return false;
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
            if (!ReferenceEquals(other, cache) && Volatile.Read(ref other.FreeCount) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private void BeginCachedLease(NativePoolThreadCache cache, NativeAllocation allocation, int length)
    {
        NativeSlab slab = allocation.Slab!;
        long id = cache.NextId++;
        allocation.Reset(
            id, slab, bumpSegment: null, offsetBytes: 0, length, slab.Capacity,
            (nuint)length * (nuint)_storageElementSize, referenceRoots: null, scoped: false,
            cache.Generation!.ScopeEpoch);
        allocation.Lifecycle = NativeAllocationLifecycle.Initializing;
        cache.AddLeased(allocation);
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
        NativeLeaseInitializer<T> initializer)
    {
        long allocationId = allocation.Id;
        int initializedLength = 0;
        try
        {
            NativeLeaseWriter<T> writer = new(allocation, ref initializedLength);
            initializer(writer);
            allocation.InitializedLength = initializedLength;
            CompleteCachedInitialization(cache, allocation);
            return new NativePoolLease(allocationId, allocation);
        }
        catch
        {
            allocation.InitializedLength = initializedLength;
            AbortCachedInitialization(cache, allocation);
            throw;
        }
    }

    private void CompleteCachedInitialization(NativePoolThreadCache cache, NativeAllocation allocation)
    {
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
                cache.RemoveLeased(allocation);
                allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                allocation.InitializedLength = 0;
                cache.Initializing--;
                cache.Free[cache.FreeCount++] = allocation;
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

    // Returns a lease. Its own thread returns a cached record into the cache
    // and ignores a stale one, as the locked path does; everything else takes
    // the locked path, which first folds a cache that still holds the record.
    internal void ReturnLease(
        NativeGeneration generationState,
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        string operation = "Pooled.Dispose")
    {
        if (allocation.CacheOwner is NativePoolThreadCache cache
            && cache.OwnerThreadId == NativeThreadIdentity.Current
            && !NativeMemoryTestHooks.FaultInjectionPending)
        {
            if (cache.FreeCount == NativePoolThreadCache.FreeCapacity)
            {
                SpillThreadCacheFree(cache);
            }

            if (cache.Enter())
            {
                if (ReferenceEquals(allocation.CacheOwner, cache)
                    && cache.FreeCount < NativePoolThreadCache.FreeCapacity)
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

                    cache.RemoveLeased(allocation);
                    cache.PayloadDelta -= (long)allocation.StorageBytes;
                    allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                    cache.Free[cache.FreeCount++] = allocation;
                    cache.Exit();
                    return;
                }

                cache.Exit();
            }
        }

        ReturnLease(generationNumber, allocationId, operation);
    }

    // The busy window replaces the generation and record admission atomics for
    // an operation on this thread's own cached record.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryEnterCachedOperation(
        NativeGeneration generation,
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        string operation,
        out NativeOperationToken token)
    {
        if (allocation.CacheOwner is NativePoolThreadCache cache
            && cache.OwnerThreadId == NativeThreadIdentity.Current
            && !NativeMemoryTestHooks.FaultInjectionPending
            && cache.Enter())
        {
            if (ReferenceEquals(allocation.CacheOwner, cache)
                && ReferenceEquals(cache.Generation, generation)
                && generation.Number == generationNumber
                && allocation.Id == allocationId
                && allocation.Lifecycle == NativeAllocationLifecycle.Active)
            {
                allocation.EnterOwnerOperation();
                cache.ActiveOperations++;
                int epoch = cache.FoldEpoch;
                cache.Exit();
                token = new NativeOperationToken(this, generation, allocation, cache, epoch, operation);
                return true;
            }

            cache.Exit();
        }

        token = default;
        return false;
    }

    internal void ExitCachedOperation(
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

        // A fold moved this operation's count into the generation.
        WaitForThreadCacheFold();
        allocation.ExitOperation();
        ExitGenerationOperation(generation);
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
                allocation = cache.Free[0]!;
                cache.Free[0] = cache.Free[--cache.FreeCount];
                cache.Free[cache.FreeCount] = null;
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
        generation.Allocations.EnsureCapacity(checked(generation.Allocations.Count + cache.LeasedCount));
        bool current = ReferenceEquals(generation, _current);
        for (int index = 0; index < cache.FreeCount; index++)
        {
            NativeAllocation allocation = cache.Free[index]!;
            cache.Free[index] = null;
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
        for (int index = 0; index < cache.LeasedCount; index++)
        {
            NativeAllocation allocation = cache.Leased[index]!;
            cache.Leased[index] = null;
            allocation.CacheOwner = null;
            generation.Allocations.Add(allocation.Id, allocation);
        }

        cache.LeasedCount = 0;
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
        cache.FoldEpoch++;
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
            nuint aligned = AlignArenaBumpCursor(cursor, alignment);
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

        // A fold moved this initializer's count to the generation.
        WaitForThreadCacheFold();
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
        // range only if nothing was allocated after it.
        WaitForThreadCacheFold();
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
