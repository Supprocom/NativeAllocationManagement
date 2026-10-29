using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

internal static unsafe class NativeBlockAllocator
{
    // Cache-line alignment: malloc's 16-byte alignment splits a third of
    // 32-byte vector stores across cache lines, and filling a 16 KiB block
    // took 216-301 ns instead of 129 ns on the reference host. Every block
    // is allocated, resized and freed with the aligned calls.
    internal const nuint BlockAlignment = 64;

    // Callers own their checked typed extent. The physical acquisition and
    // rollback have one body, without a generic forwarding call in Tier0.
    internal static NativeBlock Allocate(
        nuint byteLength,
        string ownerKind,
        string operation,
        NativeMemoryBudget? budget = null,
        long ownerId = 0,
        bool alreadyReserved = false)
    {
        ValidateMetricsLength(byteLength);
        if (byteLength == 0)
        {
            return new NativeBlock(IntPtr.Zero, 0, 0, budget, ownerId);
        }

        if (ownerId == 0)
        {
            ownerId = NativeOwnerIdentity.Next();
        }
        if (!alreadyReserved) budget?.Reserve(byteLength, ownerId);
        void* memory = null;
        bool acquired = false;
        bool recorded = false;
        long metricsEpoch = 0;
        try
        {
            if (NativeMemoryTestHooks.ConsumeForcedFailure())
            {
                throw CreateAllocationFailure(byteLength, ownerKind, operation);
            }

            memory = NativeMemory.AlignedAlloc(byteLength, BlockAlignment);
            if (memory == null)
            {
                // NativeMemory.AlignedAlloc reports allocation failure with a null pointer.
#pragma warning disable CA2201
                throw new OutOfMemoryException();
#pragma warning restore CA2201
            }

            metricsEpoch = NativeMemoryAccounting.RecordAllocation(byteLength, zeroed: false);
            recorded = true;
            budget?.Commit(byteLength, ownerId, allocationOrdinal: 1);
            acquired = true;
            return new NativeBlock(
                (IntPtr)memory,
                byteLength,
                metricsEpoch,
                budget,
                ownerId);
        }
        catch (OutOfMemoryException exception)
        {
            throw CreateAllocationFailure(
                byteLength,
                ownerKind,
                operation,
                exception);
        }
        finally
        {
            if (!acquired)
            {
                if (memory != null)
                {
                    NativeMemory.AlignedFree(memory);
                    if (recorded)
                    {
                        NativeMemoryAccounting.RecordFree(byteLength, detached: false, metricsEpoch);
                    }
                }

                budget?.Cancel(byteLength, ownerId);
            }
        }
    }

    internal static bool TryResize<T>(
        NativeBlock block,
        int preferredCapacity,
        int minimumCapacity,
        string ownerKind,
        string operation,
        NativeMemoryBudget? budget,
        bool throwOnBudgetFailure,
        out NativeBlock replacement,
        out int capacity,
        long ownerId = 0)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumCapacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(preferredCapacity, minimumCapacity);
        nuint elementSize = (nuint)Unsafe.SizeOf<T>();
        nuint preferredBytes = checked((nuint)preferredCapacity * elementSize);
        nuint minimumBytes = checked((nuint)minimumCapacity * elementSize);
        ValidateMetricsLength(preferredBytes);
        budget ??= block.Budget;
        ownerId = block.OwnerId != 0 ? block.OwnerId : ownerId;
        if (ownerId == 0)
        {
            ownerId = NativeOwnerIdentity.Next();
        }
        nuint admittedBytes = preferredBytes;
        if (budget is not null
            && !budget.TryReservePreferred(preferredBytes, minimumBytes, out admittedBytes, out long availableBytes, ownerId))
        {
            if (throwOnBudgetFailure)
            {
                throw new NativeMemoryBudgetExceededException(
                    budget.Id, budget.CapacityBytes, minimumBytes, availableBytes);
            }

            replacement = default;
            capacity = 0;
            return false;
        }

