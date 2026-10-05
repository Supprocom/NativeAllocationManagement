using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

internal sealed class NativeLayoutControl : NativeMemoryReservationControl<byte>
{
    private readonly NativeLayout _layout;
    private int _payloadOffset;
    private bool _layoutPrepared;
    private bool _initializationCompleted;
    private long _copiedBytes;
    private long _detachedOwners;
    private bool _layoutHistoryOverflowed;

    internal NativeLayoutControl(NativeMemoryBudget budget, long ownerId, NativeLayout layout)
        : base(budget, ownerId, layout.BackingBytes) => _layout = layout;

    internal override NativeTransferStatistics CaptureSnapshot(long bindingVersion)
    {
        NativeTransferStatistics actual = base.CaptureSnapshot(bindingVersion);
        bool initialized = HasActivated && actual.InitializedPayloadBytes != 0;
        return actual with
        {
            InitializedPayloadBytes = initialized ? _layout.LogicalBytes : 0,
            PeakInitializedPayloadBytes = HasActivated ? _layout.LogicalBytes : 0,
            ControlFieldBytes = actual.ControlFieldBytes + IntPtr.Size + sizeof(int) + 2L * sizeof(long) + 3L * sizeof(bool)
        };
    }

    internal NativeLayoutStatistics CaptureLayout(long version)
    {
        NativeTransferStatistics actual = CaptureSnapshot(version);
        bool published = HasActivated && !StorageReturned;
        return new()
        {
            LayoutId = _layout.Id,
            Ownership = actual,
            Reservation = CaptureReservation(version),
            RegionCount = _layout.RegionCount,
            InitializedRegionCount = published ? _layout.RegionCount : 0,
            LogicalInitializedBytes = actual.InitializedPayloadBytes,
            LayoutExtentBytes = _layout.ExtentBytes,
            InterRegionPaddingBytes = _layout.ExtentBytes - _layout.LogicalBytes,
            AlignmentSlackBytes = _layout.BackingBytes - _layout.ExtentBytes,
            PayloadOffsetBytes = _payloadOffset,
            PayloadAlignment = _layout.Alignment,
            LayoutIsPrepared = _layoutPrepared,
            InitializationCompleted = _initializationCompleted,
            DescriptorFieldBytes = _layout.MetadataFieldBytes,
            CopiedBytes = Volatile.Read(ref _copiedBytes),
            DetachedOwnerCount = Volatile.Read(ref _detachedOwners),
            HistoryOverflowed = actual.HistoryOverflowed || Volatile.Read(ref _layoutHistoryOverflowed)
        };
    }

    private protected override void PrepareBackingCore()
    {
        base.PrepareBackingCore();
        if (_layoutPrepared) return;
        nuint mask = (nuint)(_layout.Alignment - 1);
        _payloadOffset = OwnedBlock.Pointer == IntPtr.Zero ? 0 : (int)(unchecked(0 - (nuint)OwnedBlock.Pointer) & mask);
        _layoutPrepared = true;
        NativeMemoryBudget budget = OwnedBlock.Budget!;
        if (budget.TraceEnabled)
            budget.RecordOwnershipTransition(NativeMemoryTraceKind.LayoutPrepared, Id, _layout.Id, (nuint)_layout.ExtentBytes,
                BackingOrdinal);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Every consumed layout activation failure must reconcile its charged resource before preserving the original exception.")]
    internal long ActivateLayout<TState>(long version, TState state, NativeLayoutInitializer<TState> initializer,
        CancellationToken cancellationToken) where TState : allows ref struct
    {
        Claim(version, Preparing, "NativeLayout.Activate");
        bool producerEntered = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareBackingCore();
            cancellationToken.ThrowIfCancellationRequested();
            producerEntered = true;
            Initialize(state, initializer);
            cancellationToken.ThrowIfCancellationRequested();
            PublishActivation(version);
            return version;
        }
        catch (Exception failure)
        {
            ReconcileActivationFailure(failure, producerEntered);
            throw;
        }
        finally { GC.KeepAlive(this); }
    }

    private unsafe void Initialize<TState>(scoped TState state,
        NativeLayoutInitializer<TState> initializer) where TState : allows ref struct
    {
        Span<int> initialized = stackalloc int[_layout.RegionCount];
        initialized.Clear();
        Span<byte> backing = new((void*)OwnedBlock.Pointer, DeclaredLength);
        Span<byte> payload = backing.Slice(_payloadOffset, _layout.ExtentBytes);
        initializer(new NativeLayoutWriter(_layout, payload, initialized), state);
        for (int index = 0; index < initialized.Length; index++)
        {
            if (initialized[index] != _layout.Region(index).Length)
                throw new InvalidOperationException($"Layout region {index} initialized {initialized[index]} of {_layout.Region(index).Length} required elements.");
        }
        ClearPadding(backing);
        _initializationCompleted = true;
        NativeMemoryBudget budget = OwnedBlock.Budget!;
        if (budget.TraceEnabled)
            budget.RecordOwnershipTransition(NativeMemoryTraceKind.LayoutInitialized, Id, _layout.Id, (nuint)_layout.LogicalBytes,
                BackingOrdinal);
    }

