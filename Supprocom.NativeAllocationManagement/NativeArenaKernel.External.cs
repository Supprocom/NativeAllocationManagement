using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

internal sealed unsafe partial class NativeArenaKernel
{
    private NativeSegment? _externalBuffer;
    private IntPtr _externalHeaderMemory;
    private nuint _externalHeaderBytes;
    private long _externalActiveBytes;
    private long _externalRetainedBytes;
    private int _externalHeaderCount;

    internal NativeArenaKernel(SafeBuffer buffer, nuint byteOffset,
        NativeArenaPreparation preparation, NativeMemoryBudget budget)
    {
        _returnMemoryOnDispose = NativeMemoryReturn.ToNativeMemory;
        _budget = budget;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _lifecycle = NativeOwnerLifecycle.Active;
        _prepared = true;
        _preparation = preparation;
        nuint totalBytes = checked(preparation.OrdinaryBytes + preparation.ScopedBytes);
        _ = checked((long)totalBytes);
        if (preparation.ScopedBytes != 0 && preparation.OrdinaryBytes % SegmentAlignment != 0)
        {
            throw new ArgumentException("The ordinary bound must align the scoped lane to 64 bytes.", nameof(preparation));
        }
        if (totalBytes == 0)
        {
            budget.RecordPreparation(0, Id);
            return;
        }

        int headerCount = (preparation.OrdinaryBytes != 0 ? 1 : 0)
            + (preparation.ScopedBytes != 0 ? 1 : 0);
        nuint headerBytes = NativeAlignedAllocation.GetBackingByteLength(checked(HeaderBytes * (nuint)headerCount));
        budget.Reserve(headerBytes, Id);
        bool committed = false;
        bool recorded = false;
        long metricsEpoch = 0;
        NativeSegment? external = null;
        void* headers = null;
        try
        {
            external = NativeSegment.Borrow(buffer, byteOffset, totalBytes);
            if (NativeMemoryTestHooks.ConsumeForcedFailure())
            {
                throw new NativeAllocationFailedException(headerBytes, OwnerKind,
                    unchecked((long)_generation), "external preparation", _lifecycle);
            }
            headers = NativeMemory.AlignedAlloc(headerBytes, SegmentAlignment);
            if (headers == null)
            {
                throw new NativeAllocationFailedException(headerBytes, OwnerKind,
                    unchecked((long)_generation), "external preparation", _lifecycle);
            }
            NativeMemory.Clear(headers, headerBytes);
            metricsEpoch = NativeMemoryAccounting.RecordAllocation(headerBytes, zeroed: false);
            recorded = true;
            budget.Commit(headerBytes, Id, allocationOrdinal: 1);
            committed = true;
            _externalBuffer = external;
            _externalHeaderMemory = (IntPtr)headers;
            _externalHeaderBytes = headerBytes;
            _externalHeaderCount = headerCount;
            _externalActiveBytes = checked((long)totalBytes);
            _externalRetainedBytes = _externalActiveBytes;
            _retainedBytes = checked((long)headerBytes);
            _preparedPeakRetainedBytes = _retainedBytes;
            _segmentCount = headerCount;
            _freshSegmentAllocationCount = 1;
            _nextAllocationOrdinal = 1;
            ArenaSegmentHeader* nextHeader = (ArenaSegmentHeader*)headers;
            byte* data = (byte*)external.Pointer;
            if (preparation.OrdinaryBytes != 0)
            {
                InitializeExternalLane(ref _ordinary, nextHeader, data,
                    preparation.OrdinaryBytes, metricsEpoch);
                nextHeader = (ArenaSegmentHeader*)((byte*)nextHeader + HeaderBytes);
            }
            if (preparation.ScopedBytes != 0)
            {
                InitializeExternalLane(ref _scoped, nextHeader,
                    data + preparation.OrdinaryBytes, preparation.ScopedBytes, metricsEpoch);
            }
            budget.RecordPreparation(headerBytes, Id);
        }
        catch
        {
            // Publication is constructor-local: no lease can observe partially prepared lanes.
            _ordinary = default;
            _scoped = default;
            _externalBuffer = null;
            _externalHeaderMemory = IntPtr.Zero;
            _externalHeaderCount = 0;
            _externalActiveBytes = 0;
            _externalRetainedBytes = 0;
            _retainedBytes = 0;
            _segmentCount = 0;
            if (headers != null)
            {
                NativeMemory.AlignedFree(headers);
                if (recorded)
                {
                    NativeMemoryAccounting.RecordFree(headerBytes, detached: false, metricsEpoch);
                }
            }
            if (committed)
            {
                budget.Release(headerBytes, Id, allocationOrdinal: 1);
            }
            else
            {
                budget.Cancel(headerBytes, Id);
            }
            external?.FreeNow();
            throw;
        }
    }

    private static void InitializeExternalLane(ref ArenaLane lane,
        ArenaSegmentHeader* header, byte* data, nuint capacity, long metricsEpoch)
    {
        header->DataStart = data;
        header->Capacity = capacity;
        header->MetricsEpoch = metricsEpoch;
        header->AllocationOrdinal = 1;
        header->External = 1;
        lane.First = header;
        lane.Tail = header;
        ResetLane(ref lane);
    }

    private void FreeExternalSegment(ArenaSegmentHeader* segment, NativeMemoryTraceKind traceKind)
    {
        _externalActiveBytes -= checked((long)segment->Capacity);
        _segmentCount--;
        _externalHeaderCount--;
        if (_externalHeaderCount != 0)
        {
            return;
        }
        long metricsEpoch = segment->MetricsEpoch;
        NativeMemory.AlignedFree((void*)_externalHeaderMemory);
        _externalHeaderMemory = IntPtr.Zero;
        _budget?.Release(_externalHeaderBytes, Id, traceKind, allocationOrdinal: 1);
        NativeMemoryAccounting.RecordFree(_externalHeaderBytes, detached: false, metricsEpoch);
        _retainedBytes -= checked((long)_externalHeaderBytes);
        _externalHeaderBytes = 0;
        NativeSegment? external = _externalBuffer;
        _externalBuffer = null;
        _externalRetainedBytes = 0;
        external?.FreeNow();
    }
}
