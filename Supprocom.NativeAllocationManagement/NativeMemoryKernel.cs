using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

internal enum NativeOwnerKind
{
    Pool,
    Region,
    Arena
}

/// <summary>Describes the lifecycle gate state of a native owner.</summary>
public enum NativeOwnerLifecycle
{
    /// <summary>The owner accepts allocations and bounded native operations.</summary>
    Active,
    /// <summary>A lifecycle transition has claimed the owner gate.</summary>
    Returning,
    /// <summary>A lease-generation transition has claimed the owner gate.</summary>
    RollingOver,
    /// <summary>The current generation is stale and cannot be used.</summary>
    Returned,
    /// <summary>The owner is permanently closed.</summary>
    Disposed,
    /// <summary>The owner-shaped value was never constructed.</summary>
    Uninitialized,
    /// <summary>The owner was declared without leasing its first generation.</summary>
    Unleased
}

internal enum NativeAllocationLifecycle
{
    Initializing,
    Active,
    Returning,
    Returned
}

internal static class NativeOperationAdmission
{
    private const int ClosedBit = int.MinValue;
    private const int CountMask = int.MaxValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryEnter(ref int state)
    {
        int entered = Interlocked.Increment(ref state);
        if (entered > 0)
        {
            return true;
        }

        Interlocked.Decrement(ref state);
        if (entered == ClosedBit)
        {
            throw new InvalidOperationException(
                "The native operation count reached its limit.");
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Exit(ref int state)
    {
        int exited = Interlocked.Decrement(ref state);
        int count = exited & CountMask;
        if (count == CountMask)
        {
            Interlocked.Increment(ref state);
            throw new InvalidOperationException(
                "The native operation count is already zero.");
        }

        return count;
    }

    internal static int Close(ref int state) =>
        Interlocked.Or(ref state, ClosedBit) & CountMask;

    internal static void Open(ref int state)
    {
        while (true)
        {
            int observed = Volatile.Read(ref state);
            int opened = observed & CountMask;
            if (observed == opened
                || Interlocked.CompareExchange(
                    ref state,
                    opened,
                    observed)
                == observed)
            {
                return;
            }
        }
    }

    internal static void Reset(ref int state) =>
        Volatile.Write(ref state, 0);

    internal static int Count(ref int state) =>
        Volatile.Read(ref state) & CountMask;
}

internal readonly record struct NativePoolLease(
    long AllocationId,
    NativeAllocation AllocationState)
{
    internal long Generation =>
        AllocationState.GenerationState.Number;

    internal int Length => AllocationState.Length;

    internal int Capacity => AllocationState.Capacity;

    internal NativeGeneration GenerationState =>
        AllocationState.GenerationState;
}

internal readonly record struct NativeRegionAllocation(
    long AllocationId,
    NativeAllocation AllocationState)
{
    internal long Generation =>
        AllocationState.GenerationState.Number;

    internal int Length => AllocationState.Length;

    internal int Capacity => AllocationState.Capacity;

    internal NativeGeneration GenerationState =>
        AllocationState.GenerationState;
}

internal readonly struct NativeBumpInitialization
{
    internal NativeBumpInitialization(
        NativeAllocation allocation,
        NativeBumpSegment? createdSegment,
        nuint originalCursor,
        bool cursorCaptured,
        bool scoped,
        bool startedScope)
    {
        Allocation = allocation;
        CreatedSegment = createdSegment;
        OriginalCursor = originalCursor;
        CursorCaptured = cursorCaptured;
        Scoped = scoped;
        StartedScope = startedScope;
    }

    internal NativeGeneration Generation =>
        Allocation.GenerationState;

    internal NativeAllocation Allocation { get; }

    internal NativeRegionAllocation Lease =>
        new(Allocation.Id, Allocation);

    internal NativeBumpSegment? BumpSegment =>
        Allocation.BumpSegment;

    internal NativeBumpSegment? CreatedSegment { get; }

    internal nuint OriginalCursor { get; }

    internal bool CursorCaptured { get; }

    internal bool Scoped { get; }

    internal bool StartedScope { get; }
}

internal readonly record struct NativeBumpBatchRequest(
    int Length,
    nuint ByteLength,
    nuint Alignment,
    bool ContainsReferences);

[InlineArray(8)]
internal struct NativeBumpBatchRequestBuffer
{
    private NativeBumpBatchRequest _element0;
}

[InlineArray(8)]
internal struct NativeBumpCursorBuffer
{
    private nuint _element0;
}

[InlineArray(8)]
internal struct NativeBumpInitializationBuffer
{
    private NativeBumpInitialization _element0;
}

[InlineArray(4)]
internal struct NativeBumpInitializationGroupBuffer
{
    private NativeBumpInitialization _element0;
}

internal readonly record struct NativeHandleMetadata(int Length, int Capacity);

internal readonly record struct NativeMemoryTestMetrics(
    long AllocationCount,
    long ZeroedAllocationCount,
    long FreeCount,
    long DetachedGenerationCount,
    long OutstandingNativeBytes,
    long DetachedNativeBytes,
    long RetiredNativeBytes,
    long BumpTraversalVisitCount,
    long ReusedNativeSegmentCount,
    long ReclaimedRangeReuseCount = 0,
    long ReclaimedRangeReuseBytes = 0,
    long StorageClearCount = 0,
    long StorageClearBytes = 0,
    long WrittenClearBytes = 0)
{
    internal long RetainedNativeBytes => OutstandingNativeBytes - DetachedNativeBytes;
}

/// <summary>Reports physical native storage observed by the NAM runtime.</summary>
public readonly record struct NativeMemoryStatistics(
    long OutstandingNativeBytes,
    long PeakOutstandingNativeBytes,
    long DetachedNativeBytes,
    long RetiredNativeBytes,
    long ReusedNativeSegmentCount,
    long ReclaimedRangeReuseCount = 0,
    long ReclaimedRangeReuseBytes = 0)
{
    /// <summary>Gets storage still owned by active or retained allocator generations.</summary>
    public long RetainedNativeBytes => OutstandingNativeBytes - DetachedNativeBytes;
}

/// <summary>Reports the current physical and logical state of one native owner.</summary>
/// <remarks>
/// Requested bytes describe live logical lease demand. Retained bytes describe
/// physical segments still held by the owner, including idle reusable capacity.
/// Growth slack is therefore derived only while a request is live; retained idle
/// capacity must not be interpreted as geometric growth slack.
/// </remarks>
public readonly record struct NativeOwnerStatistics(
    NativeOwnerLifecycle Lifecycle,
    long Generation,
    long RequestedBytes,
    long RetainedBytes,
    long RetiredBytes,
    int SegmentCount,
    int AvailableSegmentCount,
    int RetiredSegmentCount,
    long TrimmedBytes,
    long TrimCallCount,
    long FreshSegmentAllocationCount);

internal readonly record struct NativeOwnerDiagnosticSnapshot(
    NativeOwnerLifecycle Lifecycle,
    long Generation,
    long ScopeEpoch,
    long MetricsEpoch,
    int ActiveRecords,
    int ScopedRecords,
    int ReferenceRoots,
    int OrdinaryTraversalIndex,
    int ScopedTraversalIndex,
    int RetainedSegmentCount,
    int AvailableSegmentCount,
    int RetiredGenerationCount,
    int RetiredSegmentCount,
    long RetiredBytes,
    int QuarantinedGenerationCount,
    int QuarantinedSegmentCount,
    bool CurrentGenerationQuarantined);

/// <summary>Provides process-local physical storage counters for measurement and diagnostics.</summary>
public static class NativeMemoryDiagnostics
{
    /// <summary>Reads the current physical native storage counters.</summary>
    public static NativeMemoryStatistics Snapshot() => NativeMemoryTestHooks.SnapshotPublic();
}

internal static class NativeMemoryTestHooks
{
    private sealed class NativeHotMetrics
    {
        internal long Epoch = long.MinValue;
        internal long BumpTraversalVisitCount;
        internal long ReusedNativeSegmentCount;
        internal long ReclaimedRangeReuseCount;
        internal long ReclaimedRangeReuseBytes;
        internal long StorageClearCount;
        internal long StorageClearBytes;
        internal long WrittenClearBytes;

        internal void Reset(long epoch)
        {
            BumpTraversalVisitCount = 0;
            ReusedNativeSegmentCount = 0;
            ReclaimedRangeReuseCount = 0;
            ReclaimedRangeReuseBytes = 0;
            StorageClearCount = 0;
            StorageClearBytes = 0;
            WrittenClearBytes = 0;
            Volatile.Write(ref Epoch, epoch);
        }
    }

    private readonly record struct NativeHotMetricsSnapshot(
        long BumpTraversalVisitCount,
        long ReusedNativeSegmentCount,
        long ReclaimedRangeReuseCount,
        long ReclaimedRangeReuseBytes,
        long StorageClearCount,
        long StorageClearBytes,
        long WrittenClearBytes);

    private static readonly ConcurrentBag<NativeHotMetrics> HotMetrics = [];

    [ThreadStatic]
    private static NativeHotMetrics? _threadHotMetrics;

    private static long _allocationCount;
    private static long _zeroedAllocationCount;
    private static long _freeCount;
    private static long _detachedGenerationCount;
    private static long _outstandingNativeBytes;
    private static long _peakOutstandingNativeBytes;
    private static long _detachedNativeBytes;
    private static long _retiredNativeBytes;
    private static long _metricsEpoch;
    private static int _forcedFailures;
    private static int _forcedClearFailures;
    private static int _forcedCommitBoundary;
    private static int _commitBoundary;
    private static int _forcedPostCommitBoundary;
    private static int _postCommitBoundary;
    private static int _forcedRetiredSnapshotPreparation;
    private static int _forcedQuarantineReservation;
    private static Action<string>? _operationEntered;
    private static Action<string>? _beforeOperationEntry;
    private static Action<string, NativeOwnerKernel>? _beforeOperationEntryWithKernel;
    private static Action<string, NativeOwnerKernel, long, long>? _operationEnteredWithAllocation;
    private static Action<string, NativeGenerationOwner>? _operationEnteredWithGenerationOwner;
    private static int _operationHooksEnabled;

    internal static void Reset()
    {
        Interlocked.Increment(ref _metricsEpoch);
        Interlocked.Exchange(ref _allocationCount, 0);
        Interlocked.Exchange(ref _zeroedAllocationCount, 0);
        Interlocked.Exchange(ref _freeCount, 0);
        Interlocked.Exchange(ref _detachedGenerationCount, 0);
        Interlocked.Exchange(ref _outstandingNativeBytes, 0);
        Interlocked.Exchange(ref _peakOutstandingNativeBytes, 0);
        Interlocked.Exchange(ref _detachedNativeBytes, 0);
        Interlocked.Exchange(ref _retiredNativeBytes, 0);
        Interlocked.Exchange(ref _forcedFailures, 0);
        Interlocked.Exchange(ref _forcedClearFailures, 0);
        Interlocked.Exchange(ref _forcedCommitBoundary, 0);
        Interlocked.Exchange(ref _commitBoundary, 0);
        Interlocked.Exchange(ref _forcedPostCommitBoundary, 0);
        Interlocked.Exchange(ref _postCommitBoundary, 0);
        Interlocked.Exchange(ref _forcedRetiredSnapshotPreparation, 0);
        Interlocked.Exchange(ref _forcedQuarantineReservation, 0);
        Volatile.Write(ref _operationEntered, null);
        Volatile.Write(ref _beforeOperationEntry, null);
        Volatile.Write(ref _beforeOperationEntryWithKernel, null);
        Volatile.Write(ref _operationEnteredWithAllocation, null);
        Volatile.Write(ref _operationEnteredWithGenerationOwner, null);
        Volatile.Write(ref _operationHooksEnabled, 0);
    }

    internal static long CurrentMetricsEpoch => Volatile.Read(ref _metricsEpoch);

    internal static void FailNextAllocation() => Interlocked.Increment(ref _forcedFailures);

    internal static bool ConsumeForcedFailure()
    {
        while (true)
        {
            int current = Volatile.Read(ref _forcedFailures);
            if (current == 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _forcedFailures, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    internal static void FailNextClear() => Interlocked.Increment(ref _forcedClearFailures);

    internal static bool ConsumeForcedClearFailure()
    {
        while (true)
        {
            int current = Volatile.Read(ref _forcedClearFailures);
            if (current == 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _forcedClearFailures, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    internal static void FailAtCommitBoundary(int boundary)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boundary);
        Volatile.Write(ref _forcedCommitBoundary, boundary);
    }

    internal static void FailAfterCommitBoundary(int boundary)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boundary);
        Volatile.Write(ref _forcedPostCommitBoundary, boundary);
    }

    internal static void FailNextRetiredSnapshotPreparation() =>
        Interlocked.Increment(ref _forcedRetiredSnapshotPreparation);

    internal static bool ConsumeRetiredSnapshotPreparationFailure() =>
        ConsumeCounter(ref _forcedRetiredSnapshotPreparation);

    internal static void FailNextQuarantineReservation() =>
        Interlocked.Increment(ref _forcedQuarantineReservation);

    internal static bool ConsumeQuarantineReservationFailure() =>
        ConsumeCounter(ref _forcedQuarantineReservation);

    private static bool ConsumeCounter(ref int counter)
    {
        while (true)
        {
            int current = Volatile.Read(ref counter);
            if (current == 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref counter, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    internal static void BeginCommitSequence() => Volatile.Write(ref _commitBoundary, 0);

    internal static void BeginPostCommitSequence() => Volatile.Write(ref _postCommitBoundary, 0);

    internal static void CheckCommitBoundary(string operation, string boundary)
    {
        if (Volatile.Read(ref _forcedCommitBoundary) == 0)
        {
            return;
        }

        int ordinal = Interlocked.Increment(ref _commitBoundary);
        if (Volatile.Read(ref _forcedCommitBoundary) == ordinal
            && Interlocked.CompareExchange(ref _forcedCommitBoundary, 0, ordinal) == ordinal)
        {
            throw new InvalidOperationException($"Injected pre-publication failure during {operation} at {boundary} commit boundary {ordinal}.");
        }
    }

    internal static void CheckPostCommitBoundary(string operation, string boundary)
    {
        if (Volatile.Read(ref _forcedPostCommitBoundary) == 0)
        {
            return;
        }

        int ordinal = Interlocked.Increment(ref _postCommitBoundary);
        if (Volatile.Read(ref _forcedPostCommitBoundary) == ordinal
            && Interlocked.CompareExchange(ref _forcedPostCommitBoundary, 0, ordinal) == ordinal)
        {
            throw new InvalidOperationException($"Injected post-publication cleanup failure during {operation} at {boundary} commit boundary {ordinal}.");
        }
    }

    internal static long RecordAllocation(nuint byteLength, bool zeroed)
    {
        long metricsEpoch = CurrentMetricsEpoch;
        Interlocked.Increment(ref _allocationCount);
        long current = Interlocked.Add(ref _outstandingNativeBytes, checked((long)byteLength));
        while (true)
        {
            long peak = Volatile.Read(ref _peakOutstandingNativeBytes);
            if (current <= peak || Interlocked.CompareExchange(ref _peakOutstandingNativeBytes, current, peak) == peak)
            {
                break;
            }
        }
        if (zeroed)
        {
            Interlocked.Increment(ref _zeroedAllocationCount);
        }

        return metricsEpoch;
    }

    internal static void RecordFree(nuint byteLength, bool detached, long metricsEpoch)
    {
        if (metricsEpoch != CurrentMetricsEpoch)
        {
            return;
        }

        Interlocked.Increment(ref _freeCount);
        long bytes = checked((long)byteLength);
        Interlocked.Add(ref _outstandingNativeBytes, -bytes);
        if (detached)
        {
            Interlocked.Add(ref _detachedNativeBytes, -bytes);
        }
    }

    internal static void RecordDetachedGeneration(long metricsEpoch)
    {
        if (metricsEpoch == CurrentMetricsEpoch)
        {
            Interlocked.Increment(ref _detachedGenerationCount);
        }
    }

    internal static void RecordDetachedBytes(nuint byteLength, long metricsEpoch)
    {
        if (metricsEpoch == CurrentMetricsEpoch)
        {
            Interlocked.Add(ref _detachedNativeBytes, checked((long)byteLength));
        }
    }

    internal static void RecordRetiredBytes(nuint byteLength, bool add, long metricsEpoch)
    {
        if (metricsEpoch != CurrentMetricsEpoch)
        {
            return;
        }

        long bytes = checked((long)byteLength);
        Interlocked.Add(ref _retiredNativeBytes, add ? bytes : -bytes);
    }

    internal static NativeMemoryTestMetrics Snapshot()
    {
        NativeHotMetricsSnapshot hot = SnapshotHotMetrics();
        return new NativeMemoryTestMetrics(
            Volatile.Read(ref _allocationCount),
            Volatile.Read(ref _zeroedAllocationCount),
            Volatile.Read(ref _freeCount),
            Volatile.Read(ref _detachedGenerationCount),
            Volatile.Read(ref _outstandingNativeBytes),
            Volatile.Read(ref _detachedNativeBytes),
            Volatile.Read(ref _retiredNativeBytes),
            hot.BumpTraversalVisitCount,
            hot.ReusedNativeSegmentCount,
            hot.ReclaimedRangeReuseCount,
            hot.ReclaimedRangeReuseBytes,
            hot.StorageClearCount,
            hot.StorageClearBytes,
            hot.WrittenClearBytes);
    }

    internal static NativeMemoryStatistics SnapshotPublic()
    {
        NativeHotMetricsSnapshot hot = SnapshotHotMetrics();
        return new NativeMemoryStatistics(
            Volatile.Read(ref _outstandingNativeBytes),
            Volatile.Read(ref _peakOutstandingNativeBytes),
            Volatile.Read(ref _detachedNativeBytes),
            Volatile.Read(ref _retiredNativeBytes),
            hot.ReusedNativeSegmentCount,
            hot.ReclaimedRangeReuseCount,
            hot.ReclaimedRangeReuseBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RecordBumpTraversalVisit()
    {
        CurrentHotMetrics().BumpTraversalVisitCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RecordReusedNativeSegment()
    {
        CurrentHotMetrics().ReusedNativeSegmentCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RecordReclaimedRangeReuse(nuint byteLength)
    {
        RecordReclaimedRangeReuse(1, byteLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RecordReclaimedRangeReuse(
        int rangeCount,
        nuint byteLength)
    {
        NativeHotMetrics metrics = CurrentHotMetrics();
        metrics.ReclaimedRangeReuseCount = checked(
            metrics.ReclaimedRangeReuseCount
            + rangeCount);
        metrics.ReclaimedRangeReuseBytes = checked(
            metrics.ReclaimedRangeReuseBytes
            + (long)byteLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RecordStorageClear(
        nuint byteLength,
        nuint writtenBytes)
    {
        NativeHotMetrics metrics = CurrentHotMetrics();
        metrics.StorageClearCount++;
        metrics.StorageClearBytes = checked(
            metrics.StorageClearBytes
            + (long)byteLength);
        metrics.WrittenClearBytes = checked(
            metrics.WrittenClearBytes
            + (long)writtenBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NativeHotMetrics CurrentHotMetrics()
    {
        long epoch = CurrentMetricsEpoch;
        NativeHotMetrics? metrics = _threadHotMetrics;
        if (metrics is null)
        {
            metrics = new NativeHotMetrics();
            HotMetrics.Add(metrics);
            _threadHotMetrics = metrics;
        }

        if (Volatile.Read(ref metrics.Epoch) != epoch)
        {
            metrics.Reset(epoch);
        }

        return metrics;
    }

    private static NativeHotMetricsSnapshot SnapshotHotMetrics()
    {
        long epoch = CurrentMetricsEpoch;
        long bumpTraversalVisitCount = 0;
        long reusedNativeSegmentCount = 0;
        long reclaimedRangeReuseCount = 0;
        long reclaimedRangeReuseBytes = 0;
        long storageClearCount = 0;
        long storageClearBytes = 0;
        long writtenClearBytes = 0;
        foreach (NativeHotMetrics metrics in HotMetrics)
        {
            if (Volatile.Read(ref metrics.Epoch) != epoch)
            {
                continue;
            }

            bumpTraversalVisitCount = checked(
                bumpTraversalVisitCount
                + Volatile.Read(
                    ref metrics.BumpTraversalVisitCount));
            reusedNativeSegmentCount = checked(
                reusedNativeSegmentCount
                + Volatile.Read(
                    ref metrics.ReusedNativeSegmentCount));
            reclaimedRangeReuseCount = checked(
                reclaimedRangeReuseCount
                + Volatile.Read(
                    ref metrics.ReclaimedRangeReuseCount));
            reclaimedRangeReuseBytes = checked(
                reclaimedRangeReuseBytes
                + Volatile.Read(
                    ref metrics.ReclaimedRangeReuseBytes));
            storageClearCount = checked(
                storageClearCount
                + Volatile.Read(ref metrics.StorageClearCount));
            storageClearBytes = checked(
                storageClearBytes
                + Volatile.Read(ref metrics.StorageClearBytes));
            writtenClearBytes = checked(
                writtenClearBytes
                + Volatile.Read(ref metrics.WrittenClearBytes));
        }

        return new NativeHotMetricsSnapshot(
            bumpTraversalVisitCount,
            reusedNativeSegmentCount,
            reclaimedRangeReuseCount,
            reclaimedRangeReuseBytes,
            storageClearCount,
            storageClearBytes,
            writtenClearBytes);
    }

    internal static bool HasForcedCommitBoundary => Volatile.Read(ref _forcedCommitBoundary) != 0;

    internal static bool OperationHooksEnabled =>
        Volatile.Read(ref _operationHooksEnabled) != 0;

    internal static void SetOperationEntered(Action<string>? callback)
    {
        Volatile.Write(ref _operationEntered, callback);
        RefreshOperationHooksEnabled();
    }

    internal static void SetBeforeOperationEntry(Action<string>? callback)
    {
        Volatile.Write(ref _beforeOperationEntry, callback);
        RefreshOperationHooksEnabled();
    }

    internal static void SetBeforeOperationEntryWithKernel(Action<string, NativeOwnerKernel>? callback)
    {
        Volatile.Write(ref _beforeOperationEntryWithKernel, callback);
        RefreshOperationHooksEnabled();
    }

    internal static void SetOperationEnteredWithAllocation(Action<string, NativeOwnerKernel, long, long>? callback)
    {
        Volatile.Write(ref _operationEnteredWithAllocation, callback);
        RefreshOperationHooksEnabled();
    }

    internal static void SetOperationEnteredWithGenerationOwner(Action<string, NativeGenerationOwner>? callback)
    {
        Volatile.Write(ref _operationEnteredWithGenerationOwner, callback);
        RefreshOperationHooksEnabled();
    }

    internal static void NotifyBeforeOperationEntry(string operation, NativeOwnerKernel kernel)
    {
        if (Volatile.Read(ref _operationHooksEnabled) == 0)
        {
            return;
        }

        Volatile.Read(ref _beforeOperationEntry)?.Invoke(operation);
        Volatile.Read(ref _beforeOperationEntryWithKernel)?.Invoke(operation, kernel);
    }

    internal static void NotifyOperationEntered(
        string operation,
        NativeOwnerKernel kernel,
        NativeGenerationOwner generationOwner,
        long generation,
        long allocationId)
    {
        if (Volatile.Read(ref _operationHooksEnabled) == 0)
        {
            return;
        }

        Volatile.Read(ref _operationEntered)?.Invoke(operation);
        Volatile.Read(ref _operationEnteredWithAllocation)?.Invoke(operation, kernel, generation, allocationId);
        Volatile.Read(ref _operationEnteredWithGenerationOwner)?.Invoke(operation, generationOwner);
    }

    private static void RefreshOperationHooksEnabled()
    {
        bool enabled = Volatile.Read(ref _operationEntered) is not null
            || Volatile.Read(ref _beforeOperationEntry) is not null
            || Volatile.Read(ref _beforeOperationEntryWithKernel) is not null
            || Volatile.Read(ref _operationEnteredWithAllocation) is not null
            || Volatile.Read(ref _operationEnteredWithGenerationOwner) is not null;
        Volatile.Write(ref _operationHooksEnabled, enabled ? 1 : 0);
    }

}

internal static class NativeStorageClear
{
    internal static void Clear(IntPtr pointer, nuint byteLength)
    {
        if (byteLength == 0)
        {
            return;
        }

        unsafe
        {
            NativeMemory.Clear((void*)pointer, byteLength);
        }

        NativeMemoryTestHooks.RecordStorageClear(byteLength, byteLength);
    }
}

internal sealed class NativeSegment
{
    internal const nuint Alignment = 64;
    private IntPtr _pointer;
    private SafeBuffer? _externalBuffer;
    private int _detached;
    private readonly long _metricsEpoch;
    private readonly bool _ownsNativeMemory;

    private NativeSegment(
        IntPtr pointer,
        nuint byteLength,
        long metricsEpoch,
        bool ownsNativeMemory,
        SafeBuffer? externalBuffer = null)
    {
        _pointer = pointer;
        ByteLength = byteLength;
        _metricsEpoch = metricsEpoch;
        _ownsNativeMemory = ownsNativeMemory;
        _externalBuffer = externalBuffer;
    }

    internal nuint ByteLength { get; }

    internal long MetricsEpoch => _metricsEpoch;

    internal IntPtr Pointer => Volatile.Read(ref _pointer);

    internal bool MarkDetached()
    {
        if (!_ownsNativeMemory)
        {
            return false;
        }

        Volatile.Write(ref _detached, 1);
        return true;
    }

    internal static NativeSegment Allocate(
        nuint byteLength,
        string ownerKind,
        long generation,
        string operation,
        NativeOwnerLifecycle currentLifecycle,
        bool zeroed)
    {
        if (byteLength == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength), "A native segment must contain at least one byte.");
        }

        if (NativeMemoryTestHooks.ConsumeForcedFailure())
        {
            throw new NativeAllocationFailedException(byteLength, ownerKind, generation, operation, currentLifecycle);
        }

        try
        {
            unsafe
            {
                IntPtr pointer = (IntPtr)NativeMemory.AlignedAlloc(
                    byteLength,
                    Alignment);
                if (pointer == IntPtr.Zero)
                {
                    throw new NativeAllocationFailedException(byteLength, ownerKind, generation, operation, currentLifecycle);
                }

                if (zeroed)
                {
                    NativeMemory.Clear(
                        (void*)pointer,
                        byteLength);
                }

                return new NativeSegment(
                    pointer,
                    byteLength,
                    NativeMemoryTestHooks.RecordAllocation(
                        byteLength,
                        zeroed),
                    ownsNativeMemory: true);
            }
        }
        catch (OutOfMemoryException exception)
        {
            throw new NativeAllocationFailedException(byteLength, ownerKind, generation, operation, currentLifecycle, exception);
        }
    }

    internal static NativeSegment Borrow(
        SafeBuffer buffer,
        nuint byteOffset,
        nuint byteLength)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfZero(byteLength);

        bool referenceAdded = false;
        try
        {
            buffer.DangerousAddRef(ref referenceAdded);
            ulong availableBytes = buffer.ByteLength;
            ulong requestedOffset = checked((ulong)byteOffset);
            ulong requestedLength = checked((ulong)byteLength);
            if (requestedOffset > availableBytes
                || requestedLength > availableBytes - requestedOffset)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(byteLength),
                    "The external memory range exceeds the safe buffer.");
            }

            nint baseAddress = buffer.DangerousGetHandle();
            if (baseAddress == 0 || baseAddress == -1)
            {
                throw new ArgumentException(
                    "The external memory buffer has an invalid address.",
                    nameof(buffer));
            }

            nint address = checked(
                baseAddress + checked((nint)byteOffset));
            if (unchecked((nuint)address) % Alignment != 0)
            {
                throw new ArgumentException(
                    $"The external memory address must use {Alignment}-byte alignment.",
                    nameof(byteOffset));
            }

            return new NativeSegment(
                address,
                byteLength,
                metricsEpoch: 0,
                ownsNativeMemory: false,
                externalBuffer: buffer);
        }
        catch
        {
            if (referenceAdded)
            {
                buffer.DangerousRelease();
            }

            throw;
        }
    }

    internal void FreeNow()
    {
        IntPtr pointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        SafeBuffer? externalBuffer =
            Interlocked.Exchange(ref _externalBuffer, null);
        if (externalBuffer is not null)
        {
            externalBuffer.DangerousRelease();
            return;
        }

        unsafe
        {
            NativeMemory.AlignedFree((void*)pointer);
        }

        NativeMemoryTestHooks.RecordFree(ByteLength, Volatile.Read(ref _detached) != 0, _metricsEpoch);
    }

    ~NativeSegment()
    {
        try
        {
            FreeNow();
        }
        catch
        {
        }
    }
}

internal sealed class NativeSlab
{
    internal NativeSlab(NativeSegment segment, int capacity, bool containsReferences, long allocationOrdinal)
    {
        Segment = segment;
        Capacity = capacity;
        ContainsReferences = containsReferences;
        AllocationOrdinal = allocationOrdinal;
    }

    internal NativeSegment Segment { get; }

    internal int Capacity { get; }

    internal bool ContainsReferences { get; }

    internal long AllocationOrdinal { get; }

    internal bool HasBeenUsed { get; set; }
}

/// <summary>
/// Owns the managed roots for native pointer-sized handle slots. The native segment
/// is the slot storage; this table is only the GC root side of that representation.
/// </summary>
internal sealed class NativeReferenceRootTable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, object?> _roots = [];
    private readonly Stack<long> _availableIds = [];
    private long _nextId;

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _roots.Count;
            }
        }
    }

    internal void ReserveForClear(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotCount);
        if (slotCount == 0)
        {
            return;
        }

        lock (_gate)
        {
            _availableIds.EnsureCapacity(checked(_availableIds.Count + slotCount));
        }
    }

    internal void Set<T>(NativeSegment segment, nuint offsetBytes, T value)
    {
        lock (_gate)
        {
            nuint previousRawId = ReadSlot(segment, offsetBytes);
            long previousId = previousRawId == 0 ? 0 : checked((long)previousRawId);
            _availableIds.EnsureCapacity(checked(_availableIds.Count + (previousId == 0 ? 0 : 1)));
            _roots.EnsureCapacity(checked(_roots.Count + 1));

            bool reusedId = _availableIds.Count != 0;
            long id = reusedId
                ? _availableIds.Peek()
                : checked(_nextId + 1);
            _roots.Add(id, value);
            try
            {
                WriteSlot(segment, offsetBytes, checked((nuint)id));
            }
            catch
            {
                _roots.Remove(id);
                throw;
            }

            if (reusedId)
            {
                _availableIds.Pop();
            }
            else
            {
                _nextId = id;
            }

            if (previousId != 0 && _roots.Remove(previousId))
            {
                _availableIds.Push(previousId);
            }
        }
    }

    internal void SetInitial<T>(
        NativeSegment segment,
        nuint offsetBytes,
        T value)
    {
        lock (_gate)
        {
            if (value is null)
            {
                WriteSlot(segment, offsetBytes, 0);
                return;
            }

            _roots.EnsureCapacity(checked(_roots.Count + 1));
            bool reusedId = _availableIds.Count != 0;
            long id = reusedId
                ? _availableIds.Peek()
                : checked(_nextId + 1);
            _roots.Add(id, value);
            try
            {
                WriteSlot(segment, offsetBytes, checked((nuint)id));
            }
            catch
            {
                _roots.Remove(id);
                throw;
            }

            if (reusedId)
            {
                _availableIds.Pop();
            }
            else
            {
                _nextId = id;
            }
        }
    }

    internal T Get<T>(NativeSegment segment, nuint offsetBytes)
    {
        lock (_gate)
        {
            nuint rawId = ReadSlot(segment, offsetBytes);
            if (rawId == 0)
            {
                return default!;
            }

            long id = checked((long)rawId);
            if (!_roots.TryGetValue(id, out object? value))
            {
                throw new InvalidOperationException("The native reference slot has no owner-held root.");
            }

            return value is null ? default! : (T)value;
        }
    }

    internal void Clear(NativeSegment segment, nuint offsetBytes)
    {
        lock (_gate)
        {
            _availableIds.EnsureCapacity(checked(_availableIds.Count + 1));
            ClearLocked(segment, offsetBytes);
        }
    }

    internal void ClearRange(NativeSegment segment, nuint offsetBytes, int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotCount);
        ValidateSlotRange(offsetBytes, slotCount);
        lock (_gate)
        {
            _availableIds.EnsureCapacity(checked(_availableIds.Count + slotCount));
            for (int index = 0; index < slotCount; index++)
            {
                ClearLocked(segment, ComputeSlotOffset(offsetBytes, index, slotCount));
            }
        }
    }

    internal void ClearRangeReserved(NativeSegment segment, nuint offsetBytes, int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotCount);
        ValidateSlotRange(offsetBytes, slotCount);
        lock (_gate)
        {
            for (int index = 0; index < slotCount; index++)
            {
                ClearLocked(segment, ComputeSlotOffset(offsetBytes, index, slotCount));
            }
        }
    }

    internal static nuint ComputeSlotOffsetForTest(nuint offsetBytes, int index, int slotCount) =>
        ComputeSlotOffset(offsetBytes, index, slotCount);

    private static void ValidateSlotRange(nuint offsetBytes, int slotCount)
    {
        if (slotCount == 0)
        {
            return;
        }

        _ = ComputeSlotOffset(offsetBytes, checked(slotCount - 1), slotCount);
    }

    internal static nuint ComputeSlotOffset(nuint offsetBytes, int index, int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        if (index >= slotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        nuint stride = (nuint)IntPtr.Size;
        nuint totalBytes = checked((nuint)slotCount * stride);
        _ = checked(offsetBytes + totalBytes - stride);
        return checked(offsetBytes + checked((nuint)index * stride));
    }

    private void ClearLocked(NativeSegment segment, nuint offsetBytes)
    {
        nuint rawId = ReadSlot(segment, offsetBytes);
        if (rawId == 0)
        {
            return;
        }

        WriteSlot(segment, offsetBytes, 0);
        long id = checked((long)rawId);
        if (_roots.Remove(id))
        {
            _availableIds.Push(id);
        }
    }

    private static nuint ReadSlot(NativeSegment segment, nuint offsetBytes)
    {
        unsafe
        {
            byte* pointer = (byte*)segment.Pointer + checked((nint)offsetBytes);
            return Unsafe.Read<nuint>(pointer);
        }
    }

    private static void WriteSlot(NativeSegment segment, nuint offsetBytes, nuint value)
    {
        unsafe
        {
            byte* pointer = (byte*)segment.Pointer + checked((nint)offsetBytes);
            Unsafe.Write(pointer, value);
        }
    }
}

internal sealed class NativeBumpSegment
{
    internal NativeBumpSegment(NativeSegment segment, long allocationOrdinal)
    {
        Segment = segment;
        HighCursor = segment.ByteLength;
        AllocationOrdinal = allocationOrdinal;
    }

    internal NativeSegment Segment { get; }

    internal nuint LowCursor { get; set; }

    internal nuint HighCursor { get; set; }

    internal long AllocationOrdinal { get; }

    internal long ReclaimedScopeEpoch { get; set; }

    internal nuint ReclaimedRangeStart { get; set; }

    internal nuint ReclaimedRangeEnd { get; set; }

    internal long PendingScopeEpoch { get; set; } = -1;

    internal nuint PendingScopeRangeStart { get; set; }

    internal nuint PendingScopeRangeEnd { get; set; }

    internal bool IsCompletelyIdle => LowCursor == 0 && HighCursor == Segment.ByteLength;

    internal void BeginPendingScopeRange(long scopeEpoch, nuint start, nuint end)
    {
        PendingScopeEpoch = scopeEpoch;
        PendingScopeRangeStart = start;
        PendingScopeRangeEnd = end;
    }

    internal void ExtendPendingScopeRange(nuint start)
    {
        if (start < PendingScopeRangeStart)
        {
            PendingScopeRangeStart = start;
        }
    }

    internal void ClearPendingScopeRange()
    {
        PendingScopeEpoch = -1;
        PendingScopeRangeStart = 0;
        PendingScopeRangeEnd = 0;
    }

    internal void MarkReclaimedRange(long scopeEpoch, nuint start, nuint end)
    {
        if (end <= start)
        {
            return;
        }

        ReclaimedScopeEpoch = scopeEpoch;
        ReclaimedRangeStart = start;
        ReclaimedRangeEnd = end;
    }

    internal nuint ReclaimedOverlap(nuint offset, nuint length, long scopeEpoch)
    {
        if (scopeEpoch != ReclaimedScopeEpoch || length == 0)
        {
            return 0;
        }

        nuint end = checked(offset + length);
        nuint start = offset > ReclaimedRangeStart ? offset : ReclaimedRangeStart;
        nuint overlapEnd = end < ReclaimedRangeEnd ? end : ReclaimedRangeEnd;
        return overlapEnd > start ? overlapEnd - start : 0;
    }
}

internal sealed class NativeAllocation
{
    private long _id;
    private long _scopeEpoch;
    private int _lifecycle;

    internal NativeAllocation(
        NativeGeneration generationState,
        long id,
        NativeSlab? slab,
        NativeBumpSegment? bumpSegment,
        nuint offsetBytes,
        int length,
        int capacity,
        nuint storageBytes,
        NativeReferenceRootTable? referenceRoots,
        bool scoped,
        long scopeEpoch)
    {
        GenerationState = generationState;
        Reset(id, slab, bumpSegment, offsetBytes, length, capacity, storageBytes, referenceRoots, scoped, scopeEpoch);
    }

    internal NativeGeneration GenerationState { get; }

    internal long Id => Volatile.Read(ref _id);

    internal NativeSlab? Slab { get; private set; }

    internal NativeBumpSegment? BumpSegment { get; private set; }

    internal NativeSegment? Segment => Slab?.Segment ?? BumpSegment?.Segment;

    internal nuint OffsetBytes { get; private set; }

    internal int Length { get; private set; }

    internal int Capacity { get; private set; }

    internal nuint StorageBytes { get; private set; }

    internal NativeReferenceRootTable? ReferenceRoots { get; private set; }

    internal bool IsScoped { get; private set; }

    internal long ScopeEpoch => Volatile.Read(ref _scopeEpoch);

    internal void SetScopeEpochForTest(long value) =>
        Volatile.Write(ref _scopeEpoch, value);

    internal NativeAllocationLifecycle Lifecycle
    {
        get => (NativeAllocationLifecycle)Volatile.Read(ref _lifecycle);
        set => Volatile.Write(ref _lifecycle, (int)value);
    }

    private int _operationAdmission;

    internal int ActiveOperations =>
        NativeOperationAdmission.Count(ref _operationAdmission);

    internal int InitializedLength { get; set; }

    internal void Reset(
        long id,
        NativeSlab? slab,
        NativeBumpSegment? bumpSegment,
        nuint offsetBytes,
        int length,
        int capacity,
        nuint storageBytes,
        NativeReferenceRootTable? referenceRoots,
        bool scoped,
        long scopeEpoch)
    {
        Volatile.Write(ref _id, 0);
        Slab = slab;
        BumpSegment = bumpSegment;
        OffsetBytes = offsetBytes;
        Length = length;
        Capacity = capacity;
        StorageBytes = storageBytes;
        ReferenceRoots = referenceRoots;
        IsScoped = scoped;
        Volatile.Write(ref _scopeEpoch, scopeEpoch);
        Lifecycle = NativeAllocationLifecycle.Active;
        NativeOperationAdmission.Reset(ref _operationAdmission);
        InitializedLength = 0;
        Volatile.Write(ref _id, id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryEnterOperation() =>
        NativeOperationAdmission.TryEnter(ref _operationAdmission);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ExitOperation() =>
        NativeOperationAdmission.Exit(ref _operationAdmission);

    internal int CloseOperationAdmission() =>
        NativeOperationAdmission.Close(ref _operationAdmission);

    internal void OpenOperationAdmission() =>
        NativeOperationAdmission.Open(ref _operationAdmission);

    internal T GetValue<T>(int index)
    {
        if (ReferenceRoots is not null)
        {
            return ReferenceRoots.Get<T>(
                Segment ?? throw new InvalidOperationException("A reference allocation has no native segment."),
                NativeReferenceRootTable.ComputeSlotOffset(
                    OffsetBytes,
                    index,
                    checked((int)(StorageBytes / (nuint)IntPtr.Size))));
        }

        if (Length == 0 || Segment is null)
        {
            return default!;
        }

        unsafe
        {
            byte* pointer = (byte*)Segment.Pointer + checked((nint)OffsetBytes);
            return Unsafe.Read<T>(pointer + checked(index * Unsafe.SizeOf<T>()));
        }
    }

    internal Span<T> AsSpan<T>()
    {
        if (NativeTypeLayout.ContainsReferences<T>())
        {
            throw new NotSupportedException("A direct span view is available only for value storage without managed references.");
        }

        if (Length == 0)
        {
            return Span<T>.Empty;
        }

        NativeSegment segment = Segment ?? throw new InvalidOperationException("The allocation has no native segment.");
        unsafe
        {
            ref T first = ref Unsafe.AsRef<T>((void*)((byte*)segment.Pointer + checked((nint)OffsetBytes)));
            return MemoryMarshal.CreateSpan(ref first, Length);
        }
    }

    internal void SetValue<T>(int index, T value)
    {
        if (ReferenceRoots is not null)
        {
            ReferenceRoots.Set(
                Segment ?? throw new InvalidOperationException("A reference allocation has no native segment."),
                NativeReferenceRootTable.ComputeSlotOffset(
                    OffsetBytes,
                    index,
                    checked((int)(StorageBytes / (nuint)IntPtr.Size))),
                value);
            return;
        }

        if (Length == 0 || Segment is null)
        {
            return;
        }

        unsafe
        {
            byte* pointer = (byte*)Segment.Pointer + checked((nint)OffsetBytes);
            Unsafe.Write(pointer + checked(index * Unsafe.SizeOf<T>()), value);
        }
    }

    internal void SetInitialValue<T>(int index, T value)
    {
        if (ReferenceRoots is not null)
        {
            ReferenceRoots.SetInitial(
                Segment ?? throw new InvalidOperationException("A reference allocation has no native segment."),
                NativeReferenceRootTable.ComputeSlotOffset(
                    OffsetBytes,
                    index,
                    checked((int)(StorageBytes / (nuint)IntPtr.Size))),
                value);
            return;
        }

        SetValue(index, value);
    }

    internal void ClearInitializedReferences()
    {
        if (ReferenceRoots is null
            || Segment is null
            || InitializedLength == 0)
        {
            return;
        }

        ReferenceRoots.ClearRange(
            Segment,
            OffsetBytes,
            InitializedLength);
    }

    internal void ClearValues(bool reservedRootCapacity = false)
    {
        if (ReferenceRoots is not null && Segment is not null && StorageBytes != 0)
        {
            int slotCount = checked((int)(StorageBytes / (nuint)IntPtr.Size));
            if (reservedRootCapacity)
            {
                ReferenceRoots.ClearRangeReserved(Segment, OffsetBytes, slotCount);
            }
            else
            {
                ReferenceRoots.ClearRange(Segment, OffsetBytes, slotCount);
            }

            return;
        }

        if (StorageBytes == 0 || Segment is null)
        {
            return;
        }

        unsafe
        {
            NativeStorageClear.Clear(
                (IntPtr)((byte*)Segment.Pointer + checked((nint)OffsetBytes)),
                StorageBytes);
        }
    }

    internal void Fill<T>(T value)
    {
        FillFrom(0, value);
    }

    internal void FillFrom<T>(int start, T value)
    {
        if (start == Length)
        {
            return;
        }

        if (ReferenceRoots is null)
        {
            AsSpan<T>()[start..].Fill(value);
            return;
        }

        for (int index = start; index < Length; index++)
        {
            SetValue(index, value);
        }
    }

    internal void CopyFrom<T>(ReadOnlySpan<T> source)
    {
        CopyFrom(0, source);
    }

    internal void CopyFrom<T>(
        int start,
        ReadOnlySpan<T> source)
    {
        if (source.IsEmpty)
        {
            return;
        }

        if (ReferenceRoots is null)
        {
            source.CopyTo(AsSpan<T>()[start..]);
            return;
        }

        for (int index = 0; index < source.Length; index++)
        {
            SetValue(start + index, source[index]);
        }
    }

    internal void CopyTo<T>(Span<T> destination)
    {
        for (int index = 0; index < Length; index++)
        {
            destination[index] = GetValue<T>(index);
        }
    }
}

internal sealed class NativeGenerationOwner
{
    private readonly object _gate = new();
    private List<NativeSegment>? _segments = [];
    private int _released;
    private int _detached;
    private readonly long _metricsEpoch;

    internal NativeGenerationOwner(long generation)
    {
        Generation = generation;
        _metricsEpoch = NativeMemoryTestHooks.CurrentMetricsEpoch;
    }

    internal long Generation { get; }

    internal long MetricsEpoch => _metricsEpoch;

    internal void AddSegment(NativeSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        lock (_gate)
        {
            if (Volatile.Read(ref _released) != 0 || _segments is null)
            {
                segment.FreeNow();
                throw new InvalidOperationException("The native generation owner has already been released.");
            }

            _segments.Add(segment);
        }
    }

    internal void PrepareAddSegmentCapacity(int additionalSegments)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalSegments);
        lock (_gate)
        {
            if (Volatile.Read(ref _released) != 0 || _segments is null)
            {
                throw new InvalidOperationException("The native generation owner has already been released.");
            }

            _segments.EnsureCapacity(checked(_segments.Count + additionalSegments));
        }
    }

    internal int SegmentListCapacityForTest()
    {
        lock (_gate)
        {
            return _segments?.Capacity ?? 0;
        }
    }

    internal void RemoveSegment(NativeSegment segment)
    {
        lock (_gate)
        {
            _segments?.Remove(segment);
        }
    }

    internal void Detach()
    {
        if (Volatile.Read(ref _released) != 0 || Interlocked.Exchange(ref _detached, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_segments is null)
            {
                return;
            }

            foreach (NativeSegment segment in _segments)
            {
                if (segment.MarkDetached())
                {
                    NativeMemoryTestHooks.RecordDetachedBytes(
                        segment.ByteLength,
                        segment.MetricsEpoch);
                }
            }
        }

        NativeMemoryTestHooks.RecordDetachedGeneration(_metricsEpoch);
    }

    internal void ReleaseToNative()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            List<NativeSegment>? segments = _segments;
            _segments = null;
            if (segments is not null)
            {
                foreach (NativeSegment segment in segments)
                {
                    segment.FreeNow();
                }
            }
        }

        GC.SuppressFinalize(this);
    }

    ~NativeGenerationOwner()
    {
        try
        {
            ReleaseToNative();
        }
        catch
        {
        }
    }
}

internal sealed class NativeGeneration
{
    private long _scopeEpoch;
    private int _memoryDetached;

    internal NativeGeneration(long number)
    {
        Number = number;
        Owner = new NativeGenerationOwner(number);
    }

    internal long Number { get; }

    internal NativeGenerationOwner Owner { get; }

    internal Dictionary<long, NativeAllocation> Allocations { get; } = new();

    // Ordinary leases reuse these records after an individual return.
    internal List<NativeAllocation> ReusableAllocations { get; } = [];

    internal NativeReferenceRootTable ReferenceRoots { get; } = new();

    internal List<NativeSlab> Slabs { get; } = [];

    internal List<NativeSlab> AvailableSlabs { get; } = [];

    internal List<NativeBumpSegment> BumpSegments { get; } = [];

    internal int OrdinaryBumpTraversalIndex { get; set; }

    internal int ScopedBumpTraversalIndex { get; set; } = -1;

    private int _operationAdmission;

    internal int ActiveOperations =>
        NativeOperationAdmission.Count(ref _operationAdmission);

    internal int LeaseReturnsInProgress { get; set; }

    internal int InitializationsInProgress { get; set; }

    internal long RetiredNativeBytes { get; set; }

    internal long ScopeEpoch
    {
        get => Volatile.Read(ref _scopeEpoch);
        set => Volatile.Write(ref _scopeEpoch, value);
    }

    // This bank keeps one record for each position in the largest scoped batch.
    internal List<NativeAllocation> ScopedAllocationBank { get; } = [];

    internal int ScopedRecordCount { get; set; }

    // Only reference roots and pool slabs need work during a scoped reset.
    internal List<NativeAllocation> ScopedCleanupPending { get; } = [];

    internal List<NativeBumpSegment> ScopedTouchedSegments { get; } = [];

    internal bool ScopedBaselineActive { get; set; }

    internal bool MemoryDetached
    {
        get => Volatile.Read(ref _memoryDetached) != 0;
        set => Volatile.Write(ref _memoryDetached, value ? 1 : 0);
    }

    // These arrays are prepared before the generation can become retired. Retired
    // cleanup must never allocate a snapshot while it is draining or quarantining.
    internal NativeSlab[] RetiredSlabs { get; set; } = [];

    internal NativeBumpSegment[] RetiredBumps { get; set; } = [];

    internal bool IsQuarantined { get; set; }

    internal int RetiredSegmentCount => checked(RetiredSlabs.Length + RetiredBumps.Length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryEnterOperation() =>
        NativeOperationAdmission.TryEnter(ref _operationAdmission);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ExitOperation() =>
        NativeOperationAdmission.Exit(ref _operationAdmission);

    internal int CloseOperationAdmission() =>
        NativeOperationAdmission.Close(ref _operationAdmission);

    internal void OpenOperationAdmission() =>
        NativeOperationAdmission.Open(ref _operationAdmission);

    internal void AddSlabOrdered(NativeSlab slab)
    {
        int index = 0;
        while (index < Slabs.Count && Slabs[index].AllocationOrdinal < slab.AllocationOrdinal)
        {
            index++;
        }

        Slabs.Insert(index, slab);
    }

    internal void AddAvailableSlabOrdered(NativeSlab slab)
    {
        int index = 0;
        while (index < AvailableSlabs.Count && AvailableSlabs[index].AllocationOrdinal < slab.AllocationOrdinal)
        {
            index++;
        }

        AvailableSlabs.Insert(index, slab);
    }

    internal int AddBumpOrdered(NativeBumpSegment segment)
    {
        int index = 0;
        while (index < BumpSegments.Count && BumpSegments[index].AllocationOrdinal < segment.AllocationOrdinal)
        {
            index++;
        }

        if (OrdinaryBumpTraversalIndex >= index)
        {
            OrdinaryBumpTraversalIndex++;
        }

        if (ScopedBumpTraversalIndex >= index)
        {
            ScopedBumpTraversalIndex++;
        }

        BumpSegments.Insert(index, segment);
        return index;
    }
}

internal ref struct NativeOperationToken
{
    private NativeOwnerKernel? _kernel;
    private readonly NativeGeneration _generationState;
    private readonly NativeGenerationOwner _generationOwner;
    private readonly NativeAllocation _allocation;
    private readonly bool _allocationEntered;
    private readonly string _operation;

    internal NativeOperationToken(
        NativeOwnerKernel kernel,
        NativeGeneration generation,
        NativeAllocation allocation,
        bool allocationEntered,
        string operation)
    {
        _kernel = kernel;
        _generationState = generation;
        _generationOwner = generation.Owner;
        _allocation = allocation;
        _allocationEntered = allocationEntered;
        _operation = operation;
    }

    internal NativeLeaseView<T> GetView<T>() => new(_allocation);

    internal T GetValue<T>(int index) => _allocation.GetValue<T>(index);

    internal void SetValue<T>(int index, T value) => _allocation.SetValue(index, value);

    public void Dispose()
    {
        NativeOwnerKernel? kernel = _kernel;
        if (kernel is null)
        {
            return;
        }

        _kernel = null;
        kernel.ExitOperation(
            _generationState,
            _allocation,
            _allocationEntered,
            _operation);
        GC.KeepAlive(_generationOwner);
    }
}

[InlineArray(8)]
internal struct NativeCompositeAllocationBuffer
{
    internal const int Capacity = 8;
    private NativeAllocation? _element0;
}

internal ref struct NativeCompositeOperationToken
{
    private NativeOwnerKernel? _kernel;
    private readonly NativeGeneration _generationState;
    private readonly NativeGenerationOwner _generationOwner;
    private NativeCompositeAllocationBuffer _allocations;
    private readonly int _count;
    private readonly bool _allocationsEntered;

    internal NativeCompositeOperationToken(
        NativeOwnerKernel kernel,
        NativeGeneration generation,
        NativeCompositeAllocationBuffer allocations,
        int count,
        bool allocationsEntered)
    {
        _kernel = kernel;
        _generationState = generation;
        _generationOwner = generation.Owner;
        _allocations = allocations;
        _count = count;
        _allocationsEntered = allocationsEntered;
    }

    internal NativeLeaseView<T> GetView<T>(int index)
    {
        if ((uint)index >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return new NativeLeaseView<T>(
            _allocations[index]
                ?? throw new InvalidOperationException("The composite native operation has no allocation at this index."));
    }

    public void Dispose()
    {
        NativeOwnerKernel? kernel = _kernel;
        if (kernel is null)
        {
            return;
        }

        _kernel = null;
        kernel.ExitCompositeOperation(
            _generationState,
            ref _allocations,
            _count,
            _allocationsEntered);
        GC.KeepAlive(_generationOwner);
    }
}

internal readonly record struct NativeMultiOwnerOperationEntry(
    NativeOwnerKernel Kernel,
    NativeGeneration Generation,
    NativeAllocation Allocation,
    long GenerationNumber,
    long AllocationId);

[InlineArray(NativeCompositeAllocationBuffer.Capacity)]
internal struct NativeMultiOwnerOperationEntryBuffer
{
    private NativeMultiOwnerOperationEntry _element0;
}

[InlineArray(NativeCompositeAllocationBuffer.Capacity)]
internal struct NativeMultiOwnerOperationMap
{
    private byte _element0;
}

internal ref struct NativeMultiOwnerOperationToken
{
    private NativeCompositeOperationToken _group0;
    private NativeCompositeOperationToken _group1;
    private NativeCompositeOperationToken _group2;
    private NativeCompositeOperationToken _group3;
    private NativeCompositeOperationToken _group4;
    private NativeCompositeOperationToken _group5;
    private NativeCompositeOperationToken _group6;
    private NativeCompositeOperationToken _group7;
    private NativeMultiOwnerOperationMap _groupByEntry;
    private NativeMultiOwnerOperationMap _indexByEntry;
    private int _groupCount;
    private int _entryCount;

    internal NativeMultiOwnerOperationToken(
        ref NativeMultiOwnerOperationEntryBuffer entries,
        int count,
        string operation)
    {
        this = default;
        if ((uint)(count - 1)
            >= NativeCompositeAllocationBuffer.Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _entryCount = count;
        int assignedMask = 0;
        Span<long> allocationIds =
            stackalloc long[
                NativeCompositeAllocationBuffer.Capacity];
        try
        {
            while (assignedMask != (1 << count) - 1)
            {
                int firstIndex = 0;
                while ((assignedMask & (1 << firstIndex)) != 0)
                {
                    firstIndex++;
                }

                NativeMultiOwnerOperationEntry first =
                    entries[firstIndex];
                NativeCompositeAllocationBuffer allocations =
                    default;
                int allocationCount = 0;
                for (int index = firstIndex;
                    index < count;
                    index++)
                {
                    if ((assignedMask & (1 << index)) != 0)
                    {
                        continue;
                    }

                    NativeMultiOwnerOperationEntry candidate =
                        entries[index];
                    if (!ReferenceEquals(
                            first.Kernel,
                            candidate.Kernel)
                        || !ReferenceEquals(
                            first.Generation,
                            candidate.Generation)
                        || first.GenerationNumber
                            != candidate.GenerationNumber)
                    {
                        continue;
                    }

                    allocations[allocationCount] =
                        candidate.Allocation;
                    allocationIds[allocationCount] =
                        candidate.AllocationId;
                    _groupByEntry[index] =
                        checked((byte)_groupCount);
                    _indexByEntry[index] =
                        checked((byte)allocationCount);
                    allocationCount++;
                    assignedMask |= 1 << index;
                }

                NativeCompositeOperationToken token =
                    first.Kernel.EnterCompositeOperation(
                        first.Generation,
                        allocations,
                        first.GenerationNumber,
                        allocationIds[..allocationCount],
                        operation);
                switch (_groupCount)
                {
                    case 0: _group0 = token; break;
                    case 1: _group1 = token; break;
                    case 2: _group2 = token; break;
                    case 3: _group3 = token; break;
                    case 4: _group4 = token; break;
                    case 5: _group5 = token; break;
                    case 6: _group6 = token; break;
                    case 7: _group7 = token; break;
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(count));
                }

                _groupCount++;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal NativeLeaseView<T> GetView<T>(int entryIndex)
    {
        if ((uint)entryIndex >= (uint)_entryCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(entryIndex));
        }

        int allocationIndex = _indexByEntry[entryIndex];
        return _groupByEntry[entryIndex] switch
        {
            0 => _group0.GetView<T>(allocationIndex),
            1 => _group1.GetView<T>(allocationIndex),
            2 => _group2.GetView<T>(allocationIndex),
            3 => _group3.GetView<T>(allocationIndex),
            4 => _group4.GetView<T>(allocationIndex),
            5 => _group5.GetView<T>(allocationIndex),
            6 => _group6.GetView<T>(allocationIndex),
            7 => _group7.GetView<T>(allocationIndex),
            _ => throw new InvalidOperationException(
                "The native operation group is not valid.")
        };
    }

    public void Dispose()
    {
        while (_groupCount > 0)
        {
            _groupCount--;
            switch (_groupCount)
            {
                case 0: _group0.Dispose(); break;
                case 1: _group1.Dispose(); break;
                case 2: _group2.Dispose(); break;
                case 3: _group3.Dispose(); break;
                case 4: _group4.Dispose(); break;
                case 5: _group5.Dispose(); break;
                case 6: _group6.Dispose(); break;
                case 7: _group7.Dispose(); break;
            }
        }

        _entryCount = 0;
    }
}

internal sealed class NativeOwnerKernel
{
    private const nuint DefaultBumpSegmentBytes = 4096;

    private readonly Lock _gate = new();
    private readonly NativeOwnerKind _kind;
    private readonly string _ownerKind;
    private readonly NativeMemoryReturn _returnMemoryOnDispose;
    private readonly int _storageElementSize;
    private readonly nuint _initialReservation;
    private readonly bool _containsReferences;
    private long _generation;
    private long _nextAllocationId;
    private long _nextSegmentOrdinal;
    private NativeGeneration? _current;
    private NativeOwnerLifecycle _lifecycle;
    private readonly List<NativeGeneration> _retiredGenerations = [];
    private readonly List<NativeGeneration> _quarantinedGenerations = [];
    private long _trimmedBytes;
    private long _trimCallCount;
    private long _freshSegmentAllocationCount;

    internal NativeOwnerLifecycle Lifecycle
    {
        get
        {
            lock (_gate)
            {
                return _lifecycle;
            }
        }
    }

    internal NativeOwnerStatistics GetStatistics()
    {
        lock (_gate)
        {
            NativeGeneration? current = _current;
            if (current is null)
            {
                return new(
                    _lifecycle,
                    _generation,
                    0,
                    0,
                    SumRetiredBytesLocked(),
                    0,
                    0,
                    CountRetiredSegmentsLocked(),
                    _trimmedBytes,
                    _trimCallCount,
                    _freshSegmentAllocationCount);
            }

            long requestedBytes = 0;
            foreach (NativeAllocation allocation in current.Allocations.Values)
            {
                if (!IsCurrentAllocation(current, allocation))
                {
                    continue;
                }

                long allocationBytes = _kind == NativeOwnerKind.Pool
                    ? checked((long)allocation.Length * _storageElementSize)
                    : checked((long)allocation.StorageBytes);
                requestedBytes = checked(requestedBytes + allocationBytes);
            }

            long retainedBytes = 0;
            foreach (NativeSlab slab in current.Slabs)
            {
                retainedBytes = checked(retainedBytes + (long)slab.Segment.ByteLength);
            }

            foreach (NativeBumpSegment bump in current.BumpSegments)
            {
                retainedBytes = checked(retainedBytes + (long)bump.Segment.ByteLength);
            }

            int availableSegmentCount = current.AvailableSlabs.Count;
            foreach (NativeBumpSegment bump in current.BumpSegments)
            {
                if (bump.IsCompletelyIdle)
                {
                    availableSegmentCount++;
                }
            }

            return new(
                _lifecycle,
                current.Number,
                requestedBytes,
                retainedBytes,
                SumRetiredBytesLocked(),
                current.Slabs.Count + current.BumpSegments.Count,
                availableSegmentCount,
                CountRetiredSegmentsLocked(),
                _trimmedBytes,
                _trimCallCount,
                _freshSegmentAllocationCount);
        }
    }

    internal NativeOwnerDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        lock (_gate)
        {
            NativeGeneration? current = _current;
            int activeRecords = 0;
            if (current is not null)
            {
                foreach (NativeAllocation allocation
                    in current.Allocations.Values)
                {
                    if (IsCurrentAllocation(current, allocation))
                    {
                        activeRecords++;
                    }
                }
            }

            int availableSegmentCount =
                current?.AvailableSlabs.Count ?? 0;
            if (current is not null)
            {
                foreach (NativeBumpSegment bump
                    in current.BumpSegments)
                {
                    if (bump.IsCompletelyIdle)
                    {
                        availableSegmentCount++;
                    }
                }
            }

            int quarantinedSegments = 0;
            foreach (NativeGeneration generation
                in _quarantinedGenerations)
            {
                quarantinedSegments = checked(
                    quarantinedSegments
                    + generation.RetiredSegmentCount);
            }

            return new NativeOwnerDiagnosticSnapshot(
                _lifecycle,
                current?.Number ?? _generation,
                current?.ScopeEpoch ?? 0,
                NativeMemoryTestHooks.CurrentMetricsEpoch,
                activeRecords,
                current?.ScopedRecordCount ?? 0,
                current?.ReferenceRoots.Count ?? 0,
                current?.OrdinaryBumpTraversalIndex ?? 0,
                current?.ScopedBumpTraversalIndex ?? -1,
                current is null
                    ? 0
                    : checked(
                        current.Slabs.Count
                        + current.BumpSegments.Count),
                availableSegmentCount,
                _retiredGenerations.Count,
                CountRetiredSegmentsLocked(),
                SumRetiredBytesLocked(),
                _quarantinedGenerations.Count,
                quarantinedSegments,
                current?.IsQuarantined == true);
        }
    }

    private long SumRetiredBytesLocked()
    {
        long total = 0;
        foreach (NativeGeneration generation in _retiredGenerations)
        {
            total = checked(total + generation.RetiredNativeBytes);
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            total = checked(total + generation.RetiredNativeBytes);
        }

        return total;
    }

    private int CountRetiredSegmentsLocked()
    {
        int total = 0;
        foreach (NativeGeneration generation in _retiredGenerations)
        {
            total = checked(total + generation.RetiredSegmentCount);
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            total = checked(total + generation.RetiredSegmentCount);
        }

        return total;
    }

    internal int CurrentAllocationRecordCountForTest()
    {
        lock (_gate)
        {
            NativeGeneration? current = _current;
            if (current is null)
            {
                return 0;
            }

            int count = 0;
            foreach (NativeAllocation allocation in current.Allocations.Values)
            {
                if (IsCurrentAllocation(current, allocation))
                {
                    count++;
                }
            }

            return count;
        }
    }

    internal int CurrentReferenceRootCountForTest()
    {
        lock (_gate)
        {
            return _current?.ReferenceRoots.Count ?? 0;
        }
    }

    internal (int Ordinary, int Scoped, int SegmentCount) CurrentBumpTraversalForTest()
    {
        lock (_gate)
        {
            NativeGeneration? generation = _current;
            return generation is null
                ? (0, -1, 0)
                : (generation.OrdinaryBumpTraversalIndex, generation.ScopedBumpTraversalIndex, generation.BumpSegments.Count);
        }
    }

    internal int QuarantinedSegmentCountForTest()
    {
        lock (_gate)
        {
            int count = 0;
            foreach (NativeGeneration generation in _quarantinedGenerations)
            {
                count = checked(count + generation.RetiredSegmentCount);
            }

            return count;
        }
    }

    internal int QuarantinedGenerationCountForTest()
    {
        lock (_gate)
        {
            return _quarantinedGenerations.Count;
        }
    }

    internal int RetiredGenerationCountForTest()
    {
        lock (_gate)
        {
            return _retiredGenerations.Count;
        }
    }

    internal int QuarantineCapacityForTest()
    {
        lock (_gate)
        {
            return _quarantinedGenerations.Capacity;
        }
    }

    internal (int Slabs, int AvailableSlabs, int Bumps, int OwnerSegments) CurrentBankCapacitiesForTest()
    {
        lock (_gate)
        {
            NativeGeneration? generation = _current;
            return generation is null
                ? (0, 0, 0, 0)
                : (
                    generation.Slabs.Capacity,
                    generation.AvailableSlabs.Capacity,
                    generation.BumpSegments.Capacity,
                    generation.Owner.SegmentListCapacityForTest());
        }
    }

    internal long CurrentScopeEpochForTest()
    {
        lock (_gate)
        {
            return _current?.ScopeEpoch ?? 0;
        }
    }

    internal long GenerationCounterForTest()
    {
        lock (_gate)
        {
            return _generation;
        }
    }

    internal void SetScopeEpochForTest(long value)
    {
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked("SetScopeEpochForTest");
            generation.ScopeEpoch = value;
            foreach (NativeAllocation allocation in generation.Allocations.Values)
            {
                if (allocation.IsScoped
                    && allocation.Lifecycle == NativeAllocationLifecycle.Active)
                {
                    allocation.SetScopeEpochForTest(value);
                }
            }

            foreach (NativeBumpSegment segment in generation.ScopedTouchedSegments)
            {
                if (segment.PendingScopeEpoch >= 0)
                {
                    segment.PendingScopeEpoch = value;
                }
            }
        }
    }

    internal void SetGenerationCounterForTest(long value)
    {
        lock (_gate)
        {
            _generation = value;
        }
    }

    internal long[] CurrentSegmentOrdinalsForTest()
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return [];
            }

            if (_kind == NativeOwnerKind.Pool)
            {
                return _current.Slabs.Select(slab => slab.AllocationOrdinal).ToArray();
            }

            return _current.BumpSegments.Select(segment => segment.AllocationOrdinal).ToArray();
        }
    }

    private NativeOwnerKernel(
        NativeOwnerKind kind,
        string ownerKind,
        NativeMemoryReturn returnMemoryOnDispose,
        int storageElementSize,
        nuint initialReservation,
        bool containsReferences,
        bool doNotLeaseOnDeclaration)
    {
        _kind = kind;
        _ownerKind = ownerKind;
        _returnMemoryOnDispose = returnMemoryOnDispose;
        _storageElementSize = storageElementSize;
        _initialReservation = initialReservation;
        _containsReferences = containsReferences;
        _lifecycle = doNotLeaseOnDeclaration ? NativeOwnerLifecycle.Unleased : NativeOwnerLifecycle.Active;

        if (doNotLeaseOnDeclaration)
        {
            return;
        }

        NativeGeneration generation = new(0);
        _current = generation;
        try
        {
            ReserveInitialStorageLocked(generation, "initial reservation", _lifecycle);
        }
        catch
        {
            generation.Owner.ReleaseToNative();
            _current = null;
            throw;
        }
    }

    internal static NativeOwnerKernel CreatePool(
        int initialCapacity,
        int storageElementSize,
        string ownerKind,
        NativeMemoryReturn returnMemoryOnDispose,
        bool containsReferences,
        bool doNotLeaseOnDeclaration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(storageElementSize);
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        return new NativeOwnerKernel(
            NativeOwnerKind.Pool,
            ownerKind,
            returnMemoryOnDispose,
            storageElementSize,
            (nuint)initialCapacity,
            containsReferences,
            doNotLeaseOnDeclaration);
    }

    internal static NativeOwnerKernel CreateRegion(
        nuint preAllocateBytes,
        string ownerKind,
        NativeMemoryReturn returnMemoryOnDispose,
        bool containsReferences,
        bool doNotLeaseOnDeclaration)
    {
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        return new NativeOwnerKernel(
            NativeOwnerKind.Region,
            ownerKind,
            returnMemoryOnDispose,
            storageElementSize: 0,
            preAllocateBytes,
            containsReferences,
            doNotLeaseOnDeclaration);
    }

    internal static NativeOwnerKernel CreateArena(
        nuint preAllocateBytes,
        string ownerKind,
        NativeMemoryReturn returnMemoryOnDispose,
        bool doNotLeaseOnDeclaration)
    {
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        return new NativeOwnerKernel(
            NativeOwnerKind.Arena,
            ownerKind,
            returnMemoryOnDispose,
            storageElementSize: 0,
            preAllocateBytes,
            containsReferences: false,
            doNotLeaseOnDeclaration);
    }

    internal NativePoolLease RentInitialized<T>(
        int length,
        bool scoped,
        NativeLeaseInitializer<T> initializer)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        (NativeGeneration Generation, NativeAllocation Allocation, NativePoolLease Lease)
            reservation = BeginPoolInitialization(length, scoped);
        int initializedLength = 0;
        try
        {
            NativeLeaseWriter<T> writer = new(
                reservation.Allocation,
                ref initializedLength);
            initializer(writer);
            reservation.Allocation.InitializedLength = initializedLength;
            CompleteInitialization(
                reservation.Generation,
                reservation.Allocation,
                scoped);
            return reservation.Lease;
        }
        catch
        {
            reservation.Allocation.InitializedLength = initializedLength;
            AbortPoolInitialization(
                reservation.Generation,
                reservation.Allocation);
            throw;
        }
    }

    private (
        NativeGeneration Generation,
        NativeAllocation Allocation,
        NativePoolLease Lease)
        BeginPoolInitialization(int length, bool scoped)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked(scoped ? "LeaseScoped" : "Rent");
            if (_kind != NativeOwnerKind.Pool)
            {
                throw CreateStateException("Rent", "This owner does not expose typed pool leases.", 0);
            }

            NativeSlab? slab = null;
            if (length > 0)
            {
                slab = TakeSmallestAvailableSlabLocked(generation, length);
                if (slab is null)
                {
                    generation.Slabs.EnsureCapacity(checked(generation.Slabs.Count + 1));
                    slab = AddPoolSlabLocked(generation, length, "pool growth", _lifecycle);
                    generation.AddSlabOrdered(slab);
                }
            }

            long allocationId = NextAllocationIdLocked();
            long epoch = scoped ? BeginScopedLocked(generation) : generation.ScopeEpoch;
            generation.Allocations.EnsureCapacity(checked(generation.Allocations.Count + 1));
            if (scoped)
            {
                generation.ScopedAllocationBank.EnsureCapacity(
                    checked(generation.ScopedAllocationBank.Count + 1));
                generation.ScopedCleanupPending.EnsureCapacity(
                    checked(generation.ScopedCleanupPending.Count + 1));
            }

            if (!scoped)
            {
                generation.ReusableAllocations.EnsureCapacity(
                    checked(generation.ReusableAllocations.Count + 1));
            }

            if (slab is not null)
            {
                generation.AvailableSlabs.EnsureCapacity(
                    checked(generation.AvailableSlabs.Count + 1));
            }

            NativeAllocation allocation = RentAllocationLocked(
                generation,
                allocationId,
                slab,
                bumpSegment: null,
                offsetBytes: 0,
                length,
                slab?.Capacity ?? 0,
                storageBytes: slab is null
                    ? 0
                    : checked((nuint)length * (nuint)_storageElementSize),
                referenceRoots: _containsReferences ? generation.ReferenceRoots : null,
                scoped,
                epoch);
            allocation.Lifecycle = NativeAllocationLifecycle.Initializing;
            generation.Allocations.Add(allocationId, allocation);
            if (scoped)
            {
                generation.ScopedRecordCount =
                    checked(generation.ScopedRecordCount + 1);
            }

            generation.InitializationsInProgress++;
            if (slab is not null)
            {
                if (slab.HasBeenUsed)
                {
                    NativeMemoryTestHooks.RecordReusedNativeSegment();
                }

                slab.HasBeenUsed = true;
            }

            return (
                generation,
                allocation,
                new NativePoolLease(
                    allocationId,
                    allocation));
        }
    }

