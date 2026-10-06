using System.Reflection;
using System.Runtime.InteropServices;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Conformance;

// Independent declared shapes and complete nested state. This reflection is
// conformance work, never an allocator path or an allocation measurement.
internal static class NativeLayoutDiagnosticOracle
{
    internal static long ControlFieldBytes => NativeAdmissionDiagnosticOracle.ReservationControlFieldBytes
        + IntPtr.Size + sizeof(int) + 2L * sizeof(long) + 3L * sizeof(bool);
    internal static long DescriptorFieldBytes(int count) => IntPtr.Size + 2L * sizeof(long) + 2L * sizeof(int)
        + count * (IntPtr.Size + 4L * sizeof(int));

    internal static void Run(int traceCapacity)
    {
        NativeLayoutBuilder builder = new(2);
        NativeLayoutField<byte> first = builder.Add<byte>(3);
        NativeLayoutField<byte> second = builder.Add<byte>(2);
        NativeLayout layout = builder.Build();
        NativeAdmissionDiagnosticOracle.Verify(layout.Describe(first), new Region(layout.Id, 0, 3, 1, 0, 3, 1), "first-field");
        NativeAdmissionDiagnosticOracle.Verify(layout.Describe(second), new Region(layout.Id, 1, 2, 1, 3, 2, 1), "second-field");
        NativeMemoryBudget budget = new(8, traceCapacity);
        if (!layout.TryReserve(budget, out NativeLayoutReservation? permission, out _))
            throw new InvalidOperationException("Declared layout permission was refused.");
        Statistics expected = Pending(layout.Id, budget.Id, permission.Value.Id, 2, 5, 5, 1);
        try
        {
            Verify(permission.Value.CaptureSnapshot(), expected, "pending");
            permission.Value.PrepareBacking();
            expected = Prepared(expected);
            Verify(permission.Value.CaptureSnapshot(), expected, "prepared");
            NativeLayoutOwner? source = NativeLayoutReservation.Activate(ref permission, (first, second), static (writer, fields) =>
            {
                writer.Region(fields.first).Fill(17);
                writer.Region(fields.second).Fill(19);
            });
            expected = Activated(expected, 5);
            try
            {
                Verify(source.Value.CaptureSnapshot(), expected, "activated");
                NativeLayoutOwner moved = NativeLayoutOwner.Move(ref source);
                expected = expected with
                {
                    Ownership = expected.Ownership with { BindingVersion = 2, AuthorityVersion = 2, MoveCount = 1 },
                    Reservation = expected.Reservation with { BindingVersion = 2, AuthorityVersion = 2 }
                };
                try
                {
                    Verify(moved.CaptureSnapshot(), expected, "moved");
                    if (moved.Read((first, second), static (view, fields) => view.Region(fields.first)[0] + view.Region(fields.second)[1]) != 36)
                        throw new InvalidOperationException("Typed initialized output differs.");
                    expected = expected with { Ownership = expected.Ownership with { PeakBorrowCount = 1 } };
                    Verify(moved.CaptureSnapshot(), expected, "read-ended");
                    NativeTransfer<byte> copy = moved.DetachField(first, budget);
                    try
                    {
                        expected = expected with { CopiedBytes = 3, DetachedOwnerCount = 1 };
                        Verify(moved.CaptureSnapshot(), expected, "detached");
                        if (budget.CaptureStatistics().CommittedBytes != 8 || copy.Read(static view => view[0] + view[2]) != 34)
                            throw new InvalidOperationException("Independent copy or charged overlap differs.");
                    }
                    finally { copy.Dispose(); }
                    if (budget.CaptureStatistics().CommittedBytes != 5)
                        throw new InvalidOperationException("Returning the copy altered source charge.");
                }
                finally { moved.Dispose(); }
                Verify(moved.CaptureSnapshot(), Returned(expected), "returned");
            }
            finally { source?.Dispose(); }
        }
        finally { permission?.Dispose(); }
        NativeMemoryBudgetStatistics terminal = budget.CaptureStatistics();
        if (terminal.CommittedBytes != 0 || terminal.ReservedBytes != 0 || terminal.PeakCommittedBytes != 8
            || terminal.AllocationCount != 2 || terminal.FreeCount != 2)
            throw new InvalidOperationException("Physical layout/copy cleanup differs.");
    }

