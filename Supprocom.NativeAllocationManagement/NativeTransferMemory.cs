using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

// Memory<T> over a unique transfer. One manager per authority version is cached
// on the control, so repeated AsMemory calls allocate nothing and a Move costs
// one manager. Every span request re-checks that the control is active at the
// manager's version, so a Memory<T> taken before a move or disposal can never
// reach storage the old binding no longer owns.
//
// Pins count on the control, not on the manager: a pin outlives the binding
// that took it. Dispose with pins outstanding parks the control as Retiring,
// as a move over an active callback does, and the last unpin returns the storage.
internal partial class NativeTransferControl<T>
    where T : unmanaged
{
    private NativeTransferMemoryManager<T>? _memory;
    private int _pins;
    private NativeSharedMemoryManager<T>?[]? _sharedMemory;

    internal bool MemoryIsPinned => Volatile.Read(ref _pins) != 0;

    internal Memory<T> AsMemory(long authorityVersion)
    {
        EnsureActive(authorityVersion, "AsMemory");
        NativeTransferMemoryManager<T>? manager = Volatile.Read(ref _memory);
        if (manager is null || manager.Version != authorityVersion)
        {
            manager = NativeTransferMemoryManager<T>.Create(this, authorityVersion);
            Volatile.Write(ref _memory, manager);
            ArmMemory(manager, authorityVersion);
        }

        return manager.CachedMemory;
    }

    // A direct block's pointer and length cannot change while one authority version is
    // Active: the shape is published before Active, and a move, disposal or sharing
    // conversion ends the version (and clears the manager's pointer, see RetireMemory)
    // before the storage can go anywhere. A pooled lease can be invalidated by its pool,
    // and an empty block has no address, so those managers keep the validating path.
    private void ArmMemory(NativeTransferMemoryManager<T> manager, long authorityVersion)
    {
        if (_kernel is null && _length > 0)
        {
            manager.Arm(_block.Pointer);
            // Re-check after publishing the pointer: a disposal that ran meanwhile has already retired it.
            if (Volatile.Read(ref _state) != Active || Volatile.Read(ref _authorityVersion) != authorityVersion)
            {
                manager.Retire();
            }
        }
    }

    // Called once the binding's end is certain, before its storage can be returned or
    // handed on: a stale Memory<T> then takes the validating path and throws.
    private void RetireMemory() => Volatile.Read(ref _memory)?.Retire();

    internal unsafe Span<T> GetMemorySpan(long authorityVersion)
    {
        if (Volatile.Read(ref _state) != Active || Volatile.Read(ref _authorityVersion) != authorityVersion)
        {
            ThrowMemoryDisposed();
        }

        return MemoryExtent(out _);
    }

    // The storage this control holds, or an object-disposed failure when it has
    // been returned meanwhile (only a caller that races its own disposal sees it).
    private unsafe Span<T> MemoryExtent(out NativeAllocation? allocation)
    {
        NativeOwnerKernel? kernel = _kernel;
        allocation = null;
        if (kernel is null)
        {
            IntPtr pointer = _block.Pointer;
            if (pointer == IntPtr.Zero)
            {
                if (_length == 0 && Volatile.Read(ref _payloadReturned) == 0)
                {
                    return default;
                }

                ThrowMemoryDisposed();
            }

            return new Span<T>((void*)pointer, _length);
        }

        allocation = _allocationState;
        if (allocation is null || !kernel.TransferAuthorityIsActive(allocation.GenerationState, allocation, _block.MetricsEpoch, _block.OwnerId))
        {
            ThrowMemoryDisposed();
        }

        return allocation.AsSpan<T>();
    }

    internal unsafe void* PinMemory(long authorityVersion, int elementIndex)
    {
        if ((uint)elementIndex > (uint)_length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }

        // A pooled lease holds its generation for the pin, so the pool cannot
        // be disposed or rolled over under it; a direct block needs no more
        // than the count.
        NativeOwnerKernel? kernel = _kernel;
        NativeGeneration? generation = null;
        if (kernel is not null)
        {
            generation = _allocationState?.GenerationState;
            if (generation is null || !generation.TryEnterOperation())
            {
                ThrowMemoryDisposed();
            }
        }

        // The increment is a full fence, as a borrow's admission is: either
        // Dispose sees this pin, or this sees the state Dispose published.
        Interlocked.Increment(ref _pins);
        try
        {
            if (Volatile.Read(ref _state) != Active || Volatile.Read(ref _authorityVersion) != authorityVersion)
            {
                ThrowMemoryDisposed();
            }

            Span<T> extent = MemoryExtent(out _);
            return Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetReference(extent), elementIndex));
        }
        catch
        {
            ReleaseMemoryPin(kernel, generation);
            throw;
        }
    }

    internal void UnpinMemory()
    {
        NativeOwnerKernel? kernel = _kernel;
        NativeGeneration? generation = kernel is null ? null : _allocationState?.GenerationState;
        ReleaseMemoryPin(kernel, generation);
    }

    private void ReleaseMemoryPin(NativeOwnerKernel? kernel, NativeGeneration? generation)
    {
        int remaining = Interlocked.Decrement(ref _pins);
        if (generation is not null)
        {
            kernel!.ExitPinnedOperation(generation);
        }

        // Dispose, or a move that found a callback, parked the control while
        // pinned; the last pin returns the storage.
        if (remaining == 0 && Volatile.Read(ref _state) == Retiring)
        {
            CompleteRetirement();
        }
    }

    // Dispose found pins: the binding is over, the storage waits for the last
    // unpin. The exchange is a full fence, so a pin that leaves concurrently
    // either is seen by the count the completion reads or completes it itself.
    private void RetireForPins()
    {
        Interlocked.Exchange(ref _state, Retiring);
        CompleteRetirement();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal static void ThrowMemoryDisposed() =>
        throw new ObjectDisposedException(
            $"NativeTransfer<{typeof(T).Name}>",
            "The Memory<T> belongs to a NativeTransfer that was disposed or moved.");

    internal ReadOnlyMemory<T> SharedMemory(NativeSharedControl<T> control, int slot, long version, int offset, int length)
    {
        NativeSharedMemoryManager<T>?[]? cache = Volatile.Read(ref _sharedMemory);
        if (cache is null)
        {
            Interlocked.CompareExchange(ref _sharedMemory, new NativeSharedMemoryManager<T>?[control.StrongBindingCount], null);
            cache = _sharedMemory!;
        }

        if ((uint)slot >= (uint)cache.Length)
        {
            return NativeSharedMemoryManager<T>.Create(this, control, slot, version, offset, length).CachedMemory;
        }

        NativeSharedMemoryManager<T>? manager = Volatile.Read(ref cache[slot]);
        if (manager is null || !manager.Matches(control, version, offset, length))
        {
            manager = NativeSharedMemoryManager<T>.Create(this, control, slot, version, offset, length);
            Volatile.Write(ref cache[slot], manager);
        }

        return manager.CachedMemory;
    }

    internal unsafe Span<T> GetSharedSpan(int offset, int length)
    {
        IntPtr pointer = _block.Pointer;
        if (pointer == IntPtr.Zero)
        {
            if (length == 0 && Volatile.Read(ref _payloadReturned) == 0)
            {
                return default;
            }

            NativeSharedMemoryManager<T>.ThrowDisposed();
        }

        return new Span<T>((void*)pointer, checked(offset + length)).Slice(offset, length);
    }
}

