using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

// A MemoryPool<byte> over native storage, for System.IO.Pipelines and other APIs that rent buffers.
//
// Rent hands out one owner object, which is itself the MemoryManager<byte>: it holds the
// storage pointer and length directly, so the Span of its Memory is one field read and a check.
// Disposing the owner clears the pointer, so a Memory<byte> taken from it throws
// ObjectDisposedException instead of reading storage that has gone to another rent. The storage
// goes to a small per-thread cache by power-of-two size class (4 KiB up), as ArrayPool keeps its
// arrays; a block returned on another thread joins that thread's cache. A full cache frees.
//
// A pin keeps the storage: disposing the owner while pinned does not return it until the last
// unpin. Storage comes through NativeBlockAllocator, so native-byte accounting sees it.
internal sealed class NativeMemoryPool : MemoryPool<byte>
{
    private const int MinClassShift = 12;
    private const int ClassCount = 19;
    private const int SlotsPerClass = 8;
    private const int MaxClassShift = MinClassShift + ClassCount - 1;
    // A full thread cache moves half its blocks to the shared stack of the class, and an empty one
    // takes that many back: a thread that only returns what another thread rented (a pipe's reader)
    // then pays a lock once per batch rather than once per block.
    private const int Batch = SlotsPerClass / 2;
    private const nuint SharedBytesPerClass = 4 * 1024 * 1024;
    private const int MaxSharedBlocks = 64;

    private static readonly int s_cacheSlot = NativeThreadState.AllocateTypedSlot();
    private static readonly SharedStack[] s_shared = CreateShared();

    internal static NativeMemoryPool Default { get; } = new();

    public override int MaxBufferSize => 1 << MaxClassShift;

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        int size = minBufferSize <= 0 ? 1 << MinClassShift : minBufferSize;
        if (size > MaxBufferSize)
        {
            throw new ArgumentOutOfRangeException(nameof(minBufferSize));
        }

        int shift = Math.Max(MinClassShift, BitOperations.Log2((uint)(size - 1)) + 1);
        NativeThreadState thread = NativeThreadState.Current;
        ThreadCache cache = thread.GetTyped<ThreadCache>(s_cacheSlot) ?? CreateCache(thread);
        if (!cache.TryTake(shift - MinClassShift, out CachedBlock block))
        {
            block = Refill(cache, shift - MinClassShift);
        }

