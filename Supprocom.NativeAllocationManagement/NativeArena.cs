using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Owns reusable heterogeneous bump storage for one thread.</summary>
public sealed partial class NativeArena : IDisposable
{
    /// <summary>Gets the stable process-local allocator identity.</summary>
    public long Id { get; }

    /// <summary>Captures actual thread-confined lane state without retaining native authority.</summary>
    public NativeOwnerDiagnosticSnapshot CaptureDiagnosticSnapshot() =>
        GetDiagnosticSnapshot();

    /// <summary>Creates one active Arena with an optional raw byte reservation.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "RS0027", Justification = "Preserve the published optional signature; the budget-first overload has only required arguments and strictly greater arity, so it cannot capture any existing call.")]
    public NativeArena(
        nuint preAllocateBytes = 0,
        NativeMemoryReturn returnMemoryOnDispose =
            NativeMemoryReturn.ToGarbageCollector)
        : this(preAllocateBytes, returnMemoryOnDispose, budget: null, requireBudget: false)
    {
    }

    /// <summary>Creates an arena whose complete backing extents share one admission ceiling.</summary>
    /// <param name="budget">The backing admission domain.</param>
    /// <param name="preAllocateBytes">The initial usable byte reservation; native headers are charged too.</param>
    /// <param name="returnMemoryOnDispose">The final storage cleanup policy.</param>
    public NativeArena(NativeMemoryBudget budget, nuint preAllocateBytes, NativeMemoryReturn returnMemoryOnDispose)
        : this(preAllocateBytes, returnMemoryOnDispose, budget, requireBudget: true)
    {
    }

    /// <summary>Captures actual whole-unit idle retention, outlier extents and maintenance history without allocating.</summary>
    public NativeArenaRetentionStatistics CaptureRetentionSnapshot() => GetRetentionSnapshot();

    /// <summary>Captures prepared lane capacity and recorded history without allocating or resetting it.</summary>
    public NativePreparedArenaStatistics CapturePreparedSnapshot() => GetPreparedSnapshot();

    /// <summary>Tries a fully initialized ordinary scratch without growth; capacity refusal precedes initialization.</summary>
    public bool TryScratch<T>(int length, NativeLeaseInitializer<T> initializer, out ArenaLease<T> lease)
        where T : unmanaged => TryScratch(length, scoped: false, initializer, out lease);

    /// <summary>Tries a fully initialized scoped scratch without growth; capacity refusal precedes initialization.</summary>
    public bool TryScratchScoped<T>(int length, NativeLeaseInitializer<T> initializer, out ArenaLease<T> lease)
        where T : unmanaged => TryScratch(length, scoped: true, initializer, out lease);

    internal NativeOwnerLifecycle CurrentLifecycle =>
        _lifecycle;

    internal NativeArena KernelForInitialization => this;

    // Compatibility probe retains the per-owner instance shape.
#pragma warning disable CA1822
    internal int CurrentAllocationRecordCountForTest => 0;
#pragma warning restore CA1822

