using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

public sealed partial class NativeMemoryBudget
{
    private sealed class NativeAdmissionLedger
    {
        internal long Outstanding;
        internal long PeakOutstanding;
        internal long PendingBytes;
        internal long PeakPendingBytes;
        internal long PreparedBytes;
        internal long PeakPreparedBytes;
        internal long Admitted;
        internal long Prepared;
        internal long PreparationFailures;
        internal long BackendFailures;
        internal long Activated;
        internal long Cancelled;
        internal long InitializationFailures;
        internal long Abandoned;
        internal long ReturnFailures;
    }

    private NativeAdmissionLedger? _applicationAdmission;
    private long _applicationRejectedCount;
    private long _applicationControlPreparationFailureCount;

    /// <summary>Admits ownership metadata and a declared initialized unmanaged range before payload production.</summary>
    /// <remarks>False means native-byte capacity only and allocates nothing. Metadata and later backend failures throw; native byte admission is not an OS allocation guarantee.</remarks>
    /// <typeparam name="T">The unmanaged payload element type.</typeparam>
    /// <param name="length">The exact eventual initialized element count.</param>
    /// <param name="reservation">The sole producer permission on success; null on refusal.</param>
    /// <param name="reason">The actual expected-capacity outcome.</param>
    public bool TryReserve<T>(int length, [NotNullWhen(true)] out NativeMemoryReservation<T>? reservation,
        out NativeMemoryAdmissionExhaustionReason reason) where T : unmanaged
        => TryReserveCore(length, layout: null, out reservation, out reason);

    internal bool TryReserveLayout(NativeLayout layout, [NotNullWhen(true)] out NativeMemoryReservation<byte>? reservation,
        out NativeMemoryAdmissionExhaustionReason reason) => TryReserveCore(layout.BackingBytes, layout, out reservation, out reason);