    private void CompleteInitialization(
        NativeGeneration generation,
        NativeAllocation allocation,
        bool scoped)
    {
        if (allocation.InitializedLength != allocation.Length)
        {
            throw new InvalidOperationException(
                "The native lease initializer did not write all logical elements.");
        }

        lock (_gate)
        {
            if (!ReferenceEquals(generation, _current)
                || _lifecycle != NativeOwnerLifecycle.Active
                || allocation.Lifecycle
                    != NativeAllocationLifecycle.Initializing)
            {
                throw CreateStateException(
                    "Initialize",
                    "The owner changed before the initializer completed.",
                    allocation.Id);
            }

            allocation.Lifecycle = NativeAllocationLifecycle.Active;
            allocation.InitializedLength = 0;
            if (scoped
                && (allocation.ReferenceRoots is not null
                    || allocation.Slab is not null))
            {
                generation.ScopedCleanupPending.Add(allocation);
            }

            generation.InitializationsInProgress--;
        }
    }

    private void AbortPoolInitialization(
        NativeGeneration generation,
        NativeAllocation allocation)
    {
        lock (_gate)
        {
            if (allocation.Lifecycle
                != NativeAllocationLifecycle.Initializing)
            {
                return;
            }

            allocation.ClearInitializedReferences();
            allocation.Lifecycle = NativeAllocationLifecycle.Returned;
            allocation.InitializedLength = 0;
            generation.Allocations.Remove(allocation.Id);
            if (allocation.IsScoped)
            {
                ReleaseLastScopedRecordLocked(generation, allocation);
            }
            else
            {
                generation.ReusableAllocations.Add(allocation);
            }

            if (allocation.Slab is not null && allocation.Length > 0)
            {
                generation.AddAvailableSlabOrdered(allocation.Slab);
            }

            generation.InitializationsInProgress--;
        }
    }

