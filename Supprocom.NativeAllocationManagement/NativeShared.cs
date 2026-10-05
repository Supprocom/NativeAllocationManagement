using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supprocom.NativeAllocationManagement;

/// <summary>A prepared binding for initialized immutable native data; assignment does not acquire another owner.</summary>
/// <typeparam name="T">The initialized unmanaged element type.</typeparam>
[StructLayout(LayoutKind.Sequential)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1815", Justification = "An ownership binding has no value-equality contract; assignment aliases one release identity.")]
public readonly struct NativeShared<T> : IDisposable where T : unmanaged
{
    private readonly NativeSharedPayload<T>? _payload;
    private readonly int _slot;
    private readonly long _version;
    private readonly int _offset;
    private readonly int _length;

    internal NativeShared(NativeSharedPayload<T> payload, int slot, long version, int offset, int length)
    {
        _payload = payload; _slot = slot; _version = version; _offset = offset; _length = length;
    }

    /// <summary>Gets the non-reusable payload identity, not an address.</summary>
    public long Id => Payload.Control.Id;
    /// <summary>Gets this binding's immutable logical range length, not its retained backing cost.</summary>
    public int Length => _length;
    /// <summary>Gets a consistent observation even after this binding has released its authority.</summary>
    public NativeSharingStatistics CaptureSnapshot() => Payload.Control.CaptureSnapshot();

    /// <summary>Retries failed payload cleanup without reopening ownership; false means a surviving binding or entered read still prevents return.</summary>
    public bool TryCompletePayloadReturn() => Payload.Control.TryCompletePayloadReturn(Payload);

    /// <summary>Prepares all sharing metadata, consumes unique ownership, and publishes initialized read-only storage.</summary>
    /// <remarks>Invalid preparation and metadata failure precede consumption; failures after destructive movement consume the source and return its storage.</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "Typed destructive conversion consumes the exact unique element type through a ref binding.")]
    public static NativeShared<T> Create(ref NativeTransfer<T>? source, NativeSharingPreparation preparation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preparation.StrongBindingCount, nameof(preparation));
        ArgumentOutOfRangeException.ThrowIfNegative(preparation.WeakBindingCount, nameof(preparation));
        NativeTransfer<T> observed = source ?? throw new ArgumentNullException(nameof(source));
        int length = observed.Length;
        NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativeShared.Create", 1, "sharing metadata preparation");
        NativeSharedControl<T> control = new(preparation, observed.Id, length);
        NativeSharedPayload<T> payload = new(control);
        control.PreparePayloadReference(payload);
        NativeTransfer<T> moved = NativeTransfer<T>.Move(ref source);
        try
        {
            payload.Initialize(moved);
            NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativeShared.Create", 2, "the initial strong binding");
            return control.PublishInitial(payload, length);
        }
        catch (Exception initializationFailure)
        {
            try { payload.AbortInitialization(moved); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Shared publication and cleanup both failed.", initializationFailure, cleanupFailure);
            }
            throw;
        }
    }

    /// <summary>Acquires one independent strong binding, returning false only for prepared slot exhaustion.</summary>
    public bool TryShare(out NativeShared<T> share, out NativeSharingExhaustionReason reason) =>
        Payload.Control.TryShare(Payload, _slot, _version, _offset, _length, out share, out reason);

    /// <summary>Acquires a read-only slice while retaining the complete original backing extent.</summary>
    public bool TrySlice(int offset, int length, out NativeShared<T> slice, out NativeSharingExhaustionReason reason)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset > _length || length > _length - offset)
            throw new ArgumentOutOfRangeException(nameof(length), "The slice exceeds this binding's logical range.");
        return Payload.Control.TryShare(Payload, _slot, _version, checked(_offset + offset), length, out slice, out reason);
    }

    /// <summary>Acquires one weak observer without retaining the payload or its pooled owner.</summary>
    public bool TryDowngrade(out NativeWeak<T> weak, out NativeSharingExhaustionReason reason) =>
        Payload.Control.TryDowngrade(_slot, _version, _offset, _length, out weak, out reason);

    /// <summary>Runs one entered synchronous read-only bounded callback.</summary>
    public void Access(NativeReadOnlyLeaseAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeSharedPayload<T> payload = Payload;
        payload.Control.EnterRead(_slot, _version);
        Exception? callbackFailure = null;
        try { action(payload.GetView(_offset, _length)); }
        catch (Exception failure) { callbackFailure = failure; throw; }
        finally { EndRead(payload, callbackFailure); }
    }

    /// <summary>Computes a non-ref-struct result in one entered synchronous read-only callback.</summary>
    public TResult Read<TResult>(NativeReadOnlyLeaseFunc<T, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeSharedPayload<T> payload = Payload;
        payload.Control.EnterRead(_slot, _version);
        Exception? callbackFailure = null;
        try { return action(payload.GetView(_offset, _length)); }
        catch (Exception failure) { callbackFailure = failure; throw; }
        finally { EndRead(payload, callbackFailure); }
    }

    private static void EndRead(NativeSharedPayload<T> payload, Exception? callbackFailure)
    {
        try { payload.Control.ExitRead(payload); }
        catch (Exception cleanupFailure) when (callbackFailure is not null)
        {
            throw new AggregateException("The shared callback and payload return both failed.", callbackFailure, cleanupFailure);
        }
        finally { GC.KeepAlive(payload); }
    }

    /// <summary>Explicitly copies this slice into independent unique ownership after full overlap admission.</summary>
    /// <remarks>False means native-budget exhaustion before allocation or copying. Other failures throw and preserve this source. Empty copies do not invent backing or copy events.</remarks>
    public bool TryDetach(NativeMemoryBudget budget, out NativeTransfer<T> detached)
    {
        ArgumentNullException.ThrowIfNull(budget);
        detached = default;
        NativeSharedPayload<T> payload = Payload;
        payload.Control.EnterRead(_slot, _version);
        try
        {
            long ownerId = NativeOwnerIdentity.Next();
            nuint bytes = checked((nuint)_length * (nuint)Unsafe.SizeOf<T>());
            if (!budget.TryReserve(bytes, out _, ownerId))
            {
                payload.Control.RecordDetachRefusal();
                return false;
            }
            NativeBlock block = NativeBlockAllocator.Allocate<T>(_length, nameof(NativeShared<T>),
                "NativeShared.Detach", budget, ownerId, alreadyReserved: true);
            bool published = false;
            try
            {
                NativeTransfer<T> destination = NativeTransfer<T>.CreateOwnedBlock(block, _length, _length);
                try
                {
                    unsafe { payload.GetView(_offset, _length).CopyTo(new Span<T>((void*)block.Pointer, _length)); }
                    detached = destination;
                    published = true;
                    payload.Control.RecordDetach();
                    return true;
                }
                catch { published = true; destination.Dispose(); throw; }
            }
            finally { if (!published) NativeBlockAllocator.Free(block); }
        }
        finally { payload.Control.ExitRead(payload); GC.KeepAlive(payload); }
    }

    /// <summary>Releases this independently acquired binding once; aliases cannot decrement ownership twice.</summary>
    public void Dispose() => Payload.Control.ReleaseStrong(Payload, _slot, _version);

    private NativeSharedPayload<T> Payload => _payload
        ?? throw new NativeAllocationUninitializedException(nameof(NativeShared<T>), "ownership operation");
}

