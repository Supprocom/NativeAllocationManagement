namespace Supprocom.NativeAllocationManagement;

// Immutable sharing reuses acquisition-time custody and its emergency finalizer.
// Weak observers retain only NativeSharedControl, never this storage owner.
internal partial class NativeTransferControl<T> where T : unmanaged
{
    internal NativeSharedControl<T> Control => _sharingControl!;

    internal unsafe void InitializeSharing(long authorityVersion, NativeSharedControl<T> control)
    {
        _sharingControl = control;
        int observed = Interlocked.CompareExchange(ref _state, Moving, Active);
        if (observed != Active) ThrowInactive("NativeShared.Pin", observed);
        if (Volatile.Read(ref _authorityVersion) != authorityVersion)
        {
            Volatile.Write(ref _state, Active);
            ThrowInactive("NativeShared.Pin", Moved);
        }
        if (NativeOperationAdmission.Close(ref _operationAdmission) != 0)
        {
            NativeOperationAdmission.Open(ref _operationAdmission);
            Volatile.Write(ref _state, Active);
            throw new InvalidOperationException("Shared custody cannot begin during an entered unique callback.");
        }
        nuint ownedBytes;
        nuint borrowedBytes;
        NativeMemoryBudget? budget;
        long ordinal;
        try
        {
            if (_kernel is null)
            {
                ownedBytes = _block.ByteLength;
                borrowedBytes = 0;
                budget = _block.Budget;
                ordinal = ownedBytes == 0 ? 0 : 1;
            }
            else
            {
                _kernel.PrepareTransferReturnCapacity(_block.MetricsEpoch, _block.OwnerId);
                NativeOperationToken token = EnterKernelOperation("NativeShared.Pin");
                try
                {
                    NativeSegment? segment = _allocationState!.Segment;
                    IntPtr pointer = _length == 0 ? IntPtr.Zero : (IntPtr)((byte*)segment!.Pointer + _allocationState.OffsetBytes);
                    ownedBytes = segment?.AllocationByteLength ?? 0;
                    borrowedBytes = segment is { AllocationByteLength: 0 } ? segment.ByteLength : 0;
                    budget = _block.Budget;
                    ordinal = segment?.AllocationOrdinal ?? 0;
                    // Pooled storage is still returned through its kernel. This
                    // zero-length block only caches the entered pointer/budget.
                    _block = _block with { Pointer = pointer };
                    token.MoveToSharingCustody(out _sharingAllocationEntered, out _sharingGenerationEntered);
                }
                finally { token.Dispose(); }
            }
            Volatile.Write(ref _state, Shared);
        }
        catch
        {
            NativeOperationAdmission.Open(ref _operationAdmission);
            Volatile.Write(ref _state, Active);
            throw;
        }
        control.ConfigureStorage(ownedBytes, borrowedBytes, budget, ordinal);
    }

    internal unsafe NativeReadOnlyLeaseView<T> GetView(int offset, int length) =>
        new(new ReadOnlySpan<T>((void*)_block.Pointer, checked(offset + length)).Slice(offset, length));

    internal void ReturnSharedPayload()
    {
        if (StorageHasBeenReturned)
        {
            CompleteSharedPayloadReturn();
            return;
        }
        int observed = Interlocked.CompareExchange(ref _state, Disposing, Shared);
        if (observed is Moving or Disposing)
        {
            // A rejected stale unique operation can briefly own the private
            // cleanup state after failed pin preparation. Do not strand the
            // release gate, overwrite that operation, or invent completion.
            Control.DeferPayloadReturn();
            return;
        }
        try
        {
            if (observed is not (Shared or Active)) ThrowInactive("NativeShared.Return", observed);
            NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativeShared.Return", 3, "payload authority return");
            if (observed == Active)
            {
                // A failed pin has not published sharing. Only the private moved
                // binding retains return authority; stale aliases stay invalid.
                Dispose(Volatile.Read(ref _authorityVersion));
            }
            else
            {
                ReleaseSharingPin();
                ReturnStorage("NativeTransfer.Dispose");
                Volatile.Write(ref _state, Disposed);
            }
            CompleteSharedPayloadReturn();
        }
        catch (Exception returnFailure)
        {
            bool returned = StorageHasBeenReturned;
            // Publish custody readiness before allowing another retry. The
            // Active fallback's Dispose owns its own state restoration.
            if (observed == Shared) Volatile.Write(ref _state, returned ? Disposed : Shared);
            Control.RecordReturnFailure();
            NativeMemoryTestHooks.NotifyBeforeOperationEntry("NativeShared.RetryReady");
            if (returned)
            {
                try { CompleteSharedPayloadReturn(); }
                catch (Exception observationFailure)
                {
                    throw new AggregateException("Storage return completed but its observations failed.", returnFailure, observationFailure);
                }
            }
            throw;
        }
    }

    private void ReleaseSharingPin()
    {
        if (!_sharingAllocationEntered && !_sharingGenerationEntered) return;
        NativeOwnerKernel kernel = _kernel!;
        NativeAllocation allocation = _allocationState!;
        NativeGeneration generation = allocation.GenerationState;
        bool allocationEntered = _sharingAllocationEntered;
        bool generationEntered = _sharingGenerationEntered;
        _sharingAllocationEntered = false;
        _sharingGenerationEntered = false;
        try { kernel.ExitOperation(generation, allocation, allocationEntered, generationEntered, "NativeShared.Pin"); }
        finally { GC.KeepAlive(generation.Owner); }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "Actual shared payload return disarms the existing acquisition-time emergency finalizer; no second finalizable wrapper is allocated.")]
    private void CompleteSharedPayloadReturn()
    {
        try { Control.RecordPayloadReturn(); }
        finally { GC.SuppressFinalize(this); }
    }

    internal void AbortSharingInitialization()
    {
        if (Control.BeginAbandonedRelease()) ReturnSharedPayload();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Emergency native cleanup cannot propagate into process termination; a failed actual return is retried by finalization.")]
    private void FinalizeSharedPayload()
    {
        try
        {
            NativeMemoryTestHooks.NotifyBeforeOperationEntry("NativeShared.Finalize");
            if (Control.BeginAbandonedRelease()) ReturnSharedPayload();
        }
        catch { GC.ReRegisterForFinalize(this); }
    }
}
