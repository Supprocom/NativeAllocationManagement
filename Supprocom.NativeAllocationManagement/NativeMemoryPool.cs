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

    private static readonly int s_cacheSlot = NativeThreadState.AllocateTypedSlot();

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
        if (!cache.TryTake(shift - MinClassShift, out NativeBlock block))
        {
            block = NativeBlockAllocator.Allocate((nuint)1 << shift, nameof(NativeMemoryPool), "NativeMemoryPool.Rent");
        }

        return Owner.Create(block);
    }

    // Storage returns to the calling thread's cache, whichever thread rented it.
    private static void Return(in NativeBlock block)
    {
        int index = BitOperations.Log2((uint)block.ByteLength) - MinClassShift;
        NativeThreadState thread = NativeThreadState.Current;
        ThreadCache cache = thread.GetTyped<ThreadCache>(s_cacheSlot) ?? CreateCache(thread);
        if (!cache.TryAdd(index, in block))
        {
            NativeBlockAllocator.Free(block);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ThreadCache CreateCache(NativeThreadState thread)
    {
        ThreadCache cache = new();
        thread.SetTyped(s_cacheSlot, cache);
        return cache;
    }

    // The shared instance is never disposed; cached storage belongs to threads, not to the pool.
    protected override void Dispose(bool disposing)
    {
    }

    // Blocks this thread keeps for its next rents, by size class. Larger classes keep fewer.
    private sealed class ThreadCache : INativeThreadStorage
    {
        private readonly NativeBlock[] _blocks = new NativeBlock[ClassCount * SlotsPerClass];
        private readonly int[] _counts = new int[ClassCount];

        internal bool TryTake(int index, out NativeBlock block)
        {
            int count = _counts[index];
            if (count == 0)
            {
                block = default;
                return false;
            }

            _counts[index] = --count;
            ref NativeBlock slot = ref _blocks[(index * SlotsPerClass) + count];
            block = slot;
            slot = default;
            return true;
        }

        internal bool TryAdd(int index, in NativeBlock block)
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
                while (TryTake(index, out NativeBlock block))
                {
                    NativeBlockAllocator.Free(block);
                }
            }
        }
    }

    private sealed unsafe class Owner : MemoryManager<byte>
    {
        private readonly NativeBlock _block;
        private readonly int _length;
        private nint _pointer;
        private int _pins;
        private int _disposed;
        private int _released;
        private Memory<byte> _memory;

        private Owner(in NativeBlock block)
        {
            _block = block;
            _length = (int)block.ByteLength;
            _pointer = block.Pointer;
        }

        internal static Owner Create(in NativeBlock block)
        {
            Owner owner = new(in block);
            owner._memory = owner.BaseMemory();
            return owner;
        }

        private Memory<byte> BaseMemory() => base.Memory;

        public override Memory<byte> Memory
        {
            get
            {
                if (Volatile.Read(ref _pointer) == 0)
                {
                    ThrowDisposed();
                }

                return _memory;
            }
        }

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

            // The increment is a full fence, as Dispose's exchange is: either Dispose sees this pin
            // and defers the return, or this sees the cleared pointer and backs out.
            Interlocked.Increment(ref _pins);
            nint pointer = Volatile.Read(ref _pointer);
            if (pointer == 0)
            {
                Unpin();
                ThrowDisposed();
            }

            return new MemoryHandle((byte*)pointer + elementIndex, default, this);
        }

        public override void Unpin()
        {
            // A copied handle disposed twice must not release another handle's pin.
            int pins = Volatile.Read(ref _pins);
            while (pins > 0)
            {
                int observed = Interlocked.CompareExchange(ref _pins, pins - 1, pins);
                if (observed == pins)
                {
                    if (pins == 1)
                    {
                        TryRelease();
                    }

                    return;
                }

                pins = observed;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _pointer, 0);
            TryRelease();
        }

        private void TryRelease()
        {
            if (Volatile.Read(ref _pins) == 0
                && Volatile.Read(ref _disposed) != 0
                && Interlocked.CompareExchange(ref _released, 1, 0) == 0)
            {
                NativeMemoryPool.Return(in _block);
            }
        }

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        private static void ThrowDisposed() =>
            throw new ObjectDisposedException(nameof(NativeMemoryPool), "The buffer was returned to the pool.");
    }
}
