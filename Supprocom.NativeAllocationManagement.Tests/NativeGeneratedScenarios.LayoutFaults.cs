using System;
using System.Threading;
using Supprocom.NativeAllocationManagement;
using Supprocom.NativeAllocationManagement.Conformance;

namespace Supprocom.NativeAllocationManagement.Tests;

internal static partial class NativeGeneratedScenarios
{
    internal static void RunLayoutFaultPrograms(int seed, int iterations, int traceCapacity, Action<string> trace)
    {
        SeededRandom random = new(seed);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            int firstLength = random.Next(1, 5);
            int secondLength = random.Next(1, 5);
            int extent = firstLength + secondLength;
            int mode = iteration < 5 ? iteration : random.Next(5);
            bool movePermission = random.Next(2) == 0;
            byte payload = checked((byte)random.Next(1, 100));
            trace($"layout-fault seed={seed} iteration={iteration} first={firstLength} second={secondLength} extent={extent} mode={mode} move={movePermission} payload={payload} tracing={traceCapacity}");
            NativeLayoutBuilder shape = new(2);
            NativeLayoutField<byte> first = shape.Add<byte>(firstLength);
            NativeLayoutField<byte> second = shape.Add<byte>(secondLength);
            NativeLayout layout = shape.Build();
            NativeMemoryBudget budget = new(extent + firstLength, traceCapacity);
            if (!layout.TryReserve(budget, out NativeLayoutReservation? permission, out _) || !permission.HasValue)
                throw new InvalidOperationException("Generated layout admission was refused.");
            NativeLayoutDiagnosticOracle.Statistics expected = NativeLayoutDiagnosticOracle.Pending(layout.Id, budget.Id,
                permission.Value.Id, 2, extent, extent, 1);
            try
            {
                if (movePermission)
                {
                    permission = NativeLayoutReservation.Move(ref permission);
                    expected = expected with
                    {
                        Ownership = expected.Ownership with { BindingVersion = 2, AuthorityVersion = 2 },
                        Reservation = expected.Reservation with { BindingVersion = 2, AuthorityVersion = 2, ReservationMoveCount = 1 }
                    };
                }
                NativeLayoutDiagnosticOracle.Verify(permission.Value.CaptureSnapshot(), expected, "generated-pending");
                if (mode == 0)
                {
                    permission.Value.Dispose();
                    expected = expected with
                    {
                        Ownership = expected.Ownership.Returned(),
                        Reservation = expected.Reservation with
                        { BindingIsActive = false, Outcome = NativeMemoryReservationOutcome.Cancelled, ReservedBytes = 0, HasReservationReturnObligation = false }
                    };
                    NativeLayoutDiagnosticOracle.Verify(permission.Value.CaptureSnapshot(), expected, "generated-pending-cancelled");
                    permission = null;
                    Require(budget.CaptureStatistics().AllocationCount == 0 && budget.CaptureStatistics().FreeCount == 0,
                        "pending layout cancellation manufactured native backing");
                    continue;
                }
                if (mode == 1)
                {
                    NativeMemoryTestHooks.FailNextAllocation();
                    bool failed = false;
                    try { permission.Value.PrepareBacking(); }
                    catch (NativeAllocationFailedException) { failed = true; }
                    Require(failed && budget.CaptureStatistics().ReservedBytes == extent && budget.CaptureStatistics().CommittedBytes == 0,
                        "failed generated layout acquisition lost exact pending permission");
                    expected = expected with { Reservation = expected.Reservation with { BackingPreparationFailureCount = 1 } };
                    NativeLayoutDiagnosticOracle.Verify(permission.Value.CaptureSnapshot(), expected, "generated-backing-failed");
                }
                permission.Value.PrepareBacking();
                expected = NativeLayoutDiagnosticOracle.Prepared(expected);
                NativeLayoutDiagnosticOracle.Verify(permission.Value.CaptureSnapshot(), expected, "generated-prepared");
                if (mode is 2 or 3)
                {
                    NativeLayoutReservation numericAlias = permission.Value;
                    using CancellationTokenSource stop = new();
                    bool failed = false;
                    try
                    {
                        _ = NativeLayoutReservation.Activate(ref permission, (first, second, payload, mode, stop), static (writer, state) =>
                        {
                            writer.Region(state.first).Fill(state.payload);
                            if (state.mode == 3)
                            {
                                writer.Region(state.second).Fill(state.payload);
                                state.stop.Cancel();
                            }
                        }, stop.Token);
                    }
                    catch (OperationCanceledException) when (mode == 3) { failed = true; }
                    catch (InvalidOperationException) when (mode == 2) { failed = true; }
                    Require(failed && !permission.HasValue, "failed generated layout publication preserved permission or returned authority");
                    expected = expected with
                    {
                        InitializationCompleted = mode == 3,
                        Ownership = expected.Ownership.Returned(),
                        Reservation = expected.Reservation with
                        {
                            BindingIsActive = false,
                            Outcome = mode == 3 ? NativeMemoryReservationOutcome.Cancelled : NativeMemoryReservationOutcome.InitializationFailed,
                            OwnedBackingBytes = 0,
                            HasReservationReturnObligation = false
                        }
                    };
                    NativeLayoutDiagnosticOracle.Verify(numericAlias.CaptureSnapshot(), expected, "generated-publication-consumed");
                }
                else
                {
                    NativeLayoutOwner owner = NativeLayoutReservation.Activate(ref permission, (first, second, payload), static (writer, state) =>
                    { writer.Region(state.first).Fill(state.payload); writer.Region(state.second).Fill(state.payload); });
                    expected = NativeLayoutDiagnosticOracle.Activated(expected, extent);
                    try
                    {
                        if (mode == 4)
                        {
                            NativeMemoryTestHooks.FailAtManagedPublicationBoundary(8);
                            bool failed = false;
                            try { using NativeTransfer<byte> unexpected = owner.DetachField(first, budget); }
                            catch (InvalidOperationException) { failed = true; }
                            Require(failed && budget.CaptureStatistics().CommittedBytes == extent
                                && budget.CaptureStatistics().FreeCount == 1, "failed copy publication lost source or retained failed destination");
                            expected = expected with { CopiedBytes = firstLength, Ownership = expected.Ownership with { PeakBorrowCount = 1 } };
                            NativeLayoutDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected, "generated-copy-publication-failed");
                        }
                        int actual = owner.Read((first, second), static (view, fields) => view.Region(fields.first)[0] + view.Region(fields.second)[0]);
                        Require(actual == payload * 2, "generated layout failure damaged source output");
                        expected = expected with { Ownership = expected.Ownership with { PeakBorrowCount = 1 } };
                        NativeLayoutDiagnosticOracle.Verify(owner.CaptureSnapshot(), expected, "generated-read-ended");
                    }
                    finally { owner.Dispose(); }
                    NativeLayoutDiagnosticOracle.Verify(owner.CaptureSnapshot(), NativeLayoutDiagnosticOracle.Returned(expected), "generated-layout-returned");
                }
            }
            finally
            {
                try { permission?.Dispose(); }
                finally { NativeMemoryTestHooks.Reset(); }
                Require(budget.CaptureStatistics().CommittedBytes == 0 && budget.CaptureStatistics().ReservedBytes == 0,
                    "generated layout terminal cleanup left physical or pending storage");
                trace($"layout-fault cleanup seed={seed} iteration={iteration} mode={mode} committed=0 reserved=0");
            }
        }
    }
}
