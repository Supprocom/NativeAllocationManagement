using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

// The builder's only finalizable object. It guards the growing block while the
// builder writes, and Complete publishes it as the transfer control instead of
// acquiring a second finalizable control.
//
// Unbudgeted controls are reused: returning one (transfer Dispose, or builder
// Dispose before Complete) parks it with its block on a small per-thread
// stack, and the next unbudgeted builder or transfer of the same element type
// on that thread takes both. A full stack overflows into a few locked shared
// partitions, from which threads with an empty stack take, so values created
// on one thread and returned on another are reused too. Diagnostics count a
// parked block as returned and a reused one as a new allocation, the way
// malloc's own free lists are invisible to its callers.
internal sealed class NativeBuilderControl<T> : NativeTransferControl<T>
    where T : unmanaged
{
    private const int Building = 11;
    private const nuint ParkLimitBytes = 64 * 1024;
    private const nuint SmallReuseBytes = 1024;

    [ThreadStatic]
    private static ParkedControls? t_parked;

    private static readonly SharedPartition[] s_shared = SharedPartition.CreateAll();

    private NativeBlock _spare;
    private long _lifetimeFirstVersion = 1;
    private bool _parked;

    private NativeBuilderControl(NativeBlock block)
        : base(block, length: 0, capacity: 0, published: false) =>
        PublishControlState(Building);


    internal static NativeBuilderControl<T> Acquire(
        nuint byteLength,
        NativeMemoryBudget? budget,
        long ownerId,
        out NativeBlock block)
    {
        if (budget is null
            && !NativeMemoryTestHooks.FaultInjectionPending
            && (t_parked is { Count: > 0 } stack
                ? stack.TakeCurrent(byteLength)
                : SharedPartition.TryTake(s_shared, byteLength)) is NativeBuilderControl<T> parked)
        {
            block = parked.TakeSpare(byteLength, ownerId);
            parked.BeginLifetime(block, ownerId);
            return parked;
        }

        block = NativeBlockAllocator.Allocate(
            byteLength,
            nameof(NativeBuilder<T>),
            "NativeBuilder.Constructor",
            budget,
            ownerId: ownerId);
        try
        {
            return new NativeBuilderControl<T>(block);
        }
        catch
        {
            NativeBlockAllocator.Free(block);
            throw;
        }
    }

    // A direct transfer runs no user code before publication: the copy or fill
    // cannot fail, so the acquired control publishes without a builder.
    internal static unsafe NativeTransfer<T> CreateTransfer(scoped ReadOnlySpan<T> values)
    {
        NativeBuilderControl<T> control = Acquire(
            (nuint)values.Length * (nuint)Unsafe.SizeOf<T>(),
            budget: null,
            NativeOwnerIdentity.Next(),
            out NativeBlock block);
        Span<T> destination = new((void*)block.Pointer, values.Length);
        values.CopyTo(destination);
        NativeMemoryAccounting.RecordCopiedRange(values, destination);
        return control.Publish(block, values.Length, Capacity(block));
    }

    internal static unsafe NativeTransfer<T> CreateTransfer(int length, T value)
    {
        NativeBuilderControl<T> control = Acquire(
            checked((nuint)length * (nuint)Unsafe.SizeOf<T>()),
            budget: null,
            NativeOwnerIdentity.Next(),
            out NativeBlock block);
        new Span<T>((void*)block.Pointer, length).Fill(value);
        return control.Publish(block, length, Capacity(block));
    }

    internal static NativeTransfer<T> CreateUninitializedTransfer(int length)
    {
        NativeBuilderControl<T> control = Acquire(
            checked((nuint)length * (nuint)Unsafe.SizeOf<T>()),
            budget: null,
            NativeOwnerIdentity.Next(),
            out NativeBlock block);
        return control.Publish(block, length, Capacity(block));
    }

    private static int Capacity(NativeBlock block) =>
        (int)Math.Min(int.MaxValue, block.ByteLength / (nuint)Unsafe.SizeOf<T>());

    internal void InstallBlock(NativeBlock block) => InstallOwnedBlock(block);

    internal NativeTransfer<T> Publish(NativeBlock block, int length, int capacity)
    {
        InstallOwnedBlock(block);
        PublishShape(length, capacity);
        PublishControlState(Active);
        return NativeTransfer<T>.CreateAdmitted(this, CurrentAuthorityVersion);
    }

    // The builder ends before Complete: park its block, or free it exactly once.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "Builder disposal returns the unpublished block; a freed block disarms the emergency finalizer.")]
    internal void ReturnUnpublished(NativeBlock block)
    {
        InstallOwnedBlock(block);
        if (TryPark())
        {
            return;
        }

        try
        {
            NativeBlockAllocator.Free(block);
        }
        finally
        {
            CompleteStorageReturn();
            PublishControlState(Disposed);
            GC.SuppressFinalize(this);
        }
    }

    private protected override bool TryHandOffForReuse() => TryPark();

    internal override NativeTransferStatistics CaptureSnapshot(long bindingVersion)
    {
        long first = Volatile.Read(ref _lifetimeFirstVersion);
        long derivedFieldBytes = sizeof(long) + Unsafe.SizeOf<NativeBlock>() + sizeof(bool);
        if (bindingVersion < first)
        {
            // A value from an earlier lifetime of this reused control: that
            // lifetime ended by returning its storage.
            return new NativeTransferStatistics
            {
                OwnerId = Id,
                BindingVersion = bindingVersion,
                AuthorityVersion = CurrentAuthorityVersion,
                Lifecycle = NativeTransferLifecycle.Returned,
                PayloadReturnCount = 1,
                ControlFieldBytes = base.CaptureSnapshot(bindingVersion).ControlFieldBytes + derivedFieldBytes
            };
        }

        NativeTransferStatistics actual = base.CaptureSnapshot(bindingVersion);
        return actual with
        {
            MoveCount = actual.AuthorityVersion - first,
            ControlFieldBytes = actual.ControlFieldBytes + derivedFieldBytes
        };
    }

    private bool TryPark()
    {
        NativeBlock block = OwnedBlock;
        if (block.Budget is not null
            || block.ByteLength > ParkLimitBytes
            || IsSharing
            || NativeMemoryTestHooks.FaultInjectionPending)
        {
            return false;
        }

        ParkedControls stack = t_parked ??= new ParkedControls();
        if (stack.HasRoom(block.ByteLength))
        {
            BecomeParked(block);
            stack.Push(this, block.ByteLength);
            return true;
        }

        if (!SharedPartition.TryPark(s_shared, this, block))
        {
            return false;
        }

        AfterSharedHandOffForTest?.Invoke();
        return true;
    }

    // Runs after a control became visible to other threads and before the
    // returning call completes, so a test can reuse it inside that window.
    internal static Action? AfterSharedHandOffForTest { get; set; }

    // Everything the returning thread writes happens here, before the control
    // becomes visible in a reuse slot; it keeps its finalizer registration.
    private void BecomeParked(NativeBlock block)
    {
        NativeMemoryAccounting.RecordFree(block.ByteLength, detached: false, block.MetricsEpoch);
        _spare = block;
        CompleteStorageReturn();
        _parked = true;
        PublishControlState(Disposed);
    }

    // A stack from an older reuse epoch frees its blocks; the controls are
    // dropped and their finalizers then find nothing left to free.
    private unsafe void DiscardSpare()
    {
        if (_spare.Pointer != IntPtr.Zero)
        {
            NativeMemory.Free((void*)_spare.Pointer);
        }

        _spare = default;
    }

    // Reuse only a block that fits without pinning far more than requested.
    private static bool Fits(nuint spareBytes, nuint byteLength) =>
        spareBytes >= byteLength
        && spareBytes <= Math.Max(byteLength * 4, SmallReuseBytes);

    private unsafe NativeBlock TakeSpare(nuint byteLength, long ownerId)
    {
        NativeBlock spare = _spare;
        _spare = default;
        if (spare.Pointer != IntPtr.Zero && Fits(spare.ByteLength, byteLength))
        {
            long metricsEpoch = NativeMemoryAccounting.RecordAllocation(spare.ByteLength, zeroed: false);
            return new NativeBlock(spare.Pointer, spare.ByteLength, metricsEpoch, Budget: null, ownerId);
        }

        if (spare.Pointer != IntPtr.Zero)
        {
            NativeMemory.Free((void*)spare.Pointer);
        }

        return NativeBlockAllocator.Allocate(
            byteLength,
            nameof(NativeBuilder<T>),
            "NativeBuilder.Constructor",
            budget: null,
            ownerId: ownerId);
    }

    private void BeginLifetime(NativeBlock block, long ownerId)
    {
        long first = CurrentAuthorityVersion + 1;
        BeginReusedLifetime(block, ownerId, first);
        Volatile.Write(ref _lifetimeFirstVersion, first);
        _parked = false;
        PublishControlState(Building);
    }

    // Parked controls of one element type on one thread: at most eight, holding
    // at most ParkLimitBytes of spare blocks together.
    private sealed class ParkedControls
    {
        private const int Capacity = 8;
        private readonly NativeBuilderControl<T>?[] _items = new NativeBuilderControl<T>?[Capacity];
        private nuint _bytes;
        private int _epoch = NativeStorageReuse.CurrentEpoch;

        internal int Count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool HasRoom(nuint bytes)
        {
            if (_epoch != NativeStorageReuse.CurrentEpoch)
            {
                DiscardAll();
            }

            return (uint)Count < Capacity && _bytes + bytes <= ParkLimitBytes;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Push(NativeBuilderControl<T> control, nuint bytes)
        {
            _items[Count++] = control;
            _bytes += bytes;
        }

        // Takes the most recent control when its block fits; otherwise searches
        // the rest, and failing that reuses the most recent control with a new block.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal NativeBuilderControl<T>? TakeCurrent(nuint byteLength)
        {
            if (_epoch != NativeStorageReuse.CurrentEpoch)
            {
                DiscardAll();
                return null;
            }

            int top = Count - 1;
            NativeBuilderControl<T> control = _items[top]!;
            nuint bytes = control._spare.ByteLength;
            if (!Fits(bytes, byteLength))
            {
                return TakeSearched(byteLength);
            }

            _items[top] = null;
            Count = top;
            _bytes -= bytes;
            return control;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private NativeBuilderControl<T> TakeSearched(nuint byteLength)
        {
            int index = Count - 1;
            for (int candidate = index - 1; candidate >= 0; candidate--)
            {
                if (Fits(_items[candidate]!._spare.ByteLength, byteLength))
                {
                    index = candidate;
                    break;
                }
            }

            NativeBuilderControl<T> control = _items[index]!;
            _items[index] = _items[--Count];
            _items[Count] = null;
            _bytes -= control._spare.ByteLength;
            return control;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void DiscardAll()
        {
            foreach (ref readonly NativeBuilderControl<T>? control in _items.AsSpan(0, Count))
            {
                control!.DiscardSpare();
            }

            Array.Clear(_items);
            Count = 0;
            _bytes = 0;
            _epoch = NativeStorageReuse.CurrentEpoch;
        }
    }

    // Overflow shared between threads, one partition per processor up to eight,
    // each holding at most sixteen controls and 128 KiB of spare blocks. Empty or
    // full partitions are skipped without taking their lock.
    private sealed class SharedPartition
    {
        private const int Capacity = 16;
        private const nuint LimitBytes = 128 * 1024;
        private readonly Lock _gate = new();
        private readonly NativeBuilderControl<T>?[] _items = new NativeBuilderControl<T>?[Capacity];
        private nuint _bytes;
        private int _epoch = NativeStorageReuse.SharedEpoch;
        private int _count;

        internal static SharedPartition[] CreateAll()
        {
            SharedPartition[] partitions = new SharedPartition[Math.Clamp(Environment.ProcessorCount, 1, 8)];
            foreach (ref SharedPartition partition in partitions.AsSpan())
            {
                partition = new SharedPartition();
            }

            return partitions;
        }

        internal static NativeBuilderControl<T>? TryTake(SharedPartition[] partitions, nuint byteLength)
        {
            int start = Thread.GetCurrentProcessorId();
            for (int offset = 0; offset < partitions.Length; offset++)
            {
                SharedPartition partition = partitions[(start + offset) % partitions.Length];
                if (Volatile.Read(ref partition._count) != 0
                    && partition.TryTake(byteLength) is NativeBuilderControl<T> control)
                {
                    return control;
                }
            }

            return null;
        }

        internal static bool TryPark(SharedPartition[] partitions, NativeBuilderControl<T> control, NativeBlock block)
        {
            int start = Thread.GetCurrentProcessorId();
            for (int offset = 0; offset < partitions.Length; offset++)
            {
                SharedPartition partition = partitions[(start + offset) % partitions.Length];
                if (Volatile.Read(ref partition._count) != Capacity
                    && partition.TryPark(control, block))
                {
                    return true;
                }
            }

            return false;
        }

        private NativeBuilderControl<T>? TryTake(nuint byteLength)
        {
            lock (_gate)
            {
                DiscardIfStale();
                if (_count == 0)
                {
                    return null;
                }

                int index = _count - 1;
                for (int candidate = index; candidate >= 0; candidate--)
                {
                    if (Fits(_items[candidate]!._spare.ByteLength, byteLength))
                    {
                        index = candidate;
                        break;
                    }
                }

                NativeBuilderControl<T> control = _items[index]!;
                int last = _count - 1;
                _items[index] = _items[last];
                _items[last] = null;
                _bytes -= control._spare.ByteLength;
                Volatile.Write(ref _count, last);
                return control;
            }
        }

        private bool TryPark(NativeBuilderControl<T> control, NativeBlock block)
        {
            lock (_gate)
            {
                DiscardIfStale();
                if (_count == Capacity || _bytes + block.ByteLength > LimitBytes)
                {
                    return false;
                }

                control.BecomeParked(block);
                _items[_count] = control;
                _bytes += block.ByteLength;
                Volatile.Write(ref _count, _count + 1);
                return true;
            }
        }

        private void DiscardIfStale()
        {
            int epoch = NativeStorageReuse.SharedEpoch;
            if (_epoch == epoch)
            {
                return;
            }

            foreach (ref readonly NativeBuilderControl<T>? parked in _items.AsSpan(0, _count))
            {
                parked!.DiscardSpare();
            }

            Array.Clear(_items);
            _bytes = 0;
            _epoch = epoch;
            Volatile.Write(ref _count, 0);
        }
    }

    /// <summary>Returns an abandoned builder's unpublished block or a parked spare block.</summary>
    /// <remarks>The base finalizer then sees a returned control; published transfers keep its path.</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0055", Justification = "Emergency native-memory cleanup supplements mandatory deterministic disposal.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "An emergency finalizer must never let cleanup exceptions terminate the process.")]
    ~NativeBuilderControl()
    {
        if (_parked)
        {
            // Diagnostics already counted the parked block as returned.
            unsafe
            {
                if (_spare.Pointer != IntPtr.Zero)
                {
                    NativeMemory.Free((void*)_spare.Pointer);
                }
            }

            _spare = default;
            return;
        }

        if (!TryTransition(Building, Finalized))
        {
            return;
        }

        try
        {
            NativeBlockAllocator.Free(OwnedBlock);
        }
        catch
        {
        }

        CompleteStorageReturn();
        PublishControlState(Disposed);
    }
}
