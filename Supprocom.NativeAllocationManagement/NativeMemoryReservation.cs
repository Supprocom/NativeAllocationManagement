using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>A transferable pre-producer permission over one declared unmanaged range and its prepared ownership control.</summary>
/// <remarks>Copies alias one release identity. Admission is not an OS allocation guarantee; PrepareBacking establishes no-growth backing before production.</remarks>
[StructLayout(LayoutKind.Sequential)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1815", Justification = "A linear producer permission deliberately has no value-equality contract; copying it acquires no independent ownership.")]
public readonly struct NativeMemoryReservation<T> : IDisposable where T : unmanaged
{
    private readonly NativeMemoryReservationControl<T>? _control;
    private readonly long _authorityVersion;

    internal NativeMemoryReservation(NativeMemoryReservationControl<T> control, long authorityVersion)
    {
        _control = control;
        _authorityVersion = authorityVersion;
    }

    /// <summary>Gets immutable control lineage, not initialized native authority.</summary>
    public long Id => Control(nameof(Id)).Id;

    /// <summary>Destructively moves producer permission, without allocating or acquiring a second charge.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "Destructive permission movement consumes the exact unmanaged element type through a ref source.")]
    public static NativeMemoryReservation<T> Move(ref NativeMemoryReservation<T>? source)
    {
        NativeMemoryReservation<T> observed = source ?? throw new ArgumentNullException(nameof(source));
        source = null;
        NativeMemoryReservationControl<T> control = observed.Control(nameof(Move));
        return new(control, control.MoveReservation(observed._authorityVersion));
    }

    /// <summary>Acquires admitted backing before producing payload; failure throws and preserves permission for retry or cancellation.</summary>
    /// <remarks>Cancellation checked after successful acquisition can leave prepared backing held by this still-owning reservation.</remarks>
    public void PrepareBacking(CancellationToken cancellationToken = default) =>
        Control(nameof(PrepareBacking)).PrepareBacking(_authorityVersion, cancellationToken);

    /// <summary>Consumes permission and publishes unique ownership only after complete initialization and the final cancellation check.</summary>
    /// <remarks>The same prepared control is reused. Invalid initializer arguments do not consume the source; initialization, cancellation and acquisition failures do.</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "Activation consumes the exact typed ref permission and returns one unique owner over the same acquisition-time control.")]
    public static NativeTransfer<T> Activate(ref NativeMemoryReservation<T>? source,
        NativeLeaseInitializer<T> initializer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        NativeMemoryReservation<T> observed = source ?? throw new ArgumentNullException(nameof(source));
        source = null;
        return observed.Control(nameof(Activate)).Activate(observed._authorityVersion, initializer, cancellationToken);
    }

    /// <summary>Cancels pending permission or physically frees its prepared unpublished backing, exactly once.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1065", Justification = "Disposal rejects stale or actively preparing permission before returning its resource.")]
    public void Dispose() => Control(nameof(Dispose)).DisposeReservation(_authorityVersion);

    /// <summary>Samples actual permission and control state; available on stale or returned non-default values without reopening authority.</summary>
    public NativeMemoryReservationStatistics CaptureSnapshot() => Control(nameof(CaptureSnapshot)).CaptureReservation(_authorityVersion);

    /// <summary>Retries idle consumed terminal cleanup without reopening producer permission or unique authority.</summary>
    public bool TryCompletePayloadReturn() => Control(nameof(TryCompletePayloadReturn)).TryCompletePayloadReturn();

    internal object? ControlForTest => _control;

    private NativeMemoryReservationControl<T> Control(string operation) => _control
        ?? throw new NativeAllocationUninitializedException(nameof(NativeMemoryReservation<T>), operation);
}

// One specialized acquisition-time object, later referenced by NativeTransfer.
// Ordinary unique owners carry none of these reservation-only fields.
internal sealed class NativeMemoryReservationControl<T> : NativeTransferControl<T> where T : unmanaged
{
    private const int Reserved = 8;
    private const int Preparing = 9;
    private readonly long _budgetId;
    private long _initialUniqueVersion;
    private long _preparationFailures;
    private NativeMemoryReservationOutcome _outcome;
    private bool _backingPrepared;

    internal NativeMemoryReservationControl(NativeMemoryBudget budget, long ownerId, int length)
        : base(new NativeBlock(IntPtr.Zero, 0, 0, budget, ownerId), length, length, published: false)
    {
        _budgetId = budget.Id;
        _outcome = NativeMemoryReservationOutcome.Pending;
    }

    private nuint RequiredBytes => checked((nuint)DeclaredLength * (nuint)Unsafe.SizeOf<T>());
    private NativeMemoryBudget Budget => OwnedBlock.Budget!;

    internal void PublishAdmission() => PublishControlState(Reserved);

    internal override NativeTransferStatistics CaptureSnapshot(long bindingVersion)
    {
        NativeTransferStatistics actual = base.CaptureSnapshot(bindingVersion);
        long initialVersion = Volatile.Read(ref _initialUniqueVersion);
        return actual with
        {
            MoveCount = initialVersion == 0 ? 0 : actual.AuthorityVersion - initialVersion,
            InitializedPayloadBytes = initialVersion == 0 ? 0 : actual.InitializedPayloadBytes,
            PeakInitializedPayloadBytes = initialVersion == 0 ? 0 : actual.PeakInitializedPayloadBytes,
            ControlFieldBytes = actual.ControlFieldBytes + 3L * sizeof(long) + sizeof(int) + sizeof(bool)
        };
    }

    internal NativeMemoryReservationStatistics CaptureReservation(long bindingVersion)
    {
        NativeTransferStatistics actual = CaptureSnapshot(bindingVersion);
        long initialVersion = Volatile.Read(ref _initialUniqueVersion);
        bool obligation = initialVersion == 0 && !StorageReturned && CurrentControlState != 0;
        return new()
        {
            BudgetId = _budgetId,
            OwnerId = Id,
            BindingVersion = bindingVersion,
            AuthorityVersion = actual.AuthorityVersion,
            BindingIsActive = obligation && CurrentControlState == Reserved && bindingVersion == actual.AuthorityVersion,
            Outcome = _outcome,
            DeclaredLength = DeclaredLength,
            RequestedBytes = checked((long)RequiredBytes),
            ReservedBytes = obligation && !_backingPrepared ? checked((long)RequiredBytes) : 0,
            OwnedBackingBytes = actual.OwnedBackingBytes,
            PeakOwnedBackingBytes = actual.PeakOwnedBackingBytes,
            BackingIsPrepared = _backingPrepared,
            ReservationMoveCount = (initialVersion == 0 ? actual.AuthorityVersion : initialVersion) - 1,
            BackingPreparationFailureCount = Volatile.Read(ref _preparationFailures),
            HasReservationReturnObligation = obligation,
            ReturnFailureCount = actual.PayloadReturnFailureCount,
            HistoryOverflowed = actual.HistoryOverflowed,
            ControlFieldBytes = actual.ControlFieldBytes
        };
    }

    internal long MoveReservation(long version)
    {
        Claim(version, Moving, "Move");
        try
        {
            long next = checked(version + 1);
            PublishAuthorityVersion(next);
            NativeMemoryBudget budget = Budget;
            if (budget.TraceEnabled)
                budget.RecordOwnershipTransition(NativeMemoryTraceKind.ReservationMoved, Id, Id, RequiredBytes);
            PublishControlState(Reserved);
            return next;
        }
        catch (OverflowException moveFailure)
        {
            _outcome = NativeMemoryReservationOutcome.AuthorityExhausted;
            CleanupConsumed(moveFailure);
            throw;
        }
    }

    internal void PrepareBacking(long version, CancellationToken cancellationToken)
    {
        Claim(version, Preparing, "PrepareBacking");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareBackingCore();
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            PublishControlState(Reserved);
            GC.KeepAlive(this);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Any producer failure must preserve its original classification and reconcile the consumed reservation before propagation.")]
    internal NativeTransfer<T> Activate(long version, NativeLeaseInitializer<T> initializer, CancellationToken cancellationToken)
    {
        Claim(version, Preparing, "Activate");
        bool producerEntered = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareBackingCore();
            cancellationToken.ThrowIfCancellationRequested();
            int initialized = 0;
            NativeLeaseWriter<T> writer = new(OwnedBlock.Pointer, DeclaredLength, ref initialized);
            producerEntered = true;
            initializer(writer);
            cancellationToken.ThrowIfCancellationRequested();
            if (initialized != DeclaredLength)
                throw new InvalidOperationException($"Reservation initialized {initialized} of {DeclaredLength} required elements.");
            Budget.ActivateApplicationReservation(RequiredBytes, Id);
            Volatile.Write(ref _initialUniqueVersion, version);
            _outcome = NativeMemoryReservationOutcome.Activated;
            PublishControlState(Active);
            return NativeTransfer<T>.CreateAdmitted(this, version);
        }
        catch (Exception activationFailure)
        {
            if (activationFailure is OperationCanceledException) _outcome = NativeMemoryReservationOutcome.Cancelled;
            else if (producerEntered)
            {
                _outcome = NativeMemoryReservationOutcome.InitializationFailed;
                Budget.RecordApplicationInitializationFailure(RequiredBytes, Id);
            }
            else _outcome = NativeMemoryReservationOutcome.PreparationFailed;
            CleanupConsumed(activationFailure);
            throw;
        }
        finally { GC.KeepAlive(this); }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "The public permission delegates deterministic cleanup to its one finalizable acquisition-time control.")]
    internal void DisposeReservation(long version)
    {
        Claim(version, Disposing, "Dispose");
        NativeMemoryReservationOutcome previous = _outcome;
        _outcome = NativeMemoryReservationOutcome.Cancelled;
        try
        {
            ReturnStorage("NativeMemoryReservation.Dispose");
            PublishControlState(Disposed);
            GC.SuppressFinalize(this);
        }
        catch
        {
            if (StorageReturned)
            {
                PublishControlState(Disposed);
                GC.SuppressFinalize(this);
            }
            else
            {
                _outcome = previous;
                PublishControlState(Reserved);
            }
            throw;
        }
    }

    private void PrepareBackingCore()
    {
        if (_backingPrepared) return;
        NativeBlock acquired = default;
        try
        {
            acquired = NativeBlockAllocator.Allocate<T>(DeclaredLength,
                nameof(NativeMemoryReservation<T>), "NativeMemoryReservation.PrepareBacking", ownerId: Id);
            NativeBlock committed = Budget.CommitApplicationBacking(acquired, RequiredBytes, Id);
            InstallOwnedBlock(committed);
            _backingPrepared = true;
            _outcome = NativeMemoryReservationOutcome.Prepared;
            NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativeMemoryReservation.PrepareBacking", 7, "prepared backing observation after actual acquisition");
        }
        catch (Exception preparationFailure)
        {
            if (!_backingPrepared) NativeBlockAllocator.Free(acquired);
            IncrementControlHistory(ref _preparationFailures);
            Budget.RecordApplicationBackingFailure(RequiredBytes, Id, preparationFailure is NativeAllocationFailedException);
            throw;
        }
    }

    private void Claim(long version, int state, string operation)
    {
        if (!TryTransition(Reserved, state)) ThrowInactive(operation);
        if (CurrentAuthorityVersion != version)
        {
            PublishControlState(Reserved);
            ThrowInactive(operation);
        }
    }

    private static void ThrowInactive(string operation) => throw new InvalidOperationException(
        $"NativeMemoryReservation.{operation} requires the current idle producer permission.");

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "A consumed permission disarms emergency finalization only after its resource really returned.")]
    private void CleanupConsumed(Exception originalFailure)
    {
        PublishControlState(Retiring);
        try
        {
            ReturnStorage("NativeMemoryReservation.ConsumedReturn");
            PublishControlState(Disposed);
            GC.SuppressFinalize(this);
        }
        catch (Exception cleanupFailure)
        {
            if (StorageReturned)
            {
                PublishControlState(Disposed);
                GC.SuppressFinalize(this);
            }
            throw new AggregateException("Consumed reservation failure and resource return both failed.", originalFailure, cleanupFailure);
        }
    }

    private protected override bool CanFinalize(int state) => state == Reserved || base.CanFinalize(state);

    private protected override void ReturnStorage(string operation)
    {
        if (_initialUniqueVersion != 0)
        {
            base.ReturnStorage(operation);
            return;
        }
        if (StorageReturned) return;
        NativeMemoryBudget budget = Budget;
        if (CurrentControlState == Finalized && _outcome is NativeMemoryReservationOutcome.Pending or NativeMemoryReservationOutcome.Prepared)
            _outcome = NativeMemoryReservationOutcome.Abandoned;
        try
        {
            NativeMemoryTestHooks.CheckManagedPublicationBoundary(operation, 4, "reservation resource return");
            budget.ReturnApplicationReservation(OwnedBlock, RequiredBytes, _backingPrepared, _outcome, Id);
        }
        catch
        {
            RecordReturnFailure();
            budget.RecordApplicationReturnFailure(RequiredBytes, Id);
            throw;
        }
        CompleteStorageReturn();
        NativeMemoryTestHooks.CheckManagedPublicationBoundary(operation, 5, "reservation return observation after actual cleanup");
    }
}
