using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>Owns reusable heterogeneous bump storage for one thread.</summary>
public sealed class NativeArena : IDisposable
{
    private readonly NativeArenaKernel _kernel;

    /// <summary>Creates one active Arena with an optional raw byte reservation.</summary>
    public NativeArena(
        nuint preAllocateBytes = 0,
        NativeMemoryReturn returnMemoryOnDispose =
            NativeMemoryReturn.ToGarbageCollector)
    {
        NativeMemoryReturnValidation.Validate(
            returnMemoryOnDispose,
            nameof(returnMemoryOnDispose));
        _kernel = new NativeArenaKernel(
            preAllocateBytes,
            returnMemoryOnDispose);
    }

    internal NativeOwnerLifecycle CurrentLifecycle =>
        _kernel.Lifecycle;

    internal int CurrentAllocationRecordCountForTest => 0;

    /// <summary>Gets the current Arena storage statistics.</summary>
    public NativeOwnerStatistics GetStatistics() =>
        _kernel.GetStatistics();

    /// <summary>Initializes one generation-bound bump range.</summary>
    public ArenaLease<T> Scratch<T>(
        int length,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged =>
        _kernel.Scratch(length, scoped: false, initializer);

    /// <summary>Initializes one scoped bump range.</summary>
    public ArenaLease<T> ScratchScoped<T>(
        int length,
        NativeLeaseInitializer<T> initializer)
        where T : unmanaged =>
        _kernel.Scratch(length, scoped: true, initializer);

    /// <summary>Invalidates all leases and reuses all retained segments.</summary>
    public void Reset() => _kernel.Reset();

    /// <summary>Invalidates scoped leases and reuses scoped segments.</summary>
    public void RecycleScoped() => _kernel.RecycleScoped();

    /// <summary>Frees every idle tail segment.</summary>
    public nuint TrimRetainedMemory() =>
        _kernel.TrimRetainedMemory(nuint.MaxValue);

    /// <summary>Frees idle tail segments until the byte budget is met.</summary>
    public nuint TrimRetainedMemoryByBytes(nuint bytesToRelease) =>
        _kernel.TrimRetainedMemory(bytesToRelease);

    /// <summary>Closes the Arena and applies its cleanup policy.</summary>
    public void Dispose() => _kernel.Dispose();
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

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUninitialized(string operation) =>
        throw new NativeAllocationUninitializedException(
            nameof(ArenaLease<T>),
            operation);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowSourceLength() =>
        throw new ArgumentException(
            "The source length must equal the arena logical length.",
            "source");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDestinationLength() =>
        throw new ArgumentException(
            "The destination must contain the complete arena range.",
            "destination");
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
