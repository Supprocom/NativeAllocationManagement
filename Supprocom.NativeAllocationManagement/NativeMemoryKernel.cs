using System.Collections.Concurrent;
using System.Numerics;
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

internal readonly record struct NativeArenaAllocation(
    NativeGeneration GenerationState,
    IntPtr Pointer,
    nuint OriginalCursor,
    int Length,
    long ScopeEpoch = long.MinValue)
{
    internal long Generation => GenerationState.Number;

    internal int Capacity => Length;

    internal bool Scoped => ScopeEpoch != long.MinValue;
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
    private static int _forcedManagedPublicationBoundary;
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
        Interlocked.Exchange(ref _forcedManagedPublicationBoundary, 0);
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

    internal static void FailAtManagedPublicationBoundary(
        int boundary)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boundary);
        Volatile.Write(
            ref _forcedManagedPublicationBoundary,
            boundary);
    }

    internal static void CheckManagedPublicationBoundary(
        string operation,
        int ordinal,
        string publication)
    {
        int configured = Volatile.Read(
            ref _forcedManagedPublicationBoundary);
        if (configured != ordinal
            || Interlocked.CompareExchange(
                ref _forcedManagedPublicationBoundary,
                0,
                configured) != configured)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Injected managed publication failure during {operation} before {publication}.");
    }

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

    internal static void NotifyBeforeOperationEntry(string operation)
    {
        if (Volatile.Read(ref _operationHooksEnabled) == 0)
        {
            return;
        }

        Volatile.Read(ref _beforeOperationEntry)?.Invoke(operation);
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

    internal bool IsArenaFastSegment { get; set; }

    internal bool IsArenaTransferSegment { get; set; }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    internal bool IsArenaTransferSlot { get; private set; }

    internal int ArenaTransferSlotIndex { get; private set; } = -1;

    internal int ArenaTransferVersion { get; private set; }

    internal int ArenaTransferSizeClass { get; private set; }

    internal nuint ArenaTransferBlockBytes { get; private set; }

    internal NativeArenaTransferLane? ArenaTransferLane { get; private set; }

    internal int ArenaTransferNextSlot;

    internal long ScopeEpoch => Volatile.Read(ref _scopeEpoch);

    internal void SetScopeEpochForTest(long value) =>
        Volatile.Write(ref _scopeEpoch, value);

    internal NativeAllocationLifecycle Lifecycle
    {
        get => (NativeAllocationLifecycle)Volatile.Read(ref _lifecycle);
        set => Volatile.Write(ref _lifecycle, (int)value);
    }

    internal bool TryTransitionLifecycle(
        NativeAllocationLifecycle expected,
        NativeAllocationLifecycle next) =>
        Interlocked.CompareExchange(
            ref _lifecycle,
            (int)next,
            (int)expected) == (int)expected;

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

    internal void ConfigureArenaTransferSlot(int slotIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotIndex);
        IsArenaTransferSlot = true;
        ArenaTransferSlotIndex = slotIndex;
        Lifecycle = NativeAllocationLifecycle.Returned;
    }

    internal long ResetArenaTransfer(
        NativeArenaTransferLane lane,
        NativeBumpSegment? bumpSegment,
        nuint offsetBytes,
        int length,
        int capacity,
        nuint storageBytes,
        int sizeClass,
        nuint blockBytes)
    {
        if (!IsArenaTransferSlot
            || ArenaTransferSlotIndex < 0)
        {
            throw new InvalidOperationException(
                "The Arena transfer record is not a compact slot.");
        }

        int version = checked(ArenaTransferVersion + 1);
        long id = NativeOwnerKernel.PackArenaTransferId(
            ArenaTransferSlotIndex,
            version);
        ArenaTransferVersion = version;
        ArenaTransferSizeClass = sizeClass;
        ArenaTransferBlockBytes = blockBytes;
        ArenaTransferLane = lane;
        ArenaTransferNextSlot = 0;
        Reset(
            id,
            slab: null,
            bumpSegment,
            offsetBytes,
            length,
            capacity,
            storageBytes,
            referenceRoots: null,
            scoped: false,
            scopeEpoch: 0);
        Lifecycle = NativeAllocationLifecycle.Initializing;
        return id;
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

    internal Span<T> AsCapacitySpan<T>()
    {
        if (NativeTypeLayout.ContainsReferences<T>())
        {
            throw new NotSupportedException(
                "A direct capacity span requires value storage without managed references.");
        }

        if (Capacity == 0)
        {
            return Span<T>.Empty;
        }

        NativeSegment segment = Segment
            ?? throw new InvalidOperationException(
                "The allocation has no native segment.");
        unsafe
        {
            ref T first = ref Unsafe.AsRef<T>(
                (void*)((byte*)segment.Pointer
                    + checked((nint)OffsetBytes)));
            return MemoryMarshal.CreateSpan(ref first, Capacity);
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

internal sealed class NativeArenaTransferLane
{
    private int _initializerActive;

    internal NativeArenaTransferLane(
        NativeOwnerKernel owner,
        NativeGeneration generation,
        int threadId,
        int sizeClassCount)
    {
        Owner = owner;
        Generation = generation;
        ThreadId = threadId;
        FreeSlotHeads = new long[sizeClassCount];
        RemoteFreeSlotHeads = new long[sizeClassCount];
        ChunkSegments = new NativeBumpSegment?[sizeClassCount];
        ChunkCursors = new nuint[sizeClassCount];
        ChunkLimits = new nuint[sizeClassCount];
    }

    internal NativeOwnerKernel Owner { get; }

    internal NativeGeneration Generation { get; }

    internal int ThreadId { get; }

    internal long[] FreeSlotHeads { get; }

    internal long[] RemoteFreeSlotHeads { get; }

    internal NativeBumpSegment?[] ChunkSegments { get; }

    internal nuint[] ChunkCursors { get; }

    internal nuint[] ChunkLimits { get; }

    internal int UnusedSlotHead { get; set; }

    internal bool InitializerActive
    {
        get => Volatile.Read(ref _initializerActive) != 0;
        set => Volatile.Write(
            ref _initializerActive,
            value ? 1 : 0);
    }
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct NativeArenaTransferBatchSlot
{
    [FieldOffset(0)]
    internal long State;
}

internal enum NativeArenaTransferBatchTransition
{
    Success,
    Stale,
    InUse
}

internal readonly record struct NativeArenaTransferBatchReservation(
    NativeArenaTransferBatchState State,
    int SlotIndex,
    long Authority,
    IntPtr Pointer,
    int Length);

internal sealed class NativeArenaTransferBatchState
{
    private const long LifecycleMask = 3;
    private const int OperationShift = 22;
    private const long OperationUnit = 1L << OperationShift;
    private const long OperationMask = 0x3FFL << OperationShift;
    private const long MutableMask = LifecycleMask | OperationMask;
    private readonly NativeArenaTransferBatchSlot[] _slots;
    private readonly IntPtr _basePointer;
    private readonly nuint _stride;
    private int _valid = 1;

    internal NativeArenaTransferBatchState(
        NativeOwnerKernel owner,
        NativeGeneration generation,
        NativeBumpSegment? segment,
        IntPtr basePointer,
        NativeArenaTransferBatchSlot[] slots,
        int length,
        nuint storageBytes,
        nuint stride)
    {
        Owner = owner;
        Generation = generation;
        Segment = segment;
        _basePointer = basePointer;
        Length = length;
        StorageBytes = storageBytes;
        _stride = stride;
        _slots = slots;
        for (int index = 0; index < slots.Length; index++)
        {
            _slots[index].State = PackState(
                index,
                version: 0,
                NativeAllocationLifecycle.Returned);
        }
    }

    internal NativeOwnerKernel Owner { get; }

    internal NativeGeneration Generation { get; }

    internal NativeBumpSegment? Segment { get; }

    internal int Count => _slots.Length;

    internal int Length { get; }

    internal nuint StorageBytes { get; }

    internal bool IsValid => Volatile.Read(ref _valid) != 0;

    internal long RequestedBytes
    {
        get
        {
            int activeCount = CountActiveRecords();
            return checked((long)StorageBytes * activeCount);
        }
    }

    internal bool HasInitializer
    {
        get
        {
            for (int index = 0; index < _slots.Length; index++)
            {
                if (ReadLifecycle(ref _slots[index])
                    == NativeAllocationLifecycle.Initializing)
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal int OperationCount
    {
        get
        {
            int count = 0;
            for (int index = 0; index < _slots.Length; index++)
            {
                count = checked(
                    count + GetOperationCount(
                        Volatile.Read(ref _slots[index].State)));
            }

            return count;
        }
    }

    internal bool HasOperation => OperationCount != 0;

    internal int CountActiveRecords()
    {
        int count = 0;
        for (int index = 0; index < _slots.Length; index++)
        {
            if (ReadLifecycle(ref _slots[index])
                != NativeAllocationLifecycle.Returned)
            {
                count++;
            }
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryBeginInitialization(
        int slotIndex,
        out long authority)
    {
        ref NativeArenaTransferBatchSlot slot =
            ref GetSlot(slotIndex);
        long observed = Volatile.Read(ref slot.State);
        if (GetLifecycle(observed)
                != NativeAllocationLifecycle.Returned)
        {
            authority = GetAuthority(observed);
            return false;
        }

        authority = AdvanceAuthority(slotIndex, observed);
        long initializing = AddLifecycle(
            authority,
            NativeAllocationLifecycle.Initializing);
        Volatile.Write(ref slot.State, initializing);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryPublishInitialization(
        int slotIndex,
        long authority)
    {
        ref NativeArenaTransferBatchSlot slot =
            ref GetSlot(slotIndex);
        long initializing = AddLifecycle(
            authority,
            NativeAllocationLifecycle.Initializing);
        if (!IsValid
            || Volatile.Read(ref slot.State) != initializing)
        {
            AbortInitialization(slotIndex, authority);
            return false;
        }

        long active = AddLifecycle(
            authority,
            NativeAllocationLifecycle.Active);
        Volatile.Write(ref slot.State, active);
        if (IsValid)
        {
            return true;
        }

        Interlocked.CompareExchange(
            ref slot.State,
            AddLifecycle(
                authority,
                NativeAllocationLifecycle.Returned),
            active);
        return false;
    }

    internal void AbortInitialization(
        int slotIndex,
        long authority)
    {
        ref NativeArenaTransferBatchSlot slot =
            ref GetSlot(slotIndex);
        Interlocked.CompareExchange(
            ref slot.State,
            AddLifecycle(
                authority,
                NativeAllocationLifecycle.Returned),
            AddLifecycle(
                authority,
                NativeAllocationLifecycle.Initializing));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryEnterOperation(
        int slotIndex,
        long authority)
    {
        ref long state = ref GetSlot(slotIndex).State;
        long active = AddLifecycle(
            authority,
            NativeAllocationLifecycle.Active);
        long entered = Interlocked.Add(
            ref state,
            OperationUnit);
        int operationCount = GetOperationCount(entered);
        if (operationCount == 0)
        {
            Interlocked.Add(ref state, -OperationUnit);
            throw new InvalidOperationException(
                "The fixed transfer slot operation count reached its limit.");
        }

        if (ClearOperationCount(entered) == active)
        {
            return true;
        }

        _ = ExitOperation(slotIndex);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ExitOperation(int slotIndex)
    {
        ref long state = ref GetSlot(slotIndex).State;
        long exited = Interlocked.Add(
            ref state,
            -OperationUnit);
        int count = GetOperationCount(exited);
        if (count == 0x3FF)
        {
            Interlocked.Add(ref state, OperationUnit);
            throw new InvalidOperationException(
                "The fixed transfer slot operation count is already zero.");
        }

        return count;
    }

    internal NativeArenaTransferBatchTransition TryMove(
        int slotIndex,
        long authority,
        out long nextAuthority)
    {
        ref NativeArenaTransferBatchSlot slot =
            ref GetSlot(slotIndex);
        long active = AddLifecycle(
            authority,
            NativeAllocationLifecycle.Active);
        nextAuthority = authority;
        long observed = Volatile.Read(ref slot.State);
        if (ClearOperationCount(observed) != active)
        {
            return NativeArenaTransferBatchTransition.Stale;
        }

        if (GetOperationCount(observed) != 0)
        {
            return NativeArenaTransferBatchTransition.InUse;
        }

        nextAuthority = AdvanceAuthority(slotIndex, authority);
        if (Interlocked.CompareExchange(
                ref slot.State,
                AddLifecycle(
                    nextAuthority,
                    NativeAllocationLifecycle.Active),
                active) == active)
        {
            return NativeArenaTransferBatchTransition.Success;
        }

        observed = Volatile.Read(ref slot.State);
        if (ClearOperationCount(observed) == active
            && GetOperationCount(observed) != 0)
        {
            return NativeArenaTransferBatchTransition.InUse;
        }

        return NativeArenaTransferBatchTransition.Stale;
    }

    internal NativeArenaTransferBatchTransition TryReturn(
        int slotIndex,
        long authority)
    {
        ref NativeArenaTransferBatchSlot slot =
            ref GetSlot(slotIndex);
        long active = AddLifecycle(
            authority,
            NativeAllocationLifecycle.Active);
        long observed = Volatile.Read(ref slot.State);
        if (ClearOperationCount(observed) != active)
        {
            return NativeArenaTransferBatchTransition.Stale;
        }

        if (GetOperationCount(observed) != 0)
        {
            return NativeArenaTransferBatchTransition.InUse;
        }

        if (Interlocked.CompareExchange(
                ref slot.State,
                AddLifecycle(
                    authority,
                    NativeAllocationLifecycle.Returned),
                active) == active)
        {
            return NativeArenaTransferBatchTransition.Success;
        }

        observed = Volatile.Read(ref slot.State);
        if (ClearOperationCount(observed) == active
            && GetOperationCount(observed) != 0)
        {
            return NativeArenaTransferBatchTransition.InUse;
        }

        return NativeArenaTransferBatchTransition.Stale;
    }

    internal int ActiveOperations(int slotIndex) =>
        GetOperationCount(
            Volatile.Read(ref GetSlot(slotIndex).State));

    internal long Authority(int slotIndex) =>
        GetAuthority(Volatile.Read(ref GetSlot(slotIndex).State));

    internal IntPtr GetPointer(int slotIndex)
    {
        _ = GetSlot(slotIndex);
        return StorageBytes == 0
            ? IntPtr.Zero
            : NativeOwnerKernel.AddArenaTransferPointer(
                _basePointer,
                checked(_stride * (nuint)(uint)slotIndex));
    }

    internal void Invalidate()
    {
        Volatile.Write(ref _valid, 0);
        for (int index = 0; index < _slots.Length; index++)
        {
            ref NativeArenaTransferBatchSlot slot =
                ref _slots[index];
            while (true)
            {
                long observed = Volatile.Read(ref slot.State);
                long returned = (observed & ~LifecycleMask)
                    | (long)NativeAllocationLifecycle.Returned;
                if (observed == returned
                    || Interlocked.CompareExchange(
                        ref slot.State,
                        returned,
                        observed) == observed)
                {
                    break;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref NativeArenaTransferBatchSlot GetSlot(int slotIndex)
    {
        if ((uint)slotIndex >= (uint)_slots.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        }

        return ref _slots[slotIndex];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsSlotActive(
        ref NativeArenaTransferBatchSlot slot,
        long authority) =>
        IsValid
        && ClearOperationCount(Volatile.Read(ref slot.State))
            == AddLifecycle(
                authority,
                NativeAllocationLifecycle.Active);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NativeAllocationLifecycle ReadLifecycle(
        ref NativeArenaTransferBatchSlot slot) =>
        GetLifecycle(Volatile.Read(ref slot.State));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NativeAllocationLifecycle GetLifecycle(long state) =>
        (NativeAllocationLifecycle)(state & LifecycleMask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long GetAuthority(long state) =>
        state & ~MutableMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ClearOperationCount(long state) =>
        state & ~OperationMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetOperationCount(long state) =>
        (int)((state & OperationMask) >> OperationShift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long AddLifecycle(
        long authority,
        NativeAllocationLifecycle lifecycle) =>
        authority | (long)lifecycle;

    private static long PackState(
        int slotIndex,
        int version,
        NativeAllocationLifecycle lifecycle)
    {
        uint storedIndex = checked((uint)(slotIndex + 1));
        if (storedIndex > 0x000FFFFFU)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        }

        long authority = long.MinValue
            | ((long)(uint)version << 32)
            | ((long)storedIndex << 2);
        return AddLifecycle(authority, lifecycle);
    }

    private static long AdvanceAuthority(
        int slotIndex,
        long authority)
    {
        uint packedVersion = authority == 0
            ? 0
            : unchecked((uint)((ulong)authority >> 32));
        int currentVersion = unchecked(
            (int)(packedVersion & 0x7FFFFFFFU));
        int version = checked(currentVersion + 1);
        return GetAuthority(PackState(
            slotIndex,
            version,
            NativeAllocationLifecycle.Initializing));
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

internal sealed class NativeAllocationTable :
    IEnumerable<NativeAllocation>
{
    private long _singleKey;
    private NativeAllocation? _singleValue;
    private Dictionary<long, NativeAllocation>? _many;

    internal int Count => _many?.Count
        ?? (_singleValue is null ? 0 : 1);

    internal NativeAllocationTable Values => this;

    internal void EnsureCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (_many is not null)
        {
            _many.EnsureCapacity(capacity);
            return;
        }

        if (capacity <= 1)
        {
            return;
        }

        var many = new Dictionary<long, NativeAllocation>(capacity);
        if (_singleValue is not null)
        {
            many.Add(_singleKey, _singleValue);
            _singleKey = 0;
            _singleValue = null;
        }

        _many = many;
    }

    internal void Add(
        long key,
        NativeAllocation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_many is not null)
        {
            _many.Add(key, value);
            return;
        }

        if (_singleValue is null)
        {
            _singleKey = key;
            _singleValue = value;
            return;
        }

        if (_singleKey == key)
        {
            throw new ArgumentException(
                "An allocation with the same ID is already active.",
                nameof(key));
        }

        EnsureCapacity(2);
        _many!.Add(key, value);
    }

    internal bool TryGetValue(
        long key,
        out NativeAllocation value)
    {
        if (_many is not null)
        {
            bool found = _many.TryGetValue(
                key,
                out NativeAllocation? foundValue);
            value = foundValue!;
            return found;
        }

        if (_singleValue is not null
            && _singleKey == key)
        {
            value = _singleValue;
            return true;
        }

        value = null!;
        return false;
    }

    internal bool Remove(long key)
    {
        if (_many is null)
        {
            if (_singleValue is null
                || _singleKey != key)
            {
                return false;
            }

            _singleKey = 0;
            _singleValue = null;
            return true;
        }

        bool removed = _many.Remove(key);
        if (!removed)
        {
            return false;
        }

        if (_many.Count > 1)
        {
            return true;
        }

        if (_many.Count == 1)
        {
            foreach ((long remainingKey, NativeAllocation remainingValue)
                in _many)
            {
                _singleKey = remainingKey;
                _singleValue = remainingValue;
            }
        }

        _many = null;
        return true;
    }

    internal void Clear()
    {
        _many = null;
        _singleKey = 0;
        _singleValue = null;
    }

    public Enumerator GetEnumerator() =>
        new(_singleValue, _many);

    IEnumerator<NativeAllocation>
        IEnumerable<NativeAllocation>.GetEnumerator() =>
        GetEnumerator();

    System.Collections.IEnumerator
        System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();

    internal struct Enumerator : IEnumerator<NativeAllocation>
    {
        private readonly NativeAllocation? _single;
        private Dictionary<long, NativeAllocation>.Enumerator _many;
        private readonly bool _hasMany;
        private bool _singleRead;

        internal Enumerator(
            NativeAllocation? single,
            Dictionary<long, NativeAllocation>? many)
        {
            _single = single;
            _many = many?.GetEnumerator() ?? default;
            _hasMany = many is not null;
            _singleRead = false;
            Current = null!;
        }

        public NativeAllocation Current { get; private set; }

        object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_hasMany)
            {
                if (!_many.MoveNext())
                {
                    Current = null!;
                    return false;
                }

                Current = _many.Current.Value;
                return true;
            }

            if (_singleRead || _single is null)
            {
                Current = null!;
                return false;
            }

            _singleRead = true;
            Current = _single;
            return true;
        }

        public void Reset() =>
            throw new NotSupportedException();

        public void Dispose()
        {
            if (_hasMany)
            {
                _many.Dispose();
            }
        }
    }
}

internal sealed class NativeGeneration
{
    private long _scopeEpoch;
    private int _memoryDetached;
    private int _initializationsInProgress;
    private NativeAllocation?[] _arenaTransferSlots = [];
    private NativeArenaTransferLane[] _arenaTransferLaneSnapshot = [];
    private NativeArenaTransferBatchState[]
        _arenaTransferBatchSnapshot = [];

    internal NativeGeneration(long number)
    {
        Number = number;
        Owner = new NativeGenerationOwner(number);
    }

    internal long Number { get; }

    internal NativeGenerationOwner Owner { get; }

    internal NativeAllocationTable Allocations { get; } = new();

    // Ordinary leases reuse these records after an individual return.
    internal List<NativeAllocation> ReusableAllocations { get; } = [];

    internal NativeReferenceRootTable ReferenceRoots { get; } = new();

    internal List<NativeSlab> Slabs { get; } = [];

    internal List<NativeSlab> AvailableSlabs { get; } = [];

    internal NativeAllocation? HotPoolAllocation { get; set; }

    internal List<NativeBumpSegment> BumpSegments { get; } = [];

    internal int OrdinaryBumpTraversalIndex { get; set; }

    internal int ScopedBumpTraversalIndex { get; set; } = -1;

    private int _operationAdmission;

    internal int ActiveOperations =>
        NativeOperationAdmission.Count(ref _operationAdmission);

    internal int LeaseReturnsInProgress { get; set; }

    internal int InitializationsInProgress
    {
        get => Volatile.Read(
            ref _initializationsInProgress);
        set => Volatile.Write(
            ref _initializationsInProgress,
            value);
    }

    internal void BeginInitialization() =>
        Interlocked.Increment(
            ref _initializationsInProgress);

    internal void EndInitialization()
    {
        int remaining = Interlocked.Decrement(
            ref _initializationsInProgress);
        if (remaining < 0)
        {
            Interlocked.Increment(
                ref _initializationsInProgress);
            throw new InvalidOperationException(
                "The generation has no active initializer to complete.");
        }
    }

    internal List<NativeArenaTransferLane> ArenaTransferLanes { get; } = [];

    internal List<NativeArenaTransferBatchState>
        ArenaTransferBatches { get; } = [];

    internal NativeArenaTransferLane[] ArenaTransferLaneSnapshot =>
        Volatile.Read(ref _arenaTransferLaneSnapshot);

    internal NativeArenaTransferBatchState[] ArenaTransferBatchSnapshot =>
        Volatile.Read(ref _arenaTransferBatchSnapshot);

    internal NativeAllocation?[] ArenaTransferSlots =>
        Volatile.Read(ref _arenaTransferSlots);

    internal int ArenaTransferSlotCount { get; set; }

    internal void PublishArenaTransferSlots(
        NativeAllocation?[] slots) =>
        Volatile.Write(ref _arenaTransferSlots, slots);

    internal void PublishArenaTransferLanes(
        NativeArenaTransferLane[] lanes) =>
        Volatile.Write(ref _arenaTransferLaneSnapshot, lanes);

    internal void PublishArenaTransferBatches(
        NativeArenaTransferBatchState[] batches) =>
        Volatile.Write(ref _arenaTransferBatchSnapshot, batches);

    internal long FastArenaRequestedBytes { get; set; }

    internal long FastArenaScopedRequestedBaseline { get; set; }

    internal bool FastArenaScopedActive { get; set; }

    internal int ArenaInitializationsInProgress;

    internal List<NativeAllocation>? ArenaCompositeAllocations { get; set; }

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
        if (AvailableSlabs.Count == 0
            || AvailableSlabs[^1].AllocationOrdinal
                < slab.AllocationOrdinal)
        {
            AvailableSlabs.Add(slab);
            return;
        }

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

internal ref struct NativeArenaLocalOperationToken
{
    private NativeOwnerKernel? _kernel;
    private readonly NativeGeneration _generation;

    internal NativeArenaLocalOperationToken(
        NativeOwnerKernel kernel,
        NativeGeneration generation)
    {
        _kernel = kernel;
        _generation = generation;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        NativeOwnerKernel? kernel = _kernel;
        if (kernel is null)
        {
            return;
        }

        _kernel = null;
        kernel.ExitArenaFastLocalOperation(
            _generation,
            "ArenaLease");
    }
}

internal ref struct NativeOperationToken
{
    private NativeOwnerKernel? _kernel;
    private readonly NativeGeneration _generationState;
    private readonly NativeGenerationOwner _generationOwner;
    private readonly NativeAllocation _allocation;
    private readonly bool _allocationEntered;
    private readonly bool _generationEntered;
    private readonly string _operation;

    internal NativeOperationToken(
        NativeOwnerKernel kernel,
        NativeGeneration generation,
        NativeAllocation allocation,
        bool allocationEntered,
        string operation,
        bool generationEntered = true)
    {
        _kernel = kernel;
        _generationState = generation;
        _generationOwner = generation.Owner;
        _allocation = allocation;
        _allocationEntered = allocationEntered;
        _generationEntered = generationEntered;
        _operation = operation;
    }

    internal NativeLeaseView<T> GetView<T>() => new(_allocation);

    internal NativeLeaseView<T> GetView<T>(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > _allocation.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                "The prefix length exceeds the lease length.");
        }

        return new NativeLeaseView<T>(_allocation, length);
    }

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
            _generationEntered,
            _operation);
        GC.KeepAlive(_generationOwner);
    }
}

internal ref struct NativeArenaOperationToken
{
    private NativeOperationToken _legacy;
    private NativeOwnerKernel? _kernel;
    private readonly NativeGeneration? _generationState;
    private readonly NativeGenerationOwner? _generationOwner;
    private readonly IntPtr _pointer;
    private readonly int _length;
    private readonly bool _localOperation;
    private readonly string? _operation;

    internal NativeArenaOperationToken(
        NativeOperationToken legacy)
    {
        _legacy = legacy;
        _kernel = null;
        _generationState = null;
        _generationOwner = null;
        _pointer = IntPtr.Zero;
        _length = 0;
        _localOperation = false;
        _operation = null;
    }

    internal NativeArenaOperationToken(
        NativeOwnerKernel kernel,
        NativeGeneration generation,
        IntPtr pointer,
        int length,
        bool localOperation,
        string operation)
    {
        _legacy = default;
        _kernel = kernel;
        _generationState = generation;
        _generationOwner = generation.Owner;
        _pointer = pointer;
        _length = length;
        _localOperation = localOperation;
        _operation = operation;
    }

    internal NativeLeaseView<T> GetView<T>() =>
        _generationState is null
            ? _legacy.GetView<T>()
            : new NativeLeaseView<T>(
                _pointer,
                _length);

    internal T GetValue<T>(int index) =>
        _generationState is null
            ? _legacy.GetValue<T>(index)
            : GetView<T>()[index];

    internal void SetValue<T>(int index, T value)
    {
        if (_generationState is null)
        {
            _legacy.SetValue(index, value);
            return;
        }

        GetView<T>()[index] = value;
    }

    public void Dispose()
    {
        NativeGeneration? generation = _generationState;
        if (generation is null)
        {
            _legacy.Dispose();
            return;
        }

        NativeOwnerKernel? kernel = _kernel;
        if (kernel is null)
        {
            return;
        }

        _kernel = null;
        if (_localOperation)
        {
            kernel.ExitArenaFastLocalOperation(
                generation,
                _operation!);
        }
        else
        {
            kernel.ExitArenaFastOperation(
                generation,
                _operation!);
        }

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
    private const int ArenaTransferSlotBatchSize = 32;
    private const nuint ArenaTransferMinimumChunkBytes = 262_144;
    internal const int ArenaTransferSizeClassCount = 65;
    private const long ArenaTransferIdMarker = long.MinValue;

    [ThreadStatic]
    private static WeakReference<NativeArenaTransferLane>?
        _cachedArenaTransferLane;

    private readonly Lock _gate = new();
    private readonly NativeOwnerKind _kind;
    private readonly string _ownerKind;
    private readonly NativeMemoryReturn _returnMemoryOnDispose;
    private readonly int _storageElementSize;
    private readonly nuint _preLease;
    private readonly nuint _preAllocateBytes;
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
    private long _arenaFastSlowPathCount;
    private readonly int _arenaFastThreadId;
    private NativeGeneration? _arenaFastGeneration;
    private NativeBumpSegment? _arenaFastSegment;
    private nuint _arenaFastCursor;
    private NativeBumpSegment? _arenaFastRollbackPreviousSegment;
    private NativeBumpSegment? _arenaFastRollbackCreatedSegment;
    private nuint _arenaFastRollbackPreviousCursor;
    private NativeBumpSegment? _arenaFastScopedSegment;
    private NativeBumpSegment? _arenaFastScopedRollbackPreviousSegment;
    private NativeBumpSegment? _arenaFastScopedRollbackCreatedSegment;
    private bool _arenaFastScopedRollbackStartedSegment;
    private int _arenaFastInitializerActive;
    private int _arenaFastBoundaryClosing;
    private int _arenaFastOperationDepth;
    private NativeGeneration? _arenaFastHazardGeneration;
    private NativeGeneration? _arenaTransferGeneration;
    private NativeBumpSegment? _arenaTransferCentralSegment;
    private long _arenaTransferSlowPathCount;
    private long _arenaTransferSlotCreationCount;

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

            foreach (NativeAllocation? allocation in
                current.ArenaTransferSlots)
            {
                if (allocation is null
                    || allocation.Lifecycle
                        != NativeAllocationLifecycle.Active)
                {
                    continue;
                }

                requestedBytes = checked(
                    requestedBytes
                    + checked((long)allocation.StorageBytes));
            }

            foreach (NativeArenaTransferBatchState batch in
                current.ArenaTransferBatchSnapshot)
            {
                requestedBytes = checked(
                    requestedBytes + batch.RequestedBytes);
            }

            requestedBytes = checked(
                requestedBytes
                + current.FastArenaRequestedBytes);

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

                if (_kind == NativeOwnerKind.Arena)
                {
                    activeRecords = checked(
                        activeRecords
                        + CountArenaTransferRecords(current)
                        + (current.FastArenaScopedActive ? 1 : 0));
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
                current is null
                    ? 0
                    : checked(
                        current.ScopedRecordCount
                        + (current.FastArenaScopedActive ? 1 : 0)),
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

            if (_kind == NativeOwnerKind.Arena)
            {
                count = checked(
                    count
                    + CountArenaTransferRecords(current)
                    + (current.ArenaCompositeAllocations?.Count
                        ?? 0));
            }

            return count;
        }
    }

    internal int CurrentInitializationCountForTest()
    {
        lock (_gate)
        {
            return _current?.InitializationsInProgress ?? 0;
        }
    }

    internal int CurrentGenerationActiveOperationsForTest()
    {
        lock (_gate)
        {
            return _current?.ActiveOperations ?? 0;
        }
    }

    internal int CurrentReferenceRootCountForTest()
    {
        lock (_gate)
        {
            return _current?.ReferenceRoots.Count ?? 0;
        }
    }

    internal int CurrentConcurrentReservationCountForTest()
    {
        lock (_gate)
        {
            NativeGeneration? generation = _current;
            if (generation is null)
            {
                return 0;
            }

            return CountArenaTransferRecords(generation);
        }
    }

    internal (
        long SlowPaths,
        long SlotCreations,
        int DictionaryRecords) ArenaTransferMetricsForTest()
    {
        lock (_gate)
        {
            return (
                _arenaTransferSlowPathCount,
                _arenaTransferSlotCreationCount,
                _current?.Allocations.Count ?? 0);
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
        nuint preLease,
        nuint preAllocateBytes,
        bool containsReferences,
        bool doNotLeaseOnDeclaration)
    {
        _kind = kind;
        _ownerKind = ownerKind;
        _returnMemoryOnDispose = returnMemoryOnDispose;
        _storageElementSize = storageElementSize;
        _preLease = preLease;
        _preAllocateBytes = preAllocateBytes;
        _containsReferences = containsReferences;
        _arenaFastThreadId =
            kind == NativeOwnerKind.Arena
                ? Environment.CurrentManagedThreadId
                : 0;
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
        int preLease,
        nuint preAllocateBytes,
        int storageElementSize,
        string ownerKind,
        NativeMemoryReturn returnMemoryOnDispose,
        bool containsReferences,
        bool doNotLeaseOnDeclaration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(preLease);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(storageElementSize);
        NativeMemoryReturnValidation.Validate(returnMemoryOnDispose, nameof(returnMemoryOnDispose));
        return new NativeOwnerKernel(
            NativeOwnerKind.Pool,
            ownerKind,
            returnMemoryOnDispose,
            storageElementSize,
            (nuint)preLease,
            preAllocateBytes,
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
            preLease: 0,
            preAllocateBytes: preAllocateBytes,
            containsReferences: containsReferences,
            doNotLeaseOnDeclaration: doNotLeaseOnDeclaration);
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
            preLease: 0,
            preAllocateBytes: preAllocateBytes,
            containsReferences: false,
            doNotLeaseOnDeclaration: doNotLeaseOnDeclaration);
    }

    internal long ArenaFastSlowPathCountForTest()
    {
        lock (_gate)
        {
            return _arenaFastSlowPathCount;
        }
    }

    internal bool IsArenaFastThread =>
        _kind == NativeOwnerKind.Arena
        && Environment.CurrentManagedThreadId
            == _arenaFastThreadId;

    internal bool CanUseArenaFastLocalOperation =>
        IsArenaFastThread
        && !NativeMemoryTestHooks.OperationHooksEnabled;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BeginArenaFastBoundary()
    {
        if (_kind == NativeOwnerKind.Arena)
        {
            Volatile.Write(
                ref _arenaFastBoundaryClosing,
                1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EndArenaFastBoundary()
    {
        if (_kind == NativeOwnerKind.Arena)
        {
            Volatile.Write(
                ref _arenaFastBoundaryClosing,
                0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasArenaFastInitializer()
    {
        if (_kind != NativeOwnerKind.Arena)
        {
            return false;
        }

        if (Volatile.Read(
                ref _arenaFastInitializerActive) != 0)
        {
            return true;
        }

        return HasArenaTransferInitializer(
            Volatile.Read(ref _current));
    }

    private static bool HasArenaTransferInitializer(
        NativeGeneration? generation)
    {
        if (generation is null)
        {
            return false;
        }

        foreach (NativeArenaTransferLane lane in
            generation.ArenaTransferLaneSnapshot)
        {
            if (lane.InitializerActive)
            {
                return true;
            }
        }

        foreach (NativeArenaTransferBatchState batch in
            generation.ArenaTransferBatchSnapshot)
        {
            if (batch.HasInitializer)
            {
                return true;
            }
        }

        return false;
    }

    private static int CountArenaTransferRecords(
        NativeGeneration generation)
    {
        int count = 0;
        NativeAllocation?[] slots = generation.ArenaTransferSlots;
        int slotCount = Math.Min(
            generation.ArenaTransferSlotCount,
            slots.Length);
        for (int index = 0; index < slotCount; index++)
        {
            NativeAllocation? allocation = slots[index];
            if (allocation is not null
                && allocation.Lifecycle
                    != NativeAllocationLifecycle.Returned)
            {
                count++;
            }
        }

        foreach (NativeArenaTransferBatchState batch in
            generation.ArenaTransferBatchSnapshot)
        {
            count = checked(
                count + batch.CountActiveRecords());
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasArenaFastHazard(
        NativeGeneration generation) =>
        _kind == NativeOwnerKind.Arena
        && (ReferenceEquals(
                Volatile.Read(
                    ref _arenaFastHazardGeneration),
                generation)
            || HasArenaTransferOperation(generation));

    private static bool HasArenaTransferOperation(
        NativeGeneration generation) =>
        CountArenaTransferOperations(generation) != 0;

    private static int CountArenaTransferOperations(
        NativeGeneration generation)
    {
        int count = 0;
        NativeAllocation?[] slots = generation.ArenaTransferSlots;
        int slotCount = Math.Min(
            generation.ArenaTransferSlotCount,
            slots.Length);
        for (int index = 0; index < slotCount; index++)
        {
            if (slots[index] is { } allocation)
            {
                count = checked(
                    count + allocation.ActiveOperations);
            }
        }

        foreach (NativeArenaTransferBatchState batch in
            generation.ArenaTransferBatchSnapshot)
        {
            count = checked(
                count + batch.OperationCount);
        }

        return count;
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

            if (!scoped
                && !_containsReferences
                && length > 0
                && TakeHotPoolAllocationLocked(
                    generation,
                    length)
                    is NativeAllocation hotAllocation)
            {
                return BeginHotPoolInitializationLocked(
                    generation,
                    hotAllocation,
                    length);
            }

            NativeSlab? slab = null;
            if (length > 0)
            {
                slab = TakeSmallestAvailableSlabLocked(
                    generation,
                    length);
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

            nuint storageBytes = slab is null
                ? 0
                : checked(
                    (nuint)length
                    * (nuint)_storageElementSize);
            NativeAllocation allocation = RentAllocationLocked(
                generation,
                allocationId,
                slab,
                bumpSegment: null,
                offsetBytes: 0,
                length,
                slab?.Capacity ?? 0,
                storageBytes,
                referenceRoots: _containsReferences
                    ? generation.ReferenceRoots
                    : null,
                scoped,
                epoch);
            allocation.Lifecycle = NativeAllocationLifecycle.Initializing;
            generation.Allocations.Add(allocationId, allocation);
            if (scoped)
            {
                generation.ScopedRecordCount =
                    checked(generation.ScopedRecordCount + 1);
            }

            generation.BeginInitialization();
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

    private (
        NativeGeneration Generation,
        NativeAllocation Allocation,
        NativePoolLease Lease)
        BeginHotPoolInitializationLocked(
            NativeGeneration generation,
            NativeAllocation allocation,
            int length)
    {
        NativeSlab slab = allocation.Slab
            ?? throw new InvalidOperationException(
                "A hot pool allocation requires one native slab.");
        long allocationId = NextAllocationIdLocked();
        nuint storageBytes = checked(
            (nuint)length
            * (nuint)_storageElementSize);
        allocation.Reset(
            allocationId,
            slab,
            bumpSegment: null,
            offsetBytes: 0,
            length,
            slab.Capacity,
            storageBytes,
            referenceRoots: null,
            scoped: false,
            generation.ScopeEpoch);
        allocation.Lifecycle =
            NativeAllocationLifecycle.Initializing;
        generation.Allocations.Add(
            allocationId,
            allocation);
        generation.BeginInitialization();
        NativeMemoryTestHooks.RecordReusedNativeSegment();
        return (
            generation,
            allocation,
            new NativePoolLease(
                allocationId,
                allocation));
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

        if (!scoped
            && !_containsReferences
            && _kind == NativeOwnerKind.Pool
            && ReferenceEquals(
                Volatile.Read(ref _current),
                generation)
            && allocation.Lifecycle
                == NativeAllocationLifecycle.Initializing)
        {
            allocation.InitializedLength = 0;
            allocation.Lifecycle =
                NativeAllocationLifecycle.Active;
            generation.EndInitialization();
            return;
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

            generation.EndInitialization();
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
                generation.HotPoolAllocation =
                    generation.ReusableAllocations.Count == 1
                    && generation.AvailableSlabs.Count == 0
                    && allocation.Slab is not null
                        ? allocation
                        : null;
            }

            if (allocation.Slab is not null && allocation.Length > 0)
            {
                generation.AddAvailableSlabOrdered(allocation.Slab);
            }

            generation.EndInitialization();
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
        return LeaseBumpInitializedCore(
            length,
            elementSize,
            alignment,
            scoped,
            containsReferences,
            initializer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeArenaAllocation LeaseArenaBumpInitialized<T>(
        int length,
        int elementSize,
        nuint alignment,
        NativeLeaseInitializer<T> initializer)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        nuint byteLength = CalculateArenaByteLength(
            length,
            elementSize);
        NativeGeneration generation = BeginArenaBumpReservation(
            "Scratch");
        NativeArenaAllocation allocation =
            ReserveArenaBumpRangeSafe(
                generation,
                length,
                byteLength,
                alignment);
        InitializeArenaBumpRange(
            generation,
            allocation,
            length,
            byteLength,
            initializer);
        return allocation;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeArenaAllocation
        LeaseArenaScopedBumpInitialized<T>(
            int length,
            int elementSize,
            nuint alignment,
            NativeLeaseInitializer<T> initializer)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        nuint byteLength = CalculateArenaByteLength(
            length,
            elementSize);
        NativeGeneration generation = BeginArenaBumpReservation(
            "LeaseScoped");
        NativeArenaAllocation allocation =
            ReserveArenaScopedBumpRangeSafe(
                generation,
                length,
                byteLength,
                alignment);
        InitializeArenaScopedBumpRange(
            generation,
            allocation,
            length,
            byteLength,
            initializer);
        return allocation;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InitializeArenaScopedBumpRange<T>(
        NativeGeneration generation,
        NativeArenaAllocation allocation,
        int length,
        nuint byteLength,
        NativeLeaseInitializer<T> initializer)
    {
        int initializedLength = 0;
        try
        {
            long requestedBytes = checked(
                generation.FastArenaRequestedBytes
                + checked((long)byteLength));
            NativeLeaseWriter<T> writer = new(
                allocation.Pointer,
                length,
                ref initializedLength);
            initializer(writer);
            if (initializedLength != length)
            {
                throw new InvalidOperationException(
                    "The native lease initializer did not write all logical elements.");
            }

            generation.FastArenaRequestedBytes =
                requestedBytes;
            generation.FastArenaScopedActive = true;
        }
        catch
        {
            RollBackArenaScopedBumpReservation(
                generation,
                allocation);
            throw;
        }
        finally
        {
            FinishArenaBumpInitialization();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InitializeArenaBumpRange<T>(
        NativeGeneration generation,
        NativeArenaAllocation allocation,
        int length,
        nuint byteLength,
        NativeLeaseInitializer<T> initializer)
    {
        int initializedLength = 0;
        try
        {
            NativeLeaseWriter<T> writer = new(
                allocation.Pointer,
                length,
                ref initializedLength);
            initializer(writer);
            if (initializedLength != length)
            {
                throw new InvalidOperationException(
                    "The native lease initializer did not write all logical elements.");
            }

            generation.FastArenaRequestedBytes =
                checked(
                    generation.FastArenaRequestedBytes
                    + checked((long)byteLength));
        }
        catch
        {
            RollBackArenaBumpReservation(
                generation,
                allocation);
            throw;
        }
        finally
        {
            FinishArenaBumpInitialization();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativeArenaAllocation ReserveArenaBumpRangeSafe(
        NativeGeneration generation,
        int length,
        nuint byteLength,
        nuint alignment)
    {
        try
        {
            return ReserveArenaBumpRange(
                generation,
                length,
                byteLength,
                alignment);
        }
        catch
        {
            FinishArenaBumpInitialization();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativeArenaAllocation
        ReserveArenaScopedBumpRangeSafe(
            NativeGeneration generation,
            int length,
            nuint byteLength,
            nuint alignment)
    {
        try
        {
            return ReserveArenaScopedBumpRange(
                generation,
                length,
                byteLength,
                alignment);
        }
        catch
        {
            RollBackArenaScopedBumpReservation(
                generation,
                default);
            FinishArenaBumpInitialization();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeGeneration BeginArenaBumpReservation(
        string operation)
    {
        if (Volatile.Read(
                ref _arenaFastInitializerActive)
            != 0)
        {
            ThrowArenaFastInitializerInUse(operation);
        }

        Volatile.Write(
            ref _arenaFastInitializerActive,
            1);
        NativeGeneration? generation = _arenaFastGeneration;
        if (Volatile.Read(
                ref _arenaFastBoundaryClosing)
                != 0
            || generation is null)
        {
            generation = EnterArenaInitializationSlowSafe(
                operation);
        }
        else if (!ReferenceEquals(
                Volatile.Read(ref _current),
                generation)
            || _lifecycle != NativeOwnerLifecycle.Active)
        {
            generation = EnterArenaInitializationSlowSafe(
                operation);
        }
        else if (Volatile.Read(
                ref generation.ArenaInitializationsInProgress)
            != 0)
        {
            generation = EnterArenaInitializationSlowSafe(
                operation);
        }

        return generation;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativeGeneration EnterArenaInitializationSlowSafe(
        string operation)
    {
        try
        {
            return EnterArenaInitializationSlow(operation);
        }
        catch
        {
            FinishArenaBumpInitialization();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativeGeneration EnterArenaInitializationSlow(
        string operation)
    {
        lock (_gate)
        {
            _arenaFastSlowPathCount = checked(
                _arenaFastSlowPathCount + 1);
            NativeGeneration generation = EnsureActiveLocked(
                operation);
            if (_kind != NativeOwnerKind.Arena)
            {
                throw CreateStateException(
                    operation,
                    "This owner does not have an Arena scratch lane.",
                    0);
            }

            if (Volatile.Read(
                    ref _arenaFastBoundaryClosing)
                != 0)
            {
                throw CreateInUseException(
                    operation,
                    generation.Number,
                    0,
                    generation.ActiveOperations,
                    "The Arena is in a lane-boundary transition.");
            }

            if (!ReferenceEquals(
                    _arenaFastGeneration,
                    generation))
            {
                _arenaFastGeneration = generation;
                _arenaFastSegment = null;
                _arenaFastCursor = 0;
                _arenaFastScopedSegment = null;
            }

            if (generation.InitializationsInProgress != 0)
            {
                throw CreateInUseException(
                    operation,
                    generation.Number,
                    0,
                    generation.ActiveOperations,
                    "A different native lease initializer is already active.");
            }

            if (HasArenaTransferInitializer(generation))
            {
                throw CreateInUseException(
                    operation,
                    generation.Number,
                    0,
                    generation.ActiveOperations,
                    "An Arena transfer initializer is already active.");
            }

            return generation;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeArenaAllocation ReserveArenaBumpRange(
        NativeGeneration generation,
        int length,
        nuint byteLength,
        nuint alignment)
    {
        if (byteLength == 0)
        {
            return new NativeArenaAllocation(
                generation,
                IntPtr.Zero,
                _arenaFastCursor,
                length);
        }

        NativeBumpSegment? previousSegment =
            _arenaFastSegment;
        nuint previousCursor = _arenaFastCursor;
        NativeBumpSegment? createdSegment = null;
        NativeBumpSegment? segment = previousSegment;
        nuint aligned = segment is null
            ? 0
            : AlignArenaBumpCursor(
                _arenaFastCursor,
                alignment);
        if (segment is null
            || aligned > segment.HighCursor
            || byteLength
                > segment.HighCursor - aligned)
        {
            segment = GrowArenaBumpLane(
                generation,
                byteLength,
                alignment,
                out createdSegment);
            aligned = AlignArenaBumpCursor(
                _arenaFastCursor,
                alignment);
        }

        nuint originalCursor = _arenaFastCursor;
        nuint end = checked(aligned + byteLength);
        _arenaFastCursor = end;
        segment.LowCursor = end;
        if (createdSegment is not null)
        {
            _arenaFastRollbackPreviousSegment = previousSegment;
            _arenaFastRollbackCreatedSegment = createdSegment;
            _arenaFastRollbackPreviousCursor = previousCursor;
        }

        return new NativeArenaAllocation(
            generation,
            AddPointer(segment.Segment.Pointer, aligned),
            originalCursor,
            length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeArenaAllocation
        ReserveArenaScopedBumpRange(
            NativeGeneration generation,
            int length,
            nuint byteLength,
            nuint alignment)
    {
        if (!generation.FastArenaScopedActive)
        {
            if (!generation.ScopedBaselineActive)
            {
                generation.ScopedTouchedSegments.Clear();
                generation.ScopedBaselineActive = true;
            }

            generation.FastArenaScopedRequestedBaseline =
                generation.FastArenaRequestedBytes;
        }

        if (byteLength == 0)
        {
            return new NativeArenaAllocation(
                generation,
                IntPtr.Zero,
                OriginalCursor: 0,
                length,
                generation.ScopeEpoch);
        }

        NativeBumpSegment? previousSegment =
            _arenaFastScopedSegment;
        NativeBumpSegment? createdSegment = null;
        NativeBumpSegment? segment = previousSegment;
        nuint offset = 0;
        if (segment is null
            || !TryCalculateArenaScopedOffset(
                segment,
                byteLength,
                alignment,
                out offset))
        {
            segment = GrowArenaScopedBumpLane(
                generation,
                byteLength,
                alignment,
                out createdSegment);
            if (!TryCalculateArenaScopedOffset(
                    segment,
                    byteLength,
                    alignment,
                    out offset))
            {
                throw new InvalidOperationException(
                    "The Arena scoped lane growth did not provide sufficient storage.");
            }
        }

        nuint originalCursor = segment.HighCursor;
        if (createdSegment is not null)
        {
            _arenaFastScopedRollbackPreviousSegment =
                previousSegment;
            _arenaFastScopedRollbackCreatedSegment =
                createdSegment;
        }
        if (segment.PendingScopeEpoch
            != generation.ScopeEpoch)
        {
            generation.ScopedTouchedSegments.EnsureCapacity(
                checked(
                    generation.ScopedTouchedSegments.Count
                    + 1));
            generation.ScopedTouchedSegments.Add(segment);
            segment.BeginPendingScopeRange(
                generation.ScopeEpoch,
                offset,
                originalCursor);
            _arenaFastScopedRollbackStartedSegment = true;
        }

        segment.HighCursor = offset;
        return new NativeArenaAllocation(
            generation,
            AddPointer(segment.Segment.Pointer, offset),
            originalCursor,
            length,
            generation.ScopeEpoch);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryCalculateArenaScopedOffset(
        NativeBumpSegment segment,
        nuint byteLength,
        nuint alignment,
        out nuint offset)
    {
        offset = 0;
        if (segment.IsArenaTransferSegment
            || segment.HighCursor < segment.LowCursor
            || byteLength
                > segment.HighCursor - segment.LowCursor)
        {
            return false;
        }

        offset = AlignDown(
            segment.HighCursor - byteLength,
            alignment);
        return offset >= segment.LowCursor
            && byteLength
                <= segment.HighCursor - offset;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativeBumpSegment GrowArenaBumpLane(
        NativeGeneration generation,
        nuint byteLength,
        nuint alignment,
        out NativeBumpSegment? createdSegment)
    {
        lock (_gate)
        {
            _arenaFastSlowPathCount = checked(
                _arenaFastSlowPathCount + 1);
            if (!ReferenceEquals(generation, _current)
                || _lifecycle != NativeOwnerLifecycle.Active)
            {
                throw CreateStateException(
                    "Scratch",
                    "The Arena changed before its scratch range grew.",
                    0);
            }

            nuint requiredBytes =
                RequiredFreshBumpBytes(
                    byteLength,
                    alignment);
            NativeBumpSegment? scoped =
                _arenaFastScopedSegment;
            if (scoped is not null
                && scoped.HighCursor >= scoped.LowCursor)
            {
                nuint scopedCursor = AlignArenaBumpCursor(
                    scoped.LowCursor,
                    alignment);
                if (scopedCursor <= scoped.HighCursor
                    && byteLength
                        <= scoped.HighCursor - scopedCursor)
                {
                    _arenaFastSegment = scoped;
                    _arenaFastCursor = scoped.LowCursor;
                    createdSegment = null;
                    return scoped;
                }
            }

            foreach (NativeBumpSegment candidate in
                generation.BumpSegments)
            {
                if (candidate.IsArenaFastSegment
                    || candidate.IsArenaTransferSegment
                    || candidate.HighCursor
                        < candidate.LowCursor)
                {
                    continue;
                }

                nuint candidateCursor =
                    AlignArenaBumpCursor(
                        candidate.LowCursor,
                        alignment);
                if (candidateCursor > candidate.HighCursor
                    || byteLength
                        > candidate.HighCursor - candidateCursor)
                {
                    continue;
                }

                candidate.IsArenaFastSegment = true;
                _arenaFastSegment = candidate;
                _arenaFastCursor = candidate.LowCursor;
                createdSegment = null;
                return candidate;
            }

            nuint segmentBytes = ChooseBumpSegmentBytes(
                generation,
                requiredBytes);
            generation.BumpSegments.EnsureCapacity(
                checked(generation.BumpSegments.Count + 1));
            generation.Owner.PrepareAddSegmentCapacity(1);
            NativeSegment segment = NativeSegment.Allocate(
                segmentBytes,
                _ownerKind,
                generation.Number,
                "arena lane growth",
                _lifecycle,
                zeroed: false);
            NativeBumpSegment bump = new(
                segment,
                NextSegmentOrdinalLocked())
            {
                IsArenaFastSegment = true
            };
            try
            {
                AppendBumpSegmentLocked(generation, bump);
                generation.Owner.AddSegment(segment);
                _freshSegmentAllocationCount++;
                _arenaFastSegment = bump;
                _arenaFastCursor = 0;
                createdSegment = bump;
                return bump;
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
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativeBumpSegment GrowArenaScopedBumpLane(
        NativeGeneration generation,
        nuint byteLength,
        nuint alignment,
        out NativeBumpSegment? createdSegment)
    {
        lock (_gate)
        {
            _arenaFastSlowPathCount = checked(
                _arenaFastSlowPathCount + 1);
            if (!ReferenceEquals(generation, _current)
                || _lifecycle != NativeOwnerLifecycle.Active)
            {
                throw CreateStateException(
                    "LeaseScoped",
                    "The Arena changed before its scoped range grew.",
                    0);
            }

            NativeBumpSegment? ordinary =
                _arenaFastSegment;
            if (ordinary is not null
                && TryCalculateArenaScopedOffset(
                    ordinary,
                    byteLength,
                    alignment,
                    out _))
            {
                ordinary.IsArenaFastSegment = true;
                _arenaFastScopedSegment = ordinary;
                createdSegment = null;
                return ordinary;
            }

            for (int index =
                    generation.BumpSegments.Count - 1;
                index >= 0;
                index--)
            {
                NativeBumpSegment candidate =
                    generation.BumpSegments[index];
                if (!TryCalculateArenaScopedOffset(
                        candidate,
                        byteLength,
                        alignment,
                        out _))
                {
                    continue;
                }

                candidate.IsArenaFastSegment = true;
                _arenaFastScopedSegment = candidate;
                createdSegment = null;
                return candidate;
            }

            nuint requiredBytes =
                RequiredFreshBumpBytes(
                    byteLength,
                    alignment);
            nuint segmentBytes = ChooseBumpSegmentBytes(
                generation,
                requiredBytes);
            generation.BumpSegments.EnsureCapacity(
                checked(generation.BumpSegments.Count + 1));
            generation.Owner.PrepareAddSegmentCapacity(1);
            NativeSegment segment = NativeSegment.Allocate(
                segmentBytes,
                _ownerKind,
                generation.Number,
                "arena scoped lane growth",
                _lifecycle,
                zeroed: false);
            NativeBumpSegment bump = new(
                segment,
                NextSegmentOrdinalLocked())
            {
                IsArenaFastSegment = true
            };
            try
            {
                AppendBumpSegmentLocked(generation, bump);
                generation.Owner.AddSegment(segment);
                _freshSegmentAllocationCount++;
                _arenaFastScopedSegment = bump;
                createdSegment = bump;
                return bump;
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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RollBackArenaBumpReservation(
        NativeGeneration generation,
        NativeArenaAllocation allocation)
    {
        if (allocation.Pointer == IntPtr.Zero)
        {
            return;
        }

        if (_arenaFastRollbackCreatedSegment is null)
        {
            _arenaFastCursor = allocation.OriginalCursor;
            _arenaFastSegment!.LowCursor =
                allocation.OriginalCursor;
            return;
        }

        lock (_gate)
        {
            NativeBumpSegment created =
                _arenaFastRollbackCreatedSegment;
            generation.BumpSegments.Remove(created);
            generation.Owner.RemoveSegment(
                created.Segment);
            created.IsArenaFastSegment = false;
            created.Segment.FreeNow();
            _arenaFastSegment = _arenaFastRollbackPreviousSegment;
            _arenaFastCursor = _arenaFastRollbackPreviousCursor;
            ResetBumpTraversal(generation);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RollBackArenaScopedBumpReservation(
        NativeGeneration generation,
        NativeArenaAllocation allocation)
    {
        _ = allocation;
        NativeBumpSegment? created =
            _arenaFastScopedRollbackCreatedSegment;
        if (_arenaFastScopedRollbackStartedSegment)
        {
            NativeBumpSegment segment =
                _arenaFastScopedSegment!;
            int lastIndex =
                generation.ScopedTouchedSegments.Count - 1;
            if (lastIndex >= 0
                && ReferenceEquals(
                    generation.ScopedTouchedSegments[lastIndex],
                    segment))
            {
                generation.ScopedTouchedSegments.RemoveAt(
                    lastIndex);
            }

            segment.ClearPendingScopeRange();
        }

        if (created is not null)
        {
            lock (_gate)
            {
                generation.BumpSegments.Remove(created);
                generation.Owner.RemoveSegment(
                    created.Segment);
                created.IsArenaFastSegment = false;
                created.Segment.FreeNow();
                _arenaFastScopedSegment =
                    _arenaFastScopedRollbackPreviousSegment;
                ResetBumpTraversal(generation);
            }
        }
        else if (allocation.Pointer != IntPtr.Zero)
        {
            _arenaFastScopedSegment!.HighCursor =
                allocation.OriginalCursor;
        }

        if (!generation.FastArenaScopedActive
            && generation.ScopedRecordCount == 0
            && generation.ScopedTouchedSegments.Count == 0)
        {
            generation.ScopedBaselineActive = false;
            generation.FastArenaScopedRequestedBaseline = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FinishArenaBumpInitialization()
    {
        if (_arenaFastRollbackCreatedSegment is not null)
        {
            _arenaFastRollbackPreviousSegment = null;
            _arenaFastRollbackCreatedSegment = null;
            _arenaFastRollbackPreviousCursor = 0;
        }

        if (_arenaFastScopedRollbackCreatedSegment is not null)
        {
            _arenaFastScopedRollbackPreviousSegment = null;
            _arenaFastScopedRollbackCreatedSegment = null;
        }

        if (_arenaFastScopedRollbackStartedSegment)
        {
            _arenaFastScopedRollbackStartedSegment = false;
        }

        Volatile.Write(
            ref _arenaFastInitializerActive,
            0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint CalculateArenaByteLength(
        int length,
        int elementSize)
    {
        uint unsignedLength = (uint)length;
        uint unsignedElementSize = (uint)elementSize;
        if (IntPtr.Size == 4)
        {
            ulong byteLength =
                (ulong)unsignedLength * unsignedElementSize;
            if (byteLength > uint.MaxValue)
            {
                ThrowArenaByteLengthOverflow();
            }
        }

        return (nuint)unsignedLength * unsignedElementSize;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowArenaByteLengthOverflow() =>
        throw new OverflowException(
            "The arena allocation byte count overflows native addressable storage.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint AlignArenaBumpCursor(
        nuint value,
        nuint alignment)
    {
        nuint mask = alignment - 1;
        return checked(value + mask) & ~mask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowArenaFastInitializerInUse(
        string operation)
    {
        lock (_gate)
        {
            NativeGeneration generation =
                EnsureActiveLocked(operation);
            throw CreateInUseException(
                operation,
                generation.Number,
                0,
                generation.ActiveOperations,
                "An Arena scratch initializer is already active on this lane.");
        }
    }

    private static unsafe IntPtr AddPointer(
        IntPtr pointer,
        nuint offset) =>
        (IntPtr)((byte*)pointer + checked((nint)offset));

    internal static IntPtr AddArenaTransferPointer(
        IntPtr pointer,
        nuint offset) =>
        AddPointer(pointer, offset);

    internal NativeArenaTransferBatchState CreateArenaTransferBatch(
        int slotCount,
        int length,
        int elementSize,
        nuint alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        ValidateBumpInitializationArguments(
            length,
            elementSize,
            alignment);
        nuint storageBytes = CalculateByteLength(
            length,
            elementSize,
            "Arena transfer batch allocation");
        nuint stride = storageBytes == 0
            ? 0
            : AlignUp(storageBytes, alignment);
        nuint totalBytes = storageBytes == 0
            ? 0
            : checked(
                checked(stride * (nuint)(uint)(slotCount - 1))
                + storageBytes);
        NativeArenaTransferBatchSlot[] batchSlots =
            new NativeArenaTransferBatchSlot[slotCount];

        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked(
                "CreateTransferBatch");
            if (_kind != NativeOwnerKind.Arena)
            {
                throw CreateStateException(
                    "CreateTransferBatch",
                    "Only NativeArena has fixed transfer batches.",
                    0);
            }

            BeginArenaFastBoundary();
            int activeOperations =
                generation.CloseOperationAdmission();
            try
            {
                if (activeOperations != 0
                    || generation.LeaseReturnsInProgress != 0
                    || generation.InitializationsInProgress != 0
                    || HasArenaFastInitializer()
                    || HasArenaFastHazard(generation))
                {
                    throw CreateInUseException(
                        "CreateTransferBatch",
                        generation.Number,
                        0,
                        activeOperations,
                        "The Arena has an active operation during batch setup.");
                }

                if (!ReferenceEquals(
                        _arenaFastGeneration,
                        generation))
                {
                    _arenaFastGeneration = generation;
                    _arenaFastSegment = null;
                    _arenaFastCursor = 0;
                    _arenaFastScopedSegment = null;
                }

                generation.ArenaTransferBatches.EnsureCapacity(
                    checked(
                        generation.ArenaTransferBatches.Count + 1));
                NativeArenaTransferBatchState[] published =
                    new NativeArenaTransferBatchState[
                        checked(
                            generation.ArenaTransferBatches.Count
                            + 1)];
                generation.ArenaTransferBatchSnapshot.CopyTo(
                    published,
                    0);
                NativeBumpSegment? segment = null;
                nuint batchOffset = 0;
                if (totalBytes != 0)
                {
                    segment = ReserveArenaTransferBatchStorageLocked(
                        generation,
                        totalBytes,
                        alignment,
                        out batchOffset);
                }

                IntPtr basePointer = totalBytes == 0
                    ? IntPtr.Zero
                    : AddPointer(
                        segment!.Segment.Pointer,
                        batchOffset);
                NativeArenaTransferBatchState batch = new(
                    this,
                    generation,
                    segment,
                    basePointer,
                    batchSlots,
                    length,
                    storageBytes,
                    stride);
                published[^1] = batch;
                generation.ArenaTransferBatches.Add(batch);
                generation.PublishArenaTransferBatches(published);
                _arenaTransferSlotCreationCount = checked(
                    _arenaTransferSlotCreationCount + slotCount);
                return batch;
            }
            finally
            {
                generation.OpenOperationAdmission();
                EndArenaFastBoundary();
            }
        }
    }

    private NativeBumpSegment ReserveArenaTransferBatchStorageLocked(
        NativeGeneration generation,
        nuint totalBytes,
        nuint alignment,
        out nuint offset)
    {
        foreach (NativeBumpSegment candidate in
            generation.BumpSegments)
        {
            if (candidate.IsArenaFastSegment
                || (!candidate.IsArenaTransferSegment
                    && !candidate.IsCompletelyIdle))
            {
                continue;
            }

            nuint candidateOffset = AlignUp(
                candidate.LowCursor,
                alignment);
            if (candidateOffset > candidate.HighCursor
                || totalBytes
                    > candidate.HighCursor - candidateOffset)
            {
                continue;
            }

            candidate.LowCursor = checked(
                candidateOffset + totalBytes);
            candidate.IsArenaTransferSegment = true;
            _arenaTransferCentralSegment = candidate;
            offset = candidateOffset;
            return candidate;
        }

        nuint segmentBytes = Math.Max(
            RequiredFreshBumpBytes(totalBytes, alignment),
            DefaultBumpSegmentBytes);
        generation.BumpSegments.EnsureCapacity(
            checked(generation.BumpSegments.Count + 1));
        generation.Owner.PrepareAddSegmentCapacity(1);
        NativeSegment nativeSegment = NativeSegment.Allocate(
            segmentBytes,
            _ownerKind,
            generation.Number,
            "arena transfer batch growth",
            _lifecycle,
            zeroed: false);
        NativeBumpSegment created = new(
            nativeSegment,
            NextSegmentOrdinalLocked())
        {
            IsArenaTransferSegment = true
        };
        try
        {
            AppendBumpSegmentLocked(generation, created);
            generation.Owner.AddSegment(nativeSegment);
            _freshSegmentAllocationCount++;
            offset = AlignUp(created.LowCursor, alignment);
            created.LowCursor = checked(offset + totalBytes);
            _arenaTransferCentralSegment = created;
            return created;
        }
        catch
        {
            generation.BumpSegments.Remove(created);
            generation.Owner.RemoveSegment(nativeSegment);
            nativeSegment.FreeNow();
            ResetBumpTraversal(generation);
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryBeginArenaTransferBatchInitialization(
        NativeArenaTransferBatchState batch,
        int slotIndex,
        out NativeArenaTransferBatchReservation reservation)
    {
        reservation = default;
        if (!IsArenaTransferBatchFastPathOpen(batch)
            || !batch.TryBeginInitialization(
                slotIndex,
                out long authority))
        {
            return false;
        }

        if (!IsArenaTransferBatchFastPathOpen(batch))
        {
            batch.AbortInitialization(slotIndex, authority);
            return false;
        }

        reservation = new NativeArenaTransferBatchReservation(
            batch,
            slotIndex,
            authority,
            batch.GetPointer(slotIndex),
            batch.Length);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal NativeArenaTransferBatchReservation
        BeginArenaTransferBatchInitializationSlow<T>(
        NativeArenaTransferBatchState batch,
        int slotIndex)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentOutOfRangeException.ThrowIfNegative(slotIndex);
        if (!ReferenceEquals(batch.Owner, this)
            || (uint)slotIndex >= (uint)batch.Count)
        {
            throw new ArgumentException(
                "The transfer batch does not belong to this Arena.",
                nameof(batch));
        }

        NativeGeneration generation =
            BeginArenaTransferInitialization();
        long authority = 0;
        bool initializing = false;
        try
        {
            if (!ReferenceEquals(batch.Generation, generation))
            {
                throw CreateReturnedException(
                    "ScratchTransferable",
                    batch.Generation.Number,
                    _generation,
                    0,
                    "The transfer batch belongs to an inactive generation.");
            }

            if (!batch.TryBeginInitialization(
                    slotIndex,
                    out authority))
            {
                throw CreateInUseException(
                    "ScratchTransferable",
                    generation.Number,
                    batch.Authority(slotIndex),
                    batch.ActiveOperations(slotIndex),
                    "The fixed transfer slot is already active.");
            }

            initializing = true;
            if (!IsArenaTransferBatchOpen(batch))
            {
                throw CreateArenaTransferBatchUnavailable(
                    batch,
                    authority,
                    "ScratchTransferable");
            }

            IntPtr pointer = batch.GetPointer(slotIndex);
            return new NativeArenaTransferBatchReservation(
                batch,
                slotIndex,
                authority,
                pointer,
                batch.Length);
        }
        catch
        {
            if (initializing)
            {
                batch.AbortInitialization(
                    slotIndex,
                    authority);
            }

            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeArenaTransferBatchReservation
        PublishArenaTransferBatchInitialization(
        NativeArenaTransferBatchState batch,
        int slotIndex,
        long authority,
        IntPtr pointer,
        int length)
    {
        if (!IsArenaTransferBatchFastPathOpen(batch)
            || !batch.TryPublishInitialization(
                slotIndex,
                authority))
        {
            throw CreateArenaTransferBatchUnavailable(
                batch,
                authority,
                "ScratchTransferable");
        }

        return new NativeArenaTransferBatchReservation(
            batch,
            slotIndex,
            authority,
            pointer,
            length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AbortArenaTransferBatchInitialization(
        NativeArenaTransferBatchState batch,
        int slotIndex,
        long authority) =>
        batch.AbortInitialization(slotIndex, authority);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeArenaTransferBatchOperationToken
        EnterArenaTransferBatchOperation(
        NativeArenaTransferBatchState batch,
        int slotIndex,
        long authority,
        string operation)
    {
        if (IsArenaTransferBatchFastPathOpen(batch)
            && batch.TryEnterOperation(
                slotIndex,
                authority))
        {
            if (IsArenaTransferBatchFastPathOpen(batch))
            {
                return new NativeArenaTransferBatchOperationToken(
                    this,
                    batch,
                    slotIndex);
            }

            ExitArenaTransferBatchOperation(
                batch,
                slotIndex);
        }

        return EnterArenaTransferBatchOperationSlow(
            batch,
            slotIndex,
            authority,
            operation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativeArenaTransferBatchOperationToken
        EnterArenaTransferBatchOperationSlow(
        NativeArenaTransferBatchState batch,
        int slotIndex,
        long authority,
        string operation)
    {
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked(
                operation,
                batch.Generation.Number,
                authority);
            if (Volatile.Read(
                    ref _arenaFastBoundaryClosing) != 0)
            {
                throw CreateInUseException(
                    operation,
                    batch.Generation.Number,
                    authority,
                    batch.ActiveOperations(slotIndex),
                    "The Arena is in a lane-boundary transition.");
            }

            if (!ReferenceEquals(generation, batch.Generation)
                || !batch.IsValid
                || !batch.TryEnterOperation(
                    slotIndex,
                    authority))
            {
                throw CreateReturnedException(
                    operation,
                    batch.Generation.Number,
                    _generation,
                    authority,
                    "The fixed transfer slot is not active.");
            }

            return new NativeArenaTransferBatchOperationToken(
                this,
                batch,
                slotIndex);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitArenaTransferBatchOperation(
        NativeArenaTransferBatchState batch,
        int slotIndex)
    {
        _ = batch.ExitOperation(slotIndex);
        NativeGeneration generation = batch.Generation;
        if (!generation.MemoryDetached)
        {
            return;
        }

        lock (_gate)
        {
            FinishOperationExitLocked(generation);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long MoveArenaTransferBatchLease(
        NativeArenaTransferBatchState batch,
        int slotIndex,
        long authority,
        string operation)
    {
        if (!IsArenaTransferBatchFastPathOpen(batch))
        {
            throw CreateArenaTransferBatchUnavailable(
                batch,
                authority,
                operation);
        }

        NativeArenaTransferBatchTransition transition =
            batch.TryMove(
                slotIndex,
                authority,
                out long nextAuthority);
        if (transition == NativeArenaTransferBatchTransition.Success)
        {
            if (IsArenaTransferBatchFastPathOpen(batch))
            {
                return nextAuthority;
            }

            _ = batch.TryReturn(
                slotIndex,
                nextAuthority);
            throw CreateArenaTransferBatchUnavailable(
                batch,
                nextAuthority,
                operation);
        }

        if (transition == NativeArenaTransferBatchTransition.InUse)
        {
            throw new InvalidOperationException(
                "The fixed transfer lease cannot move during an active callback.");
        }

        throw CreateArenaTransferBatchUnavailable(
            batch,
            authority,
            operation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ReturnArenaTransferBatchLease(
        NativeArenaTransferBatchState batch,
        int slotIndex,
        long authority)
    {
        NativeArenaTransferBatchTransition transition =
            batch.TryReturn(slotIndex, authority);
        if (transition == NativeArenaTransferBatchTransition.InUse)
        {
            throw new InvalidOperationException(
                "The fixed transfer lease cannot return during an active callback.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsArenaTransferBatchOpen(
        NativeArenaTransferBatchState batch) =>
        batch.IsValid
        && _kind == NativeOwnerKind.Arena
        && Volatile.Read(ref _arenaFastBoundaryClosing) == 0
        && _lifecycle == NativeOwnerLifecycle.Active
        && ReferenceEquals(
            Volatile.Read(ref _current),
            batch.Generation);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsArenaTransferBatchFastPathOpen(
        NativeArenaTransferBatchState batch) =>
        batch.IsValid
        && Volatile.Read(ref _arenaFastBoundaryClosing) == 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Exception CreateArenaTransferBatchUnavailable(
        NativeArenaTransferBatchState batch,
        long authority,
        string operation)
    {
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked(
                operation,
                batch.Generation.Number,
                authority);
            if (Volatile.Read(
                    ref _arenaFastBoundaryClosing) != 0)
            {
                return CreateInUseException(
                    operation,
                    batch.Generation.Number,
                    authority,
                    activeOperationCount: 0,
                    "The Arena is in a lane-boundary transition.");
            }

            return CreateReturnedException(
                operation,
                batch.Generation.Number,
                generation.Number,
                authority,
                "The fixed transfer slot is not active.");
        }
    }

    internal NativeRegionAllocation LeaseConcurrentBumpInitialized<T>(
        int length,
        int elementSize,
        nuint alignment,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ValidateBumpInitializationArguments(
            length,
            elementSize,
            alignment);
        (
            nuint storageBytes,
            nuint blockBytes,
            int sizeClass,
            int capacity) = CalculateArenaTransferLayout(
                length,
                elementSize);
        NativeGeneration generation =
            BeginArenaTransferInitialization();
        NativeArenaTransferLane? lane = null;
        NativeAllocation? allocation = null;
        bool published = false;
        try
        {
            lane = EnterArenaTransferLane(generation);
            allocation = RentArenaTransferSlot(
                lane,
                length,
                capacity,
                storageBytes,
                blockBytes,
                sizeClass);
            int initializedLength = 0;
            NativeLeaseWriter<T> writer = new(
                allocation,
                ref initializedLength);
            initializer(writer);
            if (initializedLength != length)
            {
                throw new InvalidOperationException(
                    "The native lease initializer did not write all logical elements.");
            }

            allocation.InitializedLength = 0;
            allocation.Lifecycle = NativeAllocationLifecycle.Active;
            published = true;
            return new NativeRegionAllocation(
                allocation.Id,
                allocation);
        }
        catch
        {
            if (allocation is not null && !published)
            {
                allocation.InitializedLength = 0;
                allocation.Lifecycle =
                    NativeAllocationLifecycle.Returned;
                ReturnArenaTransferSlot(allocation);
            }

            throw;
        }
        finally
        {
            if (lane is not null)
            {
                lane.InitializerActive = false;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeArenaTransferLane EnterArenaTransferLane(
        NativeGeneration generation)
    {
        NativeArenaTransferLane lane =
            GetArenaTransferLane(generation);
        if (lane.InitializerActive)
        {
            throw CreateInUseException(
                "ScratchTransferable",
                generation.Number,
                0,
                generation.ActiveOperations,
                "An Arena transfer initializer is already active on this producer lane.");
        }

        lane.InitializerActive = true;
        if (!ReferenceEquals(
                Volatile.Read(ref _current),
                generation)
            || _lifecycle != NativeOwnerLifecycle.Active
            || Volatile.Read(
                ref _arenaFastBoundaryClosing) != 0
            || Volatile.Read(
                ref _arenaFastInitializerActive) != 0
            || Volatile.Read(
                ref generation.ArenaInitializationsInProgress) != 0)
        {
            lane.InitializerActive = false;
            throw CreateInUseException(
                "ScratchTransferable",
                generation.Number,
                0,
                generation.ActiveOperations,
                "The Arena changed before the transfer reservation started.");
        }

        return lane;
    }

    internal static long PackArenaTransferId(
        int slotIndex,
        int version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        uint storedIndex = checked((uint)(slotIndex + 1));
        return ArenaTransferIdMarker
            | ((long)(uint)version << 32)
            | storedIndex;
    }

    private static (
        nuint StorageBytes,
        nuint BlockBytes,
        int SizeClass,
        int Capacity) CalculateArenaTransferLayout(
            int length,
            int elementSize)
    {
        nuint storageBytes = CalculateByteLength(
            length,
            elementSize,
            "Arena transfer allocation");
        if (storageBytes == 0)
        {
            return (0, 0, 0, 0);
        }

        ulong minimum = Math.Max(
            (ulong)storageBytes,
            (ulong)(uint)IntPtr.Size);
        ulong rounded = BitOperations.RoundUpToPowerOf2(
            minimum);
        if (rounded == 0
            || rounded > (ulong)nuint.MaxValue)
        {
            throw new OverflowException(
                "The Arena transfer size class overflows native addressable storage.");
        }

        nuint blockBytes = (nuint)rounded;
        int sizeClass = checked(
            BitOperations.Log2(rounded) + 1);
        nuint elementCapacity = blockBytes
            / (nuint)(uint)elementSize;
        int capacity = elementCapacity > int.MaxValue
            ? int.MaxValue
            : (int)elementCapacity;
        return (
            storageBytes,
            blockBytes,
            sizeClass,
            capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeGeneration BeginArenaTransferInitialization()
    {
        NativeGeneration? generation =
            Volatile.Read(ref _current);
        if (_kind == NativeOwnerKind.Arena
            && generation is not null
            && _lifecycle == NativeOwnerLifecycle.Active
            && Volatile.Read(
                ref _arenaFastBoundaryClosing) == 0
            && Volatile.Read(
                ref _arenaFastInitializerActive) == 0
            && Volatile.Read(
                ref generation.ArenaInitializationsInProgress) == 0)
        {
            return generation;
        }

        return BeginArenaTransferInitializationSlow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativeGeneration
        BeginArenaTransferInitializationSlow()
    {
        lock (_gate)
        {
            _arenaTransferSlowPathCount = checked(
                _arenaTransferSlowPathCount + 1);
            NativeGeneration generation = EnsureActiveLocked(
                "ScratchTransferable");
            if (_kind != NativeOwnerKind.Arena)
            {
                throw CreateStateException(
                    "ScratchTransferable",
                    "Only NativeArena has transferable scratch lanes.",
                    0);
            }

            if (Volatile.Read(
                    ref _arenaFastInitializerActive) != 0
                || generation.InitializationsInProgress != 0)
            {
                throw CreateInUseException(
                    "ScratchTransferable",
                    generation.Number,
                    0,
                    generation.ActiveOperations,
                    "A different native lease initializer is already active.");
            }

            if (Volatile.Read(
                    ref _arenaFastBoundaryClosing) != 0)
            {
                throw CreateInUseException(
                    "ScratchTransferable",
                    generation.Number,
                    0,
                    generation.ActiveOperations,
                    "The Arena is in a lane-boundary transition.");
            }

            return generation;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeArenaTransferLane GetArenaTransferLane(
        NativeGeneration generation)
    {
        int threadId = Environment.CurrentManagedThreadId;
        WeakReference<NativeArenaTransferLane>? cached =
            _cachedArenaTransferLane;
        if (cached is not null
            && cached.TryGetTarget(
                out NativeArenaTransferLane? lane)
            && ReferenceEquals(lane.Owner, this)
            && ReferenceEquals(lane.Generation, generation)
            && lane.ThreadId == threadId)
        {
            return lane;
        }

        return GetArenaTransferLaneSlow(
            generation,
            threadId);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativeArenaTransferLane GetArenaTransferLaneSlow(
        NativeGeneration generation,
        int threadId)
    {
        lock (_gate)
        {
            _arenaTransferSlowPathCount = checked(
                _arenaTransferSlowPathCount + 1);
            if (!ReferenceEquals(generation, _current)
                || _lifecycle != NativeOwnerLifecycle.Active)
            {
                throw CreateStateException(
                    "ScratchTransferable",
                    "The Arena changed before the producer lane became active.",
                    0);
            }

            foreach (NativeArenaTransferLane candidate in
                generation.ArenaTransferLanes)
            {
                if (candidate.ThreadId != threadId)
                {
                    continue;
                }

                CacheArenaTransferLane(candidate);
                return candidate;
            }

            generation.ArenaTransferLanes.EnsureCapacity(
                checked(generation.ArenaTransferLanes.Count + 1));
            NativeArenaTransferLane created = new(
                this,
                generation,
                threadId,
                ArenaTransferSizeClassCount);
            generation.ArenaTransferLanes.Add(created);
            generation.PublishArenaTransferLanes(
                generation.ArenaTransferLanes.ToArray());
            CacheArenaTransferLane(created);
            return created;
        }
    }

    private static void CacheArenaTransferLane(
        NativeArenaTransferLane lane)
    {
        WeakReference<NativeArenaTransferLane>? cached =
            _cachedArenaTransferLane;
        if (cached is null)
        {
            _cachedArenaTransferLane = new(
                lane);
            return;
        }

        cached.SetTarget(lane);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeAllocation RentArenaTransferSlot(
        NativeArenaTransferLane lane,
        int length,
        int capacity,
        nuint storageBytes,
        nuint blockBytes,
        int sizeClass)
    {
        NativeGeneration generation = lane.Generation;
        NativeAllocation? allocation =
            PopArenaTransferFreeSlot(
                lane,
                sizeClass);
        if (allocation is null)
        {
            allocation = PopArenaTransferRemoteSlot(
                lane,
                sizeClass);
        }

        if (allocation is null
            && (lane.UnusedSlotHead == 0
                || (blockBytes != 0
                    && !HasArenaTransferChunkSpace(
                        lane,
                        sizeClass,
                        blockBytes))))
        {
            allocation = RefillArenaTransferLaneSlow(
                lane,
                sizeClass,
                blockBytes);
        }

        NativeBumpSegment? segment;
        nuint offset;
        if (allocation is null)
        {
            allocation = PopArenaTransferUnusedSlot(lane);
            if (blockBytes == 0)
            {
                segment = null;
                offset = 0;
            }
            else
            {
                segment = lane.ChunkSegments[sizeClass]
                    ?? throw new InvalidOperationException(
                        "The Arena transfer lane has no active chunk.");
                nuint cursor = AlignUp(
                    lane.ChunkCursors[sizeClass],
                    (nuint)IntPtr.Size);
                offset = cursor;
                lane.ChunkCursors[sizeClass] = checked(
                    cursor + blockBytes);
            }
        }
        else
        {
            segment = allocation.BumpSegment;
            offset = allocation.OffsetBytes;
        }

        _ = allocation.ResetArenaTransfer(
            lane,
            segment,
            offset,
            length,
            capacity,
            storageBytes,
            sizeClass,
            blockBytes);
        return allocation;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasArenaTransferChunkSpace(
        NativeArenaTransferLane lane,
        int sizeClass,
        nuint blockBytes)
    {
        NativeBumpSegment? segment =
            lane.ChunkSegments[sizeClass];
        if (segment is null)
        {
            return false;
        }

        nuint cursor = AlignUp(
            lane.ChunkCursors[sizeClass],
            (nuint)IntPtr.Size);
        nuint limit = lane.ChunkLimits[sizeClass];
        return cursor <= limit
            && blockBytes <= limit - cursor;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativeAllocation? RefillArenaTransferLaneSlow(
        NativeArenaTransferLane lane,
        int sizeClass,
        nuint blockBytes)
    {
        lock (_gate)
        {
            _arenaTransferSlowPathCount = checked(
                _arenaTransferSlowPathCount + 1);
            NativeGeneration generation = EnsureActiveLocked(
                "ScratchTransferable");
            if (!ReferenceEquals(generation, lane.Generation)
                || Volatile.Read(
                    ref _arenaFastBoundaryClosing) != 0)
            {
                throw CreateStateException(
                    "ScratchTransferable",
                    "The Arena changed before the producer lane refilled.",
                    0);
            }

            NativeAllocation? recycled =
                PopArenaTransferRemoteSlot(
                    lane,
                    sizeClass)
                ?? StealArenaTransferSlotLocked(
                    generation,
                    lane,
                    sizeClass);
            if (recycled is not null)
            {
                return recycled;
            }

            if (lane.UnusedSlotHead == 0)
            {
                AddArenaTransferSlotBatchLocked(
                    generation,
                    lane);
            }

            if (blockBytes != 0
                && !HasArenaTransferChunkSpace(
                    lane,
                    sizeClass,
                    blockBytes))
            {
                ReserveArenaTransferChunkLocked(
                    generation,
                    lane,
                    sizeClass,
                    blockBytes);
            }

            return null;
        }
    }

    private static NativeAllocation?
        StealArenaTransferSlotLocked(
        NativeGeneration generation,
        NativeArenaTransferLane receivingLane,
        int sizeClass)
    {
        foreach (NativeArenaTransferLane candidate in
            generation.ArenaTransferLanes)
        {
            if (ReferenceEquals(candidate, receivingLane))
            {
                continue;
            }

            NativeAllocation? allocation =
                PopArenaTransferFreeSlot(
                    candidate,
                    sizeClass);
            allocation ??= PopArenaTransferRemoteSlot(
                candidate,
                sizeClass);
            if (allocation is not null)
            {
                return allocation;
            }
        }

        return null;
    }

    private void AddArenaTransferSlotBatchLocked(
        NativeGeneration generation,
        NativeArenaTransferLane lane)
    {
        int firstIndex = generation.ArenaTransferSlotCount;
        int requiredCount = checked(
            firstIndex + ArenaTransferSlotBatchSize);
        NativeAllocation?[] slots =
            generation.ArenaTransferSlots;
        if (slots.Length < requiredCount)
        {
            int doubled = slots.Length == 0
                ? ArenaTransferSlotBatchSize
                : checked(slots.Length * 2);
            int capacity = Math.Max(
                doubled,
                requiredCount);
            NativeAllocation?[] expanded = new NativeAllocation?[capacity];
            slots.CopyTo(expanded, 0);
            slots = expanded;
        }

        for (int index = firstIndex;
            index < requiredCount;
            index++)
        {
            NativeAllocation allocation = new(
                generation,
                id: 0,
                slab: null,
                bumpSegment: null,
                offsetBytes: 0,
                length: 0,
                capacity: 0,
                storageBytes: 0,
                referenceRoots: null,
                scoped: false,
                scopeEpoch: 0);
            allocation.ConfigureArenaTransferSlot(index);
            allocation.ArenaTransferNextSlot =
                lane.UnusedSlotHead;
            lane.UnusedSlotHead = checked(index + 1);
            slots[index] = allocation;
        }

        generation.ArenaTransferSlotCount = requiredCount;
        generation.PublishArenaTransferSlots(slots);
        _arenaTransferSlotCreationCount = checked(
            _arenaTransferSlotCreationCount
            + ArenaTransferSlotBatchSize);
    }

    private void ReserveArenaTransferChunkLocked(
        NativeGeneration generation,
        NativeArenaTransferLane lane,
        int sizeClass,
        nuint blockBytes)
    {
        bool firstChunk =
            lane.ChunkSegments[sizeClass] is null;
        nuint desiredBytes = blockBytes;
        if (!firstChunk)
        {
            try
            {
                desiredBytes = checked(
                    blockBytes
                    * ArenaTransferSlotBatchSize);
            }
            catch (OverflowException)
            {
                desiredBytes = blockBytes;
            }

            if (desiredBytes < ArenaTransferMinimumChunkBytes)
            {
                desiredBytes = ArenaTransferMinimumChunkBytes;
            }
        }

        if (!ReferenceEquals(
                _arenaTransferGeneration,
                generation))
        {
            _arenaTransferGeneration = generation;
            _arenaTransferCentralSegment = null;
        }

        NativeBumpSegment? segment =
            _arenaTransferCentralSegment;
        if (!TryReserveArenaTransferChunk(
                segment,
                desiredBytes,
                blockBytes,
                out nuint offset,
                out nuint chunkBytes))
        {
            segment = null;
            foreach (NativeBumpSegment candidate in
                generation.BumpSegments)
            {
                if (candidate.IsArenaFastSegment
                    || (!candidate.IsArenaTransferSegment
                        && !candidate.IsCompletelyIdle)
                    || !TryReserveArenaTransferChunk(
                        candidate,
                        desiredBytes,
                        blockBytes,
                        out offset,
                        out chunkBytes))
                {
                    continue;
                }

                segment = candidate;
                break;
            }

            if (segment is null)
            {
                nuint segmentBytes = desiredBytes > DefaultBumpSegmentBytes
                    ? desiredBytes
                    : DefaultBumpSegmentBytes;
                generation.BumpSegments.EnsureCapacity(
                    checked(generation.BumpSegments.Count + 1));
                generation.Owner.PrepareAddSegmentCapacity(1);
                NativeSegment nativeSegment = NativeSegment.Allocate(
                    segmentBytes,
                    _ownerKind,
                    generation.Number,
                    "arena transfer lane growth",
                    _lifecycle,
                    zeroed: false);
                NativeBumpSegment created = new(
                    nativeSegment,
                    NextSegmentOrdinalLocked())
                {
                    IsArenaTransferSegment = true
                };
                try
                {
                    AppendBumpSegmentLocked(
                        generation,
                        created);
                    generation.Owner.AddSegment(nativeSegment);
                    _freshSegmentAllocationCount++;
                    segment = created;
                    if (!TryReserveArenaTransferChunk(
                            segment,
                            desiredBytes,
                            blockBytes,
                            out offset,
                            out chunkBytes))
                    {
                        throw new InvalidOperationException(
                            "The Arena transfer segment did not provide its required chunk.");
                    }
                }
                catch
                {
                    generation.BumpSegments.Remove(created);
                    generation.Owner.RemoveSegment(nativeSegment);
                    nativeSegment.FreeNow();
                    ResetBumpTraversal(generation);
                    throw;
                }
            }
        }

        segment!.IsArenaTransferSegment = true;
        _arenaTransferCentralSegment = segment;
        lane.ChunkSegments[sizeClass] = segment;
        lane.ChunkCursors[sizeClass] = offset;
        lane.ChunkLimits[sizeClass] = checked(
            offset + chunkBytes);
    }

    private static bool TryReserveArenaTransferChunk(
        NativeBumpSegment? segment,
        nuint desiredBytes,
        nuint minimumBytes,
        out nuint offset,
        out nuint chunkBytes)
    {
        offset = 0;
        chunkBytes = 0;
        if (segment is null
            || segment.IsArenaFastSegment
            || segment.HighCursor < segment.LowCursor)
        {
            return false;
        }

        nuint candidate = AlignUp(
            segment.LowCursor,
            (nuint)IntPtr.Size);
        if (candidate > segment.HighCursor
            || minimumBytes
                > segment.HighCursor - candidate)
        {
            return false;
        }

        nuint available = segment.HighCursor - candidate;
        chunkBytes = desiredBytes < available
            ? desiredBytes
            : available;
        segment.LowCursor = checked(candidate + chunkBytes);
        segment.IsArenaTransferSegment = true;
        offset = candidate;
        return true;
    }

    private static NativeAllocation?
        PopArenaTransferFreeSlot(
            NativeArenaTransferLane lane,
            int sizeClass)
    {
        while (true)
        {
            long observed = Volatile.Read(
                ref lane.FreeSlotHeads[sizeClass]);
            int storedIndex = unchecked((int)(uint)observed);
            if (storedIndex == 0)
            {
                return null;
            }

            NativeAllocation allocation =
                GetArenaTransferSlot(
                    lane.Generation,
                    storedIndex);
            int nextIndex = allocation.ArenaTransferNextSlot;
            long updated = AdvanceArenaTransferHead(
                observed,
                nextIndex);
            if (Interlocked.CompareExchange(
                    ref lane.FreeSlotHeads[sizeClass],
                    updated,
                    observed) == observed)
            {
                allocation.ArenaTransferNextSlot = 0;
                return allocation;
            }
        }
    }

    private static NativeAllocation
        PopArenaTransferUnusedSlot(
            NativeArenaTransferLane lane)
    {
        int head = lane.UnusedSlotHead;
        if (head == 0)
        {
            throw new InvalidOperationException(
                "The Arena transfer lane has no unused publication slot.");
        }

        NativeAllocation allocation =
            GetArenaTransferSlot(
                lane.Generation,
                head);
        lane.UnusedSlotHead =
            allocation.ArenaTransferNextSlot;
        allocation.ArenaTransferNextSlot = 0;
        return allocation;
    }

    private static NativeAllocation GetArenaTransferSlot(
        NativeGeneration generation,
        int storedIndex)
    {
        NativeAllocation?[] slots =
            generation.ArenaTransferSlots;
        int index = checked(storedIndex - 1);
        return (uint)index < (uint)slots.Length
            ? slots[index]
                ?? throw new InvalidOperationException(
                    "The Arena transfer slot is not published.")
            : throw new InvalidOperationException(
                "The Arena transfer slot index is outside the publication table.");
    }

    private static NativeAllocation?
        PopArenaTransferRemoteSlot(
        NativeArenaTransferLane lane,
        int sizeClass)
    {
        while (true)
        {
            long observed = Volatile.Read(
                ref lane.RemoteFreeSlotHeads[sizeClass]);
            int storedIndex = unchecked((int)(uint)observed);
            if (storedIndex == 0)
            {
                return null;
            }

            NativeAllocation allocation =
                GetArenaTransferSlot(
                    lane.Generation,
                    storedIndex);
            int nextIndex = allocation.ArenaTransferNextSlot;
            long updated = AdvanceArenaTransferHead(
                observed,
                nextIndex);
            if (Interlocked.CompareExchange(
                    ref lane.RemoteFreeSlotHeads[sizeClass],
                    updated,
                    observed) == observed)
            {
                allocation.ArenaTransferNextSlot = 0;
                return allocation;
            }
        }
    }

    private static void ReturnArenaTransferSlot(
        NativeAllocation allocation)
    {
        NativeArenaTransferLane lane = allocation.ArenaTransferLane
            ?? throw new InvalidOperationException(
                "The Arena transfer slot has no producer lane.");
        int storedIndex = checked(
            allocation.ArenaTransferSlotIndex + 1);
        int sizeClass = allocation.ArenaTransferSizeClass;
        if (Environment.CurrentManagedThreadId == lane.ThreadId)
        {
            PushArenaTransferSlot(
                ref lane.FreeSlotHeads[sizeClass],
                allocation,
                storedIndex);
            return;
        }

        PushArenaTransferSlot(
            ref lane.RemoteFreeSlotHeads[sizeClass],
            allocation,
            storedIndex);
    }

    private static void PushArenaTransferSlot(
        ref long head,
        NativeAllocation allocation,
        int storedIndex)
    {
        long observed;
        do
        {
            observed = Volatile.Read(ref head);
            allocation.ArenaTransferNextSlot =
                unchecked((int)(uint)observed);
        }
        while (Interlocked.CompareExchange(
            ref head,
            AdvanceArenaTransferHead(
                observed,
                storedIndex),
            observed) != observed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long AdvanceArenaTransferHead(
        long observed,
        int storedIndex)
    {
        uint stamp = unchecked((uint)((ulong)observed >> 32) + 1);
        return unchecked(
            ((long)stamp << 32)
            | (uint)storedIndex);
    }

    private NativeRegionAllocation LeaseBumpInitializedCore<T>(
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
            Volatile.Write(
                ref generation.ArenaInitializationsInProgress,
                generation.InitializationsInProgress);
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
            if (segment.IsArenaTransferSegment)
            {
                continue;
            }

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

        if (_kind == NativeOwnerKind.Arena
            && HasArenaFastInitializer())
        {
            throw CreateInUseException(
                scoped ? "LeaseScoped" : "Lease",
                generation.Number,
                0,
                generation.ActiveOperations,
                "An Arena scratch initializer is already active on the owner lane.");
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
                bumpSegment = FindBumpSpaceLocked(
                    generation,
                    byteLength,
                    alignment,
                    scoped);
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
                    offset = AlignUp(
                        bumpSegment.LowCursor,
                        alignment);
                    bumpSegment.LowCursor = checked(
                        offset + byteLength);
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

            int initializationCount = checked(
                generation.InitializationsInProgress + 1);
            generation.InitializationsInProgress = initializationCount;
            Volatile.Write(
                ref generation.ArenaInitializationsInProgress,
                generation.InitializationsInProgress);

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
            else if (bumpSegment is not null
                && cursorCaptured)
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
        Volatile.Write(
            ref generation.ArenaInitializationsInProgress,
            generation.InitializationsInProgress);
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
        Volatile.Write(
            ref generation.ArenaInitializationsInProgress,
            generation.InitializationsInProgress);
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
            Volatile.Write(
                ref generation.ArenaInitializationsInProgress,
                generation.InitializationsInProgress);
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
            Volatile.Write(
                ref generation.ArenaInitializationsInProgress,
                generation.InitializationsInProgress);
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

    internal NativeHandleMetadata ValidateArenaFastHandle(
        NativeArenaAllocation allocation,
        string operation)
    {
        NativeArenaOperationToken token =
            EnterArenaFastOperationCore(
                allocation,
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

    internal NativeArenaOperationToken EnterArenaFastOperation(
        NativeArenaAllocation allocation,
        string operation) =>
        EnterArenaFastOperationCore(
            allocation,
            operation,
            notifyHooks: true);

    private NativeArenaOperationToken EnterArenaFastOperationCore(
        NativeArenaAllocation allocation,
        string operation,
        bool notifyHooks)
    {
        if (notifyHooks)
        {
            NativeMemoryTestHooks.NotifyBeforeOperationEntry(
                operation,
                this);
        }

        NativeGeneration generation =
            allocation.GenerationState;
        NativeArenaOperationToken token;
        if (IsArenaFastThread
            && TryEnterArenaFastLocalOperation(
                allocation,
                operation,
                out token))
        {
            goto Entered;
        }

        if (IsArenaFastHandleActive(allocation)
            && generation.TryEnterOperation())
        {
            if (IsArenaFastHandleActive(allocation))
            {
                token = new NativeArenaOperationToken(
                    this,
                    generation,
                    allocation.Pointer,
                    allocation.Length,
                    localOperation: false,
                    operation);
                goto Entered;
            }

            ExitGenerationOperation(generation);
        }

        token = EnterArenaFastOperationSlow(
            allocation,
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
                allocation.Generation,
                allocationId: 0);
        }
        catch
        {
            token.Dispose();
            throw;
        }

        return token;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeArenaLocalOperationToken
        EnterArenaFastLocalOperation(
            NativeArenaAllocation allocation,
            string operation)
    {
        if (!TryEnterArenaFastLocalState(allocation))
        {
            ThrowArenaFastLocalOperation(
                allocation,
                operation);
        }

        return new NativeArenaLocalOperationToken(
            this,
            allocation.GenerationState);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryEnterArenaFastLocalOperation(
        NativeArenaAllocation allocation,
        string operation,
        out NativeArenaOperationToken token)
    {
        NativeGeneration generation =
            allocation.GenerationState;
        if (!TryEnterArenaFastLocalState(allocation))
        {
            token = default;
            return false;
        }

        token = new NativeArenaOperationToken(
            this,
            generation,
            allocation.Pointer,
            allocation.Length,
            localOperation: true,
            operation);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryEnterArenaFastLocalState(
        NativeArenaAllocation allocation)
    {
        NativeGeneration generation =
            allocation.GenerationState;
        int depth = _arenaFastOperationDepth;
        if (depth != 0)
        {
            if (!ReferenceEquals(
                    _arenaFastHazardGeneration,
                    generation)
                || !IsArenaFastHandleActive(allocation))
            {
                return false;
            }

            _arenaFastOperationDepth = checked(depth + 1);
            return true;
        }

        Volatile.Write(
            ref _arenaFastHazardGeneration,
            generation);
        if (Volatile.Read(
                ref _arenaFastBoundaryClosing)
                != 0
            || !IsArenaFastHandleActive(allocation))
        {
            Volatile.Write(
                ref _arenaFastHazardGeneration,
                null);
            return false;
        }

        _arenaFastOperationDepth = 1;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowArenaFastLocalOperation(
        NativeArenaAllocation allocation,
        string operation)
    {
        NativeGeneration allocationGeneration =
            allocation.GenerationState;
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked(
                operation,
                allocationGeneration.Number,
                allocationId: 0);
            if (!ReferenceEquals(
                    generation,
                    allocationGeneration)
                || !IsArenaFastHandleActive(
                    allocation))
            {
                throw CreateReturnedException(
                    operation,
                    allocationGeneration.Number,
                    _generation,
                    allocationId: 0,
                    "The Arena scratch range belongs to an inactive generation.");
            }

            throw CreateInUseException(
                operation,
                allocationGeneration.Number,
                allocationId: 0,
                generation.ActiveOperations,
                "The Arena lane is in a lifecycle transition.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativeArenaOperationToken EnterArenaFastOperationSlow(
        NativeArenaAllocation allocation,
        string operation)
    {
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked(
                operation,
                allocation.Generation,
                allocationId: 0);
            if (!ReferenceEquals(
                    generation,
                    allocation.GenerationState)
                || !IsArenaFastHandleActive(allocation))
            {
                throw CreateReturnedException(
                    operation,
                    allocation.Generation,
                    _generation,
                    allocationId: 0,
                    "The Arena scratch range belongs to an inactive generation.");
            }

            if (!generation.TryEnterOperation())
            {
                throw CreateInUseException(
                    operation,
                    allocation.Generation,
                    allocationId: 0,
                    generation.ActiveOperations,
                    "The Arena generation does not accept a new operation.");
            }

            return new NativeArenaOperationToken(
                this,
                generation,
                allocation.Pointer,
                allocation.Length,
                localOperation: false,
                operation);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsArenaFastHandleActive(
        NativeArenaAllocation allocation) =>
        IsArenaFastHandleActive(allocation.GenerationState)
        && (!allocation.Scoped
            || allocation.ScopeEpoch
                == allocation.GenerationState.ScopeEpoch);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsArenaFastHandleActive(
        NativeGeneration generation) =>
        ReferenceEquals(
            Volatile.Read(ref _current),
            generation)
        && _lifecycle == NativeOwnerLifecycle.Active;

    internal NativeAllocation MaterializeArenaAllocation(
        NativeArenaAllocation allocation,
        nuint storageBytes,
        string operation)
    {
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked(
                operation,
                allocation.Generation,
                allocationId: 0);
            if (!ReferenceEquals(
                    generation,
                    allocation.GenerationState)
                || !IsArenaFastHandleActive(allocation))
            {
                throw CreateReturnedException(
                    operation,
                    allocation.Generation,
                    _generation,
                    allocationId: 0,
                    "The Arena scratch range belongs to an inactive generation.");
            }

            List<NativeAllocation>? materialized =
                generation.ArenaCompositeAllocations;
            if (materialized is not null)
            {
                foreach (NativeAllocation candidate in
                    materialized)
                {
                    if (candidate.Length
                            == allocation.Length
                        && candidate.StorageBytes
                            == storageBytes
                        && candidate.IsScoped
                            == allocation.Scoped
                        && (!allocation.Scoped
                            || candidate.ScopeEpoch
                                == allocation.ScopeEpoch)
                        && candidate.Lifecycle
                            == NativeAllocationLifecycle.Active
                        && GetAllocationPointer(candidate)
                            == allocation.Pointer)
                    {
                        return candidate;
                    }
                }
            }

            NativeBumpSegment? bumpSegment = null;
            nuint offsetBytes = 0;
            if (storageBytes != 0)
            {
                (bumpSegment, offsetBytes) =
                    FindArenaBumpRangeLocked(
                        generation,
                        allocation.Pointer,
                        storageBytes,
                        operation);
            }

            long allocationId = NextAllocationIdLocked();
            NativeAllocation created = new(
                generation,
                allocationId,
                slab: null,
                bumpSegment,
                offsetBytes,
                allocation.Length,
                allocation.Capacity,
                storageBytes,
                referenceRoots: null,
                allocation.Scoped,
                allocation.Scoped
                    ? allocation.ScopeEpoch
                    : generation.ScopeEpoch)
            {
                Lifecycle = NativeAllocationLifecycle.Active
            };
            materialized ??= [];
            materialized.Add(created);
            generation.ArenaCompositeAllocations =
                materialized;
            return created;
        }
    }

    private static IntPtr GetAllocationPointer(
        NativeAllocation allocation)
    {
        NativeSegment? segment = allocation.Segment;
        return segment is null
            ? IntPtr.Zero
            : AddPointer(
                segment.Pointer,
                allocation.OffsetBytes);
    }

    private static (
        NativeBumpSegment Segment,
        nuint OffsetBytes) FindArenaBumpRangeLocked(
            NativeGeneration generation,
            IntPtr pointer,
            nuint storageBytes,
            string operation)
    {
        nuint address = (nuint)pointer;
        foreach (NativeBumpSegment candidate in
            generation.BumpSegments)
        {
            nuint start = (nuint)candidate.Segment.Pointer;
            if (address < start)
            {
                continue;
            }

            nuint offset = address - start;
            if (offset <= candidate.Segment.ByteLength
                && storageBytes
                    <= candidate.Segment.ByteLength - offset)
            {
                return (candidate, offset);
            }
        }

        throw new NativeAllocationStateException(
            "The Arena scratch range does not belong to an active segment.",
            "NativeArena",
            generation.Number,
            generation.Number,
            operation,
            generation.ActiveOperations,
            allocationId: 0,
            NativeOwnerLifecycle.Active);
    }

    internal void ExitArenaFastOperation(
        NativeGeneration generation,
        string operation)
    {
        _ = operation;
        ExitGenerationOperation(generation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitArenaFastLocalOperation(
        NativeGeneration generation,
        string operation)
    {
        _ = operation;
        int depth = _arenaFastOperationDepth;
        if (depth <= 0)
        {
            throw new InvalidOperationException(
                "The Arena lane operation count is already zero.");
        }

        depth--;
        _arenaFastOperationDepth = depth;
        if (depth != 0)
        {
            return;
        }

        Volatile.Write(
            ref _arenaFastHazardGeneration,
            null);
        if (!generation.MemoryDetached
            && ReferenceEquals(
                Volatile.Read(ref _current),
                generation))
        {
            return;
        }

        lock (_gate)
        {
            FinishOperationExitLocked(generation);
        }
    }

    internal void TransferLeaseAuthority<TState>(
        NativeGeneration expectedGeneration,
        NativeAllocation expectedAllocation,
        long generationNumber,
        long allocationId,
        string operation,
        TState state,
        Action<TState> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        NativeMemoryTestHooks.NotifyBeforeOperationEntry(
            operation,
            this);
        if (expectedAllocation.IsArenaTransferSlot)
        {
            TransferArenaSlotAuthority(
                expectedGeneration,
                expectedAllocation,
                generationNumber,
                allocationId,
                operation,
                state,
                publish);
            return;
        }

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
                    "The transfer authority belongs to an inactive allocation.");
            }

            publish(state);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TransferArenaSlotAuthority<TState>(
        NativeGeneration generation,
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        string operation,
        TState state,
        Action<TState> publish)
    {
        if (Volatile.Read(
                ref _arenaFastBoundaryClosing) == 0
            && IsDirectHandleActive(
                generation,
                allocation,
                generationNumber,
                allocationId)
            && allocation.TryEnterOperation())
        {
            try
            {
                if (Volatile.Read(
                        ref _arenaFastBoundaryClosing) == 0
                    && IsDirectHandleActive(
                        generation,
                        allocation,
                        generationNumber,
                        allocationId))
                {
                    publish(state);
                    return;
                }
            }
            finally
            {
                allocation.ExitOperation();
            }
        }

        TransferArenaSlotAuthoritySlow(
            generation,
            allocation,
            generationNumber,
            allocationId,
            operation,
            state,
            publish);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TransferArenaSlotAuthoritySlow<TState>(
        NativeGeneration expectedGeneration,
        NativeAllocation expectedAllocation,
        long generationNumber,
        long allocationId,
        string operation,
        TState state,
        Action<TState> publish)
    {
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked(
                operation,
                generationNumber,
                allocationId);
            if (Volatile.Read(
                    ref _arenaFastBoundaryClosing) != 0)
            {
                throw CreateInUseException(
                    operation,
                    generationNumber,
                    allocationId,
                    expectedAllocation.ActiveOperations,
                    "The Arena is in a lane-boundary transition.");
            }

            if (!ReferenceEquals(
                    generation,
                    expectedGeneration)
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
                    "The transfer authority belongs to an inactive allocation.");
            }

            if (!expectedAllocation.TryEnterOperation())
            {
                throw CreateInUseException(
                    operation,
                    generationNumber,
                    allocationId,
                    expectedAllocation.ActiveOperations,
                    "The transfer slot does not accept an operation.");
            }

            try
            {
                publish(state);
            }
            finally
            {
                expectedAllocation.ExitOperation();
            }
        }
    }

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
        if (allocation.IsArenaTransferSlot)
        {
            token = EnterArenaTransferOperation(
                generation,
                allocation,
                generationNumber,
                allocationId,
                operation);
            goto Entered;
        }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeOperationToken EnterArenaTransferOperation(
        NativeGeneration generation,
        NativeAllocation allocation,
        long generationNumber,
        long allocationId,
        string operation)
    {
        if (Volatile.Read(
                ref _arenaFastBoundaryClosing) == 0
            && IsDirectHandleActive(
                generation,
                allocation,
                generationNumber,
                allocationId)
            && allocation.TryEnterOperation())
        {
            if (Volatile.Read(
                    ref _arenaFastBoundaryClosing) == 0
                && IsDirectHandleActive(
                    generation,
                    allocation,
                    generationNumber,
                    allocationId))
            {
                return new NativeOperationToken(
                    this,
                    generation,
                    allocation,
                    allocationEntered: true,
                    operation,
                    generationEntered: false);
            }

            allocation.ExitOperation();
        }

        return EnterArenaTransferOperationSlow(
            generation,
            allocation,
            generationNumber,
            allocationId,
            operation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativeOperationToken EnterArenaTransferOperationSlow(
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
            if (Volatile.Read(
                    ref _arenaFastBoundaryClosing) != 0)
            {
                throw CreateInUseException(
                    operation,
                    generationNumber,
                    allocationId,
                    expectedAllocation.ActiveOperations,
                    "The Arena is in a lane-boundary transition.");
            }

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
                    "The transfer slot is not active.");
            }

            if (!expectedAllocation.TryEnterOperation())
            {
                throw CreateInUseException(
                    operation,
                    generationNumber,
                    allocationId,
                    expectedAllocation.ActiveOperations,
                    "The transfer slot does not accept an operation.");
            }

            return new NativeOperationToken(
                this,
                generation,
                expectedAllocation,
                allocationEntered: true,
                operation,
                generationEntered: false);
        }
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
        bool generationEntered,
        string operation)
    {
        _ = operation;
        int allocationOperations = -1;
        if (allocationEntered)
        {
            allocationOperations = allocation.ExitOperation();
        }

        if (generationEntered)
        {
            ExitGenerationOperation(generation);
            return;
        }

        if (allocation.IsArenaTransferSlot
            && allocationOperations == 0
            && (generation.MemoryDetached
                || !ReferenceEquals(
                    Volatile.Read(ref _current),
                    generation)))
        {
            lock (_gate)
            {
                FinishOperationExitLocked(generation);
            }
        }
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
            if (generation.ActiveOperations == 0
                && !HasArenaFastHazard(generation))
            {
                DrainDetachedGenerationLocked(generation);
            }

            return;
        }

        if (generation != _current
            && generation.ActiveOperations == 0
            && !HasArenaFastHazard(generation)
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
        if (allocationId < 0)
        {
            ReturnArenaTransferLease(
                generationNumber,
                allocationId,
                operation);
            return;
        }

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
            if (generation.Number != generationNumber || !generation.Allocations.TryGetValue(allocationId, out NativeAllocation allocation))
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
                    generation.HotPoolAllocation =
                        generation.ReusableAllocations.Count == 1
                        && generation.AvailableSlabs.Count == 1
                            ? allocation
                            : null;
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
            EndArenaFastBoundary();
        }
    }

    internal void RecycleScoped()
    {
        lock (_gate)
        {
            NativeGeneration generation = EnsureActiveLocked("RecycleScoped");
            if (generation.ScopedRecordCount == 0
                && !generation.FastArenaScopedActive
                && generation.ScopedTouchedSegments.Count == 0)
            {
                return;
            }

            BeginArenaFastBoundary();
            int activeOperations =
                generation.CloseOperationAdmission();
            try
            {
                if (activeOperations != 0
                    || generation.LeaseReturnsInProgress != 0
                    || generation.InitializationsInProgress != 0
                    || HasArenaFastInitializer()
                    || HasArenaFastHazard(generation))
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
                    checked(
                        generation.ScopedRecordCount
                        + (generation.FastArenaScopedActive ? 1 : 0)),
                    slabTransferCount: 0,
                    bumpTransferCount: 0);
                if (referenceClearCount != 0)
                {
                    EnsureNoInjectedClearFailureLocked(
                        "RecycleScoped");
                }

                RecordArenaScopedReclaimedRanges(generation);

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
                            segment.HighCursor,
                            segment.PendingScopeRangeEnd);
                        segment.HighCursor =
                            segment.PendingScopeRangeEnd;
                    }

                    segment.ClearPendingScopeRange();
                }

                generation.ScopedCleanupPending.Clear();
                RecycleArenaCompositeAllocationsLocked(
                    generation);
                if (generation.FastArenaScopedActive)
                {
                    generation.FastArenaRequestedBytes =
                        generation.FastArenaScopedRequestedBaseline;
                }

                generation.FastArenaScopedRequestedBaseline = 0;
                generation.FastArenaScopedActive = false;
                generation.ScopedRecordCount = 0;
                generation.ScopedBaselineActive = false;
                generation.ScopedTouchedSegments.Clear();
                generation.ScopeEpoch = nextScopeEpoch;
                ResetBumpTraversal(generation);
            }
            finally
            {
                generation.OpenOperationAdmission();
                EndArenaFastBoundary();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReturnArenaTransferLease(
        long generationNumber,
        long allocationId,
        string operation)
    {
        NativeGeneration? generation =
            Volatile.Read(ref _current);
        if (generation is null
            || generation.Number != generationNumber)
        {
            return;
        }

        if (Volatile.Read(
                ref _arenaFastBoundaryClosing) == 0
            && ReferenceEquals(
                Volatile.Read(ref _current),
                generation)
            && _lifecycle == NativeOwnerLifecycle.Active)
        {
            ReturnArenaTransferLeaseCore(
                generation,
                allocationId);
            return;
        }

        ReturnArenaTransferLeaseSlow(
            generationNumber,
            allocationId,
            operation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReturnArenaTransferLeaseSlow(
        long generationNumber,
        long allocationId,
        string operation)
    {
        _ = operation;
        lock (_gate)
        {
            if (_lifecycle != NativeOwnerLifecycle.Active)
            {
                return;
            }

            NativeGeneration? generation = _current;
            if (generation is null
                || generation.Number != generationNumber)
            {
                return;
            }

            ReturnArenaTransferLeaseCore(
                generation,
                allocationId);
        }
    }

    private static void ReturnArenaTransferLeaseCore(
        NativeGeneration generation,
        long allocationId)
    {
        if (!TryGetArenaTransferSlot(
                generation,
                allocationId,
                out NativeAllocation? allocation)
            || allocation is null
            || !allocation.TryTransitionLifecycle(
                NativeAllocationLifecycle.Active,
                NativeAllocationLifecycle.Returning))
        {
            return;
        }

        allocation.InitializedLength = 0;
        allocation.Lifecycle =
            NativeAllocationLifecycle.Returned;
        ReturnArenaTransferSlot(allocation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetArenaTransferSlot(
        NativeGeneration generation,
        long allocationId,
        out NativeAllocation? allocation)
    {
        allocation = null;
        if (allocationId >= 0)
        {
            return false;
        }

        uint storedIndex = (uint)allocationId;
        if (storedIndex == 0)
        {
            return false;
        }

        int index = checked((int)(storedIndex - 1));
        NativeAllocation?[] slots =
            generation.ArenaTransferSlots;
        if ((uint)index >= (uint)slots.Length)
        {
            return false;
        }

        NativeAllocation? candidate = slots[index];
        if (candidate is null
            || !candidate.IsArenaTransferSlot
            || candidate.Id != allocationId)
        {
            return false;
        }

        allocation = candidate;
        return true;
    }

    private static void RecordArenaScopedReclaimedRanges(
        NativeGeneration generation)
    {
        if (!generation.FastArenaScopedActive
            || generation.ScopedRecordCount != 0)
        {
            return;
        }

        int rangeCount = 0;
        nuint rangeBytes = 0;
        foreach (NativeBumpSegment segment in
            generation.ScopedTouchedSegments)
        {
            if (segment.PendingScopeEpoch
                    != generation.ScopeEpoch
                || segment.PendingScopeRangeEnd
                    <= segment.HighCursor)
            {
                continue;
            }

            nuint reusedBytes = segment.ReclaimedOverlap(
                segment.HighCursor,
                segment.PendingScopeRangeEnd
                    - segment.HighCursor,
                generation.ScopeEpoch);
            if (reusedBytes == 0)
            {
                continue;
            }

            rangeCount = checked(rangeCount + 1);
            rangeBytes = checked(rangeBytes + reusedBytes);
        }

        if (rangeCount != 0)
        {
            NativeMemoryTestHooks.RecordReclaimedRangeReuse(
                rangeCount,
                rangeBytes);
        }
    }

    private static void RecycleArenaCompositeAllocationsLocked(
        NativeGeneration generation)
    {
        List<NativeAllocation>? materialized =
            generation.ArenaCompositeAllocations;
        if (materialized is null)
        {
            return;
        }

        for (int index = materialized.Count - 1;
            index >= 0;
            index--)
        {
            NativeAllocation allocation = materialized[index];
            if (!allocation.IsScoped
                || allocation.ScopeEpoch
                    != generation.ScopeEpoch)
            {
                continue;
            }

            allocation.Lifecycle =
                NativeAllocationLifecycle.Returned;
            materialized.RemoveAt(index);
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
                EndArenaFastBoundary();
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
                EndArenaFastBoundary();
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
                EndArenaFastBoundary();
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
                EndArenaFastBoundary();
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
                EndArenaFastBoundary();
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
            BeginArenaFastBoundary();
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
                    EndArenaFastBoundary();
                }

                throw;
            }
        }
    }

    private void ReserveInitialStorageLocked(NativeGeneration generation, string operation, NativeOwnerLifecycle observedLifecycle)
    {
        if (_kind == NativeOwnerKind.Pool)
        {
            int reservationCount = (_preAllocateBytes == 0 ? 0 : 1)
                + (_preLease == 0 ? 0 : 1);
            if (reservationCount == 0)
            {
                return;
            }

            generation.Slabs.EnsureCapacity(
                checked(generation.Slabs.Count + reservationCount));
            generation.AvailableSlabs.EnsureCapacity(
                checked(generation.AvailableSlabs.Count + reservationCount));
            generation.Owner.PrepareAddSegmentCapacity(reservationCount);
            if (_preAllocateBytes != 0)
            {
                NativeSlab rawSlab = AddPoolRawSlabLocked(
                    generation,
                    _preAllocateBytes,
                    operation,
                    observedLifecycle);
                generation.AddSlabOrdered(rawSlab);
                generation.AddAvailableSlabOrdered(rawSlab);
            }

            if (_preLease != 0)
            {
                int capacity = checked((int)_preLease);
                NativeSlab typedSlab = AddPoolSlabLocked(
                    generation,
                    capacity,
                    operation,
                    observedLifecycle);
                generation.AddSlabOrdered(typedSlab);
                generation.AddAvailableSlabOrdered(typedSlab);
            }
        }
        else if (_kind is NativeOwnerKind.Region or NativeOwnerKind.Arena
            && _preAllocateBytes > 0)
        {
            generation.BumpSegments.EnsureCapacity(checked(generation.BumpSegments.Count + 1));
            generation.Owner.PrepareAddSegmentCapacity(1);
            NativeSegment segment = NativeSegment.Allocate(
                _preAllocateBytes,
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
        return AddPoolSlabLocked(
            generation,
            capacity,
            byteLength,
            operation,
            observedLifecycle);
    }

    private NativeSlab AddPoolRawSlabLocked(
        NativeGeneration generation,
        nuint byteLength,
        string operation,
        NativeOwnerLifecycle observedLifecycle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(byteLength);
        nuint usableElements = byteLength / (nuint)_storageElementSize;
        int capacity = usableElements > int.MaxValue
            ? int.MaxValue
            : (int)usableElements;
        return AddPoolSlabLocked(
            generation,
            capacity,
            byteLength,
            operation,
            observedLifecycle);
    }

    private NativeSlab AddPoolSlabLocked(
        NativeGeneration generation,
        int capacity,
        nuint byteLength,
        string operation,
        NativeOwnerLifecycle observedLifecycle)
    {
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NativeAllocation?
        TakeHotPoolAllocationLocked(
            NativeGeneration generation,
            int length)
    {
        NativeAllocation? allocation =
            generation.HotPoolAllocation;
        generation.HotPoolAllocation = null;
        if (allocation?.Slab is not NativeSlab slab
            || slab.Capacity < length
            || allocation.Lifecycle
                != NativeAllocationLifecycle.Returned
            || generation.ReusableAllocations.Count != 1
            || generation.AvailableSlabs.Count != 1
            || !ReferenceEquals(
                generation.ReusableAllocations[^1],
                allocation)
            || !ReferenceEquals(
                generation.AvailableSlabs[^1],
                slab))
        {
            return null;
        }

        generation.ReusableAllocations.RemoveAt(
            generation.ReusableAllocations.Count - 1);
        generation.AvailableSlabs.RemoveAt(
            generation.AvailableSlabs.Count - 1);
        return allocation;
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
                if (segment.IsArenaTransferSegment
                    || segment.HighCursor < segment.LowCursor
                    || byteLength > segment.HighCursor - segment.LowCursor)
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
                if (segment.IsArenaFastSegment
                    || segment.IsArenaTransferSegment)
                {
                    continue;
                }

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
            BeginArenaFastBoundary();
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
                    reserveQuarantineSlot:
                        tolerant
                        && (generation.ActiveOperations != 0
                            || HasArenaFastHazard(generation)));
                if (tolerant
                    && (generation.ActiveOperations != 0
                        || HasArenaFastHazard(generation)))
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
                    generation.ActiveOperations != 0
                    || HasArenaFastHazard(generation);
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
                    EndArenaFastBoundary();
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
            BeginArenaFastBoundary();
            current.CloseOperationAdmission();
            bool completed = false;
            try
            {
                bool tolerant =
                    policy == NativeMemoryReturn.ToGarbageCollector;
                if (current.LeaseReturnsInProgress != 0
                    || current.InitializationsInProgress != 0
                    || HasArenaFastInitializer()
                    || (!tolerant
                        && (current.ActiveOperations != 0
                            || HasArenaFastHazard(current))))
                {
                    throw CreateInUseException(operation, current.Number, 0, current.ActiveOperations, "No lease generation was invalidated or reset.");
                }

                EnsureNoInjectedClearFailureLocked(operation);
                bool retainAllSegments =
                    tolerant
                    && (current.ActiveOperations != 0
                        || HasArenaFastHazard(current));
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
                            bump.IsArenaFastSegment = false;
                            bump.IsArenaTransferSegment = false;
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
                        if (current.ActiveOperations == 0
                            && !HasArenaFastHazard(current))
                        {
                            DrainRetiredGenerationLocked(current);
                        }
                    }
                    else
                    {
                        current.Owner.ReleaseToNative();
                    }

                    EndArenaFastBoundary();
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
                    EndArenaFastBoundary();
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
                bump.IsArenaFastSegment = false;
                bump.IsArenaTransferSegment = false;
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

        foreach (NativeAllocation? allocation in
            generation.ArenaTransferSlots)
        {
            if (allocation is not null)
            {
                allocation.Lifecycle =
                    NativeAllocationLifecycle.Returned;
            }
        }

        foreach (NativeArenaTransferBatchState batch in
            generation.ArenaTransferBatchSnapshot)
        {
            batch.Invalidate();
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

    private bool TryCloseForMaintenanceLocked(
        NativeGeneration generation)
    {
        BeginArenaFastBoundary();
        int activeOperations =
            generation.CloseOperationAdmission();
        if (activeOperations == 0
            && !HasArenaFastInitializer()
            && !HasArenaFastHazard(generation)
            && generation.LeaseReturnsInProgress == 0
            && generation.InitializationsInProgress == 0)
        {
            return true;
        }

        generation.OpenOperationAdmission();
        EndArenaFastBoundary();
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

            if (ReferenceEquals(
                    segment,
                    _arenaFastSegment))
            {
                _arenaFastSegment = null;
                _arenaFastCursor = 0;
            }

            if (ReferenceEquals(
                    segment,
                    _arenaFastScopedSegment))
            {
                _arenaFastScopedSegment = null;
            }

            if (ReferenceEquals(
                    segment,
                    _arenaTransferCentralSegment))
            {
                _arenaTransferCentralSegment = null;
            }

            segment.IsArenaFastSegment = false;
            segment.IsArenaTransferSegment = false;

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

        NativeGeneration? arenaHazard = Volatile.Read(
            ref _arenaFastHazardGeneration);
        bool activeArenaHazard = arenaHazard is
            { MemoryDetached: false };
        NativeGeneration? arenaInitializer =
            HasArenaFastInitializer()
                ? _arenaFastGeneration ?? _current
                : null;
        bool activeArenaInitializer = arenaInitializer is
            { MemoryDetached: false };
        if (activeArenaHazard)
        {
            activeOperations = checked(
                activeOperations + 1);
        }

        if (activeArenaInitializer)
        {
            leaseReturnsInProgress = checked(
                leaseReturnsInProgress + 1);
        }

        NativeGeneration? fastBusyGeneration =
            arenaHazard ?? arenaInitializer;
        if ((activeArenaHazard
                || activeArenaInitializer)
            && fastBusyGeneration is not null
            && fastBusyGeneration.ActiveOperations == 0
            && fastBusyGeneration.LeaseReturnsInProgress == 0
            && fastBusyGeneration.InitializationsInProgress == 0)
        {
            busyGenerationCount = checked(
                busyGenerationCount + 1);
            if (firstBusyGeneration == 0
                || fastBusyGeneration.Number
                    < firstBusyGeneration)
            {
                firstBusyGeneration =
                    fastBusyGeneration.Number;
            }
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

        int generationOperations = checked(
            generation.ActiveOperations
            + CountArenaTransferOperations(generation));
        activeOperations = checked(
            activeOperations + generationOperations);
        leaseReturnsInProgress = checked(
            leaseReturnsInProgress
            + generation.LeaseReturnsInProgress
            + generation.InitializationsInProgress);
        if (generationOperations == 0
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
