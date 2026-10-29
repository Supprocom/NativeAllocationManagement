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
    // A transfer's spare block up to this size parks with its control; a
    // larger one would be allocated afresh by every transfer that needs it.
    private const nuint ParkLimitBytes = 1024 * 1024;
    private const nuint SmallReuseBytes = 1024;

    private static readonly int s_parkedSlot = NativeThreadState.AllocateTypedSlot();

    private static readonly SharedPartition[] s_shared = SharedPartition.CreateAll();

    private NativeBlockSlot _spare;
    private long _lifetimeFirstVersion = 1;
    private bool _parked;

    private NativeBuilderControl(NativeBlock block)
        : base(block, length: 0, capacity: 0, published: false) =>
        PublishControlState(Building);


    // The acquired block is installed in the returned control: read it back
    // through Block or Pointer instead of copying it out through a parameter.
    internal static NativeBuilderControl<T> Acquire(
        nuint byteLength,
        NativeMemoryBudget? budget,
        long ownerId) =>
        Acquire(NativeThreadState.Current, byteLength, budget, ownerId);

    internal static NativeBuilderControl<T> Acquire(
        NativeThreadState thread,
        nuint byteLength,
        NativeMemoryBudget? budget,
        long ownerId)
    {
        if (budget is null
            && !NativeMemoryTestHooks.FaultInjectionPending
            && (thread.GetTyped<ParkedControls>(s_parkedSlot) is { Count: > 0 } stack
                ? stack.TakeCurrent(thread, byteLength)
                : TakeFromOtherThreads(thread, byteLength)) is NativeBuilderControl<T> parked)
        {
            parked.BeginLifetime(parked.TakeSpare(thread, byteLength, ownerId), ownerId);
            return parked;
        }

        return AcquireNew(byteLength, budget, ownerId);
    }

    // An empty stack takes a whole magazine of another thread's returns, then
    // the single-control overflow.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeBuilderControl<T>? TakeFromOtherThreads(NativeThreadState thread, nuint byteLength)
    {
        if (Depot.HasFull && !DepotDisabledForTest)
        {
            ParkedControls stack = thread.GetTyped<ParkedControls>(s_parkedSlot) ?? CreateParkedControls(thread);
            if (stack.Count == 0 && Depot.TryRefill(thread, stack))
            {
                return stack.TakeCurrent(thread, byteLength);
            }
        }

        return SharedPartition.TryTake(s_shared, byteLength);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeBuilderControl<T> AcquireNew(
        nuint byteLength,
        NativeMemoryBudget? budget,
        long ownerId)
    {
        NativeBlock block = budget is null
            ? NativeBlockAllocator.AllocateReusable(
                byteLength,
                nameof(NativeBuilder<T>),
                "NativeBuilder.Constructor",
                ownerId)
            : NativeBlockAllocator.Allocate(
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
        NativeThreadState thread = PrepareThread();
        NativeBuilderControl<T> control = Acquire(
            thread,
            (nuint)values.Length * (nuint)Unsafe.SizeOf<T>(),
            budget: null,
            NativeOwnerIdentity.Next(thread));
        Span<T> destination = new((void*)control.Pointer, values.Length);
        values.CopyTo(destination);
        NativeMemoryAccounting.RecordCopiedRange(values, destination);
        return control.PublishInstalled(values.Length);
    }

    internal static unsafe NativeTransfer<T> CreateTransfer(int length, T value)
    {
        NativeThreadState thread = PrepareThread();
        NativeBuilderControl<T> control = Acquire(
            thread,
            checked((nuint)length * (nuint)Unsafe.SizeOf<T>()),
            budget: null,
            NativeOwnerIdentity.Next(thread));
        new Span<T>((void*)control.Pointer, length).Fill(value);
        return control.PublishInstalled(length);
    }

    internal static NativeTransfer<T> CreateUninitializedTransfer(int length)
    {
        NativeThreadState thread = PrepareThread();
        NativeBuilderControl<T> control = Acquire(
            thread,
            checked((nuint)length * (nuint)Unsafe.SizeOf<T>()),
            budget: null,
            NativeOwnerIdentity.Next(thread));
        return control.PublishInstalled(length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NativeThreadState PrepareThread()
    {
        NativeMemoryAccounting.PrepareThread();
        return NativeThreadState.Current;
    }

    internal NativeBlock Block => OwnedBlock;

    internal IntPtr Pointer => OwnedPointer;

    // Publishes the block Acquire installed, filled by the caller.
    internal NativeTransfer<T> PublishInstalled(int length)
    {
        PublishShape(length, (int)Math.Min(int.MaxValue, OwnedByteLength / (nuint)Unsafe.SizeOf<T>()));
        PublishControlState(Active);
        return NativeTransfer<T>.CreateAdmitted(this, CurrentAuthorityVersion);
    }

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
            NativeBlockAllocator.FreeOrKeep(block);
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
        long derivedFieldBytes = sizeof(long) + Unsafe.SizeOf<NativeBlockSlot>() + sizeof(bool);
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

    private static bool CanPark(NativeBlock block) =>
        block.Budget is null
        && block.ByteLength <= ParkLimitBytes
        && !NativeMemoryTestHooks.FaultInjectionPending;

    // A shared payload returns by parking, as a disposed transfer does. Its
    // sharing control records the return first and stays with its bindings.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "A control that cannot park disarms its acquisition-time finalizer after its storage returned.")]
    private protected override bool TryParkSharedPayload()
    {
        if (!CanPark(OwnedBlock))
        {
            return false;
        }

        Control.RecordPayloadReturn(mayPark: true);
        EndSharing();
        if (!TryPark())
        {
            ReturnStorage("NativeTransfer.Dispose");
            PublishControlState(Disposed);
            GC.SuppressFinalize(this);
        }

        return true;
    }

    private bool TryPark()
    {
        NativeBlock block = OwnedBlock;
        if (!CanPark(block) || IsSharing)
        {
            return false;
        }

        NativeThreadState thread = NativeThreadState.Current;
        ParkedControls stack = thread.GetTyped<ParkedControls>(s_parkedSlot) ?? CreateParkedControls(thread);
        if (stack.HasRoom(thread, block.ByteLength)
            || (!DepotDisabledForTest && Depot.TrySpill(stack) && stack.HasRoom(thread, block.ByteLength)))
        {
            BecomeParked(thread, block);
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

    // Lets a test thread exercise the single-control overflow and per-thread
    // ceiling without the magazine depot.
    [ThreadStatic]
    internal static bool DepotDisabledForTest;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParkedControls CreateParkedControls(NativeThreadState thread)
    {
        ParkedControls stack = new(thread);
        thread.SetTyped(s_parkedSlot, stack);
        return stack;
    }

    private void BecomeParked(NativeBlock block) => BecomeParked(NativeThreadState.Current, block);

    // Everything the returning thread writes happens here, before the control
    // becomes visible in a reuse slot; it keeps its finalizer registration.
    private void BecomeParked(NativeThreadState thread, NativeBlock block)
    {
        NativeMemoryAccounting.RecordFree(thread, block.ByteLength, detached: false, block.MetricsEpoch);
        _spare.Set(block);
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
            NativeMemory.AlignedFree((void*)_spare.Pointer);
        }

        _spare.Clear();
    }

    internal bool HasSpareForTest => _spare.Pointer != IntPtr.Zero;

    // A parked spare already counts as freed.
    private unsafe void ReleaseSpareOfEndedThread()
    {
        if (_spare.Pointer != IntPtr.Zero
            && !NativeLargeBlocks.TryKeep((void*)_spare.Pointer, _spare.ByteLength))
        {
            NativeMemory.AlignedFree((void*)_spare.Pointer);
        }

        _spare.Clear();
    }

    // Reuse only a block that fits without pinning far more than requested.
    private static bool Fits(nuint spareBytes, nuint byteLength) =>
        spareBytes >= byteLength
        && spareBytes <= Math.Max(byteLength * 4, SmallReuseBytes);

    private unsafe NativeBlock TakeSpare(NativeThreadState thread, nuint byteLength, long ownerId)
    {
        NativeBlock spare = _spare.Value;
        _spare.Clear();
        if (spare.Pointer != IntPtr.Zero && Fits(spare.ByteLength, byteLength))
        {
            long metricsEpoch = NativeMemoryAccounting.RecordAllocation(thread, spare.ByteLength, zeroed: false);
            return new NativeBlock(spare.Pointer, spare.ByteLength, metricsEpoch, Budget: null, ownerId);
        }

        // A parked spare already counts as freed.
        if (spare.Pointer != IntPtr.Zero
            && !NativeLargeBlocks.TryKeep((void*)spare.Pointer, spare.ByteLength))
        {
            NativeMemory.AlignedFree((void*)spare.Pointer);
        }

        return NativeBlockAllocator.AllocateReusable(
            byteLength,
            nameof(NativeBuilder<T>),
            "NativeBuilder.Constructor",
            ownerId);
    }

    private void BeginLifetime(NativeBlock block, long ownerId)
    {
        long first = CurrentAuthorityVersion + 1;
        BeginReusedLifetime(block, ownerId, first);
        Volatile.Write(ref _lifetimeFirstVersion, first);
        _parked = false;
        PublishControlState(Building);
    }

    // Full thread stacks travel between threads whole: a thread whose stack is
    // full hands all of it to the depot as one magazine, and a thread whose
    // stack is empty takes one back, so a producer-consumer pipeline pays one
    // lock per stack of controls instead of one per control. Magazines already
    // hold parked controls, which published Disposed before they became visible.
    // Deep enough for a batch of thousands of transfers outstanding at once:
    // with 32 magazines, transfers past about 264 outstanding acquired a new
    // control and block each (16 ints: 51 ns per transfer with 256
    // outstanding, 255 ns with 1,024, 335 ns with 4,096). The bytes bound
    // still caps the blocks the depot keeps.
    private static class Depot
    {
        private const int MagazineLimit = 512;
        private const nuint LimitBytes = 512 * 1024;
        private static readonly Lock s_gate = new();
        private static readonly Magazine?[] s_full = new Magazine?[MagazineLimit];
        private static readonly Magazine?[] s_empty = new Magazine?[MagazineLimit];
        private static int s_fullCount;
        private static int s_emptyCount;
        private static nuint s_bytes;
        private static int s_epoch = NativeStorageReuse.SharedEpoch;

        internal static bool HasFull => Volatile.Read(ref s_fullCount) != 0;

        internal static bool TrySpill(ParkedControls stack)
        {
            if (stack.Count == 0 || Volatile.Read(ref s_fullCount) == MagazineLimit)
            {
                return false;
            }

            lock (s_gate)
            {
                DiscardIfStale();
                if (s_fullCount == MagazineLimit || s_bytes + stack.Bytes > LimitBytes)
                {
                    return false;
                }

                Magazine magazine = s_emptyCount != 0 ? s_empty[--s_emptyCount]! : new Magazine();
                s_empty[s_emptyCount] = null;
                s_bytes += stack.MoveTo(magazine);
                s_full[s_fullCount] = magazine;
                Volatile.Write(ref s_fullCount, s_fullCount + 1);
                return true;
            }
        }

        internal static bool TryRefill(NativeThreadState thread, ParkedControls stack)
        {
            lock (s_gate)
            {
                DiscardIfStale();
                if (s_fullCount == 0)
                {
                    return false;
                }

                int last = s_fullCount - 1;
                Magazine magazine = s_full[last]!;
                s_full[last] = null;
                Volatile.Write(ref s_fullCount, last);
                s_bytes -= magazine.Bytes;
                stack.TakeFrom(thread, magazine);
                if (s_emptyCount != MagazineLimit)
                {
                    s_empty[s_emptyCount++] = magazine;
                }

                return true;
            }
        }

        private static void DiscardIfStale()
        {
            int epoch = NativeStorageReuse.SharedEpoch;
            if (s_epoch == epoch)
            {
                return;
            }

            foreach (ref Magazine? magazine in s_full.AsSpan(0, s_fullCount))
            {
                magazine!.Discard();
                magazine = null;
            }

            s_bytes = 0;
            s_epoch = epoch;
            Volatile.Write(ref s_fullCount, 0);
        }
    }

    private sealed class Magazine
    {
        internal readonly NativeBuilderControl<T>?[] Items = new NativeBuilderControl<T>?[ParkedControls.Capacity];
        internal int Count;
        internal nuint Bytes;

        internal void Discard()
        {
            foreach (ref NativeBuilderControl<T>? control in Items.AsSpan(0, Count))
            {
                control!.DiscardSpare();
                control = null;
            }

            Count = 0;
            Bytes = 0;
        }
    }

    // Parked controls of one element type on one thread: at most eight, holding
    // at most ParkLimitBytes of spare blocks together.
    private sealed class ParkedControls : INativeThreadStorage
    {
        internal const int Capacity = 8;
        private readonly NativeBuilderControl<T>?[] _items = new NativeBuilderControl<T>?[Capacity];
        private nuint _bytes;
        private int _epoch;

        internal int Count;

        internal nuint Bytes => _bytes;

        // Empties this stack into an empty magazine; returns the bytes moved.
        // Called after HasRoom, which already discarded a stack from an older epoch.
        internal nuint MoveTo(Magazine magazine)
        {
            Array.Copy(_items, magazine.Items, Count);
            Array.Clear(_items, 0, Count);
            magazine.Count = Count;
            magazine.Bytes = _bytes;
            nuint moved = _bytes;
            Count = 0;
            _bytes = 0;
            return moved;
        }

        // Fills this empty stack from a magazine and leaves the magazine empty.
        internal void TakeFrom(NativeThreadState thread, Magazine magazine)
        {
            _epoch = thread.ReuseEpoch;
            Array.Copy(magazine.Items, _items, magazine.Count);
            Array.Clear(magazine.Items, 0, magazine.Count);
            Count = magazine.Count;
            _bytes = magazine.Bytes;
            magazine.Count = 0;
            magazine.Bytes = 0;
        }

        internal ParkedControls(NativeThreadState thread) => _epoch = thread.ReuseEpoch;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool HasRoom(NativeThreadState thread, nuint bytes)
        {
            if (_epoch != thread.ReuseEpoch)
            {
                DiscardAll(thread);
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
        // the rest. Failing that, a stack with room keeps its controls for the
        // sizes they hold and lets the caller acquire a new one, so a thread
        // that cycles through a few sizes ends with a block for each; a full
        // stack reuses its most recent control with a new block.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal NativeBuilderControl<T>? TakeCurrent(NativeThreadState thread, nuint byteLength)
        {
            if (_epoch != thread.ReuseEpoch)
            {
                DiscardAll(thread);
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
        private NativeBuilderControl<T>? TakeSearched(nuint byteLength)
        {
            int index = -1;
            for (int candidate = Count - 2; candidate >= 0; candidate--)
            {
                if (Fits(_items[candidate]!._spare.ByteLength, byteLength))
                {
                    index = candidate;
                    break;
                }
            }

            if (index < 0)
            {
                if (Count < Capacity)
                {
                    return null;
                }

                index = Count - 1;
            }

            NativeBuilderControl<T> control = _items[index]!;
            _items[index] = _items[--Count];
            _items[Count] = null;
            _bytes -= control._spare.ByteLength;
            return control;
        }

        // Large spares go to NativeLargeBlocks, the rest to native memory; the
        // controls are dropped.
        public void ReleaseStorageOfEndedThread()
        {
            foreach (ref readonly NativeBuilderControl<T>? control in _items.AsSpan(0, Count))
            {
                control!.ReleaseSpareOfEndedThread();
            }

            Array.Clear(_items);
            Count = 0;
            _bytes = 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void DiscardAll(NativeThreadState thread)
        {
            foreach (ref readonly NativeBuilderControl<T>? control in _items.AsSpan(0, Count))
            {
                control!.DiscardSpare();
            }

            Array.Clear(_items);
            Count = 0;
            _bytes = 0;
            _epoch = thread.ReuseEpoch;
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
                    NativeMemory.AlignedFree((void*)_spare.Pointer);
                }
            }

            _spare.Clear();
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
