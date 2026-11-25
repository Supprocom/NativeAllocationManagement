using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Performance;

/// <summary>
/// --mapped-files [--quick] [--dir PATH] [--size-mib N] [--reads N] [--reps N]
/// Sequential and random reads of 16-byte structs over one file, warm and cold page cache,
/// NAM views against MemoryMappedViewAccessor and RandomAccess.
/// </summary>
internal static partial class MappedFileBenchmark
{
    private const int TicksPerWindow = 4096;
    private const int TickBytes = 16;
    private const int PosixFadviseDontNeed = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct Tick
    {
        public long Time;
        public double Price;
    }

    private sealed record Options(bool Quick, string Directory, long SizeBytes, int Reads, int WarmReps, int ColdReps);

    private sealed record Row(string Workload, string Cache, string Implementation, double Milliseconds, double NanosPerOp, double MegabytesPerSecond, double AllocatedBytesPerOp, long Checksum);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int posix_fadvise(int fd, long offset, long length, int advice);

    internal static int Run(string[] args)
    {
        if (!TryParse(args, out Options? options, out string? error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        Directory.CreateDirectory(options.Directory);
        string path = Path.Combine(options.Directory, "nam-mapped-bench-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            Console.WriteLine($"mapped-files: size {options.SizeBytes >> 20} MiB, dir {options.Directory}, quick {options.Quick}, reads {options.Reads}, load {(File.Exists("/proc/loadavg") ? File.ReadAllText("/proc/loadavg").Trim() : "n/a")}");
            WriteFile(path, options.SizeBytes);
            bool evictable = OperatingSystem.IsLinux() && !options.Directory.StartsWith("/dev/shm", StringComparison.Ordinal);
            long ticks = options.SizeBytes / TickBytes;
            long[] indices = new long[options.Reads];
            Random random = new(12345);
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = random.NextInt64(ticks);
            }

            List<Row> rows = [];
            foreach (bool cold in new[] { false, true })
            {
                string cache = cold ? (evictable ? "cold" : "cold (not evicted)") : "warm";
                int reps = cold ? options.ColdReps : options.WarmReps;
                // Cold random reads touch one page each; fewer of them keep a cold pass short.
                long[] workIndices = cold && evictable ? indices[..Math.Max(1, indices.Length / 20)] : indices;
                (string Workload, string Implementation, Func<(long Sum, long Ops, long Bytes)> Body)[] cases =
                [
                    ("sequential", "NAM view", () => NamSequential(path, ticks)),
                    ("sequential", "accessor.ReadArray", () => AccessorSequential(path, ticks)),
                    ("sequential", "RandomAccess+ArrayPool", () => RandomAccessSequential(path, ticks)),
                    ("random", "NAM view", () => NamRandom(path, workIndices)),
                    ("random", "accessor.Read<T>", () => AccessorRandom(path, workIndices, useReadArray: false)),
                    ("random", "accessor.ReadArray", () => AccessorRandom(path, workIndices, useReadArray: true)),
                    ("random", "RandomAccess", () => RandomAccessRandom(path, workIndices)),
                ];
                if (!cold)
                {
                    // Prime the page cache once so warm numbers do not include the first read.
                    _ = RandomAccessSequential(path, ticks);
                }

                foreach ((string workload, string implementation, Func<(long Sum, long Ops, long Bytes)> body) in cases)
                {
                    rows.Add(Measure(workload, cache, implementation, reps, cold && evictable, path, body));
                }
            }

            Print(rows);
            return Validate(rows) ? 0 : 3;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool TryParse(string[] args, out Options options, out string error)
    {
        bool quick = false;
        string? dir = null;
        long sizeMib = 0;
        int reads = 0;
        int reps = 0;
        error = string.Empty;
        options = null!;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--quick":
                    quick = true;
                    break;
                case "--dir" when i + 1 < args.Length:
                    dir = args[++i];
                    break;
                case "--size-mib" when i + 1 < args.Length && long.TryParse(args[i + 1], CultureInfo.InvariantCulture, out sizeMib):
                    i++;
                    break;
                case "--reads" when i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out reads):
                    i++;
                    break;
                case "--reps" when i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out reps):
                    i++;
                    break;
                default:
                    error = "unknown or incomplete option: " + args[i];
                    return false;
            }
        }

        if (dir is null)
        {
            if (!quick)
            {
                error = "a full run needs --dir PATH on the disk under test (a 4 GiB file is written there); --quick needs nothing.";
                return false;
            }

            dir = Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath();
        }

        sizeMib = sizeMib != 0 ? sizeMib : quick ? 32 : 4096;
        if (sizeMib <= 0 || (sizeMib << 20) % (TicksPerWindow * TickBytes) != 0)
        {
            error = "--size-mib must be a multiple of 1 MiB.";
            return false;
        }

        if (dir.StartsWith("/dev/shm", StringComparison.Ordinal) && sizeMib > 256)
        {
            error = "memory-backed /dev/shm is limited to 256 MiB here.";
            return false;
        }

