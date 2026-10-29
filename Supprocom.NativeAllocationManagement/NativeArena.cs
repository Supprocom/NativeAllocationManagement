using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

// The state lives in a NativeArenaKernel, which a default arena's disposal
// keeps for the next NativeArena its thread creates. Creating an arena per
// scope then allocates only this object (an arena object held all the state:
// 408 bytes per scope). Each object binds one lifetime of its kernel; once the
// kernel has moved on, the object behaves as a disposed arena.

/// <summary>Owns reusable heterogeneous bump storage for one thread.</summary>
public sealed class NativeArena : IDisposable
{
    private readonly NativeArenaKernel _kernel;
    private readonly int _lifetime;

    /// <summary>Creates one active Arena with an optional raw byte reservation.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "RS0027", Justification = "Preserve the published optional signature; the budget-first overload has only required arguments and strictly greater arity, so it cannot capture any existing call.")]
    public NativeArena(
        nuint preAllocateBytes = 0,
        NativeMemoryReturn returnMemoryOnDispose =
            NativeMemoryReturn.ToGarbageCollector)
        : this(NativeArenaKernel.Acquire(preAllocateBytes, returnMemoryOnDispose))
    {
    }

    /// <summary>Creates an arena whose complete backing extents share one admission ceiling.</summary>
    /// <param name="budget">The backing admission domain.</param>
    /// <param name="preAllocateBytes">The initial usable byte reservation; native headers are charged too.</param>
    /// <param name="returnMemoryOnDispose">The final storage cleanup policy.</param>
    public NativeArena(NativeMemoryBudget budget, nuint preAllocateBytes, NativeMemoryReturn returnMemoryOnDispose)
        : this(new NativeArenaKernel(preAllocateBytes, returnMemoryOnDispose, budget, requireBudget: true))
    {
    }

    /// <summary>Creates an exact initial reservation with opt-in cold growth and whole-idle-unit retention limits.</summary>
    /// <param name="budget">The complete native backing admission ceiling.</param>
    /// <param name="retentionPolicy">The immutable ordinary growth and native idle-extent policy.</param>
    /// <param name="preAllocateBytes">The exact initial usable reservation, which may itself be an outlier.</param>
    /// <param name="returnMemoryOnDispose">The final cleanup policy.</param>
    public NativeArena(NativeMemoryBudget budget, NativeArenaRetentionPolicy retentionPolicy,
        nuint preAllocateBytes, NativeMemoryReturn returnMemoryOnDispose)
        : this(new NativeArenaKernel(budget, retentionPolicy, preAllocateBytes, returnMemoryOnDispose))
    {
    }

    /// <summary>Prepares two bounded lanes in one provider-owned mapped range.</summary>
    /// <param name="buffer">The provider whose safe-handle hold lasts until all lane backing is released.</param>
    /// <param name="byteOffset">The aligned start of the declared range.</param>
    /// <param name="preparation">Ordinary and scoped bounds; the scoped start must also be 64-byte aligned.</param>
    /// <param name="budget">The admission ceiling for NAM-owned headers, not provider-owned mapped bytes.</param>
    public NativeArena(SafeBuffer buffer, nuint byteOffset,
        NativeArenaPreparation preparation, NativeMemoryBudget budget)
        : this(new NativeArenaKernel(buffer, byteOffset, preparation, budget))
    {
    }

    /// <summary>Prepares both bounded lanes and prohibits fresh backing during execution.</summary>
    /// <param name="preparation">Exact ordinary and scoped usable-byte bounds.</param>
    /// <param name="budget">The shared ceiling for complete header and aligned backing extents.</param>
    public NativeArena(NativeArenaPreparation preparation, NativeMemoryBudget budget)
        : this(new NativeArenaKernel(preparation, budget))
    {
    }

    private NativeArena(NativeArenaKernel kernel)
    {
        _kernel = kernel;
        _lifetime = kernel.Lifetime;
        Id = kernel.Id;
    }

    /// <summary>Gets the stable process-local allocator identity.</summary>
    public long Id { get; }

    /// <summary>Captures actual thread-confined lane state without retaining native authority.</summary>
    public NativeOwnerDiagnosticSnapshot CaptureDiagnosticSnapshot()
    {
        NativeArenaKernel kernel = _kernel;
        if (kernel.Lifetime == _lifetime)
        {
            return kernel.GetDiagnosticSnapshot();
        }

        kernel.ValidateOwnerThread(nameof(CaptureDiagnosticSnapshot));
        return NativeArenaKernel.EndedSnapshot(Id);
    }

    /// <summary>Captures actual whole-unit idle retention, outlier extents and maintenance history without allocating.</summary>
    public NativeArenaRetentionStatistics CaptureRetentionSnapshot() =>
        Kernel(nameof(CaptureRetentionSnapshot)).GetRetentionSnapshot();

