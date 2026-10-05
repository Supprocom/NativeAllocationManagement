using System;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

// Public NAM calls only: the same source is compiled by isolated package consumers.
internal static partial class NativeGeneratedScenarios
{
    internal static void RunAdmission(int seed, int steps, Action<string> trace)
    {
        SeededRandom random = new(seed);
        NativeMemoryBudget budget = new(128);
        NativeMemoryReservation<int>?[] permissions = new NativeMemoryReservation<int>?[8];
        NativeTransfer<int>?[] owners = new NativeTransfer<int>?[8];
        int[] states = new int[8]; // Independent model: empty, pending, prepared, published.
        int[] lengths = new int[8];
        int[] values = new int[8];
        try
        {
            for (int step = 0; step < steps; step++)
            {
                int slot = random.Next(states.Length);
                int operation = random.Next(7);
                trace($"admission seed={seed} step={step} slot={slot} operation={operation} state={states[slot]}");
                if (operation == 0 && states[slot] == 0)
                {
                    int length = random.Next(1, 9);
                    long admitted = 0;
                    for (int index = 0; index < states.Length; index++)
                        if (states[index] != 0) admitted += lengths[index] * 4L;
                    bool expected = admitted + length * 4L <= 128;
                    bool accepted = budget.TryReserve<int>(length, out permissions[slot], out NativeMemoryAdmissionExhaustionReason reason);
                    Require(expected == accepted, "admission result differs from independent byte sum");
                    Require(reason == (expected ? NativeMemoryAdmissionExhaustionReason.None : NativeMemoryAdmissionExhaustionReason.NativeByteCapacity), "admission reason differs");
                    if (accepted) { states[slot] = 1; lengths[slot] = length; values[slot] = random.Next(1, 1000); }
                    else Require(!permissions[slot].HasValue, "refusal published permission");
                }
                else if (operation == 1 && states[slot] == 1)
                {
                    permissions[slot]!.Value.PrepareBacking();
                    states[slot] = 2;
                }
                else if (operation == 2 && states[slot] is 1 or 2)
                {
                    NativeMemoryReservation<int> stale = permissions[slot]!.Value;
                    permissions[slot] = NativeMemoryReservation<int>.Move(ref permissions[slot]);
                    ExpectInvalid(stale.Dispose);
                }
                else if (operation == 2 && states[slot] == 3)
                {
                    NativeTransfer<int> stale = owners[slot]!.Value;
                    owners[slot] = NativeTransfer<int>.Move(ref owners[slot]);
                    ExpectInvalid(stale.Dispose);
                }
                else if (operation == 3 && states[slot] is 1 or 2)
                {
                    int value = values[slot];
                    owners[slot] = NativeMemoryReservation<int>.Activate(ref permissions[slot], writer => writer.Fill(value));
                    Require(!permissions[slot].HasValue, "activation did not clear permission");
                    states[slot] = 3;
                }
                else if (operation == 4 && states[slot] != 0)
                {
                    if (states[slot] == 3) { owners[slot]!.Value.Dispose(); owners[slot] = null; }
                    else { permissions[slot]!.Value.Dispose(); permissions[slot] = null; }
                    states[slot] = 0;
                }
                else if (operation == 5 && states[slot] is 1 or 2)
                {
                    ExpectInvalid(() => NativeMemoryReservation<int>.Activate(ref permissions[slot], static _ => { }));
                    Require(!permissions[slot].HasValue, "incomplete publication retained producer permission");
                    states[slot] = 0;
                }
                else if (operation == 6 && states[slot] == 3)
                {
                    int actual = owners[slot]!.Value.Read(static view =>
                    {
                        int sum = 0;
                        for (int index = 0; index < view.Length; index++) sum += view[index];
                        return sum;
                    });
                    Require(actual == lengths[slot] * values[slot], "initialized unique output differs");
                }
                long reserved = 0, committed = 0, prepared = 0, outstanding = 0;
                for (int index = 0; index < states.Length; index++)
                {
                    if (states[index] == 1) reserved += lengths[index] * 4L;
                    if (states[index] is 2 or 3) committed += lengths[index] * 4L;
                    if (states[index] == 2) prepared += lengths[index] * 4L;
                    if (states[index] is 1 or 2) outstanding++;
                }
                NativeMemoryBudgetStatistics actualBudget = budget.CaptureStatistics();
                NativeMemoryAdmissionStatistics admission = budget.CaptureAdmissionStatistics();
                Require(actualBudget.ReservedBytes == reserved && actualBudget.CommittedBytes == committed, "reservation/physical charge differs");
                Require(admission.PreparedUnpublishedBytes == prepared && admission.OutstandingReservationCount == outstanding, "producer gauge differs");
                Require(reserved + committed <= 128, "oracle exceeded native cap");
            }
        }
        finally
        {
            for (int index = 0; index < states.Length; index++)
            {
                owners[index]?.Dispose();
                permissions[index]?.Dispose();
            }
        }
        Require(budget.CaptureStatistics().CommittedBytes == 0 && budget.CaptureStatistics().ReservedBytes == 0, "admission teardown leaked native charge");
    }