    internal static void RunEmpty(int traceCapacity, bool emptyField)
    {
        NativeLayoutBuilder builder = new(emptyField ? 1 : 0);
        NativeLayoutField<byte> field = emptyField ? builder.Add<byte>(0) : default;
        NativeLayout layout = builder.Build();
        if (emptyField)
            NativeAdmissionDiagnosticOracle.Verify(layout.Describe(field), new Region(layout.Id, 0, 0, 1, 0, 0, 1), "empty-field");
        NativeMemoryBudget budget = new(0, traceCapacity);
        if (!layout.TryReserve(budget, out NativeLayoutReservation? permission, out _))
            throw new InvalidOperationException("Empty ownership permission was refused.");
        Statistics expected = Pending(layout.Id, budget.Id, permission.Value.Id, emptyField ? 1 : 0, 0, 0, 1);
        try
        {
            Verify(permission.Value.CaptureSnapshot(), expected, "empty-pending");
            permission.Value.PrepareBacking();
            expected = Prepared(expected);
            Verify(permission.Value.CaptureSnapshot(), expected, "empty-prepared");
            NativeLayoutOwner owner = NativeLayoutReservation.Activate(ref permission, (field, emptyField), static (writer, state) =>
            {
                if (state.emptyField) writer.Region(state.field).Fill(0);
            });
            expected = Activated(expected, 0);
            try { Verify(owner.CaptureSnapshot(), expected, "empty-active"); }
            finally { owner.Dispose(); }
            Verify(owner.CaptureSnapshot(), Returned(expected), "empty-returned");
        }
        finally { permission?.Dispose(); }
        NativeMemoryBudgetStatistics terminal = budget.CaptureStatistics();
        if (terminal.CommittedBytes != 0 || terminal.ReservedBytes != 0 || terminal.AllocationCount != 0 || terminal.FreeCount != 0)
            throw new InvalidOperationException("Empty ownership fabricated backing acquisition or release.");
    }

    internal static Statistics Pending(long layoutId, long budgetId, long ownerId, int count, int extent, long logical, int alignment) => new()
    {
        LayoutId = layoutId,
        Ownership = new()
        {
            OwnerId = ownerId,
            AllocationId = ownerId,
            BindingVersion = 1,
            AuthorityVersion = 1,
            Lifecycle = NativeTransferLifecycle.Uninitialized,
            HasReturnObligation = true,
            ControlFieldBytes = ControlFieldBytes
        },
        Reservation = NativeAdmissionDiagnosticOracle.Reservation.Pending(budgetId, ownerId, extent == 0 ? 0 : extent + alignment - 1,
            extent == 0 ? 0 : extent + alignment - 1) with
        { ControlFieldBytes = ControlFieldBytes },
        RegionCount = count,
        LayoutExtentBytes = extent,
        InterRegionPaddingBytes = extent - logical,
        AlignmentSlackBytes = extent == 0 ? 0 : alignment - 1,
        PayloadAlignment = alignment,
        DescriptorFieldBytes = DescriptorFieldBytes(count)
    };

    internal static Statistics Prepared(Statistics expected, int offset = 0)
    {
        long backing = expected.LayoutExtentBytes + expected.AlignmentSlackBytes;
        return expected with
        {
            LayoutIsPrepared = true,
            PayloadOffsetBytes = offset,
            Ownership = expected.Ownership with { OwnedBackingBytes = backing, PeakOwnedBackingBytes = backing },
            Reservation = expected.Reservation with
            {
                Outcome = NativeMemoryReservationOutcome.Prepared,
                ReservedBytes = 0,
                OwnedBackingBytes = backing,
                PeakOwnedBackingBytes = backing,
                BackingIsPrepared = true
            }
        };
    }

