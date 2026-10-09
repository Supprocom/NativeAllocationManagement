using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

// Pointer/token records and the acquisition loop do not depend on element type.
// This is stateless cold preparation, not another owner or a hot dispatcher.
internal static unsafe class NativePreparedPoolStorage
{
    // Free links are -1 or nonnegative. These markers need no extra state field.
    internal const int Initializing = -2;
    internal const int Leased = -3;

    internal static void Acquire(NativePoolPreparation preparation, nuint stride,
        nuint totalBytes, NativeMemoryBudget? budget, long ownerId,
        out Slot[] slots, out Page[] pages, out NativeOwnerLifecycle lifecycle)
    {
        slots = [];
        pages = [];
        lifecycle = default;
        budget?.Reserve(totalBytes, ownerId);
        nuint remainingReservation = totalBytes;
        bool prepared = false;
        int preparedSlotCount = 0;
        long ordinal = 0;
        try
        {
            NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativePreparedPool.Preparation", 1, "metadata banks");
            slots = new Slot[preparation.SlotCount];
            pages = new Page[1 + (preparation.SlotCount - 1) / preparation.SlotsPerPage];
            lifecycle = NativeOwnerLifecycle.Active;
            foreach (ref Page page in pages.AsSpan())
            {
                ordinal = checked(ordinal + 1);
                NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativePreparedPool.Preparation",
                    checked((int)ordinal + 1), "page acquisition");
                int firstSlot = preparedSlotCount;
                int count = Math.Min(preparation.SlotsPerPage, preparation.SlotCount - firstSlot);
                nuint bytes = checked(stride * (nuint)count);
                void* memory = null;
                bool committed = false;
                long epoch = 0;
                try
                {
                    if (NativeMemoryTestHooks.ConsumeForcedFailure())
                    {
                        throw new NativeAllocationFailedException(bytes, "NativePreparedPool", 0,
                            "page preparation", lifecycle);
                    }
                    memory = NativeMemory.Alloc(bytes);
                    if (memory == null)
                    {
                        throw new NativeAllocationFailedException(bytes, "NativePreparedPool", 0,
                            "page preparation", lifecycle);
                    }
                    epoch = NativeMemoryAccounting.RecordAllocation(bytes, zeroed: false);
                    budget?.Commit(bytes, ownerId, NativeMemoryTraceKind.PageAcquired, ordinal);
                    remainingReservation -= bytes;
                    committed = true;
                    page = new Page((IntPtr)memory, bytes, firstSlot, count, epoch, ordinal);
                    // The checked page extent already proves every slot offset.
                    // Advance within that extent instead of multiplying per slot.
                    byte* cursor = (byte*)memory;
                    for (int offset = 0; offset < count; offset++)
                    {
                        int index = preparedSlotCount++;
                        slots[index] = new Slot
                        {
                            Pointer = (IntPtr)cursor,
                            Next = index - 1
                        };
                        cursor += stride;
                    }
                }
                finally
                {
                    if (!committed && memory != null)
                    {
                        NativeMemory.Free(memory);
                        NativeMemoryAccounting.RecordFree(bytes, detached: false, epoch);
                    }
                }
            }
            NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativePreparedPool.Preparation",
                pages.Length + 2, "prepared authority");
            budget?.RecordPreparation(totalBytes, ownerId);
            prepared = true;
        }
        catch (OutOfMemoryException exception)
        {
            throw new NativeAllocationFailedException(totalBytes, "NativePreparedPool", 0,
                "page preparation", lifecycle, exception);
        }
        finally
        {
            if (remainingReservation != 0)
            {
                budget?.Cancel(remainingReservation, ownerId);
            }
            if (!prepared)
            {
                Array.Clear(slots);
                foreach (ref Page page in pages.AsSpan())
                {
                    if (page.AllocationBytes == 0)
                    {
                        continue;
                    }
                    NativeMemory.Free((void*)page.Pointer);
                    budget?.Release(page.AllocationBytes, ownerId,
                        NativeMemoryTraceKind.Released, page.Ordinal);
                    NativeMemoryAccounting.RecordFree(page.AllocationBytes,
                        detached: false, page.MetricsEpoch);
                    page = default;
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Slot
    {
        internal IntPtr Pointer;
        internal long Token;
        internal int Next;
        internal int BorrowCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Page
    {
        internal Page(IntPtr pointer, nuint allocationBytes, int firstSlot,
            int slotCount, long metricsEpoch, long ordinal)
        {
            Pointer = pointer;
            AllocationBytes = allocationBytes;
            FirstSlot = firstSlot;
            SlotCount = slotCount;
            MetricsEpoch = metricsEpoch;
            Ordinal = ordinal;
        }

        internal readonly IntPtr Pointer;
        internal readonly nuint AllocationBytes;
        internal readonly int FirstSlot;
        internal readonly int SlotCount;
        internal readonly long MetricsEpoch;
        internal readonly long Ordinal;
    }

}