    internal static void RunSharing(int seed, int steps, Action<string> trace)
    {
        SeededRandom random = new(seed);
        NativeMemoryBudget budget = new(64);
        Require(budget.TryReserve<int>(16, out NativeMemoryReservation<int>? permission, out _), "initial sharing admission failed");
        NativeTransfer<int>? unique = NativeMemoryReservation<int>.Activate(ref permission, static writer => writer.Fill(37));
        NativeShared<int> witness = NativeShared<int>.Create(ref unique, new(4, 3));
        NativeShared<int>?[] strong = new NativeShared<int>?[4];
        NativeWeak<int>?[] weak = new NativeWeak<int>?[3];
        int[] lengths = new int[4];
        int[] weakLengths = new int[3];
        strong[0] = witness;
        lengths[0] = 16;
        int peakStrong = 1, peakWeak = 0;
        long shares = 0, observers = 0, upgrades = 0, rejectedStrong = 0, rejectedWeak = 0, expiredUpgrades = 0;
        try
        {
            for (int step = 0; step < steps; step++)
            {
                int strongCount = Count(strong), weakCount = Count(weak);
                // Force every bounded-capacity edge before the seeded random tail.
                int operation = step switch
                {
                    < 3 => 0,
                    < 7 => 1,
                    7 => 2,
                    8 => 3,
                    9 => 2,
                    _ => random.Next(6)
                };
                int source = Find(strong, present: true), destination = Find(strong, present: false);
                int observer = Find(weak, present: true), observerDestination = Find(weak, present: false);
                trace($"sharing seed={seed} step={step} operation={operation} strong={strongCount} weak={weakCount}");
                if (operation == 0 && source >= 0)
                {
                    int length = random.Next(lengths[source] + 1);
                    bool accepted = strong[source]!.Value.TrySlice(0, length, out NativeShared<int> acquired, out NativeSharingExhaustionReason reason);
                    Require(accepted == (destination >= 0), "slice slot result differs");
                    Require(reason == (accepted ? NativeSharingExhaustionReason.None : NativeSharingExhaustionReason.NoStrongBinding), "slice refusal differs");
                    if (accepted) { strong[destination] = acquired; lengths[destination] = length; shares++; }
                    else rejectedStrong++;
                }
                else if (operation == 1 && source >= 0)
                {
                    bool accepted = strong[source]!.Value.TryDowngrade(out NativeWeak<int> acquired, out NativeSharingExhaustionReason reason);
                    Require(accepted == (observerDestination >= 0), "weak slot result differs");
                    Require(reason == (accepted ? NativeSharingExhaustionReason.None : NativeSharingExhaustionReason.NoWeakBinding), "weak refusal differs");
                    if (accepted) { weak[observerDestination] = acquired; weakLengths[observerDestination] = lengths[source]; observers++; }
                    else rejectedWeak++;
                }
                else if (operation == 2 && observer >= 0)
                {
                    bool expected = strongCount > 0 && destination >= 0;
                    bool accepted = weak[observer]!.Value.TryUpgrade(out NativeShared<int> acquired, out NativeSharingExhaustionReason reason);
                    Require(accepted == expected, "weak upgrade differs from independent live-owner/slot model");
                    NativeSharingExhaustionReason expectedReason = strongCount == 0 ? NativeSharingExhaustionReason.ExpiredPayload
                        : destination < 0 ? NativeSharingExhaustionReason.NoStrongBinding : NativeSharingExhaustionReason.None;
                    Require(reason == expectedReason, "upgrade reason differs");
                    if (accepted) { strong[destination] = acquired; lengths[destination] = weakLengths[observer]; upgrades++; }
                    else if (strongCount == 0) expiredUpgrades++;
                    else rejectedStrong++;
                }
                else if (operation == 3 && source >= 0)
                {
                    NativeShared<int> stale = strong[source]!.Value;
                    stale.Dispose(); strong[source] = null;
                    ExpectInvalid(stale.Dispose);
                    ExpectInvalid(() => stale.Read(static view => view.Length));
                }
                else if (operation == 4 && observer >= 0)
                {
                    NativeWeak<int> stale = weak[observer]!.Value;
                    stale.Dispose(); weak[observer] = null;
                    ExpectInvalid(stale.Dispose);
                    ExpectInvalid(() => stale.TryUpgrade(out _, out _));
                }
                else if (operation == 5 && source >= 0)
                {
                    int sum = strong[source]!.Value.Read(static view =>
                    {
                        int total = 0;
                        for (int index = 0; index < view.Length; index++) total += view[index];
                        return total;
                    });
                    Require(sum == lengths[source] * 37, "immutable slice output differs");
                }
                strongCount = Count(strong); weakCount = Count(weak);
                peakStrong = Math.Max(peakStrong, strongCount); peakWeak = Math.Max(peakWeak, weakCount);
                NativeSharingStatistics actual = witness.CaptureSnapshot();
                Require(actual.StrongBindingCount == strongCount && actual.WeakBindingCount == weakCount && actual.ActiveReadCount == 0, "sharing current counts differ");
                Require(actual.PeakStrongBindingCount == peakStrong && actual.PeakWeakBindingCount == peakWeak, "sharing high-water differs");
                Require(actual.ShareCount == shares && actual.WeakCreationCount == observers && actual.SuccessfulUpgradeCount == upgrades, "sharing success history differs");
                Require(actual.RejectedStrongCount == rejectedStrong && actual.RejectedWeakCount == rejectedWeak && actual.ExpiredUpgradeCount == expiredUpgrades, "sharing rejection history differs");
                Require(actual.Expired == (strongCount == 0) && actual.PayloadReleased == (strongCount == 0), "weak observers retained payload or resurrected ownership");
                Require(actual.OwnedBackingBytes == (strongCount == 0 ? 0 : 64) && budget.CaptureStatistics().CommittedBytes == (strongCount == 0 ? 0 : 64), "slice backing was mischarged");
            }
        }
        finally
        {
            foreach (NativeShared<int>? owner in strong) owner?.Dispose();
            foreach (NativeWeak<int>? observer in weak) observer?.Dispose();
        }
        Require(budget.CaptureStatistics().CommittedBytes == 0, "sharing teardown leaked native charge");
    }

