using System;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

internal static partial class NativeGeneratedScenarios
{
    internal static void RunSparsePageReuse(int seed, int iterations, int traceCapacity, Action<string> trace)
    {
        SeededRandom random = new(seed);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            int length = random.Next(1, 5);
            int payload = random.Next(1, 1000);
            trace($"sparse-page seed={seed} iteration={iteration} slots=4 slotsPerPage=2 slotCapacity=4 length={length} payload={payload} backing=256 tracing={traceCapacity}");
            NativeMemoryBudget budget = new(256, traceCapacity);
            using NativePool<int> pool = new(new NativePoolPreparation(4, 4, 2), budget);
            using (Pooled<int> survivor = pool.Rent(length, writer => writer.Fill(payload)))
            {
                Pooled<int> ended = pool.Rent(4, static writer => writer.Fill(19));
                using (ended)
                {
                    Require(pool.CapturePreparedSnapshot().OccupiedSlotCount == 2
                        && budget.CaptureStatistics().CommittedBytes == 256,
                        "dense phase must retain its two prepared pages before maintenance");
                }
                // One arbitrary occupied slot pins precisely one page. The other
                // completely idle page is freed; the slot's numeric index does
                // not need to be guessed from the allocator's private free list.
                Require(pool.TrimRetainedMemory() == 128, "sparse page trim did not free exactly one idle page");
                NativePreparedPoolStatistics sparse = pool.CapturePreparedSnapshot();
                Require(sparse.RetainedPageCount == 1 && sparse.RetainedSlotCount == 2 && sparse.OccupiedSlotCount == 1
                    && sparse.AvailableSlotCount == 1 && sparse.RetainedBytes == 128 && sparse.UnusedSlotBytes == 16,
                    "one live slot's complete page charge or actual idle capacity differs");
                Require(budget.CaptureStatistics().CommittedBytes == 128 && budget.CaptureStatistics().AllocationCount == 2
                    && budget.CaptureStatistics().FreeCount == 1, "sparse retained charge was duplicated or lost");
                using (Pooled<int> reused = pool.Rent(4, static writer => writer.Fill(23)))
                {
                    bool rejectedStale = false;
                    try { _ = ended.Read(static view => view[0]); }
                    catch (NativeAllocationReturnedException) { rejectedStale = true; }
                    Require(rejectedStale, "reused or trimmed slot revived ended authority");
                    Require(survivor.Read(static view => view[0]) == payload && reused.Read(static view => view[3]) == 23,
                        "sparse reuse damaged surviving or initialized output");
                    Require(!pool.TryRent(1, static writer => writer.Write(0), out _, out NativePoolExhaustionReason reason)
                        && reason == NativePoolExhaustionReason.NoAvailableSlot, "trimmed capacity silently refilled a page");
                    Require(budget.CaptureStatistics().AllocationCount == 2, "sparse reuse acquired another backing page");
                    Require(pool.TrimRetainedMemory() == 0, "remaining live page was physically freed");
                }
            }
            Require(pool.TrimRetainedMemory() == 128 && budget.CaptureStatistics().CommittedBytes == 0
                && budget.CaptureStatistics().FreeCount == 2 && pool.CapturePreparedSnapshot().OccupiedSlotCount == 0,
                "last sparse page did not clean up exactly");
            trace($"sparse-page cleanup seed={seed} iteration={iteration} allocatedPages=2 actualFrees=2 committed=0");
        }
    }

    internal static void RunTighterGrowthAtTheCap(int seed, int iterations, int traceCapacity, Action<string> trace)
    {
        SeededRandom random = new(seed);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            int initial = random.Next(1, 5);
            int target = initial * 2 + 1;
            int payload = random.Next(1, 1000);
            long oldExtent = initial * 4L;
            long targetExtent = target * 4L;
            long overlapCap = oldExtent + targetExtent;
            trace($"tight-growth seed={seed} iteration={iteration} initial={initial} target={target} payload={payload} oldExtent={oldExtent} replacement={targetExtent} overlapCap={overlapCap} tracing={traceCapacity}");
            NativeMemoryBudget budget = new(overlapCap, traceCapacity);
            using NativeBuilder<int> builder = new(budget, initial);
            for (int index = 0; index < initial; index++) builder.Append(payload + index);
            Require(builder.TryEnsureCapacity(target), "known-size tighter growth was refused solely for optional slack");
            Require(builder.Capacity == target && budget.CaptureStatistics().CommittedBytes == targetExtent
                && budget.CaptureStatistics().PeakAdmittedBytes == overlapCap && budget.CaptureStatistics().ReservedBytes == 0,
                "tighter growth bypassed or misreported conservative overlap admission");
            for (int index = initial; index < target; index++) builder.Append(payload + index);
            int expectedSum = target * payload + target * (target - 1) / 2;
            Require(!builder.TryEnsureCapacity(target + 1), "near-cap repeated growth exceeded full overlap");
            NativeTransfer<int> owner = builder.Complete();
            try
            {
                int actualSum = owner.Read(static view =>
                {
                    int sum = 0;
                    for (int index = 0; index < view.Length; index++) sum += view[index];
                    return sum;
                });
                Require(actualSum == expectedSum && budget.CaptureStatistics().CommittedBytes == targetExtent,
                    "near-cap refusal or transfer damaged earlier output/charge");
            }
            finally { owner.Dispose(); }
            Require(budget.CaptureStatistics().CommittedBytes == 0 && budget.CaptureStatistics().ReservedBytes == 0,
                "near-cap growth terminal return leaked charge");
            trace($"tight-growth cleanup seed={seed} iteration={iteration} checksum={expectedSum} committed=0 reserved=0");
        }
    }
}
