using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

// Large blocks that unbudgeted owners return, kept for the next owner on any
// thread that needs one. The C runtime maps a large block afresh for every
// allocation on Windows (above about 1 MiB on 64-bit heaps) and on glibc
// with a fixed mmap threshold, and every page then faults on first touch: a
// region, arena or transfer made per use with 2 MiB of storage took
// 810-870 us instead of ArrayPool's 1.6 us, slower than new byte[]. The
// per-thread reuse slots keep at most 1 MiB, so this cache takes what they
// cannot.
//
// Bounded in count and bytes; when full, the oldest block is freed. A block
// not taken again before the next full collection after it was kept is freed
// then, and all of them under high memory load, as ArrayPool trims its arrays.
// A kept block stays outstanding in the diagnostics, as all storage NAM keeps
// for reuse does: keeping and taking it record nothing, and the free is
// recorded when the cache frees it. Only a block of the current measurement
// epoch is taken, so every owner's storage is counted in the epoch it is used
// in; an older one waits to be freed. Nothing else about a kept block is
// observable, so tests share the cache; it stands down while a test injects a
// fault.
internal static unsafe class NativeLargeBlocks
{
    internal const nuint MinimumBytes = 256 * 1024;
    internal const nuint LimitBytes = 64 * 1024 * 1024;
    // Room for a block between one owner's return and its next use on many
    // threads at once; the bytes bound is what limits what is kept.
    private const int Capacity = 32;

    private static readonly Lock s_gate = new();
    // Oldest first.
    private static readonly Entry[] s_entries = new Entry[Capacity];
    private static int s_count;
    private static nuint s_bytes;
    private static int s_collections;
    private static int s_trimmerStarted;

    // Whether a kept block holds this address.
    internal static bool HoldsForTest(void* address)
    {
        lock (s_gate)
        {
            foreach (ref Entry entry in s_entries.AsSpan(0, s_count))
            {
                if (address >= entry.Pointer && address < (byte*)entry.Pointer + entry.Bytes)
                {
                    return true;
                }
            }

            return false;
        }
    }

    // The kept blocks of the current measurement epoch: still counted as
    // outstanding in it.
    internal static (int Count, long Bytes) KeptInCurrentEpochForTest
    {
        get
        {
            long epoch = NativeMemoryAccounting.CurrentMetricsEpoch;
            int count = 0;
            long bytes = 0;
            lock (s_gate)
            {
                foreach (ref Entry entry in s_entries.AsSpan(0, s_count))
                {
                    if (entry.MetricsEpoch == epoch)
                    {
                        count++;
                        bytes += (long)entry.Bytes;
                    }
                }
            }

            return (count, bytes);
        }
    }

    // A kept block of at least byteLength bytes and at most twice that, from
    // the current measurement epoch, or null. It is still counted as
    // outstanding in its epoch: the taker records no allocation.
    internal static void* TryTake(nuint byteLength, out nuint blockBytes, out long metricsEpoch)
    {
        blockBytes = 0;
        metricsEpoch = 0;
        if (Volatile.Read(ref s_count) == 0
            || byteLength < MinimumBytes
            || NativeMemoryTestHooks.FaultInjectionPending)
        {
            return null;
        }

        long currentEpoch = NativeMemoryAccounting.CurrentMetricsEpoch;
        lock (s_gate)
        {
            int best = -1;
            for (int index = 0; index < s_count; index++)
            {
                ref Entry entry = ref s_entries[index];
                if (entry.Bytes >= byteLength
                    && entry.Bytes - byteLength <= byteLength
                    && entry.MetricsEpoch == currentEpoch
                    && (best < 0 || entry.Bytes < s_entries[best].Bytes))
                {
                    best = index;
                }
            }

            if (best < 0)
            {
                return null;
            }

            Entry taken = s_entries[best];
            RemoveAt(best);
            blockBytes = taken.Bytes;
            metricsEpoch = taken.MetricsEpoch;
            return taken.Pointer;
        }
    }

