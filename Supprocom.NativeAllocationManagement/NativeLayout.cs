using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Prepares a bounded, reusable shape for unmanaged regions with one common lifetime.</summary>
public sealed class NativeLayoutBuilder
{
    /// <summary>The maximum number of independently initialized regions in a layout.</summary>
    public const int MaximumRegionCount = 64;
    private readonly NativeLayoutRegion[] _regions;
    private readonly long _id = NativeOwnerIdentity.NextWithoutPreparation();
    private int _count;
    private int _extent;
    private int _alignment = 1;
    private long _logicalBytes;
    private NativeLayout? _frozen;

    /// <summary>Prepares fixed metadata capacity; this builder is not thread-safe.</summary>
    public NativeLayoutBuilder(int regionCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(regionCapacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(regionCapacity, MaximumRegionCount);
        _regions = new NativeLayoutRegion[regionCapacity];
    }

    /// <summary>Adds one region with checked dimensions and optional power-of-two alignment, at most 64 bytes.</summary>
    public NativeLayoutField<T> Add<T>(int length, int alignment = 0) where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (_frozen is not null) throw new InvalidOperationException("A frozen layout cannot change shape.");
        if (_count == _regions.Length) throw new InvalidOperationException("The prepared layout metadata capacity is exhausted.");
        AlignmentProbe<T> probe = new() { Prefix = 0, Value = default };
        int natural = checked((int)Unsafe.ByteOffset(ref probe.Prefix, ref Unsafe.As<T, byte>(ref probe.Value)));
        int effective = alignment == 0 ? natural : alignment;
        if (effective < natural || effective > 64 || (effective & (effective - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be a power of two between the type's alignment and 64 bytes.");
        int bytes = checked(length * Unsafe.SizeOf<T>());
        int offset = checked(_extent + effective - 1) & ~(effective - 1);
        int extent = checked(offset + bytes);
        int maximumAlignment = Math.Max(_alignment, effective);
        // Validate the complete future backing extent before mutating the shape.
        _ = extent == 0 ? 0 : checked(extent + maximumAlignment - 1);
        long logical = checked(_logicalBytes + bytes);
        NativeLayoutField<T> field = new(_id, _count);
        _regions[_count++] = new(typeof(T).TypeHandle, length, offset, bytes, effective);
        _extent = extent;
        _alignment = maximumAlignment;
        _logicalBytes = logical;
        return field;
    }

    /// <summary>Freezes one immutable compact descriptor; repeated calls return the same descriptor.</summary>
    public NativeLayout Build() => _frozen ??= new NativeLayout(_id, _regions.AsSpan(0, _count).ToArray(),
        _extent, _alignment, _logicalBytes);

    // The actual sequential field offset establishes this runtime's alignment;
    // size-based guesses can miss SIMD or specially packed unmanaged shapes.
    [StructLayout(LayoutKind.Sequential)]
    private struct AlignmentProbe<T> where T : unmanaged
    {
        internal byte Prefix;
        internal T Value;
    }
}

/// <summary>A typed descriptor token, not a pointer, owner, or lifetime grant.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeLayoutField<T> where T : unmanaged
{
    internal NativeLayoutField(long layoutId, int index) { LayoutId = layoutId; Index = index; }
    /// <summary>Gets the immutable descriptor identity; zero identifies an invalid default token.</summary>
    public long LayoutId { get; }
    /// <summary>Gets the region ordinal in that descriptor.</summary>
    public int Index { get; }
}

/// <summary>An immutable checked shape reused by unique common-lifetime owners.</summary>
public sealed class NativeLayout
{
    private readonly NativeLayoutRegion[] _regions;
    internal NativeLayout(long id, NativeLayoutRegion[] regions, int extent, int alignment, long logicalBytes)
    {
        Id = id; _regions = regions; ExtentBytes = extent; Alignment = alignment; LogicalBytes = logicalBytes;
    }
    /// <summary>Gets descriptor identity, independent of any native owner.</summary>
    public long Id { get; }
    /// <summary>Gets the number of declared regions, including zero-length regions.</summary>
    public int RegionCount => _regions.Length;
    /// <summary>Gets initialized typed payload bytes, excluding layout padding and alignment slack.</summary>
    public long LogicalBytes { get; }
    /// <summary>Gets the checked payload extent including inter-region padding.</summary>
    public int ExtentBytes { get; }
    /// <summary>Gets the required payload-base alignment in bytes.</summary>
    public int Alignment { get; }
    /// <summary>Gets the complete admitted block extent, including worst-case base-alignment slack.</summary>
    public int BackingBytes => ExtentBytes == 0 ? 0 : checked(ExtentBytes + Alignment - 1);
    /// <summary>Gets declared descriptor fields and region-array element storage, excluding CLR headers/padding and the builder.</summary>
    public long MetadataFieldBytes => IntPtr.Size + sizeof(long) * 2L + sizeof(int) * 2L
        + (long)_regions.Length * Unsafe.SizeOf<NativeLayoutRegion>();

    /// <summary>Describes a checked typed region without accessing or retaining any native payload.</summary>
    public NativeLayoutRegionStatistics Describe<T>(NativeLayoutField<T> field) where T : unmanaged
    {
        NativeLayoutRegion region = Region(field);
        return new(Id, field.Index, region.Length, Unsafe.SizeOf<T>(), region.Offset, region.Bytes, region.Alignment);
    }

    /// <summary>Admits complete backing and one ownership control before any region producer runs.</summary>
    public bool TryReserve(NativeMemoryBudget budget,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NativeLayoutReservation? reservation,
        out NativeMemoryAdmissionExhaustionReason reason)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (!budget.TryReserveLayout(this, out NativeMemoryReservation<byte>? permission, out reason))
        {
            reservation = null;
            return false;
        }
        reservation = new((NativeLayoutControl)permission.Value.ControlForTest!, authorityVersion: 1);
        return true;
    }

    internal NativeLayoutRegion Region<T>(NativeLayoutField<T> field) where T : unmanaged
    {
        if (field.LayoutId != Id || (uint)field.Index >= (uint)_regions.Length)
            throw new ArgumentException("The field does not belong to this layout.", nameof(field));
        NativeLayoutRegion region = _regions[field.Index];
        if (!region.Type.Equals(typeof(T).TypeHandle))
            throw new ArgumentException("The field element type does not match its layout region.", nameof(field));
        return region;
    }

    internal NativeLayoutRegion Region(int index) => _regions[index];
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeLayoutRegion(RuntimeTypeHandle Type, int Length, int Offset, int Bytes, int Alignment);

/// <summary>Immutable descriptor facts for one checked typed field, not native-use authority.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeLayoutRegionStatistics(
    long LayoutId, int Index, int Length, int ElementBytes, int OffsetBytes, int PayloadBytes, int Alignment);

/// <summary>Initializes all typed regions before unique ownership is published.</summary>
public delegate void NativeLayoutInitializer<TState>(scoped NativeLayoutWriter writer, scoped TState state)
    where TState : allows ref struct;
/// <summary>Processes a bounded typed layout with explicit caller state.</summary>
public delegate TResult NativeLayoutFunc<TState, TResult>(scoped NativeLayoutView view, scoped TState state)
    where TState : allows ref struct;
/// <summary>Mutates a bounded typed layout with explicit caller state.</summary>
public delegate void NativeLayoutAction<TState>(scoped NativeLayoutView view, scoped TState state)
    where TState : allows ref struct;

/// <summary>A stack-only all-region initializer; no field gains independent ownership.</summary>
public readonly ref struct NativeLayoutWriter
{
    private readonly NativeLayout? _layout;
    private readonly Span<byte> _payload;
    private readonly Span<int> _initialized;
    internal NativeLayoutWriter(NativeLayout layout, Span<byte> payload, Span<int> initialized)
    { _layout = layout; _payload = payload; _initialized = initialized; }

    /// <summary>Gets a sequential initialization writer for a checked field.</summary>
    public unsafe NativeLeaseWriter<T> Region<T>(NativeLayoutField<T> field) where T : unmanaged
    {
        NativeLayoutRegion region = (_layout ?? throw new NativeAllocationUninitializedException(nameof(NativeLayoutWriter), nameof(Region))).Region(field);
        ref byte first = ref MemoryMarshal.GetReference(_payload.Slice(region.Offset, region.Bytes));
        return new((IntPtr)Unsafe.AsPointer(ref first), region.Length, ref _initialized[field.Index]);
    }
}

/// <summary>A stack-only view admitted by its unique layout owner for one callback.</summary>
public readonly ref struct NativeLayoutView
{
    private readonly NativeLayout? _layout;
    private readonly Span<byte> _payload;
    internal NativeLayoutView(NativeLayout layout, Span<byte> payload) { _layout = layout; _payload = payload; }
    /// <summary>Gets one checked typed region; its span cannot outlive this callback.</summary>
    public Span<T> Region<T>(NativeLayoutField<T> field) where T : unmanaged
    {
        NativeLayoutRegion region = (_layout ?? throw new NativeAllocationUninitializedException(nameof(NativeLayoutView), nameof(Region))).Region(field);
        return MemoryMarshal.Cast<byte, T>(_payload.Slice(region.Offset, region.Bytes));
    }
}
