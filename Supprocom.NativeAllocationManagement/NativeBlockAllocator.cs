using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

internal static unsafe class NativeBlockAllocator
{
    internal static NativeBlock Allocate<T>(
        int capacity,
        string ownerKind,
        string operation,
        NativeMemoryBudget? budget = null)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        nuint byteLength = checked(
            (nuint)capacity * (nuint)Unsafe.SizeOf<T>());
        ValidateMetricsLength(byteLength);
        if (byteLength == 0)
        {
            return default;
        }

        budget?.Reserve(byteLength);
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

            metricsEpoch = NativeMemoryTestHooks.RecordAllocation(byteLength, zeroed: false);
            recorded = true;
            budget?.Commit(byteLength);
            acquired = true;
            return new NativeBlock(
                (IntPtr)memory,
                byteLength,
                metricsEpoch,
                budget);
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
                        NativeMemoryTestHooks.RecordFree(byteLength, detached: false, metricsEpoch);
                    }
                }

                budget?.Cancel(byteLength);
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
        out int capacity)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumCapacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(preferredCapacity, minimumCapacity);
        nuint elementSize = (nuint)Unsafe.SizeOf<T>();
        nuint preferredBytes = checked((nuint)preferredCapacity * elementSize);
        nuint minimumBytes = checked((nuint)minimumCapacity * elementSize);
        ValidateMetricsLength(preferredBytes);
        budget ??= block.Budget;
        nuint admittedBytes = preferredBytes;
        if (budget is not null
            && !budget.TryReservePreferred(preferredBytes, minimumBytes, out admittedBytes, out long availableBytes))
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
        replacement = ResizeAdmitted(block, admittedBytes, ownerKind, operation, budget);
        return true;
    }

    private static NativeBlock ResizeAdmitted(
        NativeBlock block,
        nuint byteLength,
        string ownerKind,
        string operation,
        NativeMemoryBudget? budget)
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
            long metricsEpoch = NativeMemoryTestHooks.RecordReallocation(
                block.ByteLength, byteLength, block.MetricsEpoch);
            budget?.CommitReallocation(byteLength, block.ByteLength);
            acquired = true;
            return new NativeBlock((IntPtr)memory, byteLength, metricsEpoch, budget);
        }
        catch (OutOfMemoryException exception)
        {
            throw CreateAllocationFailure(byteLength, ownerKind, operation, exception);
        }
        finally
        {
            if (!acquired)
            {
                budget?.Cancel(byteLength);
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
        block.Budget?.Release(block.ByteLength);
        try
        {
            NativeMemoryTestHooks.RecordFree(
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
    NativeMemoryBudget? Budget = null);

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