    // Keeps a returned block allocated with NativeSegment.Alignment, still
    // counted as outstanding in its measurement epoch; false when the caller
    // must free it.
    internal static bool TryKeep(void* block, nuint blockBytes, long metricsEpoch)
    {
        if (blockBytes < MinimumBytes
            || blockBytes > LimitBytes
            || NativeMemoryTestHooks.FaultInjectionPending)
        {
            return false;
        }

        if (Volatile.Read(ref s_trimmerStarted) == 0)
        {
            StartTrimmer();
        }

        Span<Entry> evicted = stackalloc Entry[Capacity];
        int evictedCount = 0;
        lock (s_gate)
        {
            while (s_count == Capacity || s_bytes + blockBytes > LimitBytes)
            {
                evicted[evictedCount++] = s_entries[0];
                RemoveAt(0);
            }

            s_entries[s_count] = new Entry(block, blockBytes, metricsEpoch, s_collections);
            s_bytes += blockBytes;
            Volatile.Write(ref s_count, s_count + 1);
        }

        Free(evicted[..evictedCount]);
        return true;
    }

    // Outside the lock: recording a free can create the calling thread's
    // state, which registers it under NativeEndedThreads' lock, and that lock
    // is held around keeps.
    private static void Free(ReadOnlySpan<Entry> entries)
    {
        foreach (ref readonly Entry entry in entries)
        {
            NativeMemory.AlignedFree(entry.Pointer);
            NativeMemoryAccounting.RecordFree(entry.Bytes, detached: false, entry.MetricsEpoch);
        }
    }

    private static void RemoveAt(int index)
    {
        s_bytes -= s_entries[index].Bytes;
        int last = s_count - 1;
        Array.Copy(s_entries, index + 1, s_entries, index, last - index);
        s_entries[last] = default;
        Volatile.Write(ref s_count, last);
    }

    // Moves the entries to free into the span and returns how many; the
    // caller frees them outside the lock.
    private static int RemoveWhere(Func<Entry, bool> free, Span<Entry> removed)
    {
        int kept = 0;
        int count = 0;
        foreach (ref Entry entry in s_entries.AsSpan(0, s_count))
        {
            if (free(entry))
            {
                removed[count++] = entry;
                s_bytes -= entry.Bytes;
            }
            else
            {
                s_entries[kept++] = entry;
            }
        }

        Array.Clear(s_entries, kept, s_count - kept);
        Volatile.Write(ref s_count, kept);
        return count;
    }

    private static void StartTrimmer()
    {
        if (Interlocked.Exchange(ref s_trimmerStarted, 1) == 0)
        {
            _ = new Trimmer();
        }
    }

    private static void Trim()
    {
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        Trim(highLoad: memory.MemoryLoadBytes >= memory.HighMemoryLoadThresholdBytes * 9 / 10);
    }

    internal static void Trim(bool highLoad)
    {
        Span<Entry> removed = stackalloc Entry[Capacity];
        int count;
        lock (s_gate)
        {
            int previous = s_collections++;
            count = RemoveWhere(entry => highLoad || entry.Stamp < previous, removed);
        }

        Free(removed[..count]);
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly struct Entry(void* pointer, nuint bytes, long metricsEpoch, int stamp)
    {
        internal readonly void* Pointer = pointer;
        internal readonly nuint Bytes = bytes;
        internal readonly long MetricsEpoch = metricsEpoch;
        internal readonly int Stamp = stamp;
    }

    // Finalized at every collection of its generation and registered again,
    // so once promoted it runs at each full collection.
    private sealed class Trimmer
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "A trim failure must not terminate the process; the next collection retries.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0055", Justification = "The finalizer is the full-collection callback that trims the cache.")]
        ~Trimmer()
        {
            try
            {
                Trim();
            }
            catch
            {
            }

            GC.ReRegisterForFinalize(this);
        }
    }
}
