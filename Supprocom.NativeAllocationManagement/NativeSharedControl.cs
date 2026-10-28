using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement;

// Weak observation holds this bounded value/slot state, not the payload or allocator.
//
// A control whose payload returned with no weak observer or entered reader left
// parks on the returning thread, and that thread's next NativeShared.Create
// with the same preparation reuses it, banks and weak reference included.
// Reuse raises every slot version to a common floor, so bindings of earlier
// lifetimes fail validation and their snapshots report the payload returned.
// Release always returns the payload this control recorded at expiry, never a
// reader's: a stale reader can only complete the current lifetime's release.
internal sealed class NativeSharedControl<T> where T : unmanaged
{
    private const int ParkCapacity = 4;
    private static readonly int s_parkedSlot = NativeThreadState.AllocateTypedSlot();

    // A spin gate, not a Lock: holders update a few fields, never block or run
    // user code, and the control needs no second object.
    private int _gate;
    private NativeSharingBindingBank _strong;
    private NativeSharingBindingBank _weak;
    // The banks themselves: expiry drops _strong/_weak, reuse restores these.
    private readonly NativeSharingBindingBank _strongBank;
    private readonly NativeSharingBindingBank _weakBank;
    private readonly NativeSharingPreparation _preparation;
    private WeakReference<NativeTransferControl<T>>? _payload;
    private WeakReference<NativeTransferControl<T>>? _payloadReference;
    private NativeTransferControl<T>? _releasingPayload;
    private long _lifetimeFloor;
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
    private long _initializedBytes;
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

    internal long Id { get; private set; }
    internal long OwnerId { get; private set; }

    private NativeSharedControl(NativeThreadState thread, NativeSharingPreparation preparation, long ownerId, int length)
    {
        _preparation = preparation;
        _strongBank = new NativeSharingBindingBank(preparation.StrongBindingCount);
        _weakBank = preparation.WeakBindingCount == 0 ? NativeSharingBindingBank.Empty : new(preparation.WeakBindingCount);
        _strong = _strongBank;
        _weak = _weakBank;
        NativeMemoryAccounting.PrepareThread();
        Id = NativeOwnerIdentity.Next(thread);
        OwnerId = ownerId;
        _initializedBytes = (long)length * Unsafe.SizeOf<T>();
    }

    internal static NativeSharedControl<T> Acquire(NativeSharingPreparation preparation, long ownerId, int length)
    {
        NativeThreadState thread = NativeThreadState.Current;
        if (!NativeMemoryTestHooks.FaultInjectionPending
            && thread.GetTyped<ParkedControls>(s_parkedSlot) is { Count: > 0 } stack
            && stack.TryTake(thread, preparation) is { } parked)
        {
            parked.BeginLifetime(thread, ownerId, length);
            return parked;
        }

        return new NativeSharedControl<T>(thread, preparation, ownerId, length);
    }

    // No binding of this lifetime exists yet, but stale bindings of earlier ones
    // still reach this control: the reset runs under the gate, so their gated
    // decisions see either the returned lifetime or the new one, never a mix.
    private void BeginLifetime(NativeThreadState thread, long ownerId, int length)
    {
        NativeMemoryAccounting.PrepareThread();
        long id = NativeOwnerIdentity.Next(thread);
        EnterGate();
        try
        {
            Reset(id, ownerId, length);
        }
        finally { ExitGate(); }
    }

    private void Reset(long id, long ownerId, int length)
    {
        Id = id;
        OwnerId = ownerId;
        _initializedBytes = (long)length * Unsafe.SizeOf<T>();
        _tracedBudget = null;
        _releasingPayload = null;
        _peakStrong = 0;
        _peakWeak = 0;
        _ownedBytes = 0;
        _borrowedBytes = 0;
        _allocationOrdinal = 0;
        _shares = 0;
        _weakCreations = 0;
        _upgrades = 0;
        _strongRefusals = 0;
        _weakRefusals = 0;
        _expiredUpgrades = 0;
        _payloadReturns = 0;
        _detaches = 0;
        _detachRefusals = 0;
        _returnFailures = 0;
        _historyOverflowed = false;
        long floor = Math.Max(_strongBank.MaxVersion, _weakBank.MaxVersion);
        _strongBank.RaiseVersions(floor);
        _weakBank.RaiseVersions(floor);
        Volatile.Write(ref _lifetimeFloor, floor);
        _strong = _strongBank;
        _weak = _weakBank;
        _releaseStarted = false;
        _released = false;
        Volatile.Write(ref _expired, false);
    }

