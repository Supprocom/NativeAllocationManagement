using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

internal static partial class NativeGeneratedScenarios
{
    internal static async Task RunSharingSchedulesAsync(int seed, int iterations, Action<string> trace)
    {
        SeededRandom random = new(seed);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            NativeMemoryBudget budget = new(16);
            Require(budget.TryReserve<int>(4, out NativeMemoryReservation<int>? permission, out _), "scheduled admission failed");
            NativeTransfer<int>? unique = NativeMemoryReservation<int>.Activate(ref permission, static writer => writer.Fill(37));
            NativeShared<int> owner = NativeShared<int>.Create(ref unique, new(2, 1));
            Require(owner.TryDowngrade(out NativeWeak<int> weak, out _), "scheduled weak preparation failed");
            NativeShared<int>? upgraded = null;
            bool initialReleased = false;
            int order = iteration < 3 ? iteration : random.Next(3);
            trace($"weak-race seed={seed} iteration={iteration} order={order}");
            try
            {
                if (order == 0)
                {
                    owner.Dispose();
                    initialReleased = true;
                    Require(!weak.TryUpgrade(out _, out NativeSharingExhaustionReason reason) && reason == NativeSharingExhaustionReason.ExpiredPayload, "release-first resurrected payload");
                }
                else if (order == 1)
                {
                    Require(weak.TryUpgrade(out NativeShared<int> acquired, out _), "upgrade-first failed");
                    upgraded = acquired;
                    owner.Dispose();
                    initialReleased = true;
                }
                else
                {
                    using Barrier start = new(2);
                    Task release = Task.Run(() =>
                    {
                        if (!start.SignalAndWait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Scheduled release did not reach barrier.");
                        owner.Dispose();
                        initialReleased = true;
                    });
                    Task<NativeShared<int>?> upgrade = Task.Run(() =>
                    {
                        if (!start.SignalAndWait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Scheduled upgrade did not reach barrier.");
                        bool accepted = weak.TryUpgrade(out NativeShared<int> acquired, out NativeSharingExhaustionReason reason);
                        Require(accepted || reason == NativeSharingExhaustionReason.ExpiredPayload, "racing upgrade had an impossible refusal");
                        return accepted ? acquired : (NativeShared<int>?)null;
                    });
                    await Task.WhenAll(release, upgrade).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    upgraded = await upgrade.ConfigureAwait(false);
                }
                NativeSharingStatistics actual = weak.CaptureSnapshot();
                int expectedStrong = upgraded.HasValue ? 1 : 0;
                Require(actual.StrongBindingCount == expectedStrong && actual.ActiveReadCount == 0 && actual.WeakBindingCount == 1, "race final owner model differs");
                Require(actual.Expired == !upgraded.HasValue && actual.PayloadReleased == !upgraded.HasValue, "race expiration differs from acquired ownership");
                Require(budget.CaptureStatistics().CommittedBytes == expectedStrong * 16L, "race final payload charge differs");
                if (upgraded.HasValue) Require(upgraded.Value.Read(static view => view[3]) == 37, "race published invalid payload");
                trace($"weak-race result seed={seed} iteration={iteration} upgraded={upgraded.HasValue} committed={budget.CaptureStatistics().CommittedBytes}");
            }
            finally
            {
                if (!initialReleased) owner.Dispose();
                upgraded?.Dispose();
                weak.Dispose();
            }
            Require(budget.CaptureStatistics().CommittedBytes == 0, "race cleanup leaked payload");
        }
    }

    internal static async Task RunBorrowReturnSchedulesAsync(int seed, int iterations, Action<string> trace)
    {
        SeededRandom random = new(seed);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            NativeMemoryBudget budget = new(16);
            Require(budget.TryReserve<int>(4, out NativeMemoryReservation<int>? permission, out _), "borrow admission failed");
            NativeTransfer<int>? source = NativeMemoryReservation<int>.Activate(ref permission, static writer => writer.Fill(41));
            NativeTransfer<int> owner = source.Value;
            using ManualResetEventSlim entered = new(false);
            using ManualResetEventSlim leave = new(false);
            Task reader = Task.Run(() => owner.Access(view =>
            {
                entered.Set();
                if (!leave.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Scheduled borrow did not receive exit permission.");
                Require(view[0] == 41 && view[3] == 41, "return invalidated an entered borrow");
            }));
            bool consume = iteration == 0 || iteration > 1 && random.Next(2) == 0;
            trace($"borrow-return seed={seed} iteration={iteration} consume={consume}");
            Exception? verificationFailure = null;
            try
            {
                if (!entered.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Scheduled borrow did not enter.");
                Require(owner.CaptureSnapshot().ActiveBorrowCount == 1, "entered borrow count differs");
                if (consume)
                {
                    ExpectInvalid(() => NativeTransfer<int>.Move(ref source));
                    Require(!source.HasValue && owner.CaptureSnapshot().Lifecycle == NativeTransferLifecycle.Retiring, "failed entered movement did not consume and defer ownership");
                }
                else
                {
                    ExpectInvalid(owner.Dispose);
                    Require(owner.CaptureSnapshot().BindingIsActive, "rejected return lost surviving authority");
                }
                Require(budget.CaptureStatistics().CommittedBytes == 16, "entered borrow backing returned prematurely");
            }
            catch (InvalidOperationException failure) { verificationFailure = failure; }
            catch (TimeoutException failure) { verificationFailure = failure; }
            finally { leave.Set(); }
            try { await reader.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
            finally
            {
                if (owner.CaptureSnapshot().BindingIsActive) owner.Dispose();
                else _ = owner.TryCompletePayloadReturn();
            }
            if (verificationFailure is not null) ExceptionDispatchInfo.Capture(verificationFailure).Throw();
            Require(owner.CaptureSnapshot().ActiveBorrowCount == 0 && owner.CaptureSnapshot().PayloadReturnCount == 1, "borrow completion return differed");
            Require(budget.CaptureStatistics().CommittedBytes == 0, "borrow completion retained charge");
        }
    }
}