    /// <summary>Initializes one generation-bound bump range.</summary>
    public ArenaLease<T> Scratch<T>(
        int length,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged =>
        Scratch(length, scoped: false, initializer);

    /// <summary>Initializes one scoped bump range.</summary>
    public ArenaLease<T> ScratchScoped<T>(
        int length,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged =>
        Scratch(length, scoped: true, initializer);

    /// <summary>Frees every idle tail segment.</summary>
    public nuint TrimRetainedMemory() =>
        TrimRetainedMemory(nuint.MaxValue);

    /// <summary>Frees idle tail segments until the byte budget is met.</summary>
    public nuint TrimRetainedMemoryByBytes(nuint bytesToRelease) =>
        TrimRetainedMemory(bytesToRelease);
}

/// <summary>A generation-bound capability for one Arena range.</summary>
/// <typeparam name="T">The unmanaged element type in the range.</typeparam>
public readonly ref struct ArenaLease<T>
    where T : unmanaged
{
    private readonly NativeArena? _kernel;
    private readonly IntPtr _pointer;
    private readonly int _length;
    private readonly ulong _generation;
    private readonly ulong _scopeEpoch;
    private readonly bool _scoped;

    internal ArenaLease(
        NativeArena kernel,
        IntPtr pointer,
        int length,
        ulong generation,
        ulong scopeEpoch,
        bool scoped)
    {
        _kernel = kernel;
        _pointer = pointer;
        _length = length;
        _generation = generation;
        _scopeEpoch = scopeEpoch;
        _scoped = scoped;
    }

    /// <summary>Gets the immutable logical element count.</summary>
    public int Length => _length;

    /// <summary>Gets the immutable physical element capacity.</summary>
    public int Capacity => _length;

    /// <summary>Clears the logical range during one validated borrow.</summary>
    public void Clear()
    {
        ArenaBorrow<T> borrow = EnterBorrow(nameof(Clear));
        try
        {
            borrow.View.Clear();
        }
        finally
        {
            borrow.Dispose();
        }
    }

    /// <summary>Copies one exact source during one validated borrow.</summary>
    public void CopyFrom(scoped ReadOnlySpan<T> source)
    {
        if (source.Length != _length)
        {
            ThrowSourceLength();
        }

        ArenaBorrow<T> borrow = EnterBorrow(nameof(CopyFrom));
        try
        {
            borrow.View.CopyFrom(source);
        }
        finally
        {
            borrow.Dispose();
        }
    }

    /// <summary>Copies the logical range during one validated borrow.</summary>
    public void CopyTo(scoped Span<T> destination)
    {
        if (destination.Length < _length)
        {
            ThrowDestinationLength();
        }

        ArenaBorrow<T> borrow = EnterBorrow(nameof(CopyTo));
        try
        {
            borrow.View.CopyTo(destination);
        }
        finally
        {
            borrow.Dispose();
        }
    }

    /// <summary>Runs one bounded write callback.</summary>
    public void Access(NativeLeaseAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArenaBorrow<T> borrow = EnterBorrow(nameof(Access));
        try
        {
            action(borrow.View);
        }
        finally
        {
            borrow.Dispose();
        }
    }

    /// <summary>Runs one bounded read callback.</summary>
    public TResult Read<TResult>(NativeLeaseFunc<T, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArenaBorrow<T> borrow = EnterBorrow(nameof(Read));
        try
        {
            return action(borrow.View);
        }
        finally
        {
            borrow.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ArenaBorrow<T> EnterBorrow(string operation)
    {
        NativeArena kernel = GetKernel(operation);
        IntPtr pointer = kernel.EnterBorrow(
            _pointer,
            _length,
            _generation,
            _scopeEpoch,
            _scoped,
            operation);
        return new ArenaBorrow<T>(
            kernel,
            pointer,
            _length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeArena GetKernel(string operation)
    {
        NativeArena? kernel = _kernel;
        if (kernel is null)
        {
            ThrowUninitialized(operation);
        }

        return kernel;
    }

    internal NativeArena KernelForComposite => GetKernel(nameof(NativeLeaseOperations.Access));

    internal NativeLeaseView<T> GetViewForComposite(NativeArena kernel, string operation)
    {
        if (!ReferenceEquals(kernel, GetKernel(operation)))
        {
            throw new ArgumentException("Fast composite leases must belong to the same arena.", nameof(kernel));
        }
        kernel.ValidateCompositeEpoch(_generation, _scopeEpoch, _scoped, operation);
        return new NativeLeaseView<T>(_pointer, _length);
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUninitialized(string operation) =>
        throw new NativeAllocationUninitializedException(
            nameof(ArenaLease<T>),
            operation);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    // This cold helper reports the public caller's source argument.
#pragma warning disable MA0015
    private static void ThrowSourceLength() =>
        throw new ArgumentException(
            "The source length must equal the arena logical length.",
            "source");
#pragma warning restore MA0015

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    // This cold helper reports the public caller's destination argument.
#pragma warning disable MA0015
    private static void ThrowDestinationLength() =>
        throw new ArgumentException(
            "The destination must contain the complete arena range.",
            "destination");
#pragma warning restore MA0015
}

internal readonly ref struct ArenaBorrow<T>
    where T : unmanaged
{
    private readonly NativeArena _kernel;

    internal ArenaBorrow(
        NativeArena kernel,
        IntPtr pointer,
        int length)
    {
        _kernel = kernel;
        View = new NativeLeaseView<T>(pointer, length);
    }

    internal NativeLeaseView<T> View { get; }

    internal void Dispose() => _kernel.ExitBorrow();
}