internal sealed class NativeTransferMemoryManager<T> : MemoryManager<T>
    where T : unmanaged
{
    private readonly NativeTransferControl<T> _control;
    private int _pins;
    private Memory<T> _memory;
    // The storage address while the binding is live and its storage is a direct block; zero
    // otherwise. GetSpan is then one read and a check.
    private nint _pointer;
    private int _length;

    private NativeTransferMemoryManager(NativeTransferControl<T> control, long version)
    {
        _control = control;
        Version = version;
    }

    internal long Version { get; }

    internal Memory<T> CachedMemory => _memory;

    internal static NativeTransferMemoryManager<T> Create(NativeTransferControl<T> control, long version)
    {
        NativeTransferMemoryManager<T> manager = new(control, version);
        manager._memory = manager.Memory;
        return manager;
    }

    internal void Arm(nint pointer)
    {
        _length = _memory.Length;
        Volatile.Write(ref _pointer, pointer);
    }

    internal void Retire() => Volatile.Write(ref _pointer, 0);

    public override unsafe Span<T> GetSpan()
    {
        nint pointer = Volatile.Read(ref _pointer);
        return pointer != 0 ? new Span<T>((void*)pointer, _length) : _control.GetMemorySpan(Version);
    }

    public override unsafe MemoryHandle Pin(int elementIndex = 0)
    {
        void* pointer = _control.PinMemory(Version, elementIndex);
        Interlocked.Increment(ref _pins);
        return new MemoryHandle(pointer, default, this);
    }

    public override void Unpin()
    {
        // A handle disposes once; a second Unpin of a copied handle must not
        // release another handle's pin.
        int pins = Volatile.Read(ref _pins);
        while (pins > 0)
        {
            int observed = Interlocked.CompareExchange(ref _pins, pins - 1, pins);
            if (observed == pins)
            {
                _control.UnpinMemory();
                return;
            }

            pins = observed;
        }
    }

    // The memory is owned by the transfer; disposing the manager releases nothing.
    protected override void Dispose(bool disposing)
    {
    }
}