    /// <summary>Captures prepared lane capacity and recorded history without allocating or resetting it.</summary>
    public NativePreparedArenaStatistics CapturePreparedSnapshot() =>
        Kernel(nameof(CapturePreparedSnapshot)).GetPreparedSnapshot();

    /// <summary>Tries a fully initialized ordinary scratch without growth; capacity refusal precedes initialization.</summary>
    public bool TryScratch<T>(int length, NativeLeaseInitializer<T> initializer, out ArenaLease<T> lease)
        where T : unmanaged => Kernel(nameof(TryScratch)).TryScratch(length, scoped: false, initializer, out lease);

    /// <summary>Tries a fully initialized scoped scratch without growth; capacity refusal precedes initialization.</summary>
    public bool TryScratchScoped<T>(int length, NativeLeaseInitializer<T> initializer, out ArenaLease<T> lease)
        where T : unmanaged => Kernel(nameof(TryScratchScoped)).TryScratch(length, scoped: true, initializer, out lease);

    internal NativeOwnerLifecycle CurrentLifecycle =>
        _kernel.Lifetime == _lifetime ? _kernel.CurrentLifecycle : NativeOwnerLifecycle.Disposed;

    internal NativeArena KernelForInitialization => this;

    // The kernel serving this arena's lifetime, for lease operations.
    internal NativeArenaKernel KernelFor(string operation) => Kernel(operation);

    // Compatibility probe retains the per-owner instance shape.
#pragma warning disable CA1822
    internal int CurrentAllocationRecordCountForTest => 0;
#pragma warning restore CA1822