    internal static void RunPreparedPool(int seed, int steps, Action<string> trace)
    {
        PoolOracle model = new(seed, steps, trace);
        NativeMemoryBudget budget = new(256);
        using NativePool<int> pool = new(new NativePoolPreparation(4, 4, 2), budget);
        while (model.Remaining > 0) ExercisePool(pool, budget, model, 0);
        Require(pool.CapturePreparedSnapshot().OccupiedSlotCount == 0, "prepared pool retained occupied slots");
        Require(pool.TrimRetainedMemory() == 256, "idle prepared page release differs");
        Require(budget.CaptureStatistics().CommittedBytes == 0, "trimmed pool retained native charge");
        Require(!pool.TryRent(1, static writer => writer.Write(1), out _, out _), "trim implicitly refilled prepared pool");
    }

    private static void ExercisePool(NativePool<int> pool, NativeMemoryBudget budget, PoolOracle model, int occupied)
    {
        if (model.Remaining-- <= 0) return;
        int request = model.Random.Next(1, 7);
        model.Trace($"pool step={model.Step++} occupied={occupied} request={request}");
        bool expected = request <= 4 && occupied < 4;
        if (!pool.TryRent(request, static writer => writer.Fill(19), out Pooled<int> lease, out NativePoolExhaustionReason reason))
        {
            Require(!expected, "prepared slot acquisition differs");
            if (request > 4) { model.RejectedShape++; Require(reason == NativePoolExhaustionReason.ShapeExceeded, "shape refusal differs"); }
            else { model.RejectedFull++; Require(reason == NativePoolExhaustionReason.NoAvailableSlot, "slot refusal differs"); }
            CheckPool(pool, budget, model, occupied);
            return;
        }
        using (lease)
        {
            Require(expected, "prepared slot acquisition differs");
            model.Successful++;
            model.PeakOccupied = Math.Max(model.PeakOccupied, occupied + 1);
            CheckPool(pool, budget, model, occupied + 1);
            ExercisePool(pool, budget, model, occupied + 1);
            Require(lease.Read(static view => view[0]) == 19, "another slot damaged existing output");
        }
        bool rejected = false;
        try { _ = lease.Read(static view => view[0]); }
        catch (NativeAllocationReturnedException) { rejected = true; }
        Require(rejected, "returned slot alias remained usable");
        CheckPool(pool, budget, model, occupied);
    }

