using System.Reflection;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Conformance;

// Public-only expected transitions. Reflection compares complete property sets,
// not a selection; it is verification work and never runs in an allocator.
internal static class NativeAdmissionDiagnosticOracle
{
    // Sequential native block: pointer, native extent, metrics epoch, budget
    // reference and owner identity. Excludes CLR headers and field padding.
    internal static long UniqueControlFieldBytes => 6L * IntPtr.Size + 8L * sizeof(long) + 6L * sizeof(int) + 2L * sizeof(bool);
    internal static long ReservationControlFieldBytes => UniqueControlFieldBytes + 3L * sizeof(long) + sizeof(int) + sizeof(bool);

    internal static void Run(int traceCapacity)
    {
        NativeMemoryBudget budget = new(20, traceCapacity);
        Admission expected = Admission.Empty(budget.Id, 20);
        Verify(budget.CaptureAdmissionStatistics(), expected, "empty");
        if (!budget.TryReserve<int>(3, out NativeMemoryReservation<int>? source, out _))
            throw new InvalidOperationException("Declared twelve-byte permission was refused.");
        long ownerId = source.Value.Id;
        Reservation reservation = Reservation.Pending(budget.Id, ownerId, 3, 12);
        expected = expected with
        {
            OutstandingReservationCount = 1,
            PeakOutstandingReservationCount = 1,
            PendingBytes = 12,
            PeakPendingBytes = 12,
            AdmittedReservationCount = 1,
            LedgerFieldBytes = 120
        };
        Verify(source.Value.CaptureSnapshot(), reservation, "pending");
        Verify(budget.CaptureAdmissionStatistics(), expected, "pending");
        try
        {
            if (!budget.TryReserve<byte>(8, out NativeMemoryReservation<byte>? second, out _))
                throw new InvalidOperationException("Declared eight-byte permission was refused.");
            Reservation secondExpected = Reservation.Pending(budget.Id, second.Value.Id, 8, 8);
            expected = expected with
            {
                OutstandingReservationCount = 2,
                PeakOutstandingReservationCount = 2,
                PendingBytes = 20,
                PeakPendingBytes = 20,
                AdmittedReservationCount = 2
            };
            try
            {
                Verify(second.Value.CaptureSnapshot(), secondExpected, "second-pending");
                Verify(budget.CaptureAdmissionStatistics(), expected, "second-pending");
                if (budget.TryReserve<byte>(1, out NativeMemoryReservation<byte>? unexpected, out NativeMemoryAdmissionExhaustionReason reason))
                {
                    unexpected.Value.Dispose();
                    throw new InvalidOperationException("Admission exceeded the cap.");
                }
                if (reason != NativeMemoryAdmissionExhaustionReason.NativeByteCapacity || unexpected.HasValue)
                    throw new InvalidOperationException("Refusal must default output and classify capacity.");
                expected = expected with { RejectedReservationCount = 1 };
                Verify(budget.CaptureAdmissionStatistics(), expected, "refused");
            }
            finally { second.Value.Dispose(); }
            expected = expected with { OutstandingReservationCount = 1, PendingBytes = 12, CancelledReservationCount = 1 };
            Verify(second.Value.CaptureSnapshot(), secondExpected with
            { BindingIsActive = false, Outcome = NativeMemoryReservationOutcome.Cancelled, ReservedBytes = 0, HasReservationReturnObligation = false }, "second-cancelled");
            Verify(budget.CaptureAdmissionStatistics(), expected, "second-cancelled");

            NativeMemoryReservation<int>? moved = NativeMemoryReservation<int>.Move(ref source);
            try
            {
                reservation = reservation with { BindingVersion = 2, AuthorityVersion = 2, ReservationMoveCount = 1 };
                Verify(moved.Value.CaptureSnapshot(), reservation, "current-after-move");
                moved.Value.PrepareBacking();
                reservation = reservation with
                {
                    Outcome = NativeMemoryReservationOutcome.Prepared,
                    ReservedBytes = 0,
                    OwnedBackingBytes = 12,
                    PeakOwnedBackingBytes = 12,
                    BackingIsPrepared = true
                };
                expected = expected with
                {
                    PendingBytes = 0,
                    PreparedUnpublishedBytes = 12,
                    PeakPreparedUnpublishedBytes = 12,
                    BackingPreparationCount = 1
                };
                Verify(moved.Value.CaptureSnapshot(), reservation, "prepared");
                Verify(budget.CaptureAdmissionStatistics(), expected, "prepared");
                NativeTransfer<int> unique = NativeMemoryReservation<int>.Activate(ref moved, static writer => writer.Fill(42));
                Unique uniqueExpected = Unique.Initial(ownerId, 12, 12) with
                { BindingVersion = 2, AuthorityVersion = 2, ControlFieldBytes = ReservationControlFieldBytes };
                try
                {
                    reservation = reservation with { Outcome = NativeMemoryReservationOutcome.Activated, HasReservationReturnObligation = false };
                    expected = expected with { OutstandingReservationCount = 0, PreparedUnpublishedBytes = 0, ActivationCount = 1 };
                    Verify(budget.CaptureAdmissionStatistics(), expected, "activated");
                    Verify(unique.CaptureSnapshot(), uniqueExpected, "same-control-unique");
                    if (unique.Read(static view => view[0] + view[1] + view[2]) != 126)
                        throw new InvalidOperationException("Initialized output differs.");
                    Verify(unique.CaptureSnapshot(), uniqueExpected with { PeakBorrowCount = 1 }, "read-ended");
                }
                finally { unique.Dispose(); }
                Verify(unique.CaptureSnapshot(), (uniqueExpected with { PeakBorrowCount = 1 }).Returned(), "unique-returned");
                Verify(budget.CaptureAdmissionStatistics(), expected, "unique-returned");
                if (budget.CaptureStatistics().CommittedBytes != 0 || budget.CaptureStatistics().ReservedBytes != 0)
                    throw new InvalidOperationException("Terminal storage obligations remain.");
            }
            finally { moved?.Dispose(); }
        }
        finally { source?.Dispose(); }
    }