    // A binding version at or below the floor belongs to an earlier lifetime,
    // whose payload has returned.
    internal bool IsEarlierLifetime(long version) => version <= Volatile.Read(ref _lifetimeFloor);

    // Weak references to returned payloads, recycled on the thread that
    // returned them so that preparing weak bindings allocates no GC handle.
    private const int PayloadReferencePoolCapacity = 4;

    [ThreadStatic]
    private static WeakReference<NativeTransferControl<T>>?[]? t_payloadReferences;

    [ThreadStatic]
    private static int t_payloadReferenceCount;

    internal void PreparePayloadReference(NativeTransferControl<T> payload)
    {
        if (_preparation.WeakBindingCount == 0) return;
        if (_payloadReference is { } own)
        {
            own.SetTarget(payload);
            _payload = own;
            return;
        }

        int count = t_payloadReferenceCount;
        if (count != 0)
        {
            WeakReference<NativeTransferControl<T>>?[] pool = t_payloadReferences!;
            WeakReference<NativeTransferControl<T>> reference = pool[--count]!;
            pool[count] = null;
            t_payloadReferenceCount = count;
            reference.SetTarget(payload);
            _payload = reference;
            _payloadReference = reference;
            return;
        }

        _payload = new(payload);
        _payloadReference = _payload;
    }

    // Under the gate, after the last use of the reference: every reader
    // takes the gate to read _payload.
    private static void RecyclePayloadReference(WeakReference<NativeTransferControl<T>> reference)
    {
        int count = t_payloadReferenceCount;
        if (count == PayloadReferencePoolCapacity) return;
        reference.SetTarget(null!);
        (t_payloadReferences ??= new WeakReference<NativeTransferControl<T>>?[PayloadReferencePoolCapacity])[count] = reference;
        t_payloadReferenceCount = count + 1;
    }

    internal void ConfigureStorage(nuint ownedBytes, nuint borrowedBytes, NativeMemoryBudget? budget, long allocationOrdinal)
    {
        _ownedBytes = checked((long)ownedBytes);
        _borrowedBytes = checked((long)borrowedBytes);
        _allocationOrdinal = allocationOrdinal;
        if (budget is { TraceEnabled: true }) _tracedBudget = new(budget);
    }

    // No other thread can reach this control before its first binding is
    // returned, so the first publication needs no gate.
    internal NativeShared<T> PublishInitial(NativeTransferControl<T> payload, int length)
    {
        if (!_strong.TryAcquire(out int slot, out long version))
            throw new InvalidOperationException("Sharing preparation has no initial strong binding.");
        _peakStrong = 1;
        Trace(NativeMemoryTraceKind.Shared);
        return new(payload, this, slot, version, 0, length);
    }

