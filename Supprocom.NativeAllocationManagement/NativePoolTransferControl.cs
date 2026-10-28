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

    // A full thread stack overflows into a few shared partitions, from which a
    // thread with an empty stack takes: in a pipeline the consuming thread
    // returns every control and the producing thread rents every lease.
    private static readonly SharedPartition[] s_shared = SharedPartition.CreateAll();

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
        if ((parked is { Count: > 0 } ? parked.Pop() : SharedPartition.TryTake(s_shared)) is NativePoolTransferControl<T> reused)
        {
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
        else if (!SharedPartition.TryPark(s_shared, this))
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

    // Holders move one slot under a spin gate and never allocate or block.
    private sealed class SharedPartition
    {
        private const int Capacity = 128;
        private readonly NativePoolTransferControl<T>?[] _items = new NativePoolTransferControl<T>?[Capacity];
        private int _gate;
        private int _count;

        internal static SharedPartition[] CreateAll()
        {
            SharedPartition[] partitions = new SharedPartition[Math.Clamp(Environment.ProcessorCount, 1, 8)];
            foreach (ref SharedPartition partition in partitions.AsSpan())
            {
                partition = new SharedPartition();
            }

            return partitions;
        }

        internal static NativePoolTransferControl<T>? TryTake(SharedPartition[] partitions)
        {
            int start = Thread.GetCurrentProcessorId();
            for (int offset = 0; offset < partitions.Length; offset++)
            {
                SharedPartition partition = partitions[(start + offset) % partitions.Length];
                if (Volatile.Read(ref partition._count) != 0
                    && partition.TryTake() is NativePoolTransferControl<T> control)
                {
                    return control;
                }
            }

            return null;
        }

        internal static bool TryPark(SharedPartition[] partitions, NativePoolTransferControl<T> control)
        {
            int start = Thread.GetCurrentProcessorId();
            for (int offset = 0; offset < partitions.Length; offset++)
            {
                SharedPartition partition = partitions[(start + offset) % partitions.Length];
                if (Volatile.Read(ref partition._count) != Capacity
                    && partition.TryPark(control))
                {
                    return true;
                }
            }

            return false;
        }

        private NativePoolTransferControl<T>? TryTake()
        {
            Enter();
            int count = _count;
            if (count == 0)
            {
                Volatile.Write(ref _gate, 0);
                return null;
            }

            NativePoolTransferControl<T> control = _items[--count]!;
            _items[count] = null;
            Volatile.Write(ref _count, count);
            Volatile.Write(ref _gate, 0);
            return control;
        }

        private bool TryPark(NativePoolTransferControl<T> control)
        {
            Enter();
            int count = _count;
            if (count == Capacity)
            {
                Volatile.Write(ref _gate, 0);
                return false;
            }

            _items[count] = control;
            Volatile.Write(ref _count, count + 1);
            Volatile.Write(ref _gate, 0);
            return true;
        }

        private void Enter()
        {
            SpinWait spinner = default;
            while (Interlocked.CompareExchange(ref _gate, 1, 0) != 0)
            {
                spinner.SpinOnce();
            }
        }
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
