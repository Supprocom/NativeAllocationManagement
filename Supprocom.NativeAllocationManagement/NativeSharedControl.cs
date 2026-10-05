using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

// Weak observation holds this bounded value/slot state, not the payload or allocator.
internal sealed class NativeSharedControl<T> where T : unmanaged
{
    private readonly Lock _gate = new();
    private NativeSharingBindingBank _strong;
    private NativeSharingBindingBank _weak;
    private readonly NativeSharingPreparation _preparation;
    private WeakReference<NativeSharedPayload<T>>? _payload;
    private WeakReference<NativeMemoryBudget>? _tracedBudget;
    private bool _expired;
    private bool _released;
    private bool _releaseStarted;
    private int _activeReads;
    private int _peakStrong;
    private int _peakWeak;
    private long _ownedBytes;
    private long _borrowedBytes;
    private long _allocationOrdinal;
    private readonly long _initializedBytes;
    private long _shares;
    private long _weakCreations;
    private long _upgrades;
    private long _strongRefusals;
    private long _weakRefusals;
    private long _expiredUpgrades;
    private long _payloadReturns;
    private long _detaches;
    private long _detachRefusals;
    private long _returnFailures;
    private bool _historyOverflowed;

    internal long Id { get; } = NativeOwnerIdentity.Next();
    internal long OwnerId { get; }

    internal NativeSharedControl(NativeSharingPreparation preparation, long ownerId, int length)
    {
        _preparation = preparation;
        _strong = new NativeSharingBindingBank(preparation.StrongBindingCount);
        _weak = preparation.WeakBindingCount == 0 ? NativeSharingBindingBank.Empty : new(preparation.WeakBindingCount);
        OwnerId = ownerId;
        _initializedBytes = (long)length * Unsafe.SizeOf<T>();
    }

    internal void PreparePayloadReference(NativeSharedPayload<T> payload)
    {
        if (_preparation.WeakBindingCount != 0) _payload = new(payload);
    }

    internal void ConfigureStorage(nuint ownedBytes, nuint borrowedBytes, NativeMemoryBudget? budget, long allocationOrdinal)
    {
        _ownedBytes = checked((long)ownedBytes);
        _borrowedBytes = checked((long)borrowedBytes);
        _allocationOrdinal = allocationOrdinal;
        if (budget is { TraceEnabled: true }) _tracedBudget = new(budget);
    }

    internal NativeShared<T> PublishInitial(NativeSharedPayload<T> payload, int length)
    {
        lock (_gate)
        {
            if (!_strong.TryAcquire(out int slot, out long version))
                throw new InvalidOperationException("Sharing preparation has no initial strong binding.");
            _peakStrong = 1;
            Trace(NativeMemoryTraceKind.Shared);
            return new(payload, slot, version, 0, length);
        }
    }

    internal bool TryShare(NativeSharedPayload<T> payload, int slot, long version, int offset, int length,
        out NativeShared<T> share, out NativeSharingExhaustionReason reason)
    {
        lock (_gate)
        {
            ValidateStrong(slot, version);
            share = default;
            if (!_strong.TryAcquire(out int acquired, out long acquiredVersion))
            {
                Increment(ref _strongRefusals);
                reason = NativeSharingExhaustionReason.NoStrongBinding;
                return false;
            }
            _peakStrong = Math.Max(_peakStrong, _strong.Occupied);
            Increment(ref _shares);
            share = new(payload, acquired, acquiredVersion, offset, length);
            reason = NativeSharingExhaustionReason.None;
            Trace(NativeMemoryTraceKind.Shared);
            return true;
        }
    }

    internal bool TryDowngrade(int slot, long version, int offset, int length,
        out NativeWeak<T> weak, out NativeSharingExhaustionReason reason)
    {
        lock (_gate)
        {
            ValidateStrong(slot, version);
            weak = default;
            if (!_weak.TryAcquire(out int acquired, out long acquiredVersion))
            {
                Increment(ref _weakRefusals);
                reason = NativeSharingExhaustionReason.NoWeakBinding;
                return false;
            }
            _peakWeak = Math.Max(_peakWeak, _weak.Occupied);
            Increment(ref _weakCreations);
            weak = new(this, acquired, acquiredVersion, offset, length);
            reason = NativeSharingExhaustionReason.None;
            Trace(NativeMemoryTraceKind.WeakCreated);
            return true;
        }
    }

