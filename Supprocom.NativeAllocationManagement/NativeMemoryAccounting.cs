using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

// Byte gauges (outstanding, peak, detached, retired) are always on. Per-event
// counters (allocations, frees, clears, copies, reuse, traversal) cost cycles on
// hot paths, so they run only when the DetailedCounters switch is set at startup;
// the static readonly flag lets the JIT remove them otherwise. Native fault
// injection is a separate concern.
internal static class NativeMemoryAccounting
{
    internal const string DetailedCountersSwitch = "Supprocom.NativeAllocationManagement.DetailedCounters";

    internal static bool DetailedCounters
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => DetailedSwitch.Enabled;
    }

    // A separate holder keeps this class free of a type initializer.
    private static class DetailedSwitch
    {
        internal static readonly bool Enabled = Read();

        private static bool Read()
        {
            string? environment = Environment.GetEnvironmentVariable("NAM_DETAILED_COUNTERS");
            if (environment is not null)
            {
                return environment is "1" || string.Equals(environment, "true", StringComparison.OrdinalIgnoreCase);
            }

            return AppContext.TryGetSwitch(DetailedCountersSwitch, out bool enabled) && enabled;
        }
    }

    private sealed class NativeHotMetrics
    {
        internal long Epoch = long.MinValue;
        internal WeakReference<Thread>? OwnerThread;
        internal bool Shared;
        internal bool HistoryOverflowed;
        internal long BumpTraversalVisitCount;
        internal long ReusedNativeSegmentCount;
        internal long ReclaimedRangeReuseCount;
        internal long ReclaimedRangeReuseBytes;
        internal long StorageClearCount;
        internal long StorageClearBytes;
        internal long WrittenClearBytes;
        internal long CopiedBytes;

        internal void Reset(long epoch)
        {
            HistoryOverflowed = false;
            BumpTraversalVisitCount = 0;
            ReusedNativeSegmentCount = 0;
            ReclaimedRangeReuseCount = 0;
            ReclaimedRangeReuseBytes = 0;
            StorageClearCount = 0;
            StorageClearBytes = 0;
            WrittenClearBytes = 0;
            CopiedBytes = 0;
            Volatile.Write(ref Epoch, epoch);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeHotMetricsSnapshot
    {
        internal long BumpTraversalVisitCount;
        internal long ReusedNativeSegmentCount;
        internal long ReclaimedRangeReuseCount;
        internal long ReclaimedRangeReuseBytes;
        internal long StorageClearCount;
        internal long StorageClearBytes;
        internal long WrittenClearBytes;
        internal long CopiedBytes;
        internal bool HistoryOverflowed;
    }

    internal const int ThreadMetricSlotCapacity = 64;
    private static bool _hotAccountingInitialized;

    private static class HotAccounting
    {
        internal static readonly Lock Gate = new();
        internal static readonly NativeHotMetrics SharedMetrics = new() { Shared = true, Epoch = 0 };

        // Strict initialization keeps physical-only counters allocation-free.
        // Publication precedes the first possible hot producer; an observer
        // then accesses Gate and waits for this constructor to finish.
        static HotAccounting() => Volatile.Write(ref _hotAccountingInitialized, true);
    }

    // Physical allocation/free and snapshots need no per-thread shard bank.
    // Acquire it under HotAccounting.Gate only when a hot-counter owner is claimed.
    private static NativeHotMetrics?[]? HotMetrics;
    private static int _claimedSlotCount;
    private static NativeHotMetricsSnapshot _completedHotMetrics;
    private static bool _historyOverflowed;

    [ThreadStatic]
    private static NativeHotMetrics? _threadHotMetrics;

    [ThreadStatic]
    private static Thread? _threadOwner;

    private static long _allocationCount;
    private static long _reallocationCount;
    private static long _zeroedAllocationCount;
    private static long _freeCount;
    private static long _detachedGenerationCount;
    private static long _outstandingNativeBytes;
    private static long _peakOutstandingNativeBytes;
    private static long _detachedNativeBytes;
    private static long _retiredNativeBytes;
    private static long _metricsEpoch;

    internal static long CurrentMetricsEpoch => Volatile.Read(ref _metricsEpoch);

    // Outstanding bytes change on every native allocation, free and parked-block
    // reuse, so one shared counter would bounce between cores. Each thread instead
    // keeps its change since its last flush in its own stripe; readers add every
    // stripe of the current epoch to the flushed total, so a quiescent read is
    // exact. A stripe flushes when its change passes FlushBytes either way, and
    // publishes its high-water mark when its change falls back to zero, which keeps
    // single-threaded peaks exact. Concurrent peaks can miss up to FlushBytes of
    // another thread's unflushed allocations.
    private const long FlushBytes = 256 * 1024;
    private const int GaugeStripeLimit = 1024;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 192)]
    private struct GaugeFields
    {
        // The middle of 192 bytes keeps these off the cache lines of neighboring objects.
        [System.Runtime.InteropServices.FieldOffset(64)] internal long Epoch;
        [System.Runtime.InteropServices.FieldOffset(72)] internal long Delta;
        [System.Runtime.InteropServices.FieldOffset(80)] internal long MaxDelta;
    }

    private sealed class GaugeStripe
    {
        internal GaugeFields Fields;
        internal WeakReference<Thread>? OwnerThread;
    }

    private static class GaugeRegistry
    {
        internal static readonly Lock Gate = new();
        internal static GaugeStripe[] Stripes = [];
        internal static int Count;
    }

    [ThreadStatic]
    private static GaugeStripe? t_gauge;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddOutstanding(long bytes, long metricsEpoch)
    {
        GaugeStripe? stripe = t_gauge;
        if (stripe is null || stripe.Fields.Epoch != metricsEpoch)
        {
            stripe = ClaimGauge(metricsEpoch);
            if (stripe is null)
            {
                RecordPeak(Interlocked.Add(ref _outstandingNativeBytes, bytes));
                return;
            }
        }

        ref GaugeFields fields = ref stripe.Fields;
        long delta = fields.Delta + bytes;
        Volatile.Write(ref fields.Delta, delta);
        if (delta > fields.MaxDelta)
        {
            Volatile.Write(ref fields.MaxDelta, delta);
            if (delta > FlushBytes)
            {
                FlushGauge(ref fields);
            }
        }
        else if (delta <= 0 && fields.MaxDelta > 0 || delta < -FlushBytes)
        {
            FlushGauge(ref fields);
        }
    }

    // Moves the stripe's change into the shared total and records its high-water
    // mark against the total before this flush.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FlushGauge(ref GaugeFields fields)
    {
        long delta = fields.Delta;
        long max = fields.MaxDelta;
        long before = delta == 0
            ? Volatile.Read(ref _outstandingNativeBytes)
            : Interlocked.Add(ref _outstandingNativeBytes, delta) - delta;
        Volatile.Write(ref fields.Delta, 0);
        Volatile.Write(ref fields.MaxDelta, 0);
        RecordPeak(before + max);
    }

    // Null means the thread could not get a stripe and uses the shared total.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static GaugeStripe? ClaimGauge(long metricsEpoch)
    {
        GaugeStripe? stripe = t_gauge;
        if (stripe is not null)
        {
            // The first use in a new measurement epoch drops the old epoch's change.
            stripe.Fields.Delta = 0;
            stripe.Fields.MaxDelta = 0;
            Volatile.Write(ref stripe.Fields.Epoch, metricsEpoch);
            return stripe;
        }

        try
        {
            lock (GaugeRegistry.Gate)
            {
                stripe = TakeDeadGaugeLocked() ?? AddGaugeLocked();
                if (stripe is null)
                {
                    return null;
                }

                _threadOwner ??= Thread.CurrentThread;
                stripe.OwnerThread = new WeakReference<Thread>(_threadOwner);
                stripe.Fields.Delta = 0;
                stripe.Fields.MaxDelta = 0;
                Volatile.Write(ref stripe.Fields.Epoch, metricsEpoch);
            }
        }
        catch (OutOfMemoryException)
        {
            return null;
        }

        t_gauge = stripe;
        return stripe;
    }

    // A stripe whose thread exited folds its change into the shared total first.
    private static GaugeStripe? TakeDeadGaugeLocked()
    {
        foreach (ref readonly GaugeStripe stripe in GaugeRegistry.Stripes.AsSpan(0, GaugeRegistry.Count))
        {
            if (stripe.OwnerThread is not null
                && stripe.OwnerThread.TryGetTarget(out Thread? owner)
                && owner.IsAlive)
            {
                continue;
            }

            if (Volatile.Read(ref stripe.Fields.Epoch) == CurrentMetricsEpoch)
            {
                FlushGauge(ref stripe.Fields);
            }

            Volatile.Write(ref stripe.Fields.Epoch, long.MinValue);
            return stripe;
        }

        return null;
    }

    private static GaugeStripe? AddGaugeLocked()
    {
        int count = GaugeRegistry.Count;
        if (count == GaugeStripeLimit)
        {
            return null;
        }

        GaugeStripe stripe = new() { Fields = { Epoch = long.MinValue } };
        GaugeStripe[] stripes = GaugeRegistry.Stripes;
        if (count == stripes.Length)
        {
            GaugeStripe[] grown = new GaugeStripe[Math.Max(8, count * 2)];
            stripes.AsSpan().CopyTo(grown);
            stripes = grown;
        }

        stripes[count] = stripe;
        // Readers take the gate, so publishing the slot and then the count is enough.
        GaugeRegistry.Stripes = stripes;
        GaugeRegistry.Count = count + 1;
        return stripe;
    }

    private static (long Outstanding, long Peak) ReadOutstanding()
    {
        lock (GaugeRegistry.Gate)
        {
            long epoch = CurrentMetricsEpoch;
            long total = Volatile.Read(ref _outstandingNativeBytes);
            long deltas = 0;
            long maxima = 0;
            foreach (ref readonly GaugeStripe stripe in GaugeRegistry.Stripes.AsSpan(0, GaugeRegistry.Count))
            {
                if (Volatile.Read(ref stripe.Fields.Epoch) == epoch)
                {
                    deltas += Volatile.Read(ref stripe.Fields.Delta);
                    maxima += Volatile.Read(ref stripe.Fields.MaxDelta);
                }
            }

            return (total + deltas, Math.Max(Volatile.Read(ref _peakOutstandingNativeBytes), total + maxima));
        }
    }

    internal static void PrepareThread()
    {
        if (DetailedCounters)
        {
            _ = CurrentHotMetrics();
        }
    }

    // Measurement reset is internal and requires quiescent producers.
    // Fault-injection state lives elsewhere and cannot disable these counters.
    internal static void ResetForTests()
    {
        lock (HotAccounting.Gate)
        {
            long next = checked(CurrentMetricsEpoch + 1);
            Volatile.Write(ref _metricsEpoch, next);
            Interlocked.Exchange(ref _allocationCount, 0);
            Interlocked.Exchange(ref _reallocationCount, 0);
            Interlocked.Exchange(ref _zeroedAllocationCount, 0);
            Interlocked.Exchange(ref _freeCount, 0);
            Interlocked.Exchange(ref _detachedGenerationCount, 0);
            Interlocked.Exchange(ref _outstandingNativeBytes, 0);
            Interlocked.Exchange(ref _peakOutstandingNativeBytes, 0);
            Interlocked.Exchange(ref _detachedNativeBytes, 0);
            Interlocked.Exchange(ref _retiredNativeBytes, 0);
            Volatile.Write(ref _historyOverflowed, false);
            _completedHotMetrics = default;
            HotAccounting.SharedMetrics.Reset(next);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NativeHotMetrics CurrentHotMetrics()
    {
        long epoch = CurrentMetricsEpoch;
        NativeHotMetrics? metrics = _threadHotMetrics;
        if (metrics is null)
        {
            metrics = ClaimHotMetrics(epoch);
            _threadHotMetrics = metrics;
        }
        if (Volatile.Read(ref metrics.Epoch) != epoch)
        {
            // Shared state resets once at the quiescent reset boundary.
            // A construction-thread slot can lazily enter the new epoch.
            if (!metrics.Shared)
            {
                metrics.Reset(epoch);
            }
        }
        return metrics;
    }

    private static NativeHotMetrics ClaimHotMetrics(long epoch)
    {
        try
        {
            return ClaimAvailableHotMetrics(epoch);
        }
        catch (OutOfMemoryException)
        {
            // An observational record must not fail after producer work. The
            // preexisting shared bank needs no slot, wrapper or weak allocation.
            return HotAccounting.SharedMetrics;
        }
    }

    private static NativeHotMetrics ClaimAvailableHotMetrics(long epoch)
    {
        NativeMemoryTestHooks.AccountingClaimBoundary(1);
        lock (HotAccounting.Gate)
        {
            NativeHotMetrics?[] bank = HotMetrics ??= new NativeHotMetrics[ThreadMetricSlotCapacity];
            foreach (ref NativeHotMetrics? slot in bank.AsSpan())
            {
                NativeHotMetrics? metrics = slot;
                if (metrics is null)
                {
                    metrics = new NativeHotMetrics();
                    slot = metrics;
                    _claimedSlotCount++;
                }
                if (metrics.OwnerThread is not null
                    && metrics.OwnerThread.TryGetTarget(out Thread? owner)
                    && owner.IsAlive)
                {
                    continue;
                }
                NativeMemoryTestHooks.AccountingClaimBoundary(2);
                if (Volatile.Read(ref metrics.Epoch) == epoch)
                {
                    AccumulateHotMetrics(ref _completedHotMetrics, metrics);
                }
                metrics.Reset(epoch);
                NativeMemoryTestHooks.AccountingClaimBoundary(3);
                _threadOwner ??= Thread.CurrentThread;
                metrics.OwnerThread = new WeakReference<Thread>(_threadOwner);
                return metrics;
            }
            // Fixed metadata does not cap application threads. Only excess
            // threads use shared atomic history; ordinary slots remain local.
            return HotAccounting.SharedMetrics;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddHotHistory(NativeHotMetrics metrics, ref long counter, long delta)
    {
        if (metrics.Shared)
        {
            AddAtomicHistory(ref counter, delta, ref metrics.HistoryOverflowed);
        }
        else
        {
            NativeOwnerHistory.Add(ref counter, delta, ref metrics.HistoryOverflowed);
        }
    }

    private static void AddAtomicHistory(ref long counter, long delta, ref bool overflowed)
    {
        while (true)
        {
            long current = Volatile.Read(ref counter);
            if (delta > long.MaxValue - current)
            {
                if (Interlocked.CompareExchange(ref counter, long.MaxValue, current) != current)
                {
                    continue;
                }
                Volatile.Write(ref overflowed, true);
                return;
            }
            if (Interlocked.CompareExchange(ref counter, current + delta, current) == current)
            {
                return;
            }
        }
    }

    private static void AccumulateHotMetrics(ref NativeHotMetricsSnapshot total, NativeHotMetrics metrics)
    {
        NativeOwnerHistory.Add(ref total.BumpTraversalVisitCount,
            Volatile.Read(ref metrics.BumpTraversalVisitCount), ref total.HistoryOverflowed);
        NativeOwnerHistory.Add(ref total.ReusedNativeSegmentCount,
            Volatile.Read(ref metrics.ReusedNativeSegmentCount), ref total.HistoryOverflowed);
        NativeOwnerHistory.Add(ref total.ReclaimedRangeReuseCount,
            Volatile.Read(ref metrics.ReclaimedRangeReuseCount), ref total.HistoryOverflowed);
        NativeOwnerHistory.Add(ref total.ReclaimedRangeReuseBytes,
            Volatile.Read(ref metrics.ReclaimedRangeReuseBytes), ref total.HistoryOverflowed);
        NativeOwnerHistory.Add(ref total.StorageClearCount,
            Volatile.Read(ref metrics.StorageClearCount), ref total.HistoryOverflowed);
        NativeOwnerHistory.Add(ref total.StorageClearBytes,
            Volatile.Read(ref metrics.StorageClearBytes), ref total.HistoryOverflowed);
        NativeOwnerHistory.Add(ref total.WrittenClearBytes,
            Volatile.Read(ref metrics.WrittenClearBytes), ref total.HistoryOverflowed);
        NativeOwnerHistory.Add(ref total.CopiedBytes,
            Volatile.Read(ref metrics.CopiedBytes), ref total.HistoryOverflowed);
        total.HistoryOverflowed |= Volatile.Read(ref metrics.HistoryOverflowed);
    }

    private static NativeHotMetricsSnapshot SnapshotHotMetrics()
    {
        if (!Volatile.Read(ref _hotAccountingInitialized))
        {
            // No hot producer can run before strict initialization publishes.
            return default;
        }

        lock (HotAccounting.Gate)
        {
            long epoch = CurrentMetricsEpoch;
            NativeHotMetricsSnapshot total = _completedHotMetrics;
            foreach (ref readonly NativeHotMetrics? metrics in HotMetrics.AsSpan(0, _claimedSlotCount))
            {
                if (metrics is not null && Volatile.Read(ref metrics.Epoch) == epoch)
                {
                    AccumulateHotMetrics(ref total, metrics);
                }
            }
            AccumulateHotMetrics(ref total, HotAccounting.SharedMetrics);
            return total;
        }
    }
    internal static long RecordAllocation(nuint byteLength, bool zeroed)
    {
        long metricsEpoch = CurrentMetricsEpoch;
        if (DetailedCounters)
        {
            AddAtomicHistory(ref _allocationCount, 1, ref _historyOverflowed);
            if (zeroed)
            {
                AddAtomicHistory(ref _zeroedAllocationCount, 1, ref _historyOverflowed);
            }
        }

        AddOutstanding(checked((long)byteLength), metricsEpoch);
        return metricsEpoch;
    }

    internal static long RecordReallocation(
        nuint previousByteLength,
        nuint byteLength,
        long previousMetricsEpoch)
    {
        if (DetailedCounters)
        {
            AddAtomicHistory(ref _reallocationCount, 1, ref _historyOverflowed);
        }

        if (previousByteLength == 0 || previousMetricsEpoch != CurrentMetricsEpoch)
        {
            // A pre-measurement block enters this epoch on successful resizing.
            return RecordAllocation(byteLength, zeroed: false);
        }

        AddOutstanding(checked((long)byteLength - (long)previousByteLength), previousMetricsEpoch);
        return previousMetricsEpoch;
    }

    private static void RecordPeak(long current)
    {
        while (true)
        {
            long peak = Volatile.Read(ref _peakOutstandingNativeBytes);
            if (current <= peak || Interlocked.CompareExchange(ref _peakOutstandingNativeBytes, current, peak) == peak)
            {
                break;
            }
        }
    }

    internal static void RecordFree(nuint byteLength, bool detached, long metricsEpoch)
    {
        if (metricsEpoch != CurrentMetricsEpoch)
        {
            return;
        }

        if (DetailedCounters)
        {
            AddAtomicHistory(ref _freeCount, 1, ref _historyOverflowed);
        }

        long bytes = checked((long)byteLength);
        AddOutstanding(-bytes, metricsEpoch);
        if (detached)
        {
            Interlocked.Add(ref _detachedNativeBytes, -bytes);
        }
    }

    internal static void RecordDetachedGeneration(long metricsEpoch)
    {
        if (DetailedCounters && metricsEpoch == CurrentMetricsEpoch)
        {
            AddAtomicHistory(ref _detachedGenerationCount, 1, ref _historyOverflowed);
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
        (long outstanding, _) = ReadOutstanding();
        return new NativeMemoryTestMetrics(
            Volatile.Read(ref _allocationCount),
            Volatile.Read(ref _zeroedAllocationCount),
            Volatile.Read(ref _freeCount),
            Volatile.Read(ref _detachedGenerationCount),
            outstanding,
            Volatile.Read(ref _detachedNativeBytes),
            Volatile.Read(ref _retiredNativeBytes),
            hot.BumpTraversalVisitCount,
            hot.ReusedNativeSegmentCount,
            hot.ReclaimedRangeReuseCount,
            hot.ReclaimedRangeReuseBytes,
            hot.StorageClearCount,
            hot.StorageClearBytes,
            hot.WrittenClearBytes,
            Volatile.Read(ref _reallocationCount))
        {
            HistoryOverflowed = Volatile.Read(ref _historyOverflowed) || hot.HistoryOverflowed,
            CopiedBytes = hot.CopiedBytes,
            MetricsEpoch = CurrentMetricsEpoch
        };
    }

    internal static NativeMemoryStatistics SnapshotPublic()
    {
        NativeHotMetricsSnapshot hot = SnapshotHotMetrics();
        (long outstanding, long peak) = ReadOutstanding();
        return new NativeMemoryStatistics(
            outstanding,
            peak,
            Volatile.Read(ref _detachedNativeBytes),
            Volatile.Read(ref _retiredNativeBytes),
            hot.ReusedNativeSegmentCount,
            hot.ReclaimedRangeReuseCount,
            hot.ReclaimedRangeReuseBytes)
        {
            AllocationCount = Volatile.Read(ref _allocationCount),
            ReallocationCount = Volatile.Read(ref _reallocationCount),
            FreeCount = Volatile.Read(ref _freeCount),
            HistoryOverflowed = Volatile.Read(ref _historyOverflowed) || hot.HistoryOverflowed,
            MetricsEpoch = CurrentMetricsEpoch,
            ZeroedAllocationCount = Volatile.Read(ref _zeroedAllocationCount),
            DetachedGenerationCount = Volatile.Read(ref _detachedGenerationCount),
            BumpTraversalVisitCount = hot.BumpTraversalVisitCount,
            StorageClearCount = hot.StorageClearCount,
            StorageClearBytes = hot.StorageClearBytes,
            WrittenClearBytes = hot.WrittenClearBytes,
            CopiedBytes = hot.CopiedBytes
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RecordBumpTraversalVisit()
    {
        if (!DetailedCounters)
        {
            return;
        }

        NativeHotMetrics metrics = CurrentHotMetrics();
        AddHotHistory(metrics, ref metrics.BumpTraversalVisitCount, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RecordCopiedRange<T>(scoped ReadOnlySpan<T> source, scoped Span<T> destination)
    {
        if (!DetailedCounters
            || source.IsEmpty || Unsafe.AreSame(
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(source),
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(destination)))
        {
            return;
        }
        NativeHotMetrics metrics = CurrentHotMetrics();
        // Both factors are Int32-sized; their product fits Int64. This
        // observation cannot throw overflow after the actual copy succeeds.
        long bytes = (long)source.Length * Unsafe.SizeOf<T>();
        AddHotHistory(metrics, ref metrics.CopiedBytes, bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CopyRange<T>(scoped ReadOnlySpan<T> source, scoped Span<T> destination)
    {
        if (!DetailedCounters
            || source.IsEmpty || Unsafe.AreSame(
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(source),
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(destination)))
        {
            source.CopyTo(destination);
            return;
        }

        // Callers validate ranges first. Acquire first-use diagnostic storage
        // before changing data, then credit only a completed copy. One lookup
        // also covers the shared fallback when a local record cannot be claimed.
        NativeHotMetrics metrics = CurrentHotMetrics();
        source.CopyTo(destination);
        long bytes = (long)source.Length * Unsafe.SizeOf<T>();
        AddHotHistory(metrics, ref metrics.CopiedBytes, bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ClearStorage<T>(scoped Span<T> values)
    {
        if (!DetailedCounters)
        {
            values.Clear();
            return;
        }

        if (values.IsEmpty)
        {
            return;
        }

        NativeHotMetrics metrics = CurrentHotMetrics();
        values.Clear();
        // Int32-sized element count and stride fit Int64 without a fallible
        // conversion after the actual write.
        long bytes = (long)values.Length * Unsafe.SizeOf<T>();
        AddHotHistory(metrics, ref metrics.StorageClearCount, 1);
        AddHotHistory(metrics, ref metrics.StorageClearBytes, bytes);
        AddHotHistory(metrics, ref metrics.WrittenClearBytes, bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RecordReusedNativeSegment()
    {
        if (!DetailedCounters)
        {
            return;
        }

        NativeHotMetrics metrics = CurrentHotMetrics();
        AddHotHistory(metrics, ref metrics.ReusedNativeSegmentCount, 1);
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
        if (!DetailedCounters)
        {
            return;
        }

        NativeHotMetrics metrics = CurrentHotMetrics();
        AddHotHistory(metrics, ref metrics.ReclaimedRangeReuseCount, rangeCount);
        AddHotHistory(metrics, ref metrics.ReclaimedRangeReuseBytes, checked((long)byteLength));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RecordStorageClear(
        nuint byteLength,
        nuint writtenBytes)
    {
        if (!DetailedCounters)
        {
            return;
        }

        NativeHotMetrics metrics = CurrentHotMetrics();
        AddHotHistory(metrics, ref metrics.StorageClearCount, 1);
        AddHotHistory(metrics, ref metrics.StorageClearBytes, checked((long)byteLength));
        AddHotHistory(metrics, ref metrics.WrittenClearBytes, checked((long)writtenBytes));
    }

}
