using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Supprocom.NativeAllocationManagement.Performance;

// --memory-native-handoff: data goes on to native code that keeps the buffer after the handing-off
// call returns. The pipe reader gives each segment to a consumer as a pointer and a length and
// acknowledges nothing until the consumer has checksummed it; only then does the reader advance,
// so the buffer must stay valid and put until the acknowledgement. At most four segments are in
// flight. NAM storage is native and stays put, so it needs no pin and no copy; an array from a
// managed pool needs a pin held until the acknowledgement, or a copy into native memory.
internal static class MemoryHandoffBenchmarks
{
    private const int SegmentBytes = 64 * 1024;

    private static ulong s_checksum;

    private enum Mode
    {
        NamPool,
        ArrayPoolPinned,
        ArrayPoolCopied,
        PinnedHeapPool
    }

    private sealed record Result(double MegabytesPerSecond, double AllocatedMegabytesPerGiB);

    internal static async Task<int> RunCommandAsync(string[] args)
    {
        bool quick = args.Contains("--quick", StringComparer.Ordinal);
        int samples = ReadInt(args, "--samples", quick ? 2 : 9);
        int megabytes = ReadInt(args, "--size-mb", quick ? 16 : 1024);
        string directory = ReadString(args, "--dir") ?? "/home/claude/nam-bench-tmp";
        Print("loadavg at start: " + MemoryBenchmarks.ReadLoadAverage());

        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"nam-handoff-source-{megabytes}mb.bin"));
        EnsureSource(source, (long)megabytes * 1024 * 1024);

        Mode[] modes = Enum.GetValues<Mode>();
        Dictionary<Mode, List<Result>> results = modes.ToDictionary(static mode => mode, static _ => new List<Result>());
        foreach (Mode mode in modes)
        {
            _ = await RunOnceAsync(mode, source).ConfigureAwait(false);
        }

        for (int sample = 0; sample < samples; sample++)
        {
            for (int offset = 0; offset < modes.Length; offset++)
            {
                Mode mode = modes[(sample + offset) % modes.Length];
                results[mode].Add(await RunOnceAsync(mode, source).ConfigureAwait(false));
            }
        }

        Print(string.Create(CultureInfo.InvariantCulture, $"memory-native-handoff: {megabytes} MiB through a pipe to a native consumer, 4 segments in flight, {samples} samples"));
        Print("row                              MB/s (med [min..max])             managed MB per GiB (med)");
        foreach (Mode mode in modes)
        {
            List<Result> list = results[mode];
            Print(string.Create(
                CultureInfo.InvariantCulture,
                $"{Describe(mode),-32} {MemoryBenchmarks.Summary(list.ConvertAll(static r => r.MegabytesPerSecond)),-34} {MemoryBenchmarks.Median(list.ConvertAll(static r => r.AllocatedMegabytesPerGiB)),10:F2}"));
        }

        Print("loadavg at end: " + MemoryBenchmarks.ReadLoadAverage());
        Print(string.Create(CultureInfo.InvariantCulture, $"checksum {s_checksum}"));
        return 0;
    }

    private static string Describe(Mode mode) => mode switch
    {
        Mode.NamPool => "NAM pool, pointer as is",
        Mode.ArrayPoolPinned => "ArrayPool adapter, pinned",
        Mode.ArrayPoolCopied => "ArrayPool adapter, copied",
        _ => "pinned-heap arrays, pointer as is"
    };

    private static void Print(string line) => MemoryBenchmarks.Print(line);

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

    // The source stays between runs: created once if it is missing or the wrong size.
    private static void EnsureSource(string path, long length)
    {
        if (File.Exists(path) && new FileInfo(path).Length == length)
        {
            return;
        }

        byte[] chunk = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(chunk);
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1);
        for (long written = 0; written < length; written += chunk.Length)
        {
            stream.Write(chunk, 0, (int)Math.Min(chunk.Length, length - written));
        }
    }

    private static async Task<Result> RunOnceAsync(Mode mode, string source)
    {
        MemoryPool<byte> pool = mode switch
        {
            Mode.NamPool => NativeMemoryPool.Shared,
            Mode.PinnedHeapPool => new PinnedHeapMemoryPool(),
            _ => new MemoryBenchmarks.ArrayPoolMemoryPool()
        };
        try
        {
            Pipe pipe = new(new PipeOptions(pool, minimumSegmentSize: SegmentBytes, useSynchronizationContext: false));
            FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long length = new FileInfo(source).Length;
            using HandoffRing ring = new(copyBuffers: mode == Mode.ArrayPoolCopied);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            long started = Stopwatch.GetTimestamp();
            await using (input.ConfigureAwait(false))
            {
                Task producer = Task.Run(async () =>
                {
                    while (true)
                    {
                        Memory<byte> memory = pipe.Writer.GetMemory(SegmentBytes);
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

                MemoryHandle[] pins = new MemoryHandle[HandoffRing.Slots];
                while (true)
                {
                    ReadResult result = await pipe.Reader.ReadAsync().ConfigureAwait(false);
                    long last = -1;
                    foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                    {
                        long ticket = ring.Acquire();
                        int slot = (int)(ticket % HandoffRing.Slots);
                        switch (mode)
                        {
                            case Mode.ArrayPoolPinned:
                                // The slot's previous pin ended with its acknowledgement, which Acquire waited for.
                                pins[slot].Dispose();
                                pins[slot] = segment.Pin();
                                ring.Publish(ticket, HandoffRing.AddressOf(pins[slot]), segment.Length);
                                break;
                            case Mode.ArrayPoolCopied:
                                ring.Publish(ticket, ring.CopyInto(ticket, segment.Span), segment.Length);
                                break;
                            default:
                                ring.Publish(ticket, HandoffRing.AddressOf(segment.Span), segment.Length);
                                break;
                        }

                        last = ticket;
                    }

                    // The buffers belong to the pipe again at AdvanceTo: every segment is acknowledged first.
                    if (last >= 0)
                    {
                        ring.WaitAcknowledged(last);
                    }

                    foreach (ref MemoryHandle pin in pins.AsSpan())
                    {
                        pin.Dispose();
                        pin = default;
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

            ring.Finish();
            double seconds = (double)(Stopwatch.GetTimestamp() - started) / Stopwatch.Frequency;
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            s_checksum += ring.ChecksumValue;
            if (ring.ConsumedBytes != length)
            {
                throw new InvalidOperationException("The consumer did not see every byte.");
            }

            double gibibytes = length / (1024d * 1024d * 1024d);
            return new Result(length / seconds / (1024 * 1024), allocated / (1024d * 1024d) / gibibytes);
        }
        finally
        {
            pool.Dispose();
        }
    }

    // MemoryPool<byte> over pinned-object-heap arrays, kept for reuse: their address never changes, so
    // a pointer to one needs no pin call.
    private sealed class PinnedHeapMemoryPool : MemoryPool<byte>
    {
        private readonly ConcurrentStack<byte[]> _free = new();

        public override int MaxBufferSize => int.MaxValue;

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            int size = minBufferSize <= 0 ? 4096 : minBufferSize;
            if (!_free.TryPop(out byte[]? array) || array.Length < size)
            {
                array = GC.AllocateArray<byte>(size, pinned: true);
            }

            return new Owner(this, array);
        }

        protected override void Dispose(bool disposing)
        {
        }

        private sealed class Owner : IMemoryOwner<byte>
        {
            private readonly PinnedHeapMemoryPool _pool;
            private byte[]? _array;

            internal Owner(PinnedHeapMemoryPool pool, byte[] array)
            {
                _pool = pool;
                _array = array;
            }

            public Memory<byte> Memory => _array ?? throw new ObjectDisposedException(nameof(Owner));

            public void Dispose()
            {
                byte[]? array = Interlocked.Exchange(ref _array, null);
                if (array is not null)
                {
                    _pool._free.Push(array);
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct Slot
    {
        public nint Pointer;
        public long Length;
        public long Posted;
        public long Done;
    }

    // The consumer's side is unmanaged: a ring of slots in native memory holding only a pointer, a
    // length and two counters, and a thread that reads nothing but those and the bytes they point at.
    // Ticket t uses slot t % 4. The producer posts a ticket by writing its pointer and length and
    // then Posted = t + 1; the consumer, taking tickets in order, checksums the bytes and writes
    // Done = t + 1. A slot is free again once its previous ticket is Done.
    private sealed unsafe class HandoffRing : IDisposable
    {
        internal const int Slots = 4;
        private const int CopyBufferBytes = 4 * 1024 * 1024;

        private readonly Slot* _slots = (Slot*)NativeMemory.AllocZeroed((nuint)(Slots * sizeof(Slot)));
        private readonly nint[]? _copies;
        private readonly Thread _consumer;
        private long _next;
        private long _consumedBytes;
        private ulong _checksum;
        private bool _finished;

        internal HandoffRing(bool copyBuffers)
        {
            if (copyBuffers)
            {
                _copies = new nint[Slots];
                foreach (ref nint copy in _copies.AsSpan())
                {
                    copy = (nint)NativeMemory.Alloc(CopyBufferBytes);
                }
            }

            _consumer = new Thread(Consume) { IsBackground = true };
            _consumer.Start();
        }

        internal long ConsumedBytes => Volatile.Read(ref _consumedBytes);

        internal ulong ChecksumValue => _checksum;

        internal static nint AddressOf(ReadOnlySpan<byte> span) =>
            (nint)Unsafe.AsPointer(ref Unsafe.AsRef(in MemoryMarshal.GetReference(span)));

        internal static nint AddressOf(MemoryHandle handle) => (nint)handle.Pointer;

        // Waits until the slot of the next ticket is free (its previous ticket was acknowledged).
        internal long Acquire()
        {
            long ticket = _next++;
            Slot* slot = &_slots[ticket % Slots];
            SpinWait wait = default;
            while (Volatile.Read(ref slot->Done) < ticket - (Slots - 1))
            {
                wait.SpinOnce(-1);
            }

            return ticket;
        }

        internal nint CopyInto(long ticket, ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > CopyBufferBytes)
            {
                throw new InvalidOperationException("A segment is larger than the copy buffer.");
            }

            byte* target = (byte*)_copies![ticket % Slots];
            bytes.CopyTo(new Span<byte>(target, bytes.Length));
            return (nint)target;
        }

        internal void Publish(long ticket, nint pointer, int length)
        {
            Slot* slot = &_slots[ticket % Slots];
            slot->Pointer = pointer;
            slot->Length = length;
            Volatile.Write(ref slot->Posted, ticket + 1);
        }

        internal void WaitAcknowledged(long ticket)
        {
            Slot* slot = &_slots[ticket % Slots];
            SpinWait wait = default;
            while (Volatile.Read(ref slot->Done) < ticket + 1)
            {
                wait.SpinOnce(-1);
            }
        }

        // Ends the consumer after everything posted so far.
        internal void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            long ticket = Acquire();
            Publish(ticket, 0, -1);
            _consumer.Join();
        }

        private void Consume()
        {
            for (long ticket = 0; ; ticket++)
            {
                Slot* slot = &_slots[ticket % Slots];
                SpinWait wait = default;
                while (Volatile.Read(ref slot->Posted) < ticket + 1)
                {
                    wait.SpinOnce(-1);
                }

                long length = slot->Length;
                if (length < 0)
                {
                    return;
                }

                // The pointer is used after the producer's call returned, until Done is written.
                _checksum += Checksum((byte*)slot->Pointer, length);
                _consumedBytes += length;
                Volatile.Write(ref slot->Done, ticket + 1);
            }
        }

        private static ulong Checksum(byte* bytes, long length)
        {
            ulong sum = 0;
            ulong* words = (ulong*)bytes;
            long count = length >> 3;
            for (long index = 0; index < count; index++)
            {
                sum += words[index];
            }

            for (long index = count << 3; index < length; index++)
            {
                sum += bytes[index];
            }

            return sum;
        }

        public void Dispose()
        {
            Finish();
            NativeMemory.Free(_slots);
            if (_copies is not null)
            {
                foreach (nint copy in _copies)
                {
                    NativeMemory.Free((void*)copy);
                }
            }
        }
    }
}
