using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

internal sealed class NativeArenaTransferBatch<T>
    where T : unmanaged
{
    private readonly NativeArenaTransferBatchState _state;

    internal NativeArenaTransferBatch(
        NativeArenaTransferBatchState state)
    {
        _state = state;
    }

    internal int Count => _state.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeArenaTransferBatchInitialization<T>
        BeginInitialization(int slotIndex)
    {
        if (!_state.Owner.TryBeginArenaTransferBatchInitialization(
                _state,
                slotIndex,
                out NativeArenaTransferBatchReservation reservation))
        {
            reservation = _state.Owner
                .BeginArenaTransferBatchInitializationSlow<T>(
                    _state,
                    slotIndex);
        }

        return new NativeArenaTransferBatchInitialization<T>(
            reservation);
    }
}

internal ref struct NativeArenaTransferBatchInitialization<T>
    where T : unmanaged
{
    private NativeArenaTransferBatchState? _state;
    private readonly int _slotIndex;
    private readonly IntPtr _pointer;
    private readonly long _authority;
    private readonly int _length;

    internal NativeArenaTransferBatchInitialization(
        NativeArenaTransferBatchReservation reservation)
    {
        _state = reservation.State;
        _slotIndex = reservation.SlotIndex;
        _pointer = reservation.Pointer;
        _authority = reservation.Authority;
        _length = reservation.Length;
    }

    internal unsafe Span<T> Values
    {
        get
        {
            _ = _state
                ?? throw new ObjectDisposedException(
                    nameof(NativeArenaTransferBatchInitialization<T>));
            return new Span<T>((void*)_pointer, _length);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeArenaTransferBatchLease<T> Publish()
    {
        NativeArenaTransferBatchState state = _state
            ?? throw new ObjectDisposedException(
                nameof(NativeArenaTransferBatchInitialization<T>));
        NativeArenaTransferBatchReservation reservation =
            state.Owner.PublishArenaTransferBatchInitialization(
                state,
                _slotIndex,
                _authority,
                _pointer,
                _length);
        _state = null;
        return new NativeArenaTransferBatchLease<T>(reservation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeArenaTransferBatchPublication<T>
        PreparePublication()
    {
        NativeArenaTransferBatchState state = _state
            ?? throw new ObjectDisposedException(
                nameof(NativeArenaTransferBatchInitialization<T>));
        NativeArenaTransferBatchPublication<T> publication = new(
            state,
            _slotIndex,
            _pointer,
            _authority,
            _length);
        _state = null;
        return publication;
    }

    public void Dispose()
    {
        NativeArenaTransferBatchState? state = _state;
        if (state is null)
        {
            return;
        }

        state.Owner.AbortArenaTransferBatchInitialization(
            state,
            _slotIndex,
            _authority);
        _state = null;
    }
}

internal struct NativeArenaTransferBatchPublication<T>
    where T : unmanaged
{
    private NativeArenaTransferBatchState? _state;
    private readonly int _slotIndex;
    private readonly IntPtr _pointer;
    private readonly long _authority;
    private readonly int _length;

    internal NativeArenaTransferBatchPublication(
        NativeArenaTransferBatchState state,
        int slotIndex,
        IntPtr pointer,
        long authority,
        int length)
    {
        _state = state;
        _slotIndex = slotIndex;
        _pointer = pointer;
        _authority = authority;
        _length = length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeArenaTransferBatchLease<T> Publish()
    {
        NativeArenaTransferBatchState state = _state
            ?? throw new ObjectDisposedException(
                nameof(NativeArenaTransferBatchPublication<T>));
        NativeArenaTransferBatchReservation reservation =
            state.Owner.PublishArenaTransferBatchInitialization(
                state,
                _slotIndex,
                _authority,
                _pointer,
                _length);
        _state = null;
        return new NativeArenaTransferBatchLease<T>(reservation);
    }

    internal void Dispose()
    {
        NativeArenaTransferBatchState? state = _state;
        if (state is null)
        {
            return;
        }

        state.Owner.AbortArenaTransferBatchInitialization(
            state,
            _slotIndex,
            _authority);
        _state = null;
    }
}

internal struct NativeArenaTransferBatchLease<T>
    where T : unmanaged
{
    private NativeArenaTransferBatchState? _state;
    private readonly int _slotIndex;
    private readonly IntPtr _pointer;
    private readonly long _authority;
    private readonly int _length;

    internal NativeArenaTransferBatchLease(
        NativeArenaTransferBatchReservation reservation)
    {
        _state = reservation.State;
        _slotIndex = reservation.SlotIndex;
        _pointer = reservation.Pointer;
        _authority = reservation.Authority;
        _length = reservation.Length;
    }

    internal int Length => _length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static NativeArenaTransferBatchLease<T> Move(
        ref NativeArenaTransferBatchLease<T> source)
    {
        NativeArenaTransferBatchState state = source._state
            ?? throw new ObjectDisposedException(
                nameof(NativeArenaTransferBatchLease<T>));
        long nextAuthority =
            state.Owner.MoveArenaTransferBatchLease(
                state,
                source._slotIndex,
                source._authority,
                "NativeConcurrentArena.TransferBatch.Move");
        NativeArenaTransferBatchLease<T> result = new(
            new NativeArenaTransferBatchReservation(
                state,
                source._slotIndex,
                nextAuthority,
                source._pointer,
                source._length));
        source = default;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal TResult Read<TResult>(
        NativeLeaseFunc<T, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeArenaTransferBatchState state = _state
            ?? throw new ObjectDisposedException(
                nameof(NativeArenaTransferBatchLease<T>));
        NativeArenaTransferBatchOperationToken token =
            state.Owner.EnterArenaTransferBatchOperation(
            state,
            _slotIndex,
            _authority,
            "NativeConcurrentArena.TransferBatch.Read");
        try
        {
            return action(new NativeLeaseView<T>(
                _pointer,
                _length));
        }
        finally
        {
            token.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeArenaTransferBatchRead<T> EnterRead()
    {
        NativeArenaTransferBatchState state = _state
            ?? throw new ObjectDisposedException(
                nameof(NativeArenaTransferBatchLease<T>));
        NativeArenaTransferBatchOperationToken token =
            state.Owner.EnterArenaTransferBatchOperation(
                state,
                _slotIndex,
                _authority,
                "NativeConcurrentArena.TransferBatch.Read");
        return new NativeArenaTransferBatchRead<T>(
            token,
            _pointer,
            _length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Dispose()
    {
        NativeArenaTransferBatchState? state = _state;
        if (state is null)
        {
            return;
        }

        state.Owner.ReturnArenaTransferBatchLease(
            state,
            _slotIndex,
            _authority);
        _state = null;
    }
}

internal ref struct NativeArenaTransferBatchRead<T>
    where T : unmanaged
{
    private NativeArenaTransferBatchOperationToken _token;
    private readonly IntPtr _pointer;
    private readonly int _length;

    internal NativeArenaTransferBatchRead(
        NativeArenaTransferBatchOperationToken token,
        IntPtr pointer,
        int length)
    {
        _token = token;
        _pointer = pointer;
        _length = length;
    }

    internal unsafe ReadOnlySpan<T> Values =>
        new((void*)_pointer, _length);

    public void Dispose() => _token.Dispose();
}

internal ref struct NativeArenaTransferBatchOperationToken
{
    private NativeOwnerKernel? _kernel;
    private readonly NativeArenaTransferBatchState _state;
    private readonly int _slotIndex;

    internal NativeArenaTransferBatchOperationToken(
        NativeOwnerKernel kernel,
        NativeArenaTransferBatchState state,
        int slotIndex)
    {
        _kernel = kernel;
        _state = state;
        _slotIndex = slotIndex;
    }

    public void Dispose()
    {
        NativeOwnerKernel? kernel = _kernel;
        if (kernel is null)
        {
            return;
        }

        _kernel = null;
        kernel.ExitArenaTransferBatchOperation(
            _state,
            _slotIndex);
    }
}
