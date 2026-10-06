using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Conformance;

// This independent, public-only transition model also runs against the package.
// Only identities come from the producer; every expected gauge/history is calculated.
internal static class NativeSharingDiagnosticOracle
{
    internal const int BindingSlotBytes = 16; // Sequential Int64, Int32, Boolean plus alignment.

    internal static void Run(int traceCapacity)
    {
        NativeMemoryBudget budget = new(64, traceCapacity);
        using NativeBuilder<int> builder = new(budget, 4);
        builder.Append(17);
        builder.Append(29);
        NativeTransfer<int>? source = builder.Complete();
        NativeShared<int> owner = NativeShared<int>.Create(ref source, new(3, 2));
        Expected expected = Expected.Initial(owner.Id, builder.Id, 16, 8, new(3, 2));
        Verify(owner.CaptureSnapshot(), expected, "initial");
        Check("source-consumed", source.HasValue, false, "initial");
        if (!owner.TryDowngrade(out NativeWeak<int> weak, out _))
        {
            owner.Dispose();
            throw new InvalidOperationException("Prepared weak slot was refused.");
        }
        expected = expected with { WeakBindingCount = 1, PeakWeakBindingCount = 1, WeakCreationCount = 1 };
        try
        {
            try
            {
                Verify(weak.CaptureSnapshot(), expected, "weak-acquired");
                if (!owner.TryDowngrade(out NativeWeak<int> secondWeak, out _))
                    throw new InvalidOperationException("Second prepared observer was refused.");
                expected = expected with { WeakBindingCount = 2, PeakWeakBindingCount = 2, WeakCreationCount = 2 };
                try
                {
                    Verify(owner.CaptureSnapshot(), expected, "second-weak-acquired");
                    if (owner.TryDowngrade(out NativeWeak<int> unexpectedWeak, out NativeSharingExhaustionReason weakReason))
                    {
                        unexpectedWeak.Dispose();
                        throw new InvalidOperationException("Weak capacity was exceeded.");
                    }
                    Check("weak-refusal", (long)weakReason, (long)NativeSharingExhaustionReason.NoWeakBinding, "weak-full");
                    expected = expected with { RejectedWeakCount = 1 };
                    Verify(owner.CaptureSnapshot(), expected, "weak-full");
                    if (!owner.TryShare(out NativeShared<int> share, out _))
                        throw new InvalidOperationException("Prepared share was refused.");
                    expected = expected with { StrongBindingCount = 2, PeakStrongBindingCount = 2, ShareCount = 1 };
                    try
                    {
                        Verify(share.CaptureSnapshot(), expected, "share-acquired");
                        if (!owner.TrySlice(1, 1, out NativeShared<int> slice, out _))
                            throw new InvalidOperationException("Prepared slice was refused.");
                        expected = expected with { StrongBindingCount = 3, PeakStrongBindingCount = 3, ShareCount = 2 };
                        try
                        {
                            Verify(slice.CaptureSnapshot(), expected, "slice-retains-full-backing");
                            if (owner.TryShare(out NativeShared<int> unexpectedShare, out NativeSharingExhaustionReason strongReason))
                            {
                                unexpectedShare.Dispose();
                                throw new InvalidOperationException("Strong capacity was exceeded.");
                            }
                            Check("strong-refusal", (long)strongReason, (long)NativeSharingExhaustionReason.NoStrongBinding, "strong-full");
                            expected = expected with { RejectedStrongCount = 1 };
                            Verify(owner.CaptureSnapshot(), expected, "strong-full");
                            if (weak.TryUpgrade(out NativeShared<int> unexpectedUpgrade, out NativeSharingExhaustionReason upgradeReason))
                            {
                                unexpectedUpgrade.Dispose();
                                throw new InvalidOperationException("Upgrade exceeded strong capacity.");
                            }
                            Check("upgrade-refusal", (long)upgradeReason, (long)NativeSharingExhaustionReason.NoStrongBinding, "upgrade-full");
                            expected = expected with { RejectedStrongCount = 2 };
                            Verify(weak.CaptureSnapshot(), expected, "upgrade-full");
                            if (slice.TryDetach(new NativeMemoryBudget(0), out NativeTransfer<int> unexpectedDetach))
                            {
                                unexpectedDetach.Dispose();
                                throw new InvalidOperationException("Detach bypassed its destination budget.");
                            }
                            expected = expected with { RejectedDetachCount = 1 };
                            Verify(slice.CaptureSnapshot(), expected, "detach-refused");
                            if (!slice.TryDetach(budget, out NativeTransfer<int> detached))
                                throw new InvalidOperationException("Admitted detach was refused.");
                            try
                            {
                                expected = expected with { DetachCount = 1 };
                                Verify(slice.CaptureSnapshot(), expected, "detach-is-independent");
                                Check("detached-value", detached.Read(static view => view[0]), 29, "detach-is-independent");
                                Check("overlap-charge", budget.CaptureStatistics().CommittedBytes, 20, "detach-is-independent");
                            }
                            finally { detached.Dispose(); }
                            Check("source-charge", budget.CaptureStatistics().CommittedBytes, 16, "detached-returned");
                            // A captured owning value is not an authorized package
                            // callback shape. Nonzero entered-read observations are
                            // independently proved by local runtime tests instead.
                            slice.Access(static view => Check("slice-value", view[0], 29, "entered-read"));
                            Verify(slice.CaptureSnapshot(), expected, "all-reads-ended");
                        }
                        finally { slice.Dispose(); }
                        expected = expected with { StrongBindingCount = 2 };
                        Verify(share.CaptureSnapshot(), expected, "slice-released");
                    }
                    finally { share.Dispose(); }
                    expected = expected with { StrongBindingCount = 1 };
                    Verify(owner.CaptureSnapshot(), expected, "share-released");
                }
                finally { secondWeak.Dispose(); }
                expected = expected with { WeakBindingCount = 1 };
                Verify(weak.CaptureSnapshot(), expected, "second-observer-released");
                if (!weak.TryUpgrade(out NativeShared<int> upgrade, out _))
                    throw new InvalidOperationException("Live weak upgrade was refused.");
                expected = expected with { StrongBindingCount = 2, SuccessfulUpgradeCount = 1 };
                try
                {
                    Verify(upgrade.CaptureSnapshot(), expected, "upgrade-published");
                    Check("upgrade-output", upgrade.Read(static view => view[0] + view[1]), 46, "upgrade-published");
                }
                finally { upgrade.Dispose(); }
                expected = expected with { StrongBindingCount = 1 };
                Verify(owner.CaptureSnapshot(), expected, "upgrade-released");
            }
            finally { owner.Dispose(); }
            expected = expected.AfterPayloadReturn();
            Verify(weak.CaptureSnapshot(), expected, "payload-returned-observer-survives");
            Check("expired", weak.IsExpired, true, "payload-returned-observer-survives");
            Check("final-charge", budget.CaptureStatistics().CommittedBytes, 0, "payload-returned-observer-survives");
            if (weak.TryUpgrade(out NativeShared<int> revived, out NativeSharingExhaustionReason expiredReason))
            {
                revived.Dispose();
                throw new InvalidOperationException("Expired payload was resurrected.");
            }
            Check("expired-refusal", (long)expiredReason, (long)NativeSharingExhaustionReason.ExpiredPayload, "expired-upgrade");
            expected = expected with { ExpiredUpgradeCount = 1 };
            Verify(weak.CaptureSnapshot(), expected, "expired-upgrade");
            Check("already-returned", weak.TryCompletePayloadReturn(), true, "already-returned");
            Verify(weak.CaptureSnapshot(), expected, "retry-does-not-invent-a-second-return");
        }
        finally { weak.Dispose(); }
        expected = expected with { WeakBindingCount = 0, ManagedBankBytes = 0 };
        Verify(owner.CaptureSnapshot(), expected, "all-authority-and-bank-storage-released");
    }