/// <summary>A prepared versioned observer retaining only control metadata, never payload storage or its allocator.</summary>
/// <typeparam name="T">The observed immutable unmanaged element type.</typeparam>
[StructLayout(LayoutKind.Sequential)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1815", Justification = "An observer binding has no value-equality contract; assignment aliases one release identity.")]
public readonly struct NativeWeak<T> : IDisposable where T : unmanaged
{
    private readonly NativeSharedControl<T>? _control;
    private readonly int _slot;
    private readonly long _version;
    private readonly int _offset;
    private readonly int _length;

    internal NativeWeak(NativeSharedControl<T> control, int slot, long version, int offset, int length)
    {
        _control = control; _slot = slot; _version = version; _offset = offset; _length = length;
    }

    /// <summary>Gets the non-reusable observed payload identity.</summary>
    public long Id => Control.Id;
    /// <summary>Gets whether strong ownership has permanently expired; validates this observer's release identity.</summary>
    public bool IsExpired => Control.IsExpired(_slot, _version);
    /// <summary>Gets actual metadata and lifetime state without acquiring payload ownership.</summary>
    public NativeSharingStatistics CaptureSnapshot() => Control.CaptureSnapshot();
    /// <summary>Retries failed payload cleanup without upgrading or retaining the payload after this call.</summary>
    public bool TryCompletePayloadReturn() => Control.TryCompletePayloadReturn();
    /// <summary>Atomically acquires a strong binding or reports expiration/strong-slot exhaustion without allocating.</summary>
    public bool TryUpgrade(out NativeShared<T> shared, out NativeSharingExhaustionReason reason) =>
        Control.TryUpgrade(_slot, _version, _offset, _length, out shared, out reason);
    /// <summary>Releases this prepared observer once; aliases cannot release a reused observer slot.</summary>
    public void Dispose() => Control.ReleaseWeak(_slot, _version);

    private NativeSharedControl<T> Control => _control
        ?? throw new NativeAllocationUninitializedException(nameof(NativeWeak<T>), "observer operation");
}