    internal bool TryUpgrade(int slot, long version, int offset, int length,
        out NativeShared<T> shared, out NativeSharingExhaustionReason reason)
    {
        lock (_gate)
        {
            ValidateWeak(slot, version);
            shared = default;
            if (_expired || _strong.Occupied == 0 || _payload is null || !_payload.TryGetTarget(out NativeSharedPayload<T>? payload))
            {
                _expired = true;
                DropExpiredBanks();
                Increment(ref _expiredUpgrades);
                reason = NativeSharingExhaustionReason.ExpiredPayload;
                Trace(NativeMemoryTraceKind.UpgradeRejected);
                return false;
            }
            if (!_strong.TryAcquire(out int acquired, out long acquiredVersion))
            {
                Increment(ref _strongRefusals);
                reason = NativeSharingExhaustionReason.NoStrongBinding;
                Trace(NativeMemoryTraceKind.UpgradeRejected);
                return false;
            }
            _peakStrong = Math.Max(_peakStrong, _strong.Occupied);
            Increment(ref _upgrades);
            shared = new(payload, acquired, acquiredVersion, offset, length);
            reason = NativeSharingExhaustionReason.None;
            Trace(NativeMemoryTraceKind.WeakUpgraded);
            return true;
        }
    }

    internal bool IsExpired(int slot, long version)
    {
        lock (_gate)
        {
            ValidateWeak(slot, version);
            if (!_expired && (_payload is null || !_payload.TryGetTarget(out _)))
            {
                _expired = true;
                DropExpiredBanks();
            }
            return _expired;
        }
    }

    internal void EnterRead(int slot, long version)
    {
        lock (_gate)
        {
            ValidateStrong(slot, version);
            _activeReads = checked(_activeReads + 1);
        }
    }

    internal void ExitRead(NativeSharedPayload<T> payload)
    {
        bool release;
        lock (_gate)
        {
            if (_activeReads == 0) throw new InvalidOperationException("The shared payload has no entered read.");
            _activeReads--;
            release = BeginReleaseIfReady();
        }
        if (release) payload.ReturnStorage();
    }

    internal void ReleaseStrong(NativeSharedPayload<T> payload, int slot, long version)
    {
        bool release;
        lock (_gate)
        {
            ValidateStrong(slot, version);
            _strong.Release(slot, version);
            if (_strong.Occupied == 0)
            {
                _expired = true;
                DropExpiredBanks();
            }
            Trace(NativeMemoryTraceKind.OwnershipReleased);
            release = BeginReleaseIfReady();
        }
        if (release) payload.ReturnStorage();
    }

    internal void ReleaseWeak(int slot, long version)
    {
        lock (_gate)
        {
            ValidateWeak(slot, version);
            _weak.Release(slot, version);
            if (_expired) DropExpiredBanks();
            Trace(NativeMemoryTraceKind.OwnershipReleased);
        }
    }

    private void DropExpiredBanks()
    {
        // Expiration prevents every future strong publication. No version table
        // is needed to reject stale strong aliases, so observation cannot pin it.
        _strong = NativeSharingBindingBank.Empty;
        if (_weak.Occupied == 0) _weak = NativeSharingBindingBank.Empty;
    }

    private bool BeginReleaseIfReady()
    {
        if (!_expired || _activeReads != 0 || _released || _releaseStarted) return false;
        _releaseStarted = true;
        return true;
    }

    internal bool BeginAbandonedRelease()
    {
        lock (_gate)
        {
            _expired = true;
            DropExpiredBanks();
            return BeginReleaseIfReady();
        }
    }

    internal void RecordPayloadReturn()
    {
        lock (_gate)
        {
            _released = true;
            _expired = true;
            Increment(ref _payloadReturns);
            Trace(NativeMemoryTraceKind.PayloadReturned);
            _ownedBytes = 0;
            _borrowedBytes = 0;
            _payload = null;
        }
    }

    internal void RecordReturnFailure()
    {
        lock (_gate) { Increment(ref _returnFailures); _releaseStarted = false; }
    }

    internal bool TryCompletePayloadReturn(NativeSharedPayload<T>? knownPayload = null)
    {
        NativeSharedPayload<T>? payload;
        lock (_gate)
        {
            if (_released) return true;
            payload = knownPayload;
            if (payload is null && (_payload is null || !_payload.TryGetTarget(out payload))) return false;
            if (!BeginReleaseIfReady()) return false;
        }
        payload.ReturnStorage();
        return true;
    }

