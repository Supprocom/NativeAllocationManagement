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

    internal ArenaGroupCheckpoint BeginScopedGroup()
    {
        ValidateActive(nameof(NativeLeaseOperations.InitializeScoped));
        if (_initializerActive != 0)
        {
            ThrowNestedInitializer();
        }
        ArenaGroupCheckpoint checkpoint = new((IntPtr)_scoped.Current,
            (IntPtr)_scoped.Cursor, _scoped.UsedBytes);
        _initializerActive = 1;
        return checkpoint;
    }

    internal Span<T> ReserveScopedGroupRange<T>(int length) where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        nuint bytes = CalculateByteLength<T>(length);
        ArenaReservation reservation;
        if (_prepared)
        {
            if (!TryReservePrepared(ref _scoped, bytes, CalculateAlignment<T>(), scoped: true, out reservation))
            {
                IncrementPreparedHistory(ref _preparedRefusalCount);
                throw new InvalidOperationException("The prepared scoped group exceeds the declared byte bound.");
            }
            _preparedPeakScopedUsedBytes = Math.Max(_preparedPeakScopedUsedBytes, checked((long)_scoped.UsedBytes));
        }
        else
        {
            reservation = Reserve(ref _scoped, bytes, CalculateAlignment<T>(), scoped: true);
        }
        return new Span<T>((void*)reservation.Pointer, length);
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

    internal void EndScopedGroup(ArenaGroupCheckpoint checkpoint, bool completed)
    {
        if (!completed)
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
