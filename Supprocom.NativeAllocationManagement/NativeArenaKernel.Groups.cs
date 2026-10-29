using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

internal sealed unsafe partial class NativeArenaKernel
{
    internal void BeginCompositeBorrow()
    {
        ValidateActive(nameof(NativeLeaseOperations.Access));
        _activeBorrowCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ValidateCompositeEpoch(ulong generation, ulong scopeEpoch, bool scoped, string operation)
    {
        // Construction-thread and active-owner proof is held by the group's one admission.
        if (generation != _generation || (scoped && scopeEpoch != _scopeEpoch))
        {
            ThrowStale(generation, scopeEpoch, scoped, operation);
        }
    }

    internal ArenaGroupCheckpoint BeginScopedGroup(long payloadBytes, bool requirePrepared = false)
    {
        ValidateActive(requirePrepared ? nameof(NativeLeaseOperations.TryInitializeScoped)
            : nameof(NativeLeaseOperations.InitializeScoped));
        if (requirePrepared)
        {
            EnsurePrepared();
        }
        if (_initializerActive != 0)
        {
            ThrowNestedInitializer();
        }
        _ = checked(_initializedPayloadBytes + payloadBytes);
        ArenaGroupCheckpoint checkpoint = new((IntPtr)_scoped.Current,
            (IntPtr)_scoped.Cursor, _scoped.UsedBytes);
        _initializerActive = 1;
        return checkpoint;
    }

    internal Span<T> ReserveScopedGroupRange<T>(int length) where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (_prepared)
        {
            if (!TryReserveScopedGroupRange(length, out Span<T> span))
            {
                throw new InvalidOperationException("The prepared scoped group exceeds the declared byte bound.");
            }
            return span;
        }
        ArenaReservation reservation = Reserve(ref _scoped, CalculateByteLength<T>(length), CalculateAlignment<T>(), scoped: true);
        return new Span<T>((void*)reservation.Pointer, length);
    }

    internal bool TryReserveScopedGroupRange<T>(int length, out Span<T> span) where T : unmanaged
    {
        if (!TryReservePrepared(ref _scoped, CalculateByteLength<T>(length),
            CalculateAlignment<T>(), scoped: true, out ArenaReservation reservation))
        {
            IncrementPreparedHistory(ref _preparedRefusalCount);
            span = default;
            return false;
        }
        _preparedPeakScopedUsedBytes = Math.Max(_preparedPeakScopedUsedBytes, checked((long)_scoped.UsedBytes));
        span = new Span<T>((void*)reservation.Pointer, length);
        return true;
    }

    internal ArenaLease<T> PublishScopedGroupRange<T>(Span<T> span) where T : unmanaged
    {
        if (_prepared)
        {
            IncrementPreparedHistory(ref _preparedSuccessCount);
        }
        fixed (T* pointer = span)
        {
            return new ArenaLease<T>(this, (IntPtr)pointer, span.Length,
                _generation, _scopeEpoch, scoped: true);
        }
    }

    internal void EndScopedGroup(ArenaGroupCheckpoint checkpoint, bool completed, long payloadBytes)
    {
        if (completed)
        {
            RecordInitialization(payloadBytes, scoped: true);
        }
        else
        {
            RollBack(new ArenaReservation(scoped: true, (ArenaSegmentHeader*)checkpoint.Segment,
                (byte*)checkpoint.Cursor, checkpoint.UsedBytes, checkpoint.Cursor));
        }
        _initializerActive = 0;
    }

    internal void RecordScopedGroupInitializerFailure()
    {
        if (_prepared)
        {
            IncrementPreparedHistory(ref _preparedInitializerFailureCount);
        }
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly record struct ArenaGroupCheckpoint(IntPtr Segment, IntPtr Cursor, nuint UsedBytes);