    private void ClearPadding(scoped Span<byte> backing)
    {
        int previousEnd = 0;
        for (int index = 0; index < _layout.RegionCount; index++)
        {
            NativeLayoutRegion region = _layout.Region(index);
            int start = _payloadOffset + region.Offset;
            ClearRange(backing.Slice(previousEnd, start - previousEnd));
            previousEnd = start + region.Bytes;
        }
        ClearRange(backing[previousEnd..]);
    }

    private static void ClearRange(scoped Span<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        bytes.Clear();
        NativeMemoryAccounting.RecordStorageClear((nuint)bytes.Length, writtenBytes: 0);
    }

    internal unsafe TResult ReadLayout<TState, TResult>(long version, TState state, NativeLayoutFunc<TState, TResult> action)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterTransferOperation(version, "NativeLayoutOwner.Read");
        try { return action(new(_layout, new Span<byte>((byte*)OwnedBlock.Pointer + _payloadOffset, _layout.ExtentBytes)), state); }
        finally { ExitTransferOperation(); GC.KeepAlive(this); }
    }

    internal unsafe void AccessLayout<TState>(long version, TState state, NativeLayoutAction<TState> action)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterTransferOperation(version, "NativeLayoutOwner.Access");
        try { action(new(_layout, new Span<byte>((byte*)OwnedBlock.Pointer + _payloadOffset, _layout.ExtentBytes)), state); }
        finally { ExitTransferOperation(); GC.KeepAlive(this); }
    }

    internal unsafe NativeTransfer<T> DetachField<T>(long version, NativeLayoutField<T> field,
        NativeMemoryBudget budget, CancellationToken cancellationToken) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(budget);
        NativeLayoutRegion region = _layout.Region(field);
        cancellationToken.ThrowIfCancellationRequested();
        EnterTransferOperation(version, "NativeLayoutOwner.DetachField");
        try
        {
            if (!budget.TryReserve<T>(region.Length, out NativeMemoryReservation<T>? permission, out _))
            {
                NativeMemoryBudgetStatistics actual = budget.CaptureStatistics();
                throw new NativeMemoryBudgetExceededException(budget.Id, budget.CapacityBytes,
                    (nuint)region.Bytes, actual.CapacityBytes - actual.CommittedBytes - actual.ReservedBytes);
            }
            try
            {
                NativeAdmittedInitializer<T, CopyState<T>> copyInitializer = static (writer, context) =>
                {
                    writer.CopyDirectInitialization(context.Values);
                    context.Source.RecordLayoutHistory(ref context.Source._copiedBytes, (long)context.Values.Length * Unsafe.SizeOf<T>());
                    NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativeLayoutOwner.DetachField", 8, "field copy observation after actual copy and before publication");
                };
                var control = (NativeMemoryReservationControl<T>)permission.Value.ControlForTest!;
                permission = null;
                ReadOnlySpan<T> source = new((byte*)OwnedBlock.Pointer + _payloadOffset + region.Offset, region.Length);
                long destinationVersion = control.ActivateWithState(1, copyInitializer, new CopyState<T>(this, source), cancellationToken);
                RecordLayoutHistory(ref _detachedOwners, 1);
                NativeMemoryBudget sourceBudget = OwnedBlock.Budget!;
                if (sourceBudget.TraceEnabled)
                    sourceBudget.RecordOwnershipTransition(NativeMemoryTraceKind.Detached, Id, control.Id, (nuint)region.Bytes,
                        BackingOrdinal);
                return NativeTransfer<T>.CreateAdmitted(control, destinationVersion);
            }
            finally { permission?.Dispose(); }
        }
        finally { ExitTransferOperation(); GC.KeepAlive(this); }
    }

    private void RecordLayoutHistory(ref long history, long delta)
    {
        long observed = Volatile.Read(ref history);
        while (true)
        {
            bool overflow = delta > long.MaxValue - observed;
            long next = overflow ? long.MaxValue : observed + delta;
            long competing = Interlocked.CompareExchange(ref history, next, observed);
            if (competing == observed)
            {
                if (overflow) Volatile.Write(ref _layoutHistoryOverflowed, true);
                return;
            }
            observed = competing;
        }
    }

    private readonly ref struct CopyState<T>(NativeLayoutControl source, ReadOnlySpan<T> values)
    {
        internal NativeLayoutControl Source { get; } = source;
        internal ReadOnlySpan<T> Values { get; } = values;
    }
}