    internal static Statistics Activated(Statistics expected, long logical) => expected with
    {
        InitializedRegionCount = expected.RegionCount,
        LogicalInitializedBytes = logical,
        InitializationCompleted = true,
        Ownership = expected.Ownership with
        {
            BindingIsActive = true,
            Lifecycle = NativeTransferLifecycle.Active,
            LiveUniqueOwnerCount = 1,
            InitializedPayloadBytes = logical,
            PeakInitializedPayloadBytes = logical
        },
        Reservation = expected.Reservation with
        { BindingIsActive = false, Outcome = NativeMemoryReservationOutcome.Activated, HasReservationReturnObligation = false }
    };

    internal static Statistics Returned(Statistics expected) => expected with
    {
        InitializedRegionCount = 0,
        LogicalInitializedBytes = 0,
        Ownership = expected.Ownership.Returned(),
        Reservation = expected.Reservation with { OwnedBackingBytes = 0 }
    };

    internal static void Verify(NativeLayoutStatistics actual, Statistics expected, string stage)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        PropertyInfo[] fields = typeof(NativeLayoutStatistics).GetProperties(flags);
        if (fields.Length != typeof(Statistics).GetProperties(flags).Length)
            throw new InvalidOperationException(stage + ": layout property set differs.");
        foreach (PropertyInfo field in fields)
        {
            PropertyInfo? model = typeof(Statistics).GetProperty(field.Name, flags);
            if (model is null) throw new InvalidOperationException(stage + ": missing model field " + field.Name);
            if (string.Equals(field.Name, nameof(NativeLayoutStatistics.Ownership), StringComparison.Ordinal))
            {
                if (field.PropertyType != typeof(NativeTransferStatistics) || model.PropertyType != typeof(NativeAdmissionDiagnosticOracle.Unique))
                    throw new InvalidOperationException(stage + ": ownership schema differs.");
                NativeAdmissionDiagnosticOracle.Verify(actual.Ownership, expected.Ownership, stage + ":unique");
            }
            else if (string.Equals(field.Name, nameof(NativeLayoutStatistics.Reservation), StringComparison.Ordinal))
            {
                if (field.PropertyType != typeof(NativeMemoryReservationStatistics) || model.PropertyType != typeof(NativeAdmissionDiagnosticOracle.Reservation))
                    throw new InvalidOperationException(stage + ": reservation schema differs.");
                NativeAdmissionDiagnosticOracle.Verify(actual.Reservation, expected.Reservation, stage + ":permission");
            }
            else if (field.PropertyType != model.PropertyType || !Equals(field.GetValue(actual), model.GetValue(expected)))
                throw new InvalidOperationException($"{stage}: {field.Name} was {field.GetValue(actual)}, expected {model.GetValue(expected)}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Region(long LayoutId, int Index, int Length, int ElementBytes, int OffsetBytes, int PayloadBytes, int Alignment);

    internal readonly record struct Statistics
    {
        public long LayoutId { get; init; }
        public NativeAdmissionDiagnosticOracle.Unique Ownership { get; init; }
        public NativeAdmissionDiagnosticOracle.Reservation Reservation { get; init; }
        public int RegionCount { get; init; }
        public int InitializedRegionCount { get; init; }
        public long LogicalInitializedBytes { get; init; }
        public int LayoutExtentBytes { get; init; }
        public long InterRegionPaddingBytes { get; init; }
        public int AlignmentSlackBytes { get; init; }
        public int PayloadOffsetBytes { get; init; }
        public int PayloadAlignment { get; init; }
        public bool LayoutIsPrepared { get; init; }
        public bool InitializationCompleted { get; init; }
        public long DescriptorFieldBytes { get; init; }
        public long CopiedBytes { get; init; }
        public long DetachedOwnerCount { get; init; }
        public bool HistoryOverflowed { get; init; }
    }
}
