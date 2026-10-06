using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

internal static partial class NativeGeneratedScenarios
{
    internal static async Task RunGenerationSchedulesAsync(int seed, int iterations, int traceCapacity, Action<string> trace)
    {
        SeededRandom random = new(seed);
        long segmentExtent = OperatingSystem.IsWindows() ? 4 : 64;
        long totalExtent = 2 * segmentExtent;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            int firstExit = random.Next(2);
            bool quarantine = iteration % 2 != 0;
            int payload = random.Next(1, 1000);
            trace($"generation seed={seed} iteration={iteration} readers=2 firstExit={firstExit} quarantine={quarantine} payload={payload} extent={totalExtent} tracing={traceCapacity}");
            NativeMemoryBudget budget = new(totalExtent, traceCapacity);
            NativeConcurrentPool<int> pool = new(budget, 1, 0, NativeMemoryReturn.ToNativeMemory, false);
            using CountdownEvent entered = new(2);
            using ManualResetEventSlim releaseFirst = new(false);
            using ManualResetEventSlim releaseSecond = new(false);
            ManualResetEventSlim[] exits = [releaseFirst, releaseSecond];
            Task<bool>[] readers = new Task<bool>[2];
            for (int reader = 0; reader < readers.Length; reader++)
            {
                int slot = reader;
                readers[slot] = Task.Run(() =>
                {
                    using ConcurrentPooled<int> value = pool.Rent(1, writer => writer.Write(payload + slot));
                    try
                    {
                        value.Access(view =>
                        {
                            entered.Signal();
                            if (!exits[slot].Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Generated reader exit was not released.");
                            Require(view[0] == payload + slot, "retirement invalidated an entered reader");
                        });
                        return false;
                    }
                    catch (NativeAllocationQuarantinedException) { return true; }
                });
            }
            try
            {
                if (!entered.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Generated readers did not both enter.");
                Require(budget.CaptureStatistics().CommittedBytes == totalExtent && budget.CaptureStatistics().AllocationCount == 2,
                    "two independent slab extents differ from the declared model");
                pool.ReleaseLeasesToGarbageCollector();
                NativeOwnerDiagnosticSnapshot held = pool.CaptureDiagnosticSnapshot();
                Require(held.Generation == 1 && held.RetiredGenerationCount == 1 && held.RetiredSegmentCount == 2
                    && held.RetiredBytes == totalExtent && held.OutstandingNativeBytes == totalExtent,
                    "ended two-reader generation was not fully charged");
                exits[firstExit].Set();
                Require(!await readers[firstExit].WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false),
                    "first reader prematurely ran final drain");
                held = pool.CaptureDiagnosticSnapshot();
                Require(held.RetiredBytes == totalExtent && held.RetiredGenerationCount == 1 && budget.CaptureStatistics().FreeCount == 0,
                    "one remaining reader lost full retained backing");
                if (quarantine) NativeMemoryTestHooks.FailAfterCommitBoundary(1);
                exits[1 - firstExit].Set();
                bool failedDrain = await readers[1 - firstExit].WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                Require(failedDrain == quarantine, "last-reader drain did not match the generated fault");
                NativeOwnerDiagnosticSnapshot ended = pool.CaptureDiagnosticSnapshot();
                Require(ended.OutstandingNativeBytes == totalExtent && budget.CaptureStatistics().CommittedBytes == totalExtent
                    && budget.CaptureStatistics().FreeCount == 0 && ended.PeakOutstandingNativeBytes == totalExtent,
                    "drain invented physical free or lost a peak");
                if (quarantine)
                    Require(ended.QuarantinedGenerationCount == 1 && ended.QuarantinedSegmentCount == 2
                        && !ended.CurrentGenerationQuarantined && ended.RetiredBytes == totalExtent,
                        "failed drain did not isolate the ended generation");
                else
                    Require(ended.RetiredGenerationCount == 0 && ended.QuarantinedGenerationCount == 0
                        && ended.RetainedSegmentCount == 2 && ended.RetiredBytes == 0,
                        "successful drain did not rejoin the exact backing");
                trace($"generation result seed={seed} iteration={iteration} state={(quarantine ? "quarantined" : "rejoined")} committed={totalExtent} nativeFrees=0");
            }
            finally
            {
                releaseFirst.Set();
                releaseSecond.Set();
                try { await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
                finally { NativeMemoryTestHooks.Reset(); pool.Dispose(); }
            }
            Require(budget.CaptureStatistics().CommittedBytes == 0 && budget.CaptureStatistics().ReservedBytes == 0
                && budget.CaptureStatistics().FreeCount == 2 && pool.CaptureDiagnosticSnapshot().OutstandingNativeBytes == 0,
                "generation schedule terminal cleanup differs from two actual slab frees");
            trace($"generation cleanup seed={seed} iteration={iteration} committed=0 reserved=0 nativeFrees=2");
        }
    }
}
