using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

internal static unsafe class NativeBlockAllocator
{
    internal static NativeBlock Allocate<T>(
        int capacity,
        string ownerKind,
        string operation)
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

        if (NativeMemoryTestHooks.ConsumeForcedFailure())
        {
            throw CreateAllocationFailure(
                byteLength,
                ownerKind,
                operation);
        }

        void* memory = null;
        try
        {
            memory = NativeMemory.Alloc(byteLength);
            if (memory == null)
            {
                // NativeMemory.Alloc reports allocation failure with a null pointer.
#pragma warning disable CA2201
                throw new OutOfMemoryException();
#pragma warning restore CA2201
            }

            long metricsEpoch =
                NativeMemoryTestHooks.RecordAllocation(
                    byteLength,
                    zeroed: false);
            return new NativeBlock(
                (IntPtr)memory,
                byteLength,
                metricsEpoch);
        }
        catch (OutOfMemoryException exception)
        {
            if (memory != null)
            {
                NativeMemory.Free(memory);
            }

            throw CreateAllocationFailure(
                byteLength,
                ownerKind,
                operation,
                exception);
        }
        catch
        {
            if (memory != null)
            {
                NativeMemory.Free(memory);
            }

            throw;
        }
    }

    internal static NativeBlock Resize<T>(
        NativeBlock block,
        int capacity,
        string ownerKind,
        string operation)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        nuint byteLength = checked(
            (nuint)capacity * (nuint)Unsafe.SizeOf<T>());
        ValidateMetricsLength(byteLength);
        if (byteLength == 0)
        {
            Free(block);
            return default;
        }

        if (NativeMemoryTestHooks.ConsumeForcedFailure())
        {
            throw CreateAllocationFailure(
                byteLength,
                ownerKind,
                operation);
        }

        void* memory;
        try
        {
            memory = NativeMemory.Realloc(
                (void*)block.Pointer,
                byteLength);
            if (memory == null)
            {
                // NativeMemory.Realloc reports allocation failure with a null pointer.
#pragma warning disable CA2201
                throw new OutOfMemoryException();
#pragma warning restore CA2201
            }
        }
        catch (OutOfMemoryException exception)
        {
            throw CreateAllocationFailure(
                byteLength,
                ownerKind,
                operation,
                exception);
        }

        if (block.Pointer != IntPtr.Zero)
        {
            NativeMemoryTestHooks.RecordFree(
                block.ByteLength,
                detached: false,
                block.MetricsEpoch);
        }

        long metricsEpoch = NativeMemoryTestHooks.RecordAllocation(
            byteLength,
            zeroed: false);
        return new NativeBlock(
            (IntPtr)memory,
            byteLength,
            metricsEpoch);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "This boundary captures any failure to preserve cleanup and report the original error.")]
    internal static void Free(NativeBlock block)
    {
        if (block.Pointer == IntPtr.Zero)
        {
            return;
        }

        NativeMemory.Free((void*)block.Pointer);
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
    long MetricsEpoch);