    internal static void Verify(NativeSharingStatistics actual, Expected expected, string stage)
    {
        if (expected.Id <= 0 || expected.OwnerId <= 0 || expected.Id == expected.OwnerId)
            throw new InvalidOperationException("Shared identity and owner lineage must be distinct positive identities.");
        Check(nameof(actual.Id), actual.Id, expected.Id, stage);
        Check(nameof(actual.OwnerId), actual.OwnerId, expected.OwnerId, stage);
        Check("Preparation.StrongBindingCount", actual.Preparation.StrongBindingCount, expected.Preparation.StrongBindingCount, stage);
        Check("Preparation.WeakBindingCount", actual.Preparation.WeakBindingCount, expected.Preparation.WeakBindingCount, stage);
        Check(nameof(actual.StrongBindingCount), actual.StrongBindingCount, expected.StrongBindingCount, stage);
        Check(nameof(actual.WeakBindingCount), actual.WeakBindingCount, expected.WeakBindingCount, stage);
        Check(nameof(actual.ActiveReadCount), actual.ActiveReadCount, expected.ActiveReadCount, stage);
        Check(nameof(actual.PeakStrongBindingCount), actual.PeakStrongBindingCount, expected.PeakStrongBindingCount, stage);
        Check(nameof(actual.PeakWeakBindingCount), actual.PeakWeakBindingCount, expected.PeakWeakBindingCount, stage);
        Check(nameof(actual.Expired), actual.Expired, expected.Expired, stage);
        Check(nameof(actual.PayloadReleased), actual.PayloadReleased, expected.PayloadReleased, stage);
        Check(nameof(actual.OwnedBackingBytes), actual.OwnedBackingBytes, expected.OwnedBackingBytes, stage);
        Check(nameof(actual.BorrowedBackingBytes), actual.BorrowedBackingBytes, expected.BorrowedBackingBytes, stage);
        Check(nameof(actual.InitializedPayloadBytes), actual.InitializedPayloadBytes, expected.InitializedPayloadBytes, stage);
        Check(nameof(actual.PeakInitializedPayloadBytes), actual.PeakInitializedPayloadBytes, expected.PeakInitializedPayloadBytes, stage);
        Check(nameof(actual.ManagedBankBytes), actual.ManagedBankBytes, expected.ManagedBankBytes, stage);
        Check(nameof(actual.ShareCount), actual.ShareCount, expected.ShareCount, stage);
        Check(nameof(actual.WeakCreationCount), actual.WeakCreationCount, expected.WeakCreationCount, stage);
        Check(nameof(actual.SuccessfulUpgradeCount), actual.SuccessfulUpgradeCount, expected.SuccessfulUpgradeCount, stage);
        Check(nameof(actual.RejectedStrongCount), actual.RejectedStrongCount, expected.RejectedStrongCount, stage);
        Check(nameof(actual.RejectedWeakCount), actual.RejectedWeakCount, expected.RejectedWeakCount, stage);
        Check(nameof(actual.ExpiredUpgradeCount), actual.ExpiredUpgradeCount, expected.ExpiredUpgradeCount, stage);
        Check(nameof(actual.PayloadReturnCount), actual.PayloadReturnCount, expected.PayloadReturnCount, stage);
        Check(nameof(actual.DetachCount), actual.DetachCount, expected.DetachCount, stage);
        Check(nameof(actual.RejectedDetachCount), actual.RejectedDetachCount, expected.RejectedDetachCount, stage);
        Check(nameof(actual.PayloadReturnFailureCount), actual.PayloadReturnFailureCount, expected.PayloadReturnFailureCount, stage);
        Check(nameof(actual.HistoryOverflowed), actual.HistoryOverflowed, expected.HistoryOverflowed, stage);
    }

