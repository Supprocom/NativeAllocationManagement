using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Supprocom.NativeAllocationManagement.Performance;

// --memory-echo and --memory-pipe-copy: NAM's Memory<T> against ArrayPool and
// MemoryPool for socket and pipe I/O. Each run prints /proc/loadavg at its start
// and end so a loaded run can be rejected, and reports the median with the
// minimum and maximum of its samples.
internal static class MemoryBenchmarks
{
    private const int SmallMessage = 64;
    private const int LargeMessage = 64 * 1024;

    internal static async Task<int> RunCommandAsync(string[] args)
    {
        bool quick = args.Contains("--quick", StringComparer.Ordinal);
        int samples = ReadInt(args, "--samples", quick ? 2 : 9);
        string loadStart = ReadLoadAverage();
        Print("loadavg at start: " + loadStart);
        int exit;
        if (string.Equals(args[0], "--memory-echo", StringComparison.Ordinal))
        {
            int roundTrips = ReadInt(args, "--round-trips", quick ? 300 : 20_000);
            exit = await RunEchoAsync(samples, roundTrips).ConfigureAwait(false);
        }
        else
        {
            string? directory = ReadString(args, "--dir");
            if (directory is null)
            {
                PrintError("--memory-pipe-copy requires --dir <temporary directory>.");
                return 2;
            }

            int megabytes = ReadInt(args, "--size-mb", quick ? 16 : 1024);
            exit = await RunPipeCopyAsync(samples, megabytes, directory).ConfigureAwait(false);
        }

        Print("loadavg at end: " + ReadLoadAverage());
        return exit;
    }

    // A plain method, so the report lines are not localizable resources and do not block an async caller's analyzer.
    private static void Print(string line) => Console.Out.WriteLine(line);

    private static void PrintError(string line) => Console.Error.WriteLine(line);

    private static int ReadInt(string[] args, string name, int fallback)
    {
        string? text = ReadString(args, name);
        return text is null ? fallback : int.Parse(text, CultureInfo.InvariantCulture);
    }