        capacity = checked((int)(admittedBytes / elementSize));
        replacement = ResizeAdmitted(block, admittedBytes, ownerKind, operation, budget, ownerId);
        return true;
    }

    private static NativeBlock ResizeAdmitted(
        NativeBlock block,
        nuint byteLength,
        string ownerKind,
        string operation,
        NativeMemoryBudget? budget,
        long ownerId)
    {
        bool acquired = false;
        try
        {
            if (NativeMemoryTestHooks.ConsumeForcedFailure())
            {
                throw CreateAllocationFailure(byteLength, ownerKind, operation);
            }

            if (budget is null
                && (byteLength >= NativeLargeBlocks.MinimumBytes || block.ByteLength >= NativeLargeBlocks.MinimumBytes)
                && TryMoveThroughLargeBlocks(block, byteLength, ownerId, out NativeBlock moved))
            {
                acquired = true;
                return moved;
            }

            void* memory = NativeMemory.AlignedRealloc(
                (void*)block.Pointer,
                byteLength,
                BlockAlignment);
            if (memory == null)
            {
                // NativeMemory.AlignedRealloc reports allocation failure with a null pointer.
#pragma warning disable CA2201
                throw new OutOfMemoryException();
#pragma warning restore CA2201
            }
            long metricsEpoch = NativeMemoryAccounting.RecordReallocation(
                block.ByteLength, byteLength, block.MetricsEpoch);
            budget?.CommitReallocation(byteLength, block.ByteLength, ownerId, allocationOrdinal: 1);
            acquired = true;
            return new NativeBlock((IntPtr)memory, byteLength, metricsEpoch, budget, ownerId);
        }
        catch (OutOfMemoryException exception)
        {
            throw CreateAllocationFailure(byteLength, ownerKind, operation, exception);
        }
        finally
        {
            if (!acquired)
            {
                budget?.Cancel(byteLength, ownerId);
            }
        }
    }

    // A growing builder takes a kept block and keeps the one it leaves,
    // instead of reallocating: the aligned realloc allocates, copies and
    // frees anyway, and a builder grown per use would map every doubling
    // afresh. False when neither block goes through NativeLargeBlocks. A kept
    // block is already counted, so only a new one records an allocation.
    private static bool TryMoveThroughLargeBlocks(NativeBlock block, nuint byteLength, long ownerId, out NativeBlock moved)
    {
        nuint blockBytes = byteLength;
        void* memory = NativeLargeBlocks.TryTake(byteLength, out nuint keptBytes, out long metricsEpoch);
        if (memory != null)
        {
            blockBytes = keptBytes;
        }
        else if (block.ByteLength >= NativeLargeBlocks.MinimumBytes && !NativeMemoryTestHooks.FaultInjectionPending)
        {
            memory = NativeMemory.AlignedAlloc(byteLength, BlockAlignment);
            if (memory == null)
            {
#pragma warning disable CA2201
                throw new OutOfMemoryException();
#pragma warning restore CA2201
            }

            metricsEpoch = NativeMemoryAccounting.RecordAllocation(blockBytes, zeroed: false);
        }
        else
        {
            moved = default;
            return false;
        }

        Buffer.MemoryCopy((void*)block.Pointer, memory, blockBytes, Math.Min(block.ByteLength, byteLength));
        FreeOrKeep(block);
        moved = new NativeBlock((IntPtr)memory, blockBytes, metricsEpoch, Budget: null, ownerId);
        return true;
    }

    // An unbudgeted block kept by NativeLargeBlocks, at most twice the
    // length, or else a new one. A kept block is already counted.
    internal static NativeBlock AllocateReusable(
        nuint byteLength,
        string ownerKind,
        string operation,
        long ownerId)
    {
        void* kept = NativeLargeBlocks.TryTake(byteLength, out nuint keptBytes, out long metricsEpoch);
        if (kept == null)
        {
            return Allocate(byteLength, ownerKind, operation, budget: null, ownerId);
        }

        return new NativeBlock(
            (IntPtr)kept,
            keptBytes,
            metricsEpoch,
            Budget: null,
            ownerId == 0 ? NativeOwnerIdentity.Next() : ownerId);
    }

    // Frees the block, or hands an unbudgeted large one to NativeLargeBlocks,
    // where it stays counted.
    internal static void FreeOrKeep(NativeBlock block)
    {
        if (block.Budget is null
            && NativeLargeBlocks.TryKeep((void*)block.Pointer, block.ByteLength, block.MetricsEpoch))
        {
            return;
        }

        Free(block);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "This boundary captures any failure to preserve cleanup and report the original error.")]
    internal static void Free(NativeBlock block)
    {
        if (block.Pointer == IntPtr.Zero)
        {
            return;
        }

        NativeMemory.AlignedFree((void*)block.Pointer);
        block.Budget?.Release(block.ByteLength, block.OwnerId, allocationOrdinal: 1);
        try
        {
            NativeMemoryAccounting.RecordFree(
                block.ByteLength,
                detached: false,
                block.MetricsEpoch);
        }
        catch
        {
            // Diagnostics cannot restore storage after the physical free.
        }
    }

    private static NativeAllocationFailedException
        CreateAllocationFailure(
            nuint byteLength,
            string ownerKind,
            string operation,
            Exception? innerException = null) =>
        new(
            byteLength,
            ownerKind,
            generation: 0,
            operation,
            NativeOwnerLifecycle.Active,
            innerException);

    private static void ValidateMetricsLength(nuint byteLength)
    {
        if (byteLength > long.MaxValue)
        {
            throw new OverflowException(
                "The native block byte count exceeds the supported metrics range.");
        }
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly record struct NativeBlock(
    IntPtr Pointer,
    nuint ByteLength,
    long MetricsEpoch,
    NativeMemoryBudget? Budget = null,
    long OwnerId = 0)
{
    // An immutable extent is data, not an accessor boundary. Direct loads also
    // stay direct in Tier0 when control snapshots and return retain its history.
    internal readonly nuint ByteLength = ByteLength;
}

// A NativeBlock field of a heap object. Assigning the whole 40-byte block
// copies it with a write-barrier helper and `rep movsq` (about 17 ns on the
// reference host); Set stores the four numbers plainly and only the budget
// reference through a barrier.
internal struct NativeBlockSlot
{
    private IntPtr _pointer;
    private nuint _byteLength;
    private long _metricsEpoch;
    private long _ownerId;
    private NativeMemoryBudget? _budget;

    internal readonly IntPtr Pointer => _pointer;
    internal readonly nuint ByteLength => _byteLength;
    internal readonly long MetricsEpoch => _metricsEpoch;
    internal readonly long OwnerId => _ownerId;
    internal readonly NativeMemoryBudget? Budget => _budget;
    internal readonly NativeBlock Value => new(_pointer, _byteLength, _metricsEpoch, _budget, _ownerId);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void Set(in NativeBlock block)
    {
        _pointer = block.Pointer;
        _byteLength = block.ByteLength;
        _metricsEpoch = block.MetricsEpoch;
        _ownerId = block.OwnerId;
        if (block.Budget is not null || _budget is not null) _budget = block.Budget;
    }

    // An unbudgeted block: no reference store at all when the slot already has none.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void SetUnbudgeted(IntPtr pointer, nuint byteLength, long metricsEpoch, long ownerId)
    {
        _pointer = pointer;
        _byteLength = byteLength;
        _metricsEpoch = metricsEpoch;
        _ownerId = ownerId;
        if (_budget is not null) _budget = null;
    }

    internal void SetPointer(IntPtr pointer) => _pointer = pointer;

    // Keeps numeric identity and extent, drops the pointer and the budget.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void Detach()
    {
        _pointer = IntPtr.Zero;
        if (_budget is not null) _budget = null;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void Clear()
    {
        _pointer = IntPtr.Zero;
        _byteLength = 0;
        _metricsEpoch = 0;
        _ownerId = 0;
        if (_budget is not null) _budget = null;
    }
}

internal static class NativeAlignedAllocation
{
    // The Unix NativeMemory backend rounds size to the alignment before its
    // native call. Win32 passes size to _aligned_malloc unchanged. Neither
    // figure claims to measure opaque allocator headers or process RSS.
    internal static nuint GetBackingByteLength(nuint byteLength) =>
        byteLength == 0 || OperatingSystem.IsWindows()
            ? byteLength
            : checked(byteLength + NativeSegment.Alignment - 1)
                & ~(NativeSegment.Alignment - 1);
}
