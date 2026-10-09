using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Page = Supprocom.NativeAllocationManagement.NativePreparedPoolStorage.Page;
using Slot = Supprocom.NativeAllocationManagement.NativePreparedPoolStorage.Slot;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Provides the non-constructible implementation base of typed fixed-shape pools.</summary>
/// <remarks>
/// Use <see cref="NativePreparedPool{T}"/>. Consumers cannot construct or derive this base.
/// It exposes no allocation, access or cleanup operations; typed pools retain those contracts.
/// All fields belong to the same heap owner, with no separate control object.
/// </remarks>
public abstract unsafe class NativePreparedPoolBase
{
    private const string OwnerKind = "NativePreparedPool";
    private protected NativeMemoryBudget? _budget;
    private protected readonly int _ownerThreadId;
    private protected readonly Slot[] _slabs = [];
    private protected readonly Page[] _pages = [];
    private protected readonly NativePoolPreparation _preparation;
    private protected int _freeHead = -1;
    private protected NativeOwnerLifecycle _lifecycle;
    private protected int _liveLeaseCount;
    private protected int _retirementState;
    private protected long _leaseTokenCounter;
    private protected long _requestedBytes;
    private protected long _peakInitializedPayloadBytes;
    private protected long _retainedBytes;
    private protected long _trimmedBytes;
    private protected long _trimCallCount;
    private protected int _peakOccupiedSlots;
    private protected long _peakRetainedBytes;
    private protected long _successfulPreparedRents;
    private protected long _rejectedPreparedShapes;
    private protected long _rejectedPreparedFull;
    private protected long _preparedInitializerFailures;
    private protected bool _historyOverflowed;

    private protected readonly long _id;