internal sealed class NativeSharedMemoryManager<T> : MemoryManager<T>
    where T : unmanaged
{
    private readonly NativeTransferControl<T> _payload;
    private readonly NativeSharedControl<T> _control;
    private readonly int _slot;
    private readonly long _version;
    private readonly int _offset;
    private readonly int _length;
    private int _pins;
    private ReadOnlyMemory<T> _memory;

    private NativeSharedMemoryManager(NativeTransferControl<T> payload, NativeSharedControl<T> control, int slot, long version, int offset, int length)
    {
        _payload = payload;
        _control = control;
        _slot = slot;
        _version = version;
        _offset = offset;
        _length = length;
    }

    internal ReadOnlyMemory<T> CachedMemory => _memory;

    internal bool Matches(NativeSharedControl<T> control, long version, int offset, int length) =>
        ReferenceEquals(_control, control) && _version == version && _offset == offset && _length == length;

    internal static NativeSharedMemoryManager<T> Create(NativeTransferControl<T> payload, NativeSharedControl<T> control, int slot, long version, int offset, int length)
    {
        NativeSharedMemoryManager<T> manager = new(payload, control, slot, version, offset, length);
        manager._memory = manager.Memory;
        return manager;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal static void ThrowDisposed() =>
        throw new ObjectDisposedException(
            $"NativeShared<{typeof(T).Name}>",
            "The ReadOnlyMemory<T> belongs to a NativeShared binding that was disposed or whose payload expired.");

    public override Span<T> GetSpan()
    {
        if (!_control.IsReadable(_slot, _version))
        {
            ThrowDisposed();
        }

        return _payload.GetSharedSpan(_offset, _length);
    }

    public override unsafe MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex > (uint)_length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }

        // A pin is an entered read: the payload returns when the last binding
        // and the last read have ended, so disposing the binding while pinned
        // defers the return.
        try
        {
            _control.EnterRead(_payload, _slot, _version);
        }
        catch (InvalidOperationException)
        {
            ThrowDisposed();
        }

        Interlocked.Increment(ref _pins);
        try
        {
            Span<T> span = _payload.GetSharedSpan(_offset, _length);
            return new MemoryHandle(Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), elementIndex)), default, this);
        }
        catch
        {
            Unpin();
            throw;
        }
    }

    public override void Unpin()
    {
        int pins = Volatile.Read(ref _pins);
        while (pins > 0)
        {
            int observed = Interlocked.CompareExchange(ref _pins, pins - 1, pins);
            if (observed == pins)
            {
                _control.ExitRead(_payload, _slot, _version);
                GC.KeepAlive(_payload);
                return;
            }

            pins = observed;
        }
    }

    protected override void Dispose(bool disposing)
    {
    }
}

// The part of a lease record's memory manager a return needs to know.
internal interface INativeLeaseMemory
{
    // True when the lease has pins: its return waits for the last unpin.
    bool TryDeferReturn(long allocationId);
}

// Memory<T> over a ConcurrentPooled<T> lease: bound to the lease's allocation
// id, so a record that serves a later lease never serves this Memory<T>.
internal sealed class NativeLeaseMemoryManager<T> : MemoryManager<T>, INativeLeaseMemory
{
    private readonly NativeOwnerKernel _kernel;
    private readonly NativeAllocation _allocation;
    private readonly NativeGeneration _generation;
    private readonly long _generationNumber;
    private readonly int _length;
    // The pin count in the low bits; the top bit is set once a dispose found pins, so the last unpin returns.
    private int _state;
    private const int Deferred = int.MinValue;
    private const int PinMask = int.MaxValue;
    private volatile bool _disposed;
    private Memory<T> _memory;
    // Set for a manager that is also a pool's IMemoryOwner: disposing it returns the lease, once.
    private bool _ownsLease;
    private int _returned;