    private static void CheckPool(NativePool<int> pool, NativeMemoryBudget budget, PoolOracle model, int occupied)
    {
        NativePreparedPoolStatistics actual = pool.CapturePreparedSnapshot();
        Require(actual.OccupiedSlotCount == occupied && actual.AvailableSlotCount == 4 - occupied, "prepared occupancy differs");
        Require(actual.PeakOccupiedSlotCount == model.PeakOccupied, "prepared occupancy high-water differs");
        Require(actual.RetainedPageCount == 2 && actual.RetainedSlotCount == 4 && actual.RetainedBytes == 256, "prepared page extent differs");
        Require(actual.UnusedSlotBytes == (4 - occupied) * 16, "usable idle bytes include padding or omit slots");
        Require(actual.SuccessfulRentCount == model.Successful && actual.RejectedShapeCount == model.RejectedShape && actual.RejectedFullCount == model.RejectedFull, "prepared history differs");
        Require(budget.CaptureStatistics().AllocationCount == 2 && budget.CaptureStatistics().CommittedBytes == 256, "prepared execution grew native backing");
    }

    internal static void RunPreparedArena(int seed, int steps, Action<string> trace)
    {
        SeededRandom random = new(seed);
        NativeMemoryBudget budget = new(256);
        using NativeArena arena = new(new NativeArenaPreparation(64, 64), budget);
        long ordinary = 0, scoped = 0, peakOrdinary = 0, peakScoped = 0, successes = 0, refusals = 0;
        for (int step = 0; step < steps; step++)
        {
            int operation = random.Next(4), length = random.Next(1, 25);
            trace($"arena seed={seed} step={step} operation={operation} length={length} ordinary={ordinary} scoped={scoped}");
            if (operation is 0 or 1)
            {
                long prior = operation == 0 ? ordinary : scoped;
                long end = prior + length * 4L;
                bool expected = end <= 64;
                bool accepted = operation == 0
                    ? arena.TryScratch(length, static writer => writer.Fill(23), out ArenaLease<int> lease)
                    : arena.TryScratchScoped(length, static writer => writer.Fill(23), out lease);
                Require(accepted == expected, "prepared bump admission differs");
                if (accepted)
                {
                    Require(lease.Read(static view => view[0]) == 23, "prepared arena output differs");
                    successes++;
                    if (operation == 0) { ordinary = end; peakOrdinary = Math.Max(peakOrdinary, end); }
                    else { scoped = end; peakScoped = Math.Max(peakScoped, end); }
                }
                else refusals++;
            }
            else if (operation == 2) { arena.RecycleScoped(); scoped = 0; }
            else { arena.Reset(); ordinary = 0; scoped = 0; }
            NativePreparedArenaStatistics actual = arena.CapturePreparedSnapshot();
            Require(actual.OrdinaryUsedBytes == ordinary && actual.ScopedUsedBytes == scoped, "arena lane gauges differ");
            Require(actual.OrdinaryAvailableBytes == 64 - ordinary && actual.ScopedAvailableBytes == 64 - scoped, "arena lane availability differs");
            Require(actual.PeakOrdinaryUsedBytes == peakOrdinary && actual.PeakScopedUsedBytes == peakScoped, "arena lane high-water differs");
            Require(actual.SuccessfulScratchCount == successes && actual.RejectedCapacityCount == refusals, "arena history differs");
            Require(actual.RetainedBytes == 256 && budget.CaptureStatistics().CommittedBytes == 256 && budget.CaptureStatistics().AllocationCount == 2, "prepared arena grew backing");
        }
        arena.Dispose();
        Require(budget.CaptureStatistics().CommittedBytes == 0, "arena teardown leaked native charge");
    }