    private protected NativePreparedPoolBase(NativePoolPreparation preparation, NativeMemoryBudget? budget, int elementSize)
    {
        _id = NativeOwnerIdentity.Next();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preparation.SlotCount, nameof(preparation));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preparation.SlotCapacity, nameof(preparation));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preparation.SlotsPerPage, nameof(preparation));
        _budget = budget;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _preparation = preparation;
        nuint slotBytes = CalculateByteLength(preparation.SlotCapacity, elementSize);
        // This owner is thread-confined and exposes ordinary typed spans, not
        // aligned SIMD addresses. Consecutive slots use the CLR element stride;
        // cache-line padding and an over-aligned backend add no authority.
        nuint stride = slotBytes;
        nuint totalBytes = checked(stride * (nuint)preparation.SlotCount);
        _ = checked((long)totalBytes);
        bool prepared = false;
        try
        {
            NativePreparedPoolStorage.Acquire(preparation, stride, totalBytes,
                budget, _id, out _slabs, out _pages, out _lifecycle);
            _freeHead = preparation.SlotCount - 1;
            _retainedBytes = checked((long)totalBytes);
            _peakRetainedBytes = _retainedBytes;
            prepared = true;
        }
        finally
        {
            if (!prepared)
            {
                _budget = null;
            }
        }
    }

    private protected NativeOwnerStatistics GetStatisticsCore(int elementSize)
    {
        ValidateColdOwner("GetStatistics");
        var counts = GetStorageCounts(elementSize);

        return new NativeOwnerStatistics(
            _lifecycle,
            Generation: 0,
            _requestedBytes,
            _retainedBytes,
            RetiredBytes: 0,
            SegmentCount: counts.Retained,
            AvailableSegmentCount: counts.Available,
            RetiredSegmentCount: 0,
            _trimmedBytes,
            _trimCallCount,
            _pages.Length)
        {
            OwnerId = _id,
            Model = NativeOwnerModel.ThreadConfinedPool,
            HistoryOverflowed = _historyOverflowed,
            OutstandingNativeBytes = _retainedBytes,
            DetachedNativeBytes = 0,
            PeakOutstandingNativeBytes = _peakRetainedBytes,
            InitializedPayloadBytes = _requestedBytes,
            PeakInitializedPayloadBytes = _peakInitializedPayloadBytes,
            UsableCapacityBytes = counts.UsableBytes
        };
    }

    private protected NativeOwnerDiagnosticSnapshot GetDiagnosticSnapshotCore(int elementSize)
    {
        ValidateColdThread("CaptureDiagnosticSnapshot");
        var counts = GetStorageCounts(elementSize);
        NativeOwnerDiagnosticSnapshot snapshot = new(
            _lifecycle, 0, 0, NativeMemoryAccounting.CurrentMetricsEpoch,
            _liveLeaseCount, 0, 0,
            _lifecycle == NativeOwnerLifecycle.Active ? _freeHead : -1,
            -1, counts.Retained,
            _lifecycle == NativeOwnerLifecycle.Active ? counts.Available : 0,
            0, 0, 0, 0, 0, false)
        {
            OwnerId = _id,
            Model = NativeOwnerModel.ThreadConfinedPool,
            HistoryOverflowed = _historyOverflowed,
            OutstandingNativeBytes = _retainedBytes,
            DetachedNativeBytes = 0,
            PeakOutstandingNativeBytes = _peakRetainedBytes,
            InitializedPayloadBytes = _requestedBytes,
            PeakInitializedPayloadBytes = _peakInitializedPayloadBytes
        };
        GC.KeepAlive(this);
        return snapshot;
    }

    private (int Retained, int Available, long UsableBytes) GetStorageCounts(int elementSize)
    {
        int pages = 0;
        int availablePages = 0;
        long usableBytes = 0;
        foreach (ref readonly Page page in _pages.AsSpan())
        {
            if (page.AllocationBytes == 0)
            {
                continue;
            }
            pages++;
            availablePages += IsPageIdle(page) ? 1 : 0;
            usableBytes += checked((long)page.SlotCount * _preparation.SlotCapacity * elementSize);
        }
        return (pages, availablePages, usableBytes);
    }

    private protected nuint TrimRetainedMemoryCore(nuint byteBudget)
    {
        ValidateColdOwner("TrimRetainedMemory");
        IncrementHistory(ref _trimCallCount);
        nuint released = 0;
        foreach (ref Page page in _pages.AsSpan())
        {
            if (released >= byteBudget || page.AllocationBytes == 0 || !IsPageIdle(page))
            {
                continue;
            }
            released = checked(released + page.AllocationBytes);
            _slabs.AsSpan(page.FirstSlot, page.SlotCount).Clear();
            FreePage(ref page, trimmed: true);
        }
        NativeOwnerHistory.Add(ref _trimmedBytes, checked((long)released), ref _historyOverflowed);
        _freeHead = -1;
        // Cleared slots have Next = 0 but no backing authority. Only retained
        // page ranges may contribute free links; preserve descending pop order.
        foreach (ref readonly Page page in _pages.AsSpan())
        {
            if (page.AllocationBytes == 0)
            {
                continue;
            }
            int end = page.FirstSlot + page.SlotCount;
            for (int index = page.FirstSlot; index < end; index++)
            {
                if (_slabs[index].Next >= -1)
                {
                    _slabs[index].Next = _freeHead;
                    _freeHead = index;
                }
            }
        }
        return released;
    }

    private protected void RetireCore()
    {
        ValidateColdOwner("Retire");
        // Initializing and borrowed slots are included in the live count. Return
        // cannot decrement it while borrowed, so no redundant slot scan is needed.
        if (_liveLeaseCount != 0)
        {
            ThrowLiveRetirementState();
        }
        _lifecycle = NativeOwnerLifecycle.Returned;
        Volatile.Write(ref _retirementState, 1);
    }

    private protected void ReleaseRetiredStorageCore()
    {
        int prior = Interlocked.CompareExchange(
            ref _retirementState,
            2,
            1);
        if (prior != 1)
        {
            ThrowInvalidRetiredCleanup(prior);
        }

        _lifecycle = NativeOwnerLifecycle.Disposed;
        FreeAllCore();
    }

    private protected bool DisposeCore()
    {
        ValidateColdThread("Dispose");
        if (Volatile.Read(ref _retirementState) != 0)
        {
            ThrowRetiredDispose();
        }
        if (_lifecycle == NativeOwnerLifecycle.Disposed)
        {
            return false;
        }
        if (_liveLeaseCount != 0)
        {
            ThrowLiveLease();
        }
        _lifecycle = NativeOwnerLifecycle.Disposed;
        FreeAllCore();
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateColdOwner(string operation)
    {
        ValidateColdThread(operation);
        if (_lifecycle != NativeOwnerLifecycle.Active)
        {
            ThrowDisposed(operation);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateColdThread(string operation)
    {
        if (Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            return;
        }

        ThrowWrongThread(operation);
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLiveLease() =>
        throw new InvalidOperationException(
            "NativePreparedPool cannot dispose while a pooled lease is active.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLiveRetirementState() =>
        throw new InvalidOperationException(
            "NativePreparedPool cannot retire while a lease, initializer, or callback is active.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRetiredDispose() =>
        throw new InvalidOperationException(
            "NativePreparedPool.Dispose cannot release retired storage. Use ReleaseRetiredStorage on the coordinator thread.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidRetiredCleanup(int state) =>
        throw new InvalidOperationException(
            state == 0
                ? "NativePreparedPool must retire before coordinator cleanup."
                : "NativePreparedPool retired storage was already released.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowDisposed(string operation) =>
        throw new NativeAllocationDisposedException(
            "The native pool is disposed.",
            OwnerKind,
            generation: 0,
            currentGeneration: 0,
            operation,
            activeOperationCount: 0,
            allocationId: 0,
            _lifecycle);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowWrongThread(string operation) =>
        throw new NativeAllocationStateException(
            "NativePreparedPool is confined to its construction thread.",
            OwnerKind,
            generation: 0,
            currentGeneration: 0,
            operation,
            activeOperationCount: 0,
            allocationId: 0,
            _lifecycle);

    private protected void FreeAllCore()
    {
        // Slot authority is managed metadata; physical ownership lives only in pages.
        Array.Clear(_slabs);
        _freeHead = -1;
        foreach (ref Page page in _pages.AsSpan())
        {
            FreePage(ref page);
        }
        _retainedBytes = 0;
        _budget = null;
    }

    private bool IsPageIdle(in Page page)
    {
        foreach (ref readonly Slot slab in _slabs.AsSpan(page.FirstSlot, page.SlotCount))
        {
            if (slab.Next < -1 || slab.BorrowCount != 0)
            {
                return false;
            }
        }
        return true;
    }

    private void FreePage(ref Page page, bool trimmed = false)
    {
        if (page.AllocationBytes == 0)
        {
            return;
        }
        nuint bytes = page.AllocationBytes;
        NativeMemory.Free((void*)page.Pointer);
        _budget?.Release(bytes, _id,
            trimmed ? NativeMemoryTraceKind.Trimmed : NativeMemoryTraceKind.Released,
            page.Ordinal);
        NativeMemoryAccounting.RecordFree(bytes, detached: false, page.MetricsEpoch);
        _retainedBytes -= checked((long)bytes);
        page = default;
    }

    private protected NativePreparedPoolStatistics GetPreparedStatisticsCore(int elementSize)
    {
        ValidateColdThread("CapturePreparedSnapshot");
        int pages = 0;
        int slots = 0;
        foreach (ref readonly Page page in _pages.AsSpan())
        {
            if (page.AllocationBytes != 0)
            {
                pages++;
                slots += page.SlotCount;
            }
        }
        long bankBytes = checked((long)_slabs.Length * Unsafe.SizeOf<Slot>()
            + (long)_pages.Length * Unsafe.SizeOf<Page>());
        NativePreparedPoolStatistics result = new(_id, _lifecycle, _preparation,
            pages, slots, _liveLeaseCount, _peakOccupiedSlots, _retainedBytes, _peakRetainedBytes,
            _successfulPreparedRents, _rejectedPreparedShapes, _rejectedPreparedFull,
            _preparedInitializerFailures, bankBytes,
            checked((long)(slots - _liveLeaseCount) * _preparation.SlotCapacity * elementSize),
            _historyOverflowed);
        GC.KeepAlive(this);
        return result;
    }

    private void IncrementHistory(ref long counter) =>
        NativeOwnerHistory.Increment(ref counter, ref _historyOverflowed);

    private protected long[] GetSegmentOrdinalsCore(int elementSize)
    {
        ValidateColdThread("GetSegmentOrdinals");
        long[] result = new long[GetStorageCounts(elementSize).Retained];
        int index = 0;
        foreach (ref readonly Page page in _pages.AsSpan())
        {
            if (page.AllocationBytes != 0)
            {
                result[index++] = page.Ordinal;
            }
        }
        GC.KeepAlive(this);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint CalculateByteLength(int length, int elementSize)
    {
        if (IntPtr.Size == 4)
        {
            ulong bytes = (ulong)(uint)length
                * (uint)elementSize;
            if (bytes > uint.MaxValue)
            {
                throw new OverflowException(
                    "The pooled byte count exceeds native addressable storage.");
            }
        }

        return (nuint)(uint)length * (nuint)elementSize;
    }
}