    internal static void RunOrdinaryUnique(int traceCapacity)
    {
        NativeMemoryBudget budget = new(16, traceCapacity);
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(42);
        NativeTransfer<int>? source = builder.Complete();
        Unique expected = Unique.Initial(builder.Id, 16, 4);
        Verify(source.Value.CaptureSnapshot(), expected, "ordinary-initial");
        try
        {
            NativeTransfer<int> moved = NativeTransfer<int>.Move(ref source);
            try
            {
                expected = expected with { AuthorityVersion = 2, BindingVersion = 2, MoveCount = 1 };
                Verify(moved.CaptureSnapshot(), expected, "ordinary-moved");
                if (moved.Read(static view => view[0]) != 42) throw new InvalidOperationException("Unique output differs.");
                expected = expected with { PeakBorrowCount = 1 };
                Verify(moved.CaptureSnapshot(), expected, "ordinary-read-ended");
            }
            finally { moved.Dispose(); }
            Verify(moved.CaptureSnapshot(), expected.Returned(), "ordinary-returned");
        }
        finally { source?.Dispose(); }
        if (budget.CaptureStatistics().CommittedBytes != 0) throw new InvalidOperationException("Unique charge remains.");
    }

    internal static void Verify<TActual, TExpected>(TActual actual, TExpected expected, string stage)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        PropertyInfo[] actualFields = typeof(TActual).GetProperties(flags);
        PropertyInfo[] expectedFields = typeof(TExpected).GetProperties(flags);
        if (actualFields.Length != expectedFields.Length) throw new InvalidOperationException($"{stage}: property set differs.");
        foreach (PropertyInfo field in actualFields)
        {
            PropertyInfo? expectedField = typeof(TExpected).GetProperty(field.Name, flags);
            if (expectedField is null || expectedField.PropertyType != field.PropertyType
                || !Equals(field.GetValue(actual), expectedField.GetValue(expected)))
                throw new InvalidOperationException($"{stage}: {field.Name} was {field.GetValue(actual)}, expected {expectedField?.GetValue(expected)}.");
        }
    }

    internal readonly record struct Admission
    {
        public long BudgetId { get; init; }
        public long CapacityBytes { get; init; }
        public long OutstandingReservationCount { get; init; }
        public long PeakOutstandingReservationCount { get; init; }
        public long PendingBytes { get; init; }
        public long PeakPendingBytes { get; init; }
        public long PreparedUnpublishedBytes { get; init; }
        public long PeakPreparedUnpublishedBytes { get; init; }
        public long AdmittedReservationCount { get; init; }
        public long RejectedReservationCount { get; init; }
        public long ControlPreparationFailureCount { get; init; }
        public long BackingPreparationCount { get; init; }
        public long BackingPreparationFailureCount { get; init; }
        public long BackendAllocationFailureCount { get; init; }
        public long ActivationCount { get; init; }
        public long CancelledReservationCount { get; init; }
        public long InitializationFailureCount { get; init; }
        public long AbandonedReservationCount { get; init; }
        public long ReturnFailureCount { get; init; }
        public bool HistoryOverflowed { get; init; }
        public long LedgerFieldBytes { get; init; }
        public long BudgetAdmissionFieldBytes { get; init; }
        internal static Admission Empty(long budgetId, long capacity) => new()
        { BudgetId = budgetId, CapacityBytes = capacity, BudgetAdmissionFieldBytes = IntPtr.Size + 16L };
    }

    internal readonly record struct Reservation
    {
        public long BudgetId { get; init; }
        public long OwnerId { get; init; }
        public long BindingVersion { get; init; }
        public long AuthorityVersion { get; init; }
        public bool BindingIsActive { get; init; }
        public NativeMemoryReservationOutcome Outcome { get; init; }
        public int DeclaredLength { get; init; }
        public long RequestedBytes { get; init; }
        public long ReservedBytes { get; init; }
        public long OwnedBackingBytes { get; init; }
        public long PeakOwnedBackingBytes { get; init; }
        public bool BackingIsPrepared { get; init; }
        public long ReservationMoveCount { get; init; }
        public long BackingPreparationFailureCount { get; init; }
        public bool HasReservationReturnObligation { get; init; }
        public long ReturnFailureCount { get; init; }
        public bool HistoryOverflowed { get; init; }
        public long ControlFieldBytes { get; init; }
        internal static Reservation Pending(long budgetId, long ownerId, int length, long bytes) => new()
        {
            BudgetId = budgetId,
            OwnerId = ownerId,
            BindingVersion = 1,
            AuthorityVersion = 1,
            BindingIsActive = true,
            Outcome = NativeMemoryReservationOutcome.Pending,
            DeclaredLength = length,
            RequestedBytes = bytes,
            ReservedBytes = bytes,
            HasReservationReturnObligation = true,
            ControlFieldBytes = ReservationControlFieldBytes
        };
    }

    internal readonly record struct Unique
    {
        public long OwnerId { get; init; }
        public long AllocationId { get; init; }
        public long BindingVersion { get; init; }
        public long AuthorityVersion { get; init; }
        public bool BindingIsActive { get; init; }
        public NativeTransferLifecycle Lifecycle { get; init; }
        public int ActiveBorrowCount { get; init; }
        public int PeakBorrowCount { get; init; }
        public long MoveCount { get; init; }
        public int LiveUniqueOwnerCount { get; init; }
        public bool HasReturnObligation { get; init; }
        public long InitializedPayloadBytes { get; init; }
        public long PeakInitializedPayloadBytes { get; init; }
        public long OwnedBackingBytes { get; init; }
        public long BorrowedBackingBytes { get; init; }
        public long PeakOwnedBackingBytes { get; init; }
        public long PayloadReturnCount { get; init; }
        public long PayloadReturnFailureCount { get; init; }
        public bool HistoryOverflowed { get; init; }
        public long ControlFieldBytes { get; init; }
        internal static Unique Initial(long ownerId, long backingBytes, long initializedBytes) => new()
        {
            OwnerId = ownerId,
            AllocationId = ownerId,
            BindingVersion = 1,
            AuthorityVersion = 1,
            BindingIsActive = true,
            Lifecycle = NativeTransferLifecycle.Active,
            LiveUniqueOwnerCount = 1,
            HasReturnObligation = true,
            InitializedPayloadBytes = initializedBytes,
            PeakInitializedPayloadBytes = initializedBytes,
            OwnedBackingBytes = backingBytes,
            PeakOwnedBackingBytes = backingBytes,
            ControlFieldBytes = UniqueControlFieldBytes
        };
        internal Unique Returned() => this with
        {
            BindingIsActive = false,
            Lifecycle = NativeTransferLifecycle.Returned,
            LiveUniqueOwnerCount = 0,
            HasReturnObligation = false,
            InitializedPayloadBytes = 0,
            OwnedBackingBytes = 0,
            BorrowedBackingBytes = 0,
            PayloadReturnCount = 1
        };
    }
}