    internal NativeRegionAllocation LeaseBumpInitialized<T>(
        int length,
        int elementSize,
        nuint alignment,
        bool scoped,
        bool containsReferences,
        NativeLeaseInitializer<T> initializer)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        NativeBumpInitialization reservation = BeginBumpInitialization(
            length,
            elementSize,
            alignment,
            scoped,
            containsReferences);
        int initializedLength = 0;
        try
        {
            NativeLeaseWriter<T> writer = new(
                reservation.Allocation,
                ref initializedLength);
            initializer(writer);
            reservation.Allocation.InitializedLength = initializedLength;
            CompleteBumpInitialization(reservation);
            return reservation.Lease;
        }
        catch
        {
            reservation.Allocation.InitializedLength = initializedLength;
            AbortBumpInitialization(reservation);
            throw;
        }
    }

    internal bool BeginBumpInitializationGroup(
        ref NativeBumpInitializationGroupBuffer reservations,
        int firstLength,
        int firstElementSize,
        nuint firstAlignment,
        bool firstContainsReferences,
        int secondLength,
        int secondElementSize,
        nuint secondAlignment,
        bool secondContainsReferences,
        int thirdLength,
        int thirdElementSize,
        nuint thirdAlignment,
        bool thirdContainsReferences,
        int fourthLength,
        int fourthElementSize,
        nuint fourthAlignment,
        bool fourthContainsReferences,
        bool continueExistingGroup = false,
        bool useSingleInitializationAdmission = true,
        NativeGeneration? sourceGeneration = null,
        NativeAllocation? sourceAllocation = null,
        long sourceGenerationNumber = 0,
        long sourceAllocationId = 0)
    {
        NativeBumpBatchRequestBuffer requests = default;
        requests[0] = CreateBumpBatchRequest(
            firstLength,
            firstElementSize,
            firstAlignment,
            firstContainsReferences);
        requests[1] = CreateBumpBatchRequest(
            secondLength,
            secondElementSize,
            secondAlignment,
            secondContainsReferences);
        requests[2] = CreateBumpBatchRequest(
            thirdLength,
            thirdElementSize,
            thirdAlignment,
            thirdContainsReferences);
        requests[3] = CreateBumpBatchRequest(
            fourthLength,
            fourthElementSize,
            fourthAlignment,
            fourthContainsReferences);

        if (_gate.IsHeldByCurrentThread)
        {
            return BeginBumpInitializationGroupLocked(
                ref requests,
                ref reservations[0],
                firstLength,
                firstElementSize,
                firstAlignment,
                firstContainsReferences,
                secondLength,
                secondElementSize,
                secondAlignment,
                secondContainsReferences,
                thirdLength,
                thirdElementSize,
                thirdAlignment,
                thirdContainsReferences,
                fourthLength,
                fourthElementSize,
                fourthAlignment,
                fourthContainsReferences,
                continueExistingGroup,
                useSingleInitializationAdmission,
                sourceGeneration,
                sourceAllocation,
                sourceGenerationNumber,
                sourceAllocationId);
        }

        lock (_gate)
        {
            return BeginBumpInitializationGroupLocked(
                ref requests,
                ref reservations[0],
                firstLength,
                firstElementSize,
                firstAlignment,
                firstContainsReferences,
                secondLength,
                secondElementSize,
                secondAlignment,
                secondContainsReferences,
                thirdLength,
                thirdElementSize,
                thirdAlignment,
                thirdContainsReferences,
                fourthLength,
                fourthElementSize,
                fourthAlignment,
                fourthContainsReferences,
                continueExistingGroup,
                useSingleInitializationAdmission,
                sourceGeneration,
                sourceAllocation,
                sourceGenerationNumber,
                sourceAllocationId);
        }
    }

    private bool
        BeginBumpInitializationGroupLocked(
            ref NativeBumpBatchRequestBuffer requests,
            ref NativeBumpInitialization firstReservation,
            int firstLength,
            int firstElementSize,
            nuint firstAlignment,
            bool firstContainsReferences,
            int secondLength,
            int secondElementSize,
            nuint secondAlignment,
            bool secondContainsReferences,
            int thirdLength,
            int thirdElementSize,
            nuint thirdAlignment,
            bool thirdContainsReferences,
            int fourthLength,
            int fourthElementSize,
            nuint fourthAlignment,
            bool fourthContainsReferences,
            bool continueExistingGroup,
            bool useSingleInitializationAdmission,
            NativeGeneration? sourceGeneration,
            NativeAllocation? sourceAllocation,
            long sourceGenerationNumber,
            long sourceAllocationId)
    {
        NativeGeneration generation =
            EnsureActiveLocked("InitializeScoped");
        ValidateInitializationSourceLocked(
            generation,
            sourceGeneration,
            sourceAllocation,
            sourceGenerationNumber,
            sourceAllocationId);
        if (useSingleInitializationAdmission
            && !continueExistingGroup
            && TryBeginBumpInitializationBatchLocked(
                generation,
                ref requests,
                ref firstReservation,
                4))
        {
            return true;
        }

        int expectedInitializations =
            continueExistingGroup ? 4 : 0;
        if (generation.InitializationsInProgress
            != expectedInitializations)
        {
            throw CreateInUseException(
                "InitializeScoped",
                generation.Number,
                0,
                generation.ActiveOperations,
                "A different native lease initializer is active.");
        }

        int reservationCount = 0;
        try
        {
            firstReservation =
                BeginBumpInitializationLocked(
                firstLength,
                firstAlignment,
                scoped: true,
                firstContainsReferences,
                allowExistingInitialization: true,
                CalculateByteLength(
                    firstLength,
                    firstElementSize,
                    "scoped allocation"));
            reservationCount = 1;
            Unsafe.Add(ref firstReservation, 1) =
                BeginBumpInitializationLocked(
                secondLength,
                secondAlignment,
                scoped: true,
                secondContainsReferences,
                allowExistingInitialization: true,
                CalculateByteLength(
                    secondLength,
                    secondElementSize,
                    "scoped allocation"));
            reservationCount = 2;
            Unsafe.Add(ref firstReservation, 2) =
                BeginBumpInitializationLocked(
                thirdLength,
                thirdAlignment,
                scoped: true,
                thirdContainsReferences,
                allowExistingInitialization: true,
                CalculateByteLength(
                    thirdLength,
                    thirdElementSize,
                    "scoped allocation"));
            reservationCount = 3;
            Unsafe.Add(ref firstReservation, 3) =
                BeginBumpInitializationLocked(
                fourthLength,
                fourthAlignment,
                scoped: true,
                fourthContainsReferences,
                allowExistingInitialization: true,
                CalculateByteLength(
                    fourthLength,
                    fourthElementSize,
                    "scoped allocation"));
            reservationCount = 4;

            generation.ScopedTouchedSegments.EnsureCapacity(
                checked(generation.ScopedTouchedSegments.Count + 4));
            int referenceAllocationCount =
                (firstContainsReferences ? 1 : 0)
                + (secondContainsReferences ? 1 : 0)
                + (thirdContainsReferences ? 1 : 0)
                + (fourthContainsReferences ? 1 : 0);
            generation.ScopedCleanupPending.EnsureCapacity(
                checked(
                    generation.ScopedCleanupPending.Count
                    + referenceAllocationCount));
            return false;
        }
        catch
        {
            if (reservationCount >= 4)
            {
                AbortBumpInitialization(
                    Unsafe.Add(ref firstReservation, 3));
            }

            if (reservationCount >= 3)
            {
                AbortBumpInitialization(
                    Unsafe.Add(ref firstReservation, 2));
            }

            if (reservationCount >= 2)
            {
                AbortBumpInitialization(
                    Unsafe.Add(ref firstReservation, 1));
            }

            if (reservationCount >= 1)
            {
                AbortBumpInitialization(
                    firstReservation);
            }

            throw;
        }
    }

    private void ValidateInitializationSourceLocked(
        NativeGeneration current,
        NativeGeneration? sourceGeneration,
        NativeAllocation? sourceAllocation,
        long sourceGenerationNumber,
        long sourceAllocationId)
    {
        if (sourceGeneration is null || sourceAllocation is null)
        {
            return;
        }

        if (!ReferenceEquals(current, sourceGeneration)
            || !IsDirectHandleActive(
                current,
                sourceAllocation,
                sourceGenerationNumber,
                sourceAllocationId))
        {
            throw CreateReturnedException(
                "InitializeScoped",
                sourceGenerationNumber,
                _generation,
                sourceAllocationId,
                "The source handle is stale.");
        }

    }

    internal bool BeginBumpInitializationOctet(
        ref NativeBumpInitializationBuffer reservations,
        int firstLength,
        int firstElementSize,
        nuint firstAlignment,
        bool firstContainsReferences,
        int secondLength,
        int secondElementSize,
        nuint secondAlignment,
        bool secondContainsReferences,
        int thirdLength,
        int thirdElementSize,
        nuint thirdAlignment,
        bool thirdContainsReferences,
        int fourthLength,
        int fourthElementSize,
        nuint fourthAlignment,
        bool fourthContainsReferences,
        int fifthLength,
        int fifthElementSize,
        nuint fifthAlignment,
        bool fifthContainsReferences,
        int sixthLength,
        int sixthElementSize,
        nuint sixthAlignment,
        bool sixthContainsReferences,
        int seventhLength,
        int seventhElementSize,
        nuint seventhAlignment,
        bool seventhContainsReferences,
        int eighthLength,
        int eighthElementSize,
        nuint eighthAlignment,
        bool eighthContainsReferences,
        NativeGeneration? sourceGeneration = null,
        NativeAllocation? sourceAllocation = null,
        long sourceGenerationNumber = 0,
        long sourceAllocationId = 0)
    {
        NativeBumpBatchRequestBuffer requests = default;
        requests[0] = CreateBumpBatchRequest(
            firstLength,
            firstElementSize,
            firstAlignment,
            firstContainsReferences);
        requests[1] = CreateBumpBatchRequest(
            secondLength,
            secondElementSize,
            secondAlignment,
            secondContainsReferences);
        requests[2] = CreateBumpBatchRequest(
            thirdLength,
            thirdElementSize,
            thirdAlignment,
            thirdContainsReferences);
        requests[3] = CreateBumpBatchRequest(
            fourthLength,
            fourthElementSize,
            fourthAlignment,
            fourthContainsReferences);
        requests[4] = CreateBumpBatchRequest(
            fifthLength,
            fifthElementSize,
            fifthAlignment,
            fifthContainsReferences);
        requests[5] = CreateBumpBatchRequest(
            sixthLength,
            sixthElementSize,
            sixthAlignment,
            sixthContainsReferences);
        requests[6] = CreateBumpBatchRequest(
            seventhLength,
            seventhElementSize,
            seventhAlignment,
            seventhContainsReferences);
        requests[7] = CreateBumpBatchRequest(
            eighthLength,
            eighthElementSize,
            eighthAlignment,
            eighthContainsReferences);

        lock (_gate)
        {
            NativeGeneration current = EnsureActiveLocked(
                "InitializeScoped",
                sourceGenerationNumber,
                sourceAllocationId);
            ValidateInitializationSourceLocked(
                current,
                sourceGeneration,
                sourceAllocation,
                sourceGenerationNumber,
                sourceAllocationId);

            if (TryBeginBumpInitializationBatchLocked(
                    current,
                    ref requests,
                    ref reservations[0],
                    count: 8))
            {
                return true;
            }

            _ = BeginBumpInitializationGroupLocked(
                ref requests,
                ref reservations[0],
                    firstLength,
                    firstElementSize,
                    firstAlignment,
                    firstContainsReferences,
                    secondLength,
                    secondElementSize,
                    secondAlignment,
                    secondContainsReferences,
                    thirdLength,
                    thirdElementSize,
                    thirdAlignment,
                    thirdContainsReferences,
                    fourthLength,
                    fourthElementSize,
                    fourthAlignment,
                    fourthContainsReferences,
                    continueExistingGroup: false,
                    useSingleInitializationAdmission: false,
                    sourceGeneration,
                    sourceAllocation,
                    sourceGenerationNumber,
                    sourceAllocationId);
            try
            {
                _ = BeginBumpInitializationGroupLocked(
                    ref requests,
                    ref reservations[4],
                        fifthLength,
                        fifthElementSize,
                        fifthAlignment,
                        fifthContainsReferences,
                        sixthLength,
                        sixthElementSize,
                        sixthAlignment,
                        sixthContainsReferences,
                        seventhLength,
                        seventhElementSize,
                        seventhAlignment,
                        seventhContainsReferences,
                        eighthLength,
                        eighthElementSize,
                        eighthAlignment,
                        eighthContainsReferences,
                        continueExistingGroup: true,
                        useSingleInitializationAdmission: false,
                        sourceGeneration,
                        sourceAllocation,
                        sourceGenerationNumber,
                        sourceAllocationId);
                return false;
            }
            catch
            {
                AbortBumpInitialization(reservations[3]);
                AbortBumpInitialization(reservations[2]);
                AbortBumpInitialization(reservations[1]);
                AbortBumpInitialization(reservations[0]);
                throw;
            }
        }
    }

    private static NativeBumpBatchRequest CreateBumpBatchRequest(
        int length,
        int elementSize,
        nuint alignment,
        bool containsReferences)
    {
        ValidateBumpInitializationArguments(
            length,
            elementSize,
            alignment);
        return new NativeBumpBatchRequest(
            length,
            CalculateByteLength(
                length,
                elementSize,
                "scoped allocation"),
            alignment,
            containsReferences);
    }

    private bool TryBeginBumpInitializationBatchLocked(
        NativeGeneration generation,
        ref NativeBumpBatchRequestBuffer requests,
        ref NativeBumpInitialization firstReservation,
        int count)
    {
        int firstRecordIndex = generation.ScopedRecordCount;
        if (_kind is not (NativeOwnerKind.Region or NativeOwnerKind.Arena)
            || generation.InitializationsInProgress != 0
            || firstRecordIndex
                > generation.ScopedAllocationBank.Count - count)
        {
            return false;
        }

        for (int index = 0; index < count; index++)
        {
            NativeBumpBatchRequest request = requests[index];
            if (request.ContainsReferences)
            {
                return false;
            }
        }

        NativeBumpCursorBuffer offsets = default;
        NativeBumpCursorBuffer originalCursors = default;
        NativeBumpSegment? segment =
            FindScopedBumpBatchSpaceLocked(
                generation,
                ref requests,
                ref offsets,
                ref originalCursors,
                count,
                out nuint finalCursor);
        if (segment is null)
        {
            return false;
        }

        long firstAllocationId =
            checked(_nextAllocationId + 1);
        long lastAllocationId =
            checked(_nextAllocationId + count);
        bool startedScope =
            !generation.ScopedBaselineActive;
        long scopeEpoch = BeginScopedLocked(generation);
        if (segment.PendingScopeEpoch != scopeEpoch)
        {
            generation.ScopedTouchedSegments.EnsureCapacity(
                checked(
                    generation.ScopedTouchedSegments.Count
                    + 1));
        }

        int initializedCount = 0;
        try
        {
            for (int index = 0; index < count; index++)
            {
                NativeBumpBatchRequest request = requests[index];
                long allocationId =
                    checked(firstAllocationId + index);
                NativeAllocation allocation =
                    generation.ScopedAllocationBank[
                        firstRecordIndex + index];
                allocation.Reset(
                    allocationId,
                    slab: null,
                    segment,
                    offsets[index],
                    request.Length,
                    request.Length,
                    request.ByteLength,
                    referenceRoots: null,
                    scoped: true,
                    scopeEpoch);
                allocation.Lifecycle =
                    NativeAllocationLifecycle.Initializing;
                Unsafe.Add(ref firstReservation, index) =
                    new NativeBumpInitialization(
                        allocation,
                        createdSegment: null,
                        originalCursors[index],
                        cursorCaptured: true,
                        scoped: true,
                        startedScope:
                            startedScope && index == 0);
                initializedCount++;
            }

            segment.HighCursor = finalCursor;
            generation.ScopedRecordCount =
                checked(firstRecordIndex + count);
            generation.InitializationsInProgress =
                checked(
                    generation.InitializationsInProgress
                    + 1);
            _nextAllocationId = lastAllocationId;
            return true;
        }
        catch
        {
            for (int index = 0;
                index < initializedCount;
                index++)
            {
                NativeAllocation allocation =
                    generation.ScopedAllocationBank[
                        firstRecordIndex + index];
                allocation.Lifecycle =
                    NativeAllocationLifecycle.Returned;
                allocation.InitializedLength = 0;
            }

            if (startedScope)
            {
                generation.ScopedBaselineActive = false;
                ClearScopedRangesLocked(generation);
            }

            throw;
        }
    }

    private static NativeBumpSegment?
        FindScopedBumpBatchSpaceLocked(
            NativeGeneration generation,
            ref NativeBumpBatchRequestBuffer requests,
            ref NativeBumpCursorBuffer offsets,
            ref NativeBumpCursorBuffer originalCursors,
            int count,
            out nuint finalCursor)
    {
        finalCursor = 0;
        if (generation.BumpSegments.Count == 0)
        {
            return null;
        }

        int start = Math.Min(
            Math.Max(generation.ScopedBumpTraversalIndex, 0),
            generation.BumpSegments.Count - 1);
        for (int segmentIndex = start;
            segmentIndex >= 0;
            segmentIndex--)
        {
            NativeMemoryTestHooks.RecordBumpTraversalVisit();
            NativeBumpSegment segment =
                generation.BumpSegments[segmentIndex];
            nuint cursor = segment.HighCursor;
            bool fits = cursor >= segment.LowCursor;
            for (int index = 0;
                fits && index < count;
                index++)
            {
                NativeBumpBatchRequest request = requests[index];
                if (request.ByteLength
                    > cursor - segment.LowCursor)
                {
                    fits = false;
                    break;
                }

                nuint candidate = AlignDown(
                    cursor - request.ByteLength,
                    request.Alignment);
                if (candidate < segment.LowCursor)
                {
                    fits = false;
                    break;
                }

                originalCursors[index] = cursor;
                offsets[index] = candidate;
                cursor = candidate;
            }

            if (!fits)
            {
                continue;
            }

            generation.ScopedBumpTraversalIndex =
                segmentIndex;
            finalCursor = cursor;
            return segment;
        }

        generation.ScopedBumpTraversalIndex = -1;
        return null;
    }

    private NativeBumpInitialization BeginBumpInitialization(
        int length,
        int elementSize,
        nuint alignment,
        bool scoped,
        bool containsReferences,
        bool allowExistingInitialization = false)
    {
        ValidateBumpInitializationArguments(
            length,
            elementSize,
            alignment);
        nuint byteLength = CalculateByteLength(length, elementSize, scoped ? "scoped allocation" : "allocation");

        if (_gate.IsHeldByCurrentThread)
        {
            return BeginBumpInitializationLocked(
                length,
                alignment,
                scoped,
                containsReferences,
                allowExistingInitialization,
                byteLength);
        }

        lock (_gate)
        {
            return BeginBumpInitializationLocked(
                length,
                alignment,
                scoped,
                containsReferences,
                allowExistingInitialization,
                byteLength);
        }
    }

    private static void ValidateBumpInitializationArguments(
        int length,
        int elementSize,
        nuint alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(elementSize);
        ArgumentOutOfRangeException.ThrowIfZero(alignment);
    }

    private NativeBumpInitialization BeginBumpInitializationLocked(
        int length,
        nuint alignment,
        bool scoped,
        bool containsReferences,
        bool allowExistingInitialization,
        nuint byteLength)
    {
        NativeGeneration generation = EnsureActiveLocked(
            scoped ? "LeaseScoped" : "Lease");
        if (_kind is not (NativeOwnerKind.Region or NativeOwnerKind.Arena))
        {
            throw CreateStateException("Lease", "This owner does not expose heterogeneous allocations.", 0);
        }

        if (generation.InitializationsInProgress != 0
            && !allowExistingInitialization)
        {
            throw CreateInUseException(
                scoped ? "LeaseScoped" : "Lease",
                generation.Number,
                0,
                generation.ActiveOperations,
                "A native lease initializer is already active.");
        }

        bool startedScope = scoped && !generation.ScopedBaselineActive;
        long epoch = scoped ? BeginScopedLocked(generation) : generation.ScopeEpoch;
        NativeBumpSegment? bumpSegment = null;
        NativeBumpSegment? createdSegment = null;
        nuint originalCursor = 0;
        bool cursorCaptured = false;
        nuint offset = 0;
        try
        {
            bool reuseRegisteredScopedRecord =
                scoped
                && generation.ScopedRecordCount
                    < generation.ScopedAllocationBank.Count;
            if (!reuseRegisteredScopedRecord)
            {
                generation.Allocations.EnsureCapacity(
                    checked(generation.Allocations.Count + 1));
            }

            if (scoped)
            {
                if (!reuseRegisteredScopedRecord)
                {
                    generation.ScopedAllocationBank.EnsureCapacity(
                        checked(
                            generation.ScopedAllocationBank.Count
                            + 1));
                }

                if (containsReferences)
                {
                    generation.ScopedCleanupPending.EnsureCapacity(
                        checked(generation.ScopedCleanupPending.Count + 1));
                }
            }

            if (byteLength > 0)
            {
                bumpSegment = FindBumpSpaceLocked(generation, byteLength, alignment, scoped);
                if (bumpSegment is null)
                {
                    nuint segmentBytes = ChooseBumpSegmentBytes(
                        generation,
                        RequiredFreshBumpBytes(byteLength, alignment));
                    generation.BumpSegments.EnsureCapacity(checked(generation.BumpSegments.Count + 1));
                    generation.Owner.PrepareAddSegmentCapacity(1);
                    NativeSegment segment = NativeSegment.Allocate(
                        segmentBytes,
                        _ownerKind,
                        generation.Number,
                        scoped ? "scoped growth" : "allocation growth",
                        _lifecycle,
                        zeroed: false);
                    createdSegment = new NativeBumpSegment(segment, NextSegmentOrdinalLocked());
                    bumpSegment = createdSegment;
                    AppendBumpSegmentLocked(generation, createdSegment);
                    generation.Owner.AddSegment(segment);
                    _freshSegmentAllocationCount++;
                }

                if (scoped)
                {
                    if (bumpSegment.PendingScopeEpoch != generation.ScopeEpoch)
                    {
                        generation.ScopedTouchedSegments.EnsureCapacity(
                            checked(generation.ScopedTouchedSegments.Count + 1));
                    }

                    originalCursor = bumpSegment.HighCursor;
                    cursorCaptured = true;
                    nuint unaligned = checked(bumpSegment.HighCursor - byteLength);
                    offset = AlignDown(unaligned, alignment);
                    bumpSegment.HighCursor = offset;
                }
                else
                {
                    originalCursor = bumpSegment.LowCursor;
                    cursorCaptured = true;
                    offset = AlignUp(bumpSegment.LowCursor, alignment);
                    bumpSegment.LowCursor = checked(offset + byteLength);
                }
            }

            long allocationId = NextAllocationIdLocked();
            NativeAllocation allocation = RentAllocationLocked(
                generation,
                allocationId,
                slab: null,
                bumpSegment,
                offset,
                length,
                length,
                byteLength,
                referenceRoots: containsReferences && length > 0
                    ? generation.ReferenceRoots
                    : null,
                scoped,
                epoch,
                preserveScopedRegistration:
                    reuseRegisteredScopedRecord);
            allocation.Lifecycle = NativeAllocationLifecycle.Initializing;
            if (!reuseRegisteredScopedRecord)
            {
                generation.Allocations.Add(
                    allocationId,
                    allocation);
            }

            if (scoped)
            {
                generation.ScopedRecordCount =
                    checked(generation.ScopedRecordCount + 1);
            }

            generation.InitializationsInProgress++;

            return new NativeBumpInitialization(
                allocation,
                createdSegment,
                originalCursor,
                cursorCaptured,
                scoped,
                startedScope);
        }
        catch
        {
            if (createdSegment is not null)
            {
                generation.BumpSegments.Remove(createdSegment);
                generation.Owner.RemoveSegment(createdSegment.Segment);
                createdSegment.Segment.FreeNow();
                ResetBumpTraversal(generation);
            }
            else if (bumpSegment is not null && cursorCaptured)
            {
                if (scoped)
                {
                    bumpSegment.HighCursor = originalCursor;
                }
                else
                {
                    bumpSegment.LowCursor = originalCursor;
                }
            }

            if (startedScope && generation.ScopedRecordCount == 0)
            {
                generation.ScopedBaselineActive = false;
                ClearScopedRangesLocked(generation);
            }

            throw;
        }
    }

    private void CompleteBumpInitialization(
        NativeBumpInitialization reservation)
    {
        ValidateInitializedLength(reservation);

        lock (_gate)
        {
            ValidateBumpInitializationLocked(reservation);
            PublishBumpInitializationLocked(reservation);
        }
    }

    internal void CompleteBumpInitializationGroup(
        ref NativeBumpInitializationGroupBuffer reservations,
        bool usesSingleInitializationAdmission) =>
        CompleteBumpInitializationGroup(
            ref reservations,
            usesSingleInitializationAdmission,
            validateInitializedLengths: true);

    internal void CompleteUnmanagedBumpInitializationGroup(
        ref NativeBumpInitializationGroupBuffer reservations,
        bool usesSingleInitializationAdmission) =>
        CompleteBumpInitializationGroup(
            ref reservations,
            usesSingleInitializationAdmission,
            validateInitializedLengths: false);

    private void CompleteBumpInitializationGroup(
        ref NativeBumpInitializationGroupBuffer reservations,
        bool usesSingleInitializationAdmission,
        bool validateInitializedLengths)
    {
        if (validateInitializedLengths)
        {
            ValidateInitializedLength(reservations[0]);
            ValidateInitializedLength(reservations[1]);
            ValidateInitializedLength(reservations[2]);
            ValidateInitializedLength(reservations[3]);
        }

        if (usesSingleInitializationAdmission)
        {
            lock (_gate)
            {
                ValidateBumpInitializationLocked(
                    reservations[0]);
                PublishFastBumpInitializationBatchLocked(
                    ref reservations[0],
                    4);
            }

            return;
        }

        lock (_gate)
        {
            ValidateBumpInitializationLocked(reservations[0]);
            ValidateBumpInitializationLocked(reservations[1]);
            ValidateBumpInitializationLocked(reservations[2]);
            ValidateBumpInitializationLocked(reservations[3]);

            PublishBumpInitializationLocked(reservations[0]);
            PublishBumpInitializationLocked(reservations[1]);
            PublishBumpInitializationLocked(reservations[2]);
            PublishBumpInitializationLocked(reservations[3]);
        }
    }

    internal void CompleteBumpInitializationOctet(
        ref NativeBumpInitializationBuffer reservations,
        bool usesSingleInitializationAdmission) =>
        CompleteBumpInitializationOctet(
            ref reservations,
            usesSingleInitializationAdmission,
            validateInitializedLengths: true);

    internal void CompleteUnmanagedBumpInitializationOctet(
        ref NativeBumpInitializationBuffer reservations,
        bool usesSingleInitializationAdmission) =>
        CompleteBumpInitializationOctet(
            ref reservations,
            usesSingleInitializationAdmission,
            validateInitializedLengths: false);

    private void CompleteBumpInitializationOctet(
        ref NativeBumpInitializationBuffer reservations,
        bool usesSingleInitializationAdmission,
        bool validateInitializedLengths)
    {
        if (validateInitializedLengths)
        {
            ValidateInitializedLength(reservations[0]);
            ValidateInitializedLength(reservations[1]);
            ValidateInitializedLength(reservations[2]);
            ValidateInitializedLength(reservations[3]);
            ValidateInitializedLength(reservations[4]);
            ValidateInitializedLength(reservations[5]);
            ValidateInitializedLength(reservations[6]);
            ValidateInitializedLength(reservations[7]);
        }

        if (usesSingleInitializationAdmission)
        {
            lock (_gate)
            {
                ValidateBumpInitializationLocked(
                    reservations[0]);
                PublishFastBumpInitializationBatchLocked(
                    ref reservations[0],
                    8);
            }

            return;
        }

        lock (_gate)
        {
            ValidateBumpInitializationLocked(reservations[0]);
            ValidateBumpInitializationLocked(reservations[1]);
            ValidateBumpInitializationLocked(reservations[2]);
            ValidateBumpInitializationLocked(reservations[3]);
            ValidateBumpInitializationLocked(reservations[4]);
            ValidateBumpInitializationLocked(reservations[5]);
            ValidateBumpInitializationLocked(reservations[6]);
            ValidateBumpInitializationLocked(reservations[7]);

            PublishBumpInitializationLocked(reservations[0]);
            PublishBumpInitializationLocked(reservations[1]);
            PublishBumpInitializationLocked(reservations[2]);
            PublishBumpInitializationLocked(reservations[3]);
            PublishBumpInitializationLocked(reservations[4]);
            PublishBumpInitializationLocked(reservations[5]);
            PublishBumpInitializationLocked(reservations[6]);
            PublishBumpInitializationLocked(reservations[7]);
        }
    }

    private static void PublishFastBumpInitializationBatchLocked(
        ref NativeBumpInitialization firstReservation,
        int count)
    {
        NativeBumpInitialization first = firstReservation;
        NativeGeneration generation = first.Generation;
        NativeBumpSegment segment =
            first.BumpSegment
            ?? throw new InvalidOperationException(
                "The native batch has no backing segment.");
        NativeAllocation last =
            Unsafe.Add(
                ref firstReservation,
                count - 1).Allocation;
        TrackScopedRangeLocked(
            generation,
            segment,
            last.OffsetBytes,
            first.OriginalCursor);

        int reclaimedRangeCount = 0;
        nuint reclaimedRangeBytes = 0;
        for (int index = 0; index < count; index++)
        {
            NativeAllocation allocation =
                Unsafe.Add(
                    ref firstReservation,
                    index).Allocation;
            nuint reclaimedBytes = segment.ReclaimedOverlap(
                allocation.OffsetBytes,
                allocation.StorageBytes,
                generation.ScopeEpoch);
            if (reclaimedBytes == 0)
            {
                continue;
            }

            reclaimedRangeCount++;
            reclaimedRangeBytes = checked(
                reclaimedRangeBytes + reclaimedBytes);
        }

        if (reclaimedRangeCount != 0)
        {
            NativeMemoryTestHooks.RecordReclaimedRangeReuse(
                reclaimedRangeCount,
                reclaimedRangeBytes);
        }

        for (int index = 0; index < count; index++)
        {
            NativeAllocation allocation =
                Unsafe.Add(
                    ref firstReservation,
                    index).Allocation;
            allocation.Lifecycle =
                NativeAllocationLifecycle.Active;
            allocation.InitializedLength = 0;
        }

        generation.InitializationsInProgress--;
    }

    private static void ValidateInitializedLength(
        NativeBumpInitialization reservation)
    {
        NativeAllocation allocation = reservation.Allocation;
        if (allocation.InitializedLength != allocation.Length)
        {
            throw new InvalidOperationException(
                "The native lease initializer did not write all logical elements.");
        }
    }

    private void ValidateBumpInitializationLocked(
        NativeBumpInitialization reservation)
    {
        NativeGeneration generation = reservation.Generation;
        NativeAllocation allocation = reservation.Allocation;
        if (!ReferenceEquals(generation, _current)
            || _lifecycle != NativeOwnerLifecycle.Active
            || allocation.Lifecycle
                != NativeAllocationLifecycle.Initializing)
        {
            throw CreateStateException(
                "Initialize",
                "The owner changed before the initializer completed.",
                allocation.Id);
        }
    }

    private static void PublishBumpInitializationLocked(
        NativeBumpInitialization reservation)
    {
        NativeGeneration generation = reservation.Generation;
        NativeAllocation allocation = reservation.Allocation;
        if (reservation.Scoped
            && reservation.BumpSegment is not null)
        {
            TrackScopedRangeLocked(
                generation,
                reservation.BumpSegment,
                allocation.OffsetBytes,
                reservation.OriginalCursor);
            nuint reclaimedBytes =
                reservation.BumpSegment.ReclaimedOverlap(
                    allocation.OffsetBytes,
                    allocation.StorageBytes,
                    generation.ScopeEpoch);
            if (reclaimedBytes != 0)
            {
                NativeMemoryTestHooks.RecordReclaimedRangeReuse(
                    reclaimedBytes);
            }
        }

        allocation.Lifecycle = NativeAllocationLifecycle.Active;
        allocation.InitializedLength = 0;
        if (reservation.Scoped
            && allocation.ReferenceRoots is not null)
        {
            generation.ScopedCleanupPending.Add(allocation);
        }

        generation.InitializationsInProgress--;
    }

    internal void AbortBumpInitializationGroup(
        ref NativeBumpInitializationGroupBuffer reservations,
        bool usesSingleInitializationAdmission)
    {
        if (usesSingleInitializationAdmission)
        {
            AbortFastBumpInitializationBatch(
                ref reservations[0],
                4);
            return;
        }

        AbortBumpInitialization(reservations[3]);
        AbortBumpInitialization(reservations[2]);
        AbortBumpInitialization(reservations[1]);
        AbortBumpInitialization(reservations[0]);
    }

    internal void AbortBumpInitializationOctet(
        ref NativeBumpInitializationBuffer reservations,
        bool usesSingleInitializationAdmission)
    {
        if (usesSingleInitializationAdmission)
        {
            AbortFastBumpInitializationBatch(
                ref reservations[0],
                8);
            return;
        }

        AbortBumpInitialization(reservations[7]);
        AbortBumpInitialization(reservations[6]);
        AbortBumpInitialization(reservations[5]);
        AbortBumpInitialization(reservations[4]);
        AbortBumpInitialization(reservations[3]);
        AbortBumpInitialization(reservations[2]);
        AbortBumpInitialization(reservations[1]);
        AbortBumpInitialization(reservations[0]);
    }

    private void AbortFastBumpInitializationBatch(
        ref NativeBumpInitialization firstReservation,
        int count)
    {
        lock (_gate)
        {
            NativeBumpInitialization first = firstReservation;
            NativeGeneration generation = first.Generation;
            if (first.Allocation.Lifecycle
                != NativeAllocationLifecycle.Initializing)
            {
                return;
            }

            for (int index = count - 1; index >= 0; index--)
            {
                NativeAllocation allocation =
                    Unsafe.Add(
                        ref firstReservation,
                        index).Allocation;
                allocation.ClearInitializedReferences();
                allocation.Lifecycle =
                    NativeAllocationLifecycle.Returned;
                allocation.InitializedLength = 0;
            }

            generation.ScopedRecordCount = checked(
                generation.ScopedRecordCount - count);
            if (first.BumpSegment is not null)
            {
                first.BumpSegment.HighCursor =
                    first.OriginalCursor;
            }

            if (first.StartedScope
                && generation.ScopedRecordCount == 0)
            {
                generation.ScopedBaselineActive = false;
                ClearScopedRangesLocked(generation);
            }

            generation.InitializationsInProgress--;
        }
    }

    private void AbortBumpInitialization(
        NativeBumpInitialization reservation)
    {
        lock (_gate)
        {
            NativeGeneration generation = reservation.Generation;
            NativeAllocation allocation = reservation.Allocation;
            if (allocation.Lifecycle
                != NativeAllocationLifecycle.Initializing)
            {
                return;
            }

            allocation.ClearInitializedReferences();
            allocation.Lifecycle = NativeAllocationLifecycle.Returned;
            allocation.InitializedLength = 0;
            if (reservation.Scoped)
            {
                ReleaseLastScopedRecordLocked(generation, allocation);
            }
            else
            {
                generation.Allocations.Remove(allocation.Id);
                generation.ReusableAllocations.Add(allocation);
            }

            if (reservation.CreatedSegment is not null)
            {
                generation.BumpSegments.Remove(
                    reservation.CreatedSegment);
                generation.Owner.RemoveSegment(
                    reservation.CreatedSegment.Segment);
                reservation.CreatedSegment.Segment.FreeNow();
                ResetBumpTraversal(generation);
            }
            else if (reservation.BumpSegment is not null
                && reservation.CursorCaptured)
            {
                if (reservation.Scoped)
                {
                    reservation.BumpSegment.HighCursor =
                        reservation.OriginalCursor;
                }
                else
                {
                    reservation.BumpSegment.LowCursor =
                        reservation.OriginalCursor;
                }
            }

            generation.InitializationsInProgress--;
            if (reservation.StartedScope
                && generation.ScopedRecordCount == 0)
            {
                generation.ScopedBaselineActive = false;
                ClearScopedRangesLocked(generation);
            }
        }
    }

    internal NativeHandleMetadata ValidateHandle(
        NativeGeneration generation,
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        string operation)
    {
        NativeOperationToken token = EnterOperationCore(
            generation,
            allocation,
            generationNumber,
            allocationId,
            operation,
            notifyHooks: false);
        try
        {
            return new NativeHandleMetadata(
                allocation.Length,
                allocation.Capacity);
        }
        finally
        {
            token.Dispose();
        }
    }

    internal NativeOperationToken EnterOperation(
        NativeGeneration generation,
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        string operation) =>
        EnterOperationCore(
            generation,
            allocation,
            generationNumber,
            allocationId,
            operation,
            notifyHooks: true);

    private NativeOperationToken EnterOperationCore(
        NativeGeneration generation,
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        string operation,
        bool notifyHooks)
    {
        if (notifyHooks)
        {
            NativeMemoryTestHooks.NotifyBeforeOperationEntry(
                operation,
                this);
        }

        NativeOperationToken token;
        bool enterAllocation = _kind == NativeOwnerKind.Pool;
        if (IsDirectHandleActive(
                generation,
                allocation,
                generationNumber,
                allocationId)
            && generation.TryEnterOperation())
        {
            if (!enterAllocation)
            {
                if (IsDirectHandleActive(
                        generation,
                        allocation,
                        generationNumber,
                        allocationId))
                {
                    token = new NativeOperationToken(
                        this,
                        generation,
                        allocation,
                        allocationEntered: false,
                        operation);
                    goto Entered;
                }
            }
            else if (allocation.TryEnterOperation())
            {
                if (IsDirectHandleActive(
                        generation,
                        allocation,
                        generationNumber,
                        allocationId))
                {
                    token = new NativeOperationToken(
                        this,
                        generation,
                        allocation,
                        allocationEntered: true,
                        operation);
                    goto Entered;
                }

                allocation.ExitOperation();
            }

            ExitGenerationOperation(generation);
        }

        token = EnterOperationSlow(
            generation,
            allocation,
            generationNumber,
            allocationId,
            operation);

    Entered:
        if (!notifyHooks)
        {
            return token;
        }

        try
        {
            NativeMemoryTestHooks.NotifyOperationEntered(
                operation,
                this,
                generation.Owner,
                generationNumber,
                allocationId);
        }
        catch
        {
            token.Dispose();
            throw;
        }

        return token;
    }

    private NativeOperationToken EnterOperationSlow(
        NativeGeneration expectedGeneration,
        NativeAllocation expectedAllocation,
        long generationNumber,
        long allocationId,
        string operation)
    {
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked(
                operation,
                generationNumber,
                allocationId);
            if (!ReferenceEquals(generation, expectedGeneration)
                || !IsDirectHandleActive(
                    generation,
                    expectedAllocation,
                    generationNumber,
                    allocationId))
            {
                throw CreateReturnedException(
                    operation,
                    generationNumber,
                    _generation,
                    allocationId,
                    "The handle metadata was reused by a later allocation.");
            }

            NativeAllocation allocation = expectedAllocation;
            if (!generation.TryEnterOperation())
            {
                throw CreateInUseException(
                    operation,
                    generationNumber,
                    allocationId,
                    generation.ActiveOperations,
                    "The generation does not accept a new native operation.");
            }

            if (_kind != NativeOwnerKind.Pool)
            {
                return new NativeOperationToken(
                    this,
                    generation,
                    allocation,
                    allocationEntered: false,
                    operation);
            }

            if (allocation.TryEnterOperation())
            {
                return new NativeOperationToken(
                    this,
                    generation,
                    allocation,
                    allocationEntered: true,
                    operation);
            }

            ExitGenerationOperation(generation);
            throw CreateInUseException(
                operation,
                generationNumber,
                allocationId,
                allocation.ActiveOperations,
                "The allocation does not accept a new native operation.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsDirectHandleActive(
        NativeGeneration generation,
        NativeAllocation allocation,
        long generationNumber,
        long allocationId) =>
        ReferenceEquals(Volatile.Read(ref _current), generation)
        && generation.Number == generationNumber
        && allocation.Id == allocationId
        && allocation.Lifecycle == NativeAllocationLifecycle.Active
        && (!allocation.IsScoped
            || allocation.ScopeEpoch == generation.ScopeEpoch);

    internal NativeCompositeOperationToken EnterCompositeOperation(
        NativeGeneration generation,
        NativeCompositeAllocationBuffer allocations,
        long generationNumber,
        scoped ReadOnlySpan<long> allocationIds,
        string operation)
    {
        if (allocationIds.IsEmpty || allocationIds.Length > NativeCompositeAllocationBuffer.Capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(allocationIds),
                $"A composite native operation requires between one and {NativeCompositeAllocationBuffer.Capacity} allocations.");
        }

        NativeMemoryTestHooks.NotifyBeforeOperationEntry(operation, this);
        bool enterAllocations = _kind == NativeOwnerKind.Pool;
        if (!TryEnterCompositeFast(
                generation,
                ref allocations,
                generationNumber,
                allocationIds,
                enterAllocations))
        {
            EnterCompositeSlow(
                generation,
                ref allocations,
                generationNumber,
                allocationIds,
                operation,
                enterAllocations);
        }

        NativeCompositeOperationToken token = new(
            this,
            generation,
            allocations,
            allocationIds.Length,
            enterAllocations);
        if (!NativeMemoryTestHooks.OperationHooksEnabled)
        {
            return token;
        }

        try
        {
            for (int index = 0; index < allocationIds.Length; index++)
            {
                NativeMemoryTestHooks.NotifyOperationEntered(
                    operation,
                    this,
                    generation.Owner,
                    generationNumber,
                    allocationIds[index]);
            }
        }
        catch
        {
            token.Dispose();
            throw;
        }

        return token;
    }

    private bool TryEnterCompositeFast(
        NativeGeneration generation,
        ref NativeCompositeAllocationBuffer allocations,
        long generationNumber,
        scoped ReadOnlySpan<long> allocationIds,
        bool enterAllocations)
    {
        if (!generation.TryEnterOperation())
        {
            return false;
        }

        if (!enterAllocations)
        {
            for (int index = 0;
                index < allocationIds.Length;
                index++)
            {
                if (!IsDirectHandleActive(
                        generation,
                        allocations[index]!,
                        generationNumber,
                        allocationIds[index]))
                {
                    ExitGenerationOperation(generation);
                    return false;
                }
            }

            return true;
        }

        int enteredCount = 0;
        for (int index = 0; index < allocationIds.Length; index++)
        {
            NativeAllocation? allocation = allocations[index];
            if (allocation is null)
            {
                ExitCompositeAdmissionFailure(
                    generation,
                    ref allocations,
                    enteredCount);
                return false;
            }

            if (!allocation.TryEnterOperation())
            {
                ExitCompositeAdmissionFailure(
                    generation,
                    ref allocations,
                    enteredCount);
                return false;
            }

            enteredCount++;
            if (!IsDirectHandleActive(
                    generation,
                    allocation,
                    generationNumber,
                    allocationIds[index]))
            {
                ExitCompositeAdmissionFailure(
                    generation,
                    ref allocations,
                    enteredCount);
                return false;
            }
        }

        return true;
    }

    private void EnterCompositeSlow(
        NativeGeneration expectedGeneration,
        ref NativeCompositeAllocationBuffer allocations,
        long generationNumber,
        scoped ReadOnlySpan<long> allocationIds,
        string operation,
        bool enterAllocations)
    {
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked(
                operation,
                generationNumber,
                allocationIds[0]);
            if (!ReferenceEquals(generation, expectedGeneration))
            {
                throw CreateReturnedException(
                    operation,
                    generationNumber,
                    _generation,
                    allocationIds[0],
                    "The handle generation has already ended.");
            }

            for (int index = 0; index < allocationIds.Length; index++)
            {
                NativeAllocation? allocation =
                    allocations[index];
                if (allocation is null
                    || !IsDirectHandleActive(
                        generation,
                        allocation,
                        generationNumber,
                        allocationIds[index]))
                {
                    throw CreateReturnedException(
                        operation,
                        generationNumber,
                        _generation,
                        allocationIds[index],
                        "The handle metadata was reused by a later allocation.");
                }
            }

            if (!generation.TryEnterOperation())
            {
                throw CreateInUseException(
                    operation,
                    generationNumber,
                    allocationIds[0],
                    generation.ActiveOperations,
                    "The generation does not accept a composite native operation.");
            }

            if (!enterAllocations)
            {
                return;
            }

            int enteredCount = 0;
            try
            {
                for (int index = 0;
                    index < allocationIds.Length;
                    index++)
                {
                    NativeAllocation allocation =
                        allocations[index]!;
                    if (!allocation.TryEnterOperation())
                    {
                        throw CreateInUseException(
                            operation,
                            generationNumber,
                            allocationIds[index],
                            allocation.ActiveOperations,
                            "An allocation does not accept a composite native operation.");
                    }

                    enteredCount++;
                }
            }
            catch
            {
                ExitCompositeAdmissionFailure(
                    generation,
                    ref allocations,
                    enteredCount);
                throw;
            }
        }
    }

    private void ExitCompositeAdmissionFailure(
        NativeGeneration generation,
        ref NativeCompositeAllocationBuffer allocations,
        int enteredCount)
    {
        for (int index = enteredCount - 1; index >= 0; index--)
        {
            allocations[index]!.ExitOperation();
        }

        ExitGenerationOperation(generation);
    }

    internal void ExitOperation(
        NativeGeneration generation,
        NativeAllocation allocation,
        bool allocationEntered,
        string operation)
    {
        _ = operation;
        if (allocationEntered)
        {
            allocation.ExitOperation();
        }

        ExitGenerationOperation(generation);
    }

    internal void ExitCompositeOperation(
        NativeGeneration generation,
        ref NativeCompositeAllocationBuffer allocations,
        int count,
        bool allocationsEntered)
    {
        for (int index = count - 1; index >= 0; index--)
        {
            NativeAllocation? allocation = allocations[index];
            allocations[index] = null;
            if (allocationsEntered && allocation is not null)
            {
                allocation.ExitOperation();
            }
        }

        ExitGenerationOperation(generation);
    }

    private void ExitGenerationOperation(NativeGeneration generation)
    {
        int remaining = generation.ExitOperation();
        if (remaining != 0
            || (!generation.MemoryDetached
                && ReferenceEquals(
                    Volatile.Read(ref _current),
                    generation)))
        {
            return;
        }

        lock (_gate)
        {
            FinishOperationExitLocked(generation);
        }
    }

    private void FinishOperationExitLocked(NativeGeneration generation)
    {
        if (generation.MemoryDetached)
        {
            if (generation.ActiveOperations == 0)
            {
                DrainDetachedGenerationLocked(generation);
            }

            return;
        }

        if (generation != _current
            && generation.ActiveOperations == 0
            && _retiredGenerations.Contains(generation))
        {
            DrainRetiredGenerationLocked(generation);
        }
    }

    internal void ReturnLease(
        long generationNumber,
        long allocationId,
        string operation = "Pooled.Dispose")
    {
        lock (_gate)
        {
            if (_lifecycle is NativeOwnerLifecycle.Disposed or NativeOwnerLifecycle.Returned or NativeOwnerLifecycle.Unleased)
            {
                return;
            }

            if (_lifecycle is NativeOwnerLifecycle.Returning or NativeOwnerLifecycle.RollingOver)
            {
                return;
            }

            NativeGeneration generation = _current!;
            if (generation.Number != generationNumber || !generation.Allocations.TryGetValue(allocationId, out NativeAllocation? allocation))
            {
                return;
            }

            if (allocation.Lifecycle != NativeAllocationLifecycle.Active)
            {
                return;
            }

            if (allocation.IsScoped
                && allocation.ScopeEpoch != generation.ScopeEpoch)
            {
                return;
            }

            if (!allocation.IsScoped)
            {
                generation.ReusableAllocations.EnsureCapacity(
                    checked(generation.ReusableAllocations.Count + 1));
            }

            if (allocation.Slab is not null && allocation.Length > 0)
            {
                generation.AvailableSlabs.EnsureCapacity(checked(generation.AvailableSlabs.Count + 1));
            }

            allocation.ReferenceRoots?.ReserveForClear(ClearSlotCount(allocation));
            int activeOperations =
                allocation.CloseOperationAdmission();
            if (activeOperations != 0)
            {
                allocation.OpenOperationAdmission();
                throw CreateInUseException(
                    operation,
                    generation.Number,
                    allocationId,
                    activeOperations,
                    "The lease has an active native operation. No storage was cleared or requeued.");
            }

            allocation.Lifecycle = NativeAllocationLifecycle.Returning;
            generation.LeaseReturnsInProgress++;
            try
            {
                if (allocation.ReferenceRoots is not null)
                {
                    EnsureNoInjectedClearFailureLocked(
                        operation,
                        afterStateChange: true);
                    ClearReferenceStorage(allocation);
                }

                if (allocation.Slab is not null && allocation.Length > 0)
                {
                    generation.AddAvailableSlabOrdered(allocation.Slab);
                }

                allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                generation.Allocations.Remove(allocation.Id);
                if (!allocation.IsScoped)
                {
                    generation.ReusableAllocations.Add(allocation);
                }
            }
            catch
            {
                allocation.Lifecycle = NativeAllocationLifecycle.Active;
                allocation.OpenOperationAdmission();
                throw;
            }
            finally
            {
                generation.LeaseReturnsInProgress--;
            }
        }
    }

    internal void ReturnMemoryToNativeMemory() => ReturnMemory(NativeMemoryReturn.ToNativeMemory, "ReturnMemoryToNativeMemory");

    internal void ReturnMemoryToGarbageCollector() => ReturnMemory(NativeMemoryReturn.ToGarbageCollector, "ReturnMemoryToGarbageCollector");

    internal void ReleaseLeasesToNativeMemory() => ReleaseLeases(NativeMemoryReturn.ToNativeMemory, "ReleaseLeasesToNativeMemory");

    internal void ReleaseLeasesToGarbageCollector() => ReleaseLeases(NativeMemoryReturn.ToGarbageCollector, "ReleaseLeasesToGarbageCollector");

    internal void LeaseFromMemory()
    {
        lock (_gate)
        {
            EnsureNotDisposedLocked("LeaseFromMemory", 0);
            if (_lifecycle == NativeOwnerLifecycle.Active)
            {
                throw CreateStateException("LeaseFromMemory", "The owner is already active; return its current generation first.", 0);
            }

            if (_lifecycle is NativeOwnerLifecycle.Returning or NativeOwnerLifecycle.RollingOver)
            {
                throw CreateInUseException("LeaseFromMemory", _generation, 0, 0, "The owner is in a lifecycle transition.");
            }

            if (_kind == NativeOwnerKind.Region && _lifecycle == NativeOwnerLifecycle.Returned)
            {
                throw CreateStateException("LeaseFromMemory", "NativeRegion is a single-generation lexical owner and cannot be re-leased.", 0);
            }

            if (_lifecycle is not (NativeOwnerLifecycle.Unleased or NativeOwnerLifecycle.Returned))
            {
                throw CreateStateException("LeaseFromMemory", "The owner has no leasable generation.", 0);
            }

            NativeOwnerLifecycle observed = _lifecycle;
            NativeGeneration candidate = new(_generation);
            try
            {
                ReserveInitialStorageLocked(candidate, "activation reservation", observed);
            }
            catch
            {
                candidate.Owner.ReleaseToNative();
                throw;
            }

            _current = candidate;
            _lifecycle = NativeOwnerLifecycle.Active;
        }
    }

    internal void RecycleScoped()
    {
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked("RecycleScoped");
            if (generation.ScopedRecordCount == 0
                && generation.ScopedTouchedSegments.Count == 0)
            {
                return;
            }

            int activeOperations =
                generation.CloseOperationAdmission();
            try
            {
                if (activeOperations != 0
                    || generation.LeaseReturnsInProgress != 0
                    || generation.InitializationsInProgress != 0)
                {
                    throw CreateInUseException(
                        "RecycleScoped",
                        generation.Number,
                        0,
                        activeOperations,
                        "A scoped allocation is still entered by a native operation.");
                }

                long nextScopeEpoch =
                    checked(generation.ScopeEpoch + 1);
                int referenceClearCount =
                    PrepareScopedCleanupLocked(generation);
                PreflightCommitBoundaries(
                    "RecycleScoped",
                    generation.ScopedRecordCount,
                    slabTransferCount: 0,
                    bumpTransferCount: 0);
                if (referenceClearCount != 0)
                {
                    EnsureNoInjectedClearFailureLocked(
                        "RecycleScoped");
                }

                foreach (NativeAllocation allocation in
                    generation.ScopedCleanupPending)
                {
                    if (!IsCurrentAllocation(
                            generation,
                            allocation))
                    {
                        continue;
                    }

                    ClearReferenceStorage(allocation);
                    if (allocation.Slab is not null
                        && allocation.Length > 0)
                    {
                        generation.AddAvailableSlabOrdered(
                            allocation.Slab);
                    }
                }

                foreach (NativeBumpSegment segment in
                    generation.ScopedTouchedSegments)
                {
                    if (segment.PendingScopeEpoch
                        == generation.ScopeEpoch)
                    {
                        segment.MarkReclaimedRange(
                            nextScopeEpoch,
                            segment.PendingScopeRangeStart,
                            segment.PendingScopeRangeEnd);
                        segment.HighCursor =
                            segment.PendingScopeRangeEnd;
                    }

                    segment.ClearPendingScopeRange();
                }

                generation.ScopedCleanupPending.Clear();
                generation.ScopedRecordCount = 0;
                generation.ScopedBaselineActive = false;
                generation.ScopedTouchedSegments.Clear();
                generation.ScopeEpoch = nextScopeEpoch;
                ResetBumpTraversal(generation);
            }
            finally
            {
                generation.OpenOperationAdmission();
            }
        }
    }

    internal nuint ReserveRetainedMemory(nuint byteLength)
    {
        ArgumentOutOfRangeException.ThrowIfZero(byteLength);
        lock (_gate)
        {
            if (!CanTrimLocked("ReserveRetainedMemory"))
            {
                return 0;
            }

            if (_kind is not (NativeOwnerKind.Region or NativeOwnerKind.Arena))
            {
                throw CreateStateException(
                    "ReserveRetainedMemory",
                    "Only a heterogeneous owner can reserve a byte segment.",
                    0);
            }

            NativeGeneration generation = _current!;
            if (!TryCloseForMaintenanceLocked(generation))
            {
                return 0;
            }

            try
            {
                generation.BumpSegments.EnsureCapacity(
                    checked(generation.BumpSegments.Count + 1));
                generation.Owner.PrepareAddSegmentCapacity(1);
                NativeSegment segment = NativeSegment.Allocate(
                    byteLength,
                    _ownerKind,
                    generation.Number,
                    "retained reservation",
                    _lifecycle,
                    zeroed: false);
                NativeBumpSegment bump = new(
                    segment,
                    NextSegmentOrdinalLocked());
                try
                {
                    AppendBumpSegmentLocked(generation, bump);
                    generation.Owner.AddSegment(segment);
                    _freshSegmentAllocationCount++;
                    return byteLength;
                }
                catch
                {
                    generation.BumpSegments.Remove(bump);
                    generation.Owner.RemoveSegment(segment);
                    segment.FreeNow();
                    ResetBumpTraversal(generation);
                    throw;
                }
            }
            finally
            {
                generation.OpenOperationAdmission();
            }
        }
    }

    internal nuint ReserveExternalMemory(
        SafeBuffer buffer,
        nuint byteOffset,
        nuint byteLength)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfZero(byteLength);
        lock (_gate)
        {
            if (!CanTrimLocked("ReserveExternalMemory"))
            {
                return 0;
            }

            if (_kind is not (NativeOwnerKind.Region or NativeOwnerKind.Arena))
            {
                throw CreateStateException(
                    "ReserveExternalMemory",
                    "Only a heterogeneous owner can reserve an external byte segment.",
                    0);
            }

            NativeGeneration generation = _current!;
            if (!TryCloseForMaintenanceLocked(generation))
            {
                return 0;
            }

            try
            {
                generation.BumpSegments.EnsureCapacity(
                    checked(generation.BumpSegments.Count + 1));
                generation.Owner.PrepareAddSegmentCapacity(1);
                NativeSegment segment = NativeSegment.Borrow(
                    buffer,
                    byteOffset,
                    byteLength);
                NativeBumpSegment bump = new(
                    segment,
                    NextSegmentOrdinalLocked());
                try
                {
                    AppendBumpSegmentLocked(generation, bump);
                    generation.Owner.AddSegment(segment);
                    return byteLength;
                }
                catch
                {
                    generation.BumpSegments.Remove(bump);
                    generation.Owner.RemoveSegment(segment);
                    segment.FreeNow();
                    ResetBumpTraversal(generation);
                    throw;
                }
            }
            finally
            {
                generation.OpenOperationAdmission();
            }
        }
    }

    internal nuint TrimRetainedMemory()
    {
        lock (_gate)
        {
            if (!CanTrimLocked("TrimRetainedMemory"))
            {
                return 0;
            }

            NativeGeneration generation = _current!;
            if (!TryCloseForMaintenanceLocked(generation))
            {
                return 0;
            }

            try
            {
                nuint released = TrimRetainedMemoryLocked(null);
                _trimCallCount++;
                _trimmedBytes =
                    checked(_trimmedBytes + (long)released);
                return released;
            }
            finally
            {
                generation.OpenOperationAdmission();
            }
        }
    }

    internal nuint TrimRetainedMemoryByBytes(nuint bytesToRelease)
    {
        if (bytesToRelease == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesToRelease), "A zero trim request is not valid; use the parameterless trim operation.");
        }

        lock (_gate)
        {
            if (!CanTrimLocked("TrimRetainedMemoryByBytes"))
            {
                return 0;
            }

            NativeGeneration generation = _current!;
            if (!TryCloseForMaintenanceLocked(generation))
            {
                return 0;
            }

            try
            {
                nuint released =
                    TrimRetainedMemoryLocked(bytesToRelease);
                _trimCallCount++;
                _trimmedBytes =
                    checked(_trimmedBytes + (long)released);
                return released;
            }
            finally
            {
                generation.OpenOperationAdmission();
            }
        }
    }

    internal nuint TrimRetainedMemoryByLeaseSize(
        int leaseLength,
        int elementSize,
        nuint alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseLength);
        ArgumentOutOfRangeException.ThrowIfZero(alignment);
        lock (_gate)
        {
            if (!CanTrimLocked("TrimRetainedMemoryByLeaseSize"))
            {
                return 0;
            }

            NativeGeneration generation = _current!;
            if (!TryCloseForMaintenanceLocked(generation))
            {
                return 0;
            }

            try
            {
                nuint byteLength = CalculateByteLength(
                    leaseLength,
                    elementSize,
                    "lease-sized trim");
                nuint requested = _kind == NativeOwnerKind.Pool
                    ? byteLength
                    : ChooseBumpSegmentBytes(
                        generation,
                        RequiredFreshBumpBytes(byteLength, alignment));
                nuint released =
                    TrimRetainedMemoryLocked(requested);
                _trimCallCount++;
                _trimmedBytes =
                    checked(_trimmedBytes + (long)released);
                return released;
            }
            finally
            {
                generation.OpenOperationAdmission();
            }
        }
    }

    internal void Dispose()
    {
        lock (_gate)
        {
            if (_lifecycle == NativeOwnerLifecycle.Disposed)
            {
                return;
            }

            if (_lifecycle is NativeOwnerLifecycle.Returning or NativeOwnerLifecycle.RollingOver)
            {
                throw CreateInUseException("Dispose", _generation, 0, 0, "Another lifecycle transition is already using the owner gate.");
            }

            NativeGeneration? admissionGeneration = _current;
            admissionGeneration?.CloseOperationAdmission();
            try
            {
                EnsureOwnerWideIdleLocked(
                    "Dispose",
                    "No current, retired, or quarantined generation was invalidated or freed.");

                if (_lifecycle == NativeOwnerLifecycle.Unleased)
                {
                    if (_returnMemoryOnDispose == NativeMemoryReturn.ToNativeMemory)
                    {
                        ReleaseOwnerWideStorageLocked();
                    }
                    else
                    {
                        DetachOwnerWideStorageLocked();
                    }

                    _current = null;
                    _retiredGenerations.Clear();
                    _quarantinedGenerations.Clear();
                    _lifecycle = NativeOwnerLifecycle.Disposed;
                    GC.SuppressFinalize(this);
                    return;
                }

                if (_lifecycle == NativeOwnerLifecycle.Returned)
                {
                    EnsureNoInjectedClearFailureLocked("Dispose");
                    PrepareOwnerWideClearLocked(skipActiveOperations: false);
                    PreflightCommitBoundaries(
                        "Dispose",
                        CountOwnerWideClearableAllocationsLocked(skipActiveOperations: false),
                        slabTransferCount: 0,
                        bumpTransferCount: 0);
                    ClearOwnerWideAllocationsLocked(skipActiveOperations: false);
                    _lifecycle = NativeOwnerLifecycle.Returning;
                    InvalidateOwnerWideGenerationsLocked();
                    if (_returnMemoryOnDispose == NativeMemoryReturn.ToNativeMemory)
                    {
                        ReleaseOwnerWideStorageLocked();
                    }
                    else
                    {
                        DetachOwnerWideStorageLocked();
                    }

                    _current = null;
                    _retiredGenerations.Clear();
                    _quarantinedGenerations.Clear();
                    _lifecycle = NativeOwnerLifecycle.Disposed;
                    GC.SuppressFinalize(this);
                    return;
                }

                long nextGenerationNumber = checked(_generation + 1);
                EnsureNoInjectedClearFailureLocked("Dispose");
                PrepareOwnerWideClearLocked(skipActiveOperations: false);
                PreflightCommitBoundaries(
                    "Dispose",
                    CountOwnerWideClearableAllocationsLocked(skipActiveOperations: false),
                    slabTransferCount: 0,
                    bumpTransferCount: 0);
                ClearOwnerWideAllocationsLocked(skipActiveOperations: false);
                _lifecycle = NativeOwnerLifecycle.Returning;
                InvalidateOwnerWideGenerationsLocked();
                if (_returnMemoryOnDispose == NativeMemoryReturn.ToNativeMemory)
                {
                    ReleaseOwnerWideStorageLocked();
                }
                else
                {
                    DetachOwnerWideStorageLocked();
                }

                _current = null;
                _generation = nextGenerationNumber;
                _retiredGenerations.Clear();
                _quarantinedGenerations.Clear();
                _lifecycle = NativeOwnerLifecycle.Disposed;
                GC.SuppressFinalize(this);
            }
            catch
            {
                if (admissionGeneration is not null
                    && ReferenceEquals(_current, admissionGeneration)
                    && _lifecycle == NativeOwnerLifecycle.Active)
                {
                    admissionGeneration.OpenOperationAdmission();
                }

                throw;
            }
        }
    }

    private void ReserveInitialStorageLocked(NativeGeneration generation, string operation, NativeOwnerLifecycle observedLifecycle)
    {
        if (_kind == NativeOwnerKind.Pool && _initialReservation > 0)
        {
            int capacity = checked((int)_initialReservation);
            generation.Slabs.EnsureCapacity(checked(generation.Slabs.Count + 1));
            generation.AvailableSlabs.EnsureCapacity(checked(generation.AvailableSlabs.Count + 1));
            generation.Owner.PrepareAddSegmentCapacity(1);
            NativeSlab slab = AddPoolSlabLocked(generation, capacity, operation, observedLifecycle);
            generation.AddSlabOrdered(slab);
            generation.AddAvailableSlabOrdered(slab);
        }
        else if (_kind is NativeOwnerKind.Region or NativeOwnerKind.Arena && _initialReservation > 0)
        {
            generation.BumpSegments.EnsureCapacity(checked(generation.BumpSegments.Count + 1));
            generation.Owner.PrepareAddSegmentCapacity(1);
            NativeSegment segment = NativeSegment.Allocate(
                _initialReservation,
                _ownerKind,
                generation.Number,
                operation,
                observedLifecycle,
                zeroed: false);
            NativeBumpSegment bump = new(segment, NextSegmentOrdinalLocked());
            AppendBumpSegmentLocked(generation, bump);
            generation.Owner.AddSegment(segment);
            _freshSegmentAllocationCount++;
        }
    }

    private NativeSlab AddPoolSlabLocked(NativeGeneration generation, int capacity, string operation, NativeOwnerLifecycle observedLifecycle)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        nuint byteLength = CalculateByteLength(capacity, _storageElementSize, operation);
        NativeSegment segment = NativeSegment.Allocate(
            byteLength,
            _ownerKind,
            generation.Number,
            operation,
            observedLifecycle,
            zeroed: false);
        try
        {
            generation.Owner.AddSegment(segment);
        }
        catch
        {
            segment.FreeNow();
            throw;
        }

        try
        {
            NativeSlab slab = new(segment, capacity, _containsReferences, NextSegmentOrdinalLocked());
            _freshSegmentAllocationCount++;
            return slab;
        }
        catch
        {
            generation.Owner.RemoveSegment(segment);
            segment.FreeNow();
            throw;
        }
    }

    private NativeSlab? TakeSmallestAvailableSlabLocked(NativeGeneration generation, int length)
    {
        NativeSlab? best = null;
        int bestIndex = -1;
        for (int index = 0; index < generation.AvailableSlabs.Count; index++)
        {
            NativeSlab candidate = generation.AvailableSlabs[index];
            if (candidate.Capacity < length || (best is not null && candidate.Capacity >= best.Capacity))
            {
                continue;
            }

            best = candidate;
            bestIndex = index;
        }

        if (bestIndex >= 0)
        {
            generation.AvailableSlabs.RemoveAt(bestIndex);
        }

        return best;
    }

    private NativeBumpSegment? FindBumpSpaceLocked(NativeGeneration generation, nuint byteLength, nuint alignment, bool scoped)
    {
        if (generation.BumpSegments.Count == 0)
        {
            return null;
        }

        if (scoped)
        {
            int start = Math.Min(
                Math.Max(generation.ScopedBumpTraversalIndex, 0),
                generation.BumpSegments.Count - 1);
            for (int index = start; index >= 0; index--)
            {
                NativeMemoryTestHooks.RecordBumpTraversalVisit();
                NativeBumpSegment segment = generation.BumpSegments[index];
                if (segment.HighCursor < segment.LowCursor || byteLength > segment.HighCursor - segment.LowCursor)
                {
                    continue;
                }

                nuint candidate = AlignDown(segment.HighCursor - byteLength, alignment);
                if (candidate >= segment.LowCursor && byteLength <= segment.HighCursor - candidate)
                {
                    generation.ScopedBumpTraversalIndex = index;
                    return segment;
                }
            }
        }
        else
        {
            int start = Math.Min(
                Math.Max(generation.OrdinaryBumpTraversalIndex, 0),
                generation.BumpSegments.Count - 1);
            for (int index = start; index < generation.BumpSegments.Count; index++)
            {
                NativeMemoryTestHooks.RecordBumpTraversalVisit();
                NativeBumpSegment segment = generation.BumpSegments[index];
                nuint candidate = AlignUp(segment.LowCursor, alignment);
                if (candidate <= segment.HighCursor && byteLength <= segment.HighCursor - candidate)
                {
                    generation.OrdinaryBumpTraversalIndex = index;
                    return segment;
                }
            }
        }

        if (scoped)
        {
            generation.ScopedBumpTraversalIndex = -1;
        }
        else
        {
            generation.OrdinaryBumpTraversalIndex = generation.BumpSegments.Count;
        }

        return null;
    }

    private static void AppendBumpSegmentLocked(NativeGeneration generation, NativeBumpSegment segment)
    {
        int previousCount = generation.BumpSegments.Count;
        int newIndex = generation.AddBumpOrdered(segment);
        if (previousCount == 0 || generation.OrdinaryBumpTraversalIndex >= previousCount)
        {
            generation.OrdinaryBumpTraversalIndex = newIndex;
        }

        if (segment.AllocationOrdinal >= generation.BumpSegments[^1].AllocationOrdinal)
        {
            generation.ScopedBumpTraversalIndex = newIndex;
        }
    }

    private static void ResetBumpTraversal(NativeGeneration generation)
    {
        generation.OrdinaryBumpTraversalIndex = 0;
        generation.ScopedBumpTraversalIndex = generation.BumpSegments.Count - 1;
    }

    private long BeginScopedLocked(NativeGeneration generation)
    {
        if (!generation.ScopedBaselineActive)
        {
            generation.ScopedTouchedSegments.Clear();
            generation.ScopedBaselineActive = true;
        }

        return generation.ScopeEpoch;
    }

    private static void TrackScopedRangeLocked(
        NativeGeneration generation,
        NativeBumpSegment segment,
        nuint start,
        nuint originalHighCursor)
    {
        if (segment.PendingScopeEpoch == generation.ScopeEpoch)
        {
            segment.ExtendPendingScopeRange(start);
            return;
        }

        segment.BeginPendingScopeRange(generation.ScopeEpoch, start, originalHighCursor);
        generation.ScopedTouchedSegments.Add(segment);
    }

    private static void ClearScopedRangesLocked(NativeGeneration generation)
    {
        foreach (NativeBumpSegment segment in generation.ScopedTouchedSegments)
        {
            segment.ClearPendingScopeRange();
        }

        generation.ScopedTouchedSegments.Clear();
    }

    private void ReturnMemory(NativeMemoryReturn policy, string operation)
    {
        lock (_gate)
        {
            EnsureNotDisposedLocked(operation, 0);
            if (_lifecycle == NativeOwnerLifecycle.Returned)
            {
                throw CreateReturnedException(operation, _generation, _generation, 0, "The current generation has already been returned.");
            }

            if (_lifecycle == NativeOwnerLifecycle.Unleased)
            {
                throw CreateStateException(operation, "The owner has not been leased; activate it with LeaseFromMemory first.", 0);
            }

            if (_lifecycle is NativeOwnerLifecycle.Returning or NativeOwnerLifecycle.RollingOver)
            {
                throw CreateInUseException(operation, _generation, 0, 0, "Another lifecycle transition is already using the owner gate.");
            }

            bool tolerant = policy == NativeMemoryReturn.ToGarbageCollector;
            NativeGeneration generation = _current!;
            generation.CloseOperationAdmission();
            bool completed = false;
            try
            {
                if (tolerant)
                {
                    EnsureOwnerWideLeaseReturnsIdleLocked(operation, "No individual lease return was left in progress.");
                }
                else
                {
                    EnsureOwnerWideIdleLocked(operation, "No lease was invalidated, detached, or freed.");
                }
                long nextGenerationNumber = checked(_generation + 1);
                EnsureNoInjectedClearFailureLocked(operation);
                NativeSlab[] retiredSlabs = SnapshotRetiredSlabsLocked(generation, operation);
                NativeBumpSegment[] retiredBumps = SnapshotRetiredBumpsLocked(generation, operation);
                PrepareRetiredStorageLocked(
                    generation,
                    retiredSlabs,
                    retiredBumps,
                    operation,
                    reserveQuarantineSlot: tolerant && generation.ActiveOperations != 0);
                if (tolerant && generation.ActiveOperations != 0)
                {
                    _retiredGenerations.EnsureCapacity(checked(_retiredGenerations.Count + 1));
                }

                if (!tolerant)
                {
                    PrepareOwnerWideClearLocked(skipActiveOperations: false);
                    PreflightCommitBoundaries(
                        operation,
                        CountOwnerWideClearableAllocationsLocked(skipActiveOperations: false),
                        slabTransferCount: 0,
                        bumpTransferCount: 0);
                    ClearOwnerWideAllocationsLocked(skipActiveOperations: false);
                    _lifecycle = NativeOwnerLifecycle.Returning;
                    InvalidateOwnerWideGenerationsLocked();
                    ReleaseOwnerWideStorageLocked();
                    _current = null;
                    _generation = nextGenerationNumber;
                    _retiredGenerations.Clear();
                    _quarantinedGenerations.Clear();
                    _lifecycle = NativeOwnerLifecycle.Returned;
                    completed = true;
                    return;
                }

                bool deferAllCleanup =
                    generation.ActiveOperations != 0;
                PreflightCommitBoundaries(
                    operation,
                    deferAllCleanup
                        ? 0
                        : CountClearableAllocations(
                            generation,
                            skipActiveOperations: false),
                    slabTransferCount: 0,
                    bumpTransferCount: 0);
                if (!deferAllCleanup)
                {
                    ClearGenerationAllocationsLocked(
                        generation,
                        skipActiveOperations: false);
                }

                _lifecycle = NativeOwnerLifecycle.Returning;
                InvalidateOwnerWideGenerationsLocked();
                DetachOwnerWideStorageLocked();
                _current = null;
                _generation = nextGenerationNumber;
                _lifecycle = NativeOwnerLifecycle.Returned;
                completed = true;
            }
            finally
            {
                if (!completed
                    && ReferenceEquals(_current, generation)
                    && _lifecycle == NativeOwnerLifecycle.Active)
                {
                    generation.OpenOperationAdmission();
                }
            }
        }
    }

    private void ReleaseLeases(NativeMemoryReturn policy, string operation)
    {
        lock (_gate)
        {
            EnsureNotDisposedLocked(operation, 0);
            if (_kind == NativeOwnerKind.Region)
            {
                throw CreateStateException(operation, "NativeRegion has one lexical generation and does not release leases.", 0);
            }

            NativeGeneration current = EnsureActiveLocked(operation);
            current.CloseOperationAdmission();
            bool completed = false;
            try
            {
                bool tolerant =
                    policy == NativeMemoryReturn.ToGarbageCollector;
                if (current.LeaseReturnsInProgress != 0
                    || current.InitializationsInProgress != 0
                    || (!tolerant
                        && current.ActiveOperations != 0))
                {
                    throw CreateInUseException(operation, current.Number, 0, current.ActiveOperations, "No lease generation was invalidated or reset.");
                }

                EnsureNoInjectedClearFailureLocked(operation);
                bool retainAllSegments =
                    tolerant && current.ActiveOperations != 0;
                long nextGenerationNumber = checked(current.Number + 1);
                NativeSlab[] slabs = SnapshotRetiredSlabsLocked(current, operation);
                NativeBumpSegment[] bumps = SnapshotRetiredBumpsLocked(current, operation);
                NativeGeneration next = new(nextGenerationNumber);
                try
                {
                    int slabTransferCount = 0;
                    foreach (NativeSlab slab in slabs)
                    {
                        if (!retainAllSegments
                            && !IsSegmentBusy(current, slab))
                        {
                            slabTransferCount++;
                        }
                    }

                    int bumpTransferCount = 0;
                    foreach (NativeBumpSegment bump in bumps)
                    {
                        if (!retainAllSegments
                            && !IsSegmentBusy(current, bump))
                        {
                            bumpTransferCount++;
                        }
                    }

                    NativeSlab[] retiredSlabs = CreateRetiredSlabSnapshot(slabs, slabTransferCount, current);
                    NativeBumpSegment[] retiredBumps = CreateRetiredBumpSnapshot(bumps, bumpTransferCount, current);
                    PrepareRetiredStorageLocked(
                        current,
                        retiredSlabs,
                        retiredBumps,
                        operation,
                        reserveQuarantineSlot: retainAllSegments);
                    (int rejoinableSlabs, int rejoinableBumps) = CountPotentialRejoinSegmentsLocked(
                        retiredSlabs,
                        retiredBumps);
                    next.Slabs.EnsureCapacity(checked(slabTransferCount + rejoinableSlabs));
                    next.AvailableSlabs.EnsureCapacity(checked(slabTransferCount + rejoinableSlabs));
                    next.BumpSegments.EnsureCapacity(checked(bumpTransferCount + rejoinableBumps));
                    next.Owner.PrepareAddSegmentCapacity(
                        checked(slabTransferCount + bumpTransferCount + rejoinableSlabs + rejoinableBumps));
                    if (retainAllSegments)
                    {
                        _retiredGenerations.EnsureCapacity(checked(_retiredGenerations.Count + 1));
                    }

                    PreflightCommitBoundaries(
                        operation,
                        retainAllSegments
                            ? 0
                            : CountClearableAllocations(
                                current,
                                skipActiveOperations: false),
                        slabTransferCount,
                        bumpTransferCount);
                }
                catch
                {
                    next.Owner.ReleaseToNative();
                    throw;
                }

                _lifecycle = NativeOwnerLifecycle.RollingOver;
                try
                {
                    if (!retainAllSegments)
                    {
                        ClearGenerationAllocationsLocked(
                            current,
                            skipActiveOperations: false);
                    }

                    foreach (NativeSlab slab in slabs)
                    {
                        if (!retainAllSegments
                            && !IsSegmentBusy(current, slab))
                        {
                            TransferSegmentLocked(current, next, slab.Segment);
                            current.Slabs.Remove(slab);
                            current.AvailableSlabs.Remove(slab);
                            next.AddSlabOrdered(slab);
                            next.AddAvailableSlabOrdered(slab);
                        }
                    }

                    foreach (NativeBumpSegment bump in bumps)
                    {
                        if (!retainAllSegments
                            && !IsSegmentBusy(current, bump))
                        {
                            bump.LowCursor = 0;
                            bump.HighCursor = bump.Segment.ByteLength;
                            TransferSegmentLocked(current, next, bump.Segment);
                            current.BumpSegments.Remove(bump);
                            next.AddBumpOrdered(bump);
                        }
                    }

                    ResetBumpTraversal(next);

                    InvalidateGenerationLocked(current);
                    _current = next;
                    _generation = next.Number;
                    _lifecycle = NativeOwnerLifecycle.Active;
                    if (retainAllSegments)
                    {
                        current.RetiredNativeBytes =
                            checked((long)GetGenerationSegmentBytes(current));
                        if (current.RetiredNativeBytes != 0)
                        {
                            NativeMemoryTestHooks.RecordRetiredBytes((nuint)current.RetiredNativeBytes, add: true, metricsEpoch: current.Owner.MetricsEpoch);
                        }

                        _retiredGenerations.Add(current);
                        if (current.ActiveOperations == 0)
                        {
                            DrainRetiredGenerationLocked(current);
                        }
                    }
                    else
                    {
                        current.Owner.ReleaseToNative();
                    }

                    completed = true;
                }
                catch
                {
                    _lifecycle = NativeOwnerLifecycle.Active;
                    if (!ReferenceEquals(_current, next))
                    {
                        next.Owner.ReleaseToNative();
                    }
                    throw;
                }
            }
            finally
            {
                if (!completed
                    && ReferenceEquals(_current, current)
                    && _lifecycle == NativeOwnerLifecycle.Active)
                {
                    current.OpenOperationAdmission();
                }
            }
        }
    }

    private void DrainRetiredGenerationLocked(NativeGeneration generation)
    {
        if (generation.MemoryDetached)
        {
            DrainDetachedGenerationLocked(generation);
            return;
        }

        NativeSlab[] slabs = generation.RetiredSlabs;
        NativeBumpSegment[] bumps = generation.RetiredBumps;
        NativeGeneration? current = _current;
        string operation = "DrainRetiredGeneration";
        long failedAllocationId = 0;
        long failedSegmentOrdinal = 0;
        string failedBoundary = "retired generation drain";

        try
        {
            bool canRejoin = current is not null && _lifecycle != NativeOwnerLifecycle.Disposed;
            PreflightCommitBoundaries(
                operation,
                CountClearableAllocations(generation, skipActiveOperations: false),
                canRejoin ? slabs.Length : 0,
                canRejoin ? bumps.Length : 0);
            NativeMemoryTestHooks.BeginPostCommitSequence();

            foreach (NativeAllocation allocation in generation.Allocations.Values)
            {
                failedAllocationId = allocation.Id;
                failedSegmentOrdinal = GetSegmentOrdinal(allocation);
                failedBoundary = "clear";
                ClearAllocationStorageReserved(allocation);
                NativeMemoryTestHooks.CheckPostCommitBoundary(operation, "clear");
            }

            if (!canRejoin)
            {
                _retiredGenerations.Remove(generation);
                if (generation.RetiredNativeBytes != 0)
                {
                    NativeMemoryTestHooks.RecordRetiredBytes((nuint)generation.RetiredNativeBytes, add: false, metricsEpoch: generation.Owner.MetricsEpoch);
                    generation.RetiredNativeBytes = 0;
                }

                generation.Owner.ReleaseToNative();
                return;
            }

            foreach (NativeSlab slab in slabs)
            {
                failedSegmentOrdinal = slab.AllocationOrdinal;
                failedBoundary = "slab transfer";
                TransferSegmentLocked(generation, current!, slab.Segment);
                generation.Slabs.Remove(slab);
                generation.AvailableSlabs.Remove(slab);
                current!.AddSlabOrdered(slab);
                current.AddAvailableSlabOrdered(slab);
                NativeMemoryTestHooks.CheckPostCommitBoundary(operation, "slab transfer");
            }

            foreach (NativeBumpSegment bump in bumps)
            {
                failedSegmentOrdinal = bump.AllocationOrdinal;
                failedBoundary = "bump transfer";
                bump.LowCursor = 0;
                bump.HighCursor = bump.Segment.ByteLength;
                TransferSegmentLocked(generation, current!, bump.Segment);
                generation.BumpSegments.Remove(bump);
                current!.AddBumpOrdered(bump);
                NativeMemoryTestHooks.CheckPostCommitBoundary(operation, "bump transfer");
            }

            generation.AvailableSlabs.Clear();
            generation.BumpSegments.Clear();
            ResetBumpTraversal(current!);
            _retiredGenerations.Remove(generation);
            if (generation.RetiredNativeBytes != 0)
            {
                NativeMemoryTestHooks.RecordRetiredBytes((nuint)generation.RetiredNativeBytes, add: false, metricsEpoch: generation.Owner.MetricsEpoch);
                generation.RetiredNativeBytes = 0;
            }

            generation.Owner.ReleaseToNative();
        }
        catch (Exception exception)
        {
            _retiredGenerations.Remove(generation);
            QuarantineGenerationStorageLocked(generation, current, slabs, bumps);
            if (generation.RetiredNativeBytes != 0)
            {
                NativeMemoryTestHooks.RecordRetiredBytes((nuint)generation.RetiredNativeBytes, add: false, metricsEpoch: generation.Owner.MetricsEpoch);
                generation.RetiredNativeBytes = 0;
            }

            throw CreateQuarantinedException(
                operation,
                generation.Number,
                failedAllocationId,
                failedSegmentOrdinal,
                failedBoundary,
                exception);
        }
    }

    private void DrainDetachedGenerationLocked(NativeGeneration generation)
    {
        const string operation = "DrainDetachedGeneration";
        long failedAllocationId = 0;
        long failedSegmentOrdinal = 0;
        string failedBoundary = "detached clear";

        try
        {
            PreflightCommitBoundaries(
                operation,
                CountClearableAllocations(generation, skipActiveOperations: false),
                slabTransferCount: 0,
                bumpTransferCount: 0);
            NativeMemoryTestHooks.BeginPostCommitSequence();

            foreach (NativeAllocation allocation in generation.Allocations.Values)
            {
                failedAllocationId = allocation.Id;
                failedSegmentOrdinal = GetSegmentOrdinal(allocation);
                failedBoundary = "clear";
                ClearAllocationStorageReserved(allocation);
                allocation.Lifecycle = NativeAllocationLifecycle.Returned;
                NativeMemoryTestHooks.CheckPostCommitBoundary(operation, "clear");
            }

            generation.Allocations.Clear();
            generation.ScopedAllocationBank.Clear();
            generation.ScopedCleanupPending.Clear();
            generation.ScopedRecordCount = 0;
            generation.ScopedBaselineActive = false;
            ClearScopedRangesLocked(generation);
            if (generation.RetiredNativeBytes != 0)
            {
                NativeMemoryTestHooks.RecordRetiredBytes(
                    (nuint)generation.RetiredNativeBytes,
                    add: false,
                    metricsEpoch: generation.Owner.MetricsEpoch);
                generation.RetiredNativeBytes = 0;
            }
        }
        catch (Exception exception)
        {
            throw CreateDetachedCleanupException(
                operation,
                generation,
                failedAllocationId,
                failedSegmentOrdinal,
                failedBoundary,
                exception);
        }
    }

    private void TransferSegmentLocked(NativeGeneration source, NativeGeneration destination, NativeSegment segment)
    {
        destination.Owner.AddSegment(segment);
        source.Owner.RemoveSegment(segment);
    }

    private void QuarantineGenerationStorageLocked(
        NativeGeneration generation,
        NativeGeneration? current,
        NativeSlab[] slabs,
        NativeBumpSegment[] bumps)
    {
        foreach (NativeSlab slab in slabs)
        {
            generation.Slabs.Remove(slab);
            generation.AvailableSlabs.Remove(slab);
            generation.Owner.RemoveSegment(slab.Segment);
            current?.Slabs.Remove(slab);
            current?.AvailableSlabs.Remove(slab);
            current?.Owner.RemoveSegment(slab.Segment);
            generation.Owner.AddSegment(slab.Segment);
        }

        foreach (NativeBumpSegment bump in bumps)
        {
            generation.BumpSegments.Remove(bump);
            generation.Owner.RemoveSegment(bump.Segment);
            current?.BumpSegments.Remove(bump);
            current?.Owner.RemoveSegment(bump.Segment);
            generation.Owner.AddSegment(bump.Segment);
        }

        generation.AvailableSlabs.Clear();
        generation.BumpSegments.Clear();
        generation.IsQuarantined = true;
        _quarantinedGenerations.Add(generation);
        if (current is not null)
        {
            ResetBumpTraversal(current);
        }
    }

    private NativeAllocationQuarantinedException CreateQuarantinedException(
        string operation,
        long generation,
        long allocationId,
        long segmentOrdinal,
        string boundary,
        Exception innerException) =>
        new(
            $"{_ownerKind}.{operation} quarantined ended generation {generation} storage at segment {segmentOrdinal} after a cleanup failure at {boundary}. The storage remains physically owned but is removed from all reusable banks and will not be reused; dispose the owner to release it. (lifecycle {_lifecycle})",
            _ownerKind,
            generation,
            _generation,
            operation,
            ActiveOperationCountLocked(),
            allocationId,
            segmentOrdinal,
            boundary,
            _lifecycle,
            innerException);

    private NativeAllocationQuarantinedException CreateDetachedCleanupException(
        string operation,
        NativeGeneration generation,
        long allocationId,
        long segmentOrdinal,
        string boundary,
        Exception innerException) =>
        new(
            $"{_ownerKind}.{operation} could not clear detached generation {generation.Number} at {boundary}. The storage remains detached and finalizable; it is no longer owned by the allocator and will not be synchronously released or reused (lifecycle {_lifecycle}).",
            _ownerKind,
            generation.Number,
            _generation,
            operation,
            activeOperationCount: 0,
            allocationId,
            segmentOrdinal,
            boundary,
            _lifecycle,
            innerException);

    private static long GetSegmentOrdinal(NativeAllocation allocation) =>
        allocation.Slab?.AllocationOrdinal ?? allocation.BumpSegment?.AllocationOrdinal ?? 0;

    private void InvalidateGenerationLocked(NativeGeneration generation)
    {
        foreach (NativeAllocation allocation in generation.Allocations.Values)
        {
            allocation.Lifecycle = NativeAllocationLifecycle.Returned;
        }

        generation.AvailableSlabs.Clear();
        generation.ScopedAllocationBank.Clear();
        generation.ScopedCleanupPending.Clear();
        generation.ScopedRecordCount = 0;
        generation.ScopedBaselineActive = false;
        ClearScopedRangesLocked(generation);
    }

    private NativeGeneration EnsureActiveLocked(string operation, long generationNumber = 0, long allocationId = 0)
    {
        EnsureNotDisposedLocked(operation, allocationId);
        if (_lifecycle == NativeOwnerLifecycle.Returned)
        {
            throw CreateReturnedException(operation, generationNumber, _generation, allocationId, "The owner generation is returned.");
        }

        if (_lifecycle is NativeOwnerLifecycle.Returning or NativeOwnerLifecycle.RollingOver)
        {
            throw CreateInUseException(operation, generationNumber, allocationId, 0, "The owner is in a lifecycle transition and rejects new operations.");
        }

        if (_lifecycle == NativeOwnerLifecycle.Unleased)
        {
            throw CreateStateException(operation, "The owner has not been leased; activate it with LeaseFromMemory first.", allocationId);
        }

        return _current ?? throw new InvalidOperationException("The active native owner has no generation.");
    }

    private bool CanTrimLocked(string operation)
    {
        EnsureNotDisposedLocked(operation, 0);
        if (_lifecycle is NativeOwnerLifecycle.Unleased or NativeOwnerLifecycle.Returned)
        {
            return false;
        }

        if (_lifecycle is NativeOwnerLifecycle.Returning or NativeOwnerLifecycle.RollingOver)
        {
            throw CreateInUseException(operation, _generation, 0, ActiveOperationCountLocked(), "The owner is in a lifecycle transition and cannot be trimmed.");
        }

        return true;
    }

    private static bool TryCloseForMaintenanceLocked(
        NativeGeneration generation)
    {
        int activeOperations =
            generation.CloseOperationAdmission();
        if (activeOperations == 0
            && generation.LeaseReturnsInProgress == 0
            && generation.InitializationsInProgress == 0)
        {
            return true;
        }

        generation.OpenOperationAdmission();
        return false;
    }

    private void EnsureNotDisposedLocked(string operation, long allocationId)
    {
        if (_lifecycle == NativeOwnerLifecycle.Disposed)
        {
            throw new NativeAllocationDisposedException(
                $"{_ownerKind}.{operation} cannot run because the owner is permanently disposed (lifecycle {NativeOwnerLifecycle.Disposed}).",
                _ownerKind,
                _generation,
                _generation,
                operation,
                ActiveOperationCountLocked(),
                allocationId,
                NativeOwnerLifecycle.Disposed);
        }
    }

    private static bool IsCurrentAllocation(
        NativeGeneration generation,
        NativeAllocation allocation) =>
        allocation.Lifecycle == NativeAllocationLifecycle.Active
        && (!allocation.IsScoped
            || allocation.ScopeEpoch == generation.ScopeEpoch);

    private void ClearAllocationStorage(NativeAllocation allocation)
    {
        ClearReferenceStorage(allocation);
    }

    private static void ClearReferenceStorage(NativeAllocation allocation)
    {
        if (allocation.ReferenceRoots is not null)
        {
            allocation.ClearValues();
        }
    }

    private void ClearAllocationStorageReserved(NativeAllocation allocation)
    {
        if (allocation.ReferenceRoots is not null)
        {
            allocation.ClearValues(reservedRootCapacity: true);
        }
    }

    private static NativeSlab[] SnapshotRetiredSlabsLocked(NativeGeneration generation, string operation)
    {
        if (NativeMemoryTestHooks.ConsumeRetiredSnapshotPreparationFailure())
        {
            throw new InvalidOperationException($"Injected retired-generation snapshot preparation failure during {operation}.");
        }

        return generation.Slabs.ToArray();
    }

    private static NativeBumpSegment[] SnapshotRetiredBumpsLocked(NativeGeneration generation, string operation)
    {
        if (NativeMemoryTestHooks.ConsumeRetiredSnapshotPreparationFailure())
        {
            throw new InvalidOperationException($"Injected retired-generation snapshot preparation failure during {operation}.");
        }

        return generation.BumpSegments.ToArray();
    }

    private static NativeSlab[] CreateRetiredSlabSnapshot(
        NativeSlab[] slabs,
        int transferCount,
        NativeGeneration generation)
    {
        if (transferCount == 0)
        {
            return slabs;
        }

        int retiredCount = checked(slabs.Length - transferCount);
        if (retiredCount == 0)
        {
            return [];
        }

        NativeSlab[] retired = new NativeSlab[retiredCount];
        int index = 0;
        foreach (NativeSlab slab in slabs)
        {
            if (IsSegmentBusy(generation, slab))
            {
                retired[index++] = slab;
            }
        }

        return retired;
    }

    private static NativeBumpSegment[] CreateRetiredBumpSnapshot(
        NativeBumpSegment[] bumps,
        int transferCount,
        NativeGeneration generation)
    {
        if (transferCount == 0)
        {
            return bumps;
        }

        int retiredCount = checked(bumps.Length - transferCount);
        if (retiredCount == 0)
        {
            return [];
        }

        NativeBumpSegment[] retired = new NativeBumpSegment[retiredCount];
        int index = 0;
        foreach (NativeBumpSegment bump in bumps)
        {
            if (IsSegmentBusy(generation, bump))
            {
                retired[index++] = bump;
            }
        }

        return retired;
    }

    private (int Slabs, int Bumps) CountPotentialRejoinSegmentsLocked(
        NativeSlab[] newlyRetiredSlabs,
        NativeBumpSegment[] newlyRetiredBumps)
    {
        int slabs = newlyRetiredSlabs.Length;
        int bumps = newlyRetiredBumps.Length;
        foreach (NativeGeneration retired in _retiredGenerations)
        {
            if (retired.MemoryDetached)
            {
                continue;
            }

            slabs = checked(slabs + retired.RetiredSlabs.Length);
            bumps = checked(bumps + retired.RetiredBumps.Length);
        }

        return (slabs, bumps);
    }

    private void PrepareRetiredStorageLocked(
        NativeGeneration generation,
        NativeSlab[] retiredSlabs,
        NativeBumpSegment[] retiredBumps,
        string operation,
        bool reserveQuarantineSlot)
    {
        generation.RetiredSlabs = retiredSlabs;
        generation.RetiredBumps = retiredBumps;
        PrepareGenerationClearLocked(generation, skipActiveOperations: false);
        generation.Owner.PrepareAddSegmentCapacity(checked(retiredSlabs.Length + retiredBumps.Length));

        int outstandingQuarantineReservations = checked(
            _retiredGenerations.Count + (reserveQuarantineSlot ? 1 : 0));
        if (outstandingQuarantineReservations != 0)
        {
            if (reserveQuarantineSlot && NativeMemoryTestHooks.ConsumeQuarantineReservationFailure())
            {
                throw new InvalidOperationException($"Injected quarantine-slot reservation failure during {operation}.");
            }

            _quarantinedGenerations.EnsureCapacity(
                checked(_quarantinedGenerations.Count + outstandingQuarantineReservations));
        }
    }

    private static int ClearSlotCount(NativeAllocation allocation)
    {
        return allocation.ReferenceRoots is null || allocation.StorageBytes == 0
            ? 0
            : checked((int)(allocation.StorageBytes / (nuint)IntPtr.Size));
    }

    private static bool WillClear(NativeAllocation allocation, bool skipActiveOperations) =>
        !skipActiveOperations || allocation.ActiveOperations == 0;

    private void PrepareGenerationClearLocked(
        NativeGeneration generation,
        bool skipActiveOperations)
    {
        int slots = 0;
        int availableSlabs = 0;
        foreach (NativeAllocation allocation in generation.Allocations.Values)
        {
            if (!WillClear(allocation, skipActiveOperations))
            {
                continue;
            }

            slots = checked(slots + ClearSlotCount(allocation));
            if (allocation.Slab is not null && allocation.Length > 0)
            {
                availableSlabs++;
            }
        }

        generation.ReferenceRoots.ReserveForClear(slots);
        if (availableSlabs != 0)
        {
            generation.AvailableSlabs.EnsureCapacity(checked(generation.AvailableSlabs.Count + availableSlabs));
        }
    }

    private static int PrepareScopedCleanupLocked(
        NativeGeneration generation)
    {
        int slots = 0;
        int availableSlabs = 0;
        int referenceClearCount = 0;
        foreach (NativeAllocation allocation in
            generation.ScopedCleanupPending)
        {
            if (!IsCurrentAllocation(generation, allocation))
            {
                continue;
            }

            int allocationSlots = ClearSlotCount(allocation);
            if (allocationSlots != 0)
            {
                slots = checked(slots + allocationSlots);
                referenceClearCount++;
            }

            if (allocation.Slab is not null && allocation.Length > 0)
            {
                availableSlabs++;
            }
        }

        generation.ReferenceRoots.ReserveForClear(slots);
        if (availableSlabs != 0)
        {
            generation.AvailableSlabs.EnsureCapacity(
                checked(generation.AvailableSlabs.Count + availableSlabs));
        }

        return referenceClearCount;
    }

    private static int CountClearableAllocations(NativeGeneration generation, bool skipActiveOperations)
    {
        int count = 0;
        foreach (NativeAllocation allocation in generation.Allocations.Values)
        {
            if (WillClear(allocation, skipActiveOperations))
            {
                count++;
            }
        }

        return count;
    }

    private static void PreflightCommitBoundaries(
        string operation,
        int clearCount,
        int slabTransferCount,
        int bumpTransferCount)
    {
        if (!NativeMemoryTestHooks.HasForcedCommitBoundary)
        {
            return;
        }

        NativeMemoryTestHooks.BeginCommitSequence();
        for (int index = 0; index < clearCount; index++)
        {
            NativeMemoryTestHooks.CheckCommitBoundary(operation, "clear");
        }

        for (int index = 0; index < slabTransferCount; index++)
        {
            NativeMemoryTestHooks.CheckCommitBoundary(operation, "slab transfer");
        }

        for (int index = 0; index < bumpTransferCount; index++)
        {
            NativeMemoryTestHooks.CheckCommitBoundary(operation, "bump transfer");
        }
    }

    private void ClearGenerationAllocationsLocked(NativeGeneration generation, bool skipActiveOperations)
    {
        foreach (NativeAllocation allocation in generation.Allocations.Values)
        {
            if (allocation.ReferenceRoots is null
                || (skipActiveOperations
                    && allocation.ActiveOperations != 0))
            {
                continue;
            }

            ClearReferenceStorage(allocation);
        }
    }

    private int CountOwnerWideClearableAllocationsLocked(bool skipActiveOperations)
    {
        int count = 0;
        if (_current is not null && !_current.MemoryDetached)
        {
            count = checked(count + CountClearableAllocations(_current, skipActiveOperations));
        }

        foreach (NativeGeneration generation in _retiredGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            count = checked(count + CountClearableAllocations(generation, skipActiveOperations));
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            count = checked(count + CountClearableAllocations(generation, skipActiveOperations));
        }

        return count;
    }

    private void PrepareOwnerWideClearLocked(bool skipActiveOperations)
    {
        if (_current is not null && !_current.MemoryDetached)
        {
            PrepareGenerationClearLocked(_current, skipActiveOperations);
        }

        foreach (NativeGeneration generation in _retiredGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            PrepareGenerationClearLocked(generation, skipActiveOperations);
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            PrepareGenerationClearLocked(generation, skipActiveOperations);
        }
    }

    private void ClearOwnerWideAllocationsLocked(bool skipActiveOperations)
    {
        if (_current is not null && !_current.MemoryDetached)
        {
            ClearGenerationAllocationsLocked(_current, skipActiveOperations);
        }

        foreach (NativeGeneration generation in _retiredGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            ClearGenerationAllocationsLocked(generation, skipActiveOperations);
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            ClearGenerationAllocationsLocked(generation, skipActiveOperations);
        }
    }

    private void InvalidateOwnerWideGenerationsLocked()
    {
        if (_current is not null && !_current.MemoryDetached)
        {
            InvalidateGenerationLocked(_current);
        }

        foreach (NativeGeneration generation in _retiredGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            InvalidateGenerationLocked(generation);
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            InvalidateGenerationLocked(generation);
        }
    }

    private void ReleaseOwnerWideStorageLocked()
    {
        if (_current is not null && !_current.MemoryDetached)
        {
            ReleaseGenerationStorageLocked(_current);
        }

        foreach (NativeGeneration generation in _retiredGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            ReleaseGenerationStorageLocked(generation);
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            ReleaseGenerationStorageLocked(generation);
        }
    }

    private static void ReleaseGenerationStorageLocked(NativeGeneration generation)
    {
        if (generation.RetiredNativeBytes != 0)
        {
            NativeMemoryTestHooks.RecordRetiredBytes(
                (nuint)generation.RetiredNativeBytes,
                add: false,
                metricsEpoch: generation.Owner.MetricsEpoch);
            generation.RetiredNativeBytes = 0;
        }

        generation.Owner.ReleaseToNative();
    }

    private void DetachOwnerWideStorageLocked()
    {
        if (_current is not null)
        {
            _current.MemoryDetached = true;
            _current.Owner.Detach();
        }

        foreach (NativeGeneration generation in _retiredGenerations)
        {
            generation.MemoryDetached = true;
            generation.Owner.Detach();
            if (generation.ActiveOperations == 0 && generation.RetiredNativeBytes != 0)
            {
                NativeMemoryTestHooks.RecordRetiredBytes(
                    (nuint)generation.RetiredNativeBytes,
                    add: false,
                    metricsEpoch: generation.Owner.MetricsEpoch);
                generation.RetiredNativeBytes = 0;
            }
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            generation.MemoryDetached = true;
            generation.Owner.Detach();
            if (generation.ActiveOperations == 0 && generation.RetiredNativeBytes != 0)
            {
                NativeMemoryTestHooks.RecordRetiredBytes(
                    (nuint)generation.RetiredNativeBytes,
                    add: false,
                    metricsEpoch: generation.Owner.MetricsEpoch);
                generation.RetiredNativeBytes = 0;
            }
        }

        _retiredGenerations.Clear();
        _quarantinedGenerations.Clear();
    }

    private void EnsureNoInjectedClearFailureLocked(string operation, bool afterStateChange = false)
    {
        if (NativeMemoryTestHooks.ConsumeForcedClearFailure())
        {
            string timing = afterStateChange
                ? "after entering the reversible transition; the owner state will be rolled back"
                : "before the owner state changed";
            throw new InvalidOperationException($"Injected native clear failure during {operation} {timing}.");
        }
    }

    private nuint TrimRetainedMemoryLocked(nuint? requestedBytes)
    {
        nuint released = 0;
        if (_kind == NativeOwnerKind.Pool)
        {
            NativeGeneration current = _current!;
            for (int index = current.Slabs.Count - 1;
                index >= 0 && (requestedBytes is null || released < requestedBytes.Value);
                index--)
            {
                NativeSlab slab = current.Slabs[index];
                if (!current.AvailableSlabs.Remove(slab))
                {
                    continue;
                }

                foreach (NativeAllocation allocation in current.Allocations.Values
                    .Where(allocation => allocation.Lifecycle == NativeAllocationLifecycle.Returned
                        && ReferenceEquals(allocation.Slab, slab))
                    .ToArray())
                {
                    current.Allocations.Remove(allocation.Id);
                }

                current.Slabs.Remove(slab);
                current.Owner.RemoveSegment(slab.Segment);
                slab.Segment.FreeNow();
                released = checked(released + slab.Segment.ByteLength);
            }

            return released;
        }

        NativeGeneration generation = _current!;
        for (int index = generation.BumpSegments.Count - 1; index >= 0; index--)
        {
            NativeBumpSegment segment = generation.BumpSegments[index];
            if (requestedBytes is not null && released >= requestedBytes.Value)
            {
                break;
            }

            if (!segment.IsCompletelyIdle)
            {
                break;
            }

            generation.BumpSegments.RemoveAt(index);
            generation.Owner.RemoveSegment(segment.Segment);
            segment.Segment.FreeNow();
            released = checked(released + segment.Segment.ByteLength);
            ResetBumpTraversal(generation);
        }

        return released;
    }

    private static nuint GetBusySegmentBytes(NativeGeneration generation)
    {
        nuint total = 0;
        foreach (NativeSlab slab in generation.Slabs)
        {
            if (IsSegmentBusy(generation, slab))
            {
                total = checked(total + slab.Segment.ByteLength);
            }
        }

        foreach (NativeBumpSegment bump in generation.BumpSegments)
        {
            if (IsSegmentBusy(generation, bump))
            {
                total = checked(total + bump.Segment.ByteLength);
            }
        }

        return total;
    }

    private static nuint GetGenerationSegmentBytes(
        NativeGeneration generation)
    {
        nuint total = 0;
        foreach (NativeSlab slab in generation.Slabs)
        {
            total = checked(total + slab.Segment.ByteLength);
        }

        foreach (NativeBumpSegment bump in generation.BumpSegments)
        {
            total = checked(total + bump.Segment.ByteLength);
        }

        return total;
    }

    private static bool IsSegmentBusy(NativeGeneration generation, NativeSlab slab)
    {
        foreach (NativeAllocation allocation in generation.Allocations.Values)
        {
            if (allocation.ActiveOperations != 0 && ReferenceEquals(allocation.Slab, slab))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSegmentBusy(NativeGeneration generation, NativeBumpSegment bump)
    {
        foreach (NativeAllocation allocation in generation.Allocations.Values)
        {
            if (allocation.ActiveOperations != 0 && ReferenceEquals(allocation.BumpSegment, bump))
            {
                return true;
            }
        }

        return false;
    }

    private static nuint CalculateByteLength(int length, int elementSize, string operation)
    {
        try
        {
            return checked((nuint)length * (nuint)elementSize);
        }
        catch (OverflowException exception)
        {
            throw new OverflowException($"The {operation} byte count overflows native addressable storage.", exception);
        }
    }

    private static nuint ChooseBumpSegmentBytes(NativeGeneration generation, nuint requiredBytes)
    {
        nuint previous = generation.BumpSegments.Count == 0 ? 0 : generation.BumpSegments[^1].Segment.ByteLength;
        nuint growth = DefaultBumpSegmentBytes;
        if (previous != 0)
        {
            try
            {
                growth = checked(previous * 2);
            }
            catch (OverflowException)
            {
                growth = requiredBytes;
            }
        }

        return Math.Max(requiredBytes, Math.Max(DefaultBumpSegmentBytes, growth));
    }

    private static nuint RequiredFreshBumpBytes(
        nuint byteLength,
        nuint alignment) =>
        alignment <= NativeSegment.Alignment
            ? byteLength
            : checked(byteLength + alignment - 1);

    private static nuint AlignUp(nuint value, nuint alignment)
    {
        nuint remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static nuint AlignDown(nuint value, nuint alignment) => value - value % alignment;

    private NativeAllocation RentAllocationLocked(
        NativeGeneration generation,
        long id,
        NativeSlab? slab,
        NativeBumpSegment? bumpSegment,
        nuint offsetBytes,
        int length,
        int capacity,
        nuint storageBytes,
        NativeReferenceRootTable? referenceRoots,
        bool scoped,
        long scopeEpoch,
        bool preserveScopedRegistration = false)
    {
        if (scoped
            && generation.ScopedRecordCount
                < generation.ScopedAllocationBank.Count)
        {
            NativeAllocation scopedAllocation =
                generation.ScopedAllocationBank[
                    generation.ScopedRecordCount];
            if (!preserveScopedRegistration)
            {
                generation.Allocations.Remove(
                    scopedAllocation.Id);
            }

            scopedAllocation.Reset(
                id,
                slab,
                bumpSegment,
                offsetBytes,
                length,
                capacity,
                storageBytes,
                referenceRoots,
                scoped: true,
                scopeEpoch);
            return scopedAllocation;
        }

        NativeAllocation allocation;
        if (generation.ReusableAllocations.Count == 0)
        {
            allocation = new NativeAllocation(
                generation,
                id,
                slab,
                bumpSegment,
                offsetBytes,
                length,
                capacity,
                storageBytes,
                referenceRoots,
                scoped,
                scopeEpoch);
        }
        else
        {
            int last = generation.ReusableAllocations.Count - 1;
            allocation = generation.ReusableAllocations[last];
            generation.ReusableAllocations.RemoveAt(last);
            allocation.Reset(
                id,
                slab,
                bumpSegment,
                offsetBytes,
                length,
                capacity,
                storageBytes,
                referenceRoots,
                scoped,
                scopeEpoch);
        }

        if (scoped)
        {
            generation.ScopedAllocationBank.Add(allocation);
        }

        return allocation;
    }

    private static void ReleaseLastScopedRecordLocked(
        NativeGeneration generation,
        NativeAllocation allocation)
    {
        int recordCount = generation.ScopedRecordCount;
        if (recordCount == 0
            || !ReferenceEquals(
                generation.ScopedAllocationBank[recordCount - 1],
                allocation))
        {
            return;
        }

        generation.ScopedRecordCount = recordCount - 1;
    }

    private long NextAllocationIdLocked() => checked(++_nextAllocationId);

    private long NextSegmentOrdinalLocked() => checked(++_nextSegmentOrdinal);

    private int ActiveOperationCountLocked() => GetOwnerWideActivityLocked().ActiveOperations;

    private OwnerWideActivity GetOwnerWideActivityLocked()
    {
        int activeOperations = 0;
        int leaseReturnsInProgress = 0;
        int busyGenerationCount = 0;
        long firstBusyGeneration = 0;

        AccumulateOwnerWideActivity(
            _current is { MemoryDetached: false } ? _current : null,
            ref activeOperations,
            ref leaseReturnsInProgress,
            ref busyGenerationCount,
            ref firstBusyGeneration);
        foreach (NativeGeneration generation in _retiredGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            AccumulateOwnerWideActivity(
                generation,
                ref activeOperations,
                ref leaseReturnsInProgress,
                ref busyGenerationCount,
                ref firstBusyGeneration);
        }

        foreach (NativeGeneration generation in _quarantinedGenerations)
        {
            if (generation.MemoryDetached)
            {
                continue;
            }

            AccumulateOwnerWideActivity(
                generation,
                ref activeOperations,
                ref leaseReturnsInProgress,
                ref busyGenerationCount,
                ref firstBusyGeneration);
        }

        return new OwnerWideActivity(
            activeOperations,
            leaseReturnsInProgress,
            busyGenerationCount,
            firstBusyGeneration);
    }

    private static void AccumulateOwnerWideActivity(
        NativeGeneration? generation,
        ref int activeOperations,
        ref int leaseReturnsInProgress,
        ref int busyGenerationCount,
        ref long firstBusyGeneration)
    {
        if (generation is null)
        {
            return;
        }

        activeOperations = checked(activeOperations + generation.ActiveOperations);
        leaseReturnsInProgress = checked(
            leaseReturnsInProgress
            + generation.LeaseReturnsInProgress
            + generation.InitializationsInProgress);
        if (generation.ActiveOperations == 0
            && generation.LeaseReturnsInProgress == 0
            && generation.InitializationsInProgress == 0)
        {
            return;
        }

        busyGenerationCount = checked(busyGenerationCount + 1);
        if (firstBusyGeneration == 0 || generation.Number < firstBusyGeneration)
        {
            firstBusyGeneration = generation.Number;
        }
    }

    private NativeAllocationInUseException CreateOwnerWideInUseException(
        string operation,
        string reason)
    {
        OwnerWideActivity activity = GetOwnerWideActivityLocked();
        string firstGeneration = activity.BusyGenerationCount == 0
            ? "none"
            : activity.FirstBusyGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new NativeAllocationInUseException(
            $"{_ownerKind}.{operation} could not complete because native ownership is in use across the owner. {reason} Owner-wide activity: {activity.ActiveOperations} active operation(s), {activity.LeaseReturnsInProgress} lease-return token(s), {activity.BusyGenerationCount} busy generation(s); first busy generation {firstGeneration}.",
            _ownerKind,
            activity.FirstBusyGeneration == 0 ? _generation : activity.FirstBusyGeneration,
            _generation,
            operation,
            activity.ActiveOperations,
            allocationId: 0,
            currentLifecycle: _lifecycle,
            ownerWideLeaseReturnCount: activity.LeaseReturnsInProgress,
            ownerWideBusyGenerationCount: activity.BusyGenerationCount);
    }

    private void EnsureOwnerWideIdleLocked(string operation, string reason)
    {
        OwnerWideActivity activity = GetOwnerWideActivityLocked();
        if (activity.ActiveOperations != 0 || activity.LeaseReturnsInProgress != 0)
        {
            throw CreateOwnerWideInUseException(operation, reason);
        }
    }

    private void EnsureOwnerWideLeaseReturnsIdleLocked(string operation, string reason)
    {
        OwnerWideActivity activity = GetOwnerWideActivityLocked();
        if (activity.LeaseReturnsInProgress != 0)
        {
            throw CreateOwnerWideInUseException(operation, reason);
        }
    }

    private readonly record struct OwnerWideActivity(
        int ActiveOperations,
        int LeaseReturnsInProgress,
        int BusyGenerationCount,
        long FirstBusyGeneration);

    private NativeAllocationReturnedException CreateReturnedException(string operation, long generation, long currentGeneration, long allocationId, string reason) =>
        new(
            $"{_ownerKind}.{operation} cannot access native storage. {reason} The handle belongs to generation {generation}; the owner is currently at generation {currentGeneration} (lifecycle {_lifecycle}).",
            _ownerKind,
            generation,
            currentGeneration,
            operation,
            ActiveOperationCountLocked(),
            allocationId,
            _lifecycle);

    private NativeAllocationInUseException CreateInUseException(string operation, long generation, long allocationId, int activeOperationCount, string reason) =>
        new(
            $"{_ownerKind}.{operation} could not complete for generation {generation} because native ownership is in use (lifecycle {_lifecycle}). {reason}",
            _ownerKind,
            generation,
            _generation,
            operation,
            activeOperationCount,
            allocationId,
            _lifecycle);

    private NativeAllocationStateException CreateStateException(string operation, string reason, long allocationId) =>
        new(
            $"{_ownerKind}.{operation} is invalid for the current owner state (lifecycle {_lifecycle}). {reason}",
            _ownerKind,
            _generation,
            _generation,
            operation,
            ActiveOperationCountLocked(),
            allocationId,
            _lifecycle);

    private void DisposeFromFinalizer()
    {
        NativeGeneration? generation;
        lock (_gate)
        {
            if (_lifecycle == NativeOwnerLifecycle.Disposed)
            {
                return;
            }

            generation = _current;
            _current = null;
            _lifecycle = NativeOwnerLifecycle.Disposed;
        }

        try
        {
            if (generation is not null && !generation.MemoryDetached)
            {
                generation.Owner.ReleaseToNative();
            }

            foreach (NativeGeneration retired in _retiredGenerations)
            {
                if (!retired.MemoryDetached)
                {
                    retired.Owner.ReleaseToNative();
                }
            }

            foreach (NativeGeneration quarantined in _quarantinedGenerations)
            {
                if (!quarantined.MemoryDetached)
                {
                    quarantined.Owner.ReleaseToNative();
                }
            }
        }
        catch
        {
        }
    }

    ~NativeOwnerKernel() => DisposeFromFinalizer();
}