        options = new Options(
            quick,
            dir,
            sizeMib << 20,
            reads != 0 ? reads : quick ? 50_000 : 2_000_000,
            reps != 0 ? reps : quick ? 2 : 5,
            quick ? 1 : 2);
        return true;
    }

    private static void WriteFile(string path, long sizeBytes)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        Tick[] chunk = new Tick[(1 << 20) / TickBytes];
        long written = 0;
        while (written < sizeBytes)
        {
            for (int i = 0; i < chunk.Length; i++)
            {
                long index = (written / TickBytes) + i;
                chunk[i] = new Tick { Time = index, Price = index * 0.25 };
            }

            stream.Write(MemoryMarshal.AsBytes(chunk.AsSpan()));
            written += chunk.Length * TickBytes;
        }

        stream.Flush(flushToDisk: true);
    }

    private static void Evict(string path)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
        _ = posix_fadvise((int)handle.DangerousGetHandle(), 0, 0, PosixFadviseDontNeed);
    }

    private static Row Measure(string workload, string cache, string implementation, int reps, bool evict, string path, Func<(long Sum, long Ops, long Bytes)> body)
    {
        double[] millis = new double[reps];
        long sum = 0;
        long ops = 0;
        long bytes = 0;
        long allocated = 0;
        for (int rep = 0; rep < reps; rep++)
        {
            if (evict)
            {
                Evict(path);
            }

            long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            (sum, ops, bytes) = body();
            millis[rep] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        }

        Array.Sort(millis);
        double median = millis[reps / 2];
        return new Row(
            workload,
            cache,
            implementation,
            median,
            median * 1e6 / ops,
            bytes / 1048576.0 / (median / 1000.0),
            (double)allocated / ops,
            sum);
    }

    private static (long, long, long) NamSequential(string path, long ticks)
    {
        // Opening and closing the mapping is part of every measurement, as it is for the baselines.
        using NativeMappedFile file = NativeMappedFile.OpenRead(path);
        using MappedView<Tick> view = file.View<Tick>();
        long sum = 0;
        for (long start = 0; start < ticks; start += TicksPerWindow)
        {
            foreach (Tick tick in view.AsReadOnlySpan(start, TicksPerWindow))
            {
                sum += tick.Time;
            }
        }

        return (sum, ticks, ticks * TickBytes);
    }

    private static (long, long, long) AccessorSequential(string path, long ticks)
    {
        using MemoryMappedFile map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using MemoryMappedViewAccessor accessor = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        Tick[] buffer = new Tick[TicksPerWindow];
        long sum = 0;
        for (long start = 0; start < ticks; start += TicksPerWindow)
        {
            _ = accessor.ReadArray(start * TickBytes, buffer, 0, TicksPerWindow);
            foreach (Tick tick in buffer)
            {
                sum += tick.Time;
            }
        }

        return (sum, ticks, ticks * TickBytes);
    }

    private static (long, long, long) RandomAccessSequential(string path, long ticks)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TicksPerWindow * TickBytes);
        try
        {
            long sum = 0;
            for (long start = 0; start < ticks; start += TicksPerWindow)
            {
                Span<byte> window = buffer.AsSpan(0, TicksPerWindow * TickBytes);
                int read = RandomAccess.Read(handle, window, start * TickBytes);
                if (read != window.Length)
                {
                    throw new IOException("short read");
                }

                foreach (Tick tick in MemoryMarshal.Cast<byte, Tick>(window))
                {
                    sum += tick.Time;
                }
            }

            return (sum, ticks, ticks * TickBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static (long, long, long) NamRandom(string path, long[] indices)
    {
        using NativeMappedFile file = NativeMappedFile.OpenRead(path);
        using MappedView<Tick> view = file.View<Tick>();
        long sum = 0;
        foreach (long index in indices)
        {
            sum += view.Read(index).Time;
        }

        return (sum, indices.Length, indices.LongLength * TickBytes);
    }

    private static (long, long, long) AccessorRandom(string path, long[] indices, bool useReadArray)
    {
        using MemoryMappedFile map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using MemoryMappedViewAccessor accessor = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        Tick[] one = new Tick[1];
        long sum = 0;
        foreach (long index in indices)
        {
            if (useReadArray)
            {
                _ = accessor.ReadArray(index * TickBytes, one, 0, 1);
                sum += one[0].Time;
            }
            else
            {
                accessor.Read(index * TickBytes, out Tick tick);
                sum += tick.Time;
            }
        }

        return (sum, indices.Length, indices.LongLength * TickBytes);
    }

    private static (long, long, long) RandomAccessRandom(string path, long[] indices)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> buffer = stackalloc byte[TickBytes];
        long sum = 0;
        foreach (long index in indices)
        {
            if (RandomAccess.Read(handle, buffer, index * TickBytes) != TickBytes)
            {
                throw new IOException("short read");
            }

            sum += MemoryMarshal.Read<Tick>(buffer).Time;
        }

        return (sum, indices.Length, indices.LongLength * TickBytes);
    }

    private static void Print(List<Row> rows)
    {
        Console.WriteLine("| workload | cache | implementation | ms | ns/op | MB/s | alloc B/op |");
        Console.WriteLine("| --- | --- | --- | ---: | ---: | ---: | ---: |");
        foreach (Row row in rows)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {row.Workload} | {row.Cache} | {row.Implementation} | {row.Milliseconds:F1} | {row.NanosPerOp:F1} | {row.MegabytesPerSecond:F0} | {row.AllocatedBytesPerOp:F3} |"));
        }
    }

    // Every implementation must read the same data; a mismatch means a wrong baseline or a wrong view.
    private static bool Validate(List<Row> rows)
    {
        bool ok = true;
        foreach (IGrouping<(string, string), Row> group in rows.GroupBy(row => (row.Workload, row.Cache)))
        {
            if (group.Select(row => row.Checksum).Distinct().Count() != 1)
            {
                Console.Error.WriteLine($"checksum mismatch in {group.Key.Item1}/{group.Key.Item2}");
                ok = false;
            }
        }

        return ok;
    }
}
