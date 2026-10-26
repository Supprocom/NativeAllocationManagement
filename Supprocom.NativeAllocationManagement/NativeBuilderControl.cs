using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

// The builder's only finalizable object. It guards the growing block while the
// builder writes, and Complete publishes it as the transfer control instead of
// acquiring a second finalizable control.
//
// Unbudgeted controls are reused: returning one (transfer Dispose, or builder
// Dispose before Complete) parks it with its block in a per-thread slot, and
// the next unbudgeted builder of the same element type on that thread takes
// both. Diagnostics count a parked block as returned and a reused one as a new
// allocation, the way malloc's own free lists are invisible to its callers.
internal sealed class NativeBuilderControl<T> : NativeTransferControl<T>
    where T : unmanaged
{
    private const int Building = 11;
    private const nuint ParkLimitBytes = 64 * 1024;
    private const nuint SmallReuseBytes = 1024;

    [ThreadStatic]
    private static NativeBuilderControl<T>? t_parked;

    private NativeBlock _spare;
    private long _lifetimeFirstVersion = 1;
    private int _parkEpoch;
    private bool _parked;

    private NativeBuilderControl(NativeBlock block)
        : base(block, length: 0, capacity: 0, published: false) =>
        PublishControlState(Building);

    private protected override bool RetainsFinalizerAfterReturn => _parked;

    internal static NativeBuilderControl<T> Acquire(
        nuint byteLength,
        NativeMemoryBudget? budget,
        long ownerId,
        out NativeBlock block)
    {
        if (budget is null
            && t_parked is NativeBuilderControl<T> parked
            && !NativeMemoryTestHooks.FaultInjectionPending)
        {
            t_parked = null;
            if (parked._parkEpoch == NativeStorageReuse.CurrentEpoch)
            {
                block = parked.TakeSpare(byteLength, ownerId);
                parked.BeginLifetime(block, ownerId);
                return parked;
            }

            parked.DiscardSpare();
        }

        block = NativeBlockAllocator.Allocate(
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
            PublishControlState(Disposed);
            return;
        }

        try
        {
            NativeBlockAllocator.Free(block);
        }
        finally
        {
            CompleteStorageReturn();
            PublishControlState(Disposed);
            GC.SuppressFinalize(this);
        }
    }

    private protected override void ReturnStorage(string operation)
    {
        if (string.Equals(operation, "NativeTransfer.Dispose", StringComparison.Ordinal) && TryPark())
        {
            return;
        }

        base.ReturnStorage(operation);
    }

    internal override NativeTransferStatistics CaptureSnapshot(long bindingVersion)
    {
        long first = Volatile.Read(ref _lifetimeFirstVersion);
        long derivedFieldBytes = sizeof(long) + Unsafe.SizeOf<NativeBlock>() + sizeof(int) + sizeof(bool);
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

    private bool TryPark()
    {
        NativeBlock block = OwnedBlock;
        if (t_parked is not null
            || block.Budget is not null
            || block.ByteLength > ParkLimitBytes
            || IsSharing
            || NativeMemoryTestHooks.FaultInjectionPending)
        {
            return false;
        }

        NativeMemoryAccounting.RecordFree(block.ByteLength, detached: false, block.MetricsEpoch);
        _spare = block;
        CompleteStorageReturn();
        _parked = true;
        _parkEpoch = NativeStorageReuse.CurrentEpoch;
        t_parked = this;
        return true;
    }

    // A slot from an older reuse epoch frees its block; the control is dropped
    // and its finalizer then finds nothing left to free.
    private unsafe void DiscardSpare()
    {
        if (_spare.Pointer != IntPtr.Zero)
        {
            NativeMemory.Free((void*)_spare.Pointer);
        }

        _spare = default;
    }

    private unsafe NativeBlock TakeSpare(nuint byteLength, long ownerId)
    {
        NativeBlock spare = _spare;
        _spare = default;
        // Reuse only a block that fits without pinning far more than requested.
        if (spare.Pointer != IntPtr.Zero
            && spare.ByteLength >= byteLength
            && spare.ByteLength <= Math.Max(byteLength * 4, SmallReuseBytes))
        {
            long metricsEpoch = NativeMemoryAccounting.RecordAllocation(spare.ByteLength, zeroed: false);
            return new NativeBlock(spare.Pointer, spare.ByteLength, metricsEpoch, Budget: null, ownerId);
        }

        if (spare.Pointer != IntPtr.Zero)
        {
            NativeMemory.Free((void*)spare.Pointer);
        }

        return NativeBlockAllocator.Allocate(
            byteLength,
            nameof(NativeBuilder<T>),
            "NativeBuilder.Constructor",
            budget: null,
            ownerId: ownerId);
    }

    private void BeginLifetime(NativeBlock block, long ownerId)
    {
        long first = CurrentAuthorityVersion + 1;
        BeginReusedLifetime(block, ownerId, first);
        Volatile.Write(ref _lifetimeFirstVersion, first);
        _parked = false;
        PublishControlState(Building);
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
                    NativeMemory.Free((void*)_spare.Pointer);
                }
            }

            _spare = default;
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
