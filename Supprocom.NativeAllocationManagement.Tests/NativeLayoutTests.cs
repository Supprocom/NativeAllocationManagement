using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeLayoutTests
{
    [Fact]
    public void ShapePreparationChecksCapacityDimensionsAndOverflowBeforeMutation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeLayoutBuilder(65));
        NativeLayoutBuilder builder = new(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Add<int>(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Add<int>(1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Add<int>(1, 128));
        Assert.Throws<OverflowException>(() => builder.Add<long>(int.MaxValue));
        NativeLayoutField<int> first = builder.Add<int>(1);
        NativeLayoutField<byte> second = builder.Add<byte>(1);
        Assert.Equal(0, first.Index);
        Assert.Equal(1, second.Index);
        Assert.Throws<InvalidOperationException>(() => builder.Add<byte>(0));
        NativeLayout layout = builder.Build();
        Assert.Same(layout, builder.Build());
        Assert.Equal(2, layout.RegionCount);
        Assert.Equal(5, layout.LogicalBytes);
        Assert.Throws<InvalidOperationException>(() => builder.Add<int>(0));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void ActualPayloadBaseAndEveryFieldRespectCheckedAlignment(int alignment)
    {
        NativeLayoutBuilder builder = new(2);
        NativeLayoutField<byte> bytes = builder.Add<byte>(3);
        NativeLayoutField<int> integers = builder.Add<int>(2, alignment);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(layout.BackingBytes);
        Assert.True(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        permission.Value.PrepareBacking();
        object control = permission.Value.ControlForTest!;
        NativeBlock block = (NativeBlock)typeof(NativeTransferControl<byte>).GetField("_block", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control)!;
        NativeLayoutStatistics prepared = permission.Value.CaptureSnapshot();
        nuint payload = (nuint)block.Pointer + (nuint)prepared.PayloadOffsetBytes;
        Assert.Equal(0u, payload % (nuint)alignment);
        Assert.Equal(0u, (payload + (nuint)layout.Describe(integers).OffsetBytes) % (nuint)alignment);
        Assert.Equal(layout.BackingBytes, budget.CaptureStatistics().CommittedBytes);
        Assert.True(prepared.LayoutIsPrepared);
        Assert.False(prepared.InitializationCompleted);
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        NativeLayoutOwner owner = NativeLayoutReservation.Activate(ref permission, (bytes, integers), static (writer, fields) =>
        {
            writer.Region(fields.bytes).Fill(3);
            writer.Region(fields.integers).Fill(17);
        });
        try
        {
            Assert.Same(control, owner.ControlForTest);
            Assert.Equal(37, owner.Read((bytes, integers), static (view, fields) => view.Region(fields.bytes)[0] + view.Region(fields.integers)[0] + view.Region(fields.integers)[1]));
            NativeLayoutStatistics actual = owner.CaptureSnapshot();
            Assert.Equal(11, actual.LogicalInitializedBytes);
            Assert.Equal(2, actual.InitializedRegionCount);
            Assert.Equal(layout.BackingBytes, actual.Ownership.OwnedBackingBytes);
            Assert.Equal(layout.BackingBytes - 11, NativeMemoryDiagnostics.Snapshot().StorageClearBytes - before.StorageClearBytes);
            Assert.Equal(0, actual.Ownership.MoveCount);
        }
        finally { owner.Dispose(); }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void CapacityRefusalPrecedesAnyLayoutControlPreparationAndAllocatesNothing()
    {
        NativeLayoutBuilder builder = new(1);
        builder.Add<int>(1);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(layout.BackingBytes - 1);
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(6);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = layout.TryReserve(budget, out NativeLayoutReservation? refused, out var reason);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(accepted);
        Assert.Null(refused);
        Assert.Equal(0, allocated);
        Assert.Equal(NativeMemoryAdmissionExhaustionReason.NativeByteCapacity, reason);
        NativeMemoryBudget available = new(layout.BackingBytes);
        Assert.Throws<InvalidOperationException>(() => layout.TryReserve(available, out _, out _));
        Assert.Equal(1, available.CaptureAdmissionStatistics().ControlPreparationFailureCount);
        Assert.Equal(0, available.CaptureStatistics().ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyShapesPublishRealOwnershipWithoutInventedNativeEvents(bool emptyField)
    {
        NativeLayoutBuilder builder = new(emptyField ? 1 : 0);
        NativeLayoutField<int> field = emptyField ? builder.Add<int>(0) : default;
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(0);
        Assert.True(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        using NativeLayoutOwner owner = NativeLayoutReservation.Activate(ref permission, 0, static (_, _) => { });
        Assert.Equal(emptyField ? 1 : 0, owner.CaptureSnapshot().InitializedRegionCount);
        Assert.True(owner.CaptureSnapshot().InitializationCompleted);
        Assert.Equal(0, owner.CaptureSnapshot().Ownership.OwnedBackingBytes);
        Assert.Equal(0, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(0, budget.CaptureStatistics().FreeCount);
        if (emptyField) Assert.Equal(0, owner.Read(field, static (view, token) => view.Region(token).Length));
    }

    [Fact]
    public void DefaultAndForeignFieldTokensFailBeforeAccessOrCopyAdmission()
    {
        NativeLayoutBuilder firstBuilder = new(1);
        NativeLayoutField<int> field = firstBuilder.Add<int>(1);
        NativeLayout first = firstBuilder.Build();
        NativeLayoutBuilder foreignBuilder = new(1);
        NativeLayoutField<int> foreign = foreignBuilder.Add<int>(1);
        NativeLayout second = foreignBuilder.Build();
        Assert.Throws<ArgumentException>(() => second.Describe(field));
        Assert.Throws<ArgumentException>(() => first.Describe(default(NativeLayoutField<int>)));
        NativeMemoryBudget budget = new(first.BackingBytes);
        Assert.True(first.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        using NativeLayoutOwner owner = NativeLayoutReservation.Activate(ref permission, field, static (writer, token) => writer.Region(token).Write(17));
        Assert.Throws<ArgumentException>(() => owner.Read(foreign, static (view, token) => view.Region(token)[0]));
        NativeMemoryBudget destination = new(4);
        Assert.Throws<ArgumentException>(() => owner.DetachField(foreign, destination));
        Assert.Equal(0, destination.CaptureAdmissionStatistics().AdmittedReservationCount);
    }

    [Fact]
    public void MissingRegionInitializationConsumesPermissionAndReturnsCompleteBacking()
    {
        NativeLayoutBuilder builder = new(2);
        NativeLayoutField<int> first = builder.Add<int>(2);
        builder.Add<long>(1);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(layout.BackingBytes);
        Assert.True(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        NativeLayoutReservation alias = permission.Value;
        Assert.Throws<InvalidOperationException>(() => NativeLayoutReservation.Activate(ref permission, first,
            static (writer, field) => writer.Region(field).Fill(17)));
        Assert.Null(permission);
        Assert.False(alias.CaptureSnapshot().InitializationCompleted);
        Assert.Equal(0, alias.CaptureSnapshot().LogicalInitializedBytes);
        Assert.Equal(NativeMemoryReservationOutcome.InitializationFailed, alias.CaptureSnapshot().Reservation.Outcome);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().InitializationFailureCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void CancellationAfterAllWritesDoesNotPublishOrInventInitializationFailure()
    {
        NativeLayoutBuilder builder = new(1);
        NativeLayoutField<int> field = builder.Add<int>(1);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(layout.BackingBytes);
        Assert.True(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        NativeLayoutReservation alias = permission.Value;
        using CancellationTokenSource cancellation = new();
        Assert.Throws<OperationCanceledException>(() => NativeLayoutReservation.Activate(ref permission, (field, cancellation), static (writer, state) =>
        {
            writer.Region(state.field).Write(17);
            state.cancellation.Cancel();
        }, cancellation.Token));
        Assert.Null(permission);
        Assert.True(alias.CaptureSnapshot().InitializationCompleted);
        Assert.Equal(0, alias.CaptureSnapshot().InitializedRegionCount);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().ActivationCount);
        Assert.Equal(0, budget.CaptureAdmissionStatistics().InitializationFailureCount);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().CancelledReservationCount);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void WarmPreparedActivationAccessAndRepeatedMovesAllocateNoWrappers()
    {
        NativeLayoutBuilder builder = new(1);
        NativeLayoutField<int> field = builder.Add<int>(1);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(layout.BackingBytes * 2L);
        using NativeLayoutOwner warm = Create(layout, field, budget);
        _ = warm.Read(field, static (view, token) => view.Region(token)[0]);
        Assert.True(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        permission.Value.PrepareBacking();
        NativeLayoutInitializer<NativeLayoutField<int>> initializer = static (writer, token) => writer.Region(token).Write(17);
        NativeLayoutFunc<NativeLayoutField<int>, int> read = static (view, token) => view.Region(token)[0];
        NativeLayoutOwner current;
        long before = GC.GetAllocatedBytesForCurrentThread();
        current = NativeLayoutReservation.Activate(ref permission, field, initializer);
        long activationBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        NativeLayoutOwner? moving = current;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            current = NativeLayoutOwner.Move(ref moving);
            moving = current;
            _ = current.Read(field, read);
        }
        long operationBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        try
        {
            Assert.Equal(0, activationBytes);
            Assert.Equal(0, operationBytes);
            Assert.Equal(10_000, current.CaptureSnapshot().Ownership.MoveCount);
            Assert.Equal(17, current.Read(field, read));
        }
        finally { current.Dispose(); }
    }

    [Fact]
    public void DetachChargesTemporaryOverlapCopiesExactlyAndPreservesTheSourceOnFailure()
    {
        NativeLayoutBuilder builder = new(1);
        NativeLayoutField<int> field = builder.Add<int>(2);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(layout.BackingBytes + 8L);
        NativeLayoutOwner owner = Create(layout, field, budget);
        NativeMemoryBudget full = new(7);
        Assert.Throws<NativeMemoryBudgetExceededException>(() => owner.DetachField(field, full));
        Assert.Equal(0, owner.CaptureSnapshot().CopiedBytes);
        Assert.Equal(0, full.CaptureStatistics().ReservedBytes);
        NativeMemoryTestHooks.FailNextAllocation();
        Assert.Throws<NativeAllocationFailedException>(() => owner.DetachField(field, budget));
        Assert.Equal(layout.BackingBytes, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        NativeTransfer<int> detached = owner.DetachField(field, budget);
        try
        {
            Assert.Equal(layout.BackingBytes + 8L, budget.CaptureStatistics().PeakAdmittedBytes);
            Assert.Equal(8, owner.CaptureSnapshot().CopiedBytes);
            Assert.Equal(8, NativeMemoryDiagnostics.Snapshot().CopiedBytes - before.CopiedBytes);
            Assert.Equal(1, owner.CaptureSnapshot().DetachedOwnerCount);
            owner.Dispose();
            Assert.Equal(8, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(17, detached.Read(static view => view[1]));
        }
        finally { detached.Dispose(); }
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
    }

    [Fact]
    public void FailedConsumedLayoutReturnIsChargedAndRetryDoesNotRestoreAuthority()
    {
        NativeLayoutBuilder builder = new(1);
        NativeLayoutField<int> field = builder.Add<int>(1);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(layout.BackingBytes);
        Assert.True(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        NativeLayoutReservation alias = permission.Value;
        Assert.Throws<AggregateException>(() => NativeLayoutReservation.Activate(ref permission, field, static (_, _) =>
        {
            NativeMemoryTestHooks.FailAtManagedPublicationBoundary(4);
            throw new InvalidOperationException("layout producer failed");
        }));
        Assert.Null(permission);
        Assert.Equal(layout.BackingBytes, budget.CaptureStatistics().CommittedBytes);
        Assert.True(alias.CaptureSnapshot().Reservation.HasReservationReturnObligation);
        Assert.True(alias.TryCompletePayloadReturn());
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Throws<InvalidOperationException>(alias.Dispose);
    }

    [Fact]
    public void MovesRejectStaleAliasesAndEnteredReleaseWithoutLosingShape()
    {
        NativeLayoutBuilder builder = new(1);
        NativeLayoutField<int> field = builder.Add<int>(1);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(layout.BackingBytes);
        NativeLayoutOwner? source = Create(layout, field, budget);
        NativeLayoutOwner stale = source.Value;
        NativeLayoutOwner current = NativeLayoutOwner.Move(ref source);
        Assert.Null(source);
        Assert.Throws<InvalidOperationException>(() => stale.Read(field, static (view, token) => view.Region(token)[0]));
        Assert.Throws<InvalidOperationException>(stale.Dispose);
        try
        {
            _ = current.Read(0, (view, _) =>
            {
                Assert.Throws<InvalidOperationException>(current.Dispose);
                return view.Region(field)[0];
            });
            Assert.Equal(layout.Id, current.CaptureSnapshot().LayoutId);
            Assert.Equal(1, stale.CaptureSnapshot().Ownership.MoveCount);
        }
        finally { current.Dispose(); }
        Assert.Equal(0, stale.CaptureSnapshot().Ownership.OwnedBackingBytes);
        Assert.Equal(0, stale.CaptureSnapshot().InitializedRegionCount);
    }

    [Fact]
    public void TraceAndFieldMeasurementsDescribeActualPreparedInitializedAndCopiedTransitions()
    {
        NativeLayoutBuilder builder = new(1);
        NativeLayoutField<int> field = builder.Add<int>(1, 16);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(layout.BackingBytes + 4L, 32);
        NativeLayoutOwner owner = Create(layout, field, budget);
        using NativeTransfer<int> detached = owner.DetachField(field, budget);
        NativeLayoutStatistics actual = owner.CaptureSnapshot();
        long controlFields = 0;
        for (Type? type = owner.ControlForTest!.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            controlFields += type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Sum(static member => FieldBytes(member.FieldType));
        Assert.Equal(controlFields, actual.Ownership.ControlFieldBytes);
        Assert.Equal(layout.MetadataFieldBytes, actual.DescriptorFieldBytes);
        Span<NativeMemoryTraceEvent> events = stackalloc NativeMemoryTraceEvent[32];
        int count = budget.CopyTraceTo(events);
        NativeMemoryTraceEvent[] recorded = events[..count].ToArray();
        Assert.Contains(recorded, entry => entry.Kind == NativeMemoryTraceKind.LayoutPrepared && entry.OwnerId == owner.Id && entry.CorrelationId == layout.Id);
        Assert.Contains(recorded, entry => entry.Kind == NativeMemoryTraceKind.LayoutInitialized && entry.RequestedBytes == 4 && entry.CorrelationId == layout.Id);
        Assert.Contains(recorded, entry => entry.Kind == NativeMemoryTraceKind.Detached && entry.OwnerId == owner.Id && entry.CorrelationId == detached.Id);
        owner.Dispose();
    }

    [Fact]
    public void FirstPreparedCallerStateSpecializationNeedsNoNamDelegateAdapter()
    {
        NativeLayoutBuilder builder = new(1);
        NativeLayoutField<int> field = builder.Add<int>(1);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(layout.BackingBytes);
        Assert.True(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        permission.Value.PrepareBacking();
        NativeLayoutInitializer<FirstCallerState> initialize = static (writer, state) => writer.Region(state.Field).Write(23);
        long before = GC.GetAllocatedBytesForCurrentThread();
        NativeLayoutOwner owner = NativeLayoutReservation.Activate(ref permission, new FirstCallerState(field), initialize);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        try
        {
            Assert.Equal(0, allocated);
            Assert.Equal(23, owner.Read(field, static (view, token) => view.Region(token)[0]));
        }
        finally { owner.Dispose(); }
    }

    [Fact]
    public void ActualCopyRemainsMeasuredWhenDestinationPublicationFails()
    {
        NativeLayoutBuilder builder = new(1);
        NativeLayoutField<int> field = builder.Add<int>(1);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(layout.BackingBytes + 4L);
        using NativeLayoutOwner owner = Create(layout, field, budget);
        NativeMemoryStatistics before = NativeMemoryDiagnostics.Snapshot();
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(8);
        Assert.Throws<InvalidOperationException>(() => owner.DetachField(field, budget));
        Assert.Equal(4, owner.CaptureSnapshot().CopiedBytes);
        Assert.Equal(4, NativeMemoryDiagnostics.Snapshot().CopiedBytes - before.CopiedBytes);
        Assert.Equal(0, owner.CaptureSnapshot().DetachedOwnerCount);
        Assert.Equal(layout.BackingBytes, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(0, budget.CaptureStatistics().ReservedBytes);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().InitializationFailureCount);
        Assert.Equal(17, owner.Read(field, static (view, token) => view.Region(token)[0]));
    }

    [Fact]
    public void PackedAndSimdShapesUseRuntimeAlignmentAndRetainTheirTypedContents()
    {
        NativeLayoutBuilder builder = new(2);
        NativeLayoutField<PackedNine> packed = builder.Add<PackedNine>(2);
        NativeLayoutField<System.Runtime.Intrinsics.Vector256<long>> vectors = builder.Add<System.Runtime.Intrinsics.Vector256<long>>(1, 32);
        NativeLayout layout = builder.Build();
        Assert.Equal(1, layout.Describe(packed).Alignment);
        Assert.Equal(9, layout.Describe(packed).ElementBytes);
        Assert.Equal(32, layout.Describe(vectors).Alignment);
        NativeMemoryBudget budget = new(layout.BackingBytes);
        Assert.True(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        using NativeLayoutOwner owner = NativeLayoutReservation.Activate(ref permission, (packed, vectors), static (writer, fields) =>
        {
            writer.Region(fields.packed).Fill(new(3, 17));
            writer.Region(fields.vectors).Write(System.Runtime.Intrinsics.Vector256.Create(23L));
        });
        Assert.Equal(17, owner.Read(packed, static (view, field) => view.Region(field)[1].Value));
        Assert.Equal(23, owner.Read(vectors, static (view, field) => System.Runtime.Intrinsics.Vector256.GetElement(view.Region(field)[0], 3)));
    }

    [Fact]
    public void DescriptorAndFieldTokensDoNotRootAbandonedLayoutPayload()
    {
        NativeLayoutBuilder builder = new(1);
        NativeLayoutField<int> field = builder.Add<int>(1024);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(layout.BackingBytes);
        WeakReference control = Abandon(layout, field, budget);
        for (int index = 0; index < 8 && control.IsAlive; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(control.IsAlive);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
        Assert.Equal(1024, layout.Describe(field).Length);
        GC.KeepAlive(layout);
        GC.KeepAlive(budget);
    }

    [Fact]
    public void InvalidDefaultViewsAndForgedWrongTypeTokensFailBeforeAddressUse()
    {
        Assert.Throws<NativeAllocationUninitializedException>(static () =>
        {
            NativeLayoutView view = default;
            _ = view.Region(default(NativeLayoutField<int>));
        });
        Assert.Throws<NativeAllocationUninitializedException>(static () =>
        {
            NativeLayoutWriter writer = default;
            _ = writer.Region(default(NativeLayoutField<int>));
        });
        Assert.Throws<NativeAllocationUninitializedException>(static () => default(NativeLayoutOwner).CaptureSnapshot());
        Assert.Throws<NativeAllocationUninitializedException>(static () => default(NativeLayoutReservation).PrepareBacking());
        NativeLayoutBuilder builder = new(1);
        NativeLayoutField<int> field = builder.Add<int>(1);
        NativeLayout layout = builder.Build();
        NativeLayoutField<long> forged = Unsafe.As<NativeLayoutField<int>, NativeLayoutField<long>>(ref field);
        Assert.Throws<ArgumentException>(() => layout.Describe(forged));
    }

    [Fact]
    public void FailedBackingObservationRetainsTheActualChargeAndAlignmentCanPrepareWithoutAnotherAllocation()
    {
        NativeLayoutBuilder builder = new(1);
        builder.Add<int>(1, 64);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(layout.BackingBytes);
        Assert.True(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        NativeMemoryTestHooks.FailAtManagedPublicationBoundary(7);
        Assert.Throws<InvalidOperationException>(() => permission.Value.PrepareBacking());
        Assert.True(permission.Value.CaptureSnapshot().Reservation.BackingIsPrepared);
        Assert.False(permission.Value.CaptureSnapshot().LayoutIsPrepared);
        Assert.Equal(layout.BackingBytes, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        permission.Value.PrepareBacking();
        Assert.True(permission.Value.CaptureSnapshot().LayoutIsPrepared);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(1, budget.CaptureAdmissionStatistics().BackingPreparationFailureCount);
        permission.Value.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
    }

    [Fact]
    public void RealCopiedAndDetachedHistoriesSaturateWithoutChangingBackingOrAuthority()
    {
        NativeLayoutBuilder builder = new(1);
        NativeLayoutField<int> field = builder.Add<int>(1);
        NativeLayout layout = builder.Build();
        NativeMemoryBudget budget = new(layout.BackingBytes + 4L);
        using NativeLayoutOwner owner = Create(layout, field, budget);
        object control = owner.ControlForTest!;
        typeof(NativeLayoutControl).GetField("_copiedBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(control, long.MaxValue);
        typeof(NativeLayoutControl).GetField("_detachedOwners", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(control, long.MaxValue);
        using NativeTransfer<int> copy = owner.DetachField(field, budget);
        Assert.Equal(long.MaxValue, owner.CaptureSnapshot().CopiedBytes);
        Assert.Equal(long.MaxValue, owner.CaptureSnapshot().DetachedOwnerCount);
        Assert.True(owner.CaptureSnapshot().HistoryOverflowed);
        Assert.Equal(layout.BackingBytes + 4L, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(17, copy.Read(static view => view[0]));
        Assert.True(owner.CaptureSnapshot().Ownership.BindingIsActive);
    }

    private readonly record struct FirstCallerState(NativeLayoutField<int> Field);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    private readonly record struct PackedNine(byte Prefix, long Value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon(NativeLayout layout, NativeLayoutField<int> field, NativeMemoryBudget budget)
    {
        NativeLayoutOwner owner = Create(layout, field, budget);
        return new WeakReference(owner.ControlForTest!);
    }

    private static long FieldBytes(Type type) => !type.IsValueType ? IntPtr.Size
        : type == typeof(long) ? sizeof(long)
        : type == typeof(int) || type.IsEnum ? sizeof(int)
        : type == typeof(bool) ? sizeof(bool)
        : type == typeof(NativeBlock) ? Unsafe.SizeOf<NativeBlock>()
        : throw new InvalidOperationException($"Unaccounted layout control field: {type}.");

    private static NativeLayoutOwner Create(NativeLayout layout, NativeLayoutField<int> field, NativeMemoryBudget budget)
    {
        Assert.True(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _));
        return NativeLayoutReservation.Activate(ref permission, field, static (writer, token) => writer.Region(token).Fill(17));
    }
}