    /// <summary>Initializes one generation-bound bump range.</summary>
    public ArenaLease<T> Scratch<T>(
        int length,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged =>
        Kernel(nameof(Scratch)).Scratch(length, scoped: false, initializer);

    /// <summary>Initializes one scoped bump range.</summary>
    public ArenaLease<T> ScratchScoped<T>(
        int length,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged =>
        Kernel(nameof(ScratchScoped)).Scratch(length, scoped: true, initializer);

    /// <summary>Fills one generation-bound bump range with a value, without an initializer callback.</summary>
    /// <param name="length">The complete initialized element count.</param>
    /// <param name="value">The value written to every element before publication.</param>
    public ArenaLease<T> Scratch<T>(int length, T value)
        where T : unmanaged =>
        Kernel(nameof(Scratch)).ScratchFilled(length, scoped: false, value);

    /// <summary>Fills one scoped bump range with a value, without an initializer callback.</summary>
    /// <param name="length">The complete initialized element count.</param>
    /// <param name="value">The value written to every element before publication.</param>
    public ArenaLease<T> ScratchScoped<T>(int length, T value)
        where T : unmanaged =>
        Kernel(nameof(ScratchScoped)).ScratchFilled(length, scoped: true, value);

    /// <summary>Publishes one generation-bound bump range without writing its elements.</summary>
    /// <param name="length">The element count.</param>
    /// <remarks>The contents are unspecified: they may hold values an earlier lease in this process wrote. Write every element before reading it, or use <see cref="Scratch{T}(int, T)"/> for a filled range.</remarks>
    public ArenaLease<T> ScratchUninitialized<T>(int length)
        where T : unmanaged =>
        Kernel(nameof(ScratchUninitialized)).ScratchUninitialized<T>(length, scoped: false);

    /// <summary>Publishes one scoped bump range without writing its elements.</summary>
    /// <param name="length">The element count.</param>
    /// <remarks>The contents are unspecified: they may hold values an earlier lease in this process wrote. Write every element before reading it, or use <see cref="ScratchScoped{T}(int, T)"/> for a filled range.</remarks>
    public ArenaLease<T> ScratchScopedUninitialized<T>(int length)
        where T : unmanaged =>
        Kernel(nameof(ScratchScopedUninitialized)).ScratchUninitialized<T>(length, scoped: true);

    /// <summary>Frees every idle tail segment.</summary>
    public nuint TrimRetainedMemory() =>
        Kernel(nameof(TrimRetainedMemory)).TrimRetainedMemory(nuint.MaxValue);

    /// <summary>Frees idle tail segments until the byte budget is met.</summary>
    public nuint TrimRetainedMemoryByBytes(nuint bytesToRelease) =>
        Kernel(nameof(TrimRetainedMemoryByBytes)).TrimRetainedMemory(bytesToRelease);

    /// <summary>Invalidates all leases and reuses all retained segments.</summary>
    public void Reset() => Kernel(nameof(Reset)).Reset();

    /// <summary>Invalidates scoped leases and reuses scoped segments.</summary>
    public void RecycleScoped() => Kernel(nameof(RecycleScoped)).RecycleScoped();

    /// <summary>Gets the current Arena storage statistics.</summary>
    public NativeOwnerStatistics GetStatistics() => Kernel(nameof(GetStatistics)).GetStatistics();

    /// <summary>Applies the explicit policy to genuinely idle segments without invalidating any live scratch.</summary>
    /// <remarks>Rejects unconfigured owners and entered borrows or initializers before release.</remarks>
    public nuint MaintainRetention() => Kernel(nameof(MaintainRetention)).MaintainRetention();

    /// <summary>Closes the Arena and applies its cleanup policy.</summary>
    public void Dispose()
    {
        NativeArenaKernel kernel = _kernel;
        if (kernel.Lifetime == _lifetime)
        {
            kernel.Dispose();
            return;
        }

        // Disposed already, and its kernel serves a later arena.
        kernel.ValidateOwnerThread(nameof(Dispose));
    }

    // The kernel while this arena's lifetime lasts; afterwards the arena is
    // disposed, as its kernel's own checks would report.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeArenaKernel Kernel(string operation)
    {
        NativeArenaKernel kernel = _kernel;
        if (kernel.Lifetime != _lifetime)
        {
            ThrowEnded(kernel, operation);
        }

        return kernel;
    }

    [DoesNotReturn]
    private static void ThrowEnded(NativeArenaKernel kernel, string operation)
    {
        kernel.ValidateOwnerThread(operation);
        NativeArenaKernel.ThrowEnded(operation);
    }
}

/// <summary>A generation-bound capability for one Arena range.</summary>
/// <typeparam name="T">The unmanaged element type in the range.</typeparam>
public readonly ref struct ArenaLease<T>
    where T : unmanaged
{
    private readonly NativeArenaKernel? _kernel;
    private readonly IntPtr _pointer;
    private readonly int _length;
    private readonly ulong _generation;
    private readonly ulong _scopeEpoch;
    private readonly bool _scoped;

    internal ArenaLease(
        NativeArenaKernel kernel,
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

    /// <summary>Returns the logical range for direct use after one owner and lease check.</summary>
    /// <remarks>
    /// The span is confined to this lease's scope and carries no ownership. Use it only
    /// while the lease is current: not after the arena resets its generation, recycles its scoped lane or is disposed. The bundled analyzer reports later use.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe Span<T> AsSpan()
    {
        GetKernel(nameof(AsSpan)).ValidateHandle(_generation, _scopeEpoch, _scoped, nameof(AsSpan));
        return new Span<T>((void*)_pointer, _length);
    }

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

    /// <summary>Processes the logical range with a compile-time processor and explicit caller state.</summary>
    /// <typeparam name="TState">The caller state type.</typeparam>
    /// <typeparam name="TResult">The result type, which cannot contain the borrowed span.</typeparam>
    /// <typeparam name="TProcessor">The processor; its static member is called without a delegate.</typeparam>
    public TResult Process<TState, TResult, TProcessor>(
        scoped in TState state)
        where TState : allows ref struct
        where TProcessor : struct, INativeSpanProcessor<T, TState, TResult>
    {
        ArenaBorrow<T> borrow = EnterBorrow(nameof(Process));
        try
        {
            unsafe
            {
                return TProcessor.Invoke(
                    new Span<T>((void*)_pointer, _length),
                    in state);
            }
        }
        finally
        {
            borrow.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ArenaBorrow<T> EnterBorrow(string operation)
    {
        NativeArenaKernel kernel = GetKernel(operation);
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
    private NativeArenaKernel GetKernel(string operation)
    {
        NativeArenaKernel? kernel = _kernel;
        if (kernel is null)
        {
            ThrowUninitialized(operation);
        }

        return kernel;
    }

    internal NativeArenaKernel KernelForComposite => GetKernel(nameof(NativeLeaseOperations.Access));

    internal NativeLeaseView<T> GetViewForComposite(NativeArenaKernel kernel, string operation)
    {
        if (!ReferenceEquals(kernel, GetKernel(operation)))
        {
            throw new ArgumentException("Fast composite leases must belong to the same arena.", nameof(kernel));
        }
        kernel.ValidateCompositeEpoch(_generation, _scopeEpoch, _scoped, operation);
        return new NativeLeaseView<T>(_pointer, _length);
    }

    [DoesNotReturn]
    private static void ThrowUninitialized(string operation) =>
        throw new NativeAllocationUninitializedException(
            nameof(ArenaLease<T>),
            operation);

    [DoesNotReturn]
    // This cold helper reports the public caller's source argument.
#pragma warning disable MA0015
    private static void ThrowSourceLength() =>
        throw new ArgumentException(
            "The source length must equal the arena logical length.",
            "source");
#pragma warning restore MA0015

    [DoesNotReturn]
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
    private readonly NativeArenaKernel _kernel;

    internal ArenaBorrow(
        NativeArenaKernel kernel,
        IntPtr pointer,
        int length)
    {
        _kernel = kernel;
        View = new NativeLeaseView<T>(pointer, length);
    }

    internal NativeLeaseView<T> View { get; }

    internal void Dispose() => _kernel.ExitBorrow();
}
