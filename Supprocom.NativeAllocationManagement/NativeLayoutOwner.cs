using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Linear producer permission for a complete common-lifetime layout.</summary>
[StructLayout(LayoutKind.Sequential)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1815", Justification = "A linear producer capability has no value-equality contract or independently owning copies.")]
public readonly struct NativeLayoutReservation : IDisposable
{
    private readonly NativeLayoutControl? _control;
    private readonly long _authorityVersion;
    internal NativeLayoutReservation(NativeLayoutControl control, long authorityVersion) { _control = control; _authorityVersion = authorityVersion; }
    /// <summary>Gets the stable ownership identity, not initialized native authority.</summary>
    public long Id => Control(nameof(Id)).Id;
    /// <summary>Destructively moves permission without acquiring another control or charge.</summary>
    public static NativeLayoutReservation Move(ref NativeLayoutReservation? source)
    {
        NativeLayoutReservation observed = source ?? throw new ArgumentNullException(nameof(source));
        source = null;
        NativeLayoutControl control = observed.Control(nameof(Move));
        return new(control, control.MoveReservation(observed._authorityVersion));
    }
    /// <summary>Acquires the entire charged backing before production; failure preserves permission.</summary>
    public void PrepareBacking(CancellationToken cancellationToken = default) => Control(nameof(PrepareBacking)).PrepareBacking(_authorityVersion, cancellationToken);
    /// <summary>Consumes permission and publishes a unique owner only after every region is initialized.</summary>
    public static NativeLayoutOwner Activate<TState>(ref NativeLayoutReservation? source, TState state,
        NativeLayoutInitializer<TState> initializer, CancellationToken cancellationToken = default)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(initializer);
        NativeLayoutReservation observed = source ?? throw new ArgumentNullException(nameof(source));
        source = null;
        NativeLayoutControl control = observed.Control(nameof(Activate));
        long version = control.ActivateLayout(observed._authorityVersion, state, initializer, cancellationToken);
        return new(control, version);
    }
    /// <summary>Returns pending permission or its actually prepared unpublished block.</summary>
    public void Dispose() => Control(nameof(Dispose)).DisposeReservation(_authorityVersion);
    /// <summary>Samples actual descriptor/control state without reopening permission.</summary>
    public NativeLayoutStatistics CaptureSnapshot() => Control(nameof(CaptureSnapshot)).CaptureLayout(_authorityVersion);
    /// <summary>Retries a consumed terminal return without restoring producer authority.</summary>
    public bool TryCompletePayloadReturn() => Control(nameof(TryCompletePayloadReturn)).TryCompletePayloadReturn();
    internal object? ControlForTest => _control;
    private NativeLayoutControl Control(string operation) => _control ?? throw new NativeAllocationUninitializedException(nameof(NativeLayoutReservation), operation);
}