    internal static void RunLayouts(int seed, int steps, Action<string> trace)
    {
        SeededRandom random = new(seed);
        for (int step = 0; step < steps; step++)
        {
            int byteCount = random.Next(8), intCount = random.Next(1, 9);
            int offset = (byteCount + 15) / 16 * 16;
            int extent = offset + intCount * 4, backing = extent + 15;
            trace($"layout seed={seed} step={step} bytes={byteCount} ints={intCount} offset={offset} backing={backing}");
            NativeLayoutBuilder builder = new(2);
            NativeLayoutField<byte> bytes = builder.Add<byte>(byteCount);
            NativeLayoutField<int> integers = builder.Add<int>(intCount, 16);
            NativeLayout layout = builder.Build();
            Require(layout.Describe(integers).OffsetBytes == offset && layout.ExtentBytes == extent && layout.BackingBytes == backing, "checked layout differs from independent arithmetic");
            NativeMemoryBudget budget = new(backing + intCount * 4L);
            Require(layout.TryReserve(budget, out NativeLayoutReservation? permission, out _), "layout admission failed within cap");
            Require(budget.CaptureStatistics().ReservedBytes == backing && budget.CaptureStatistics().CommittedBytes == 0, "layout admission acquired backing prematurely");
            permission!.Value.PrepareBacking();
            Require(budget.CaptureStatistics().ReservedBytes == 0 && budget.CaptureStatistics().CommittedBytes == backing, "layout preparation charge differs");
            NativeLayoutOwner? source = NativeLayoutReservation.Activate(ref permission, (bytes, integers), static (writer, fields) =>
            {
                writer.Region(fields.bytes).Fill(7);
                writer.Region(fields.integers).Fill(29);
            });
            NativeLayoutOwner stale = source.Value;
            NativeLayoutOwner owner = NativeLayoutOwner.Move(ref source);
            NativeTransfer<int>? detached = null;
            bool ownerReturned = false;
            try
            {
                ExpectInvalid(() => stale.Read(integers, static (view, token) => view.Region(token)[0]));
                Require(owner.Read(integers, static (view, token) => view.Region(token)[0]) == 29, "typed initialized output differs");
                NativeLayoutStatistics actual = owner.CaptureSnapshot();
                Require(actual.LogicalInitializedBytes == byteCount + intCount * 4L && actual.InitializedRegionCount == 2, "typed initialization gauges differ");
                Require(actual.InterRegionPaddingBytes == offset - byteCount && actual.AlignmentSlackBytes == 15, "typed padding/slack differs");
                detached = owner.DetachField(integers, budget);
                Require(detached.Value.Length == intCount && detached.Value.Read(static view => view[0]) == 29, "typed field detach differs");
                Require(budget.CaptureStatistics().CommittedBytes == backing + intCount * 4L, "detach overlap was not charged");
                owner.Dispose(); ownerReturned = true;
                Require(budget.CaptureStatistics().CommittedBytes == intCount * 4L, "layout return erased detached charge");
                detached.Value.Dispose(); detached = null;
                Require(budget.CaptureStatistics().CommittedBytes == 0, "layout teardown leaked charge");
            }
            finally
            {
                if (!ownerReturned) owner.Dispose();
                detached?.Dispose();
            }
        }
    }