    internal bool TryShare(NativeTransferControl<T> payload, int slot, long version, int offset, int length,
        out NativeShared<T> share, out NativeSharingExhaustionReason reason)
    {
        EnterGate();
        try
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
            share = new(payload, this, acquired, acquiredVersion, offset, length);
            reason = NativeSharingExhaustionReason.None;
            Trace(NativeMemoryTraceKind.Shared);
            return true;
        }
        finally { ExitGate(); }
    }

    internal bool TryDowngrade(int slot, long version, int offset, int length,
        out NativeWeak<T> weak, out NativeSharingExhaustionReason reason)
    {
        EnterGate();
        try
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
        finally { ExitGate(); }
    }

    internal bool TryUpgrade(int slot, long version, int offset, int length,
        out NativeShared<T> shared, out NativeSharingExhaustionReason reason)
    {
        EnterGate();
        try
        {
            ValidateWeak(slot, version);
            shared = default;
            if (_expired || _strong.Occupied == 0 || _payload is null || !_payload.TryGetTarget(out NativeTransferControl<T>? payload))
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
            shared = new(payload, this, acquired, acquiredVersion, offset, length);
            reason = NativeSharingExhaustionReason.None;
            Trace(NativeMemoryTraceKind.WeakUpgraded);
            return true;
        }
        finally { ExitGate(); }
    }

    internal bool IsExpired(int slot, long version)
    {
        EnterGate();
        try
        {
            ValidateWeak(slot, version);
            if (!_expired && (_payload is null || !_payload.TryGetTarget(out _)))
            {
                _expired = true;
                DropExpiredBanks();
            }
            return _expired;
        }
        finally { ExitGate(); }
    }

    // Reads take no lock, so readers of one payload never serialize. A reader
    // publishes itself with a full fence before checking its binding; release
    // marks expiry and fences before counting readers (BeginReleaseIfReady).
    // Each side therefore sees the other: the reader backs out, or release
    // waits for the last reader, whose exit then completes it.
    internal void EnterRead(NativeTransferControl<T> payload, int slot, long version)
    {
        Interlocked.Increment(ref _activeReads);
        if (Volatile.Read(ref _expired) || !Volatile.Read(ref _strong).IsActiveConcurrent(slot, version))
        {
            // A release that saw this refused reader is completed here.
            LeaveRead(payload, version);
            throw new InvalidOperationException("This strong binding is stale, released or expired.");
        }
    }

    internal void ExitRead(NativeTransferControl<T> payload, long version)
    {
        if (Volatile.Read(ref _activeReads) == 0) throw new InvalidOperationException("The shared payload has no entered read.");
        LeaveRead(payload, version);
    }

    // The last reader to leave an expired control completes its release. A
    // reader of an earlier lifetime of a reused control does not hold this
    // lifetime's payload: it releases only the payload recorded at expiry.
    private void LeaveRead(NativeTransferControl<T> payload, long version)
    {
        if (Interlocked.Decrement(ref _activeReads) != 0 || !Volatile.Read(ref _expired))
        {
            return;
        }

        NativeTransferControl<T>? releasing;
        EnterGate();
        try
        {
            releasing = _releasingPayload ?? (IsEarlierLifetime(version) ? null : payload);
            if (releasing is not null && !BeginReleaseIfReady()) releasing = null;
        }
        finally { ExitGate(); }

        releasing?.ReturnSharedPayload();
    }

    internal void ReleaseStrong(NativeTransferControl<T> payload, int slot, long version)
    {
        bool release;
        EnterGate();
        try
        {
            ValidateStrong(slot, version);
            _strong.Release(slot, version);
            if (_strong.Occupied == 0)
            {
                _releasingPayload = payload;
                _expired = true;
                DropExpiredBanks();
            }
            Trace(NativeMemoryTraceKind.OwnershipReleased);
            release = BeginReleaseIfReady();
        }
        finally { ExitGate(); }
        if (release) payload.ReturnSharedPayload();
    }

    internal void ReleaseWeak(int slot, long version)
    {
        EnterGate();
        try
        {
            ValidateWeak(slot, version);
            _weak.Release(slot, version);
            if (_expired) DropExpiredBanks();
            Trace(NativeMemoryTraceKind.OwnershipReleased);
        }
        finally { ExitGate(); }
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
        // Order the expiry written before this call ahead of the reader count
        // (see EnterRead).
        Interlocked.MemoryBarrier();
        if (!_expired || Volatile.Read(ref _activeReads) != 0 || _released || _releaseStarted) return false;
        _releaseStarted = true;
        return true;
    }

    // Abandoned payloads release from their own finalizer or failed creation;
    // recording one here would keep it reachable from weak observers.
    internal bool BeginAbandonedRelease()
    {
        EnterGate();
        try
        {
            _expired = true;
            DropExpiredBanks();
            return BeginReleaseIfReady();
        }
        finally { ExitGate(); }
    }

    internal void RecordPayloadReturn(bool mayPark = false)
    {
        bool park;
        EnterGate();
        try
        {
            if (_released) return;
            _released = true;
            _expired = true;
            _releasingPayload = null;
            Increment(ref _payloadReturns);
            park = mayPark
                && _tracedBudget is null
                && _strongBank.Occupied == 0
                && _weakBank.Occupied == 0
                && Volatile.Read(ref _activeReads) == 0
                && !NativeMemoryTestHooks.FaultInjectionPending;
            try { Trace(NativeMemoryTraceKind.PayloadReturned); }
            finally
            {
                _ownedBytes = 0;
                _borrowedBytes = 0;
                if (_payload is not null)
                {
                    // A parked control keeps its own reference for the next lifetime.
                    if (!park)
                    {
                        RecyclePayloadReference(_payload);
                        _payloadReference = null;
                    }

                    _payload = null;
                }
            }
        }
        finally { ExitGate(); }

        if (park)
        {
            Park();
        }
    }

    private void Park()
    {
        NativeThreadState thread = NativeThreadState.Current;
        ParkedControls stack = thread.GetTyped<ParkedControls>(s_parkedSlot) ?? CreateParkedControls(thread);
        stack.TryPush(thread, this);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParkedControls CreateParkedControls(NativeThreadState thread)
    {
        ParkedControls stack = new(thread);
        thread.SetTyped(s_parkedSlot, stack);
        return stack;
    }

    // Returned controls of one element type on one thread, most recent last.
    private sealed class ParkedControls
    {
        private readonly NativeSharedControl<T>?[] _items = new NativeSharedControl<T>?[ParkCapacity];
        private int _epoch;

        internal int Count;

        internal ParkedControls(NativeThreadState thread) => _epoch = thread.ReuseEpoch;

        internal void TryPush(NativeThreadState thread, NativeSharedControl<T> control)
        {
            if (_epoch != thread.ReuseEpoch)
            {
                Clear(thread);
            }

            // A full stack drops its oldest control: the most recent preparation
            // is the likeliest to come back.
            if (Count == ParkCapacity)
            {
                Array.Copy(_items, 1, _items, 0, ParkCapacity - 1);
                Count--;
            }

            _items[Count++] = control;
        }

        internal NativeSharedControl<T>? TryTake(NativeThreadState thread, NativeSharingPreparation preparation)
        {
            if (_epoch != thread.ReuseEpoch)
            {
                Clear(thread);
                return null;
            }

            for (int index = Count - 1; index >= 0; index--)
            {
                NativeSharedControl<T> control = _items[index]!;
                if (control._preparation == preparation)
                {
                    _items[index] = _items[--Count];
                    _items[Count] = null;
                    return control;
                }
            }

            return null;
        }

        private void Clear(NativeThreadState thread)
        {
            Array.Clear(_items);
            Count = 0;
            _epoch = thread.ReuseEpoch;
        }
    }

    internal void RecordReturnFailure()
    {
        EnterGate();
        // Retries come from the payload's own bindings, weak target or finalizer:
        // a failed payload must not stay reachable from this control.
        try { Increment(ref _returnFailures); _releaseStarted = false; _releasingPayload = null; }
        finally { ExitGate(); }
    }

    internal void DeferPayloadReturn()
    {
        EnterGate();
        try { _releaseStarted = false; }
        finally { ExitGate(); }
    }

    // Private custody retry: the caller holds the actual payload.
    internal bool TryCompletePayloadReturn(NativeTransferControl<T> knownPayload) =>
        TryCompletePayloadReturn(long.MaxValue, knownPayload);

    internal bool TryCompletePayloadReturn(long bindingVersion, NativeTransferControl<T>? knownPayload = null)
    {
        NativeTransferControl<T>? payload;
        EnterGate();
        try
        {
            if (_released || IsEarlierLifetime(bindingVersion)) return true;
            payload = _releasingPayload ?? knownPayload;
            if (payload is null && (_payload is null || !_payload.TryGetTarget(out payload))) return false;
            if (!BeginReleaseIfReady()) return false;
        }
        finally { ExitGate(); }
        payload.ReturnSharedPayload();
        return Volatile.Read(ref _released);
    }

    internal void RecordDetach()
    {
        EnterGate();
        try { Increment(ref _detaches); Trace(NativeMemoryTraceKind.Detached); }
        finally { ExitGate(); }
    }

    internal void RecordDetachRefusal()
    {
        EnterGate();
        try { Increment(ref _detachRefusals); }
        finally { ExitGate(); }
    }

    internal NativeSharingStatistics CaptureSnapshot() => CaptureSnapshot(long.MaxValue);

    internal NativeSharingStatistics CaptureSnapshot(long bindingVersion)
    {
        EnterGate();
        try
        {
            if (IsEarlierLifetime(bindingVersion))
            {
                // A binding of an earlier lifetime of this reused control: that
                // lifetime ended with its payload returned and its banks dropped.
                return new(Id, OwnerId, _preparation, 0, 0, 0, 0, 0, true, true, 0, 0, 0, 0,
                    0, 0, 0, 0, 0, 0, 1, 0, false);
            }

            return new(Id, OwnerId, _preparation, _strong.Occupied, _weak.Occupied,
                Volatile.Read(ref _activeReads), _peakStrong, _peakWeak, _expired, _released, _ownedBytes,
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
        finally { ExitGate(); }
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnterGate()
    {
        if (Interlocked.CompareExchange(ref _gate, 1, 0) != 0)
        {
            WaitForGate();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WaitForGate()
    {
        SpinWait spinner = default;
        do
        {
            spinner.SpinOnce();
        }
        while (Volatile.Read(ref _gate) != 0
            || Interlocked.CompareExchange(ref _gate, 1, 0) != 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExitGate() => Volatile.Write(ref _gate, 0);

    private void Increment(ref long history) => NativeOwnerHistory.Increment(ref history, ref _historyOverflowed);

    private void Trace(NativeMemoryTraceKind kind)
    {
        if (_tracedBudget is not null && _tracedBudget.TryGetTarget(out NativeMemoryBudget? budget))
            budget.RecordOwnershipTransition(kind, OwnerId, Id, checked((nuint)_ownedBytes), _allocationOrdinal);
    }
}