    private NativeLeaseMemoryManager(NativeOwnerKernel kernel, NativeAllocation allocation, long allocationId, int length)
    {
        _kernel = kernel;
        _allocation = allocation;
        _generation = allocation.GenerationState;
        _generationNumber = _generation.Number;
        AllocationId = allocationId;
        _length = length;
    }

    internal long AllocationId { get; }

    internal Memory<T> CachedMemory => _memory;

    internal bool IsLive =>
        !_disposed && _kernel.TransferAuthorityIsActive(_generation, _allocation, _generationNumber, AllocationId);

    internal static NativeLeaseMemoryManager<T> Create(NativeOwnerKernel kernel, NativeAllocation allocation, long allocationId, int length)
    {
        NativeLeaseMemoryManager<T> manager = new(kernel, allocation, allocationId, length);
        manager._memory = manager.Memory;
        allocation.LeaseMemory = manager;
        return manager;
    }

    // Rents a transferable lease, so any thread can return it, and publishes a manager that owns it.
    internal static NativeLeaseMemoryManager<T> RentOwned(NativeOwnerKernel kernel, int length)
    {
        NativePoolLease lease = kernel.RentFilled(length, default(T)!, fill: false, transferable: true);
        NativeLeaseMemoryManager<T> manager = new(kernel, lease.AllocationState, lease.AllocationId, lease.AllocationState.Length);
        manager._memory = manager.Memory;
        manager._ownsLease = true;
        lease.AllocationState.LeaseMemory = manager;
        return manager;
    }

    // The return of an owned lease: after the last unpin when pinned, else at once; only the first call returns.
    private void ReturnOwned()
    {
        if (Interlocked.Exchange(ref _returned, 1) == 0)
        {
            _kernel.ReturnTransferLease(_allocation, _generationNumber, AllocationId, "NativeConcurrentPool.AsMemoryPool");
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowDisposed() =>
        throw new ObjectDisposedException(
            nameof(ConcurrentPooled<T>),
            "The Memory<T> belongs to a ConcurrentPooled lease that was disposed.");

    public override Span<T> GetSpan()
    {
        if (!IsLive)
        {
            ThrowDisposed();
        }

        return _allocation.AsSpan<T>();
    }

    public override unsafe MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex > (uint)_length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }

        // The pin count is taken first, so a dispose that runs meanwhile defers its return to the last unpin.
        int state = Volatile.Read(ref _state);
        while (true)
        {
            if (state < 0)
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

        // The generation's count keeps the pool from being disposed or
        // rolled over under a pin; the pin count makes the lease's own
        // return wait.
        if (!IsLive || !_generation.TryEnterOperation())
        {
            if (ReleasePin())
            {
                CompleteReturn();
            }

            ThrowDisposed();
        }

        try
        {
            Span<T> span = _allocation.AsSpan<T>();
            return new MemoryHandle(Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), elementIndex)), default, this);
        }
        catch
        {
            Unpin();
            throw;
        }
    }

    public override void Unpin()
    {
        if ((Volatile.Read(ref _state) & PinMask) == 0)
        {
            return;
        }

        bool completeReturn = ReleasePin();
        _kernel.ExitPinnedOperation(_generation);
        if (completeReturn)
        {
            CompleteReturn();
        }
    }

    // Drops one pin; true when it was the last of a disposed lease.
    private bool ReleasePin()
    {
        int state = Volatile.Read(ref _state);
        while (true)
        {
            int next = state - 1;
            int observed = Interlocked.CompareExchange(ref _state, next, state);
            if (observed == state)
            {
                return next == Deferred;
            }

            state = observed;
        }
    }

    private void CompleteReturn()
    {
        if (_ownsLease)
        {
            ReturnOwned();
        }
        else
        {
            _kernel.ReturnLease(_generationNumber, AllocationId, "ConcurrentPooled.Dispose");
        }
    }

    public bool TryDeferReturn(long allocationId)
    {
        if (allocationId != AllocationId)
        {
            return false;
        }

        int state = Volatile.Read(ref _state);
        while ((state & PinMask) != 0)
        {
            int observed = Interlocked.CompareExchange(ref _state, state | Deferred, state);
            if (observed == state)
            {
                _disposed = true;
                return true;
            }

            state = observed;
        }

        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsLease && !TryDeferReturn(AllocationId))
        {
            ReturnOwned();
        }
    }
}
