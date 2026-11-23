using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Supprocom.NativeAllocationManagement.Performance;

// --memory-cost: where the time of a Memory<byte> buffer goes, one cost at a time, so the
// difference between pools in --memory-pipe-copy can be attributed. Single-threaded except
// section C. Every row is warmed up, then the median of nine runs is reported.
internal static class MemoryCostBenchmarks
{
    private const int Runs = 9;
    private const int BufferSize = 64 * 1024;
    private const long FileBytes = 256L * 1024 * 1024;
    private static readonly string TemporaryDirectory = Environment.GetEnvironmentVariable("NAM_BENCH_TMP") is { Length: > 0 } dir ? dir : "/home/claude/nam-bench-tmp";

    private static long s_sink;

    internal static int RunCommand()
    {
        Print("loadavg at start: " + MemoryBenchmarks.ReadLoadAverage());
        SpanAccess();
        BufferCycle();
        CrossThreadCycle();
        ReadThroughput();
        Print("loadavg at end: " + MemoryBenchmarks.ReadLoadAverage());
        Print("sink " + s_sink.ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    private static void Print(string line) => MemoryBenchmarks.Print(line);

    // ---- A: Span access ----

    private static void SpanAccess()
    {
        Print("A. Span access: sum of memory.Span[i & 65535]");
        const int ops = 20_000_000;
        Memory<byte> array = new byte[BufferSize];
        Row("array-backed Memory", ops, bytesPerOp: 0, bodyOps => SumSpan(array, bodyOps));

        using MemoryPool<byte> adapter = new MemoryBenchmarks.ArrayPoolMemoryPool();
        using IMemoryOwner<byte> adapterOwner = adapter.Rent(BufferSize);
        Memory<byte> adapterMemory = adapterOwner.Memory;
        Row("ArrayPool adapter owner Memory", ops, bytesPerOp: 0, bodyOps => SumSpan(adapterMemory, bodyOps));

        using NativeTransfer<byte> transfer = NativeTransfer<byte>.CreateUninitialized(BufferSize);
        Memory<byte> transferMemory = transfer.AsMemory();
        Row("NativeTransfer.AsMemory()", ops, bytesPerOp: 0, bodyOps => SumSpan(transferMemory, bodyOps));

        using IMemoryOwner<byte> poolOwner = NativeMemoryPool.Default.Rent(BufferSize);
        Memory<byte> poolMemory = poolOwner.Memory;
        Row("NativeMemoryPool Memory", ops, bytesPerOp: 0, bodyOps => SumSpan(poolMemory, bodyOps));
    }

    private static long SumSpan(Memory<byte> memory, int ops)
    {
        long sum = 0;
        for (int index = 0; index < ops; index++)
        {
            sum += memory.Span[index & (BufferSize - 1)];
        }

        return sum;
    }

    // ---- B: buffer cycle ----

    private static void BufferCycle()
    {
        Print("B. Buffer cycle: Rent(65536), Memory, Span[0] write, Dispose");
        const int ops = 100_000;
        using MemoryPool<byte> adapter = new MemoryBenchmarks.ArrayPoolMemoryPool();
        using MemoryPool<byte> fresh = new MemoryBenchmarks.NamMemoryPool();
        using MemoryPool<byte> reusing = new MemoryBenchmarks.NamReusingMemoryPool();
        Row("ArrayPool adapter", ops, 0, bodyOps => PoolCycle(adapter, bodyOps));
        Row("NamMemoryPool (fresh)", ops, 0, bodyOps => PoolCycle(fresh, bodyOps));
        Row("NamReusingMemoryPool", ops, 0, bodyOps => PoolCycle(reusing, bodyOps));
        Row("NativeMemoryPool", ops, 0, bodyOps => PoolCycle(NativeMemoryPool.Default, bodyOps));
        Print("   -- NativeMemoryPool split --");
        long callsBefore = NativeMemoryDiagnostics.Snapshot().AllocationCount;
        _ = PoolCycle(NativeMemoryPool.Default, ops);
        Print(string.Create(CultureInfo.InvariantCulture, $"   allocator calls during {ops} cycles: {NativeMemoryDiagnostics.Snapshot().AllocationCount - callsBefore}"));
        int missesBefore = NativeThreadState.Current.BlockMisses;
        _ = PoolCycle(NativeMemoryPool.Default, ops);
        Print(string.Create(CultureInfo.InvariantCulture, $"   stack-block lookup misses during {ops} cycles: {NativeThreadState.Current.BlockMisses - missesBefore}"));
        Row("cycle: Rent + Dispose only", ops, 0, bodyOps => PartialCycle(bodyOps, touchMemory: false, writeSpan: false));
        Row("cycle: Rent + .Memory + Dispose", ops, 0, bodyOps => PartialCycle(bodyOps, touchMemory: true, writeSpan: false));
        Row("cycle: Rent + .Memory.Span[0] + Dispose", ops, 0, bodyOps => PartialCycle(bodyOps, touchMemory: true, writeSpan: true));
        Row("probe: NativeThreadState.Current", ops, 0, NativeMemoryPool.ProbeThreadState);
        Row("probe: Current + GetTyped + cache take/add", ops, 0, NativeMemoryPool.ProbeCache);
        Row("probe: Owner.Create + Memory.Length", ops, 0, bodyOps => NativeMemoryPool.ProbeOwner(bodyOps, dispose: false));
        Row("probe: Owner create + Dispose + take back", ops, 0, bodyOps => NativeMemoryPool.ProbeOwner(bodyOps, dispose: true));
        Row("raw ArrayPool<byte>.Shared Rent/Return", ops, 0, RawArrayPoolCycle);
        Row("raw NativeTransfer Create/Dispose", ops, 0, RawTransferCycle);
        Row("raw NativeTransfer Create/AsMemory/Dispose", ops, 0, RawTransferMemoryCycle);
        Row("NativeTransfer Move + AsMemory", ops, 0, MoveCycle);
    }

    private static long PoolCycle(MemoryPool<byte> pool, int ops)
    {
        long sum = 0;
        for (int index = 0; index < ops; index++)
        {
            using IMemoryOwner<byte> owner = pool.Rent(BufferSize);
            owner.Memory.Span[0] = 1;
            sum += owner.Memory.Length;
        }

        return sum;
    }

    private static long PartialCycle(int ops, bool touchMemory, bool writeSpan)
    {
        long sum = 0;
        for (int index = 0; index < ops; index++)
        {
            using IMemoryOwner<byte> owner = NativeMemoryPool.Default.Rent(BufferSize);
            if (touchMemory)
            {
                sum += owner.Memory.Length;
            }

            if (writeSpan)
            {
                owner.Memory.Span[0] = 1;
            }
        }

        return sum;
    }

    private static long RawArrayPoolCycle(int ops)
    {
        long sum = 0;
        for (int index = 0; index < ops; index++)
        {
            byte[] array = ArrayPool<byte>.Shared.Rent(BufferSize);
            array[0] = 1;
            sum += array.Length;
            ArrayPool<byte>.Shared.Return(array);
        }

        return sum;
    }

    private static long RawTransferCycle(int ops)
    {
        long sum = 0;
        for (int index = 0; index < ops; index++)
        {
            NativeTransfer<byte> transfer = NativeTransfer<byte>.CreateUninitialized(BufferSize);
            sum += transfer.Length;
            transfer.Dispose();
        }

        return sum;
    }

    private static long RawTransferMemoryCycle(int ops)
    {
        long sum = 0;
        for (int index = 0; index < ops; index++)
        {
            NativeTransfer<byte> transfer = NativeTransfer<byte>.CreateUninitialized(BufferSize);
            Memory<byte> memory = transfer.AsMemory();
            memory.Span[0] = 1;
            sum += memory.Length;
            transfer.Dispose();
        }

        return sum;
    }

    private static long MoveCycle(int ops)
    {
        long sum = 0;
        NativeTransfer<byte>? held = NativeTransfer<byte>.CreateUninitialized(BufferSize);
        for (int index = 0; index < ops; index++)
        {
            NativeTransfer<byte> moved = NativeTransfer<byte>.Move(ref held);
            Memory<byte> memory = moved.AsMemory();
            memory.Span[0] = 1;
            sum += memory.Length;
            held = moved;
        }

        held!.Value.Dispose();
        return sum;
    }

    // ---- C: rent on one thread, dispose on another ----

    private sealed class Ring
    {
        internal readonly IMemoryOwner<byte>?[] Slots = new IMemoryOwner<byte>?[16];
        internal int Head;
        internal int Tail;
    }

    private static void CrossThreadCycle()
    {
        Print("C. Same cycle, Rent on one thread and Dispose on another (16-slot ring, allocation is process-wide)");
        const int ops = 100_000;
        using MemoryPool<byte> adapter = new MemoryBenchmarks.ArrayPoolMemoryPool();
        using MemoryPool<byte> fresh = new MemoryBenchmarks.NamMemoryPool();
        using MemoryPool<byte> reusing = new MemoryBenchmarks.NamReusingMemoryPool();
        Row("ArrayPool adapter", ops, 0, bodyOps => HandOver(adapter, bodyOps), processWide: true);
        Row("NamMemoryPool (fresh)", ops, 0, bodyOps => HandOver(fresh, bodyOps), processWide: true);
        Row("NamReusingMemoryPool", ops, 0, bodyOps => HandOver(reusing, bodyOps), processWide: true);
        Row("NativeMemoryPool", ops, 0, bodyOps => HandOver(NativeMemoryPool.Default, bodyOps), processWide: true);
    }

    private static long HandOver(MemoryPool<byte> pool, int ops)
    {
        Ring ring = new();
        long produced = 0;
        Thread producer = new(() =>
        {
            SpinWait wait = default;
            for (int index = 0; index < ops; index++)
            {
                while (index - Volatile.Read(ref ring.Tail) >= ring.Slots.Length)
                {
                    wait.SpinOnce(-1);
                }

                IMemoryOwner<byte> owner = pool.Rent(BufferSize);
                owner.Memory.Span[0] = 1;
                ring.Slots[index & (ring.Slots.Length - 1)] = owner;
                Volatile.Write(ref ring.Head, index + 1);
                produced += BufferSize;
            }
        });
        Thread consumer = new(() =>
        {
            SpinWait wait = default;
            for (int index = 0; index < ops; index++)
            {
                while (Volatile.Read(ref ring.Head) == index)
                {
                    wait.SpinOnce(-1);
                }

                int slot = index & (ring.Slots.Length - 1);
                IMemoryOwner<byte>? owner = ring.Slots[slot];
                ring.Slots[slot] = null;
                owner!.Dispose();
                Volatile.Write(ref ring.Tail, index + 1);
            }
        });
        producer.Start();
        consumer.Start();
        producer.Join();
        consumer.Join();
        return produced;
    }

    // ---- D: read throughput ----

    private static void ReadThroughput()
    {
        Print("D. RandomAccess.Read of sequential 64 KiB chunks from a 256 MiB file in the page cache");
        Directory.CreateDirectory(TemporaryDirectory);
        string path = Path.Combine(TemporaryDirectory, "nam-cost-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            Generate(path);
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);
            const int chunks = (int)(FileBytes / BufferSize);

            byte[] array = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                ReportAddress("ArrayPool array", NativeAlignedBuffer.AddressOf(array.AsSpan()));
                _ = ReadPass(handle, array.AsSpan(0, BufferSize));
                Row("-> ArrayPool array", chunks, BufferSize, _ => ReadPass(handle, array.AsSpan(0, BufferSize)));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(array);
            }

            using NativeTransfer<byte> transfer = NativeTransfer<byte>.CreateUninitialized(BufferSize);
            Memory<byte> transferMemory = transfer.AsMemory();
            ReportAddress("NativeTransfer", NativeAlignedBuffer.AddressOf(transferMemory.Span));
            Row("-> NativeTransfer storage", chunks, BufferSize, _ => ReadPass(handle, transferMemory.Span));

            using IMemoryOwner<byte> owner = NativeMemoryPool.Default.Rent(BufferSize);
            Memory<byte> poolMemory = owner.Memory;
            ReportAddress("NativeMemoryPool", NativeAlignedBuffer.AddressOf(poolMemory.Span));
            Row("-> NativeMemoryPool buffer", chunks, BufferSize, _ => ReadPass(handle, poolMemory.Span));

            using NativeAlignedBuffer aligned64 = NativeAlignedBuffer.Allocate(BufferSize, 64);
            ReportAddress("AlignedAlloc 64", NativeAlignedBuffer.AddressOf(aligned64.Span));
            Row("-> AlignedAlloc 64", chunks, BufferSize, _ => ReadPass(handle, aligned64.Span));

            using NativeAlignedBuffer aligned4096 = NativeAlignedBuffer.Allocate(BufferSize, 4096);
            ReportAddress("AlignedAlloc 4096", NativeAlignedBuffer.AddressOf(aligned4096.Span));
            Row("-> AlignedAlloc 4096", chunks, BufferSize, _ => ReadPass(handle, aligned4096.Span));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void ReportAddress(string label, nint address) =>
        Print(string.Create(CultureInfo.InvariantCulture, $"   {label}: address mod 64 = {address % 64}, mod 4096 = {address % 4096}"));

    private static void Generate(string path)
    {
        byte[] chunk = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(chunk);
        using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1))
        {
            for (long written = 0; written < FileBytes; written += chunk.Length)
            {
                stream.Write(chunk);
            }
        }

        // One full read keeps the file in the page cache.
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        byte[] buffer = new byte[BufferSize];
        _ = ReadPass(handle, buffer);
    }