    private static void Check(string field, long actual, long expected, string stage)
    {
        if (actual != expected) throw new InvalidOperationException($"{stage}: {field} was {actual}, expected {expected}.");
    }

    private static void Check(string field, bool actual, bool expected, string stage)
    {
        if (actual != expected) throw new InvalidOperationException($"{stage}: {field} was {actual}, expected {expected}.");
    }

    internal readonly record struct Expected(
        long Id, long OwnerId, NativeSharingPreparation Preparation,
        int StrongBindingCount, int WeakBindingCount, int ActiveReadCount,
        int PeakStrongBindingCount, int PeakWeakBindingCount, bool Expired, bool PayloadReleased,
        long OwnedBackingBytes, long BorrowedBackingBytes, long InitializedPayloadBytes,
        long PeakInitializedPayloadBytes, long ManagedBankBytes, long ShareCount, long WeakCreationCount,
        long SuccessfulUpgradeCount, long RejectedStrongCount, long RejectedWeakCount,
        long ExpiredUpgradeCount, long PayloadReturnCount, long DetachCount, long RejectedDetachCount,
        long PayloadReturnFailureCount, bool HistoryOverflowed)
    {
        internal static Expected Initial(long id, long ownerId, long ownedBytes, long initializedBytes, NativeSharingPreparation preparation) =>
            new(id, ownerId, preparation, 1, 0, 0, 1, 0, false, false, ownedBytes, 0,
                initializedBytes, initializedBytes, ((long)preparation.StrongBindingCount + preparation.WeakBindingCount) * BindingSlotBytes,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false);

        internal Expected AfterPayloadReturn() => this with
        {
            StrongBindingCount = 0,
            Expired = true,
            PayloadReleased = true,
            OwnedBackingBytes = 0,
            BorrowedBackingBytes = 0,
            InitializedPayloadBytes = 0,
            ManagedBankBytes = WeakBindingCount == 0 ? 0 : (long)Preparation.WeakBindingCount * BindingSlotBytes,
            PayloadReturnCount = 1
        };
    }
}