        return Owner.Create(block, 1 << shift);
    }

    // The cache missed: a batch from the shared stack, else new storage.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CachedBlock Refill(ThreadCache cache, int index)
    {
        SharedStack shared = s_shared[index];
        if (shared.Capacity != 0 && shared.TryTakeBatch(cache, index, out CachedBlock first))
        {
            return first;
        }

        NativeBlock fresh = NativeBlockAllocator.Allocate((nuint)1 << (index + MinClassShift), nameof(NativeMemoryPool), "NativeMemoryPool.Rent");
        return new CachedBlock(fresh.Pointer, fresh.MetricsEpoch);
    }

    // Storage returns to the calling thread's cache, whichever thread rented it.
    private static void Return(CachedBlock block, int length)
    {
        int index = BitOperations.Log2((uint)length) - MinClassShift;
        NativeThreadState thread = NativeThreadState.Current;
        ThreadCache cache = thread.GetTyped<ThreadCache>(s_cacheSlot) ?? CreateCache(thread);
        if (!cache.TryAdd(index, block))
        {
            Spill(cache, index, block);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Spill(ThreadCache cache, int index, CachedBlock block)
    {
        SharedStack shared = s_shared[index];
        if (shared.Capacity != 0)
        {
            shared.AddBatch(cache, index, block);
            return;
        }

        FreeBlock(block, index);
    }

    private static void FreeBlock(CachedBlock block, int index) =>
        NativeBlockAllocator.Free(new NativeBlock(block.Pointer, (nuint)1 << (index + MinClassShift), block.Epoch));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ThreadCache CreateCache(NativeThreadState thread)
    {
        ThreadCache cache = new();
        thread.SetTyped(s_cacheSlot, cache);
        return cache;
    }

    private static SharedStack[] CreateShared()
    {
        SharedStack[] stacks = new SharedStack[ClassCount];
        for (int index = 0; index < stacks.Length; index++)
        {
            stacks[index] = new SharedStack((int)Math.Min((nuint)MaxSharedBlocks, SharedBytesPerClass >> (index + MinClassShift)));
        }

        return stacks;
    }

    // Pieces of a rent and return cycle, for --memory-cost to split its time.
    internal static long ProbeThreadState(int ops)
    {
        long sum = 0;
        for (int index = 0; index < ops; index++)
        {
            sum += NativeThreadState.Current.Serial;
        }

        return sum;
    }

    internal static long ProbeCache(int ops)
    {
        long sum = 0;
        NativeBlock spare = NativeBlockAllocator.Allocate(1 << 16, nameof(NativeMemoryPool), "NativeMemoryPool.Probe");
        CachedBlock spareBlock = new(spare.Pointer, spare.MetricsEpoch);
        for (int index = 0; index < ops; index++)
        {
            NativeThreadState thread = NativeThreadState.Current;
            ThreadCache cache = thread.GetTyped<ThreadCache>(s_cacheSlot) ?? CreateCache(thread);
            if (cache.TryTake(4, out CachedBlock taken))
            {
                sum += 1;
                _ = cache.TryAdd(4, taken);
            }
            else
            {
                _ = cache.TryAdd(4, spareBlock);
            }
        }

        ThreadCache own = NativeThreadState.Current.GetTyped<ThreadCache>(s_cacheSlot)!;
        own.ReleaseStorageOfEndedThread();
        return sum;
    }

    internal static long ProbeOwner(int ops, bool dispose)
    {
        long sum = 0;
        NativeBlock block = NativeBlockAllocator.Allocate(1 << 16, nameof(NativeMemoryPool), "NativeMemoryPool.Probe");
        CachedBlock cached = new(block.Pointer, block.MetricsEpoch);
        for (int index = 0; index < ops; index++)
        {
#pragma warning disable CA2000 // The probe measures construction alone; the garbage owner holds no unmanaged state of its own.
            Owner owner = Owner.Create(cached, 1 << 16);
#pragma warning restore CA2000
            sum += owner.Memory.Length;
            if (dispose)
            {
                // Returns the block to the cache, which the probe then empties again.
                ((IDisposable)owner).Dispose();
                _ = NativeThreadState.Current.GetTyped<ThreadCache>(s_cacheSlot)!.TryTake(4, out _);
            }
        }

        NativeBlockAllocator.Free(block);
        return sum;
    }

    // The shared instance is never disposed; cached storage belongs to threads, not to the pool.
    protected override void Dispose(bool disposing)
    {
    }

    // What a cached block needs to be freed again: the extent follows from its size class.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private readonly record struct CachedBlock(IntPtr Pointer, long Epoch);

    // Blocks that thread caches handed over, by size class, taken and given a batch at a time.
    private sealed class SharedStack
    {
        private readonly Lock _gate = new();
        private readonly CachedBlock[] _blocks;
        private int _count;

        internal SharedStack(int capacity) => _blocks = new CachedBlock[capacity];

        internal int Capacity => _blocks.Length;

        // Moves up to a batch into the caller's cache and returns one for the caller.
        internal bool TryTakeBatch(ThreadCache cache, int index, out CachedBlock first)
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    first = default;
                    return false;
                }

                first = _blocks[--_count];
                for (int moved = 1; moved < Batch && _count != 0; moved++)
                {
                    if (!cache.TryAdd(index, _blocks[_count - 1]))
                    {
                        break;
                    }

                    _count--;
                }

                return true;
            }
        }

        // The cache is full: moves a batch of its blocks, and the returned one, here; what doesn't fit is freed.
        internal void AddBatch(ThreadCache cache, int index, CachedBlock returned)
        {
            Span<CachedBlock> overflow = stackalloc CachedBlock[Batch + 1];
            int overflowCount = 0;
            lock (_gate)
            {
                Push(returned, overflow, ref overflowCount);
                for (int moved = 0; moved < Batch && cache.TryTake(index, out CachedBlock block); moved++)
                {
                    Push(block, overflow, ref overflowCount);
                }
            }

            foreach (ref readonly CachedBlock block in overflow[..overflowCount])
            {
                FreeBlock(block, index);
            }
        }

        private void Push(CachedBlock block, Span<CachedBlock> overflow, ref int overflowCount)
        {
            if (_count < _blocks.Length)
            {
                _blocks[_count++] = block;
            }
            else
            {
                overflow[overflowCount++] = block;
            }
        }
    }

    // Blocks this thread keeps for its next rents, by size class. Larger classes keep fewer.
    private sealed class ThreadCache : INativeThreadStorage
    {
        private readonly CachedBlock[] _blocks = new CachedBlock[ClassCount * SlotsPerClass];
        private readonly int[] _counts = new int[ClassCount];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryTake(int index, out CachedBlock block)
        {
            int count = _counts[index];
            if (count == 0)
            {
                block = default;
                return false;
            }

            _counts[index] = --count;
            block = _blocks[(index * SlotsPerClass) + count];
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryAdd(int index, CachedBlock block)
        {
            int count = _counts[index];
            if (count >= Capacity(index))
            {
                return false;
            }

            _blocks[(index * SlotsPerClass) + count] = block;
            _counts[index] = count + 1;
            return true;
        }

        private static int Capacity(int index) => index <= 10 ? SlotsPerClass : 1;

        public void ReleaseStorageOfEndedThread()
        {
            for (int index = 0; index < ClassCount; index++)
            {
                while (TryTake(index, out CachedBlock block))
                {
                    FreeBlock(block, index);
                }
            }
        }
    }

    // One object per rent, small: the storage address (read by GetSpan; cleared by Dispose), the
    // address held for pins and the return, the accounting epoch, the length and one state word.
    private sealed unsafe class Owner : MemoryManager<byte>
    {
        // Low bits count pins; this bit marks the owner disposed. A pin can only start while it is clear.
        private const int Disposed = 1 << 30;

        private readonly nint _storage;
        private readonly long _epoch;
        private readonly int _length;
        private nint _pointer;
        private int _state;

        private Owner(CachedBlock block, int length)
        {
            _storage = block.Pointer;
            _pointer = block.Pointer;
            _epoch = block.Epoch;
            _length = length;
        }

        internal static Owner Create(CachedBlock block, int length) => new(block, length);

        public override Span<byte> GetSpan()
        {
            nint pointer = Volatile.Read(ref _pointer);
            if (pointer == 0)
            {
                ThrowDisposed();
            }

            return new Span<byte>((void*)pointer, _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if ((uint)elementIndex > (uint)_length)
            {
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            }

            // A pin starts only while the owner is live, so a disposal either sees it (and defers
            // the return to the last unpin) or refuses it; no pin can start on returned storage.
            int state = Volatile.Read(ref _state);
            while (true)
            {
                if ((state & Disposed) != 0)
                {
                    ThrowDisposed();
                }

                int observed = Interlocked.CompareExchange(ref _state, state + 1, state);
                if (observed == state)
                {
                    break;
                }

                state = observed;
            }

            return new MemoryHandle((byte*)_storage + elementIndex, default, this);
        }

        public override void Unpin()
        {
            // A copied handle disposed twice must not release another handle's pin.
            int state = Volatile.Read(ref _state);
            while ((state & ~Disposed) > 0)
            {
                int observed = Interlocked.CompareExchange(ref _state, state - 1, state);
                if (observed == state)
                {
                    if (state - 1 == Disposed)
                    {
                        Release();
                    }

                    return;
                }

                state = observed;
            }
        }

        protected override void Dispose(bool disposing)
        {
            int previous = Interlocked.Or(ref _state, Disposed);
            if ((previous & Disposed) != 0)
            {
                return;
            }

            Volatile.Write(ref _pointer, 0);
            if (previous == 0)
            {
                Release();
            }
        }

        // Reached once: by the disposal that saw no pins, or by the unpin that ended the last one.
        private void Release() => NativeMemoryPool.Return(new CachedBlock(_storage, _epoch), _length);

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        private static void ThrowDisposed() =>
            throw new ObjectDisposedException(nameof(NativeMemoryPool), "The buffer was returned to the pool.");
    }
}