    private bool TryReserveCore<T>(int length, NativeLayout? layout, [NotNullWhen(true)] out NativeMemoryReservation<T>? reservation,
        out NativeMemoryAdmissionExhaustionReason reason) where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        nuint byteLength = checked((nuint)length * (nuint)Unsafe.SizeOf<T>());
        long bytes = checked((long)byteLength);
        lock (_gate)
        {
            if (bytes > CapacityBytes - _committedBytes - _reservedBytes)
            {
                IncrementHistory(ref _applicationRejectedCount);
                IncrementHistory(ref _rejectedAllocationCount);
                RecordTrace(NativeMemoryTraceKind.Rejected, 0, byteLength);
                reservation = null;
                reason = NativeMemoryAdmissionExhaustionReason.NativeByteCapacity;
                return false;
            }

            if (_applicationAdmission?.Outstanding == long.MaxValue)
                throw new OverflowException("Outstanding reservation identity cannot wrap.");
            NativeAdmissionLedger ledger;
            NativeMemoryReservationControl<T> control;
            try
            {
                NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativeMemoryBudget.TryReserve", 6, "application admission metadata");
                ledger = _applicationAdmission ?? new NativeAdmissionLedger();
                long ownerId = NativeOwnerIdentity.NextWithoutPreparation();
                control = layout is null ? new(this, ownerId, length)
                    : (NativeMemoryReservationControl<T>)(object)new NativeLayoutControl(this, ownerId, layout);
                NativeMemoryAccounting.PrepareThread();
            }
            catch
            {
                IncrementHistory(ref _applicationControlPreparationFailureCount);
                RecordTrace(NativeMemoryTraceKind.ReservationPreparationFailed, 0, byteLength);
                throw;
            }

            _applicationAdmission = ledger;
            _reservedBytes += bytes;
            _peakAdmittedBytes = Math.Max(_peakAdmittedBytes, _committedBytes + _reservedBytes);
            ledger.Outstanding++;
            ledger.PeakOutstanding = Math.Max(ledger.PeakOutstanding, ledger.Outstanding);
            ledger.PendingBytes += bytes;
            ledger.PeakPendingBytes = Math.Max(ledger.PeakPendingBytes, ledger.PendingBytes);
            IncrementHistory(ref ledger.Admitted);
            control.PublishAdmission();
            RecordTrace(NativeMemoryTraceKind.ReservationAdmitted, ownerId: control.Id, byteLength, correlationId: control.Id);
            reservation = new NativeMemoryReservation<T>(control, authorityVersion: 1);
            reason = NativeMemoryAdmissionExhaustionReason.None;
            return true;
        }
    }

    /// <summary>Captures exact application-admission gauges and real lifetime histories without acquiring ownership.</summary>
    public NativeMemoryAdmissionStatistics CaptureAdmissionStatistics()
    {
        lock (_gate)
        {
            NativeAdmissionLedger? ledger = _applicationAdmission;
            return new()
            {
                BudgetId = Id,
                CapacityBytes = CapacityBytes,
                OutstandingReservationCount = ledger?.Outstanding ?? 0,
                PeakOutstandingReservationCount = ledger?.PeakOutstanding ?? 0,
                PendingBytes = ledger?.PendingBytes ?? 0,
                PeakPendingBytes = ledger?.PeakPendingBytes ?? 0,
                PreparedUnpublishedBytes = ledger?.PreparedBytes ?? 0,
                PeakPreparedUnpublishedBytes = ledger?.PeakPreparedBytes ?? 0,
                AdmittedReservationCount = ledger?.Admitted ?? 0,
                RejectedReservationCount = _applicationRejectedCount,
                ControlPreparationFailureCount = _applicationControlPreparationFailureCount,
                BackingPreparationCount = ledger?.Prepared ?? 0,
                BackingPreparationFailureCount = ledger?.PreparationFailures ?? 0,
                BackendAllocationFailureCount = ledger?.BackendFailures ?? 0,
                ActivationCount = ledger?.Activated ?? 0,
                CancelledReservationCount = ledger?.Cancelled ?? 0,
                InitializationFailureCount = ledger?.InitializationFailures ?? 0,
                AbandonedReservationCount = ledger?.Abandoned ?? 0,
                ReturnFailureCount = ledger?.ReturnFailures ?? 0,
                HistoryOverflowed = _historyOverflowed,
                LedgerFieldBytes = ledger is null ? 0 : 15L * sizeof(long),
                BudgetAdmissionFieldBytes = IntPtr.Size + 2L * sizeof(long)
            };
        }
    }

    internal NativeBlock CommitApplicationBacking(NativeBlock acquired, nuint byteLength, long ownerId)
    {
        lock (_gate)
        {
            NativeAdmissionLedger ledger = _applicationAdmission!;
            long bytes = checked((long)byteLength);
            ValidateReservation(bytes);
            if (bytes > ledger.PendingBytes || ledger.Outstanding == 0)
                throw new InvalidOperationException("Prepared backing has no matching application admission.");
            _reservedBytes -= bytes;
            ledger.PendingBytes -= bytes;
            _committedBytes += bytes;
            _peakCommittedBytes = Math.Max(_peakCommittedBytes, _committedBytes);
            ledger.PreparedBytes += bytes;
            ledger.PeakPreparedBytes = Math.Max(ledger.PeakPreparedBytes, ledger.PreparedBytes);
            IncrementHistory(ref ledger.Prepared);
            if (bytes != 0)
            {
                _activeAllocationCount++;
                IncrementHistory(ref _allocationCount);
                RecordTrace(NativeMemoryTraceKind.Allocated, ownerId, byteLength);
            }
            RecordTrace(NativeMemoryTraceKind.ReservationBackingPrepared, ownerId, byteLength, correlationId: ownerId);
            return new NativeBlock(acquired.Pointer, acquired.ByteLength, acquired.MetricsEpoch, this, ownerId);
        }
    }

    internal void RecordApplicationBackingFailure(nuint bytes, long ownerId, bool backendFailure)
    {
        lock (_gate)
        {
            IncrementHistory(ref _applicationAdmission!.PreparationFailures);
            if (backendFailure)
            {
                IncrementHistory(ref _applicationAdmission.BackendFailures);
                IncrementHistory(ref _failedAllocationCount);
            }
            RecordTrace(NativeMemoryTraceKind.ReservationBackingFailed, ownerId, bytes, correlationId: ownerId);
        }
    }

    internal void ActivateApplicationReservation(nuint byteLength, long ownerId)
    {
        lock (_gate)
        {
            NativeAdmissionLedger ledger = _applicationAdmission!;
            long bytes = checked((long)byteLength);
            if (ledger.Outstanding == 0 || bytes > ledger.PreparedBytes)
                throw new InvalidOperationException("Payload publication has no prepared reservation.");
            ledger.Outstanding--;
            ledger.PreparedBytes -= bytes;
            IncrementHistory(ref ledger.Activated);
            RecordTrace(NativeMemoryTraceKind.ReservationActivated, ownerId, byteLength, correlationId: ownerId);
        }
    }

    internal void RecordApplicationInitializationFailure(nuint bytes, long ownerId)
    {
        lock (_gate)
        {
            IncrementHistory(ref _applicationAdmission!.InitializationFailures);
            RecordTrace(NativeMemoryTraceKind.ReservationInitializationFailed, ownerId, bytes, correlationId: ownerId);
        }
    }

    internal void RecordApplicationReturnFailure(nuint bytes, long ownerId)
    {
        lock (_gate)
        {
            IncrementHistory(ref _applicationAdmission!.ReturnFailures);
            RecordTrace(NativeMemoryTraceKind.ReservationReturnFailed, ownerId, bytes, correlationId: ownerId);
        }
    }

    internal void ReturnApplicationReservation(NativeBlock block, nuint byteLength, bool prepared,
        NativeMemoryReservationOutcome outcome, long ownerId)
    {
        lock (_gate)
        {
            NativeAdmissionLedger ledger = _applicationAdmission!;
            long bytes = checked((long)byteLength);
            if (ledger.Outstanding == 0 || bytes > (prepared ? ledger.PreparedBytes : ledger.PendingBytes))
                throw new InvalidOperationException("Reservation return has no matching obligation.");
            if (prepared)
            {
                // The existing domain gate is reentrant. Physical free, its
                // ordinary charge release, and reservation gauges are observed
                // as one domain transition, not an invented second free.
                NativeBlockAllocator.Free(block);
                ledger.PreparedBytes -= bytes;
            }
            else
            {
                ValidateReservation(bytes);
                _reservedBytes -= bytes;
                ledger.PendingBytes -= bytes;
            }
            ledger.Outstanding--;
            if (outcome == NativeMemoryReservationOutcome.Abandoned) IncrementHistory(ref ledger.Abandoned);
            else if (outcome == NativeMemoryReservationOutcome.Cancelled) IncrementHistory(ref ledger.Cancelled);
            NativeMemoryTraceKind kind = outcome switch
            {
                NativeMemoryReservationOutcome.Cancelled => NativeMemoryTraceKind.ReservationCancelled,
                NativeMemoryReservationOutcome.Abandoned => NativeMemoryTraceKind.ReservationAbandoned,
                NativeMemoryReservationOutcome.AuthorityExhausted => NativeMemoryTraceKind.ReservationAuthorityExhausted,
                _ => NativeMemoryTraceKind.ReservationReturned
            };
            RecordTrace(kind, ownerId, byteLength, correlationId: ownerId);
        }
    }
}
