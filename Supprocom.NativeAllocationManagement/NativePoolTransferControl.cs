namespace Supprocom.NativeAllocationManagement;

// The control of a transferable lease from a synchronized pool. Disposal
// returns the lease to its pool and parks the control on the disposing thread;
// that thread's next transferable rent of the same element type takes it
// instead of allocating another finalizable control. A parked control holds no
// storage, kernel or budget, and keeps its finalizer registration, which then
// finds nothing to return. Values from an earlier lifetime are stale: their
// authority version is below the lifetime's first.
internal sealed class NativePoolTransferControl<T> : NativeTransferControl<T>
    where T : unmanaged
{
    private const int ParkCapacity = 4;
    private static readonly int s_parkedSlot = NativeThreadState.AllocateTypedSlot();

    private long _lifetimeFirstVersion = 1;

    private NativePoolTransferControl(
        NativeOwnerKernel kernel,
        NativeAllocation allocationState,
        long generation,
        long allocationId)
        : base(kernel, allocationState, generation, allocationId)
    {
    }

    internal static NativeTransfer<T> Publish(
        NativeOwnerKernel kernel,
        NativePoolLease lease,
        string operation)
    {
        ParkedControls? parked = NativeThreadState.Current.GetTyped<ParkedControls>(s_parkedSlot);
        if (parked is { Count: > 0 })
        {
            NativePoolTransferControl<T> reused = parked.Pop();
            long first = reused.CurrentAuthorityVersion + 1;
            Volatile.Write(ref reused._lifetimeFirstVersion, first);
            reused.BeginKernelLifetime(kernel, lease.AllocationState, lease.Generation, lease.AllocationId, first);
            return NativeTransfer<T>.CreateAdmitted(reused, first);
        }

        NativePoolTransferControl<T> control;
        try
        {
            control = new NativePoolTransferControl<T>(kernel, lease.AllocationState, lease.Generation, lease.AllocationId);
        }
        catch
        {
            kernel.ReturnLease(lease.Generation, lease.AllocationId, operation);
            throw;
        }

        return NativeTransfer<T>.CreateAdmitted(control, 1);
    }

    // Returns the lease, publishes Disposed and parks the control. A failed
    // return leaves the caller's disposal to restore or retire the control.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "A control that cannot park disarms its acquisition-time finalizer after a successful return.")]
    private protected override bool TryHandOffForReuse()
    {
        if (IsSharing || NativeMemoryTestHooks.FaultInjectionPending)
        {
            return false;
        }

        ReturnStorage("NativeTransfer.Dispose");
        PublishControlState(Disposed);
        NativeThreadState thread = NativeThreadState.Current;
        ParkedControls parked = thread.GetTyped<ParkedControls>(s_parkedSlot) ?? CreateParkedControls(thread);
        if (parked.Count < ParkCapacity)
        {
            parked.Push(this);
        }
        else
        {
            GC.SuppressFinalize(this);
        }

        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static ParkedControls CreateParkedControls(NativeThreadState thread)
    {
        ParkedControls parked = new();
        thread.SetTyped(s_parkedSlot, parked);
        return parked;
    }

    private sealed class ParkedControls
    {
        private readonly NativePoolTransferControl<T>?[] _items = new NativePoolTransferControl<T>?[ParkCapacity];

        internal int Count;

        internal void Push(NativePoolTransferControl<T> control) => _items[Count++] = control;

        internal NativePoolTransferControl<T> Pop()
        {
            NativePoolTransferControl<T> control = _items[--Count]!;
            _items[Count] = null;
            return control;
        }
    }

    internal override NativeTransferStatistics CaptureSnapshot(long bindingVersion)
    {
        long first = Volatile.Read(ref _lifetimeFirstVersion);
        if (bindingVersion < first)
        {
            // A value from an earlier lifetime of this reused control: that
            // lifetime ended by returning its lease.
            return new NativeTransferStatistics
            {
                OwnerId = Id,
                BindingVersion = bindingVersion,
                AuthorityVersion = CurrentAuthorityVersion,
                Lifecycle = NativeTransferLifecycle.Returned,
                PayloadReturnCount = 1,
                ControlFieldBytes = base.CaptureSnapshot(bindingVersion).ControlFieldBytes + sizeof(long)
            };
        }

        NativeTransferStatistics actual = base.CaptureSnapshot(bindingVersion);
        return actual with
        {
            MoveCount = actual.AuthorityVersion - first,
            ControlFieldBytes = actual.ControlFieldBytes + sizeof(long)
        };
    }
}