    private static string? ReadString(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string ReadLoadAverage()
    {
        try
        {
            return File.ReadAllText("/proc/loadavg").Trim();
        }
        catch (IOException)
        {
            return "unavailable";
        }
        catch (UnauthorizedAccessException)
        {
            return "unavailable";
        }
    }

    // ---- socket echo ----

    private enum Provider
    {
        Nam,
        ArrayPool,
        MemoryPool
    }

    private sealed record EchoResult(double OperationsPerSecond, double MegabytesPerSecond, double P50Microseconds, double P99Microseconds, double AllocatedBytesPerOperation);

    private static async Task<int> RunEchoAsync(int samples, int roundTrips)
    {
        Provider[] providers = Enum.GetValues<Provider>();
        int[] sizes = [SmallMessage, LargeMessage];
        Dictionary<(Provider, int), List<EchoResult>> results = [];
        foreach (int size in sizes)
        {
            foreach (Provider provider in providers)
            {
                results[(provider, size)] = [];
            }
        }

        // One warm-up pass per provider and size, then samples in rotating order.
        foreach (int size in sizes)
        {
            foreach (Provider provider in providers)
            {
                _ = await EchoOnceAsync(provider, size, Math.Min(roundTrips, 200)).ConfigureAwait(false);
            }
        }

        for (int sample = 0; sample < samples; sample++)
        {
            foreach (int size in sizes)
            {
                int trips = size <= 4096 ? roundTrips : Math.Max(roundTrips / 10, 50);
                for (int offset = 0; offset < providers.Length; offset++)
                {
                    Provider provider = providers[(sample + offset) % providers.Length];
                    results[(provider, size)].Add(await EchoOnceAsync(provider, size, trips).ConfigureAwait(false));
                }
            }
        }

        Print($"memory-echo: {samples} samples, loopback TCP, one connection, echo of N-byte messages");
        Print("size      provider     ops/s (med [min..max])            MB/s (med)   p50 us   p99 us   alloc B/op");
        foreach (int size in sizes)
        {
            foreach (Provider provider in providers)
            {
                List<EchoResult> list = results[(provider, size)];
                Print(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{size,-9} {provider,-12} {Summary(list.ConvertAll(static r => r.OperationsPerSecond)),-34} {Median(list.ConvertAll(static r => r.MegabytesPerSecond)),10:F1} {Median(list.ConvertAll(static r => r.P50Microseconds)),8:F1} {Median(list.ConvertAll(static r => r.P99Microseconds)),8:F1} {Median(list.ConvertAll(static r => r.AllocatedBytesPerOperation)),11:F1}"));
            }
        }

        return 0;
    }

    private static async Task<EchoResult> EchoOnceAsync(Provider provider, int size, int roundTrips)
    {
        TaskCompletionSource<IPEndPoint> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task serverLoop = ServerAsync(provider, size, ready);
        IPEndPoint endpoint = await ready.Task.ConfigureAwait(false);
        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.NoDelay = true;
        await client.ConnectAsync(endpoint).ConfigureAwait(false);
        using BufferSet clientBuffer = BufferSet.Create(provider, size);
        long[] ticks = new long[roundTrips];

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long started = Stopwatch.GetTimestamp();
        for (int round = 0; round < roundTrips; round++)
        {
            long begin = Stopwatch.GetTimestamp();
            clientBuffer.Get().Span[0] = unchecked((byte)round);
            await SendAllAsync(client, clientBuffer.Get(), size).ConfigureAwait(false);
            await ReceiveAllAsync(client, clientBuffer.Get(), size).ConfigureAwait(false);
            if (clientBuffer.Get().Span[0] != unchecked((byte)round))
            {
                throw new InvalidOperationException("The echo returned different data.");
            }

            ticks[round] = Stopwatch.GetTimestamp() - begin;
        }

        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        double seconds = (double)elapsed / Stopwatch.Frequency;
        // The server loop uses its buffer and socket: it ends before they are disposed.
        client.Shutdown(SocketShutdown.Send);
        await serverLoop.ConfigureAwait(false);

        Array.Sort(ticks);
        double microsecondsPerTick = 1_000_000d / Stopwatch.Frequency;
        return new EchoResult(
            roundTrips / seconds,
            (double)size * roundTrips / seconds / (1024 * 1024),
            ticks[roundTrips / 2] * microsecondsPerTick,
            ticks[Math.Min(roundTrips - 1, (int)(roundTrips * 0.99))] * microsecondsPerTick,
            (double)allocated / roundTrips);
    }

    // The server owns its listener, connection and buffer, so nothing it uses is disposed by the caller.
    private static async Task ServerAsync(Provider provider, int size, TaskCompletionSource<IPEndPoint> ready)
    {
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        ready.SetResult((IPEndPoint)listener.LocalEndPoint!);
        using Socket socket = await listener.AcceptAsync().ConfigureAwait(false);
        socket.NoDelay = true;
        using BufferSet buffer = BufferSet.Create(provider, size);
        while (true)
        {
            int received = await socket.ReceiveAsync(buffer.Get(), SocketFlags.None).ConfigureAwait(false);
            if (received == 0)
            {
                return;
            }

            await SendAllAsync(socket, buffer.Get(), received).ConfigureAwait(false);
        }
    }

    private static async Task SendAllAsync(Socket socket, Memory<byte> buffer, int count)
    {
        int sent = 0;
        while (sent < count)
        {
            sent += await socket.SendAsync(buffer[sent..count], SocketFlags.None).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveAllAsync(Socket socket, Memory<byte> buffer, int count)
    {
        int received = 0;
        while (received < count)
        {
            int read = await socket.ReceiveAsync(buffer[received..count], SocketFlags.None).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The echo connection closed early.");
            }

            received += read;
        }
    }

    // A connection's buffer: the provider's own, asked for its Memory<byte> on every operation.
    private sealed class BufferSet : IDisposable
    {
        private readonly Provider _provider;
        private NativeTransfer<byte> _transfer;
        private byte[]? _array;
        private IMemoryOwner<byte>? _owner;

        private BufferSet(Provider provider) => _provider = provider;

        internal static BufferSet Create(Provider provider, int size)
        {
            BufferSet set = new(provider);
            switch (provider)
            {
                case Provider.Nam:
                    set._transfer = NativeTransfer<byte>.CreateUninitialized(size);
                    break;
                case Provider.ArrayPool:
                    set._array = ArrayPool<byte>.Shared.Rent(size);
                    break;
                default:
                    set._owner = MemoryPool<byte>.Shared.Rent(size);
                    break;
            }

            return set;
        }

        internal Memory<byte> Get() => _provider switch
        {
            Provider.Nam => _transfer.AsMemory(),
            Provider.ArrayPool => _array.AsMemory(),
            _ => _owner!.Memory
        };

        public void Dispose()
        {
            switch (_provider)
            {
                case Provider.Nam:
                    _transfer.Dispose();
                    break;
                case Provider.ArrayPool:
                    ArrayPool<byte>.Shared.Return(_array!);
                    break;
                default:
                    _owner!.Dispose();
                    break;
            }
        }
    }

    // ---- pipe copy ----

    private enum PipePool
    {
        Default,
        ArrayPool,
        MemoryPool,
        Nam
    }

    private sealed record CopyResult(double MegabytesPerSecond, double AllocatedMegabytes);

    private static async Task<int> RunPipeCopyAsync(int samples, int megabytes, string directory)
    {
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "nam-memory-pipe-" + Guid.NewGuid().ToString("N") + ".src");
        string destination = Path.Combine(directory, "nam-memory-pipe-" + Guid.NewGuid().ToString("N") + ".dst");
        try
        {
            await GenerateAsync(source, (long)megabytes * 1024 * 1024).ConfigureAwait(false);
            PipePool[] pools = Enum.GetValues<PipePool>();
            Dictionary<PipePool, List<CopyResult>> results = pools.ToDictionary(static pool => pool, static _ => new List<CopyResult>());
            foreach (PipePool pool in pools)
            {
                _ = await CopyOnceAsync(pool, source, destination).ConfigureAwait(false);
            }

            for (int sample = 0; sample < samples; sample++)
            {
                for (int offset = 0; offset < pools.Length; offset++)
                {
                    PipePool pool = pools[(sample + offset) % pools.Length];
                    results[pool].Add(await CopyOnceAsync(pool, source, destination).ConfigureAwait(false));
                }
            }

            Print($"memory-pipe-copy: {megabytes} MiB file copied through System.IO.Pipelines, {samples} samples");
            Print("pool         MB/s (med [min..max])             allocated MB (med)");
            foreach (PipePool pool in pools)
            {
                List<CopyResult> list = results[pool];
                Print(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{pool,-12} {Summary(list.ConvertAll(static r => r.MegabytesPerSecond)),-34} {Median(list.ConvertAll(static r => r.AllocatedMegabytes)),10:F2}"));
            }

            Print("NAM pool: a benchmark-internal MemoryPool<byte> over NativeTransfer<byte>; no public pool type was needed.");
            return 0;
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    private static async Task GenerateAsync(string path, long length)
    {
        byte[] chunk = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(chunk);
        FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            long written = 0;
            while (written < length)
            {
                int count = (int)Math.Min(chunk.Length, length - written);
                await stream.WriteAsync(chunk.AsMemory(0, count)).ConfigureAwait(false);
                written += count;
            }
        }
    }

    private static async Task<CopyResult> CopyOnceAsync(PipePool kind, string source, string destination)
    {
        MemoryPool<byte>? pool = CreatePool(kind);
        try
        {
            Pipe pipe = new(new PipeOptions(pool, minimumSegmentSize: 64 * 1024, useSynchronizationContext: false));
            FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
            long copied = 0;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            long started = Stopwatch.GetTimestamp();
            await using (input.ConfigureAwait(false))
            await using (output.ConfigureAwait(false))
            {
                Task producer = Task.Run(async () =>
                {
                    while (true)
                    {
                        Memory<byte> memory = pipe.Writer.GetMemory(64 * 1024);
                        int read = await input.ReadAsync(memory).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        pipe.Writer.Advance(read);
                        FlushResult flushed = await pipe.Writer.FlushAsync().ConfigureAwait(false);
                        if (flushed.IsCompleted)
                        {
                            break;
                        }
                    }

                    await pipe.Writer.CompleteAsync().ConfigureAwait(false);
                });

                while (true)
                {
                    ReadResult result = await pipe.Reader.ReadAsync().ConfigureAwait(false);
                    foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                    {
                        await output.WriteAsync(segment).ConfigureAwait(false);
                        copied += segment.Length;
                    }

                    pipe.Reader.AdvanceTo(result.Buffer.End);
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }

                await producer.ConfigureAwait(false);
                await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            }

            double seconds = (double)(Stopwatch.GetTimestamp() - started) / Stopwatch.Frequency;
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            if (copied != new FileInfo(source).Length)
            {
                throw new InvalidOperationException("The pipe copy lost data.");
            }

            return new CopyResult(copied / seconds / (1024 * 1024), allocated / (1024d * 1024d));
        }
        finally
        {
            pool?.Dispose();
        }
    }

    private static MemoryPool<byte>? CreatePool(PipePool kind) => kind switch
    {
        PipePool.Default => null,
        PipePool.ArrayPool => new ArrayPoolMemoryPool(),
        PipePool.MemoryPool => MemoryPool<byte>.Shared,
        _ => new NamMemoryPool()
    };

    // MemoryPool<byte> over ArrayPool<byte>.Shared, what the default pool does, as a baseline of the same shape as the NAM pool.
    private sealed class ArrayPoolMemoryPool : MemoryPool<byte>
    {
        public override int MaxBufferSize => int.MaxValue;

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1) =>
            new Owner(ArrayPool<byte>.Shared.Rent(minBufferSize <= 0 ? 4096 : minBufferSize));

        protected override void Dispose(bool disposing)
        {
        }

        private sealed class Owner : IMemoryOwner<byte>
        {
            private byte[]? _array;

            internal Owner(byte[] array) => _array = array;

            public Memory<byte> Memory => _array ?? throw new ObjectDisposedException(nameof(Owner));

            public void Dispose()
            {
                byte[]? array = Interlocked.Exchange(ref _array, null);
                if (array is not null)
                {
                    ArrayPool<byte>.Shared.Return(array);
                }
            }
        }
    }

    // MemoryPool<byte> over NativeTransfer<byte>: each rent is a native range, returned by Dispose on whichever thread ends the segment.
    private sealed class NamMemoryPool : MemoryPool<byte>
    {
        public override int MaxBufferSize => int.MaxValue;

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1) =>
            new Owner(NativeTransfer<byte>.CreateUninitialized(minBufferSize <= 0 ? 4096 : minBufferSize));

        protected override void Dispose(bool disposing)
        {
        }

        private sealed class Owner : IMemoryOwner<byte>
        {
            private NativeTransfer<byte> _transfer;
            private int _disposed;

            internal Owner(NativeTransfer<byte> transfer) => _transfer = transfer;

            public Memory<byte> Memory => _transfer.AsMemory();

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _transfer.Dispose();
                }
            }
        }
    }

    // ---- statistics ----

    private static double Median(List<double> values)
    {
        double[] sorted = [.. values];
        Array.Sort(sorted);
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private static string Summary(List<double> values)
    {
        if (values.Count == 0)
        {
            return "n/a";
        }

        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"{Median(values):F0} [{values.Min():F0}..{values.Max():F0}]");
        return text.ToString();
    }
}
