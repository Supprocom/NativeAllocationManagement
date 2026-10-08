using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

internal static unsafe class NativeBlockAllocator
{
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

            memory = NativeMemory.Alloc(byteLength);
            if (memory == null)
            {
                // NativeMemory.Alloc reports allocation failure with a null pointer.
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
                    NativeMemory.Free(memory);
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

            void* memory = NativeMemory.Realloc(
                (void*)block.Pointer,
                byteLength);
            if (memory == null)
            {
                // NativeMemory.Realloc reports allocation failure with a null pointer.
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "This boundary captures any failure to preserve cleanup and report the original error.")]
    internal static void Free(NativeBlock block)
    {
        if (block.Pointer == IntPtr.Zero)
        {
            return;
        }

        NativeMemory.Free((void*)block.Pointer);
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
