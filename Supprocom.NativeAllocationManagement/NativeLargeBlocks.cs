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
// Like a parked transfer block, a kept block counts as freed in the
// diagnostics and a taken one as a new allocation. Nothing else about a kept
// block is observable, so tests share the cache; it stands down while a test
// injects a fault.
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

    // A kept block of at least byteLength bytes and at most twice that, or null.
    internal static void* TryTake(nuint byteLength, out nuint blockBytes)
    {
        blockBytes = 0;
        if (Volatile.Read(ref s_count) == 0
            || byteLength < MinimumBytes
            || NativeMemoryTestHooks.FaultInjectionPending)
        {
            return null;
        }

        lock (s_gate)
        {
            int best = -1;
            for (int index = 0; index < s_count; index++)
            {
                nuint bytes = s_entries[index].Bytes;
                if (bytes >= byteLength
                    && bytes - byteLength <= byteLength
                    && (best < 0 || bytes < s_entries[best].Bytes))
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
            return taken.Pointer;
        }
    }

    // Keeps a returned block allocated with NativeSegment.Alignment; false
    // when the caller must free it.
    internal static bool TryKeep(void* block, nuint blockBytes)
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

        Span<nint> evicted = stackalloc nint[Capacity];
        int evictedCount = 0;
        lock (s_gate)
        {
            while (s_count == Capacity || s_bytes + blockBytes > LimitBytes)
            {
                evicted[evictedCount++] = (nint)s_entries[0].Pointer;
                RemoveAt(0);
            }

            s_entries[s_count] = new Entry(block, blockBytes, s_collections);
            s_bytes += blockBytes;
            Volatile.Write(ref s_count, s_count + 1);
        }

        foreach (ref nint pointer in evicted[..evictedCount])
        {
            NativeMemory.AlignedFree((void*)pointer);
        }

        return true;
    }

    private static void RemoveAt(int index)
    {
        s_bytes -= s_entries[index].Bytes;
        int last = s_count - 1;
        Array.Copy(s_entries, index + 1, s_entries, index, last - index);
        s_entries[last] = default;
        Volatile.Write(ref s_count, last);
    }

    private static void FreeWhere(Func<Entry, bool> free)
    {
        int kept = 0;
        foreach (ref Entry entry in s_entries.AsSpan(0, s_count))
        {
            if (free(entry))
            {
                NativeMemory.AlignedFree(entry.Pointer);
                s_bytes -= entry.Bytes;
            }
            else
            {
                s_entries[kept++] = entry;
            }
        }

        Array.Clear(s_entries, kept, s_count - kept);
        Volatile.Write(ref s_count, kept);
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
        lock (s_gate)
        {
            int previous = s_collections++;
            FreeWhere(entry => highLoad || entry.Stamp < previous);
        }
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly struct Entry(void* pointer, nuint bytes, int stamp)
    {
        internal readonly void* Pointer = pointer;
        internal readonly nuint Bytes = bytes;
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