    internal void RecordDetach()
    {
        lock (_gate) { Increment(ref _detaches); Trace(NativeMemoryTraceKind.Detached); }
    }

    internal void RecordDetachRefusal()
    {
        lock (_gate) { Increment(ref _detachRefusals); }
    }

    internal NativeSharingStatistics CaptureSnapshot()
    {
        lock (_gate)
        {
            return new(Id, OwnerId, _preparation, _strong.Occupied, _weak.Occupied,
                _activeReads, _peakStrong, _peakWeak, _expired, _released, _ownedBytes,
                _borrowedBytes, _released ? 0 : _initializedBytes,
                checked(_strong.ElementBytes + _weak.ElementBytes), _shares, _weakCreations,
                _upgrades, _strongRefusals, _weakRefusals, _expiredUpgrades, _payloadReturns,
                _detaches, _historyOverflowed)
            {
                RejectedDetachCount = _detachRefusals,
                PayloadReturnFailureCount = _returnFailures,
                PeakInitializedPayloadBytes = _initializedBytes
            };
        }
    }

    private void ValidateStrong(int slot, long version)
    {
        if (_expired || !_strong.IsActive(slot, version))
            throw new InvalidOperationException("This strong binding is stale, released or expired.");
    }

    private void ValidateWeak(int slot, long version)
    {
        if (!_weak.IsActive(slot, version))
            throw new InvalidOperationException("This weak binding is stale or released.");
    }

    private void Increment(ref long history) => NativeOwnerHistory.Increment(ref history, ref _historyOverflowed);

    private void Trace(NativeMemoryTraceKind kind)
    {
        if (_tracedBudget is not null && _tracedBudget.TryGetTarget(out NativeMemoryBudget? budget))
            budget.RecordOwnershipTransition(kind, OwnerId, Id, checked((nuint)_ownedBytes), _allocationOrdinal);
    }
}

internal sealed class NativeSharedPayload<T> where T : unmanaged
{
    private NativeTransfer<T>? _transfer;
    private NativeStorageLifetimePin _pin;
    private IntPtr _pointer;
    private int _returnState;
    internal NativeSharedControl<T> Control { get; }

    internal NativeSharedPayload(NativeSharedControl<T> control) => Control = control;

    internal void Initialize(NativeTransfer<T> transfer)
    {
        _transfer = transfer;
        _pin = transfer.PinForSharing(out _pointer, out nuint ownedBytes, out nuint borrowedBytes, out NativeMemoryBudget? budget, out long ordinal);
        Control.ConfigureStorage(ownedBytes, borrowedBytes, budget, ordinal);
        transfer.AdoptSharedFinalization();
    }

    internal unsafe NativeReadOnlyLeaseView<T> GetView(int offset, int length) =>
        new(new ReadOnlySpan<T>((void*)_pointer, checked(offset + length)).Slice(offset, length));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "Deterministic payload return disarms this payload's emergency finalizer.")]
    internal void ReturnStorage()
    {
        if (Interlocked.CompareExchange(ref _returnState, 1, 0) != 0) return;
        try
        {
            NativeMemoryTestHooks.CheckManagedPublicationBoundary("NativeShared.Return", 3, "payload authority return");
            _pin.Dispose();
            _transfer?.Dispose();
            _transfer = null;
            _pointer = IntPtr.Zero;
            Control.RecordPayloadReturn();
            GC.SuppressFinalize(this);
        }
        catch
        {
            Control.RecordReturnFailure();
            Volatile.Write(ref _returnState, 0);
            throw;
        }
    }

    internal void AbortInitialization(NativeTransfer<T> transfer)
    {
        _transfer ??= transfer;
        if (Control.BeginAbandonedRelease()) ReturnStorage();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0055", Justification = "Emergency native cleanup supplements mandatory deterministic release.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Emergency finalization cannot propagate a cleanup failure into process termination.")]
    ~NativeSharedPayload()
    {
        try
        {
            if (_transfer.HasValue)
            {
                NativeMemoryTestHooks.NotifyBeforeOperationEntry("NativeShared.Finalize");
                if (Control.BeginAbandonedRelease()) ReturnStorage();
            }
        }
        catch { GC.ReRegisterForFinalize(this); }
    }
}