    private static long ReadPass(SafeFileHandle handle, Span<byte> buffer)
    {
        long total = 0;
        for (long offset = 0; offset < FileBytes; offset += BufferSize)
        {
            total += RandomAccess.Read(handle, buffer, offset);
        }

        return total;
    }

    // ---- measurement ----

    // bytesPerOp > 0 adds a MB/s column. processWide counts allocation across all threads.
    private static void Row(string label, int ops, int bytesPerOp, Func<int, long> body, bool processWide = false)
    {
        s_sink += body(Math.Max(1, ops / 10));
        double[] nanoseconds = new double[Runs];
        double[] allocated = new double[Runs];
        for (int run = 0; run < Runs; run++)
        {
            GC.Collect();
            long before = processWide ? GC.GetTotalAllocatedBytes(precise: true) : GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            s_sink += body(ops);
            long elapsed = Stopwatch.GetTimestamp() - started;
            long after = processWide ? GC.GetTotalAllocatedBytes(precise: true) : GC.GetAllocatedBytesForCurrentThread();
            nanoseconds[run] = elapsed * 1e9 / Stopwatch.Frequency / ops;
            allocated[run] = (double)(after - before) / ops;
        }

        Array.Sort(nanoseconds);
        Array.Sort(allocated);
        double median = nanoseconds[Runs / 2];
        string throughput = bytesPerOp > 0
            ? string.Create(CultureInfo.InvariantCulture, $"  {bytesPerOp / median * 1000:F0} MB/s")
            : string.Empty;
        Print(string.Create(
            CultureInfo.InvariantCulture,
            $"   {label,-48} {median,10:F2} ns/op [{nanoseconds[0]:F2}..{nanoseconds[Runs - 1]:F2}]  {allocated[Runs / 2],9:F1} B/op{throughput}"));
    }
}