    internal static void RunOutliers(int seed, int steps, Action<string> trace)
    {
        SeededRandom random = new(seed);
        NativeMemoryBudget budget = new(2_000_000);
        using NativeArena arena = new(budget, new NativeArenaRetentionPolicy(4096, 4160), 0, NativeMemoryReturn.ToNativeMemory);
        long released = 0;
        for (int step = 0; step < steps; step++)
        {
            int outlierBytes = random.Next(128, 1025) * 64;
            trace($"outlier seed={seed} step={step} request={outlierBytes} expectedExtent={outlierBytes + 64}");
            ArenaLease<int> normal = arena.Scratch<int>(1, static writer => writer.Write(31));
            ArenaLease<byte> outlier = arena.Scratch<byte>(outlierBytes, static writer => writer.Fill(11));
            Require(normal.Read(static view => view[0]) == 31 && outlier.Read(static view => view[0]) == 11, "outlier damaged normal output");
            Require(budget.CaptureStatistics().CommittedBytes == 4160 + outlierBytes + 64, "outlier extent differs");
            arena.Reset();
            released += outlierBytes + 64;
            NativeArenaRetentionStatistics actual = arena.CaptureRetentionSnapshot();
            Require(actual.RetainedBytes == 4160 && actual.IdleBytes == 4160 && actual.OversizedBytes == 0 && actual.ReleasedBytes == released, "normal/outlier retention differs");
            bool rejected = false;
            try { _ = normal.Read(static view => view[0]); }
            catch (NativeAllocationReturnedException) { rejected = true; }
            Require(rejected, "reset left prior bump authority active");
            Require(budget.CaptureStatistics().AllocationCount == step + 2L, "normal working set acquired again after outlier");
        }
        arena.Dispose();
        Require(budget.CaptureStatistics().CommittedBytes == 0, "outlier teardown leaked charge");
    }

    private sealed class PoolOracle(int seed, int remaining, Action<string> trace)
    {
        internal SeededRandom Random { get; } = new(seed);
        internal Action<string> Trace { get; } = trace;
        internal int Remaining = remaining;
        internal int Step;
        internal int PeakOccupied;
        internal long Successful;
        internal long RejectedShape;
        internal long RejectedFull;
    }

    // Fixed xorshift32 selection for reproducible non-security test sequences.
    // The independent lifecycle/byte oracle never uses this state for authority.
    internal sealed class SeededRandom(int seed)
    {
        private uint _state = seed == 0 ? 0x6D2B79F5u : unchecked((uint)seed);

        internal int Next(int maximum)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
            uint next = _state;
            next ^= next << 13;
            next ^= next >> 17;
            next ^= next << 5;
            _state = next;
            return (int)(next % (uint)maximum);
        }

        internal int Next(int minimum, int maximum)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximum, minimum);
            return checked(minimum + Next(checked(maximum - minimum)));
        }
    }

    private static int Count<T>(T?[] bindings) where T : struct
    {
        int count = 0;
        foreach (T? binding in bindings) if (binding.HasValue) count++;
        return count;
    }

    private static int Find<T>(T?[] bindings, bool present) where T : struct
    {
        for (int index = 0; index < bindings.Length; index++)
            if (bindings[index].HasValue == present) return index;
        return -1;
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("A stale or incomplete operation unexpectedly succeeded.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