/// <summary>One unique backing owner for several initialized regions whose lifetimes coincide.</summary>
/// <remarks>A field has no independent release authority. Grouping unrelated lifetimes can retain more storage.</remarks>
[StructLayout(LayoutKind.Sequential)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1815", Justification = "A linear layout owner deliberately has no value-equality contract; assignments do not acquire ownership.")]
public readonly struct NativeLayoutOwner : IDisposable
{
    private readonly NativeLayoutControl? _control;
    private readonly long _authorityVersion;
    internal NativeLayoutOwner(NativeLayoutControl control, long authorityVersion) { _control = control; _authorityVersion = authorityVersion; }
    /// <summary>Gets stable backing-owner lineage.</summary>
    public long Id => Control(nameof(Id)).Id;
    /// <summary>Destructively moves unique authority and clears its source.</summary>
    public static NativeLayoutOwner Move(ref NativeLayoutOwner? source)
    {
        NativeLayoutOwner observed = source ?? throw new ArgumentNullException(nameof(source));
        source = null;
        NativeLayoutControl control = observed.Control(nameof(Move));
        return new(control, control.Move(observed._authorityVersion));
    }
    /// <summary>Processes typed fields in one bounded synchronous borrow, using explicit caller state.</summary>
    public TResult Read<TState, TResult>(TState state, NativeLayoutFunc<TState, TResult> action) where TState : allows ref struct =>
        Control(nameof(Read)).ReadLayout(_authorityVersion, state, action);
    /// <summary>Mutates typed fields in one bounded synchronous borrow, using explicit caller state.</summary>
    public void Access<TState>(TState state, NativeLayoutAction<TState> action) where TState : allows ref struct =>
        Control(nameof(Access)).AccessLayout(_authorityVersion, state, action);
    /// <summary>Copies a checked field into independently admitted unique ownership; the source is preserved on failure.</summary>
    /// <remarks>Temporary overlap is charged before copying. This is explicit copy, not a view or transfer of the source field.</remarks>
    public NativeTransfer<T> DetachField<T>(NativeLayoutField<T> field, NativeMemoryBudget budget,
        CancellationToken cancellationToken = default) where T : unmanaged =>
        Control(nameof(DetachField)).DetachField(_authorityVersion, field, budget, cancellationToken);
    /// <summary>Returns unique payload authority exactly once, rejecting stale or entered ownership.</summary>
    public void Dispose() => Control(nameof(Dispose)).Dispose(_authorityVersion);
    /// <summary>Samples actual layout/control metadata, available on stale and returned non-default aliases.</summary>
    public NativeLayoutStatistics CaptureSnapshot() => Control(nameof(CaptureSnapshot)).CaptureLayout(_authorityVersion);
    /// <summary>Retries consumed terminal cleanup without reopening unique authority.</summary>
    public bool TryCompletePayloadReturn() => Control(nameof(TryCompletePayloadReturn)).TryCompletePayloadReturn();
    internal object? ControlForTest => _control;
    private NativeLayoutControl Control(string operation) => _control ?? throw new NativeAllocationUninitializedException(nameof(NativeLayoutOwner), operation);
}

/// <summary>Sampled actual layout shape, ownership and explicit-copy evidence, not a cross-domain atomic transaction.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeLayoutStatistics
{
    /// <summary>Immutable descriptor identity.</summary>
    public long LayoutId { get; init; }
    /// <summary>Actual unique control state and complete backing extent; initialized bytes describe typed payload only.</summary>
    public NativeTransferStatistics Ownership { get; init; }
    /// <summary>Actual admission/control history over the same owner, not a second backing charge.</summary>
    public NativeMemoryReservationStatistics Reservation { get; init; }
    /// <summary>Declared regions, including empty fields.</summary>
    public int RegionCount { get; init; }
    /// <summary>Published initialized regions still held by the control; returned/unpublished owners have none. This does not grant authority.</summary>
    public int InitializedRegionCount { get; init; }
    /// <summary>Initialized typed bytes still held by the control, excluding padding/slack. This does not grant authority.</summary>
    public long LogicalInitializedBytes { get; init; }
    /// <summary>Immutable payload extent with inter-region padding.</summary>
    public int LayoutExtentBytes { get; init; }
    /// <summary>Immutable non-payload bytes inside the layout extent.</summary>
    public long InterRegionPaddingBytes { get; init; }
    /// <summary>Actual admitted base-alignment slack; not a hidden allocator header or RSS observation.</summary>
    public int AlignmentSlackBytes { get; init; }
    /// <summary>Actual aligned payload offset; zero before alignment preparation or for empty backing.</summary>
    public int PayloadOffsetBytes { get; init; }
    /// <summary>Required payload-base alignment in bytes.</summary>
    public int PayloadAlignment { get; init; }
    /// <summary>Whether payload alignment has actually been prepared, including for empty backing.</summary>
    public bool LayoutIsPrepared { get; init; }
    /// <summary>Whether all regions and padding completed initialization, independently of later cancellation/publication.</summary>
    public bool InitializationCompleted { get; init; }
    /// <summary>Retained descriptor fields and region-array elements, excluding CLR headers and the builder.</summary>
    public long DescriptorFieldBytes { get; init; }
    /// <summary>Actual bytes copied from this owner by NAM field detach, including copies whose later publication fails.</summary>
    public long CopiedBytes { get; init; }
    /// <summary>Successfully published independently owning field copies.</summary>
    public long DetachedOwnerCount { get; init; }
    /// <summary>Whether a real lifetime history saturated.</summary>
    public bool HistoryOverflowed { get; init; }
}
